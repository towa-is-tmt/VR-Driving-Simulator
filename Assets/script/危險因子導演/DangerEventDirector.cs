using System.Collections.Generic;
using UnityEngine;

// 🎬 危險事件導演:所有危險因子發動前都要先問它,統一控制節奏
public class DangerEventDirector : MonoBehaviour
{
    public static DangerEventDirector Instance;

    [Header("節奏控制")]
    [Tooltip("任一事件發動後,所有事件共同冷卻幾秒")]
    public float globalCooldown = 30f;

    [Range(0f, 100f)]
    [Tooltip("冷卻結束後,每次條件滿足實際發動的機率(%)。主秀類建議 100")]
    public float triggerChance = 100f;

    [Tooltip("同一種事件不連續出現兩次")]
    public bool avoidRepeat = true;

    [Header("🚗 急煞/倒車 專屬節流(防止白車多就洗版)")]
    [Tooltip("急煞或倒車發動後,要過幾秒才能再有急煞/倒車。這段空檔留給側切/故障車/救護車")]
    public float scannerThrottle = 90f;

    [Header("⏱ 最低反應間隔 (硬性,任何情況都不能跳過)")]
    [Tooltip("任兩個危險因子之間至少要隔這麼久,連『覆蓋保證』也不能跳過。給玩家反應與喘息的時間")]
    public float minGapBetweenEvents = 10f;

    [Header("🎯 覆蓋保證")]
    [Tooltip("開啟後:沒經歷過的危險因子,條件一滿足就直接放行(跳過機率),保證每種至少一次")]
    public bool guaranteeCoverage = true;
    [Tooltip("五種都經歷過後,是否清空記錄重新一輪(訓練模式建議開,自由模式建議關)")]
    public bool resetAfterAllCovered = false;

    private float lastEventTime = -999f;
    private float lastScannerEventTime = -999f;
    private string lastEventName = "";
    private bool longEventActive = false; // 🔒 有長事件(故障車/救護車)進行中

    // 已經歷過的危險因子記錄
    private readonly HashSet<string> experienced = new HashSet<string>();
    // 🔧 補上「路口衝刺」：IntersectionManager 一直有 RequestEvent("路口衝刺")，但舊清單沒有它，
    //    覆蓋保證與「一輪跑完重置」都不會把它算進去。
    private static readonly string[] AllFactors = { "急煞", "倒車", "側邊衝出", "故障車", "救護車", "路口衝刺" };

    // 🆕 其他系統可以在執行時自行登記新的危險因子（例如危險駕駛 AI 的「鬼切」「路霸」），
    //    不必每加一種就回來改上面的固定清單。只有場景裡真的有掛的系統才會登記，
    //    所以沒放進場景的因子不會讓「全部覆蓋」永遠達不到。
    private readonly HashSet<string> registeredFactors = new HashSet<string>();

    /// <summary>登記一個執行時才加入的危險因子，讓它參與覆蓋保證與一輪重置</summary>
    public void RegisterFactor(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) return;
        if (registeredFactors.Add(eventName))
            UnityEngine.Debug.Log($"<color=grey>🎬 導演:登記新危險因子「{eventName}」</color>");
    }

    private void Awake() { Instance = this; }

    public bool IsLongEventActive => longEventActive; // 供救護車生成前查詢:有大事件在演嗎

    /// <summary>
    /// 🔧 距上一個危險因子是否已滿最低反應間隔。
    ///
    /// 救護車不走 RequestEvent(它用 BeginLongEvent 直接開場),
    /// 所以原本完全繞過冷卻檢查 —— 側邊切入剛演完下一秒就可能來救護車,
    /// 玩家根本來不及反應。生成前要一併問這個。
    /// </summary>
    public bool IsMinGapSatisfied => Time.time - lastEventTime >= minGapBetweenEvents;

    /// <summary>🔧 還要等幾秒才滿足最低反應間隔(給診斷訊息用)</summary>
    public float RemainingMinGap => Mathf.Max(0f, minGapBetweenEvents - (Time.time - lastEventTime));

    // 供玩家雷達查詢:這個因子還沒經歷過嗎(用來優先挑沒經歷過的急煞/倒車)
    public bool IsUncovered(string eventName)
        => guaranteeCoverage && !experienced.Contains(eventName);

    private static bool IsScannerEvent(string name) => name == "急煞" || name == "倒車";

    [Header("🔧 診斷")]
    [Tooltip("開啟後,每次事件被否決都會在 Console 說明原因。原本是靜默 return,完全查不出被擋在哪")]
    public bool logRejections = true;

    private string lastRejectKey = "";
    private float lastRejectLogTime = -999f;

    /// <summary>🔧 統一的否決出口:會說明理由,同一個理由每 2 秒最多印一次</summary>
    private bool Reject(string eventName, string reason)
    {
        if (logRejections)
        {
            string key = eventName + "|" + reason;
            if (key != lastRejectKey || Time.time - lastRejectLogTime > 2f)
            {
                lastRejectKey = key;
                lastRejectLogTime = Time.time;
                UnityEngine.Debug.Log($"<color=orange>🎬 事件否決:{eventName} —— {reason}</color>");
            }
        }
        return false;
    }

    /// <summary>🔧 供外部查詢目前導演狀態(除錯用)</summary>
    public string DebugStatus =>
        $"longEventActive={longEventActive} | 距上次事件={Time.time - lastEventTime:F1}s/{globalCooldown}s" +
        $" | 上次事件={lastEventName} | 距上次急煞倒車={Time.time - lastScannerEventTime:F1}s/{scannerThrottle}s";

    public bool RequestEvent(string eventName)
    {
        // 🔒 大事件進行中,全部擋
        if (longEventActive)
            return Reject(eventName, $"有長事件({lastEventName})進行中,全部暫停。若它沒正常結束會永久卡住");

        // 🔧 最低反應間隔:硬性門檻,覆蓋保證也不能跳過
        float sinceLast = Time.time - lastEventTime;
        if (sinceLast < minGapBetweenEvents)
            return Reject(eventName, $"未達最低反應間隔,還要等 {minGapBetweenEvents - sinceLast:F1} 秒 (minGapBetweenEvents={minGapBetweenEvents})");

        // 全域冷卻(一次一個)
        if (sinceLast < globalCooldown)
            return Reject(eventName, $"全域冷卻中,還要等 {globalCooldown - sinceLast:F1} 秒 (globalCooldown={globalCooldown})");

        if (avoidRepeat && eventName == lastEventName)
            return Reject(eventName, $"與上一個事件相同,avoidRepeat 擋掉(上次={lastEventName})");

        // 🚗 急煞/倒車專屬長節流:剛演過急煞/倒車,這段時間不再有,把舞台讓給主秀
        if (IsScannerEvent(eventName))
        {
            float sinceScanner = Time.time - lastScannerEventTime;
            if (sinceScanner < scannerThrottle)
                return Reject(eventName, $"急煞/倒車節流中,還要等 {scannerThrottle - sinceScanner:F1} 秒 (scannerThrottle={scannerThrottle})");
        }

        // 🎯 覆蓋優先:沒經歷過的直接放行(跳過機率);已經歷過的照常骰機率
        bool uncovered = guaranteeCoverage && !experienced.Contains(eventName);
        if (!uncovered)
        {
            if (UnityEngine.Random.Range(0f, 100f) > triggerChance)
                return Reject(eventName, $"機率沒骰過(triggerChance={triggerChance}%)");
        }

        // 通過所有關卡 → 核准並記錄
        lastEventTime = Time.time;
        lastEventName = eventName;
        if (IsScannerEvent(eventName)) lastScannerEventTime = Time.time;
        MarkExperienced(eventName);

        UnityEngine.Debug.Log($"🎬 事件核准:{eventName}{(uncovered ? "(首次覆蓋)" : "")}");
        return true;
    }

    // 長事件開始:鎖住,直到它結束前誰都不能觸發
    public void BeginLongEvent(string eventName)
    {
        longEventActive = true;
        lastEventTime = Time.time;
        lastEventName = eventName;
        MarkExperienced(eventName); // 救護車不走 RequestEvent,靠這裡登記覆蓋
        UnityEngine.Debug.Log($"🔒 長事件開始:{eventName},其他危險因子全部暫停");
    }

    // 長事件結束:解鎖,從這一刻才開始算冷卻
    public void EndLongEvent()
    {
        longEventActive = false;
        lastEventTime = Time.time;
        UnityEngine.Debug.Log($"🔓 長事件結束,冷卻 {globalCooldown} 秒後才可觸發下一個");
    }

    // 登記某因子已經歷,並處理「一輪跑完重置」
    private void MarkExperienced(string eventName)
    {
        experienced.Add(eventName);

        if (resetAfterAllCovered && AllExperienced())
        {
            experienced.Clear();
            UnityEngine.Debug.Log("🔄 所有危險因子已覆蓋一輪,重置記錄,開始下一輪覆蓋");
        }
    }

    private bool AllExperienced()
    {
        foreach (var f in AllFactors)
            if (!experienced.Contains(f)) return false;
        foreach (var f in registeredFactors)
            if (!experienced.Contains(f)) return false;
        return true;
    }
}
