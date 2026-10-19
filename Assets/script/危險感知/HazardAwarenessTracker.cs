using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>危險因子要用哪一套提示策略</summary>
public enum HazardStyle
{
    /// <summary>完整：後照鏡警示 + 未察覺升級 + 反應記錄。適合救護車這種持續數十秒的接近事件。</summary>
    Full,

    /// <summary>
    /// 靜默：只做反應量測與事後回饋，當下不給任何提示。
    /// 適合側邊切入這種「無預警才是危險本體」的短事件 ——
    /// 事前提示會直接消滅危險感，後照鏡警示的方位也不對。
    /// </summary>
    Silent
}

/// <summary>
/// 🎯 危險感知追蹤器
///
/// 負責三件事，全部是「觀察」性質，不會去指揮任何車輛：
///   1. 記錄每個危險因子的發動時間，偵測玩家是否有反應（減速 / 往旁邊閃 / 轉頭看鏡子）
///   2. 玩家遲遲沒反應時提高「升級等級」，讓危險因子自己去加大提示強度
///   3. 把「發動 → 首次反應」的延遲記錄下來，供考照評分使用
///
/// ⚠️ 這個腳本不會修改任何車輛的行進、避讓或回歸邏輯，只提供查詢。
/// 場景中放一個空物件掛上它即可（單例）。
/// </summary>
public class HazardAwarenessTracker : MonoBehaviour
{
    public static HazardAwarenessTracker Instance;

    [Header("玩家參照（留空會自動用 Player tag 尋找）")]
    public Transform playerTransform;
    public Rigidbody playerRigidbody;
    [Tooltip("VR 頭部（通常是 Main Camera）。留空就不做「有沒有轉頭看鏡子」的判定")]
    public Transform playerHead;

    [Header("反應判定門檻（任一項成立就算玩家察覺了）")]
    [Tooltip("時速比危險因子發動當下掉了幾 km/h 算有反應")]
    public float reactionSpeedDropKmH = 5f;
    [Tooltip("相對發動當下往側向偏移幾公尺算有閃避")]
    public float reactionLateralShift = 0.8f;
    [Tooltip("頭部水平轉了幾度算有在查看後方")]
    public float reactionHeadTurnAngle = 25f;

    [Header("碰撞判定")]
    // 關掉的話只有撞到「演出那台車」才算 —— 為了閃它而去撞護欄就不計入。
    [Tooltip("危險因子演出期間撞到任何東西都算失敗")]
    public bool countAnyCollisionDuringHazard = true;

    [Header("未察覺就升級")]
    [Tooltip("每隔幾秒沒反應就升一級，危險因子會據此加大警笛/喇叭")]
    public float escalateAfterSeconds = 6f;
    public int maxEscalationLevel = 2;

    [Header("反應時間分級（秒）")]
    // 門檻是照真實駕駛的反應時間研究設的，不是隨便抓的數字：
    //   實驗室、預期中的刺激        約 0.20~0.25 秒
    //   真實路況、預期中的事件      約 0.70~1.00 秒
    //   突發事件（側切就是這一類）  約 1.25~1.50 秒
    //   AASHTO 停車視距的設計值     2.5 秒（涵蓋約 90% 的駕駛）
    // 所以對突發危險而言，1 秒內是優於平均、1.5 秒是平均帶、超過 2.5 秒才真的算過慢。
    // 這也讓評分有公開標準可以引用，而不是「系統說你慢就是慢」。
    [Tooltip("延遲在這個秒數內 = 反應良好（優於真實駕駛對突發事件的平均值）")]
    public float reactionGoodSeconds = 1.0f;
    [Tooltip("延遲在這個秒數內 = 反應一般（真實駕駛面對突發事件的平均帶）")]
    public float reactionNormalSeconds = 1.5f;
    [Tooltip("延遲超過這個秒數 = 反應過慢（AASHTO 停車視距採用的設計值）")]
    public float reactionSlowSeconds = 2.5f;

    [Header("事後回饋顯示（可選，留空就只輸出到 Console）")]
    [Tooltip("危險因子結束後顯示「反應良好 / 未及時反應」的文字物件。可以跟禮讓提示共用同一個")]
    public TMP_Text feedbackText;
    [Tooltip("回饋訊息顯示幾秒")]
    public float feedbackSeconds = 3.5f;
    public Color feedbackGoodColor = new Color(0.25f, 1f, 0.35f);
    public Color feedbackBadColor = new Color(1f, 0.22f, 0.18f);
    [Tooltip("強制所有提示都用粗體。同一個文字物件本來就該一樣粗，\n如果你覺得兩段訊息粗細不同，多半是指到了不同物件，或只是顏色造成的視覺差")]
    public bool useBoldText = true;
    [Tooltip("有預告的事件失敗時，附在評語後面的那一句教學提示。留空 = 不附")]
    public string telegraphedMissHint = "注意路邊停放車輛的動態";

    [Header("🔊 事後語音指導")]
    // 一定要「事件結束後」才播 —— 突發型危險因子（側切/急煞/倒車）事前給任何提示
    // 都會把危險本體消滅掉，這是這套系統一開始就定下的原則。
    // 事後播的好處是：玩家剛撞完，注意力還在路上，不會盯著面板看字。
    [Tooltip("播事後語音的 AudioSource。留空會自動在本物件上建一個 2D 音源")]
    public AudioSource voiceAudioSource;
    [Tooltip("只有失敗（未察覺／撞到）才播語音。做對事不需要被說教")]
    public bool voiceOnlyOnFailure = true;
    // 上限刻意開到 3 —— PlayOneShot 的 volumeScale 允許大於 1，
    // 而語音要在行駛中的引擎聲底下聽得清楚，1.0 常常還不夠。
    [Tooltip("語音音量。可以大於 1（PlayOneShot 的倍率沒有 1 的上限）")]
    [Range(0f, 3f)] public float voiceVolume = 1.5f;

    /// <summary>一種危險因子對應一句事後指導語音</summary>
    [System.Serializable]
    public class HazardVoice
    {
        [Tooltip("要對應哪個危險因子，字串要跟 RegisterHazard 傳進來的名稱一致\n" +
                 "（目前有：側邊衝出／前車急煞／前車倒車／故障車／救護車）")]
        public string hazardName;
        [Tooltip("玩家未察覺或撞到時播的指導語音")]
        public AudioClip missedClip;
        [Tooltip("玩家反應良好時播的語音。留空＝不播（建議留空）")]
        public AudioClip goodClip;
    }

    [Tooltip("每種危險因子的事後語音。沒列到的就不播")]
    public List<HazardVoice> hazardVoices = new List<HazardVoice>();

    // ── 📊 危險感知統計 ────────────────────────────────────────
    // 刻意「不」扣分：ScoreManager 的分數是模擬考場項目（S彎、坡道、停車…）的成績，
    // 危險應變是另一個維度的能力，混進同一個分數裡兩邊都會失真。
    // 這裡只累計次數，訓練模式與自由模式都照記，結算畫面要怎麼呈現之後再接。
    //
    // 全部是唯讀屬性，外部只能讀不能改，避免 UI 那邊不小心寫壞統計。
    public int CollisionCount { get; private set; }
    public int NoReactionCount { get; private set; }
    public int SlowReactionCount { get; private set; }
    public int GoodReactionCount { get; private set; }
    public int DangerousBypassCount { get; private set; }
    public int FastPassCount { get; private set; }
    /// <summary>總共遭遇過幾個危險因子</summary>
    public int TotalHazardCount => CollisionCount + NoReactionCount + SlowReactionCount + GoodReactionCount;

    private float reactionDelaySum = 0f;
    private int reactionDelaySamples = 0;
    /// <summary>有反應的那些事件的平均反應延遲（秒）。沒有樣本回 -1</summary>
    public float AverageReactionDelay =>
        reactionDelaySamples > 0 ? reactionDelaySum / reactionDelaySamples : -1f;

    [Header("診斷")]
    public bool logReactions = true;

    /// <summary>單次危險因子的完整記錄，供評分使用</summary>
    public class HazardRecord
    {
        public string name;
        public float startTime;
        public float endTime = -1f;
        public bool reacted;
        public float reactionDelay = -1f;   // 發動到首次反應的秒數，-1 = 全程沒反應
        public float leadSeconds;           // 事件發動前玩家拿到多少預告時間（0 = 無預告的突發事件）
        public bool  autoFeedback = true;   // false = 這個危險因子自己負責顯示評語，通用的反應時間分級不適用
        public int maxEscalationReached;
        public string reactionChannel = ""; // 是靠哪個管道察覺的：減速 / 閃避 / 轉頭

        // 危險因子演出期間有沒有撞到東西。
        // 沒有這個欄位的話,撞車會被判成「有反應」—— 因為反應偵測只看「速度有沒有掉」,
        // 而撞擊的速度暴跌比任何煞車都猛,結果撞得越用力分數看起來越漂亮。
        public bool collided;
        public float collisionSeverity;     // 0~1,跟碰撞音效同一套力道分級
        public string collidedWith = "";
    }

    private class ActiveHazard
    {
        public HazardRecord record;
        public Transform source;
        public HazardStyle style;
        public float baselineSpeedKmH;
        public Vector3 baselinePos;
        public float baselineHeadYaw;
        public int escalation;
    }

    private readonly List<ActiveHazard> active = new List<ActiveHazard>();

    /// <summary>已完成的危險因子記錄，供結算畫面讀取</summary>
    public readonly List<HazardRecord> History = new List<HazardRecord>();

    private const float MsToKmH = 3.6f;

    private void Awake() { Instance = this; }

    private void Start() { ResolvePlayer(); }

    private void ResolvePlayer()
    {
        if (playerTransform == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p != null) playerTransform = p.transform;
        }
        if (playerRigidbody == null && playerTransform != null)
            playerRigidbody = playerTransform.GetComponent<Rigidbody>();
        if (playerHead == null && Camera.main != null)
            playerHead = Camera.main.transform;
    }

    private float CurrentSpeedKmH =>
        playerRigidbody != null ? playerRigidbody.velocity.magnitude * MsToKmH : 0f;

    private float CurrentHeadYaw =>
        playerHead != null ? playerHead.eulerAngles.y : 0f;

    // ── 對外 API ───────────────────────────────────────────────

    /// <summary>
    /// 危險因子發動時呼叫。source 用來算方位（給後照鏡提示用），可為 null。
    /// style 決定要不要參與後照鏡警示與未察覺升級，預設 Full 以保持既有呼叫端行為不變。
    /// </summary>
    /// <param name="leadSeconds">
    /// 事件發動前玩家已經拿到多少秒的預告（側切車打方向燈、亮煞車燈、前輪轉出的那段）。
    /// 只影響事後回饋的措辭 —— 同樣 1.2 秒的反應延遲，有 2.4 秒預告跟完全沒預告
    /// 是兩件不同的事，不分開講會給出誤導的評語。0 = 無預告的突發事件。
    /// </param>
    /// <param name="autoFeedback">
    /// false = 這個危險因子自己負責顯示評語（例如故障車的繞行評分），
    /// 通用的反應時間分級不會跳出來。反應量測與 History 紀錄照常進行。
    /// </param>
    public void RegisterHazard(string hazardName, Transform source, HazardStyle style = HazardStyle.Full,
                               float leadSeconds = 0f, bool autoFeedback = true)
    {
        ResolvePlayer();

        var h = new ActiveHazard
        {
            record = new HazardRecord { name = hazardName, startTime = Time.time,
                                        leadSeconds = leadSeconds, autoFeedback = autoFeedback },
            source = source,
            style = style,
            baselineSpeedKmH = CurrentSpeedKmH,
            baselinePos = playerTransform != null ? playerTransform.position : Vector3.zero,
            baselineHeadYaw = CurrentHeadYaw,
            escalation = 0
        };
        active.Add(h);

        if (logReactions)
            Debug.Log($"<color=yellow>⚠ [危險感知] 「{hazardName}」發動（{style}），開始計時玩家反應</color>");
    }

    /// <summary>危險因子結束（被摧毀）時呼叫。</summary>
    public void UnregisterHazard(Transform source)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i].source != source) continue;

            var rec = active[i].record;
            rec.endTime = Time.time;
            rec.maxEscalationReached = active[i].escalation;
            History.Add(rec);

            if (logReactions)
            {
                if (rec.collided)
                    Debug.LogWarning($"✘ [危險感知] 「{rec.name}」結束 — 玩家撞上了「{rec.collidedWith}」" +
                                     $"（力道 {rec.collisionSeverity:F2}），判定失敗");
                else if (rec.reacted)
                    Debug.Log($"<color=lime>✔ [危險感知] 「{rec.name}」結束 — 玩家在 {rec.reactionDelay:F1} 秒後察覺（{rec.reactionChannel}），升級到 L{rec.maxEscalationReached}</color>");
                else
                    Debug.LogWarning($"✘ [危險感知] 「{rec.name}」結束 — 玩家全程沒有任何反應！升級到 L{rec.maxEscalationReached}");
            }

            // 📊 統計不看 autoFeedback —— 自己負責評語的危險因子(故障車)一樣要記進來
            TallyHazard(rec);

            // 事後回饋：事件已經結束才顯示，不會破壞當下的驚嚇感
            ShowFeedback(rec);

            active.RemoveAt(i);
        }
    }

    /// <summary>
    /// 🚪 靜默取消進行中的危險因子：直接從清單移除，<b>不評語、不計分、不進 History</b> ——
    /// 當這件事從來沒發生過。
    ///
    /// 給「事件已經發動、但玩家隨即改道（例如在路口轉去別條路）」用：
    /// 那台演員車已經跟玩家無關，玩家也看不到那邊了，這次演出對他毫無意義，
    /// 不該給「反應良好／未及時反應」這種評語，更不該計進反應統計 —— 那會污染考照分數。
    ///
    /// 跟 UnregisterHazard 的差別：後者是「事件正常結束」，要結算＋顯示評語；
    /// 這支是「事件作廢」，什麼都不做，只是把它從進行清單拿掉。
    /// </summary>
    public void CancelHazard(Transform source)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i].source != source) continue;

            if (logReactions)
                Debug.Log($"<color=grey>🚪 [危險感知] 「{active[i].record.name}」作廢（玩家已改道），" +
                          $"不評語、不計分</color>");

            active.RemoveAt(i);
        }
    }

    /// <summary>
    /// 由 PlayerCollisionAudio 在玩家撞到東西時呼叫，把碰撞登記到進行中的危險因子上。
    /// 撞到就是應對失敗，結算時會蓋過「有反應」的判定。
    /// </summary>
    public void ReportCollision(Transform hitObject, float severity01)
    {
        foreach (var h in active)
        {
            // 只算撞到演出那台車時，要比對來源（撞到的物件可能是車體的子物件）
            if (!countAnyCollisionDuringHazard)
            {
                if (hitObject == null || h.source == null) continue;
                if (hitObject != h.source && !hitObject.IsChildOf(h.source)) continue;
            }

            h.record.collided = true;
            h.record.collisionSeverity = Mathf.Max(h.record.collisionSeverity, Mathf.Clamp01(severity01));
            h.record.collidedWith = hitObject != null ? hitObject.name : "?";

            if (logReactions)
                Debug.LogWarning($"💥 [危險感知] 「{h.record.name}」演出期間玩家撞到「{h.record.collidedWith}」" +
                                 $"（力道 {h.record.collisionSeverity:F2}）→ 這次判定為失敗");
        }
    }

    /// <summary>
    /// 進行中的危險因子裡，有沒有任何一個已經被登記為「玩家撞上了」。
    ///
    /// 給繞行評分這類「演出還沒結束就要先下判語」的地方用 —— 那些評分自己不知道有沒有撞到，
    /// 少了這一條就會出現「玩家撞上對向車，繞行評分卻顯示安全通過」這種互相矛盾的結論。
    /// 碰撞資訊在 ReportCollision 當下就寫進 record，所以演出結束前查得到。
    /// </summary>
    public bool AnyActiveHazardCollided()
    {
        foreach (var h in active)
            if (h != null && h.record != null && h.record.collided) return true;
        return false;
    }

    /// <summary>玩家對這個危險因子做出反應了嗎。供「還沒反應就重複提示」之類的邏輯查詢。</summary>
    public bool HasReacted(Transform source)
    {
        foreach (var h in active)
            if (h.source == source) return h.record.reacted;
        return false;
    }

    /// <summary>危險因子查詢自己現在該用多強的提示（0 = 正常，數字越大越激烈）。</summary>
    public int GetEscalationLevel(Transform source)
    {
        foreach (var h in active)
            if (h.source == source)
                return h.style == HazardStyle.Full ? h.escalation : 0;
        return 0;
    }

    /// <summary>
    /// 對外開放的提示顯示，讓其他系統（例如故障車的事前警告）共用同一個文字物件，
    /// 不必各自持有 TMP_Text 參照與清除計時。沒指定 feedbackText 就只是空操作。
    /// </summary>
    public void ShowMessage(string message, Color color, float seconds)
    {
        if (feedbackText == null) return;

        feedbackText.text = message;
        feedbackText.color = color;
        feedbackText.fontStyle = useBoldText ? FontStyles.Bold : FontStyles.Normal;

        CancelInvoke(nameof(ClearFeedback));
        Invoke(nameof(ClearFeedback), Mathf.Max(0.1f, seconds));
    }

    /// <summary>
    /// 反應延遲的分級文字。門檻見上面 reactionGoodSeconds 那組欄位的說明 ——
    /// 對突發危險而言 1 秒內是優於真實駕駛的平均，不該被講成缺點。
    /// </summary>
    public string GradeReaction(float delaySeconds)
    {
        if (delaySeconds < 0f) return "未察覺";
        if (delaySeconds <= reactionGoodSeconds) return "反應良好";
        if (delaySeconds <= reactionNormalSeconds) return "反應一般";
        if (delaySeconds <= reactionSlowSeconds) return "反應偏慢";
        return "反應過慢";
    }

    /// <summary>事件結束後顯示回饋文字。沒指定 feedbackText 就只留在 Console。</summary>
    private void ShowFeedback(HazardRecord rec)
    {
        // 語音在 autoFeedback 判斷「之前」播 —— 自己負責文字評語的危險因子（故障車）
        // 一樣該有事後指導語音，那是兩回事：文字講判定結果，語音講下次要注意什麼。
        PlayFeedbackVoice(rec);

        // 這個危險因子自己有一套評分（例如故障車的繞行評分）→ 不要用通用的反應時間去講，
        // 那組門檻是為突發事件設的，套在可預見的靜態障礙上會誤判。紀錄照留。
        if (!rec.autoFeedback) return;

        // 有預告的事件才提預告秒數 —— 突發型（急煞/倒車）本來就沒有，提了會讓玩家困惑
        string lead = rec.leadSeconds > 0.05f ? $"，預告 {rec.leadSeconds:F1} 秒" : "";

        // 撞到的情況：不重複講「撞到哪裡、撞到什麼」——那是車損系統的職責，
        // 但這次危險因子的「判定結果」還是要講，否則玩家撞了反而什麼回饋都收不到。
        // （車損提示在撞擊當下跳，這則在事件結束後才跳，兩者時間上不會打架。）
        if (rec.collided)
        {
            string delay = rec.reactionDelay >= 0f ? $"（延遲 {rec.reactionDelay:F1} 秒{lead}）" : "";
            // 撞到的人最需要知道下次要看什麼 —— 這一句原本只有「未察覺」那條有，是漏的
            string hint = (rec.leadSeconds > 0.05f && !string.IsNullOrEmpty(telegraphedMissHint))
                ? $"\n{telegraphedMissHint}" : "";
            ShowMessage($"{rec.name}\n未能安全通過{delay}{hint}", feedbackBadColor, feedbackSeconds);
            return;
        }

        if (rec.reacted)
        {
            bool good = rec.reactionDelay <= reactionNormalSeconds;
            ShowMessage($"{rec.name}\n{GradeReaction(rec.reactionDelay)}" +
                        $"（延遲 {rec.reactionDelay:F1} 秒{lead}）",
                        good ? feedbackGoodColor : feedbackBadColor, feedbackSeconds);
        }
        else
        {
            // 有給過預告卻完全沒反應 → 明確告訴玩家線索在哪，這才是可操作的教學
            string hint;
            if (rec.leadSeconds > 0.05f)
            {
                hint = $"未察覺（已預告 {rec.leadSeconds:F1} 秒）";
                if (!string.IsNullOrEmpty(telegraphedMissHint)) hint += $"\n{telegraphedMissHint}";
            }
            else hint = "未及時反應，應提早減速";
            ShowMessage($"{rec.name}\n{hint}", feedbackBadColor, feedbackSeconds);
        }
    }

    /// <summary>📊 把這次的結果計入統計。只累計次數，不動 ScoreManager 的分數。</summary>
    private void TallyHazard(HazardRecord rec)
    {
        string outcome;

        if (rec.collided) { CollisionCount++; outcome = "發生碰撞"; }
        else if (!rec.reacted) { NoReactionCount++; outcome = "全程未反應"; }
        else if (rec.reactionDelay > reactionSlowSeconds)
        {
            SlowReactionCount++;
            outcome = $"反應過慢（{rec.reactionDelay:F1} 秒）";
        }
        else { GoodReactionCount++; outcome = $"反應良好（{rec.reactionDelay:F1} 秒）"; }

        if (rec.reacted && rec.reactionDelay >= 0f)
        {
            reactionDelaySum += rec.reactionDelay;
            reactionDelaySamples++;
        }

        if (logReactions)
            Debug.Log($"<color=cyan>📊 [危險感知統計] {rec.name}：{outcome}" +
                      $"（累計 遭遇{TotalHazardCount} / 良好{GoodReactionCount} / 過慢{SlowReactionCount}" +
                      $" / 未反應{NoReactionCount} / 碰撞{CollisionCount}）</color>");
    }

    /// <summary>
    /// 📊 由 AccidentDirector 回報故障車的繞行結果。
    /// 繞行有自己的判準（跨線時對向有沒有車、通過速度），不套用反應時間分級，
    /// 所以另外計數，不併進上面那四項。
    /// </summary>
    public void ReportBypassResult(bool dangerous, bool tooFast)
    {
        if (dangerous) DangerousBypassCount++;
        if (tooFast) FastPassCount++;

        if (logReactions)
            Debug.Log($"<color=cyan>📊 [繞行統計] 危險繞行 {DangerousBypassCount} 次 / 速度偏快 {FastPassCount} 次</color>");
    }

    /// <summary>
    /// 📊 結算用：把統計整理成一行一項的清單，交給 UI 自己決定怎麼排版。
    /// 訓練模式與自由模式都可以用 —— 這是能力回饋，不是考試成績。
    /// </summary>
    public List<string> BuildSummaryLines()
    {
        var lines = new List<string>();
        if (TotalHazardCount == 0 && DangerousBypassCount == 0 && FastPassCount == 0)
        {
            lines.Add("本次未遭遇危險狀況");
            return lines;
        }

        lines.Add($"遭遇危險狀況 {TotalHazardCount} 次");
        if (GoodReactionCount > 0) lines.Add($"反應良好 {GoodReactionCount} 次");
        if (SlowReactionCount > 0) lines.Add($"反應過慢 {SlowReactionCount} 次");
        if (NoReactionCount > 0) lines.Add($"完全未察覺 {NoReactionCount} 次");
        if (CollisionCount > 0) lines.Add($"發生碰撞 {CollisionCount} 次");
        if (AverageReactionDelay >= 0f) lines.Add($"平均反應延遲 {AverageReactionDelay:F1} 秒");
        if (DangerousBypassCount > 0) lines.Add($"對向有車仍跨越中心線 {DangerousBypassCount} 次");
        if (FastPassCount > 0) lines.Add($"通過事故現場未減速 {FastPassCount} 次");

        return lines;
    }

    /// <summary>📊 重新開始一輪時清空統計（接 ScoreManager.ResetForRetry 或重載場景時呼叫）</summary>
    public void ResetSummary()
    {
        CollisionCount = NoReactionCount = SlowReactionCount = GoodReactionCount = 0;
        DangerousBypassCount = FastPassCount = 0;
        reactionDelaySum = 0f;
        reactionDelaySamples = 0;
        History.Clear();
    }

    /// <summary>
    /// 事後指導語音。跟文字回饋同時發生，但語音講的是「下次要注意什麼」，
    /// 文字講的是「這次的判定結果」—— 兩者互補，不要寫成同一句話。
    /// </summary>
    private void PlayFeedbackVoice(HazardRecord rec)
    {
        if (hazardVoices == null || hazardVoices.Count == 0) return;

        bool failed = rec.collided || !rec.reacted;
        if (voiceOnlyOnFailure && !failed) return;

        AudioClip clip = null;
        foreach (var hv in hazardVoices)
        {
            if (hv == null || hv.hazardName != rec.name) continue;
            clip = failed ? hv.missedClip : hv.goodClip;
            break;
        }
        if (clip == null) return;

        PlayVoice(clip);
    }

    /// <summary>
    /// 播一句事後指導語音。開放給有自己一套判定的系統直接呼叫
    /// （例如 AccidentDirector 的繞行評分 —— 它的結局是「危險繞行／速度偏快」，
    /// 跟這裡的「撞到／未察覺」對不起來，用 hazardVoices 那張表配不出正確的語音）。
    /// 所有語音統一走這一顆音源，音量才會一致。
    /// </summary>
    public void PlayVoice(AudioClip clip)
    {
        if (clip == null) return;

        if (voiceAudioSource == null)
        {
            voiceAudioSource = gameObject.AddComponent<AudioSource>();
            voiceAudioSource.playOnAwake = false;
            voiceAudioSource.spatialBlend = 0f;   // 2D:這是給玩家的旁白,不是場景裡的聲音
        }
        voiceAudioSource.PlayOneShot(clip, voiceVolume);
    }

    private void ClearFeedback()
    {
        if (feedbackText != null) feedbackText.text = "";
    }

    /// <summary>
    /// 給後照鏡提示用：找出目前在玩家「後方」最近的危險因子。
    /// lateralOffset 為正代表在玩家右後方、負代表左後方，絕對值小代表正後方。
    /// </summary>
    public bool TryGetRearHazard(out float distance, out float lateralOffset)
    {
        distance = float.MaxValue;
        lateralOffset = 0f;
        if (playerTransform == null) return false;

        bool found = false;
        foreach (var h in active)
        {
            if (h.source == null) continue;
            // Silent 類（例如側邊切入）不參與後照鏡警示：它是從前方/側前方出現的，
            // 若讓它點亮鏡子，玩家會被引導看錯方向，也會訓練出忽略鏡子警示的習慣。
            if (h.style != HazardStyle.Full) continue;

            Vector3 local = playerTransform.InverseTransformPoint(h.source.position);
            if (local.z >= 0f) continue;                 // 不在後方就跳過

            float d = local.magnitude;
            if (d < distance)
            {
                distance = d;
                lateralOffset = local.x;
                found = true;
            }
        }
        return found;
    }

    // ── 反應偵測與升級 ─────────────────────────────────────────

    private void Update()
    {
        if (active.Count == 0 || playerTransform == null) return;

        float speedNow = CurrentSpeedKmH;
        float headYawNow = CurrentHeadYaw;

        foreach (var h in active)
        {
            if (!h.record.reacted)
            {
                string channel = DetectReaction(h, speedNow, headYawNow);
                if (channel != null)
                {
                    h.record.reacted = true;
                    h.record.reactionDelay = Time.time - h.record.startTime;
                    h.record.reactionChannel = channel;

                    if (logReactions)
                        Debug.Log($"<color=lime>✔ [危險感知] 玩家察覺「{h.record.name}」— 延遲 {h.record.reactionDelay:F1} 秒（{channel}）</color>");
                }
                else if (h.style == HazardStyle.Full)
                {
                    // 還沒反應 → 依經過時間提高提示強度。
                    // Silent 類不升級：側邊切入整個事件只有 2.5 秒，沒有時間逐步加強，
                    // 而且「升級」的唯一手段是讓車切得更狠，那是懲罰而不是教學。
                    int wanted = Mathf.FloorToInt((Time.time - h.record.startTime) / Mathf.Max(0.1f, escalateAfterSeconds));
                    wanted = Mathf.Clamp(wanted, 0, maxEscalationLevel);
                    if (wanted > h.escalation)
                    {
                        h.escalation = wanted;
                        if (logReactions)
                            Debug.LogWarning($"🔺 [危險感知] 玩家仍未察覺「{h.record.name}」，提示強度升到 L{wanted}");
                    }
                }
            }
        }
    }

    /// <summary>回傳觸發反應的管道名稱，沒反應則回 null。</summary>
    private string DetectReaction(ActiveHazard h, float speedNow, float headYawNow)
    {
        if (h.baselineSpeedKmH - speedNow >= reactionSpeedDropKmH)
            return "減速";

        Vector3 offset = playerTransform.InverseTransformPoint(h.baselinePos);
        if (Mathf.Abs(offset.x) >= reactionLateralShift)
            return "閃避";

        if (playerHead != null)
        {
            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(h.baselineHeadYaw, headYawNow));
            if (yawDelta >= reactionHeadTurnAngle)
                return "轉頭查看";
        }

        return null;
    }
}
