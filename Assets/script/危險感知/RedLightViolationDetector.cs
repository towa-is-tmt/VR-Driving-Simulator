using UnityEngine;

/// <summary>
/// 🚦 闖紅燈偵測
///
/// 掛在玩家車上（有 Rigidbody 的那個物件）。
///
/// 判定來源是場景中既有的 `TrafficLightTrigger` 觸發區 —— 每個路口進場處都已經有一個，
/// 它本來就在告訴 AI 車該不該停，這裡讓它順便也通知玩家車。
/// 所以**不需要在場景放任何新的觸發區**。
///
/// 判定邏輯：
///   在觸發區內時持續記錄「對我而言是不是紅燈」，
///   離開觸發區的當下如果仍是紅燈、而且車子是往前開出去的 → 闖紅燈。
///   停在停止線前不會離開觸發區，所以不會誤判。
///
/// 黃燈不算違規（黃燈通過本來就合法）。
/// </summary>
public class RedLightViolationDetector : MonoBehaviour
{
    [Header("判定條件（兩段式，比照真實的紅燈照相）")]
    // 現實中的紅燈照相要拍到兩張才成立闖紅燈：
    //   第一張 = 紅燈時越過停止線
    //   第二張 = 同一台車繼續前進、進入路口
    // 只越線但停住不算闖紅燈（那是比較輕的「紅燈越線」）。
    // 法規的精神是：闖紅燈的危險在於你進入路口跟橫向來車衝突，
    // 車頭稍微過線但停住並沒有製造那個危險。
    [Tooltip("越過停止線後還要再前進幾公尺才算真的闖進路口")]
    public float continueDistance = 8f;
    [Tooltip("越線後速度掉到這個值以下就視為「有停下來」，只記越線不記闖紅燈（km/h）")]
    public float stoppedSpeedKmH = 3f;
    [Tooltip("越線後最多追蹤幾秒。這只是防止狀態卡住的保險，\n正常情況是等到燈號轉綠才結束追蹤，所以要設得比一次紅燈時長還久")]
    public float pendingTimeout = 60f;
    [Tooltip("離開觸發區時朝前的速度要高於這個值才開始追蹤（km/h）")]
    public float minCrossSpeedKmH = 5f;
    [Tooltip("兩次警告之間的最短間隔，避免連續路口洗版（秒）")]
    public float cooldownSeconds = 6f;

    [Header("🚦 排除「只是路過停等區」的誤判")]
    // 停等盒本來是給 AI 判斷該不該停用的,尺寸照 AI 車道排。
    // 玩家擦到邊角、轉彎橫越、在路口附近變換車道都會觸發 OnTriggerExit，
    // 原本只要「離開當下是紅燈 + 往前開 5km/h」就開始追蹤 —— 完全不管你往哪個方向走。
    [Tooltip("車頭必須與車道方向平行到這個程度才算越線。0.7≈夾角 45 度內。橫越、擦邊會被擋掉")]
    [Range(0f, 1f)]
    public float alongLaneDot = 0.7f;
    // 沿車道開會走過整個盒子(22~25m),橫越或擦邊最多只有短軸那 2~7m。
    [Tooltip("在停等區內至少要走這麼多公尺才算真的通過。用來排除擦邊角")]
    public float minTravelThroughZone = 4f;

    [Header("🚑 禮讓救護車時不判定")]
    // 禮讓救護車而越過停止線在現實中是合法的。少了這道豁免，玩家做對的事反而被開單，
    // 考照評分等於在教錯的行為 —— 這比單純「多記一筆違規」嚴重得多。
    [Tooltip("附近有救護車時不判定闖紅燈與越線")]
    public bool suppressWhileAmbulanceNear = true;
    [Tooltip("救護車在這個距離內就暫停判定。太大的話「救護車還很遠就闖過去」也會被放過")]
    public float ambulanceSuppressRadius = 40f;

    [Header("警告顯示")]
    [TextArea(2, 3)]
    [Tooltip("留空就不顯示文字。訊息類型靠顏色區分，不用圖示")]
    public string violationMessage = "您已闖紅燈了";
    public Color violationColor = new Color(1f, 0.15f, 0.12f);
    public float displaySeconds = 4f;

    [Header("紅燈越線（較輕的違規，越線但有停住）")]
    [TextArea(2, 3)]
    [Tooltip("留空就不提醒。這是比闖紅燈輕的違規，現實中罰則也不同")]
    public string lineCrossMessage = "紅燈越線\n應停在停止線前";
    public Color lineCrossColor = new Color(1f, 0.62f, 0.05f);
    [Tooltip("越線的警示音，建議用 聲音/警示_紅燈越線.wav（比闖紅燈溫和）")]
    public AudioClip lineCrossSound;
    [Range(0f, 2f)]
    public float lineCrossVolume = 0.85f;

    /// <summary>累計紅燈越線（但有停住）次數</summary>
    public int LineCrossCount { get; private set; }

    [Header("警告音效")]
    public AudioClip violationSound;
    [Tooltip("留空會自動新增一個 2D 音源")]
    public AudioSource violationAudioSource;
    [Range(0f, 2f)]
    public float violationVolume = 1f;

    [Header("診斷")]
    public bool logViolations = true;
    [Tooltip("印出偵測器收到的每一次燈號回報與離開事件，用來確認觸發區有沒有跟玩家車溝通")]
    public bool debugLogZone = false;

    private bool lastLoggedZoneState = false;
    private bool hasEverReceivedZoneState = false;

    /// <summary>累計闖紅燈次數，可供考照評分讀取</summary>
    public int ViolationCount { get; private set; }

    private Rigidbody rb;
    private bool zoneIsRed = false;
    private Vector3 zoneEntryPos;
    private float lastZoneStayTime = -999f;
    private float nextAllowedTime = -1f;

    // 越線後的追蹤狀態（等同紅燈照相的「第二張」）
    private bool pending = false;
    private bool lineCrossReported = false;
    private Vector3 pendingLinePos;
    private TrafficSystem_Brain pendingBrain;
    private bool pendingIsNorthSouth;
    private bool pendingIsSpecial;
    private float pendingUntil;

    private const float MsToKmH = 3.6f;

    private float ForwardSpeedKmH =>
        rb != null ? Vector3.Dot(rb.velocity, transform.forward) * MsToKmH : 0f;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
    }

    /// <summary>由 TrafficLightTrigger 每幀呼叫，回報玩家所在觸發區當下的燈號。</summary>
    public void ReportZoneState(bool isRedForPlayer)
    {
        if (debugLogZone && (!hasEverReceivedZoneState || lastLoggedZoneState != isRedForPlayer))
        {
            hasEverReceivedZoneState = true;
            lastLoggedZoneState = isRedForPlayer;
            Debug.Log($"<color=cyan>🚦 [偵測] 玩家在停等區內，對他而言現在是「{(isRedForPlayer ? "紅燈" : "綠燈或黃燈")}」</color>");
        }
        // 距上次收到 Stay 超過一小段時間 = 這是「重新進入」停等區,記下進入點。
        // 出口再跟這個點相減就知道玩家在區內實際走了多遠。
        if (Time.time - lastZoneStayTime > 0.25f)
            zoneEntryPos = transform.position;
        lastZoneStayTime = Time.time;

        zoneIsRed = isRedForPlayer;
    }

    /// <summary>
    /// 由 TrafficLightTrigger 在玩家離開觸發區時呼叫。
    /// 這裡只等同「第一張照片：紅燈越線」，還不能判定闖紅燈 ——
    /// 要看接下來玩家有沒有繼續開進路口。
    /// </summary>
    public void ReportZoneExit(TrafficSystem_Brain brain, bool isNorthSouthSide, Vector3 laneAxis, bool isSpecialIntersection = false)
    {
        bool wasRed = zoneIsRed;
        zoneIsRed = false;

        if (debugLogZone)
            Debug.Log($"<color=cyan>🚦 [偵測] 玩家離開停等區｜離開當下是{(wasRed ? "紅燈" : "綠燈/黃燈")}" +
                      $"｜朝前車速 {ForwardSpeedKmH:F1} km/h（需 ≥ {minCrossSpeedKmH}）" +
                      $"｜{(pending ? "已在追蹤中，忽略" : "")}</color>");

        if (!wasRed || pending) return;

        // 🚑 禮讓救護車而越線是合法的，連「越線」都不該記
        if (IsAmbulanceNear(out float ambDist))
        {
            if (logViolations)
                Debug.Log($"<color=lime>🚑 [紅燈豁免] 救護車在 {ambDist:F0}m 內，" +
                          $"越線視為禮讓救護車，不判定</color>");
            return;
        }

        // 只算「往前開出去」的情況：倒車退出停等區不是越線
        if (ForwardSpeedKmH < minCrossSpeedKmH)
        {
            if (debugLogZone)
                Debug.Log("<color=grey>🚦 [偵測] 車速不足，不算越線（可能是停著或倒車退出）</color>");
            return;
        }

        // 🚦 必須是「沿著車道」離開,不是橫越或擦邊。
        //    laneAxis 是停等盒的長軸(車道方向),取絕對值是因為兩個進場方向共用同一條軸。
        Vector3 fwd = transform.forward; fwd.y = 0f;
        float along = laneAxis.sqrMagnitude > 0.0001f
            ? Mathf.Abs(Vector3.Dot(fwd.normalized, laneAxis.normalized))
            : 1f;
        if (along < alongLaneDot)
        {
            if (logViolations)
                Debug.Log($"<color=grey>🚦 [偵測] 不算越線：車頭與車道夾角太大" +
                          $"（平行度 {along:F2} < {alongLaneDot}），判定為橫越或擦邊而非沿車道通過</color>");
            return;
        }

        // 🚦 必須真的穿過停等區。沿車道開會走過 20m 以上,擦到邊角只有幾公尺
        float travelled = Vector3.Distance(transform.position, zoneEntryPos);
        if (travelled < minTravelThroughZone)
        {
            if (logViolations)
                Debug.Log($"<color=grey>🚦 [偵測] 不算越線：區內只走了 {travelled:F1}m" +
                          $"（需 {minTravelThroughZone}m），判定為擦到邊角</color>");
            return;
        }

        pending = true;
        lineCrossReported = false;
        pendingLinePos = transform.position;
        pendingBrain = brain;
        pendingIsNorthSouth = isNorthSouthSide;
        pendingIsSpecial = isSpecialIntersection;
        pendingUntil = Time.time + pendingTimeout;

        if (logViolations)
            Debug.Log($"<color=orange>🚦 [紅燈越線] 玩家越過停止線，開始追蹤是否繼續進入路口</color>");
    }

    private void Update()
    {
        if (!pending) return;

        // 🚑 追蹤途中救護車才出現（例如你已經越線停著，救護車從後方逼近，
        //    你只好再往前挪讓路）—— 這段位移不該被算成「繼續闖進路口」
        if (IsAmbulanceNear(out float ambDist))
        {
            pending = false;
            if (logViolations)
                Debug.Log($"<color=lime>🚑 [紅燈豁免] 追蹤中救護車接近到 {ambDist:F0}m，取消闖紅燈追蹤</color>");
            return;
        }

        // 燈號變了就不追究：越線當下是紅燈，但很快轉綠的話沒有製造衝突
        if (pendingBrain != null && !IsRedForSide(pendingBrain, pendingIsNorthSouth, pendingIsSpecial))
        {
            pending = false;
            return;
        }

        if (Time.time > pendingUntil) { pending = false; return; }

        float travelled = Vector3.Distance(transform.position, pendingLinePos);

        // 第二張照片：繼續前進超過門檻 → 真的闖進路口了。
        // 中間有沒有停過都一樣 —— 只要紅燈期間進入路口就是闖紅燈，
        // 「越線→停住→又繼續開過去」在現實中同樣成立。
        if (travelled >= continueDistance)
        {
            pending = false;
            ReportViolation(travelled);
            return;
        }

        // 停住了 → 先記一次越線，但**繼續追蹤**：
        // 玩家可能停一下又決定衝過去，那時就要升級成闖紅燈。
        if (!lineCrossReported && ForwardSpeedKmH < stoppedSpeedKmH)
        {
            lineCrossReported = true;
            ReportLineCross(travelled);
        }
    }

    /// <summary>
    /// 附近有沒有救護車。
    ///
    /// 刻意不去動 AmbulanceAI 加一個 static Transform —— 那支是避讓邏輯的核心檔案。
    /// 這裡改成先看現成的 IsAmbulanceActive（純 static bool，幾乎零成本），
    /// 只有真的有救護車在場時才做一次 FindObjectOfType。
    /// 而且整個函式只在「紅燈越線當下」與「追蹤中」才會被呼叫，不是每幀全域搜尋。
    /// </summary>
    private bool IsAmbulanceNear(out float distance)
    {
        distance = Mathf.Infinity;

        if (!suppressWhileAmbulanceNear) return false;
        if (!AmbulanceAI.IsAmbulanceActive) return false;

        AmbulanceAI amb = FindObjectOfType<AmbulanceAI>();
        if (amb == null) return false;   // 旗標還沒被 OnDestroy 清掉的空窗期

        distance = Vector3.Distance(transform.position, amb.transform.position);
        return distance <= ambulanceSuppressRadius;
    }

    // 跟 TrafficLightTrigger.IsRedForPlayerNow 是同一套判定，改動時兩邊要一起改。
    // 黃燈不算紅燈（黃燈通過合法）；特殊路口的東西向把「直行綠」與「左轉綠」都算成不是紅燈，
    // 因為停等區分不出玩家實際在哪個車道，寧可漏判也不要誤告玩家闖紅燈。
    private bool IsRedForSide(TrafficSystem_Brain brain, bool isNorthSouth, bool isSpecial)
    {
        if (brain == null) return false;

        if (!isSpecial)
        {
            var p = brain.normalPhase;
            return isNorthSouth
                ? (p != TrafficSystem_Brain.NormalPhase.A_to_C_Green
                   && p != TrafficSystem_Brain.NormalPhase.A_to_C_Yellow)
                : (p != TrafficSystem_Brain.NormalPhase.B_to_D_Green
                   && p != TrafficSystem_Brain.NormalPhase.B_to_D_Yellow);
        }

        var s = brain.specialPhase;
        if (isNorthSouth)
        {
            return s != TrafficSystem_Brain.SpecialPhase.A_to_C_Green
                && s != TrafficSystem_Brain.SpecialPhase.A_to_C_Yellow;
        }

        return s != TrafficSystem_Brain.SpecialPhase.B_to_D_Green
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_Yellow
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftGreen
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftYellow;
    }

    private void ReportViolation(float travelled)
    {
        if (Time.time < nextAllowedTime) return;
        nextAllowedTime = Time.time + cooldownSeconds;
        ViolationCount++;

        // 先前已記為「越線」的話要扣回來：同一次事件升級成闖紅燈，
        // 較輕的違規被較重的取代，不該兩筆都算。
        if (lineCrossReported && LineCrossCount > 0) LineCrossCount--;

        if (HazardAwarenessTracker.Instance != null && !string.IsNullOrEmpty(violationMessage))
            HazardAwarenessTracker.Instance.ShowMessage(violationMessage, violationColor, displaySeconds);

        PlaySound(violationSound, violationVolume);

        if (logViolations)
            Debug.LogWarning($"🚦 [闖紅燈] 玩家越線後又前進 {travelled:F1}m 進入路口（累計 {ViolationCount} 次）", this);
    }

    private void ReportLineCross(float travelled)
    {
        LineCrossCount++;

        if (HazardAwarenessTracker.Instance != null && !string.IsNullOrEmpty(lineCrossMessage))
            HazardAwarenessTracker.Instance.ShowMessage(lineCrossMessage, lineCrossColor, displaySeconds);

        PlaySound(lineCrossSound, lineCrossVolume);

        if (logViolations)
            Debug.Log($"<color=orange>🚦 [紅燈越線] 玩家越線 {travelled:F1}m 後停住，" +
                      $"不算闖紅燈（累計 {LineCrossCount} 次）</color>");
    }

    /// <summary>闖紅燈與紅燈越線共用同一個 AudioSource，只是換不同的 clip 與音量。</summary>
    private void PlaySound(AudioClip clip, float volume)
    {
        if (clip == null) return;

        if (violationAudioSource == null)
        {
            violationAudioSource = gameObject.AddComponent<AudioSource>();
            violationAudioSource.playOnAwake = false;
            violationAudioSource.loop = false;
            violationAudioSource.spatialBlend = 0f;   // 系統警告，2D
            violationAudioSource.dopplerLevel = 0f;
        }
        violationAudioSource.PlayOneShot(clip, volume);
    }
}
