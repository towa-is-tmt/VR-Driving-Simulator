using UnityEngine;

/// <summary>
/// 🆘 玩家卡死救援
///
/// 掛在玩家車上（有 Rigidbody 的那個物件）。
///
/// 為什麼需要這個：
///   AI 車的 Rigidbody 是 Kinematic，而且用 transform.position += 直接瞬移，不走物理。
///   Kinematic 剛體在 PhysX 裡等同「無限質量的牆」，所以一旦它瞬移卡進玩家車，
///   玩家的動力完全推不動它；而 AI 車自己的移動閘門又偵測到前方有玩家而停住 ——
///   兩邊互相等待，形成永久死鎖。
///
///   救護車有 HandleStuckBlockerClear 可以清掉擋路的 AI 車，但它明確排除玩家
///   （「玩家擋路 → 交給鳴笛，永不清除」），所以玩家這邊沒有任何救援機制。
///
/// 判定「真的卡死」需要三個條件同時成立，避免把正常停車誤判成卡死：
///   1. 玩家有踩油門（代表他想動）
///   2. 沒踩煞車、也不在 P 檔
///   3. 車速接近 0 持續數秒
///
/// 成立後把貼在玩家車上的 AI 車移除。車流本來就會不斷生成補位，移除一台沒有副作用。
/// </summary>
public class PlayerStuckRescue : MonoBehaviour
{
    [Header("卡死判定")]
    [Tooltip("三個條件同時成立持續這麼久才判定卡死（秒）")]
    public float stuckSeconds = 3f;
    [Tooltip("車速低於這個值視為沒在動（km/h）")]
    public float stuckSpeedKmH = 1.5f;
    [Tooltip("油門要踩超過這個值才算「玩家想動」")]
    public float minThrottle = 0.15f;
    [Tooltip("煞車超過這個值就不算卡死 —— 那是玩家自己要停的")]
    public float maxBrakeWhileStuck = 0.1f;

    [Header("解除方式")]
    [Tooltip("以玩家車為中心搜尋這個半徑內的 AI 車")]
    public float searchRadius = 4.5f;
    [Tooltip("勾選 = 直接移除擋路的車；取消 = 只推開，不移除")]
    public bool destroyBlockers = true;
    [Tooltip("推開的距離（destroyBlockers 關閉時才用）")]
    public float pushDistance = 3f;
    [Tooltip("救護車正在執行事件，移除它會中斷整個流程")]
    public bool excludeAmbulance = true;
    [Tooltip("正在為救護車避讓的車改用「推開」而不是移除。\n它們是救護車事件的一部分，憑空消失會很突兀；推開一樣能解救玩家")]
    public bool pushYieldingCarsInstead = true;

    [Header("診斷")]
    public bool logRescue = true;

    /// <summary>累計救援次數。數字持續上升代表死鎖很常發生，值得回頭修根因</summary>
    public int RescueCount { get; private set; }

    private Rigidbody rb;
    private CarPhysicsController carPhysics;
    private float stuckTimer = 0f;

    private const float MsToKmH = 3.6f;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();

        carPhysics = GetComponent<CarPhysicsController>();
        if (carPhysics == null) carPhysics = GetComponentInParent<CarPhysicsController>();

        if (carPhysics == null)
            Debug.LogError("[卡死救援] 找不到 CarPhysicsController，無法判斷玩家是否踩油門。", this);
    }

    private void Update()
    {
        if (rb == null || carPhysics == null) return;

        bool wantsToMove = carPhysics.ThrottleInput >= minThrottle
                           && carPhysics.BrakeInput <= maxBrakeWhileStuck
                           && !carPhysics.IsParked;

        float speedKmH = rb.velocity.magnitude * MsToKmH;

        if (wantsToMove && speedKmH < stuckSpeedKmH)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer >= stuckSeconds)
            {
                stuckTimer = 0f;
                Rescue();
            }
        }
        else
        {
            stuckTimer = 0f;
        }
    }

    private void Rescue()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, searchRadius, ~0,
                                                QueryTriggerInteraction.Ignore);

        int cleared = 0;
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.transform.IsChildOf(transform.root)) continue;   // 排除自己

            // 找到那台車的根物件再處理，才不會只刪掉一個車輪
            ReverseThreatAICar white = h.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation gray = h.GetComponentInParent<CarNavigation>();
            if (white == null && gray == null) continue;

            GameObject target = white != null ? white.gameObject : gray.gameObject;

            if (excludeAmbulance && white is AmbulanceAI)
            {
                if (logRescue)
                    Debug.Log($"<color=grey>🆘 [卡死救援] 略過救護車 {target.name}（移除會中斷事件）</color>");
                continue;
            }

            // 正在為救護車避讓的車是事件的一部分，憑空消失會很突兀 —— 改用推開。
            // 救護車的 yieldingCars 迴圈都有 null 檢查，就算移除也不會出錯，
            // 但保留它們可以讓避讓與回歸的畫面維持完整。
            bool isYielding = (white != null && white.IsYieldingForAmbulanceNow)
                              || (gray != null && gray.IsYieldingForAmbulanceNow);

            bool shouldDestroy = destroyBlockers && !(isYielding && pushYieldingCarsInstead);

            if (shouldDestroy)
            {
                if (logRescue)
                    Debug.LogWarning($"🆘 [卡死救援] 移除卡住玩家的車：{target.name}", target);
                Destroy(target);
            }
            else
            {
                // 沿「從玩家指向它」的方向推開，讓出空間
                Vector3 away = target.transform.position - transform.position;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = transform.right;
                target.transform.position += away.normalized * pushDistance;

                if (logRescue)
                    Debug.LogWarning($"🆘 [卡死救援] 推開卡住玩家的車：{target.name}（{pushDistance}m）" +
                                     (isYielding ? "｜它正在為救護車避讓，保留不移除" : ""), target);
            }
            cleared++;
        }

        if (cleared > 0) RescueCount++;

        if (logRescue)
        {
            // 每次卡死都完整列出周圍碰撞體 —— 清掉 AI 車之後如果還卡著，
            // 兇手就在這份清單裡（靜態碰撞體、隱形物件、被包住的車…）
            Debug.LogWarning($"🆘 [卡死救援] 觸發｜已處理 {cleared} 台 AI 車（累計 {RescueCount} 次）" +
                             $"｜以下列出玩家周圍 {searchRadius}m 內所有碰撞體：");
            LogNearbyColliders(hits);
        }
    }

    /// <summary>
    /// 列出玩家周圍每一個碰撞體的完整資訊。
    ///
    /// 「有東西卡住但看不見」通常是這幾種：
    ///   - Renderer 被停用或沒有 Renderer → 有碰撞體但看不到
    ///   - Kinematic 剛體 → 在物理上等同無限質量的牆，玩家推不動
    ///   - 兩台車完全重疊 → 外面那台把裡面那台完全包住
    /// 所以這裡把「看不看得見」「是不是 Kinematic」都印出來。
    /// </summary>
    private void LogNearbyColliders(Collider[] hits)
    {
        int n = 0;
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.transform.IsChildOf(transform.root)) continue;   // 排除玩家自己

            n++;
            Transform root = h.transform.root;

            // 完整階層路徑，方便直接在 Hierarchy 找到它
            string path = h.name;
            Transform t = h.transform.parent;
            while (t != null) { path = t.name + "/" + path; t = t.parent; }

            Rigidbody hrb = h.attachedRigidbody;
            string rbInfo = hrb == null
                ? "無 Rigidbody（靜態碰撞體）"
                : (hrb.isKinematic ? "Kinematic 剛體 ← 推不動的元兇" : "一般剛體");

            Renderer rend = h.GetComponent<Renderer>();
            if (rend == null) rend = h.GetComponentInChildren<Renderer>();
            string visInfo = rend == null ? "❗沒有 Renderer（完全看不見）"
                           : (!rend.enabled ? "❗Renderer 被停用（看不見）"
                           : (!h.gameObject.activeInHierarchy ? "❗物件未啟用" : "看得見"));

            float dist = Vector3.Distance(transform.position, h.bounds.ClosestPoint(transform.position));

            Debug.LogWarning($"   [{n}] 「{h.name}」距離 {dist:F2}m｜Layer {h.gameObject.layer}" +
                             $"｜{rbInfo}｜{visInfo}\n        路徑：{path}\n        根物件：{root.name}", h.gameObject);
        }

        if (n == 0)
            Debug.LogWarning("   附近沒有任何碰撞體。可能是懸吊卡住、車輪陷進地形，或 Rigidbody 的 Constraints 被鎖住。");
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, searchRadius);
    }
#endif
}
