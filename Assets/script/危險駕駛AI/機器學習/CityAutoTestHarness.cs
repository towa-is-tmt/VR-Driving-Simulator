using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 🤖 城市自動測試：讓「虛擬學員」自動駕駛玩家車，在真實城市裡大量測試鬼切 AI（v6 與規則式對照）
///
/// 為什麼需要：真人城市實測一次要開好幾分鐘才遇到一次鬼切，樣本累積太慢。
/// 做法：
///   1. 從「城市路網訓練樣本」（388 個已確認能觸發鬼切的城市路段）隨機挑一個
///   2. 把玩家車瞬移到該路段前方約 35m，給定初速，由虛擬學員自動駕駛（真實車輛物理，透過 CarPhysicsController 的踏板／方向盤輸入）
///   3. 到達路段、車速穩定後，呼叫觸發器 TryStart 產生鬼切（跟遊戲中完全同一套流程）
///   4. 同一個路段、同樣的車速、學員風格與難度，v6 與規則式「各測一次」（成對比較，順序隨機）
///   5. 每次結果寫一行到 危險駕駛AI_城市自動測試_v2.csv
///      v2 新增碰撞診斷：碰撞瞬間的前後距離、側向距離、鬼切車靠近速度，並自動分類「側面擦撞／前後碰撞」；
///      另記錄「並行時最小側距」（兩車前後重疊時最靠近的側向距離），用來比較安全防護調整前後的差異
///      v2 修正：v1 把 TrafficLight 圖層當成紅綠燈排除，但這個專案的 NPC 車（灰車、機車、白車）都在這個圖層，
///      導致虛擬學員完全看不到 NPC、直接撞上去。改成「沒有剛體的才排除」，並記錄學員有沒有撞到其他車
///   v3 新增碰撞器診斷：開始時列出玩家車（以及每種鬼切車第一次出現時）身上所有碰撞器的實際範圍；
///      每次碰撞記下是哪兩個碰撞器碰在一起、接觸點在車身的哪個位置，用來找出「車身外的隱形碰撞器」
///   v4：v3 實測發現碰撞器尺寸正常，但接觸點在兩台車身外約 1m —— 是鬼切車的「推測式連續碰撞偵測」
///      在兩車接近時提早回報的假接觸。演員車改為忽略這類接觸，這裡記錄被忽略的次數與最近距離做驗證
///
/// 虛擬學員（跟訓練場的虛擬玩家同一套模型）：
///   IDM 跟車；察覺鬼切車往自己車道靠過來後，經過「反應時間」才把它當前車煞車
///   風格：普通（反應 0.5～1.3 秒）、提早煞車（反應後先重煞一下）、遲鈍（反應 1.8～3 秒、煞車力弱）
///
/// 測試期間會暫時：關閉觸發器自動觸發、關閉自適應難度（改用指定等級，且不寫入學員紀錄）、
/// 關閉鍵盤駕駛、停用指定的元件（預設：卡住救援）。停止測試或離開 Play 時全部還原。
///
/// 操作：Tools > 危險駕駛AI > 城市自動測試：加入場景（在 cc.unity 執行）→ Play → 按 F11 開始／停止
/// </summary>
[DefaultExecutionOrder(-200)]   // 要比 CarPhysicsController 早執行，踏板輸入才會在同一幀生效
public class CityAutoTestHarness : MonoBehaviour
{
    public enum 學員風格 { 普通, 提早煞車, 遲鈍 }
    private enum 階段 { 閒置, 加速中, 演出中, 等待結算, 間隔 }

    [Header("🔗 參考（留空會自動尋找）")]
    public AggressiveDriverEventTrigger trigger;
    public CityRoadSamples roadSamples;
    public AdaptiveDifficultyBandit adaptiveDifficulty;
    public CarPhysicsController playerCar;

    [Header("▶ 執行")]
    [Tooltip("按 Play 後自動開始測試")]
    public bool autoStartOnPlay = false;
    public KeyCode toggleKey = KeyCode.F11;
    [Tooltip("遊戲倍速。物理步長不變，只是每秒算更多步；電腦跟不上時畫面會變慢，但結果一樣準")]
    [Range(1f, 4f)] public float timeScale = 1f;
    [Tooltip("跑幾組配對後自動停止（0 = 不限，按 F11 停止）")]
    public int maxPairs = 0;

    [Header("🧪 實驗設計")]
    [Tooltip("同一個路段、同樣條件，v6 與規則式各測一次（順序隨機）。關閉 = 只測 v6")]
    public bool pairedComparison = true;
    [Tooltip("虛擬學員的巡航時速範圍（km/h）")]
    public Vector2 speedRangeKmH = new Vector2(30f, 70f);
    [Tooltip("每組隨機挑 1～5 級難度（關閉 = 固定用下面的等級）")]
    public bool randomLevel = true;
    [Range(1, 5)] public int fixedLevel = 3;

    [Header("🎭 虛擬學員風格機率（會自動正規化）")]
    [Min(0f)] public float chanceNormal = 0.5f;
    [Min(0f)] public float chanceEarlyBrake = 0.25f;
    [Min(0f)] public float chanceSluggish = 0.25f;
    [Tooltip("察覺到開始反應的時間（秒）")]
    public Vector2 reactionRange = new Vector2(0.5f, 1.3f);
    [Tooltip("最大煞車力（m/s²）。實際能煞多重還要看車輛物理")]
    public Vector2 maxBrakeRange = new Vector2(6f, 10f);
    [Tooltip("提早煞車：反應後先踩多重（m/s²）、多久（秒）")]
    public Vector2 earlyBrakeDecel = new Vector2(3f, 7f);
    public Vector2 earlyBrakeSeconds = new Vector2(0.6f, 2f);

    [Header("🚗 自動駕駛")]
    [Tooltip("瞬移到觸發點前方多遠（m），留時間讓車速穩定")]
    public float runUpDistance = 35f;
    [Tooltip("觸發點後方至少還要有多長的路（m）")]
    public float minRoadAhead = 140f;
    [Tooltip("一次演出最長幾秒，超過就結算")]
    public float episodeTimeout = 60f;
    [Tooltip("每次結束後等幾秒再開下一次")]
    public float gapBetweenEpisodes = 1f;
    [Tooltip("車輛軸距（m），給轉向計算用")]
    public float wheelBase = 2.8f;

    [Header("🚫 測試期間暫時停用的元件（類別名稱）")]
    public string[] disableComponentTypes = { "PlayerStuckRescue" };

    [Header("📊 狀態（唯讀）")]
    [TextArea(2, 6)] public string status = "閒置";

    // ── 測試設定 ──
    private class 回合設定
    {
        public int pairId, sampleIndex, level, retries;
        public float cruise, reaction, maxBrake, earlyDecel, earlyDur, timeHeadway, maxAccel;
        public 學員風格 style;
        public bool useRL, isSecond;
        public 回合設定 Clone() { return (回合設定)MemberwiseClone(); }
    }

    private bool running;
    private 階段 stage = 階段.閒置;
    private float stageTime;
    private readonly Queue<回合設定> queue = new Queue<回合設定>();
    private 回合設定 cur;
    private int pairCounter, completedPairs;
    private readonly HashSet<int> badSamples = new HashSet<int>();

    // ── 場景參考 ──
    private Rigidbody rb;
    private Transform player;
    private FieldInfo fGas, fBrake, fSteer;
    private float playerHalfLen = 2.3f, playerHalfWid = 1.0f, groundOffset = 0.5f;
    private LaneNetwork net;
    private LanePath path;
    private float triggerS;

    // ── 還原用 ──
    private bool savedAutoTrigger, savedBypass, savedAdaptive, savedCsv, savedKeyboard;
    private float savedRlChance, savedTimeScale;
    private AggressiveDriverSettings savedSettings;
    private readonly List<Behaviour> disabledBehaviours = new List<Behaviour>();

    // ── 虛擬學員狀態 ──
    private IDMParams egoModel;
    private float sE, dE, vE, vPrev, accelSm, vRef, brakeFullDecel = 9f;
    private float gasCmd, brakeCmd, steerCmd;
    private float threatTime = -1f, styleT0 = -1f, actorLeaderTimer;
    private float otherScanTimer, otherGap = float.PositiveInfinity, otherLeaderV;
    private readonly Dictionary<int, Vector2> otherTrack = new Dictionary<int, Vector2>();   // instanceID → (s, 時間)

    // ── 演出紀錄 ──
    private AggressiveDriverActor actor;
    private float actorHalfLen = 2.8f, actorHalfWid = 1.4f, adjSide;
    private bool entered, actorHitPlayer, everFallback, outcomeReceived;
    private float entryGap, entryRearGap, maxDecelAfterEntry, eventStart;
    private string lastPhase = "";
    private DangerOutcome outcome;

    // ── 碰撞診斷 ──
    private float lastRelS, lastLatGap, lastApproach, lastSpeedDiff, lastDE, prevLatA;
    private bool prevLatAValid, lastRLControlled;
    private float minSideGapAlongside;
    private bool hitSnapTaken;
    private float hitRelS, hitLatGap, hitRearGap, hitApproach, hitSpeedDiff, hitDE;
    private bool hitRL;
    private string hitType = "";
    private string hitContact = "";
    private int specCount;
    private float specMinSep = float.PositiveInfinity;
    private readonly HashSet<string> dumpedActors = new HashSet<string>();

    // ── 學員撞到其他車（NPC）──
    private bool hitNpc;
    private string hitNpcName = "";
    private float hitNpcTime = -1f;

    // ── 即時統計 ──
    private class 統計 { public int n, entered, hit; public float errSum; public int errN; }
    private readonly 統計 statRL = new 統計(), statRule = new 統計();
    private int rejectedCount;

    // v4：演員車改為忽略「推測式接觸」（沒有真的碰到），另開新檔；多記錄推測式接觸的次數與最近距離
    private string CsvPath { get { return Path.Combine(Application.persistentDataPath, "危險駕駛AI_城市自動測試_v4.csv"); } }

    // ====================================================================
    //  開始／停止
    // ====================================================================

    private void Start()
    {
        if (autoStartOnPlay) StartTest();
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (running) StopTest("按下停止鍵"); else StartTest();
        }
        if (!running) return;

        // 踏板與方向盤：每幀寫進 CarPhysicsController 的「方向盤輸入」欄位（等同真人踩踏板）
        fGas.SetValue(playerCar, gasCmd);
        fBrake.SetValue(playerCar, brakeCmd);
        fSteer.SetValue(playerCar, steerCmd);
        // 方向盤輸入 ≤ 0.01 時 CarPhysicsController 會改用鍵盤值；測試期間鍵盤已關閉、鍵盤值會回到 0，結果一樣
    }

    [ContextMenu("開始測試")]
    public void StartTest()
    {
        if (running) return;
        if (!Application.isPlaying) { Debug.LogWarning("[城市自動測試] 請先按 Play"); return; }
        if (!ResolveReferences(out string why))
        {
            Debug.LogError("[城市自動測試] 無法開始：" + why, this);
            status = "無法開始：" + why;
            return;
        }

        // 暫存並覆寫設定
        savedAutoTrigger = trigger.autoTrigger; trigger.autoTrigger = false;
        savedBypass = trigger.hotkeyBypassDirector; trigger.hotkeyBypassDirector = true;
        savedRlChance = trigger.rlModeChance;
        savedSettings = trigger.settings;
        trigger.settings = savedSettings.Clone();
        if (adaptiveDifficulty != null)
        {
            savedAdaptive = adaptiveDifficulty.enableAdaptive; adaptiveDifficulty.enableAdaptive = false;
            savedCsv = adaptiveDifficulty.writeCsvLog; adaptiveDifficulty.writeCsvLog = false;
        }
        savedKeyboard = playerCar.enableKeyboardControl; playerCar.enableKeyboardControl = false;
        savedTimeScale = Time.timeScale; Time.timeScale = timeScale;
        DisableListedComponents();

        if (playerCar.gearController != null) playerCar.gearController.currentGear = CarGearController.AutomaticGear.D;

        net = LaneNetwork.Get(trigger.grayNarrowMaskRadius, false);
        playerHalfLen = Mathf.Clamp(AggressiveDriverActor.MeasureHalfLength(player.gameObject, 2.3f), 1.5f, 3.5f);
        playerHalfWid = Mathf.Clamp(AggressiveDriverActor.MeasureHalfWidth(player.gameObject, 1.0f), 0.7f, 1.6f);
        groundOffset = MeasureGroundOffset();
        dumpedActors.Clear();
        DumpColliders(rb.transform, "玩家車");
        if (trigger.player != null && trigger.player != rb.transform)
            Debug.Log($"[城市自動測試] 注意：觸發器的 Player（{trigger.player.name}）跟玩家車剛體所在物件（{rb.transform.name}）不是同一個物件，" +
                      $"兩者相距 {Vector3.Distance(trigger.player.position, rb.position):F2}m", trigger.player);

        running = true;
        queue.Clear();
        completedPairs = 0;
        badSamples.Clear();
        SetStage(階段.間隔);
        Debug.Log($"<color=#66ffcc>🤖 [城市自動測試] 開始｜樣本 {roadSamples.samples.Count} 個｜" +
                  $"{(pairedComparison ? "v6 與規則式成對測試" : "只測 v6")}｜模型 {ModelName}｜倍速 {timeScale:F1}｜" +
                  $"玩家車半長 {playerHalfLen:F1}m、半寬 {playerHalfWid:F1}m、離地 {groundOffset:F2}m｜" +
                  $"🛡 橫向防護：超車後車尾距 {trigger.settings.rlLateralClearGap:F1}m、車身間隔 {trigger.settings.rlSideMargin:F1}m" +
                  $"｜結果寫入 {CsvPath}</color>", this);
    }

    [ContextMenu("停止測試")]
    public void StopTestMenu() { StopTest("手動停止"); }

    public void StopTest(string why)
    {
        if (!running) return;
        running = false;
        SetStage(階段.閒置);

        gasCmd = 0f; brakeCmd = 1f; steerCmd = 0f;
        if (fGas != null && playerCar != null)
        {
            fGas.SetValue(playerCar, 0f);
            fBrake.SetValue(playerCar, 0f);
            fSteer.SetValue(playerCar, 0f);
        }
        if (actor != null) Destroy(actor.gameObject);

        if (trigger != null)
        {
            trigger.autoTrigger = savedAutoTrigger;
            trigger.hotkeyBypassDirector = savedBypass;
            trigger.rlModeChance = savedRlChance;
            if (savedSettings != null) trigger.settings = savedSettings;
        }
        if (adaptiveDifficulty != null)
        {
            adaptiveDifficulty.enableAdaptive = savedAdaptive;
            adaptiveDifficulty.writeCsvLog = savedCsv;
        }
        if (playerCar != null) playerCar.enableKeyboardControl = savedKeyboard;
        Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
        foreach (var b in disabledBehaviours) if (b != null) b.enabled = true;
        disabledBehaviours.Clear();

        status = $"已停止（{why}）\n" + Summary();
        Debug.Log($"<color=#66ffcc>🤖 [城市自動測試] 停止（{why}）\n{Summary()}</color>", this);
    }

    private void OnDisable() { if (running) StopTest("元件停用"); }

    // ====================================================================
    //  主迴圈（物理步）
    // ====================================================================

    private void FixedUpdate()
    {
        if (!running) return;
        float dt = Time.fixedDeltaTime;
        stageTime += dt;

        if (stage == 階段.間隔)
        {
            if (path != null && cur != null) Drive(dt);
            else { gasCmd = 0f; brakeCmd = 0.3f; steerCmd = 0f; }
            if (stageTime >= gapBetweenEpisodes) BeginEpisode();
            return;
        }

        if (path == null) { SetStage(階段.間隔); return; }
        Drive(dt);
        CheckNpcContact();

        switch (stage)
        {
            case 階段.加速中:
                if (hitNpc) { AbortEpisode($"助跑時學員撞到其他車（{hitNpcName}），這次不算"); break; }
                bool speedOk = Mathf.Abs(vE - cur.cruise) < 2f;
                if (sE >= triggerS - 3f && stageTime > 2.5f && (speedOk || sE > triggerS + 15f)) FireGhost();
                else if (stageTime > 25f) AbortEpisode("加速階段逾時（可能卡住或撞到東西）");
                break;

            case 階段.演出中:
                TrackActor();
                if (actor == null) SetStage(階段.等待結算);
                else if (Time.time - eventStart > episodeTimeout) { Destroy(actor.gameObject); SetStage(階段.等待結算); }
                break;

            case 階段.等待結算:
                if (outcomeReceived || stageTime > 0.6f) FinishEpisode();
                break;
        }
    }

    // ====================================================================
    //  回合
    // ====================================================================

    private void BeginEpisode()
    {
        if (queue.Count == 0)
        {
            if (maxPairs > 0 && completedPairs >= maxPairs) { StopTest($"已完成 {maxPairs} 組"); return; }
            EnqueueNewPair();
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            回合設定 spec = queue.Peek();
            if (PrepareRoad(spec)) { queue.Dequeue(); StartRun(spec); return; }

            if (!spec.isSecond)
            {
                // 配對的第一次就無法開始 → 這個路段先跳過，換一組新的
                badSamples.Add(spec.sampleIndex);
                queue.Clear();
                EnqueueNewPair();
            }
            else
            {
                // 配對的第二次：路段可能暫時被 NPC 車佔用，等一下再試；試太多次就放棄這一組
                spec.retries++;
                if (spec.retries > 5)
                {
                    Debug.LogWarning($"[城市自動測試] 第 {spec.pairId} 組的第二次一直無法開始（起點被佔用），這組不完整", this);
                    queue.Clear();
                }
                break;
            }
        }
        SetStage(階段.間隔);
    }

    private void EnqueueNewPair()
    {
        pairCounter++;
        int idx = PickSample();
        float total = chanceNormal + chanceEarlyBrake + chanceSluggish;
        float r = UnityEngine.Random.value * Mathf.Max(total, 0.0001f);
        學員風格 style = r < chanceNormal ? 學員風格.普通 : (r < chanceNormal + chanceEarlyBrake ? 學員風格.提早煞車 : 學員風格.遲鈍);

        var a = new 回合設定
        {
            pairId = pairCounter, sampleIndex = idx, style = style,
            level = randomLevel ? UnityEngine.Random.Range(1, 6) : fixedLevel,
            cruise = UnityEngine.Random.Range(speedRangeKmH.x, speedRangeKmH.y) / 3.6f,
            reaction = UnityEngine.Random.Range(reactionRange.x, reactionRange.y),
            maxBrake = UnityEngine.Random.Range(maxBrakeRange.x, maxBrakeRange.y),
            earlyDecel = UnityEngine.Random.Range(earlyBrakeDecel.x, earlyBrakeDecel.y),
            earlyDur = UnityEngine.Random.Range(earlyBrakeSeconds.x, earlyBrakeSeconds.y),
            timeHeadway = UnityEngine.Random.Range(0.9f, 1.6f),
            maxAccel = UnityEngine.Random.Range(1.2f, 2.5f),
        };
        if (style == 學員風格.遲鈍)
        {
            a.reaction = UnityEngine.Random.Range(1.8f, 3f);
            a.maxBrake = UnityEngine.Random.Range(4f, 6f);
        }

        // 成對比較：先跑哪一個隨機決定，避免「永遠第二個跑」造成偏差
        a.useRL = !pairedComparison || UnityEngine.Random.value < 0.5f;
        queue.Enqueue(a);
        if (pairedComparison)
        {
            回合設定 b = a.Clone();
            b.useRL = !a.useRL;
            b.isSecond = true;
            queue.Enqueue(b);
        }
    }

    private int PickSample()
    {
        int n = roadSamples.samples.Count;
        for (int i = 0; i < 50; i++)
        {
            int k = UnityEngine.Random.Range(0, n);
            if (!badSamples.Contains(k)) return k;
        }
        return UnityEngine.Random.Range(0, n);
    }

    /// <summary>找到樣本所在的車道、建立路徑、確認起點沒有被佔用</summary>
    private bool PrepareRoad(回合設定 spec)
    {
        var sample = roadSamples.samples[spec.sampleIndex];
        int seg = NearestLaneSegment(sample.position);
        if (seg < 0) return false;

        float runUp = Mathf.Max(runUpDistance, spec.cruise * 3.2f);   // 車速越快助跑越長，讓 2.5 秒穩定時間內不會開過頭
        LanePath newPath = LanePath.Build(net, seg, runUp + 30f, minRoadAhead + 60f, 35f);
        float lat;
        float sp = newPath.Project(sample.position, out lat);
        if (newPath.Length - sp < minRoadAhead * 0.8f) return false;

        float s0 = Mathf.Max(2f, sp - runUp);
        Vector3 p, tan;
        newPath.Sample(s0, out p, out tan);
        if (!IsSpotClear(p, tan)) return false;

        // 確定可以開始才換路徑（失敗時車子還在舊路徑上，不能讓它突然改追新路徑）
        path = newPath;
        triggerS = sp;
        Teleport(p, tan, spec.cruise);
        sE = s0;
        return true;
    }

    private void StartRun(回合設定 spec)
    {
        cur = spec;
        egoModel = IDMParams.NormalDriver();
        egoModel.timeHeadway = spec.timeHeadway;
        egoModel.maxAccel = spec.maxAccel;
        vE = vPrev = vRef = spec.cruise;
        accelSm = 0f;
        threatTime = -1f; styleT0 = -1f; actorLeaderTimer = 0f;
        otherGap = float.PositiveInfinity; otherTrack.Clear();
        actor = null;
        entered = actorHitPlayer = everFallback = outcomeReceived = false;
        entryGap = entryRearGap = float.NaN;
        maxDecelAfterEntry = 0f;
        lastPhase = "";
        eventStart = Time.time;
        prevLatAValid = false;
        minSideGapAlongside = float.PositiveInfinity;
        hitSnapTaken = false; hitType = ""; hitContact = "";
        specCount = 0; specMinSep = float.PositiveInfinity;
        hitNpc = false; hitNpcName = ""; hitNpcTime = -1f;
        SetStage(階段.加速中);
        status = $"第 {spec.pairId} 組｜{(spec.useRL ? "v6" : "規則式")}｜{spec.style}、{spec.cruise * 3.6f:F0} km/h、第 {spec.level} 級｜樣本 #{spec.sampleIndex}\n" + Summary();
    }

    private void FireGhost()
    {
        ApplyLevel(cur.level);
        trigger.rlModeChance = cur.useRL ? 1.1f : 0f;   // 1.1：Random.value 可能剛好等於 1

        if (!trigger.TryStart(DangerDriverScenario.鬼切, true))
        {
            rejectedCount++;
            WriteRow("觸發失敗", false);
            if (!cur.isSecond) { badSamples.Add(cur.sampleIndex); queue.Clear(); }   // 第一次就失敗 → 整組不跑
            SetStage(階段.間隔);
            return;
        }

        actor = FindNewestActor();
        if (actor == null) { AbortEpisode("觸發成功但找不到演員車"); return; }

        lastPhase = actor.phase.ToString();
        eventStart = Time.time;
        string prefabName = actor.name.Replace("(Clone)", "").Trim();
        if (dumpedActors.Add(prefabName)) DumpColliders(actor.transform, "鬼切車「" + prefabName + "」");
        actorHalfLen = Mathf.Clamp(AggressiveDriverActor.MeasureHalfLength(actor.gameObject, 2.8f), 1.5f, 4f);
        actorHalfWid = Mathf.Clamp(AggressiveDriverActor.MeasureHalfWidth(actor.gameObject, 1.4f), 0.7f, 2f);
        float lat; path.Project(actor.transform.position, out lat);
        adjSide = lat;

        var prev = actor.onOutcome;
        actor.onOutcome = (a, o) => { if (prev != null) prev(a, o); outcome = o; outcomeReceived = true; };
        SetStage(階段.演出中);
    }

    private void TrackActor()
    {
        if (actor == null) return;
        string ph = actor.phase.ToString();
        if (ph != lastPhase) lastPhase = ph;
        if (!entered && actor.phase == AggressiveDriverActor.Phase.切入中)
        {
            entered = true;
            entryGap = actor.gapToPlayer;
            entryRearGap = actor.gapToPlayer - actorHalfLen - playerHalfLen;
        }
        if (actor.phase == AggressiveDriverActor.Phase.撞擊停止)
        {
            actorHitPlayer = true;
            if (!hitSnapTaken) TakeHitSnapshot();
        }
        if (cur.useRL && !actor.IsRLControlled) everFallback = true;
        specCount = actor.speculativeContactCount;
        specMinSep = actor.speculativeMinSeparation;
        if (entered) maxDecelAfterEntry = Mathf.Max(maxDecelAfterEntry, -accelSm);
    }

    /// <summary>
    /// 學員車身外框稍微放大一點，檢查有沒有碰到其他有剛體的車（NPC 灰車、機車、白車）。
    /// 鬼切車不算（那個由演員車自己的碰撞判定負責）。
    /// </summary>
    private void CheckNpcContact()
    {
        if (hitNpc) return;
        Vector3 center = rb.position + Vector3.up * 1f;
        Vector3 half = new Vector3(playerHalfWid + 0.1f, 0.9f, playerHalfLen + 0.1f);
        Collider[] hits = Physics.OverlapBox(center, half, player.rotation, trigger.vehicleMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            Rigidbody hrb = h.attachedRigidbody;
            if (hrb == null) continue;
            Transform root = hrb.transform;
            if (root == player || root.IsChildOf(player) || player.IsChildOf(root)) continue;
            if (actor != null && (root == actor.transform || root.IsChildOf(actor.transform))) continue;
            hitNpc = true; hitNpcName = root.name; hitNpcTime = Time.time;
            Debug.LogWarning($"🚧 [城市自動測試] 第 {(cur != null ? cur.pairId : 0)} 組：學員撞到其他車「{root.name}」（{stage}，學員 {vE * 3.6f:F0} km/h、" +
                             $"前方偵測到的車距 {(float.IsInfinity(otherGap) ? "無" : otherGap.ToString("F1") + "m")}）", root);
            return;
        }
    }

    /// <summary>
    /// 🔍 列出某台車身上所有碰撞器的實際範圍（以車子剛體所在物件為原點、公尺；x = 左右、z = 前後、y = 上下）。
    /// ● = 會產生碰撞的實體碰撞器；○ = 觸發器、停用或輪子碰撞器（不會撞到鬼切車）。
    /// 如果某個 ● 的 x 範圍比車身寬很多，就是「車身外的隱形碰撞器」
    /// </summary>
    private void DumpColliders(Transform root, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🔍 [城市自動測試] {title}（{root.name}）的碰撞器，座標以它為原點、單位公尺：");
        float maxX = 0f;
        foreach (var col in root.GetComponentsInChildren<Collider>(true))
        {
            bool active = col.enabled && col.gameObject.activeInHierarchy;
            bool physical = active && !col.isTrigger && !(col is WheelCollider);
            LocalExtent(root, col, out Vector3 mn, out Vector3 mx);
            if (physical) maxX = Mathf.Max(maxX, Mathf.Abs(mn.x), Mathf.Abs(mx.x));
            sb.AppendLine($"  {(physical ? "●" : "○")} {RelPath(root, col.transform)}〔{col.GetType().Name}" +
                          $"{(col.isTrigger ? "、觸發器" : "")}{(active ? "" : "、停用")}、圖層 {LayerMask.LayerToName(col.gameObject.layer)}〕" +
                          $"　x {mn.x:F2}～{mx.x:F2}　z {mn.z:F2}～{mx.z:F2}　y {mn.y:F2}～{mx.y:F2}");
        }
        sb.AppendLine($"  → 實體碰撞器最遠伸到左右 {maxX:F2}m（車身半寬量測值：玩家 {playerHalfWid:F2}m、鬼切車約 {actorHalfWid:F2}m）");
        Debug.Log(sb.ToString(), root);
    }

    private static string RelPath(Transform root, Transform t)
    {
        string p = t.name;
        for (Transform x = t.parent; x != null && x != root; x = x.parent) p = x.name + "/" + p;
        return t == root ? "（本體）" + p : p;
    }

    /// <summary>碰撞器的八個角（依碰撞器種類用本地尺寸計算，停用的也算得出來）換到 root 座標，取最小最大值</summary>
    private static void LocalExtent(Transform root, Collider col, out Vector3 mn, out Vector3 mx)
    {
        Vector3 c = Vector3.zero, h = Vector3.zero;
        bool local = true;
        if (col is BoxCollider bc) { c = bc.center; h = bc.size * 0.5f; }
        else if (col is SphereCollider sc) { c = sc.center; h = new Vector3(sc.radius, sc.radius, sc.radius); }
        else if (col is CapsuleCollider cc)
        {
            c = cc.center;
            float along = Mathf.Max(cc.height * 0.5f, cc.radius);
            h = new Vector3(cc.radius, cc.radius, cc.radius);
            if (cc.direction == 0) h.x = along; else if (cc.direction == 1) h.y = along; else h.z = along;
        }
        else if (col is WheelCollider wc) { c = wc.center; h = new Vector3(wc.radius, wc.radius, wc.radius); }
        else if (col is MeshCollider mc && mc.sharedMesh != null) { c = mc.sharedMesh.bounds.center; h = mc.sharedMesh.bounds.extents; }
        else { local = false; c = col.bounds.center; h = col.bounds.extents; }

        mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        Quaternion inv = Quaternion.Inverse(root.rotation);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = c + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
            Vector3 w = local ? col.transform.TransformPoint(corner) : corner;
            Vector3 l = inv * (w - root.position);
            mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l);
        }
    }

    /// <summary>撞上瞬間的幾何：側向距離接近「兩車半寬相加」= 側面擦撞；側向重疊很多 = 前後碰撞</summary>
    private void TakeHitSnapshot()
    {
        hitSnapTaken = true;
        hitRelS = lastRelS; hitLatGap = lastLatGap; hitApproach = lastApproach;
        hitSpeedDiff = lastSpeedDiff; hitDE = lastDE; hitRL = lastRLControlled;
        hitRearGap = lastRelS - actorHalfLen - playerHalfLen;
        hitContact = actor != null ? actor.collisionDebug : "";
        float sumW = actorHalfWid + playerHalfWid;
        if (hitLatGap > sumW * 0.75f) hitType = "側面擦撞";
        else hitType = hitRelS > 0f ? "前後碰撞（鬼切車在前）" : "前後碰撞（鬼切車在後）";

        Debug.LogWarning($"💥 [城市自動測試] 第 {cur.pairId} 組 {(cur.useRL ? "v6" : "規則式")} 碰撞診斷：{hitType}｜" +
                         $"前後中心距 {hitRelS:F2}m（車尾距 {hitRearGap:F2}m）、側向中心距 {hitLatGap:F2}m（兩車半寬相加 {sumW:F2}m）｜" +
                         $"鬼切車往學員靠近 {hitApproach:F2} m/s、比學員快 {hitSpeedDiff * 3.6f:F1} km/h｜學員偏離車道中心 {hitDE:F2}m｜" +
                         $"{(hitRL ? "強化學習控制中" : "規則式控制中")}｜並行時最小側距 {(float.IsInfinity(minSideGapAlongside) ? "（未並行）" : minSideGapAlongside.ToString("F2") + "m")}" +
                         $"\n　接觸細節：{(string.IsNullOrEmpty(hitContact) ? "（演員車沒有回報）" : hitContact)}", this);
    }

    private void FinishEpisode()
    {
        string result;
        if (actorHitPlayer) result = "碰撞";
        else if (entered) result = "成功切入";
        else if (lastPhase == AggressiveDriverActor.Phase.放棄退後.ToString()) result = "放棄";
        else result = "未切入";
        WriteRow(result, true);

        統計 st = cur.useRL ? statRL : statRule;
        st.n++;
        if (entered) st.entered++;
        if (actorHitPlayer) st.hit++;
        if (entered && !actorHitPlayer) { st.errSum += Mathf.Abs(maxDecelAfterEntry - LevelTarget(cur.level)); st.errN++; }

        Debug.Log($"<color=#66ffcc>🤖 [城市自動測試] 第 {cur.pairId} 組 {(cur.useRL ? "v6" : "規則式")}：{result}" +
                  $"｜{cur.style}、{cur.cruise * 3.6f:F0} km/h、目標 {LevelTarget(cur.level):F1}、學員最大減速 {maxDecelAfterEntry:F1}" +
                  $"{(entered ? $"、切入車尾距 {entryRearGap:F1}m" : "")}{(everFallback ? "、中途改接規則式" : "")}{(hitNpc ? $"、⚠ 學員撞到其他車（{hitNpcName}）" : "")}{(specCount > 0 ? $"、⚪ 推測式接觸 {specCount} 次（最近 {specMinSep:F2}m，不算碰撞）" : "")}｜最後階段 {lastPhase}</color>", this);

        if (cur.isSecond || !pairedComparison) completedPairs++;
        actor = null;
        SetStage(階段.間隔);
        status = Summary();
    }

    private void AbortEpisode(string why)
    {
        Debug.LogWarning($"[城市自動測試] 本次作廢：{why}", this);
        if (actor != null) Destroy(actor.gameObject);
        actor = null;
        if (cur != null && !cur.isSecond) { badSamples.Add(cur.sampleIndex); queue.Clear(); }
        SetStage(階段.間隔);
    }

    private void SetStage(階段 s) { stage = s; stageTime = 0f; }

    // ====================================================================
    //  虛擬學員：自動駕駛
    // ====================================================================

    private void Drive(float dt)
    {
        // ── 量測 ──
        Vector3 vel = rb.velocity; vel.y = 0f;
        vE = Mathf.Max(0f, Vector3.Dot(vel, player.forward));
        float a = (vE - vPrev) / dt;
        vPrev = vE;
        accelSm = Mathf.Lerp(accelSm, Mathf.Clamp(a, -20f, 10f), 0.15f);

        float lat;
        sE = path.Project(rb.position, out lat);
        dE = lat;
        if (path.Length - sE < 60f) path.ExtendForward(80f, 35f);

        // ── 縱向：IDM 跟車 ＋ 對鬼切車的反應 ──
        float gap = float.PositiveInfinity, leaderV = 0f;
        ScanOtherVehicles(dt, ref gap, ref leaderV);

        if (actor != null)
        {
            float latA;
            float sA = path.Project(actor.transform.position, out latA);
            float rel = sA - sE;
            float adjAbs = Mathf.Max(Mathf.Abs(adjSide), 2f);
            bool encroach = Mathf.Abs(latA - dE) < adjAbs - 1f && rel > -15f && rel < 25f;
            if (threatTime < 0f && encroach) threatTime = Time.time;

            // ── 碰撞診斷：每一步都記下兩車相對位置（碰撞發生的那一步就是撞上瞬間的幾何）──
            float latGap = Mathf.Abs(latA - dE);
            float towardSign = Mathf.Sign(dE - latA);   // 正 = 鬼切車往學員這邊移動
            float approach = prevLatAValid ? (latA - prevLatA) / dt * towardSign : 0f;
            prevLatA = latA; prevLatAValid = true;
            lastRelS = rel; lastLatGap = latGap; lastApproach = approach;
            lastSpeedDiff = actor.CurrentSpeed - vE; lastDE = dE; lastRLControlled = actor.IsRLControlled;
            if (!actorHitPlayer && Mathf.Abs(rel) < actorHalfLen + playerHalfLen + 1f)
                minSideGapAlongside = Mathf.Min(minSideGapAlongside, latGap);

            bool ahead = rel > 0f && Mathf.Abs(latA - dE) < actorHalfWid + playerHalfWid + 0.3f;
            actorLeaderTimer = ahead ? actorLeaderTimer + dt : 0f;
            if (actorLeaderTimer >= cur.reaction)
            {
                float g = rel - actorHalfLen - playerHalfLen;
                if (g < gap) { gap = g; leaderV = actor.CurrentSpeed; }
            }
        }
        bool reacting = threatTime >= 0f && Time.time - threatTime >= cur.reaction;
        if (reacting && styleT0 < 0f) styleT0 = Time.time;
        float sinceReact = styleT0 >= 0f ? Time.time - styleT0 : -1f;

        float aDes = DrivingModel.IdmAccel(vE, cur.cruise, Mathf.Max(gap, 0.1f), vE - leaderV, egoModel);
        if (cur.style == 學員風格.提早煞車 && sinceReact >= 0f && sinceReact < cur.earlyDur) aDes = Mathf.Min(aDes, -cur.earlyDecel);
        aDes = Mathf.Clamp(aDes, -cur.maxBrake, cur.maxAccel);
        if (stage == 階段.等待結算 || stage == 階段.間隔) aDes = Mathf.Min(aDes, 0f);
        PedalsFor(aDes, dt);

        // ── 橫向：純追蹤（pure pursuit）沿車道中心 ──
        float look = Mathf.Max(7f, vE * 0.9f);
        Vector3 target, tan;
        path.Sample(sE + look, out target, out tan);
        Vector3 local = player.InverseTransformPoint(target);
        float alpha = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.5f));
        float delta = Mathf.Atan2(2f * wheelBase * Mathf.Sin(alpha), look) * Mathf.Rad2Deg;
        float steerLimit = playerCar.maxSteerAngle;
        if (playerCar.enableSpeedSensitiveSteering && playerCar.steerReduceAtKmH > 1f)
            steerLimit = Mathf.Lerp(playerCar.maxSteerAngle, playerCar.minSteerAngleAtSpeed, Mathf.Clamp01(vE * 3.6f / playerCar.steerReduceAtKmH));
        steerCmd = Mathf.Clamp(delta / Mathf.Max(steerLimit, 1f), -1f, 1f);
    }

    /// <summary>把想要的加速度換成油門／煞車踏板（速度追蹤 ＋ 煞車前饋，煞車力會邊開邊校正）</summary>
    private void PedalsFor(float aDes, float dt)
    {
        vRef = Mathf.Clamp(vRef + aDes * dt, Mathf.Max(0f, vE - 2f), vE + 2f);
        float e = vRef - vE;

        if (aDes < -0.8f)
        {
            gasCmd = 0f;
            brakeCmd = Mathf.Clamp01(-aDes / brakeFullDecel + 0.2f * Mathf.Max(0f, -e));
            // 煞車力自我校正：踩得夠深時，量到的減速度 / 踏板深度 ≈ 全踩能煞多重
            if (brakeCmd > 0.5f && accelSm < -1f)
                brakeFullDecel = Mathf.Clamp(Mathf.Lerp(brakeFullDecel, -accelSm / brakeCmd, 0.02f), 4f, 14f);
        }
        else if (e >= 0f)
        {
            brakeCmd = 0f;
            gasCmd = Mathf.Clamp01(0.15f + 0.5f * e + 0.1f * Mathf.Max(0f, aDes));
        }
        else
        {
            gasCmd = Mathf.Clamp01(0.15f + 0.5f * e);
            brakeCmd = e < -1f ? Mathf.Clamp01(0.1f * (-e - 1f)) : 0f;
        }
    }

    /// <summary>每 0.1 秒掃描自己車道前方的其他車（NPC 車流），找最近的一台當前車</summary>
    private void ScanOtherVehicles(float dt, ref float gap, ref float leaderV)
    {
        otherScanTimer -= dt;
        if (otherScanTimer <= 0f)
        {
            otherScanTimer = 0.1f;
            otherGap = float.PositiveInfinity; otherLeaderV = 0f;
            Vector3 c, tan;
            path.Sample(sE + 40f, out c, out tan);
            Collider[] hits = Physics.OverlapBox(c + Vector3.up, new Vector3(2.5f, 2f, 40f), Quaternion.LookRotation(tan),
                                                 trigger.vehicleMask, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                Rigidbody hrb = h.attachedRigidbody;
                if (hrb == null) continue;   // 沒有剛體的是靜態物件（紅綠燈桿等）。注意：NPC 灰車、機車、白車的圖層都是 TrafficLight，不能用圖層排除
                Transform root = hrb.transform;
                if (root == player || root.IsChildOf(player) || player.IsChildOf(root)) continue;
                if (actor != null && (root == actor.transform || root.IsChildOf(actor.transform))) continue;

                float latO;
                float sO = path.Project(root.position, out latO);
                if (sO <= sE || Mathf.Abs(latO - dE) > 2.0f) continue;
                float g = sO - sE - playerHalfLen - 2.4f;

                int id = root.GetInstanceID();
                bool hasVel = hrb != null && !hrb.isKinematic;
                float vO = hasVel ? Vector3.Dot(hrb.velocity, tan) : 0f;
                if (!hasVel)
                {
                    // 運動學／沒有剛體的車：用兩次位置估速度；第一次看到時先假設跟自己差不多快，避免誤判成停著的車而急煞
                    if (otherTrack.TryGetValue(id, out Vector2 last) && Time.time - last.y > 0.05f) vO = (sO - last.x) / (Time.time - last.y);
                    else vO = vE * 0.8f;
                }
                otherTrack[id] = new Vector2(sO, Time.time);

                if (g < otherGap) { otherGap = g; otherLeaderV = Mathf.Max(0f, vO); }
            }
        }
        if (otherGap < gap) { gap = otherGap; leaderV = otherLeaderV; }
    }

    // ====================================================================
    //  工具
    // ====================================================================

    private bool ResolveReferences(out string why)
    {
        why = "";
        if (trigger == null) trigger = FindObjectOfType<AggressiveDriverEventTrigger>();
        if (trigger == null) { why = "場景裡找不到 AggressiveDriverEventTrigger（危險駕駛事件觸發器）"; return false; }
        if (adaptiveDifficulty == null) adaptiveDifficulty = AdaptiveDifficultyBandit.Instance != null ? AdaptiveDifficultyBandit.Instance : FindObjectOfType<AdaptiveDifficultyBandit>();
        if (roadSamples == null || roadSamples.samples == null || roadSamples.samples.Count == 0)
        { why = "沒有指定「城市路網訓練樣本」（Road Samples 欄位）"; return false; }

        player = trigger.player;
        if (player == null) { var go = GameObject.FindGameObjectWithTag("Player"); if (go != null) player = go.transform; }
        if (player == null) { why = "找不到玩家（Player 標籤）"; return false; }

        if (playerCar == null) playerCar = player.GetComponentInParent<CarPhysicsController>();
        if (playerCar == null) playerCar = player.GetComponentInChildren<CarPhysicsController>();
        if (playerCar == null) playerCar = FindObjectOfType<CarPhysicsController>();
        if (playerCar == null) { why = "找不到玩家車的 CarPhysicsController"; return false; }

        rb = playerCar.GetComponent<Rigidbody>();
        if (rb == null) rb = trigger.playerRigidbody;
        if (rb == null) { why = "找不到玩家車的 Rigidbody"; return false; }
        player = rb.transform;

        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        fGas = typeof(CarPhysicsController).GetField("gasInput", F);
        fBrake = typeof(CarPhysicsController).GetField("brakeInput", F);
        fSteer = typeof(CarPhysicsController).GetField("steerInput", F);
        if (fGas == null || fBrake == null || fSteer == null)
        { why = "CarPhysicsController 裡找不到 gasInput／brakeInput／steerInput 欄位（程式被改過名稱？）"; return false; }

        if (trigger.ghostCutInModel == null)
        { why = "觸發器沒有指定鬼切強化學習模型（Ghost Cut In Model），無法測試 v6"; return false; }
        return true;
    }

    private string ModelName { get { return trigger != null && trigger.ghostCutInModel != null ? trigger.ghostCutInModel.name : "（無模型）"; } }

    private int NearestLaneSegment(Vector3 p)
    {
        int best = -1; float bestD = 3.5f;
        for (int i = 0; i < net.segCount; i++)
        {
            if (net.isScooter[net.segA[i]] || net.isScooter[net.segB[i]]) continue;
            net.ProjectOnSegment(i, p, out float t, out float lat, out float perp);
            if (t < -0.05f || t > 1.05f) continue;
            if (perp < bestD) { bestD = perp; best = i; }
        }
        return best;
    }

    private bool IsSpotClear(Vector3 p, Vector3 tan)
    {
        Collider[] hits = Physics.OverlapBox(p + Vector3.up, new Vector3(2f, 1.5f, playerHalfLen + 6f), Quaternion.LookRotation(tan),
                                             trigger.vehicleMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            Transform root = h.attachedRigidbody != null ? h.attachedRigidbody.transform : h.transform;
            if (actor != null && (root == actor.transform || root.IsChildOf(actor.transform))) continue;
            if (root == player || root.IsChildOf(player) || player.IsChildOf(root)) continue;
            if (h.attachedRigidbody == null) continue;   // 靜態物件（紅綠燈桿等）不算佔用
            return false;
        }
        return true;
    }

    private void Teleport(Vector3 p, Vector3 tan, float speed)
    {
        RaycastHit hit;
        float y = p.y;
        if (Physics.Raycast(p + Vector3.up * 50f, Vector3.down, out hit, 200f, trigger.groundMask, QueryTriggerInteraction.Ignore))
            y = hit.point.y;
        Vector3 pos = new Vector3(p.x, y + groundOffset, p.z);
        Quaternion rot = Quaternion.LookRotation(new Vector3(tan.x, 0f, tan.z).normalized, Vector3.up);

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = pos;
        rb.rotation = rot;
        rb.transform.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
        rb.velocity = rot * Vector3.forward * speed;
        if (playerCar.gearController != null) playerCar.gearController.currentGear = CarGearController.AutomaticGear.D;
    }

    private float MeasureGroundOffset()
    {
        RaycastHit[] hits = Physics.RaycastAll(rb.position + Vector3.up * 3f, Vector3.down, 20f, trigger.groundMask, QueryTriggerInteraction.Ignore);
        float best = float.PositiveInfinity;
        foreach (var h in hits)
        {
            if (h.transform == player || h.transform.IsChildOf(player)) continue;
            float off = rb.position.y - h.point.y;
            if (off > -0.5f && off < best) best = off;
        }
        return float.IsInfinity(best) ? 0.5f : Mathf.Clamp(best + 0.05f, 0f, 2f);
    }

    private void DisableListedComponents()
    {
        disabledBehaviours.Clear();
        if (disableComponentTypes == null) return;
        foreach (var mb in FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || !mb.enabled || mb == this) continue;
            string n = mb.GetType().Name;
            foreach (var t in disableComponentTypes)
                if (!string.IsNullOrEmpty(t) && n == t) { mb.enabled = false; disabledBehaviours.Add(mb); }
        }
        if (disabledBehaviours.Count > 0)
            Debug.Log($"[城市自動測試] 暫時停用 {disabledBehaviours.Count} 個元件：{string.Join("、", disableComponentTypes)}", this);
    }

    private AggressiveDriverActor FindNewestActor()
    {
        // 觸發器同時只會有一台演員車；剛生成的那台處於「接近」階段
        AggressiveDriverActor any = null;
        foreach (var a in FindObjectsOfType<AggressiveDriverActor>())
        {
            if (a.phase == AggressiveDriverActor.Phase.接近) return a;
            any = a;
        }
        return any;
    }

    private void ApplyLevel(int level)
    {
        var s = trigger.settings;
        if (adaptiveDifficulty != null && adaptiveDifficulty.ghostLevels != null && level - 1 < adaptiveDifficulty.ghostLevels.Length)
        {
            var L = adaptiveDifficulty.ghostLevels[level - 1];
            s.targetImposedDecel = L.targetImposedDecel;
            s.brakeAfterCutChance = L.brakeAfterCutChance;
            s.brakeAfterCutDecel = L.brakeAfterCutDecel;
            s.brakeAfterCutFloorRatio = L.brakeAfterCutFloorRatio;
            s.cutInDuration = L.cutInDuration;
            s.signalLeadSeconds = L.signalLeadSeconds;
        }
        else s.targetImposedDecel = LevelTarget(level);
    }

    private float LevelTarget(int level)
    {
        if (adaptiveDifficulty != null && adaptiveDifficulty.ghostLevels != null && level - 1 < adaptiveDifficulty.ghostLevels.Length)
            return adaptiveDifficulty.ghostLevels[level - 1].targetImposedDecel;
        return 1.5f + 0.5f * (level - 1);
    }

    private void WriteRow(string result, bool ran)
    {
        try
        {
            bool newFile = !File.Exists(CsvPath);
            var sb = new StringBuilder();
            if (newFile)
                sb.AppendLine("時間,配對編號,控制方式,模型,難度等級,目標減速度,學員風格,學員反應秒數,學員最大煞車,學員時速,樣本編號,位置X,位置Z," +
                              "隔壁車道側,結果,有切入,切入時中心距,切入時車尾距,學員最大減速度,誤差,鬼切車撞學員,演出期間任何碰撞," +
                              "最小TTC,有反應,反應秒數,中途改接規則式,最後階段,演出秒數," +
                              "並行時最小側距,碰撞類型,碰撞時前後中心距,碰撞時側向中心距,碰撞時車尾距,碰撞時鬼切車靠近速度," +
                              "碰撞時速度差kmh,碰撞時學員偏離車道中心,碰撞時強化學習控制中,防護_超車後車尾距,防護_車身間隔," +
                              "學員撞到其他車,撞到的車,撞到其他車在演出第幾秒,碰撞接觸細節,推測式接觸次數,推測式接觸最近距離");
            var smp = roadSamples.samples[cur.sampleIndex];
            float target = LevelTarget(cur.level);
            bool hasOutcome = ran && outcomeReceived;
            sb.AppendLine(string.Join(",", new[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), cur.pairId.ToString(), cur.useRL ? "強化學習" : "規則式",
                cur.useRL ? ModelName : "", cur.level.ToString(), target.ToString("F2"), cur.style.ToString(),
                cur.reaction.ToString("F2"), cur.maxBrake.ToString("F1"), (cur.cruise * 3.6f).ToString("F0"),
                cur.sampleIndex.ToString(), smp.position.x.ToString("F0"), smp.position.z.ToString("F0"),
                ran ? (adjSide > 0f ? "右" : "左") : "", result, entered ? "1" : "0",
                entered ? entryGap.ToString("F2") : "", entered ? entryRearGap.ToString("F2") : "",
                ran ? maxDecelAfterEntry.ToString("F2") : "",
                entered && !actorHitPlayer ? (maxDecelAfterEntry - target).ToString("F2") : "",
                actorHitPlayer ? "1" : "0", hasOutcome && outcome.collided ? "1" : "0",
                hasOutcome && !float.IsInfinity(outcome.minTTC) ? outcome.minTTC.ToString("F2") : "",
                hasOutcome ? (outcome.reacted ? "1" : "0") : "", hasOutcome ? outcome.reactionDelay.ToString("F2") : "",
                everFallback ? "1" : "0", lastPhase, ran ? (Time.time - eventStart).ToString("F1") : "",
                ran && !float.IsInfinity(minSideGapAlongside) ? minSideGapAlongside.ToString("F2") : "",
                hitSnapTaken ? hitType : "",
                hitSnapTaken ? hitRelS.ToString("F2") : "", hitSnapTaken ? hitLatGap.ToString("F2") : "",
                hitSnapTaken ? hitRearGap.ToString("F2") : "", hitSnapTaken ? hitApproach.ToString("F2") : "",
                hitSnapTaken ? (hitSpeedDiff * 3.6f).ToString("F1") : "", hitSnapTaken ? hitDE.ToString("F2") : "",
                hitSnapTaken ? (hitRL ? "1" : "0") : "",
                trigger.settings.rlLateralClearGap.ToString("F1"), trigger.settings.rlSideMargin.ToString("F1"),
                hitNpc ? "1" : "0", hitNpc ? hitNpcName.Replace(",", " ") : "",
                hitNpc && ran ? (hitNpcTime - eventStart).ToString("F1") : "",
                hitSnapTaken ? hitContact.Replace(",", " ") : "",
                ran ? specCount.ToString() : "",
                ran && !float.IsInfinity(specMinSep) ? specMinSep.ToString("F2") : ""
            }));
            File.AppendAllText(CsvPath, sb.ToString(), new UTF8Encoding(newFile));
        }
        catch (Exception e) { Debug.LogWarning("[城市自動測試] CSV 寫入失敗：" + e.Message, this); }
    }

    private string Summary()
    {
        return $"v6：{Line(statRL)}\n規則式：{Line(statRule)}\n觸發失敗 {rejectedCount} 次";
    }

    private static string Line(統計 s)
    {
        if (s.n == 0) return "尚無資料";
        return $"{s.n} 次｜切入 {100f * s.entered / s.n:F0}%｜碰撞 {100f * s.hit / s.n:F1}%" +
               (s.errN > 0 ? $"｜平均誤差 {s.errSum / s.errN:F1} m/s²" : "");
    }

    private void OnGUI()
    {
        if (!running) return;
        var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true };
        style.normal.textColor = Color.white;
        GUI.Box(new Rect(10, 10, 460, 120), $"🤖 城市自動測試進行中（{toggleKey} 停止）\n{status}", style);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (roadSamples == null && !Application.isPlaying)
        {
            string[] guids = AssetDatabase.FindAssets("t:CityRoadSamples");
            if (guids.Length > 0) roadSamples = AssetDatabase.LoadAssetAtPath<CityRoadSamples>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }

    [MenuItem("Tools/危險駕駛AI/城市自動測試：加入場景（在 cc.unity 執行）")]
    private static void AddToScene()
    {
        var existing = FindObjectOfType<CityAutoTestHarness>();
        if (existing != null) { Selection.activeObject = existing.gameObject; Debug.Log("[城市自動測試] 場景裡已經有了，已幫你選取"); return; }
        var go = new GameObject("城市自動測試");
        var h = go.AddComponent<CityAutoTestHarness>();
        h.OnValidate();
        Undo.RegisterCreatedObjectUndo(go, "加入城市自動測試");
        Selection.activeObject = go;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log($"<color=#66ffcc>🤖 [城市自動測試] 已加入場景｜路網樣本：{(h.roadSamples != null ? h.roadSamples.name : "未找到，請手動指定")}｜按 Play 後按 F11 開始</color>", go);
    }
#endif
}
