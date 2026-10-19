using System.Collections;
using UnityEngine;

/// <summary>
/// 🚶 行人衝出到「玩家車」前方 —— 對應考照的行人穿越項目。
///
/// 跟 ReverseTriggerController 那套（行人衝到白車前、玩家看前車煞車）教的東西不同：
///   衝到白車前 → 練「看穿前車」與跟車距離
///   衝到玩家前 → 練直接的行人偵測與煞停判斷
///
/// 為什麼另外做一支而不是塞進 ReverseTriggerController：
/// 那支從頭到尾都要先挑到一台白車演員，而這個科目**根本不需要演員車** ——
/// 玩家一定在、位置一定知道、速度一定量得到，所以時機算得準，
/// 也完全不會遇到「前方沒白車」「掃描器搶走演員」「車道太擠不佈署」那一整串問題。
///
/// 公平性是這個科目的重點：行人踏出來時玩家必須煞得住，否則就是懲罰而不是訓練。
/// 需要的距離 = 反應時間 × 車速 + 煞停距離，時速 47km/h 大約要 32 公尺。
/// 這裡會在發動時實際算一次，不夠就印警告告訴你要調哪個值。
/// </summary>
public class PedestrianDashTrigger : MonoBehaviour
{
    [Header("要用哪個行人")]
    [Tooltip("必填。建議把 Position Relative To Actor 關掉、用手拉的 Start/End Point，\n" +
             "並且打開 Time Fixed Path To Actor（時機由程式算）")]
    public CrossingPedestrian pedestrian;

    [Header("🚗 清出走廊（塞車時保證玩家感覺得到）")]
    // 塞車時玩家到穿越線之間常卡著別的車 —— 行人衝出被那台車擋住,被堵住停下的玩家沒感覺。
    // 打開這個,發動時就把「玩家車 → 穿越線」那段路的車縮小淡出,確保衝出時玩家正前方是空的。
    // 走廊的實際參數(半寬、玩家保護距離、往前延伸…)在行人身上的「對玩家清出走廊」那區調。
    [Tooltip("發動時清掉「玩家車 → 穿越線」那段路的車,確保行人衝出時玩家前方淨空。\n" +
             "關掉＝維持舊行為(不清車)")]
    public bool clearCorridorToPlayer = true;

    [Header("發動條件")]
    [Tooltip("玩家車速低於這個值就不演（m/s）。慢速時行人穿越沒有危險也沒有教學意義")]
    public float minPlayerSpeed = 5f;
    [Tooltip("觸發一次後隔幾秒才能再觸發")]
    public float cooldownSeconds = 15f;

    [Header("公平性檢查")]
    // 行人踏出來時玩家要有足夠距離煞停，不然就是「必撞」——那是懲罰不是訓練。
    // 需求距離 = 反應時間 × 車速 + 煞停距離(v²/2a)。
    [Tooltip("假設玩家的反應時間（秒）。突發事件的真實值約 1.25~1.5")]
    public float assumedReactionTime = 1.4f;
    [Tooltip("假設玩家的煞車減速度（m/s²）。一般乾燥路面全力煞車約 7~8")]
    public float assumedDeceleration = 7f;
    [Tooltip("距離不足時只印警告、照樣發動。關掉＝距離不足就不演")]
    public bool fireEvenIfUnfair = true;

    [Header("危險感知")]
    [Tooltip("註冊到 HazardAwarenessTracker 的名稱。要跟語音對應表的 Hazard Name 一致")]
    public string hazardName = "行人穿越";
    [Tooltip("行人走完之後再等幾秒才結算，讓慢半拍的煞車也算有反應")]
    public float reactionWindowSeconds = 2f;

    [Header("診斷")]
    public bool logDash = true;
    [Tooltip("連「進來的不是玩家」也印。每台車的每個子碰撞體都會觸發一次，會洗爆 Console，\n" +
             "只在查『盒子到底有沒有被碰到』時才開")]
    public bool logNonPlayerEntries = false;

    private float nextAllowedTime = -999f;
    private bool running = false;

    private void OnTriggerEnter(Collider other)
    {
        // 這幾條原本都容易變成靜默失敗，一律留訊息
        // 每台車的每個子碰撞體都會進來一次（方向燈、煞車燈、感測器…），
        // 全部印出來會把 Console 洗爆。只在專門查「盒子到底有沒有被碰到」時才開。
        if (!other.CompareTag("Player"))
        {
            if (logNonPlayerEntries)
                Debug.Log($"<color=grey>🚶 [行人衝出] {name} 進來的不是玩家" +
                          $"（{other.name}，Tag={other.tag}）</color>", this);
            return;
        }

        if (running)
        {
            if (logDash) Debug.Log($"<color=grey>🚶 [行人衝出] {name} 上一次還沒結束</color>", this);
            return;
        }

        if (Time.time < nextAllowedTime)
        {
            if (logDash)
                Debug.Log($"<color=grey>🚶 [行人衝出] {name} 冷卻中，剩 " +
                          $"{nextAllowedTime - Time.time:F1} 秒</color>", this);
            return;
        }

        if (pedestrian == null)
        {
            Debug.LogWarning($"[行人衝出] {name} 沒有指定 Pedestrian，不會發動", this);
            return;
        }

        if (pedestrian.IsCrossing)
        {
            if (logDash) Debug.Log($"<color=grey>🚶 [行人衝出] 行人 {pedestrian.name} 正在演別場</color>", this);
            return;
        }

        // ⚠️ 不能用 other.transform.root：玩家車的階層是 RRRR→SSS→playerCAR→vehicle，
        //    root 是最外層的容器，它的 forward 跟車頭方向無關，
        //    GetComponentInChildren 也可能抓到別顆 Rigidbody → 投影出來就是 0。
        //    改用「從碰到的 collider 往上找 Rigidbody」，再用那顆 Rigidbody 自己的
        //    transform.forward 當車頭方向 —— 這也是 ReverseTriggerController 在用的方式。
        Rigidbody rb = other.attachedRigidbody != null
            ? other.attachedRigidbody
            : other.GetComponentInParent<Rigidbody>();

        if (rb == null)
        {
            if (logDash)
                Debug.LogWarning($"<color=orange>🚶 [行人衝出] {name} 在 {other.name} 上找不到 Rigidbody，" +
                                 $"量不到車速</color>", this);
            return;
        }

        Transform player = rb.transform;
        float speed = Vector3.Dot(rb.velocity, player.forward);

        if (speed < minPlayerSpeed)
        {
            if (logDash)
                Debug.Log($"<color=grey>🚶 [行人衝出] {name} 玩家車速只有 " +
                          $"{speed * 3.6f:F0} km/h（門檻 {minPlayerSpeed * 3.6f:F0}），這次不演</color>", this);
            return;
        }

        if (!CheckFairness(speed) && !fireEvenIfUnfair) return;

        StartCoroutine(DashRoutine(player, speed));
    }

    /// <summary>
    /// 玩家煞不煞得住。行人的時機是由她的 Fixed Path Car Gap 決定的 ——
    /// 那個值就是「她站到車道中間時，玩家還剩多少距離」，直接拿來跟需求距離比。
    /// </summary>
    private bool CheckFairness(float playerSpeed)
    {
        float needed = assumedReactionTime * playerSpeed
                     + playerSpeed * playerSpeed / (2f * Mathf.Max(1f, assumedDeceleration));
        float given = pedestrian.fixedPathCarGap;

        if (given >= needed) return true;

        Debug.LogWarning(
            $"<color=orange>🚶 [行人衝出] {name} 距離不足：玩家 {playerSpeed * 3.6f:F0} km/h 需要 " +
            $"{needed:F0}m 才煞得住，但行人的 Fixed Path Car Gap 只有 {given:F0}m。\n" +
            $"  這樣玩家是「必撞」——請把 {pedestrian.name} 的 Fixed Path Car Gap 調到 {needed:F0} 以上</color>", this);
        return false;
    }

    private IEnumerator DashRoutine(Transform player, float speed)
    {
        running = true;
        nextAllowedTime = Time.time + cooldownSeconds;

        if (logDash)
            Debug.Log($"<color=cyan>🚶 [行人衝出] {name} 發動 —— 玩家 {speed * 3.6f:F0} km/h，" +
                      $"行人將在玩家剩 {pedestrian.fixedPathCarGap:F0}m 時站到車道上</color>", this);

        // 瞄準玩家而不是白車。CrossingPedestrian 的時機計算本來就是「相對某台車」，
        // 傳誰進去就對誰算，所以這裡不需要它有任何專用邏輯。
        pedestrian.gameObject.SetActive(true);
        if (clearCorridorToPlayer)
            // 清走廊版:一律走固定路徑、每幀重讀玩家車速,並把「玩家 → 穿越線」的車淨空
            pedestrian.BeginCrossingTimedToPlayer(player);
        else
            pedestrian.BeginCrossing(player, speed);

        // 等她真的踏出人行道才開始計反應時間 —— 在那之前玩家沒有東西可以反應
        float guard = 0f;
        float stepOutTimeout = pedestrian.EstimatedStepOutDelay + pedestrian.fixedPathWaitTimeout + 2f;
        while (!pedestrian.HasSteppedOut && guard < stepOutTimeout)
        {
            guard += Time.deltaTime;
            yield return null;
        }

        if (!pedestrian.HasSteppedOut)
        {
            if (logDash)
                Debug.Log($"<color=grey>🚶 [行人衝出] {name} 行人一直沒踏出來（多半是玩家已經開過穿越點），本次取消</color>", this);
            running = false;
            yield break;
        }

        // Silent：突發型危險因子不給事前提示，只量測反應與事後回饋。
        // 預告時間用她「站著張望」那一段 —— 那就是玩家實際拿到的線索時間。
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.RegisterHazard(
                hazardName, pedestrian.transform, HazardStyle.Silent, pedestrian.lookBeforeCrossing);

        // 等她走完，再多等一段才結算（煞車反應常常慢半拍）
        while (pedestrian.IsCrossing) yield return null;
        yield return new WaitForSeconds(reactionWindowSeconds);

        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(pedestrian.transform);

        running = false;
    }

    private void OnDrawGizmosSelected()
    {
        if (pedestrian == null) return;
        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(transform.position, pedestrian.transform.position);
        Gizmos.DrawWireSphere(pedestrian.transform.position, 0.6f);
    }
}
