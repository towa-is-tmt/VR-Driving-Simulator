using UnityEngine;

/// <summary>
/// 🚸 玩家超速偵測
///
/// 【怎麼知道玩家在哪條路上】
///   玩家不跑 AI 路網,所以要反推。做法比照 PlayerWrongWayDetector:
///   開場把兩套路網(白車 TrafficPathNode + 灰車 WaypointNode)的座標與速限
///   建成陣列快取,之後每 checkInterval 秒找一次最近節點,讀它的 speedLimitKmH。
///   2000 顆節點的距離比較在 5Hz 下完全不是負擔,而且省掉維護第二套觸發盒。
///
/// 【為什麼節點預設是 0 而不是 50】
///   0 代表「沒特別標,吃全域預設」。市區大部分路段速限相同,
///   只有學校/巷弄/快速道路才需要標 —— 要動的節點從 2030 顆降到一兩百顆。
///
/// 【提示走哪裡】
///   HazardAwarenessTracker.Instance.ShowMessage(),跟闖紅燈、逆向、跟車距離同一個出口,
///   訊息樣式才會一致,不會多長出一套 UI。
/// </summary>
public class PlayerSpeedLimitMonitor : MonoBehaviour
{
    [Header("速限來源")]
    [Tooltip("節點沒特別標(speedLimitKmH = 0)時使用的速限。市區一般道路通常是 50")]
    public int defaultSpeedLimitKmH = 50;
    [Tooltip("納入白車 TrafficPathNode 路網(752 顆)")]
    public bool useWhiteNetwork = true;
    // ⚠️ 預設關閉,而且不建議打開。兩套路網疊在同一條路上(94% 的灰車節點離最近的
    //    白車節點不到 10m),兩套都讀的話,「最近節點」會在你標過的白車節點與
    //    沒標的灰車節點之間來回跳 —— 速限就在 30 和 50 之間閃。
    //    要開的話,同一條路的灰車節點也必須標上一樣的值。
    [Tooltip("納入灰車 WaypointNode 路網(1278 顆)。★ 開啟的話灰車節點也必須標上相同速限,否則速限會在路上跳動")]
    public bool useGrayNetwork = false;
    [Tooltip("離最近節點超過這個距離就視為「不在路網上」(停車場、開出地圖),維持目前速限不亂跳")]
    public float maxNodeDistance = 30f;

    [Header("判定條件")]
    // 真實舉發有容差(台灣是速限 +10 才開單)。沒有容差的話,
    // 玩家在速限邊緣正常行駛會被訊息洗版,反而學不到東西。
    //
    // ⚠️ 但「+10 才開單」是**舉發**容差,不是**提醒**容差 —— 兩者混用是這裡原本的問題。
    //    真實世界的提醒都比 10 早得多:導航 App 到速限就變色、歐盟 ISA 基本上就在速限本身、
    //    教練一碰到速限就出聲,沒有人等你超 10 才講話。
    //    而且用方向盤全踩時,從速限爬到 +10 只要約 1 秒,再等 sustainSeconds 1.5 秒,
    //    訊息跳出來時人已經在 +25 —— 小地圖上那個路段都快開完了。
    //    所以拆成兩條線:提醒早(教學)、計分留在 10(對齊真實舉發標準)。
    [Tooltip("超過速限多少就「提醒」(km/h)。這是訊息與音效的門檻,要早。\n" +
             "5 = 限速 50 時超過 55 就提醒")]
    public float warnToleranceKmH = 5f;
    [Tooltip("超過速限多少才「計為一次違規」(km/h),供考照評分用。\n" +
             "維持 10 對齊台灣「未達 10 公里不舉發」。提醒過但沒超過這個值 → 只提醒,不計分")]
    public float toleranceKmH = 10f;

    // 固定的持續時間對「慢慢漂到 +6」和「一腳灌到 +25」一視同仁,但這兩件事該有不同的
    // 反應速度 —— 教練也是這樣:小漂移先看一下,你一腳踩下去他馬上出聲。
    // 改成隨超速量內插:超得越多、要求的持續時間越短。
    [Tooltip("剛好超過提醒門檻時,要連續超速這麼久才提醒(秒)。避免下坡滑一下就跳訊息")]
    public float sustainSeconds = 1.5f;
    [Tooltip("超速量大到下面那個值時,只要連續這麼久就提醒(秒)。明顯是刻意的,不需要再觀察")]
    public float minSustainSeconds = 0.3f;
    [Tooltip("超速多少(km/h)時持續時間降到最短。提醒門檻 → 這個值之間線性內插。\n" +
             "設成小於等於提醒門檻 = 關掉遞減,一律用上面的 Sustain Seconds(舊行為)")]
    public float sustainFullAtOverKmH = 20f;
    [Tooltip("兩次提醒之間的最短間隔(秒)。比照闖紅燈偵測的 6 秒")]
    public float cooldownSeconds = 6f;
    // 節點是分車道的,路口附近兩條不同速限的路會讓「最近節點」來回跳。
    // 要求新速限連續成立一段時間才採用,數字才不會閃。
    [Tooltip("換到新速限要連續成立這麼久才生效(秒)。防止路口附近速限跳來跳去")]
    public float limitSwitchHoldSeconds = 0.5f;
    [Tooltip("多久重新找一次最近節點(秒)。0.2 秒足夠,不必每幀")]
    public float checkInterval = 0.2f;

    [Header("警告顯示")]
    [TextArea(2, 3)]
    [Tooltip("{0} 會被換成目前速限。留空就不顯示文字,只累計次數")]
    public string speedingMessage = "您已超速！限速 {0} 公里" + " " + "請減速慢行";
    public Color speedingColor = new Color(1f, 0.62f, 0.05f);
    public float displaySeconds = 4f;

    [Header("警告音效")]
    public AudioClip speedingSound;
    [Tooltip("留空會自動新增一個 2D 音源")]
    public AudioSource speedingAudioSource;
    [Range(0f, 2f)]
    public float speedingVolume = 0.9f;

    // 嗶聲反應快但只說得出「有事」；語音說得出「該怎麼做」但要 2.9 秒，每次都播會很吵。
    // 所以第一次用語音教，之後用嗶聲提醒，連續不改再用語音重講一次。
    [Tooltip("超速語音（例如 聲音/語音提示/07_您已超速請減速慢行.wav）。\n" +
             "留空＝一律用上面的嗶聲")]
    public AudioClip speedingVoice;
    [Tooltip("語音音量。要蓋過引擎聲，通常要比嗶聲大。PlayOneShot 沒有 1 的上限")]
    [Range(0f, 3f)] public float speedingVoiceVolume = 1.6f;
    [Tooltip("連續被提醒幾次之後再用語音講一次。0 = 只有第一次用語音，之後永遠嗶聲")]
    public int revoiceEveryNWarnings = 3;
    [Tooltip("停止超速多久之後，下一次重新算「第一次」（秒）。\n" +
             "太短會讓每個路口都重播語音，太長則整場只會聽到一次")]
    public float consecutiveResetSeconds = 20f;

    // 連續被提醒的次數；不再超速並過了 consecutiveResetSeconds 就歸零
    private int consecutiveWarnings = 0;
    private float lastWarnTime = -999f;

    [Header("診斷")]
    public bool logSpeeding = true;
    [Tooltip("每次速限改變時印出來,用來確認節點有沒有標對")]
    public bool debugLogLimit = false;

    /// <summary>目前所在路段的速限(km/h)。找不到路網時維持上一次的值。</summary>
    public int CurrentSpeedLimitKmH { get; private set; }
    /// <summary>
    /// 累計超速次數,供考照評分讀取(比照 RedLightViolationDetector.ViolationCount)。
    /// 用的是 toleranceKmH(舉發容差),不是提醒容差 —— 只被提醒不會被記一次。
    /// </summary>
    public int SpeedingCount { get; private set; }
    /// <summary>此刻是否超過「提醒」容差 warnToleranceKmH(未含持續時間判定)</summary>
    public bool IsSpeedingNow { get; private set; }

    private Rigidbody rb;
    private Vector3[] nodePos;      // 節點座標(y 已歸零,比距離只看平面)
    private int[] nodeLimit;        // 對應的速限,0 代表吃全域預設

    private float nextCheckTime = 0f;
    private int pendingLimit = -1;      // 正在等待「連續成立」的新速限
    private float pendingSince = -1f;
    private float overSince = -1f;      // 開始連續超速的時間點
    private float nextWarnTime = -1f;

    private void Start()
    {
        // 位置要從真正會動的物件量。玩家車的階層是 playerCAR → vehicle ONLY LOD0(Rigidbody 在這),
        // 掛在外層的話 transform.position 永遠不動 —— 跟 PlayerReverseBroadcaster 同一個坑。
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponentInChildren<Rigidbody>();

        CurrentSpeedLimitKmH = defaultSpeedLimitKmH;
        BuildNodeCache();

        string nets = (useWhiteNetwork ? "白車" : "") + (useGrayNetwork ? (useWhiteNetwork ? "+灰車" : "灰車") : "");
        Debug.Log($"<color=orange>🚸 [超速偵測] 已啟動｜路網={(string.IsNullOrEmpty(nets) ? "★都沒開" : nets)}" +
                  $"｜快取 {(nodePos != null ? nodePos.Length : 0)} 顆節點" +
                  $"｜其中有標速限的 {CountTagged()} 顆｜全域預設 {defaultSpeedLimitKmH} km/h</color>", this);
    }

    /// <summary>開場把兩套路網的座標與速限抓成陣列。之後只比距離,不再做場景查詢。</summary>
    private void BuildNodeCache()
    {
        var pos = new System.Collections.Generic.List<Vector3>();
        var lim = new System.Collections.Generic.List<int>();

        if (useWhiteNetwork)
            foreach (var n in FindObjectsOfType<TrafficPathNode>())
            {
                if (n == null) continue;
                Vector3 p = n.transform.position; p.y = 0f;
                pos.Add(p); lim.Add(n.speedLimitKmH);
            }

        if (useGrayNetwork)
            foreach (var n in FindObjectsOfType<WaypointNode>())
            {
                if (n == null) continue;
                Vector3 p = n.transform.position; p.y = 0f;
                pos.Add(p); lim.Add(n.speedLimitKmH);
            }

        nodePos = pos.ToArray();
        nodeLimit = lim.ToArray();

        if (nodePos.Length == 0)
            Debug.LogWarning("[超速偵測] 一顆路網節點都沒抓到 —— 兩個路網開關是不是都關掉了?", this);
    }

    private int CountTagged()
    {
        if (nodeLimit == null) return 0;
        int c = 0;
        for (int i = 0; i < nodeLimit.Length; i++) if (nodeLimit[i] > 0) c++;
        return c;
    }

    private void Update()
    {
        if (rb == null || nodePos == null || nodePos.Length == 0) return;

        if (Time.time >= nextCheckTime)
        {
            nextCheckTime = Time.time + Mathf.Max(0.02f, checkInterval);
            UpdateCurrentLimit();
        }

        EvaluateSpeeding();
    }

    /// <summary>找最近節點決定速限,並套用「連續成立才換」的黏滯。</summary>
    private void UpdateCurrentLimit()
    {
        Vector3 me = rb.position; me.y = 0f;

        float bestSqr = float.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < nodePos.Length; i++)
        {
            float sqr = (nodePos[i] - me).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; bestIdx = i; }
        }

        // 離路網太遠(停車場、開出地圖)→ 維持目前速限,不亂跳
        if (bestIdx < 0 || bestSqr > maxNodeDistance * maxNodeDistance) return;

        int raw = nodeLimit[bestIdx];
        int wanted = raw > 0 ? raw : defaultSpeedLimitKmH;

        if (wanted == CurrentSpeedLimitKmH) { pendingLimit = -1; return; }

        // 新值要連續成立 limitSwitchHoldSeconds 才採用
        if (wanted != pendingLimit) { pendingLimit = wanted; pendingSince = Time.time; return; }
        if (Time.time - pendingSince < limitSwitchHoldSeconds) return;

        int old = CurrentSpeedLimitKmH;
        CurrentSpeedLimitKmH = wanted;
        pendingLimit = -1;
        overSince = -1f;   // 換路段就重算超速計時,不把上一段累積的秒數帶過來

        if (debugLogLimit)
            Debug.Log($"<color=cyan>🚸 [速限] {old} → {CurrentSpeedLimitKmH} km/h" +
                      $"(最近節點 {Mathf.Sqrt(bestSqr):F1}m,節點標記值 {raw}{(raw > 0 ? "" : " → 用全域預設")})</color>", this);
    }

    /// <summary>
    /// 超速判定:超過「提醒」門檻且持續夠久才提醒,並受冷卻時間限制。
    /// 持續時間隨超速量遞減 —— 超得越多反應越快,理由寫在 sustainSeconds 附近。
    /// 計分是另一條線:提醒的當下有沒有超過 toleranceKmH 才決定計不計。
    /// </summary>
    private void EvaluateSpeeding()
    {
        float speedKmH = rb.velocity.magnitude * 3.6f;
        float overKmH  = speedKmH - CurrentSpeedLimitKmH;

        if (overKmH <= warnToleranceKmH)
        {
            overSince = -1f;
            IsSpeedingNow = false;

            // 乖乖開了一段時間 → 下一次重新算「第一次」，語音會再出現。
            // 不歸零的話玩家在別的路段又超速時只會聽到嗶聲，等於沒被教到。
            if (consecutiveWarnings > 0 && Time.time - lastWarnTime > consecutiveResetSeconds)
                consecutiveWarnings = 0;
            return;
        }

        IsSpeedingNow = true;
        if (overSince < 0f) overSince = Time.time;
        if (Time.time - overSince < RequiredSustainSeconds(overKmH)) return;
        if (Time.time < nextWarnTime) return;

        nextWarnTime = Time.time + Mathf.Max(0.5f, cooldownSeconds);

        // 🚸 提醒一定給,計分只在超過舉發容差時才記 ——
        //    +6 一直開會被一直提醒但不會被記違規,這才對得上真實世界。
        bool counted = overKmH > toleranceKmH;
        if (counted) SpeedingCount++;

        Warn(speedKmH, overKmH, counted);
    }

    /// <summary>
    /// 這個超速量需要連續多久才提醒。
    /// 提醒門檻處 = sustainSeconds,超到 sustainFullAtOverKmH = minSustainSeconds,中間線性內插。
    /// </summary>
    private float RequiredSustainSeconds(float overKmH)
    {
        if (sustainFullAtOverKmH <= warnToleranceKmH) return sustainSeconds;   // 遞減關閉 = 舊行為
        float t = Mathf.InverseLerp(warnToleranceKmH, sustainFullAtOverKmH, overKmH);
        return Mathf.Lerp(sustainSeconds, minSustainSeconds, t);
    }

    private void Warn(float speedKmH, float overKmH, bool counted)
    {
        if (!string.IsNullOrEmpty(speedingMessage) && HazardAwarenessTracker.Instance != null)
        {
            string msg = speedingMessage.Contains("{0}")
                       ? string.Format(speedingMessage, CurrentSpeedLimitKmH)
                       : speedingMessage;
            HazardAwarenessTracker.Instance.ShowMessage(msg, speedingColor, displaySeconds);
        }

        // 🔊 語音與嗶聲分工：
        //    第一次超速播語音（教他該做什麼），之後播嗶聲（提醒就好，不用再教一次）。
        //    但如果他一直超速不改，每隔 revoiceEveryNWarnings 次再用語音講一次 ——
        //    連續被嗶卻沒改，代表提醒沒被當一回事，這時要把「該怎麼做」重新講清楚。
        //    語音 2.9 秒，每次都播會很吵；只嗶又教不到東西，所以要分工。
        consecutiveWarnings++;
        bool useVoice = speedingVoice != null
                     && (consecutiveWarnings == 1
                         || (revoiceEveryNWarnings > 0
                             && consecutiveWarnings % revoiceEveryNWarnings == 0));

        AudioClip clip = useVoice ? speedingVoice : speedingSound;

        if (clip != null)
        {
            if (speedingAudioSource == null)
            {
                speedingAudioSource = gameObject.AddComponent<AudioSource>();
                speedingAudioSource.playOnAwake = false;
                speedingAudioSource.loop = false;
                speedingAudioSource.spatialBlend = 0f;   // 系統警告,2D
                speedingAudioSource.dopplerLevel = 0f;
            }
            speedingAudioSource.PlayOneShot(clip, useVoice ? speedingVoiceVolume : speedingVolume);
        }

        lastWarnTime = Time.time;

        if (logSpeeding)
            Debug.LogWarning($"<color=orange>🚸 [超速] 時速 {speedKmH:F0} / 速限 {CurrentSpeedLimitKmH}" +
                             $"（超 {overKmH:F0}，持續門檻 {RequiredSustainSeconds(overKmH):F1}s）｜" +
                             $"{(counted ? $"計為第 {SpeedingCount} 次違規" : $"未達計分容差 {toleranceKmH:F0}，只提醒不計分")}｜" +
                             $"連續第 {consecutiveWarnings} 次提醒，{(useVoice ? "播語音" : "播嗶聲")}</color>", this);
    }
}
