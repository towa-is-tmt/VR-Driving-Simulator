using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>危險駕駛劇本種類</summary>
public enum DangerDriverScenario { 鬼切, 路霸 }

/// <summary>
/// 危險駕駛 AI 的所有可調參數（放在觸發器上統一調整，生成演員時整包傳進去）
/// </summary>
[Serializable]
public class AggressiveDriverSettings
{
    [Header("🧠 駕駛模型（IDM）")]
    [Tooltip("演員（路霸／鬼切）的駕駛性格")]
    public IDMParams aggressiveModel = IDMParams.AggressiveDriver();
    [Tooltip("用來推算「一般駕駛被插隊時需要煞多重」的參考模型，決定切入時機，不會拿來控制玩家")]
    public IDMParams playerReferenceModel = IDMParams.NormalDriver();
    [Tooltip("演員最大煞車能力（m/s²）。真車全力煞車約 8~9")]
    public float maxBrake = 8f;
    [Tooltip("計算切入時機時假設的玩家反應時間（秒）。一般駕駛面對突發狀況約 1~1.5 秒")]
    public float playerReactionTime = 1f;
    [Tooltip("計算切入時機時，兩車至少要保持的間距（公尺）")]
    public float safetyMinGap = 1f;

    [Header("👻 鬼切")]
    [Tooltip("接近時比玩家快多少（km/h）")]
    public float ghostSpeedAdvantageKmH = 15f;
    [Tooltip("★ 難度：玩家反應過來後需要踩多重的煞車才不會撞（m/s²，已把切入後的煞車計畫算進去）。\n" +
             "演員會在「需要的減速度 ≤ 這個值」的第一個瞬間切入，所以數字越大切得越近。\n" +
             "1.5 = 溫和、2.5 = 標準、3.5 以上 = 兇狠（實際效果也受下面的切入後煞車影響）")]
    public float targetImposedDecel = 2.5f;
    [Tooltip("切入時車尾與玩家車頭至少要有幾公尺（硬性下限，避免直接撞進玩家車頭）")]
    public float minCutInGap = 3f;
    [Tooltip("切入的橫移時間（秒）。真實鬼切約 1~1.5 秒")]
    public float cutInDuration = 1.2f;
    [Tooltip("切入前打方向燈幾秒。0 = 完全不打燈（鬼切本色）")]
    public float signalLeadSeconds = 0f;
    [Tooltip("切入後接著踩煞車的機率（沒有煞車的鬼切只是「切得很近」，有煞車才會逼玩家重踩）")]
    [Range(0f, 1f)] public float brakeAfterCutChance = 0.6f;
    [Tooltip("切入後煞車的減速度（m/s²）")]
    public float brakeAfterCutDecel = 5.5f;
    [Tooltip("切入後煞到玩家當時速度的幾成")]
    [Range(0f, 1f)] public float brakeAfterCutFloorRatio = 0.4f;
    [Tooltip("煞到底之後維持低速幾秒才加速離開")]
    public float brakeAfterCutHoldSeconds = 1.5f;
    [Tooltip("已經超前玩家這麼多公尺還沒切成，就放棄這次演出")]
    public float giveUpAheadGap = 28f;
    [Header("🤖 鬼切：強化學習模型控制時")]
    [Tooltip("模型輸出 +1 對應的加速度（m/s²）。必須跟訓練場的 Agent Max Accel 一致")]
    public float rlMaxAccel = 3.5f;
    [Tooltip("模型輸出 −1 對應的減速度（m/s²）。必須跟訓練場的 Agent Max Brake 一致")]
    public float rlMaxBrake = 8f;
    [Tooltip("模型輸出 ±1 對應的最大橫移速度（m/s，以 5m 車道為準，會依實際車道寬等比例縮放）")]
    public float rlMaxLateralSpeed = 2.5f;
    [Tooltip("切進玩家車道後，讓模型再控制幾秒（對應訓練場的 Observe Seconds）")]
    public float rlObserveSeconds = 3.5f;
    [Tooltip("🛡 橫向安全防護：鬼切車車尾超過玩家車頭這麼多公尺之前，不准靠到玩家旁邊（m）")]
    public float rlLateralClearGap = 1.5f;
    [Tooltip("🛡 橫向安全防護：還沒超過玩家時，兩車中心至少保持的橫向距離（m）的「最小值」。\n" +
             "實際使用「鬼切車半寬＋玩家半寬＋Rl Side Margin」與這個值取較大者（切車 prefab 縮放 1.4 倍，車寬 2.8m）")]
    public float rlSideClearance = 2.8f;
    [Tooltip("🛡 橫向安全防護：兩車車身之間至少留的空隙（m）")]
    public float rlSideMargin = 0.6f;
    [Tooltip("🛡 橫向安全防護：鬼切車車頭離玩家車尾這麼近（m，接近越快範圍自動越大）才啟動橫向防護。\n" +
             "還離很遠時往玩家車道靠，只是跑到玩家後面，不會撞")]
    public float rlShieldLongitudinalRange = 8f;
    [Tooltip("🔁 改接規則式：模型在玩家後方、車速低於玩家的這個比例超過 3 秒（卡住不前）就改由規則式駕駛")]
    [Range(0.3f, 1f)] public float rlStallSpeedRatio = 0.6f;
    [Tooltip("🔁 改接規則式：玩家時速超出這個範圍就改由規則式駕駛（v2 訓練場練過 20～85 km/h）")]
    public Vector2 rlPlayerSpeedRangeKmH = new Vector2(18f, 90f);
    [Tooltip("🔁 改接規則式：玩家車道的前車比鬼切車慢超過這麼多（km/h）就改由規則式駕駛（v2 訓練場練過很慢的前車，預設等於關閉）")]
    public float rlLeadSlowerLimitKmH = 100f;
    [Tooltip("🔁 改接規則式：玩家偏離自己車道中心超過「車道寬 × 這個比例」就改由規則式駕駛（v2 訓練場練過玩家閃避換道，1 = 整台換到隔壁車道才改接）")]
    [Range(0.2f, 1f)] public float rlPlayerLaneDriftRatio = 1f;
    [Tooltip("🔁 改接規則式：超出範圍要持續幾秒才改接（避免偵測雜訊造成誤判）")]
    public float rlFallbackConfirmSeconds = 0.4f;

    [Tooltip("切入前玩家時速低於這個值超過 1 秒（撞車、停車、塞車）就取消演出。\n" +
             "實測：玩家撞到分隔島降到 3 km/h，鬼切車跟著降到 16 km/h 還是切了進去，毫無意義")]
    public float abortIfPlayerBelowKmH = 15f;

    [Header("😠 路霸")]
    [Tooltip("從後方逼近時比玩家快多少（km/h）")]
    public float hogApproachAdvantageKmH = 20f;
    [Tooltip("車頭時距小於幾秒算「開始貼尾」（開始計時、登記危險因子）")]
    public float tailgateStartHeadway = 0.9f;
    [Tooltip("貼尾逼車至少持續幾秒才開始找超車機會")]
    public float tailgateSeconds = 7f;
    [Tooltip("一直找不到超車機會的上限（秒），超過就放棄、退後離場")]
    public float maxTailgateSeconds = 20f;
    [Tooltip("按喇叭／閃大燈的間隔（秒，隨機）")]
    public float hornIntervalMin = 1.2f;
    public float hornIntervalMax = 2.8f;
    [Tooltip("超車時比玩家快多少（km/h）")]
    public float passSpeedAdvantageKmH = 18f;
    [Tooltip("換道的橫移時間（秒）")]
    public float laneChangeDuration = 1.5f;
    [Tooltip("★ 難度：切回玩家前方時，玩家反應過來後需要的煞車減速度（m/s²，含故意煞車計畫）")]
    public float hogTargetImposedDecel = 3f;
    [Tooltip("切回後故意踩煞車（逼車急煞）的機率")]
    [Range(0f, 1f)] public float brakeCheckChance = 0.7f;
    [Tooltip("切回後隔幾秒才踩")]
    public float brakeCheckDelay = 0.8f;
    [Tooltip("故意煞車的減速度（m/s²）")]
    public float brakeCheckDecel = 6f;
    [Tooltip("故意煞到玩家當時速度的幾成")]
    [Range(0f, 1f)] public float brakeCheckFloorRatio = 0.3f;
    [Tooltip("煞到底之後維持低速幾秒才加速離開")]
    public float brakeCheckHoldSeconds = 1.5f;

    [Header("🏁 共同")]
    [Tooltip("危險解除後加速離開，比玩家快多少（km/h）")]
    public float leaveSpeedAdvantageKmH = 20f;
    [Tooltip("超前玩家這麼遠就消失")]
    public float despawnAheadDistance = 70f;
    [Tooltip("落後玩家這麼遠就消失")]
    public float despawnBehindDistance = 60f;
    [Tooltip("整場演出最長幾秒（保險）")]
    public float maxLifetime = 50f;
    [Tooltip("危險解除後再延長幾秒才結算（給玩家反應量測留時間）")]
    public float hazardTailSeconds = 3f;
    [Tooltip("玩家偏離兩條車道範圍外多少公尺，視為改道（事件作廢）")]
    public float playerLeftPathLateral = 3.5f;   // 實測：這張地圖車道約 5m 寬，2.5 會把「靠車道右緣開」誤判成改道
    [Tooltip("同車道判定：兩車側向距離小於幾公尺算同一車道")]
    public float sameLaneLateral = 1.8f;
    [Tooltip("跟車判定：玩家車身只要有一部分壓進我的車道（側向距離小於這個值）就把玩家當前車、照樣煞車。\n" +
             "實測：玩家在兩條車道中間（側向 2.1m），路霸因為用 1.8m 判斷「不同車道」直接撞上玩家車尾")]
    public float playerOverlapLateral = 2.6f;
    [Tooltip("玩家要在隔壁車道內（離車道中心 1.5m 內）持續幾秒，才算「讓道」")]
    public float yieldConfirmSeconds = 0.5f;
    [Tooltip("換道前，目標車道前方需淨空幾公尺")]
    public float laneChangeClearFront = 12f;
    [Tooltip("換道前，目標車道後方需淨空幾公尺")]
    public float laneChangeClearRear = 8f;
    [Tooltip("前方偵測距離（公尺）")]
    public float forwardProbeDistance = 45f;
    [Tooltip("做換道動作前，前方這段路要夠直（公尺）")]
    public float maneuverStraightLength = 45f;
    [Tooltip("上面那段路允許的最大偏轉角（度）")]
    public float maneuverMaxHeading = 12f;
    [Tooltip("換道動作不在路口節點附近發生")]
    public bool avoidIntersections = true;
    [Tooltip("危險解除後在畫面上顯示本次數據（TTC、車頭時距）。會跟危險感知追蹤器的評語共用同一個文字框")]
    public bool showSummaryMessage = false;

    /// <summary>深複製（自適應難度每次事件都用一份獨立的參數，不會改到觸發器上的原始設定）</summary>
    public AggressiveDriverSettings Clone()
    {
        var c = (AggressiveDriverSettings)MemberwiseClone();
        c.aggressiveModel = aggressiveModel.Clone();
        c.playerReferenceModel = playerReferenceModel.Clone();
        return c;
    }
}

/// <summary>
/// 🚗💢 危險駕駛演員（鬼切／路霸）
///
/// 由 AggressiveDriverEventTrigger 生成並呼叫 Begin()。演出期間是「劇本演員」：
///   - 縱向：IDM（激進參數），會對前車與玩家自動跟車／煞車
///   - 橫向：沿玩家車道中心線的 Frenet 座標（s, d），換道 = d 以五次多項式平滑變化
///   - 剛體設為 Kinematic，用 MovePosition 移動 → 玩家撞上去會有碰撞，但推不動它
///
/// ⚠️ 不修改任何既有車流腳本。演出結束就自行銷毀。
/// </summary>
[DisallowMultipleComponent]
public class AggressiveDriverActor : MonoBehaviour, ITrafficSpeedSource
{
    // 🚗 讓白車／灰車把演員車當成一般前車（見 ITrafficSpeedSource.cs）
    public float CurrentSpeed { get { return v; } }
    public Transform VehicleTransform { get { return transform; } }

    public enum Phase { 待命, 接近, 準備切入, 切入中, 切入後煞車, 貼尾逼車, 換道超車, 並行超越, 等待故意煞車, 故意煞車, 放棄退後, 離開, 撞擊停止 }

    [Header("🎭 目前狀態（唯讀，除錯用）")]
    public DangerDriverScenario scenario;
    public Phase phase = Phase.待命;
    public float currentSpeedKmH;
    public float lateralOffset;
    public float gapToPlayer;

    [Header("🚦 燈號（留空＝生成時自動從原本的 ReverseThreatAICar 複製綁定）")]
    public MeshRenderer leftBrakeLightRenderer;
    public MeshRenderer rightBrakeLightRenderer;
    public MeshRenderer[] leftSignalRenderers;
    public MeshRenderer[] rightSignalRenderers;
    [Tooltip("遠光燈（路霸閃大燈用）。可放 Light 所在物件或發光模型物件，會切換 SetActive")]
    public GameObject[] highBeamObjects;
    [ColorUsage(true, true)] public Color signalColor = new Color(1f, 0.4f, 0f);
    [ColorUsage(true, true)] public Color brakeColor = new Color(3f, 0f, 0f);
    public float emissionIntensity = 7f;

    [Header("🛞 輪子（留空＝自動複製）")]
    public Transform[] wheels;
    public float wheelRollMultiplier = 1.5f;

    [Header("🔊 音效（觸發器會傳入，也可以直接在這裡指定）")]
    public AudioClip hornClip;
    public AudioClip screechClip;
    [Range(0f, 3f)] public float volume = 1f;

    [Header("📐 車身擺放")]
    [Tooltip("地面射線用的圖層（路面）")]
    public LayerMask groundMask = ~0;
    [Tooltip("車身離地高度修正（模型原點不在輪胎底部時調整）")]
    public float groundOffset = 0f;
    [Tooltip("模型朝向修正（模型車頭不是 +Z 時調整，例如 180）")]
    public float modelYawOffset = 0f;

    [Tooltip("換道前檢查隔壁車道時是否排除窄路（由觸發器設定）")]
    public bool excludeNarrowRoads = false;

    [Header("🔧 診斷")]
    public bool logPhases = true;
    public bool drawPath = false;

    // ── 由 Begin 傳入 ──
    private AggressiveDriverSettings cfg;
    private LaneNetwork net;
    private LanePath path;
    private float adjOffset;               // 隔壁車道的 d（帶號，+ = 右）
    private Transform player;
    private Rigidbody playerRb;
    private float playerHalfLen = 2.3f;
    private LayerMask vehicleMask;
    private Action<AggressiveDriverActor> onFinished;

    // ── 強化學習控制（有掛 GhostCutInCityPolicy 時）──
    private GhostCutInCityPolicy rlPolicy;
    private bool rlLateralActive;
    private float rlLateralRate;
    private float rlEnteredTime = -1f;
    private bool rlFallback;                // 超出模型訓練範圍 → 這次演出改由規則式駕駛
    private float rlOodTimer;
    private float rlNextOodCheck;
    private string rlOodReason = "";
    private float rlStallTimer;
    private float adjacentLaneEnd = 1e4f;          // 隔壁車道在哪個 s 結束（觸發器生成時算好）
    private float dpPrev, dpRateSm, playerAccelSm; // 玩家側向速度、縱向加速度（平滑過，給模型看）
    private bool hasDpPrev;
    private float halfWidth = 1.0f, playerHalfWidth = 1.0f;
    public bool IsRLControlled { get { return rlPolicy != null && !rlPolicy.Failed && !rlFallback; } }

    /// <summary>由觸發器在生成時呼叫：鬼切改由強化學習模型駕駛（規則只負責安全防護與流程）</summary>
    public void AttachRLPolicy(GhostCutInCityPolicy policy) { rlPolicy = policy; }

    /// <summary>由觸發器在生成後呼叫：隔壁車道在哪個 s 結束（強化學習的觀測值之一）</summary>
    public void SetAdjacentLaneEnd(float sEnd) { adjacentLaneEnd = sEnd; }

    /// <summary>事件結算時呼叫（自適應難度用來學習）。由觸發器指定</summary>
    public Action<AggressiveDriverActor, DangerOutcome> onOutcome;
    private bool collidedWithPlayer;

    // ── 運動狀態（Frenet）──
    private float s, d, v, accel;
    private float lcFrom, lcTo, lcStart, lcDuration;
    private bool laneChanging;
    private float lateralRate;
    private float halfLen = 2.3f;

    // ── 玩家 ──
    private float sp, dp, vp, vpPrev;
    private Vector3 playerPrevPos;
    private bool hasPlayerPrev;
    private float divergeTimer;

    // ── 流程 ──
    private float phaseStart;
    private float spawnTime;
    private float nextHornTime;
    private bool willBrakeAfter;          // 生成時就決定好，切入時機的計算才能把它算進去
    private float brakeFloorSpeed;
    private float holdStart = -1f;
    private bool hazardRegistered;
    private float hazardEndAt = -1f;
    private bool playerYielded;
    private enum Signal { 無, 左, 右, 雙黃 }
    private Signal signal = Signal.無;
    private float highBeamUntil;
    private bool finished;

    // ── 數據 ──
    private float minTTC = float.PositiveInfinity;
    private float minPlayerHeadway = float.PositiveInfinity;   // 玩家跟在演員後面的最小車頭時距
    private float minActorHeadway = float.PositiveInfinity;    // 演員貼在玩家後面的最小車頭時距
    private float minGapAhead = float.PositiveInfinity;
    private float imposedAtCut = -1f;
    private float playerMaxDecel;
    private float playerSpeedAtHazardKmH;
    private float playerMaxSpeedDuringTailgateKmH;

    // ── 元件 ──
    private Rigidbody rb;
    private AudioSource audioSrc;
    private readonly List<Collider> ownColliders = new List<Collider>();
    private Material leftBrakeMat, rightBrakeMat;
    private readonly List<Material> leftSigMats = new List<Material>();
    private readonly List<Material> rightSigMats = new List<Material>();

    private const float KmH = 1f / 3.6f;

    // ====================================================================
    //  初始化
    // ====================================================================

    /// <summary>從原本白車 AI 複製燈號與輪子的綁定（要在刪除那個元件之前呼叫）</summary>
    public void CopyBindingsFrom(ReverseThreatAICar src)
    {
        if (src == null) return;
        if (leftBrakeLightRenderer == null) leftBrakeLightRenderer = src.leftBrakeLightRenderer;
        if (rightBrakeLightRenderer == null) rightBrakeLightRenderer = src.rightBrakeLightRenderer;
        if (leftSignalRenderers == null || leftSignalRenderers.Length == 0)
            leftSignalRenderers = new[] { src.leftFrontRenderer, src.leftRearRenderer };
        if (rightSignalRenderers == null || rightSignalRenderers.Length == 0)
            rightSignalRenderers = new[] { src.rightFrontRenderer, src.rightRearRenderer };
        if (wheels == null || wheels.Length == 0)
            wheels = new[] { src.leftFrontWheel, src.rightFrontWheel, src.leftRearWheel, src.rightRearWheel };
        brakeColor = src.brakeOnColor;
        emissionIntensity = src.maxEmissionIntensity;
        wheelRollMultiplier = src.wheelRollSpeedMultiplier;
    }

    public void Begin(DangerDriverScenario scenario, AggressiveDriverSettings settings, LaneNetwork network, LanePath lanePath,
                      float adjacentOffset, float spawnS, float spawnD, float initialSpeed,
                      Transform playerTf, Rigidbody playerBody, float playerHalfLength, LayerMask vehicles,
                      Action<AggressiveDriverActor> finishedCallback)
    {
        this.scenario = scenario;
        cfg = settings;
        net = network;
        path = lanePath;
        adjOffset = adjacentOffset;
        s = spawnS;
        d = spawnD;
        v = initialSpeed;
        player = playerTf;
        playerRb = playerBody;
        playerHalfLen = playerHalfLength;
        vehicleMask = vehicles;
        onFinished = finishedCallback;
        spawnTime = Time.time;

        willBrakeAfter = UnityEngine.Random.value < (scenario == DangerDriverScenario.鬼切 ? cfg.brakeAfterCutChance : cfg.brakeCheckChance);

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // 碰撞偵測用「離散」（Discrete）。原本用 ContinuousSpeculative（推測式連續偵測），城市自動測試實測：
        // 兩車並排、車身之間還有 0.2～1.2m 空隙時，物理引擎就提早回報接觸（Unity 所謂的 ghost collision），
        // 鬼切車被誤判撞到而停車打雙黃燈，玩家車也可能被看不見的力量頂一下。
        // 鬼切車每個物理步只移動約 0.5m，車身寬 2.8m，用離散偵測不會穿透
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

        GetComponentsInChildren(true, ownColliders);
        halfLen = MeasureHalfLength(gameObject, 2.3f);
        halfWidth = Mathf.Clamp(MeasureHalfWidth(gameObject, 1.0f), 0.7f, 1.8f);
        playerHalfWidth = Mathf.Clamp(MeasureHalfWidth(playerBody != null ? playerBody.gameObject : playerTf.gameObject, 1.0f), 0.7f, 1.8f);

        audioSrc = gameObject.AddComponent<AudioSource>();
        audioSrc.spatialBlend = 1f;
        audioSrc.minDistance = 5f;
        audioSrc.maxDistance = 80f;
        audioSrc.playOnAwake = false;

        CacheMaterials();
        SetHighBeam(false);

        ApplyPose(0f);   // 先擺到起始位置，避免第一幀出現在原點
        UpdatePlayerState(Time.fixedDeltaTime);

        EnterPhase(Phase.接近, scenario == DangerDriverScenario.鬼切
            ? $"從隔壁車道（{(adjOffset > 0 ? "右" : "左")}側 {Mathf.Abs(adjOffset):F1}m）後方 {sp - s:F0}m 出發"
            : $"從同車道後方 {sp - s:F0}m 出發");

        if (drawPath) path.DrawDebug(Color.magenta, adjOffset, 15f);
        if (IsRLControlled) Log($"🤖 本次由強化學習模型「{rlPolicy.ModelName}」駕駛（目標強度 {RLTarget():F1} m/s²）｜🛡 橫向安全距離 {SideClearance():F1}m" +
                                $"（鬼切車半寬 {halfWidth:F1}＋玩家半寬 {playerHalfWidth:F1}＋餘裕 {cfg.rlSideMargin:F1}）");
    }

    /// <summary>用碰撞體（沒有就用渲染器）量車身半長（m，車頭方向）</summary>
    public static float MeasureHalfLength(GameObject go, float fallback)
    {
        Bounds local;
        if (LocalBodyBounds(go, out local)) return local.extents.z > 0.5f ? local.extents.z : fallback;
        return RendererHalfExtent(go, go.transform.forward, fallback, 0.5f);
    }

    /// <summary>
    /// 在車子自己的方向上量車身範圍（單位是世界公尺，不受車頭朝向影響）。
    /// 舊版用世界座標的 AABB 再投影：車子斜著停（例如朝 289°）時 AABB 會被撐大，
    /// 實測鬼切車與玩家車的半寬都被量成 1.8m（上限值），比實際的 1.4m 大很多，城市與訓練場對不上
    /// </summary>
    private static bool LocalBodyBounds(GameObject go, out Bounds local)
    {
        local = new Bounds();
        bool has = false;
        Transform root = go.transform;
        Quaternion inv = Quaternion.Inverse(root.rotation);
        foreach (var c in go.GetComponentsInChildren<Collider>(true))
        {
            if (!IsBodyCollider(c)) continue;
            Vector3 center, size;
            if (c is BoxCollider bc) { center = bc.center; size = bc.size; }
            else if (c is SphereCollider sc) { center = sc.center; size = Vector3.one * sc.radius * 2f; }
            else if (c is CapsuleCollider cc)
            {
                center = cc.center; size = Vector3.one * cc.radius * 2f;
                size[cc.direction] = Mathf.Max(cc.height, cc.radius * 2f);
            }
            else if (c is MeshCollider mc && mc.sharedMesh != null) { center = mc.sharedMesh.bounds.center; size = mc.sharedMesh.bounds.size; }
            else continue;

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + Vector3.Scale(size * 0.5f,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 p = inv * (c.transform.TransformPoint(corner) - root.position);   // 車身方向、世界公尺
                if (!has) { local = new Bounds(p, Vector3.zero); has = true; } else local.Encapsulate(p);
            }
        }
        return has;
    }

    private static float RendererHalfExtent(GameObject go, Vector3 axis, float fallback, float minValue)
    {
        Bounds b = new Bounds();
        bool has = false;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
        }
        if (!has) return fallback;
        float half = Mathf.Abs(axis.x) * b.extents.x + Mathf.Abs(axis.y) * b.extents.y + Mathf.Abs(axis.z) * b.extents.z;
        return half > minValue ? half : fallback;
    }

    /// <summary>
    /// 算車身大小時要不要算這個碰撞器。排除：觸發器、停用的、所在物件沒啟用的（它們的 bounds 是空的，
    /// 中心在世界原點，Encapsulate 會把車身撐到幾百公尺）、輪子碰撞器（WheelCollider 只對地面做射線，不會撞到別台車）。
    /// 實測：玩家車原本量成 7.0×3.0m，排除後是車身方塊的實際大小
    /// </summary>
    private static bool IsBodyCollider(Collider c)
    {
        return c != null && !c.isTrigger && c.enabled && c.gameObject.activeInHierarchy && !(c is WheelCollider);
    }

    /// <summary>量車身半寬（m，車身右方）。橫向安全防護用</summary>
    public static float MeasureHalfWidth(GameObject go, float fallback)
    {
        Bounds local;
        if (LocalBodyBounds(go, out local)) return local.extents.x > 0.4f ? local.extents.x : fallback;
        return fallback;
    }

    /// <summary>還沒超過玩家時，兩車中心至少要保持的橫向距離</summary>
    private float SideClearance()
    {
        return Mathf.Max(cfg.rlSideClearance, halfWidth + playerHalfWidth + cfg.rlSideMargin);
    }

    // ====================================================================
    //  主迴圈
    // ====================================================================

    private void FixedUpdate()
    {
        if (cfg == null || path == null || finished) return;
        float dt = Time.fixedDeltaTime;

        if (player == null) { Finish("玩家物件消失"); return; }

        UpdatePlayerState(dt);

        // 路徑快用完就往前延伸
        float front = Mathf.Max(s, sp);
        if (front + 120f > path.Length) path.ExtendForward(150f, 35f);

        CheckPlayerDiverged(dt);
        RunPhase(dt);

        // 積分：縱向
        accel = Mathf.Clamp(accel, -cfg.maxBrake, cfg.aggressiveModel.maxAccel);
        v = Mathf.Clamp(v + accel * dt, 0f, 45f);
        s += v * dt;

        // 積分：橫向
        if (rlLateralActive)
        {
            // 強化學習模型直接給橫移速度；夾在「兩條車道」範圍內，模型出錯也不會開出道路
            d += rlLateralRate * dt;
            float lo = Mathf.Min(0f, adjOffset) - 1f, hi = Mathf.Max(0f, adjOffset) + 1f;
            d = Mathf.Clamp(d, lo, hi);
            lateralRate = rlLateralRate;
        }
        else if (laneChanging)
        {
            float t = (Time.time - lcStart) / Mathf.Max(0.1f, lcDuration);
            d = Mathf.Lerp(lcFrom, lcTo, DrivingModel.Quintic(t));
            lateralRate = (lcTo - lcFrom) * DrivingModel.QuinticDerivative(t) / Mathf.Max(0.1f, lcDuration);
            if (t >= 1f) { laneChanging = false; d = lcTo; lateralRate = 0f; }
        }
        else lateralRate = 0f;

        ApplyPose(dt);
        UpdateMetrics();
        CheckDespawn();

        // 顯示用
        currentSpeedKmH = v * 3.6f;
        lateralOffset = d;
        gapToPlayer = s - sp;
    }

    private void Update()
    {
        if (cfg == null || finished) return;
        UpdateLights();
        RollWheels();
    }

    // ====================================================================
    //  玩家狀態
    // ====================================================================

    private void UpdatePlayerState(float dt)
    {
        float lat;
        sp = path.Project(player.position, out lat);
        dp = lat;

        Vector3 p, tan;
        path.Sample(sp, out p, out tan);

        float newVp;
        if (playerRb != null && !playerRb.isKinematic)
            newVp = Vector3.Dot(playerRb.velocity, tan);
        else
        {
            newVp = hasPlayerPrev ? Vector3.Dot(player.position - playerPrevPos, tan) / Mathf.Max(dt, 0.0001f) : v;
            playerPrevPos = player.position;
            hasPlayerPrev = true;
        }
        vpPrev = vp;
        vp = Mathf.Max(0f, newVp);

        // 玩家側向速度與加速度（指數平滑，濾掉物理抖動；訓練場的虛擬玩家也是這兩個量）
        float sdt = Mathf.Max(dt, 0.0001f);
        if (hasDpPrev)
        {
            dpRateSm = Mathf.Lerp(dpRateSm, (dp - dpPrev) / sdt, 0.2f);
            playerAccelSm = Mathf.Lerp(playerAccelSm, Mathf.Clamp((vp - vpPrev) / sdt, -15f, 8f), 0.2f);
        }
        dpPrev = dp;
        hasDpPrev = true;

        if (hazardRegistered)
        {
            float decel = (vpPrev - vp) / Mathf.Max(dt, 0.0001f);
            if (decel > playerMaxDecel && decel < 15f) playerMaxDecel = decel;   // >15 多半是撞擊，不算煞車
        }
    }

    private void CheckPlayerDiverged(float dt)
    {
        if (phase == Phase.離開 || phase == Phase.撞擊停止 || phase == Phase.放棄退後) return;

        float lo = Mathf.Min(0f, adjOffset) - cfg.playerLeftPathLateral;
        float hi = Mathf.Max(0f, adjOffset) + cfg.playerLeftPathLateral;

        // 🔧 路霸：玩家換到「另一側」的車道也是讓道，不是改道。
        //    實測：路霸準備從左側超車，玩家往右換道（d = +6.0m）→ 被判成改道、事件作廢。
        if (scenario == DangerDriverScenario.路霸 && (phase == Phase.接近 || phase == Phase.貼尾逼車 || playerYielded))
        {
            float laneSpan = Mathf.Abs(adjOffset) + cfg.playerLeftPathLateral;
            lo = Mathf.Min(lo, -laneSpan);
            hi = Mathf.Max(hi, laneSpan);
        }
        if (dp < lo || dp > hi) divergeTimer += dt; else divergeTimer = 0f;

        if (divergeTimer > 0.6f)
        {
            if (hazardRegistered && HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.CancelHazard(transform);
            hazardRegistered = false;
            EnterPhase(Phase.離開, $"玩家已改道（側向偏離 {dp:F1}m），事件作廢、不計分");
        }
    }

    // ====================================================================
    //  劇本
    // ====================================================================

    private void RunPhase(float dt)
    {
        float inPhase = Time.time - phaseStart;
        signal = laneChanging && cfg.signalLeadSeconds > 0f && scenario == DangerDriverScenario.鬼切
            ? (lcTo > lcFrom ? Signal.右 : Signal.左) : Signal.無;

        switch (phase)
        {
            // ─────────────── 共同：接近 ───────────────
            case Phase.接近:
                if (scenario == DangerDriverScenario.鬼切) GhostApproach(inPhase);
                else HogApproach(inPhase);
                break;

            // ─────────────── 鬼切 ───────────────
            case Phase.準備切入:
                accel = IdmWithLeaders(vp + cfg.ghostSpeedAdvantageKmH * KmH * 0.5f);
                signal = adjOffset > 0f ? Signal.左 : Signal.右;   // 從右側車道切進來要打左燈
                if (inPhase >= cfg.signalLeadSeconds) StartCutIn();
                break;

            case Phase.切入中:
                if (IsRLControlled && scenario == DangerDriverScenario.鬼切) { RLGhostStep(inPhase, true); break; }
                accel = IdmWithLeaders(Mathf.Max(v, vp));
                if (!laneChanging) AfterCutIn();
                break;

            case Phase.切入後煞車:
                if (RunBrakeToFloor(cfg.brakeAfterCutDecel, cfg.brakeAfterCutHoldSeconds))
                    EnterLeave("切入後煞車結束");
                break;

            // ─────────────── 路霸 ───────────────
            case Phase.貼尾逼車:
                HogTailgate(inPhase);
                break;

            case Phase.換道超車:
                accel = IdmWithLeaders(vp + cfg.passSpeedAdvantageKmH * KmH);
                if (!laneChanging) EnterPhase(Phase.並行超越, "已換到隔壁車道，開始超越");
                break;

            case Phase.並行超越:
                HogPass(inPhase);
                break;

            case Phase.等待故意煞車:
                accel = IdmWithLeaders(vp);
                if (inPhase >= cfg.brakeCheckDelay)
                {
                    brakeFloorSpeed = Mathf.Max(vp * cfg.brakeCheckFloorRatio, 1.5f);
                    holdStart = -1f;
                    EnterPhase(Phase.故意煞車, $"故意煞車 {cfg.brakeCheckDecel:F1} m/s² → 降到 {brakeFloorSpeed * 3.6f:F0} km/h");
                    if (cfg.brakeCheckDecel >= 5f) PlayClip(screechClip, 0.8f);
                }
                break;

            case Phase.故意煞車:
                if (RunBrakeToFloor(cfg.brakeCheckDecel, cfg.brakeCheckHoldSeconds))
                    EnterLeave("故意煞車結束");
                break;

            case Phase.放棄退後:
                // 期望速度比玩家慢 → IDM 自然會鬆油門往後掉，落後夠遠就退場
                accel = IdmWithLeaders(Mathf.Max(0f, vp - 4f));
                break;

            // ─────────────── 收尾 ───────────────
            case Phase.離開:
                accel = IdmWithLeaders(Mathf.Max(vp, v) + cfg.leaveSpeedAdvantageKmH * KmH);
                // 玩家就在旁邊、側距不夠 → 不從旁邊加速超過去（實測：切入後被玩家繞過，離開時加速擦撞玩家）
                if (Mathf.Abs(d - dp) < SideClearance() && Mathf.Abs(s - sp) < halfLen + playerHalfLen + 4f && s <= sp + halfLen)
                    accel = Mathf.Min(accel, (vp - v) * 1.5f - 0.5f);
                break;

            case Phase.撞擊停止:
                accel = v > 0f ? -9f : 0f;
                signal = Signal.雙黃;
                if (inPhase > 12f) Finish("撞擊後停留結束");
                break;
        }

        // 危險結算時間到
        if (hazardRegistered && hazardEndAt > 0f && Time.time >= hazardEndAt) EndHazard();

        // 整場逾時保險
        if (Time.time - spawnTime > cfg.maxLifetime && phase != Phase.離開 && phase != Phase.撞擊停止)
            EnterLeave($"整場演出超過 {cfg.maxLifetime} 秒上限");
    }

    // ── 鬼切 ──
    private float slowPlayerTimer;

    private void GhostApproach(float inPhase)
    {
        // 玩家慢下來（撞車／停車）→ 鬼切沒有意義，取消
        if (vp * 3.6f < cfg.abortIfPlayerBelowKmH) slowPlayerTimer += Time.fixedDeltaTime; else slowPlayerTimer = 0f;
        if (slowPlayerTimer > 1f) { EnterLeave($"玩家時速 {vp * 3.6f:F0} 低於 {cfg.abortIfPlayerBelowKmH:F0}（撞車或停車），取消鬼切"); return; }

        // 🔁 狀況超出模型訓練範圍 → 改由規則式駕駛（只在切入前判斷，切進去之後就讓模型做完）
        if (IsRLControlled && CheckRLOutOfRange()) SwitchToRuleBased();

        if (IsRLControlled)
        {
            if (inPhase > 25f) { EnterLeave("（強化學習）接近超過 25 秒，放棄"); return; }
            if (RearGapToPlayer() > cfg.giveUpAheadGap) { EnterLeave("（強化學習）超前太遠沒有切入，放棄"); return; }
            RLGhostStep(inPhase, false);
            return;
        }

        accel = IdmWithLeaders(vp + cfg.ghostSpeedAdvantageKmH * KmH);

        float rearGap = RearGapToPlayer();
        if (rearGap > cfg.giveUpAheadGap) { EnterLeave($"已超前玩家 {rearGap:F0}m 仍沒有切入機會，放棄"); return; }
        if (inPhase > 25f) { EnterLeave("接近超過 25 秒，放棄"); return; }
        if (rearGap < cfg.minCutInGap) return;

        float required = RequiredPlayerDecel(rearGap, cfg.signalLeadSeconds + cfg.cutInDuration,
                                             cfg.brakeAfterCutDecel, Mathf.Max(vp * cfg.brakeAfterCutFloorRatio, 1.5f));
        if (required > cfg.targetImposedDecel) return;   // 還太近，再往前一點（越往前越不危險）

        string why;
        if (!CanChangeLaneTo(0f, out why)) { LogThrottled("鬼切暫緩：" + why); return; }

        imposedAtCut = required;
        float idmImposed = DrivingModel.ImposedDecel(vp, v, rearGap, cfg.playerReferenceModel);
        Log($"切入時機成立：車尾距玩家車頭 {rearGap:F1}m、相對速度 {(v - vp) * 3.6f:+0;-0} km/h、" +
            $"切入後{(willBrakeAfter ? "會" : "不會")}煞車 → 玩家反應 {cfg.playerReactionTime:F1} 秒後需減速 {required:F1} m/s²" +
            $"（目標 ≤ {cfg.targetImposedDecel:F1}；MOBIL/IDM 參考值 {idmImposed:F1}）");

        if (cfg.signalLeadSeconds > 0f) EnterPhase(Phase.準備切入, $"打方向燈 {cfg.signalLeadSeconds:F1} 秒後切入");
        else StartCutIn();
    }

    private void StartCutIn()
    {
        float target = 0f;
        StartLaneChange(target, scenario == DangerDriverScenario.鬼切 ? cfg.cutInDuration : cfg.laneChangeDuration);
        RegisterHazard(scenario == DangerDriverScenario.鬼切 ? "鬼切" : "路霸切回",
                       scenario == DangerDriverScenario.鬼切 ? cfg.signalLeadSeconds : 0f);
        if (imposedAtCut >= 4.5f) PlayClip(screechClip, 0.5f);
        EnterPhase(Phase.切入中, $"切入玩家車道（{lcDuration:F1} 秒橫移，{(cfg.signalLeadSeconds > 0f && scenario == DangerDriverScenario.鬼切 ? "有打燈" : "未打燈")}）");
    }

    // ====================================================================
    //  🤖 強化學習控制
    // ====================================================================

    /// <summary>
    /// 由訓練好的模型駕駛。規則式在這裡只做兩件事：
    ///   1. 安全防護：加速度取「模型」與「IDM 跟車」的較小值 —— 模型沒看到的其他車，IDM 會煞（Safety Shield）
    ///   2. 流程：偵測切入、登記危險因子、觀察結束後離開
    /// </summary>
    private void RLGhostStep(float inPhase, bool afterEntry)
    {
        // 模型輸出 → 加速度與橫移，連同安全防護，全部交給跟訓練場共用的 GhostCutInRL（確保兩邊行為一致）
        GhostRLState x = BuildRLState(false);
        float idmShield = IdmWithLeaders(vp + 30f * KmH);   // 城市版用射線偵測前車，結果與 GhostCutInRL.IdmShield 同義
        string shieldMsg;
        GhostCutInRL.ApplyAction(x, cfg, rlPolicy.AccelCmd, rlPolicy.LatCmd, idmShield, afterEntry, Time.fixedDeltaTime,
                                 out accel, out rlLateralRate, out shieldMsg);
        rlLateralActive = true;
        if (shieldMsg != null) LogThrottled(shieldMsg);

        // 開始往玩家車道移動 → 危險開始
        if (!hazardRegistered && Mathf.Abs(d) < Mathf.Abs(adjOffset) - 1f && d * adjOffset >= 0f)
        {
            RegisterHazard("鬼切", 0f);
            Log("（強化學習）開始切向玩家車道");
        }

        // 進入玩家車道
        if (!afterEntry && GhostCutInRL.Entered(x))
        {
            rlEnteredTime = Time.time;
            imposedAtCut = RLRequiredNow();
            EnterPhase(Phase.切入中, $"（強化學習）已切入，車尾距玩家 {RearGapToPlayer():F1}m，目標強度 {RLTarget():F1} m/s²");
            return;
        }

        if (afterEntry)
        {
            if (GhostCutInRL.LeftAfterEntry(x)) { EnterLeave("（強化學習）切入後又離開玩家車道"); return; }
            if (Time.time - rlEnteredTime >= cfg.rlObserveSeconds) EnterLeave("（強化學習）切入演出完成");
        }
    }

    /// <summary>
    /// 檢查目前狀況是否超出模型訓練時見過的範圍（每 0.1 秒一次）。
    /// 超出範圍持續 rlFallbackConfirmSeconds 秒才回傳 true，避免偵測雜訊。
    /// </summary>
    private bool CheckRLOutOfRange()
    {
        if (Time.time < rlNextOodCheck) return false;
        rlNextOodCheck = Time.time + 0.1f;

        string why = null;
        float vpk = vp * 3.6f;
        if (vpk < cfg.rlPlayerSpeedRangeKmH.x || vpk > cfg.rlPlayerSpeedRangeKmH.y)
            why = $"玩家時速 {vpk:F0}，模型只練過 {cfg.rlPlayerSpeedRangeKmH.x:F0}～{cfg.rlPlayerSpeedRangeKmH.y:F0}";
        else if (Mathf.Abs(dp) > Mathf.Abs(adjOffset) * cfg.rlPlayerLaneDriftRatio)
            why = $"玩家偏離車道 {dp:F1}m（換道閃避），模型沒練過玩家換道";
        else
        {
            float rel, leadV;
            if (FindPlayerLead(out rel, out leadV) && (v - leadV) * 3.6f > cfg.rlLeadSlowerLimitKmH)
                why = $"玩家前方的車比鬼切車慢 {(v - leadV) * 3.6f:F0} km/h，模型沒練過";
        }

        // 模型在玩家後方減速、遲遲不前（實測：被防護擋住後一路煞到 0 km/h）
        if (v < vp * cfg.rlStallSpeedRatio && RearGapToPlayer() < 0f) rlStallTimer += 0.1f; else rlStallTimer = 0f;
        if (why == null && rlStallTimer > 3f)
            why = $"模型在玩家後方減速到 {v * 3.6f:F0} km/h、超過 3 秒不前";

        if (why == null) { rlOodTimer = 0f; return false; }
        rlOodTimer += 0.1f;
        rlOodReason = why;
        return rlOodTimer >= cfg.rlFallbackConfirmSeconds;
    }

    /// <summary>改由規則式駕駛：停止模型推論，車子先回隔壁車道中心，再由規則式重新判斷切入時機</summary>
    private void SwitchToRuleBased()
    {
        rlFallback = true;
        rlLateralActive = false;
        rlLateralRate = 0f;
        if (rlPolicy != null) rlPolicy.enabled = false;   // 不再推論，省效能
        if (Mathf.Abs(d - adjOffset) > 0.3f) StartLaneChange(adjOffset, cfg.laneChangeDuration);
        Log($"🔁 超出模型訓練範圍（{rlOodReason}）→ 這次改由規則式駕駛");
    }

    private float RLTarget() { return Mathf.Clamp(cfg.targetImposedDecel, 1.5f, 4f); }

    private float RLRequiredNow() { return GhostCutInRL.RequiredNow(BuildRLState(false), cfg); }

    /// <summary>
    /// 目前的狀態（城市版）。withSensors = true 時才做射線偵測（前車、正前方的車），給模型觀測用。
    /// </summary>
    private GhostRLState BuildRLState(bool withSensors)
    {
        var x = new GhostRLState
        {
            s = s, d = d, v = v,
            sp = sp, dp = dp, vp = vp,
            dpRate = dpRateSm, playerAccel = playerAccelSm,
            adjOffset = adjOffset, adjEnd = adjacentLaneEnd,
            halfLen = halfLen, halfWidth = halfWidth,
            playerHalfLen = playerHalfLen, playerHalfWidth = playerHalfWidth,
            target = RLTarget(),
            elapsed = Time.time - spawnTime
        };
        if (withSensors)
        {
            float leadRel, leadV;
            if (FindPlayerLead(out leadRel, out leadV)) { x.hasLead = true; x.leadS = s + leadRel; x.leadV = leadV; }
            float g, spd;
            if (ProbeForward(out g, out spd)) { x.hasFront = true; x.frontGap = g; x.frontV = spd; }
        }
        return x;
    }

    /// <summary>
    /// 給模型的 21 個觀測值 —— 由 GhostCutInRL.FillObservations 計算，訓練場呼叫的是同一個函式。
    /// </summary>
    public void FillRLObservations(float[] o)
    {
        GhostCutInRL.FillObservations(BuildRLState(true), cfg, o);
    }

    private readonly RaycastHit[] leadHits = new RaycastHit[16];

    /// <summary>找玩家車道上、玩家前方的車（訓練場的「前車」）。回傳它相對於演員的沿路距離與速度</summary>
    private bool FindPlayerLead(out float relToActor, out float speed)
    {
        relToActor = 0f; speed = 0f;
        if (player == null) return false;
        Vector3 p, tan;
        path.Sample(sp, out p, out tan);
        Vector3 origin = player.position + Vector3.up * 0.8f + tan * playerHalfLen;
        int n = Physics.SphereCastNonAlloc(origin, 0.9f, tan, leadHits, 80f, vehicleMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Collider bestC = null;
        for (int i = 0; i < n; i++)
        {
            Collider c = leadHits[i].collider;
            if (c == null || ownColliders.Contains(c) || IsPlayerCollider(c)) continue;
            if (leadHits[i].distance < best) { best = leadHits[i].distance; bestC = c; }
        }
        if (bestC == null) return false;
        float sLead = sp + playerHalfLen + best + 2.3f;
        relToActor = sLead - s;
        speed = EstimateSpeed(bestC);
        return true;
    }

    private void AfterCutIn()
    {
        if (scenario == DangerDriverScenario.鬼切)
        {
            if (willBrakeAfter)
            {
                brakeFloorSpeed = Mathf.Max(vp * cfg.brakeAfterCutFloorRatio, 1.5f);
                holdStart = -1f;
                EnterPhase(Phase.切入後煞車, $"切入完成 → 接著煞車 {cfg.brakeAfterCutDecel:F1} m/s²，降到 {brakeFloorSpeed * 3.6f:F0} km/h");
                if (cfg.brakeAfterCutDecel >= 5f) PlayClip(screechClip, 0.6f);
            }
            else EnterLeave("切入完成 → 直接加速離開");
        }
        else
        {
            if (willBrakeAfter) EnterPhase(Phase.等待故意煞車, $"切回完成 → {cfg.brakeCheckDelay:F1} 秒後故意煞車");
            else EnterLeave("切回完成 → 直接加速離開");
        }
    }

    // ── 路霸 ──
    private float yieldTimer;

    /// <summary>
    /// 玩家真的換到隔壁車道（任一側，離車道中心 1.5m 內）並持續 yieldConfirmSeconds 秒才算讓道。
    /// 舊版用「偏離中心超過車道寬六成」判斷，玩家只是跨在兩車道中間（2.8m）就被當成讓道。
    /// </summary>
    private bool PlayerYieldConfirmed()
    {
        float lane = Mathf.Abs(adjOffset);
        bool inSideLane = Mathf.Abs(dp - lane) < 1.5f || Mathf.Abs(dp + lane) < 1.5f;
        yieldTimer = inSideLane ? yieldTimer + Time.fixedDeltaTime : 0f;
        return yieldTimer >= cfg.yieldConfirmSeconds;
    }

    private void HogApproach(float inPhase)
    {
        accel = IdmWithLeaders(vp + cfg.hogApproachAdvantageKmH * KmH);

        // 還沒貼上去玩家就先換車道了 → 直接超過去，不算事件
        if (PlayerYieldConfirmed())
        {
            playerYielded = true;
            EnterPhase(Phase.並行超越, "玩家在路霸貼上來之前就換了車道 → 直接超過去（不計分）");
            return;
        }

        float gap = FrontGapToPlayer();
        float thw = DrivingModel.TimeHeadway(gap, v);
        if (gap > 0f && thw < cfg.tailgateStartHeadway)
        {
            RegisterHazard("路霸逼車", 0f);
            playerSpeedAtHazardKmH = vp * 3.6f;
            playerMaxSpeedDuringTailgateKmH = playerSpeedAtHazardKmH;
            nextHornTime = Time.time + 0.3f;
            EnterPhase(Phase.貼尾逼車, $"開始貼尾：間距 {gap:F1}m、車頭時距 {thw:F2} 秒");
            return;
        }
        if (inPhase > 20f) EnterGiveUp("20 秒內追不上玩家，放棄");
    }

    private void HogTailgate(float inPhase)
    {
        // 故意壓著玩家：期望速度永遠比玩家快，IDM 會把它壓在極小的車頭時距上
        accel = IdmWithLeaders(vp + cfg.hogApproachAdvantageKmH * KmH);
        playerMaxSpeedDuringTailgateKmH = Mathf.Max(playerMaxSpeedDuringTailgateKmH, vp * 3.6f);

        // 喇叭＋閃大燈
        if (Time.time >= nextHornTime)
        {
            nextHornTime = Time.time + UnityEngine.Random.Range(cfg.hornIntervalMin, cfg.hornIntervalMax);
            PlayClip(hornClip, 1f);
            highBeamUntil = Time.time + 0.35f;
        }

        // 玩家讓道（換到任一側的隔壁車道）→ 路霸直接從原車道超過去
        if (PlayerYieldConfirmed())
        {
            playerYielded = true;
            EnterPhase(Phase.並行超越, "玩家已讓道到隔壁車道 → 路霸直接超過去（正確應對）");
            return;
        }

        if (inPhase < cfg.tailgateSeconds) return;

        string why;
        if (CanChangeLaneTo(adjOffset, out why))
        {
            StartLaneChange(adjOffset, cfg.laneChangeDuration);
            PlayClip(hornClip, 1f);
            EnterPhase(Phase.換道超車, $"貼尾 {inPhase:F1} 秒後換道超車");
            return;
        }
        LogThrottled("路霸想超車但暫緩：" + why);

        if (inPhase > cfg.maxTailgateSeconds) EnterGiveUp($"貼尾 {inPhase:F0} 秒都找不到超車機會，放棄");
    }

    private void HogPass(float inPhase)
    {
        accel = IdmWithLeaders(vp + cfg.passSpeedAdvantageKmH * KmH);
        float rearGap = RearGapToPlayer();

        if (playerYielded)
        {
            // 玩家讓道了，超過去就走，不必切回
            if (rearGap > 8f) EnterLeave("已超越讓道的玩家");
            return;
        }

        if (rearGap > cfg.giveUpAheadGap) { EnterLeave($"超前 {rearGap:F0}m 沒切回，直接離開"); return; }
        if (inPhase > 20f) { EnterLeave("並行超過 20 秒，直接離開"); return; }
        if (rearGap < cfg.minCutInGap) return;

        float cutDur = cfg.laneChangeDuration * 0.8f;
        float required = RequiredPlayerDecel(rearGap, cutDur + cfg.brakeCheckDelay,
                                             cfg.brakeCheckDecel, Mathf.Max(vp * cfg.brakeCheckFloorRatio, 1.5f));
        if (required > cfg.hogTargetImposedDecel) return;

        string why;
        if (!CanChangeLaneTo(0f, out why)) { LogThrottled("路霸切回暫緩：" + why); return; }

        imposedAtCut = required;
        Log($"切回時機成立：車尾距玩家車頭 {rearGap:F1}m、之後{(willBrakeAfter ? "會" : "不會")}故意煞車 → " +
            $"玩家需減速 {required:F1} m/s²（目標 ≤ {cfg.hogTargetImposedDecel:F1}）");
        StartLaneChange(0f, cutDur);
        RegisterHazard("路霸切回", 0f);
        if (required >= 4.5f) PlayClip(screechClip, 0.5f);
        EnterPhase(Phase.切入中, "硬切回玩家前方");
    }

    // ====================================================================
    //  工具：跟車、換道檢查
    // ====================================================================

    /// <summary>
    /// 如果現在切進玩家前方，玩家反應過來後要踩多重煞車才不會撞（m/s²）。
    /// 會把「切入後煞車／故意煞車」的計畫算進去（willBrakeAfter）。
    /// </summary>
    private float RequiredPlayerDecel(float rearGap, float brakeDelay, float brakeDecel, float floorSpeed)
    {
        float plannedDecel = willBrakeAfter ? brakeDecel : 0f;
        return DrivingModel.RequiredDecelToAvoid(rearGap, vp, v, plannedDecel, floorSpeed, brakeDelay, 99f,
                                                 cfg.playerReactionTime, cfg.safetyMinGap);
    }

    /// <summary>以 decel 煞到 brakeFloorSpeed，然後維持 holdSeconds。完成回傳 true</summary>
    private bool RunBrakeToFloor(float decel, float holdSeconds)
    {
        if (v > brakeFloorSpeed + 0.05f)
        {
            accel = -decel;
            // 不會煞過頭：這一步會低於底速就剛好停在底速
            if (v - decel * Time.fixedDeltaTime < brakeFloorSpeed) accel = -(v - brakeFloorSpeed) / Time.fixedDeltaTime;
        }
        else
        {
            accel = 0f;
            if (holdStart < 0f) holdStart = Time.time;
        }
        accel = Mathf.Min(accel, IdmWithLeaders(v));   // 前面有車照樣要煞
        return holdStart > 0f && Time.time - holdStart >= holdSeconds;
    }

    /// <summary>自己車頭到玩家車尾（玩家在前面時為正）</summary>
    private float FrontGapToPlayer() { return (sp - s) - halfLen - playerHalfLen; }

    /// <summary>自己車尾到玩家車頭（自己在前面時為正）</summary>
    private float RearGapToPlayer() { return (s - sp) - halfLen - playerHalfLen; }

    /// <summary>IDM：同時考慮玩家（同車道時）與射線偵測到的其他車</summary>
    private float IdmWithLeaders(float desiredSpeed)
    {
        float gap = float.PositiveInfinity;
        float leaderSpeed = 0f;

        // 玩家在我前面、同車道（兩台車實際車寬加起來比 Player Overlap Lateral 寬時，以車寬為準）
        float overlap = Mathf.Max(cfg.playerOverlapLateral, halfWidth + playerHalfWidth + 0.3f);
        if (Mathf.Abs(dp - d) < overlap && sp > s)
        {
            gap = FrontGapToPlayer();
            leaderSpeed = vp;
        }

        // 其他車
        float g2, spd2;
        if (ProbeForward(out g2, out spd2) && g2 < gap) { gap = g2; leaderSpeed = spd2; }

        return DrivingModel.IdmAccel(v, desiredSpeed, gap, v - leaderSpeed, cfg.aggressiveModel);
    }

    private readonly RaycastHit[] probeHits = new RaycastHit[16];

    private bool ProbeForward(out float gap, out float leaderSpeed)
    {
        gap = float.PositiveInfinity; leaderSpeed = 0f;
        Vector3 fwd = transform.forward;
        Vector3 origin = transform.position + Vector3.up * 0.8f + fwd * (halfLen - 0.5f);
        int n = Physics.SphereCastNonAlloc(origin, 0.9f, fwd, probeHits, cfg.forwardProbeDistance, vehicleMask, QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider c = probeHits[i].collider;
            if (c == null || ownColliders.Contains(c)) continue;
            if (IsPlayerCollider(c)) continue;   // 玩家用 Frenet 算，比射線準
            if (probeHits[i].distance < gap)
            {
                gap = probeHits[i].distance;
                leaderSpeed = EstimateSpeed(c);
                found = true;
            }
        }
        return found;
    }

    private bool IsPlayerCollider(Collider c)
    {
        return player != null && (c.transform == player || c.transform.IsChildOf(player) || c.attachedRigidbody == playerRb && playerRb != null);
    }

    private static float EstimateSpeed(Collider c)
    {
        var white = c.GetComponentInParent<ReverseThreatAICar>();
        if (white != null) return white.CurrentHealthSpeed;
        var gray = c.GetComponentInParent<CarNavigation>();
        if (gray != null) return gray.RealTimeSpeed;
        var body = c.attachedRigidbody;
        if (body != null && !body.isKinematic) return body.velocity.magnitude;
        var actor = c.GetComponentInParent<AggressiveDriverActor>();
        if (actor != null) return actor.v;
        return 0f;
    }

    /// <summary>能不能換到 d = targetD 的車道：前方路夠直、沒路口、隔壁車道存在、目標車道淨空</summary>
    private bool CanChangeLaneTo(float targetD, out string reason)
    {
        float s0 = s, s1 = s + cfg.maneuverStraightLength;

        float heading = path.HeadingChange(s0, s1);
        if (heading > cfg.maneuverMaxHeading) { reason = $"前方 {cfg.maneuverStraightLength:F0}m 轉角 {heading:F0}° 太彎"; return false; }

        if (cfg.avoidIntersections && path.HasIntersectionBetween(s0 - 10f, s1)) { reason = "前方有路口節點"; return false; }

        if (Mathf.Abs(targetD) > 0.5f)
        {
            float failS;
            if (!path.AdjacentLaneContinuous(s0, s1, targetD, 10f, 1.3f, 0.95f, excludeNarrowRoads, out failS))
            { reason = $"隔壁車道在前方 {failS - s:F0}m 處中斷"; return false; }
        }

        // 玩家佔住目標車道（只有換去隔壁車道時才可能）
        if (Mathf.Abs(dp - targetD) < cfg.sameLaneLateral)
        {
            float rel = sp - s;
            if (rel > -(cfg.laneChangeClearRear + halfLen + playerHalfLen) && rel < cfg.laneChangeClearFront + halfLen + playerHalfLen
                && Mathf.Abs(targetD) > 0.5f)
            { reason = "玩家在目標車道旁邊"; return false; }
        }

        // 其他車
        Vector3 p, tan;
        float mid = s + (cfg.laneChangeClearFront - cfg.laneChangeClearRear) * 0.5f;
        path.Sample(mid, out p, out tan);
        Vector3 right = Vector3.Cross(Vector3.up, tan);
        Vector3 center = p + right * targetD + Vector3.up * 1f;
        Vector3 half = new Vector3(1.2f, 1f, (cfg.laneChangeClearFront + cfg.laneChangeClearRear) * 0.5f + halfLen);
        Collider[] hits = Physics.OverlapBox(center, half, Quaternion.LookRotation(tan), vehicleMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (ownColliders.Contains(h) || IsPlayerCollider(h)) continue;
            reason = $"目標車道有車（{h.transform.root.name}）";
            return false;
        }

        reason = "";
        return true;
    }

    private void StartLaneChange(float targetD, float duration)
    {
        lcFrom = d;
        lcTo = targetD;
        lcStart = Time.time;
        lcDuration = duration;
        laneChanging = true;
    }

    // ====================================================================
    //  擺放、數據、結束
    // ====================================================================

    private void ApplyPose(float dt)
    {
        Vector3 p, tan;
        path.Sample(s, out p, out tan);
        Vector3 right = Vector3.Cross(Vector3.up, tan);
        Vector3 world = p + right * d;

        RaycastHit hit;
        if (Physics.Raycast(world + Vector3.up * 4f, Vector3.down, out hit, 12f, groundMask, QueryTriggerInteraction.Ignore)
            && !ownColliders.Contains(hit.collider))
            world.y = hit.point.y + groundOffset;
        else
            world.y = p.y + groundOffset;

        Vector3 heading = tan * Mathf.Max(v, 1f) + right * lateralRate;
        Quaternion rot = Quaternion.LookRotation(heading.normalized, Vector3.up) * Quaternion.Euler(0f, modelYawOffset, 0f);

        if (dt <= 0f || rb == null) { transform.SetPositionAndRotation(world, rot); if (rb != null) { rb.position = world; rb.rotation = rot; } }
        else { rb.MovePosition(world); rb.MoveRotation(rot); }
    }

    private void UpdateMetrics()
    {
        if (!hazardRegistered) return;

        bool sameLane = Mathf.Abs(dp - d) < cfg.sameLaneLateral;
        if (!sameLane) return;

        if (s > sp)
        {
            // 演員在玩家前面：玩家該保持的距離
            float gap = RearGapToPlayer();
            minGapAhead = Mathf.Min(minGapAhead, gap);
            minTTC = Mathf.Min(minTTC, DrivingModel.TimeToCollision(gap, vp, v));
            minPlayerHeadway = Mathf.Min(minPlayerHeadway, DrivingModel.TimeHeadway(gap, vp));
        }
        else
        {
            // 演員貼在玩家後面
            float gap = FrontGapToPlayer();
            minActorHeadway = Mathf.Min(minActorHeadway, DrivingModel.TimeHeadway(gap, v));
            minTTC = Mathf.Min(minTTC, DrivingModel.TimeToCollision(gap, v, vp));
        }
    }

    private void CheckDespawn()
    {
        if (phase == Phase.撞擊停止) return;
        float rel = s - sp;
        bool far = rel > cfg.despawnAheadDistance || -rel > cfg.despawnBehindDistance;
        bool leaving = phase == Phase.離開 || phase == Phase.放棄退後;

        if (leaving && far) { Finish($"離玩家 {Mathf.Abs(rel):F0}m，退場"); return; }
        if (s > path.Length - 2f && !path.ExtendForward(50f, 35f)) Finish("路徑走到盡頭，退場");
        if (Time.time - spawnTime > cfg.maxLifetime + 20f) Finish("超過生命週期上限，強制退場");
    }

    private void EnterLeave(string why)
    {
        rlLateralActive = false;
        rlLateralRate = 0f;
        if (hazardRegistered && hazardEndAt < 0f) hazardEndAt = Time.time + cfg.hazardTailSeconds;
        EnterPhase(Phase.離開, why);
    }

    private void EnterGiveUp(string why)
    {
        if (hazardRegistered && hazardEndAt < 0f) hazardEndAt = Time.time + cfg.hazardTailSeconds;
        EnterPhase(Phase.放棄退後, why);
    }

    private void RegisterHazard(string hazardName, float leadSeconds)
    {
        if (hazardRegistered) return;
        hazardRegistered = true;
        hazardEndAt = -1f;
        if (playerSpeedAtHazardKmH <= 0f) playerSpeedAtHazardKmH = vp * 3.6f;
        if (HazardAwarenessTracker.Instance != null)
        {
            HazardStyle style = scenario == DangerDriverScenario.路霸 ? HazardStyle.Full : HazardStyle.Silent;
            HazardAwarenessTracker.Instance.RegisterHazard(hazardName, transform, style, leadSeconds);
        }
        else Log("⚠ 場景中沒有 HazardAwarenessTracker，反應時間不會被記錄");
    }

    private void EndHazard()
    {
        if (!hazardRegistered) return;
        hazardRegistered = false;
        hazardEndAt = -1f;

        string summary = BuildSummary();
        Debug.Log($"<color=#ffb347>📊 [危險駕駛AI] {scenario} 結算：{summary}</color>", this);

        var outcome = new DangerOutcome
        {
            reacted = playerYielded || playerMaxDecel > 1.5f,
            reactionDelay = -1f,
            collided = collidedWithPlayer,
            yielded = playerYielded,
            raced = scenario == DangerDriverScenario.路霸 && playerMaxSpeedDuringTailgateKmH - playerSpeedAtHazardKmH > 8f,
            minTTC = minTTC,
            playerMaxDecel = playerMaxDecel,
            controller = IsRLControlled ? $"強化學習（{rlPolicy.ModelName}）"
                       : (rlFallback ? $"規則式（{(rlPolicy != null ? rlPolicy.ModelName : "強化學習")}超出範圍改接）" : "規則式")
        };

        var tracker = HazardAwarenessTracker.Instance;
        if (tracker != null)
        {
            int before = tracker.History.Count;
            tracker.UnregisterHazard(transform);
            // 危險感知追蹤器的判定比較完整（含轉頭、閃避、撞到任何東西），有的話以它為準
            if (tracker.History.Count > before)
            {
                var rec = tracker.History[tracker.History.Count - 1];
                outcome.reacted = rec.reacted || playerYielded;
                outcome.reactionDelay = rec.reactionDelay;
                outcome.collided = rec.collided || collidedWithPlayer;
            }
            if (cfg.showSummaryMessage)
                tracker.ShowMessage(summary, new Color(1f, 0.8f, 0.3f), 5f);
        }

        if (onOutcome != null) onOutcome(this, outcome);
    }

    private string BuildSummary()
    {
        var parts = new List<string>();
        if (!float.IsInfinity(minTTC)) parts.Add($"最小碰撞時間 TTC {minTTC:F1} 秒");
        if (!float.IsInfinity(minPlayerHeadway)) parts.Add($"你與前車最小車頭時距 {minPlayerHeadway:F1} 秒");
        if (!float.IsInfinity(minActorHeadway)) parts.Add($"路霸貼你最近 {minActorHeadway:F2} 秒");
        if (imposedAtCut >= 0f) parts.Add($"切入強度 {imposedAtCut:F1} m/s²");
        if (playerMaxDecel > 0.5f) parts.Add($"你最大減速 {playerMaxDecel:F1} m/s²");
        if (scenario == DangerDriverScenario.路霸)
        {
            if (playerYielded) parts.Add("✔ 有主動讓道");
            float rise = playerMaxSpeedDuringTailgateKmH - playerSpeedAtHazardKmH;
            if (rise > 8f) parts.Add($"✘ 被逼車時加速了 {rise:F0} km/h（不要跟路霸競速）");
        }
        return parts.Count > 0 ? string.Join("｜", parts.ToArray()) : "無可用數據";
    }

    private void Finish(string why)
    {
        if (finished) return;
        finished = true;
        if (hazardRegistered) EndHazard();
        Log("退場：" + why);
        if (onFinished != null) onFinished(this);
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        // 被外部刪掉（切場景、手動刪除）也要結算與通知觸發器，否則導演會一直鎖住
        if (!finished)
        {
            finished = true;
            if (hazardRegistered && HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.CancelHazard(transform);
            if (onFinished != null) onFinished(this);
        }
        foreach (var m in leftSigMats) if (m != null) Destroy(m);
        foreach (var m in rightSigMats) if (m != null) Destroy(m);
        if (leftBrakeMat != null) Destroy(leftBrakeMat);
        if (rightBrakeMat != null) Destroy(rightBrakeMat);
    }

    /// <summary>
    /// 🔍 碰撞診斷（給城市自動測試讀取）：撞到玩家時，記下是「哪兩個碰撞器」碰在一起、接觸點在兩台車身上的位置。
    /// 接觸點座標以車身中心為原點、單位公尺：x = 左右（+ 右）、y = 上下、z = 前後（+ 前）。
    /// 例如接觸點 x 超過車身半寬很多，就代表撞到的是車身外面的隱形碰撞器，不是車身本身。
    /// </summary>
    [HideInInspector] public string collisionDebug = "";

    [Tooltip("🔍 鬼切車用「推測式連續碰撞偵測」（ContinuousSpeculative），兩車接近時物理引擎會提早回報接觸，\n" +
             "實際上車身之間還有空隙（Unity 所謂的 ghost collision）。勾選後：接觸點距離大於下面的容許值就不算撞到")]
    public bool ignoreSpeculativeContacts = true;
    [Tooltip("接觸點距離（m）大於這個值才當成「推測式接觸、沒有真的碰到」")]
    public float speculativeContactTolerance = 0.05f;
    /// <summary>這台演員車被判定為「推測式接觸、沒有真的碰到玩家」的次數與最近距離（給城市自動測試統計）</summary>
    [HideInInspector] public int speculativeContactCount;
    [HideInInspector] public float speculativeMinSeparation = float.PositiveInfinity;

    private static string ColliderPath(Collider col)
    {
        if (col == null) return "（無）";
        string path = col.name;
        Transform t = col.transform.parent;
        for (int i = 0; i < 3 && t != null; i++, t = t.parent) path = t.name + "/" + path;
        return $"{path}〔{col.GetType().Name}{(col.isTrigger ? "、觸發器" : "")}、圖層 {LayerMask.LayerToName(col.gameObject.layer)}〕";
    }

    private static Vector3 LocalMeters(Transform root, Vector3 worldPoint)
    {
        return root != null ? Quaternion.Inverse(root.rotation) * (worldPoint - root.position) : worldPoint;
    }

    private void OnCollisionEnter(Collision c) { HandlePlayerContact(c, true); }

    /// <summary>推測式接觸之後兩車才真的碰在一起時，Unity 只會呼叫 Stay（不會再呼叫 Enter），所以 Stay 也要檢查</summary>
    private void OnCollisionStay(Collision c) { HandlePlayerContact(c, false); }

    private void HandlePlayerContact(Collision c, bool isEnter)
    {
        if (phase == Phase.撞擊停止 || c.collider == null || !IsPlayerCollider(c.collider)) return;

        // 所有接觸點裡最近的距離：≤ 0 = 真的碰到（重疊）；明顯 > 0 = 推測式接觸，車身之間還有空隙
        float minSep = float.PositiveInfinity;
        for (int i = 0; i < c.contactCount; i++) minSep = Mathf.Min(minSep, c.GetContact(i).separation);
        if (ignoreSpeculativeContacts && minSep > speculativeContactTolerance)
        {
            if (isEnter)
            {
                speculativeContactCount++;
                Debug.Log($"⚪ [危險駕駛AI] 推測式接觸（車身之間還有 {minSep:F2}m 空隙，沒有真的碰到）→ 不算碰撞", this);
            }
            speculativeMinSeparation = Mathf.Min(speculativeMinSeparation, minSep);
            return;
        }

        if (c.contactCount > 0)
        {
            ContactPoint ct = c.GetContact(0);
            Transform pRoot = playerRb != null ? playerRb.transform : player;
            Vector3 pL = LocalMeters(pRoot, ct.point);
            Vector3 aL = LocalMeters(transform, ct.point);
            collisionDebug = $"鬼切車「{ColliderPath(ct.thisCollider)}」碰到 玩家「{ColliderPath(ct.otherCollider)}」｜" +
                             $"接觸點在玩家車身 x {pL.x:F2}、y {pL.y:F2}、z {pL.z:F2}（玩家半寬 {playerHalfWidth:F2}）｜" +
                             $"在鬼切車身 x {aL.x:F2}、y {aL.y:F2}、z {aL.z:F2}（鬼切車半寬 {halfWidth:F2}）｜" +
                             $"接觸距離 {minSep:F2}m（≤0 = 真的重疊）｜{(isEnter ? "Enter" : "Stay")}";
            Debug.LogWarning("🔍 [危險駕駛AI] 碰撞診斷：" + collisionDebug, this);
        }
        laneChanging = false;
        collidedWithPlayer = true;
        EnterPhase(Phase.撞擊停止, $"與玩家碰撞（相對速度 {c.relativeVelocity.magnitude * 3.6f:F0} km/h）→ 停車打雙黃燈");
        if (hazardRegistered && hazardEndAt < 0f) hazardEndAt = Time.time + cfg.hazardTailSeconds;
    }

    // ====================================================================
    //  燈、輪子、音效
    // ====================================================================

    private void CacheMaterials()
    {
        if (leftBrakeLightRenderer != null) leftBrakeMat = leftBrakeLightRenderer.material;
        if (rightBrakeLightRenderer != null) rightBrakeMat = rightBrakeLightRenderer.material;
        if (leftSignalRenderers != null) foreach (var r in leftSignalRenderers) if (r != null) leftSigMats.Add(r.material);
        if (rightSignalRenderers != null) foreach (var r in rightSignalRenderers) if (r != null) rightSigMats.Add(r.material);
    }

    private void UpdateLights()
    {
        bool blinkOn = Mathf.Repeat(Time.time * 1.5f, 1f) < 0.55f;
        bool leftOn = blinkOn && (signal == Signal.左 || signal == Signal.雙黃);
        bool rightOn = blinkOn && (signal == Signal.右 || signal == Signal.雙黃);
        foreach (var m in leftSigMats) SetEmission(m, signalColor, leftOn ? emissionIntensity : 0.03f);
        foreach (var m in rightSigMats) SetEmission(m, signalColor, rightOn ? emissionIntensity : 0.03f);

        bool braking = accel < -1f || v < 0.3f;
        SetEmission(leftBrakeMat, brakeColor, braking ? 1f : 0.03f);
        SetEmission(rightBrakeMat, brakeColor, braking ? 1f : 0.03f);

        // 閃大燈：一次閃兩下
        bool beam = false;
        if (Time.time < highBeamUntil)
        {
            float t = highBeamUntil - Time.time;
            beam = (t > 0.22f) || (t < 0.12f);
        }
        SetHighBeam(beam);
    }

    private static void SetEmission(Material m, Color baseColor, float weight)
    {
        if (m == null) return;
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", baseColor * weight);
    }

    private bool lastHighBeam = true;
    private void SetHighBeam(bool on)
    {
        if (on == lastHighBeam || highBeamObjects == null) return;
        lastHighBeam = on;
        foreach (var go in highBeamObjects) if (go != null) go.SetActive(on);
    }

    private void RollWheels()
    {
        if (wheels == null || v < 0.05f) return;
        float angle = v * wheelRollMultiplier * Time.deltaTime * Mathf.Rad2Deg;
        foreach (var w in wheels) if (w != null) w.Rotate(Vector3.right, angle, Space.Self);
    }

    private void PlayClip(AudioClip clip, float vol)
    {
        if (clip == null || audioSrc == null) return;
        audioSrc.PlayOneShot(clip, vol * volume);
    }

    // ====================================================================
    //  Log
    // ====================================================================

    private void EnterPhase(Phase p, string why)
    {
        phase = p;
        phaseStart = Time.time;
        Log($"→ {p}｜{why}");
    }

    private void Log(string msg)
    {
        if (!logPhases) return;
        Debug.Log($"<color=#ff7f50>🚗💢 [危險駕駛AI·{scenario}] {name}</color> {msg}" +
                  $"  <color=grey>(演員 {v * 3.6f:F0}km/h d={d:F1}｜玩家 {vp * 3.6f:F0}km/h d={dp:F1}｜前後差 {s - sp:F1}m)</color>", this);
    }

    private string lastThrottleMsg = "";
    private float lastThrottleTime = -99f;
    private void LogThrottled(string msg)
    {
        if (msg == lastThrottleMsg && Time.time - lastThrottleTime < 2f) return;
        lastThrottleMsg = msg;
        lastThrottleTime = Time.time;
        Log(msg);
    }
}
