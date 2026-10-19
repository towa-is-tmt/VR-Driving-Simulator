using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
// 定義路徑類別，方便在 Inspector 中設定多條路徑
[System.Serializable]
public class CarNavigation : MonoBehaviour
{
    [Header("=== 節點導航與設定 ===")]
    public WaypointNode currentWaypointNode; // 當前目標節點
    public LayerMask carLayer;               // 判定哪些 Layer 是車子
    private float stopDistance = 3f;         // 到達路徑點的判定距離  

    [Header("=== 物理動力與隨機速度 ===")]
    private float minSpeed = 16f;
    private float maxSpeed = 18f;
    private float accelerationRate = 0.25f;  // 起步加速度 (數值越小越慢)
    private float brakingRate = 0.5f;       // 煞車減速度 (數值越大煞越快)
    private float startDelayMax = 0.8f;  // 綠燈起步或前車開走後的反應延遲

    [Header("=== 彎道預減速設定 ===")]
    private float approachDistance = 30f; // 離路口多遠開始計算「是否需要減速」
    private float reduceDistance = 10f;   // 加快減速的距離(提早達到轉彎目標動力)

    [Header("=== 無號誌「慢」路口設定 ===")]
    private float targetSlowSpeed = 11f;  // 遇到 Slow 點時的目標速度 

    [Header("=== 貝茲平滑轉向參數 ===")]
    private float bezierTriggerDistance = 10f; // 進入貝茲控制區的觸發距離
    private float targetTurnSpeed = 9f;       // 轉彎時的目標速度
    private float targetFastTurnSpeed = 14f;  // 右轉預減速、以及「不論左右轉」在過彎中(貝茲時)的提高時速

    [Header("=== 扇形雷達設定 ===")]
    private int numberOfRays = 5;              // 射線數量，越多越精細但越耗能
    private float fanAngle = 50f;              // 扇形夾角
    private float turnfanAngle = 90f;          // 轉彎時的扇形夾角
    private float detectionDistance = 15f;     // 雷達偵測基礎距離
    private float lateralDetectionMult = 0.5f; // 側邊射線長度衰減係數（側邊看比較短）
    private float straightDetectionMult = 2f;  // 直行時更專注看前方(*2倍距離 = 30m)
    private float straightRaySideSpacing = 0.8f;    // 直行時的側邊2直線射線間距
    private float scooterStraightRaySideSpacing = 1.0f;    // 直行時的側邊2直線射線間距
    private float radarForwardOffset = 2.0f;        // 雷達射線起點往車頭前方推進的距離(一般車輛，車身長)
    private float scooterRadarForwardOffset = 0f; // 機車專用：車身短，起點要更貼近車頭(數值越小越靠近車身)

    [Header("=== 停讓偵測框 ===")]                    // (車旁?m/離地?m/前方?m)
    private Vector3 stopBoxOffsetLeft = new Vector3(-17f, 0.5f, 10f); // 左橫向偏移
    private Vector3 stopBoxOffsetRight = new Vector3(14f, 0.5f, 10f);  // 右橫向偏移
    private Vector3 stopBoxSize = new Vector3(24f, 2f, 17f); // 框 (寬度, 高度, 長度)

    [Header("=== 大路口停讓偵測框 ===")] //(-17-(?/2-12)=-30)
    private Vector3 bigStopBoxOffsetLeft = new Vector3(-20f, 0.5f, 10f);
    private Vector3 bigStopBoxOffsetRight = new Vector3(17f, 0.5f, 10f);
    private Vector3 bigStopBoxSize = new Vector3(30f, 2f, 17f);

    [Header("=== 左轉偵測框 ===")] // 偵測範圍 = a-b/2 ~ a+b/2
    private float turnYieldDistance = 15f; // 離路口多近才開始檢查對向來車
    private float turnDetectionSideOffset = -2f; // 偵測框左右位移 (負值偏左)
    private float turnForwardOffset = 21f; //a 車子偵測中心往前距離
    private Vector3 turnBoxSize = new Vector3(7f, 2f, 40f); // 藍色偵測框大小( 寬度, 高度, b 長度 )

    [Header("=== 黃色網狀線偵測框 ===")]
    private Vector3 yellowGridBoxSize = new Vector3(7f, 2f, 5f); // (寬度, 高度, 長度)
    private Vector3 yellowGridBoxOffset = new Vector3(-4f, 1f, 0f); // (左右偏移, 離地高度, 前方偏移)

    [Header("=== 終點銷毀機制 ===")]
    public float despawnDistance = 50f; // 直行多久後銷毀

    [Tooltip("故障車事件封鎖節點時，離玩家這個距離內的車一律不淡出（公尺）。\n" +
             "對向節奏控制為了製造空檔會一次封鎖幾十個節點，已經在路上的車開到那些節點就會退場；\n" +
             "沒有這道保險的話，玩家會看到對向車開過故障車就憑空消失。0 = 關掉（舊行為）")]
    public float blockedFadeSafeRadius = 45f;
    private Rigidbody playerRbForStuck;

    /// <summary>
    /// 封鎖節點的前後方判定要用的「玩家前方」。
    /// 車在動就用速度方向（沒有階層問題），太慢才退回 Transform 的 forward。
    /// </summary>
    private Vector3 PlayerHeadingForBlockedFade()
    {
        if (playerRbForStuck != null)
        {
            Vector3 v = playerRbForStuck.velocity;
            v.y = 0f;
            if (v.sqrMagnitude > 1f) return v.normalized;   // 1 m/s 以上才採信
        }
        Vector3 fwd = playerTransformForStuck.forward;
        fwd.y = 0f;
        return fwd.sqrMagnitude > 0.0001f ? fwd.normalized : Vector3.forward;
    }

    [Header("=== 機車偏移設定 ===")]
    private float cruiseOffsetRange = 1.6f;     // 直走巡航時的固定隨機偏移範圍(±m)，讓機車散佈不置中
    private float scooterLaneOffsetRange = 0.88f;// 機車專用道比較窄，巡航偏移要收斂
    private float turnLateralOffset = 1.6f;     // 準備轉彎時的靠邊偏移量
    private float offsetSmoothSpeed = 2.5f;     // 偏移平滑速度(出生/回正時緩緩靠過去，避免瞬移)
    private float currentLateralOffset = 0f;    // 當前實際偏移量(內部狀態)
    private float cruiseLateralOffset = 0f;     // 本台機車出生時決定的固定巡航偏移(內部狀態)

    [Header("=== 車輛類型設定 ===")]
    [Tooltip("勾選代表這是機車，直走時會維持固定的橫向偏移(不走正中央)；不勾選則為一般車輛，走路徑點正中央")]
    public bool isScooter = false;

    [Header("=== 模式設定(勾選後車輛不會自行移動) ===")]
    public bool isStaticObstacle = false; // 如果勾選，這台車就不會自己動

    [Header("🪨 靜停姿態鎖 (玩家撞不動)")]
    [Tooltip("靜態障礙車在停放期間把 Rigidbody 轉成 Kinematic + FreezeAll,玩家撞上去推不動;取消勾選 isStaticObstacle 會自動還原")]
    public bool lockParkedPose = true;
    [Tooltip("停放期間偏離原點超過這個公尺數就拉回(雙保險)")]
    public float parkedPoseTolerance = 0.02f;

    [Header("=== 前輪視覺效果設定 ===")]
    [SerializeField] private Transform[] frontLeftWheels;  // 左右前輪：負責滾動 + 轉向
    [SerializeField] private Transform[] frontRightWheels;
    [SerializeField] private Transform[] backLeftWheels;   // 左右後輪：只負責滾動，不負責轉向
    [SerializeField] private Transform[] backRightWheels;
    private float maxSteeringAngle = 35f;  // 輪胎最大轉向角度（一般房車約 30~40 度）
    private float steeringSmoothSpeed = 15f; // 輪胎轉向的反應速度
    private float wheelRadius = 0.35f;    // 輪胎半徑（用來精確計算輪胎滾動速度）

    [Header("=== 車燈視覺效果設定 ===")]
    [SerializeField] private GameObject[] brakeLights;       // 煞車燈物件（紅燈、阻擋、煞停時亮起）
    [SerializeField] private GameObject[] leftIndicators;    // 左方向燈物件
    [SerializeField] private GameObject[] rightIndicators;   // 右方向燈物件
    [SerializeField] private GameObject[] reverseLights;     // 倒車燈物件（預留功能）
    private float indicatorBlinkRate = 0.35f; // 方向燈閃爍頻率（秒）

    [Header("=== 車燈顏色與亮度 (HDR) ===")]
    [ColorUsage(true, true)][SerializeField] private Color brakeColor = Color.red * 3f;       // 煞車燈：紅色 (乘3倍帶有HDR發光感)
    [ColorUsage(true, true)][SerializeField] private Color indicatorColor = new Color(1f, 0.45f, 0f) * 3f; // 方向燈：琥珀橘
    [ColorUsage(true, true)][SerializeField] private Color reverseColor = Color.white * 3f;    // 倒車燈：白色
    // ---------------------------------------------------------
    // 2. 內部運行狀態變數 (不需要在面板顯示的 Private 變數)
    // ---------------------------------------------------------    
    // 尋路與決策快取
    private WaypointNode lastWaypointNode; // 記錄上一個經過的節點
    private WaypointNode[] futureNodes = new WaypointNode[4]; // 核心尋路決策中心 (目標點+未來三點預選)

    /// <summary>🔧 目前的導航目標節點(唯讀)。供 AccidentDirector 判斷這台車是否走在指定路線上</summary>
    public WaypointNode CurrentTargetNode => futureNodes != null && futureNodes.Length > 0 ? futureNodes[0] : null;
    private bool isPathCompleted = false;  // 是否已跑完整條路徑
    // 即時動力控制
    private float smoothDrive = 0f;        // 實際作用於位移的動力值
    private float finalDrivePower = 1.0f;   // 綜合各項偵測後的目標動力百分比(0~1)
    private float realTimeSpeed;            // 用於偵錯與計算曲線
    private float currentRandomSpeed;      // 啟動時產生的隨機最高時速
    private float lastSmoothDrive = 0f;    // 用於偵測主動減速的變數
    // 外部控制快取
    private TrafficSystem_Brain TrafficSystem_Brain; // 引用交通號誌控制器
    private float currentStartDelay = 0f;            // 當前的起步倒數計時
    // 各種行為狀態鎖 (State Booleans)
    private bool isWaitingAtTrafficLight = false;  // 是否受紅燈阻擋
    private bool currentlyBlockedByRay = false;    // 扇形雷達是否偵測到近距離障礙(<3.5m)
    private bool filterBlockedRight = false;        // 是否被右側射線阻擋
    private bool filterBlockedLeft = false;         // 是否被左側射線阻擋
    private bool isSameTurningTrack = false;       // 【備用】是否與前車同軌道轉彎
    private bool isBlockedByCar = false;           // 綜合判斷前方是否有車
    private bool shouldCheckYield = false;         // 是否需要檢查左轉讓直行
    private bool nodeDisablesYield = false;        // 當前節點是否禁用左轉禮讓
    private bool waitingForOppositeStraightCar = false; // 左轉偵測框是否偵測到對向車 [紅色框]
    private bool hasCheckedIntersection = false;   // 【停讓】是否已經完成該路口的停讓檢查
    private bool activeStopBoxIsBig = false;       // 【停讓】目前套用大路口框還是小路口框(只給 Gizmo 用)
    private bool isLeftTurnBlockedStopping = false;// 【停讓】記錄是否曾因左轉受阻而在路口煞停/等待
    private bool isCarDetectedAtStop = false;      // 【停讓】偵測到路口有車的狀態
    private bool isReversing = false;              // 預留：是否正在倒車
    // 冷卻計時器
    private float leftTurnYieldCooldown = 0f; // 左轉偵測冷卻計時器(3s)
    private float exitBezierCooldown = 0f;   // 出彎預防點煞的冷卻計時器(1.5s)
    // 轉彎與打燈共用核心數據
    bool isIncomingTurn = false;   // 是否即將進入轉彎 (預減速 & 方向燈)
    float brakeDistanceBasis = 0f; // 計算離路口多遠開始亮煞車燈
    // 貝茲曲線內部進度
    private bool isBezierActive = false;   // 是否正在由貝茲曲線控制座標
    private float bezierT = 0f;            // 貝茲進度參數 (0.0 ~ 1.0)
    private Vector3 p0, p1, p2;            // 貝茲控制點：起點、控制點(轉折點)、終點
    // 終點直行銷毀暫存
    private bool isFinishingPath = false;   // 是否進入最後直行階段
    private Vector3 finishStartPosition;    // 開始直行的起點
    private Vector3 constantMoveDir;        // 固定前進的方向
    private Vector3 lastDirection;          // 跑完路徑後持續前進的方向
    // 輪胎滾動與旋轉角度暫存
    private float currentSteerRotation = 0f; // 當前輪胎左右轉向角度 (Y軸)
    private float currentRollRotation = 0f;  // 當前輪胎前後滾動角度 (X軸)
    // 側切防抖機制
    private int rightBlockStreak = 0;
    private int leftBlockStreak = 0;
    private const int requiredStreak = 8; // 連續幾幀都偵測到同一側才算數（約 0.13 秒 @60fps）
    private float sideDecisionLockTimer = 0f; // 決策鎖定倒數
    private const float sideDecisionLockDuration = 1.2f; // 決定側切後，鎖定這麼久才能再變更
    private bool committedSideIsRight = false; // 目前鎖定的方向（true=右, false=左）
    private bool hasCommittedSide = false;     // 目前是否已經鎖定了一個方向
    // Gizmos 專用偵錯狀態
    private bool debugShowStopBox = false; // 用於同步 Gizmos 繪製狀態燈
    private bool debugShowRightTurningStopBox = false;
    private WaypointNode debugCurrentExitNode; // 用於 Gizmos 繪製的動態出口快取
    private bool debugIsExitBlocked;           // 用於傳遞出口是否堵塞的狀態給 Gizmos

    private float lastLoggedTargetOffset = 999f; //*******
    // 屬性：回傳目前車輛是否處於「準備轉彎」狀態，供 Gizmos 繪製使用
    public bool amILeftTurning { get; private set; }  // 左轉
    public bool amIRightTurning { get; private set; } // 右轉

    // ================= 危險因子系統(救護車/故障車/倒車威脅/生存管理) =================
    [Header("🚨 玩家/前車倒車威脅偵測")]
    public float playerSafeDistance = 4f;
    public float reverseThreatDetectionRange = 15f;
    public float chainReverseSpeed = 2.0f;
    [Tooltip("🔧 連鎖後退的最低速度。原本寫死 0.1(每秒 10 公分,肉眼幾乎看不出來),建議 1.5~2.5")]
    public float minChainReverseSpeed = 1.8f;
    [Tooltip("舊的「每台車自己偵測玩家倒車」路徑。現在改由玩家車的 PlayerReverseBroadcaster 統一廣播,\n這條預設關閉;開著會在錯誤時機搶先設起 isForcedReversing,把廣播器的呼叫擋掉。\n白車 ReverseThreatAICar 早就關了,這裡補上讓兩邊一致")]
    public bool useLegacySelfDetection = false;
    private bool isForcedReversing = false;
    private Transform activeThreatTransform = null;
    public bool IsForcedReversing => isForcedReversing;
    public bool debugLogDetection = false;

    [Header("🚗 跟車安全距離")]
    public float followStopDistance = 3.5f;

    [Header("🚑 內側車道豁免")]
    // 避讓一律往右(wcEvadeOffset / ambulanceEvadeOffset 都是沿 roadRight 的正值)。
    // 三線道上,位在救護車「左邊」的內側車道車如果照樣往右讓,
    // 會直接切進救護車要走的線 —— 二次偏移還會讓它切得更深,反而把路擋死。
    //
    // 判斷方式刻意用幾何而不是標節點:場景裡三線道以上的路段有 9 個區塊,
    // 逐一標記工作量大又容易在改路網時漏掉。
    //
    // 這道判斷很安全 —— 警笛廣播本來就只通知同向車(Dot(forward,forward) > 0.4),
    // 所以會落在「救護車左邊」的只可能是同向的內側車道車,不會誤傷對向車。
    //
    // 門檻 2.5m 的來由:救護車走在自己那條鏈的左偏 2m 上,同車道的車離它 0~2m,
    // 內側車道的車離它約 3~5m(實測車道間距中位數 5.1m 減去救護車的 2m 左偏)。
    [Tooltip("車在救護車「左邊」超過這個距離就不避讓,維持原本車道正常行駛。" +
             "設 0 = 關閉這道豁免(回到舊行為:所有同向車都往右讓)")]
    public float ambulanceLeftLaneExemptDistance = 2.5f;

    [Header("🚑 救護車避讓")]
    public float ambulanceYieldOffset = 2.5f;
    public float ambulanceYieldOffsetMax = 4.5f;
    [Tooltip("救護車速度低於這個值才算「被卡住」(m/s)")]
    public float ambulanceStuckSpeed = 1.0f;
    [Tooltip("救護車連續卡住幾秒才觸發二次偏移(避免路口減速就誤判)")]
    public float ambulanceStuckTime = 1.5f;
    private float ambBlockedTimer = 0f;
    private float lastYieldLateral = -999f;
    private float yieldStallTimer = 0f;
    public float ambulanceReleaseDistance = 40f;
    public float ambulancePassClearance = 22f;
    public float yieldCutLookAhead = 8f;
    public float playerBlockDespawnTime = 10f;
    [Tooltip("🔧 救護車在我【後方】且已拉開這個距離 → 解除避讓。用來破解「我擋在它前面,卻永遠等不到它超過我」的死鎖")]
    public float ambulanceBehindReleaseDistance = 30f;
    [Tooltip("🔧 避讓持續超過這麼久仍無進展 → 強制解除(0 = 關閉此保險)。防止路口三方互鎖整場卡死")]
    public float ambulanceYieldTimeout = 12f;
    [Tooltip("🔧 救護車在我後方多少角度內算「正後方」。低於此點積就不擋我轉彎,讓我轉完離開清空車道")]
    public float ambulanceBehindDot = -0.3f;
    [Tooltip("🔧 逾時強制解除後,多久之內不再接受救護車的避讓廣播(秒)。太短會馬上被重新設回避讓狀態")]
    public float ambulanceYieldCooldown = 6f;
    private float yieldStartTime = -1f;
    private float yieldCooldownUntil = -1f;
    private bool isYieldingForAmbulance = false;
    private Transform activeAmbulanceTransform = null;
    private float currentYieldOffset = 0f;
    private bool isPulledOverForAmbulance = false;
    private float playerBlockYieldTimer = 0f;

    [Header("🚧 故障車繞行")]
    public float accidentBypassOffset = 2.2f;
    public float accidentBypassDetect = 16f;
    [Tooltip("遇到故障車時停在多遠處(要留足夠空間才轉得出去,建議 10~12)")]
    public float accidentStopDistance = 11f;
    public float accidentBypassRampSpeed = 1.5f;
    public float accidentPassClearance = 10f;
    public float accidentBypassSmoothTime = 1.8f;
    public float bypassLookAhead = 20f;
    [Tooltip("窄路借道前,往左前方看多遠確認沒有對向來車(公尺)")]
    public float oncomingCheckDistance = 30f;
    private float accidentBypassRamp = 0f;
    private float bypassOffsetVel = 0f;
    private int bypassPhase = 0;
    private Vector3 bypassObstaclePos;
    private float lastAccidentSeenTime = -999f;
    [Header("🤝 回歸主車道禮讓")]
    [Tooltip("回歸時偵測主車道來車的時間窗(秒)")]
    public float returnYieldWindow = 3f;
    [Tooltip("禮讓最久幾秒就強制切回(免得永遠等不到縫隙)")]
    public float mergeHoldTimeout = 8f;
    private bool isMergeHolding = false;
    private float mergeHoldTimer = 0f;
    private float returnStartedTime = -999f;   // 剛解除避讓的時間點

    /// <summary>
    /// 🔧 目前是否處於「剛解除避讓、正在切回主車道」的階段。
    ///
    /// 這段期間車子仍偏在路邊,但 isYieldingForAmbulance 已經是 false。
    /// 主車道的車若把它當成一般障礙物就會煞停,而它自己又在禮讓主車道 → 兩台互等。
    /// 供 IsBesideYieldingVehicle() 判斷用,純唯讀,不影響任何既有流程。
    /// </summary>
    public bool IsReturningFromYieldNow =>
        Time.time - returnStartedTime < returnYieldWindow || currentYieldOffset > 0.2f;

    [Header("🚑 路口快速淨空")]
    [Tooltip("救護車在後方、我人在路口內且尚未靠邊 → 用這個動力快速開出路口(0~1)")]
    public float clearJunctionPower = 0.9f;
    [Tooltip("🔧 避讓靠邊途中的低速動力(0~1)。靠邊靠斜切前進達成,完全停住就永遠移不出去;靠好後會自動歸零")]
    public float yieldSlidePower = 0.35f;
    private bool clearJunctionForAmbulance = false;
    private bool blockTurnForAmbulance = false;   // 🚑 救護車還沒過去 → 禁止進入轉彎   // 🚧 最後一次看到故障車/路障的時間(用來判定「合理停等」)
    public bool IsBypassingAccident => bypassPhase >= 1;

    [Header("💀 卡死自毀")]
    public float stuckDetectRadius = 120f;
    public float playerSafeRadius = 60f;
    public float bandStuckTime = 5f;
    [Tooltip("任一停等旗標連續成立超過這麼久，就不再算「合理停等」，並強制解除紅燈停等讓車開走。號誌一輪最長約 40 秒(特殊路口含左轉時相)，所以要設得比它大。設 0 = 關閉這道保險")]
    public float legitWaitTimeout = 60f;
    private float legitWaitTimer = 0f;
    public float fadeOutDuration = 0.5f;
    public float playerBlockDistance = 10f;
    public float intersectionStuckDespawnTime = 8f;
    public float intersectionStuckPlayerSafeDistance = 12f;
    private float stuckDespawnTimer = 0f;
    private Vector3 lastStuckCheckPos;
    private Transform playerTransformForStuck;
    private bool isFadingOut = false;
    private bool isInIntersectionZone = false;

    [Header("🎯 轉正平滑度")]
    public float alignSmoothSpeed = 2.5f;

    // 供救護車/白車/生成器查詢
    public float RealTimeSpeed => realTimeSpeed;
    public bool IsWaitingAtTrafficLightNow => isWaitingAtTrafficLight;
    public bool IsCarDetectedAtStopNow => isCarDetectedAtStop;
    public bool IsYieldingForAmbulanceNow => isYieldingForAmbulance;
    public bool IsPulledOverForAmbulanceNow => isPulledOverForAmbulance;
    // Stop 與 BigStop 一律視為「停」路口，差別只在停讓偵測框的大小
    private bool IsStopNodeType(WaypointNode node)
    {
        if (node == null) return false;
        return node.intersectionType == WaypointNode.IntersectionType.Stop ||
               node.intersectionType == WaypointNode.IntersectionType.BigStop;
    }
    // =============================================================================
    // 2. 新增一個公開方法讓 Trigger 呼叫
    //    用來記錄「現在有哪些停等區叫這台車停」。新路網一個路口可能有好幾個停等盒
    //    互相重疊，一台車會同時壓在兩三個上面；只用單一 bool 的話，先離開的那個盒子
    //    會把其他盒子的停等指令一起解除，車就會在紅燈中途起步。
    private HashSet<Collider> activeRedTriggers = new HashSet<Collider>();

    /// <summary>
    /// 由 TrafficLightTrigger 的 OnTriggerStay 呼叫：更新單一停等區的紅燈狀態
    /// </summary>
    public void UpdateTrafficLightStatus(Collider triggerCollider, bool shouldStop)
    {
        if (shouldStop) activeRedTriggers.Add(triggerCollider);
        else activeRedTriggers.Remove(triggerCollider);

        // 只要還有任何一個停等區要求停，就繼續等。
        // 一律走 SetTrafficLightStatus，救護車避讓與綠燈起步延遲才不會被繞過。
        SetTrafficLightStatus(activeRedTriggers.Count > 0);
    }

    /// <summary>
    /// 由 TrafficLightTrigger 的 OnTriggerExit 呼叫：完全離開某個停等區
    /// </summary>
    public void RemoveTrafficLightTrigger(Collider triggerCollider)
    {
        activeRedTriggers.Remove(triggerCollider);
        SetTrafficLightStatus(activeRedTriggers.Count > 0);
    }

    /// <summary>
    /// 🔍 診斷用:目前有哪幾個停等盒正要求我停。
    /// 停等是 OR —— 只要清單非空就停,所以「左轉盒放行了車還是不動」時,
    /// 真正壓著它的往往是清單裡的另一個盒子。
    /// </summary>
    public string DescribeActiveRedTriggers()
    {
        if (activeRedTriggers == null || activeRedTriggers.Count == 0) return "(無,沒有盒子要求我停)";
        var sb = new System.Text.StringBuilder();
        foreach (var c in activeRedTriggers)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(c == null ? "(已銷毀)" : c.name);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 🔍 診斷用:把 WillTurnLeftWithin 實際在看的那幾顆節點與各段轉角印出來。
    /// 直接看得到「它在看哪幾顆、彎在不在視窗裡」,不必再從場景檔推測。
    /// </summary>
    public string DescribeLookahead(int steps)
    {
        if (isBezierActive) return $"轉彎中(貝茲),直接用即時狀態 amILeftTurning={amILeftTurning}";

        var sb = new System.Text.StringBuilder("車身");
        Vector3 fromPos = transform.position;
        WaypointNode at = LookaheadNode(0, null);

        for (int i = 0; i < steps; i++)
        {
            WaypointNode next = LookaheadNode(i + 1, at);
            if (at == null || next == null) { sb.Append(" → ★節點不足,判定直接回傳 false"); return sb.ToString(); }

            Vector3 dirIn = at.transform.position - fromPos;
            Vector3 dirOut = next.transform.position - at.transform.position;
            dirIn.y = 0f; dirOut.y = 0f;

            string turn = "方向無效";
            if (dirIn.sqrMagnitude > 0.01f && dirOut.sqrMagnitude > 0.01f)
            {
                dirIn.Normalize(); dirOut.Normalize();
                float a = Vector3.Angle(dirIn, dirOut);
                string side = a > 15f ? (Vector3.Cross(dirIn, dirOut).y < 0f ? " 左轉★" : " 右轉") : "";
                turn = $"{a:F0}度{side}";
            }
            sb.Append($" → {at.name}[{turn}]");
            fromPos = at.transform.position;
            at = next;
        }
        if (at != null) sb.Append($" → {at.name}(在視窗外,看不到)");
        return sb.ToString();
    }

    public void SetTrafficLightStatus(bool stop)
    {
        // 🚑 避讓救護車期間一律無視紅燈(並清掉既有紅燈停等),否則停紅燈的車不會讓道
        if (isYieldingForAmbulance) { isWaitingAtTrafficLight = false; return; }
        // 如果原本是紅燈(true)，現在變綠燈(false)，代表要起步了，這時才觸發反應延遲！
        if (isWaitingAtTrafficLight == true && stop == false)
        {
            currentStartDelay = Random.Range(0.2f, startDelayMax);
        }
        isWaitingAtTrafficLight = stop;
    }
    // 讓其他車輛可以提早調閱這台車的「行駛意圖」，不受當前距離限制
    // 🟢 移動總閘門設定（移植自 ReverseThreatAICar）
    [Tooltip("最後一道保險：不管上游速度邏輯怎麼算，這一幀的位移都不准穿進前車。關掉會回到舊行為")]
    public bool useMoveGate = true;
    [Tooltip("讓閘門套用跟前方偵測相同的排除條件（對向車、隔壁讓道中的車）。關掉可能出現「輪胎想動車身卡住」的死鎖")]
    public bool gateUsesCorridorExclusions = true;
    [Tooltip("被排除的對象仍保留的硬性最小間距，純粹防止穿模")]
    public float gateHardMinGap = 0.6f;

    // ↰ 小路口：左轉車讓右轉車
    [Tooltip("我正在左轉、對方正在右轉且在這個距離內時，不再把它當「對向車」豁免掉，會煞停讓它。設 0 = 關閉這條規則")]
    public float leftYieldToRightTurnerRadius = 12f;

    // 供其他車輛查詢「我現在是不是正在轉彎，往哪邊轉」。
    // 必須加上 isBezierActive —— amILeftTurning/amIRightTurning 是「預判」，
    // 車還離路口幾十公尺、夾角一超過 10 度就已經是 true 了。
    // 不收緊的話，左轉車會被一台「等一下才要右轉」的車遠遠扣住不走。
    public bool IsLeftTurningNow => isBezierActive && amILeftTurning;
    public bool IsRightTurningNow => isBezierActive && amIRightTurning;

    /// <summary>
    /// 小路口「左轉車讓右轉車」：我正在左轉、對方正在右轉、而且很近時回傳 true，
    /// 代表那台車不可以再被當成「對向車」豁免掉。
    ///
    /// 刻意做成不對稱 —— 只有左轉車讓，右轉車行為完全不變，兩台才不會互相等成僵局。
    /// 條件也刻意收得很窄（雙方都必須正在轉彎中），窄路會車與過彎掃到對向直行車
    /// 這兩種原本的豁免完全不受影響。
    /// </summary>
    private bool MustYieldToRightTurner(Component other)
    {
        if (leftYieldToRightTurnerRadius <= 0f || other == null) return false;
        if (!IsLeftTurningNow) return false;

        Transform otherTf = null;
        bool otherTurningRight = false;
        float otherSpeed = 0f;

        CarNavigation nav = other.GetComponentInParent<CarNavigation>();
        if (nav != null && nav != this)
        {
            otherTf = nav.transform; otherTurningRight = nav.IsRightTurningNow; otherSpeed = nav.RealTimeSpeed;
        }
        else if (nav == null)
        {
            ReverseThreatAICar threat = other.GetComponentInParent<ReverseThreatAICar>();
            if (threat != null) { otherTf = threat.transform; otherTurningRight = threat.IsRightTurningNow; otherSpeed = threat.CurrentHealthSpeed; }
        }

        if (otherTf == null || !otherTurningRight) return false;

        // 對方停著不動就不該扣住我 —— 否則排隊等紅燈的車會把左轉車永遠釘在原地
        if (otherSpeed < 0.5f) return false;

        // 只讓「在我前方」的車擋。後方或側後方的車不構成轉彎衝突，
        // 原本只看距離，結果連背後的車都能扣住我。
        Vector3 local = transform.InverseTransformPoint(otherTf.position);
        if (local.z <= 0f) return false;

        return local.magnitude <= leftYieldToRightTurnerRadius;
    }

    /// <summary>
    /// 未來幾個節點內會不會左轉。專門給紅綠燈判定用。
    /// IntendsToLeftTurnEarly() 會一路看到 [2]->[3]，那可能是「下一個路口」才轉，
    /// 拿去判斷這個路口的左轉專用燈會把直行車誤扣在原地等箭頭。這支刻意只看近的兩段。
    /// </summary>
    public bool WillTurnLeftWithin(int steps = 2)
    {
        if (isBezierActive) return amILeftTurning;

        Vector3 fromPos = transform.position;
        WaypointNode at = LookaheadNode(0, null);

        for (int i = 0; i < steps; i++)
        {
            WaypointNode next = LookaheadNode(i + 1, at);
            if (at == null || next == null) return false;

            Vector3 dirIn = at.transform.position - fromPos;
            Vector3 dirOut = next.transform.position - at.transform.position;
            dirIn.y = 0f; dirOut.y = 0f;

            if (dirIn.sqrMagnitude > 0.01f && dirOut.sqrMagnitude > 0.01f)
            {
                dirIn.Normalize(); dirOut.Normalize();
                if (Vector3.Angle(dirIn, dirOut) > 15f)
                    return Vector3.Cross(dirIn, dirOut).y < 0f;
            }
            fromPos = at.transform.position;
            at = next;
        }
        return false;
    }

    /// <summary>
    /// 前瞻取節點。陣列內直接取;超出 futureNodes 的長度(只有 4 格)之後,
    /// 只有在「該節點沒有分岔」(nextChoices 剛好一個)時才敢繼續往下追 ——
    /// 那種情況車根本沒得選,追下去一定是它真正會走的路。
    ///
    /// 一遇到分岔就回傳 null 停住,所以前瞻永遠不會誤闖進下一個路口的隨機分支。
    /// 這也順便擋掉了「把下一個路口才左轉的直行車誤判成左轉車」的風險。
    /// </summary>
    private WaypointNode LookaheadNode(int index, WaypointNode prev)
    {
        if (index < futureNodes.Length) return futureNodes[index];
        if (prev == null || prev.nextChoices == null || prev.nextChoices.Count != 1) return null;
        return prev.nextChoices[0];
    }

    public bool IntendsToLeftTurnEarly()
    {
        // 如果已經在過彎（貝茲曲線啟動），直接回傳當前的即時狀態
        if (isBezierActive) return amILeftTurning;
        // 狀況 1：檢查 [0] -> [1] 是否為左轉彎
        if (futureNodes[0] != null && futureNodes[1] != null)
        {
            Vector3 dirToTarget = (futureNodes[0].transform.position - transform.position).normalized;
            Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
            if (Vector3.Angle(dirToTarget, dirNext) > 15f)
            {
                return Vector3.Cross(dirToTarget, dirNext).y < 0; // 內積 Y < 0 代表左轉
            }
        }
        // 狀況 2：檢查 [1] -> [2] 是否為左轉彎
        if (futureNodes[1] != null && futureNodes[2] != null)
        {
            Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
            Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;
            if (Vector3.Angle(dirNext, dirNextNext) > 15f)
            {
                return Vector3.Cross(dirNext, dirNextNext).y < 0;
            }
        }
        // 狀況 3：檢查 [2] -> [3] 是否為左轉彎
        if (futureNodes[2] != null && futureNodes[3] != null)
        {
            Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;
            Vector3 dirNextNextNext = (futureNodes[3].transform.position - futureNodes[2].transform.position).normalized;
            if (Vector3.Angle(dirNextNext, dirNextNextNext) > 15f)
            {
                return Vector3.Cross(dirNextNext, dirNextNextNext).y < 0;
            }
        }
        // 若未來三點都沒有左轉趨勢，則視為直行（或右轉）
        return false;
    }
    // 統一管理雷達角度邏輯
    private float GetCurrentRadarAngle()
    {
        // 如果你在編輯器沒按下 Play，預設顯示全開角度 (turnfanAngle)，方便調整
        if (!Application.isPlaying) return turnfanAngle;
        // 執行時：轉彎用 turnfanAngle ，直行用較窄的角度 
        return amILeftTurning || amIRightTurning ? turnfanAngle : fanAngle;
    }
    // 統一管理雷達距離邏輯
    private float GetCurrentDetectionDistance()
    {
        // 執行時：直行看比較遠 (基礎距離 * 倍數)，轉彎看基礎距離
        //return amILeftTurning ? detectionDistance : detectionDistance * straightDetectionMult;
        return detectionDistance;
    }

    void Start()
    {
        if (currentWaypointNode == null)
        {
            WaypointNode[] allNodes = Object.FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
            float closestDist = Mathf.Infinity;
            foreach (var node in allNodes)
            {
                float d = Vector3.Distance(transform.position, node.transform.position);
                if (d < closestDist) { closestDist = d; currentWaypointNode = node; }
            }
        }
        currentRandomSpeed = Random.Range(minSpeed, maxSpeed);
        // 🚶 會動的車自動掛上「行人禮讓感測器」——免改 prefab
        if (!isStaticObstacle && GetComponent<PedestrianYieldSensor>() == null)
            gameObject.AddComponent<PedestrianYieldSensor>();
        TrafficSystem_Brain = Object.FindFirstObjectByType<TrafficSystem_Brain>();
        if (currentWaypointNode != null)
        {
            InitializePath(currentWaypointNode);
            lastWaypointNode = currentWaypointNode; // 預設為起點節點
        }
        else
        {
            Debug.LogWarning($"[路徑初始化失敗] {gameObject.name} 找不到初始 currentWaypointNode！");
        }
        if (isScooter)
            cruiseLateralOffset = Random.Range(-cruiseOffsetRange, cruiseOffsetRange);

        GameObject stuckPlayerObj = GameObject.FindWithTag("Player");
        if (stuckPlayerObj != null)
        {
            playerTransformForStuck = stuckPlayerObj.transform;
            // 快取玩家剛體：封鎖節點的前後方判定要用「實際行進方向」而不是 forward。
            // 每幀每台車各自 GetComponentInParent 太浪費,開場抓一次就好。
            playerRbForStuck = stuckPlayerObj.GetComponentInParent<Rigidbody>();
        }
        lastStuckCheckPos = transform.position;

        // 🪨 一出生就是路邊靜態停放車 → 立刻上鎖(在第一次物理更新之前)
        if (isStaticObstacle) ApplyParkLock();
    }

    // ==================== 🪨 靜停姿態鎖 (路邊靜態停放車:撞不動) ====================
    //
    // 原本靜態障礙只是「Update 直接 return」,Rigidbody 仍是動態的
    // → 玩家側方停車擦撞時整台車被物理推走,路邊車陣就歪掉。
    // 停放期間改成 Kinematic + FreezeAll,碰撞衝量不再作用在它身上
    // (玩家車照樣正常被彈開,手感等同撞牆),並每幀釘回停放姿態當雙保險。

    private Rigidbody cachedParkRb;
    private bool parkLockActive = false;
    private Vector3 parkedAnchorPos;
    private Quaternion parkedAnchorRot;
    private bool savedIsKinematic;
    private RigidbodyConstraints savedConstraints;

    private Rigidbody ParkRigidbody
    {
        get
        {
            if (cachedParkRb == null) cachedParkRb = GetComponent<Rigidbody>();
            if (cachedParkRb == null) cachedParkRb = GetComponentInChildren<Rigidbody>();
            return cachedParkRb;
        }
    }

    /// <summary>🪨 進入靜態停放:記下姿態並關閉物理反應</summary>
    public void ApplyParkLock()
    {
        if (!lockParkedPose || parkLockActive) return;

        parkedAnchorPos = transform.position;
        parkedAnchorRot = transform.rotation;

        Rigidbody rb = ParkRigidbody;
        if (rb != null)
        {
            savedIsKinematic = rb.isKinematic;
            savedConstraints = rb.constraints;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }
        parkLockActive = true;
    }

    /// <summary>🪨 解除靜停鎖,Rigidbody 設定原樣還原</summary>
    public void ReleaseParkLock()
    {
        if (!parkLockActive) return;
        parkLockActive = false;

        Rigidbody rb = ParkRigidbody;
        if (rb != null)
        {
            rb.constraints = savedConstraints;
            rb.isKinematic = savedIsKinematic;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            if (!rb.isKinematic) rb.WakeUp();
        }
    }

    /// <summary>🪨 停放期間每幀把車釘回原點</summary>
    private void HoldParkedPose()
    {
        if (!parkLockActive) return;

        if ((transform.position - parkedAnchorPos).sqrMagnitude > parkedPoseTolerance * parkedPoseTolerance)
            transform.position = parkedAnchorPos;
        if (Quaternion.Angle(transform.rotation, parkedAnchorRot) > 0.1f)
            transform.rotation = parkedAnchorRot;
    }

    /// <summary>🪨 同步靜態停放狀態 → 鎖定狀態(Inspector 手改、車輛池回收都吃得到)</summary>
    private void SyncParkLockState()
    {
        if (isStaticObstacle) ApplyParkLock();
        else if (parkLockActive) ReleaseParkLock();
    }

    void LateUpdate()
    {
        if (isStaticObstacle) HoldParkedPose();
    }

    void Update()
    {
        SyncParkLockState(); // 🪨 靜停 ↔ 行駛 的狀態切換,同步鎖定/解鎖物理

        if (isStaticObstacle) { HoldParkedPose(); return; }
        HandleStuckDespawn();                       // 💀 卡死看門狗(範圍/淡出/玩家保護)
        if (isForcedReversing) return;              // 倒車閃避中,移動交給協程

        // 🚑 救護車避讓:救護車真的開過我車身(ambulancePassClearance)才回歸
        if (isYieldingForAmbulance)
        {
            if (yieldStartTime < 0f) yieldStartTime = Time.time;   // 🔧 記錄避讓開始時間,供逾時保險使用

            bool releaseYield = false;
            if (activeAmbulanceTransform == null)
            { releaseYield = true; diagReleaseReason = "① 救護車參照變成 null(已離場或被銷毀)"; }
            else
            {
                Vector3 toAmbR = activeAmbulanceTransform.position - transform.position;
                float dotR = Vector3.Dot(transform.forward, toAmbR.normalized);

                // 原條件:救護車跑到我「前方」且距離拉開 → 它確實已經超過我了
                // 【舊版】if (dotR > 0f && toAmbR.magnitude > ambulancePassClearance) releaseYield = true;
                if (dotR > 0f && toAmbR.magnitude > ambulancePassClearance)
                {
                    releaseYield = true;
                    diagReleaseReason = $"② 救護車已在我前方 {toAmbR.magnitude:F1}m > {ambulancePassClearance}m(正常通過)";
                }
                // 🔧 救護車在我「後方」且拉得很遠 → 只有它「沒有朝我開來」才解除。
                //    警笛走廊會通知前方 50m 的車,收到警報時救護車在後方 30~50m 是正常狀態;
                //    少了這個判斷,每 0.5 秒的廣播會被同幀解除 → 偏移目標永遠是 0 → 灰車不偏移。
                else if (dotR <= 0f && toAmbR.magnitude > ambulanceBehindReleaseDistance)
                {
                    Vector3 ambFwdR = activeAmbulanceTransform.forward; ambFwdR.y = 0f;
                    Vector3 toMeR = -toAmbR; toMeR.y = 0f;
                    bool ambComingTowardMe = ambFwdR.sqrMagnitude > 0.01f && toMeR.sqrMagnitude > 0.01f
                        && Vector3.Dot(ambFwdR.normalized, toMeR.normalized) > ambulanceComingTowardDot;
                    if (!ambComingTowardMe)
                    {
                        releaseYield = true;
                        diagReleaseReason = $"③ 救護車在後方 {toAmbR.magnitude:F1}m > {ambulanceBehindReleaseDistance}m 且沒朝我開";
                    }
                }
            }

            // 🔧 逾時保險:只在「救護車真的卡死在我後方」時啟動(死鎖破解用)。
            //    它正常通過中(還在移動)不強制解除,否則長車陣會讓到一半突然切回車道。
            bool ambStalledBehind = false;
            if (activeAmbulanceTransform != null)
            {
                Vector3 toAmbT = activeAmbulanceTransform.position - transform.position;
                var ambAI = activeAmbulanceTransform.GetComponentInParent<ReverseThreatAICar>();
                float ambSpdT = ambAI != null ? ambAI.CurrentHealthSpeed : 999f;
                // 🔧 加上距離條件:救護車「停住」不代表死鎖,它可能只是被別的車暫時擋住。
                //    以前只看速度,救護車一停 12 秒後所有讓道中的灰車就集體放棄避讓,
                //    結果更沒人讓路 → 救護車更卡,形成惡性循環。
                //    只有救護車「就緊貼在我後方卻完全動不了」才算真的被我卡死。
                ambStalledBehind = Vector3.Dot(transform.forward, toAmbT.normalized) <= 0f
                                   && ambSpdT < 0.5f
                                   && toAmbT.magnitude < ambulancePassClearance;
            }
            if (!releaseYield && ambStalledBehind && ambulanceYieldTimeout > 0f
                && Time.time - yieldStartTime > ambulanceYieldTimeout)
            {
                releaseYield = true;
                diagReleaseReason = $"④ ★逾時保險:救護車貼在我後方卻不動超過 {ambulanceYieldTimeout}s,強制解除並冷卻 {ambulanceYieldCooldown}s";
                // 🔧 進入冷卻,否則下一幀又被警笛廣播設回避讓狀態
                yieldCooldownUntil = Time.time + ambulanceYieldCooldown;
                Debug.LogWarning($"<color=orange>🚑 {name} 避讓已超過 {ambulanceYieldTimeout}s 無進展,強制解除並冷卻 {ambulanceYieldCooldown}s 以避免死鎖</color>");
            }

            if (releaseYield)
            {
                diagReleaseTime = Time.time;
                isYieldingForAmbulance = false;
                activeAmbulanceTransform = null;
                yieldStartTime = -1f;
                returnStartedTime = Time.time;   // 🤝 進入「回歸禮讓」時間窗

                // 🔧 避讓期間車子沿路邊往前滑了一段,futureNodes[0] 可能已經落在車身後方。
                //    不處理的話回歸時會先往回折返去踩那個點,看起來就是「先回到原本切出去的位置」。
                //    把已經在背後的節點跳掉,直接瞄準前方的下一個點,才會順順地斜切回車道。
                for (int skip = 0; skip < 4; skip++)
                {
                    if (futureNodes[0] == null) break;
                    Vector3 toNode = futureNodes[0].transform.position - transform.position;
                    toNode.y = 0f;
                    // 節點還在前方(或幾乎重疊)→ 不用再跳
                    if (toNode.sqrMagnitude < 0.01f) break;
                    if (Vector3.Dot(transform.forward, toNode.normalized) >= 0f) break;

                    lastWaypointNode = futureNodes[0];
                    AdvanceAndReplenishPath();
                }
            }
        }
        else yieldStartTime = -1f;
        if (isYieldingForAmbulance) isWaitingAtTrafficLight = false; // 避讓時無視紅燈
        else isPulledOverForAmbulance = false;

        // 避讓偏移量(救護車還卡在後方 → 二次偏移)
        // ⚠️ 只有「還沒開始偏移」時才受轉彎/路口限制;已經偏出去了就必須維持,
        //    否則車在路口/轉彎中會把偏移歸零 → 提前滑回車道跟救護車搶道
        float yieldOffsetTarget = 0f;
        // 🔧 「轉彎中」不該完全放棄避讓。
        //    舊版 canStartYield 要求 !isBezierActive,已經在走貝茲曲線的車偏移目標恆為 0,
        //    等於想轉彎的灰車完全不讓救護車。正確順序是「先讓完再轉」——
        //    blockTurnForAmbulance 會擋住「還沒開始」的轉彎,
        //    而已經在轉的車:還沒進路口就停下來靠邊讓,進路口了就先開出去(clearJunction 那條)。
        // (原本寫成 !bezierBlocksYield && !isInIntersectionZone,但那等價於只看 isInIntersectionZone,
        //  多出來的那一項永遠不影響結果 —— 直接寫清楚)
        bool canStartYield = !isInIntersectionZone;
        if (isYieldingForAmbulance && (canStartYield || currentYieldOffset > 0.3f))
        {
            yieldOffsetTarget = ambulanceYieldOffset;

            // 🚑 二次偏移:必須「第一段已靠邊停穩」+「救護車還在後方近距離」+「右側真的還有空間」
            if (isPulledOverForAmbulance
                && activeAmbulanceTransform != null && currentWaypointNode != null && lastWaypointNode != null)
            {
                Vector3 fwdY = currentWaypointNode.transform.position - lastWaypointNode.transform.position; fwdY.y = 0f;
                Vector3 toAmbY = activeAmbulanceTransform.position - transform.position; toAmbY.y = 0f;
                bool ambStillBehind = fwdY.sqrMagnitude > 0.01f && toAmbY.magnitude < 18f
                                      && Vector3.Dot(fwdY.normalized, toAmbY.normalized) < 0.3f;

                // 🚑 只有救護車「真的停住」才算被我卡住(路口減速觀察不算),避免誤判一直偏移
                var ambAI = activeAmbulanceTransform.GetComponentInParent<ReverseThreatAICar>();
                float ambSpd = ambAI != null ? ambAI.CurrentHealthSpeed : 0f;
                if (ambStillBehind && ambSpd < ambulanceStuckSpeed) ambBlockedTimer += Time.deltaTime;
                else ambBlockedTimer = 0f;

                if (ambStillBehind && ambBlockedTimer > ambulanceStuckTime)
                {
                    // 右側與右前方都要淨空,才敢再往邊邊挪(免得撞到路邊的車)
                    Vector3 rightY = Vector3.Cross(Vector3.up, fwdY.normalized);
                    float need = ambulanceYieldOffsetMax - currentYieldOffset + 1.0f;
                    bool sideClear = !(Physics.Raycast(transform.position + Vector3.up * 0.5f, rightY,
                                          out RaycastHit sHitY, need, carLayer, QueryTriggerInteraction.Ignore)
                                       && sHitY.collider.transform.root != transform.root);
                    Vector3 diagY = (fwdY.normalized + rightY).normalized;
                    bool diagClear = !(Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.7f, diagY,
                                          out RaycastHit dHitY, need + 2f, carLayer, QueryTriggerInteraction.Ignore)
                                       && dHitY.collider.transform.root != transform.root);

                    if (sideClear && diagClear)
                    {
                        yieldOffsetTarget = ambulanceYieldOffsetMax;
                        isPulledOverForAmbulance = false;   // 解除停穩鎖,讓車再滑一段
                    }
                }
            }
        }
        currentYieldOffset = Mathf.MoveTowards(currentYieldOffset, yieldOffsetTarget, 1.2f * Time.deltaTime);

        // 🔧 避讓/回歸診斷:勾選 Inspector 的 Debug Log Detection 才會印,每秒最多一次。
        //    回歸階段也要印 —— 「救護車走了卻不回主車道」只有在這個階段才看得到原因。
        bool inReturnPhase = !isYieldingForAmbulance && IsReturningFromYieldNow;
        if (debugLogDetection && (isYieldingForAmbulance || inReturnPhase)
            && Time.time - lastYieldDiagTime > 1f)
        {
            lastYieldDiagTime = Time.time;

            string ambInfo;
            if (activeAmbulanceTransform == null)
            {
                ambInfo = "救護車=★null(已離場或被銷毀)";
            }
            else
            {
                Vector3 toA = activeAmbulanceTransform.position - transform.position; toA.y = 0f;
                float dotA = toA.sqrMagnitude > 0.01f ? Vector3.Dot(transform.forward, toA.normalized) : 0f;
                string where = dotA > 0f ? "在我前方" : "在我後方";
                string cond = dotA > 0f
                    ? $"需要 >{ambulancePassClearance}m 才解除"
                    : $"需要 >{ambulanceBehindReleaseDistance}m 且沒朝我開才解除";
                ambInfo = $"救護車={activeAmbulanceTransform.name}  距離={toA.magnitude:F1}m  {where}(dot={dotA:F2})\n" +
                          $"   解除條件:{cond}" +
                          $"{(dotA > 0f && toA.magnitude > ambulancePassClearance ? "  → ★已滿足,這一幀就該解除" : "  → 尚未滿足")}";
            }

            if (isYieldingForAmbulance)
            {
                Debug.Log($"<color=cyan>[灰車避讓]</color> {name}  狀態=避讓中\n" +
                    $"   {ambInfo}\n" +
                    $"   在路口內={isInIntersectionZone}  轉彎中={isBezierActive}\n" +
                    $"   已靠邊停穩={isPulledOverForAmbulance}  被救護車擋轉彎={blockTurnForAmbulance}\n" +
                    $"   靠邊相位={diagYieldPhase}  已偏移={diagLateral:F2}m" +
                    $"  目標(原始/平滑)={diagRawTarget:F2}/{diagSmoothTarget:F2}m" +
                    $"  前方被擋={wcFrontBlocked}\n" +
                    $"   二次偏移條件: 在後方={diagAmbBehind}(需 true)" +
                    $"  距離={diagAmbDist:F1}m(需 <18)" +
                    $"  救護車速={diagAmbSpd:F1}(需 <{wcAmbStuckSpeed:F1})" +
                    $"  救護車回報被擋={diagAmbBlocked}" +
                    $"  已累積={diagBlockedTimer:F1}/{wcAmbStuckTime:F1}s\n" +
                    $"   上次解除:{diagReleaseReason}" +
                    $"  ({(diagReleaseTime < 0f ? "從未" : (Time.time - diagReleaseTime).ToString("F1") + " 秒前")})" +
                    $"  冷卻中={(Time.time < yieldCooldownUntil ? $"是,剩 {yieldCooldownUntil - Time.time:F1}s" : "否")}" +
                    $"  本輪已避讓={(yieldStartTime < 0f ? "-" : (Time.time - yieldStartTime).ToString("F1") + "s")}");
            }
            else
            {
                Debug.Log($"<color=lime>[灰車回歸]</color> {name}  狀態=已解除避讓,回歸中\n" +
                    $"   距解除 {Time.time - returnStartedTime:F1} 秒 (禮讓窗 {returnYieldWindow}s)" +
                    $"  殘留偏移={currentYieldOffset:F2}\n" +
                    $"   禮讓主車道中={isMergeHolding}" +
                    $"{(isMergeHolding ? $"  ★ 已等 {mergeHoldTimer:F1}/{mergeHoldTimeout}s" : "")}" +
                    $"  目標節點={(futureNodes[0] != null ? futureNodes[0].name : "★null")}\n" +
                    $"   目標在我{(futureNodes[0] != null && Vector3.Dot(transform.forward, (futureNodes[0].transform.position - transform.position).normalized) < 0f ? "★後方(會往回折返)" : "前方")}" +
                    $"  速度={realTimeSpeed:F2}");
            }
        }

        // 🤝 回歸主車道前禮讓:剛解除避讓的時間窗內,左側(主車道)有「還在動」的車 → 原地等,
        //    不把車頭伸進去擋路;等它開過去出現縫隙再切。逾時保底避免永遠等不到
        isMergeHolding = false;
        if (Time.time - returnStartedTime < returnYieldWindow && currentYieldOffset > 0.2f)
        {
            Vector3 mergeCenter = transform.position + Vector3.up * 0.5f
                                  - transform.right * 2.2f - transform.forward * 2f;
            foreach (var lc in Physics.OverlapBox(mergeCenter, new Vector3(1.8f, 1f, 9f), transform.rotation, carLayer))
            {
                if (lc.transform.root == transform.root) continue;
                var oGray = lc.GetComponentInParent<CarNavigation>();
                var oWhite = oGray == null ? lc.GetComponentInParent<ReverseThreatAICar>() : null;
                float oSpd = oGray != null ? oGray.RealTimeSpeed
                           : (oWhite != null ? oWhite.CurrentHealthSpeed : -1f);
                if (oSpd > 1f) { isMergeHolding = true; break; }   // 主車道有車在動 → 讓它先走
            }

            if (isMergeHolding)
            {
                mergeHoldTimer += Time.deltaTime;
                if (mergeHoldTimer > mergeHoldTimeout) isMergeHolding = false;   // 逾時強制切回
                else currentYieldOffset = Mathf.Max(currentYieldOffset, 0.3f);   // 維持在路邊不回切
            }
            else mergeHoldTimer = 0f;
        }
        else mergeHoldTimer = 0f;

        // 🚑 避讓中且救護車還在附近(未通過 clearance)→ 禁止進入轉彎,免得橫切到救護車路徑上
        // 【舊版】blockTurnForAmbulance = isYieldingForAmbulance && activeAmbulanceTransform != null
        //             && Vector3.Distance(transform.position, activeAmbulanceTransform.position) < ambulancePassClearance;
        // 🔧 新增方向判斷:救護車在我「正後方」時不再擋我轉彎。
        //    我擋在它前面卻停著不轉只會把車道堵死;讓我轉完離開,車道反而清得更快。
        //    只有救護車在前方/側面(轉彎會橫切到它的路徑)才需要擋。
        blockTurnForAmbulance = false;
        if (isYieldingForAmbulance && activeAmbulanceTransform != null)
        {
            Vector3 toAmbT = activeAmbulanceTransform.position - transform.position;
            toAmbT.y = 0f;
            if (toAmbT.sqrMagnitude > 0.01f)
            {
                bool nearEnough = toAmbT.magnitude < ambulancePassClearance;
                bool ambulanceBehind = Vector3.Dot(transform.forward, toAmbT.normalized) < ambulanceBehindDot;

                // 🔧 只有「救護車在我正後方 + 跟我同向(它在追我、想從我這條路超過去)」才放行轉彎;
                //    若它是橫向/對向過來的(十字路口交會),就算位置在我後方也必須擋,否則我一轉就切進它的路徑
                Vector3 ambFwd = activeAmbulanceTransform.forward; ambFwd.y = 0f;
                bool sameDirection = ambFwd.sqrMagnitude > 0.01f
                                     && Vector3.Dot(transform.forward, ambFwd.normalized) > 0.5f;

                // 🔧 方案 B:把「擋轉彎」與「已在路口」拆開判斷。
                //
                //    舊版:救護車在正後方且同向 → 一律不擋轉彎。
                //    立意是「讓我轉完離開,車道清得更快」,但那正是最常見的情況
                //    (救護車從後方追上來),結果 blockTurnForAmbulance 恆為 false
                //    → 貝茲曲線不會被取消 → 偏移的套用在非轉彎分支永遠進不去
                //    → 灰車照常轉彎,完全沒有避讓動作。
                //
                //    新版:只有「已經開進路口」才不擋(那時停下來反而堵死路口,
                //    該做的是趕快轉完離開)。還沒進路口的車一律擋下轉彎,
                //    先靠邊讓完,救護車過去後再轉 —— 也就是「先避讓再轉彎」。
                bool alreadyInsideJunction = isInIntersectionZone;
                blockTurnForAmbulance = nearEnough && !alreadyInsideJunction;

                // 保留原本的方向判斷結果供除錯參考(目前不影響行為)
                if (debugLogDetection && nearEnough && ambulanceBehind && sameDirection && alreadyInsideJunction)
                    Debug.Log($"<color=cyan>🚑</color> {name}:已在路口內且救護車在後方同向 → 不擋轉彎,趕快轉完離開");
            }
        }

        // 🚑 路口快速淨空:救護車在我後方 + 我在路口內 + 我還沒靠邊 → 加速開出路口,別堵在路中央
        //    (已經靠邊避讓的車不適用,它要留在原地等救護車通過 ambulancePassClearance 再回歸)
        clearJunctionForAmbulance = isYieldingForAmbulance
            && activeAmbulanceTransform != null
            && isInIntersectionZone
            && !isPulledOverForAmbulance
            && currentYieldOffset < 0.5f
            && Vector3.Dot(transform.forward,
                   (activeAmbulanceTransform.position - transform.position).normalized) < ambulanceBehindDot;

        HandleAccidentBypass();                     // 🚧 故障車三段式繞行

        // 🚧 走進封鎖節點的車:在玩家「後方」→ 縮小淡出退場(玩家看不到);
        //    在玩家「前方」→ 不退場,交給故障車三段式繞行,才不會眼前憑空消失
        if (AccidentDirector.IsAccidentActive && currentWaypointNode != null
            && AccidentDirector.BlockedNodes != null
            && AccidentDirector.BlockedNodes.Contains(currentWaypointNode)
            && playerTransformForStuck != null)
        {
            Vector3 toMeB = transform.position - playerTransformForStuck.position; toMeB.y = 0f;

            // 🛡 第一道:離玩家夠近就一律不淡出,不管前後。
            //    方向判定再怎麼寫都可能出錯,但「玩家眼前這個距離內的車不准憑空消失」
            //    是一條不依賴任何方向的硬規則 —— 有它在,方向就算判錯,最糟也只是
            //    遠處少一台車,不會出現眼前蒸發。實測就是對向車開過故障車就不見了。
            if (toMeB.magnitude < blockedFadeSafeRadius)
            {
                // 什麼都不做,交給 HandleAccidentBypass() 繞過去
            }
            // 🧭 第二道:方向判定改用「實際行進方向」,不用 playerTransformForStuck.forward。
            //    那個 Transform 是靠 Player tag 找到的碰撞體物件,它的 Z 軸不保證是車頭方向
            //    (玩家車階層 RRRR→SSS→CAR→vehicle 中間有 180 度旋轉)。
            //    forward 反了的話這個內積整個顛倒:後方的車留著、前方的車淡出。
            //    速度方向沒有階層問題,車往哪開就是哪個方向;太慢時才退回用 forward。
            else if (Vector3.Dot(PlayerHeadingForBlockedFade(), toMeB) < 0f)
            {
                ForceFadeOutClear();   // 玩家後方 → 淡出
                return;
            }
            // 玩家前方 → 什麼都不做,繼續往下走 HandleAccidentBypass() 繞過去
        }

        CheckPlayerReverseImmediate();              // 玩家倒車 → 連鎖閃避
        HandlePlayerBlockDetection();               // 🎺 玩家擋在正前方不動 → 按喇叭催
        CheckCloseReversingCar();                   // 前車倒車 → 連鎖閃避

        // 🚧 事故中:已抽好的未來路徑含封閉節點就重抽,繞開
        if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedNodes.Count > 0 && !isBezierActive)
        {
            for (int bi = 1; bi < futureNodes.Length; bi++)
            {
                if (futureNodes[bi] != null && AccidentDirector.BlockedNodes.Contains(futureNodes[bi]))
                {
                    for (int bj = bi; bj < futureNodes.Length; bj++)
                        futureNodes[bj] = PickRandomChoice(futureNodes[bj - 1]);
                    break;
                }
            }
        }

        // 如果正在最後直行模式，只執行移動，跳過所有導航與雷達邏輯
        if (isFinishingPath)
        {
            MoveCar(smoothDrive);
            return;
        }

        // 🚑 救護車避讓靠邊(與白車同一套):接管移動後直接 return,
        //    不讓轉彎/跟車/路口那些邏輯來干擾靠邊動作。
        if (HandleWhiteCarStyleYield()) return;

        if (currentWaypointNode == null) return;
        // 每一幀初始化區域變數，預設為「未被阻擋」
        currentlyBlockedByRay = false;
        filterBlockedRight = false;
        filterBlockedLeft = false;
        waitingForOppositeStraightCar = false;
        // 根據是否為機車，選擇適當的雷達前移距離
        float radarFwd = isScooter ? scooterRadarForwardOffset : radarForwardOffset;
        Vector3 rayOrigin = transform.position + Vector3.up * 0.5f + transform.forward * radarFwd;
        // 將 distToNode 移到最外層，讓下方所有階段都能讀取
        float distToNode = futureNodes[0] != null ?
        Vector3.Distance(transform.position, futureNodes[0].transform.position) : 999f;
        // 將 targetPower 宣告移到最前面 (預設為雷達偵測到的動力值)
        // 我們先初始化為 1.0，稍後在雷達與路口邏輯中修改它
        float targetPower = 1.0f;
        // ---------------------------------------------------------
        // 階段 1：判定轉彎狀態 (Am I Turning?)
        // 目的：決定是否啟動「左轉讓直行」偵測，以及改變扇形雷達的掃描寬度
        // ---------------------------------------------------------
        // 狀態 A：執行階段 (Execution Phase)
        // 貝茲執行中，維持 amILeftTurning 狀態以保證偵測框持續存在
        if (isBezierActive)
        {
            //Debug.Log($"<color=orange>[彎道監控]</color> " +
            // $"車速: {realTimeSpeed:F1} | " +
            // $"動力輸出(smoothDrive): {smoothDrive:F2} | " +
            // $"貝茲進度(T): {bezierT:F2} | " +
            // $"正在轉向(amILeftTurning): {amILeftTurning}");
            //amILeftTurning = shouldShowLeftTurnBox;
        }
        // 狀態 B：預判階段 (Prediction Phase)
        // 1. 沒在轉彎時(直線行駛中)，才需要「預判」前方彎道並減速
        // 2. 已經在轉彎時(貝茲啟動中)，就專心執行轉彎，不再重複計算減速
        else if (!isPathCompleted && futureNodes[0] != null)
        {
            bool detectedTurn = false;
            bool detectedLeftTurn = false;

            // 完全使用節點與節點之間的路徑向量，不使用 transform.position(會被機車橫向偏移污染)
            // 前一段：優先用 lastWaypointNode -> futureNodes[0]；沒有 lastWaypointNode 時才退而求其次用車輛位置
            Vector3 dirIncoming;
            if (lastWaypointNode != null)
            {
                dirIncoming = (futureNodes[0].transform.position - lastWaypointNode.transform.position).normalized;
            }
            else
            {
                dirIncoming = (futureNodes[0].transform.position - transform.position).normalized;
            }

            if (futureNodes[1] != null)
            {
                Vector3 dirNode0ToNode1 = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
                float pathAngle = Vector3.Angle(dirIncoming, dirNode0ToNode1);
                if (pathAngle > 10f)
                {
                    detectedTurn = true;
                    Vector3 cross = Vector3.Cross(dirIncoming, dirNode0ToNode1);
                    detectedLeftTurn = cross.y < 0;
                }
            }

            amILeftTurning = detectedTurn && detectedLeftTurn;
            amIRightTurning = detectedTurn && !detectedLeftTurn;
        }
        // ---------------------------------------------------------
        // 階段 2：左轉讓直行邏輯 (Yield to Opposite Straight Traffic)
        // 核心邏輯：建立一個虛擬的「偵測盒」去掃描對向車道是否有直行車
        // ---------------------------------------------------------
        if (leftTurnYieldCooldown > 0f)
        {
            leftTurnYieldCooldown -= Time.deltaTime;
        }
        // 核心改動：距離判斷 distToNextNode < turnYieldDistance(離路口多近檢查對向來車)
        float distToNextNode = futureNodes[0] != null ? Vector3.Distance(transform.position, futureNodes[0].transform.position) : 999f;
        // 啟動條件：判定為左轉 且 (距離路口近 或 剛進入貝茲曲線)
        // 若目標節點勾選了 disableLeftTurnYield，則強制不啟動讓直行檢查
        nodeDisablesYield = (futureNodes[0] != null && futureNodes[0].disableLeftTurnYield);
        shouldCheckYield = amILeftTurning && !nodeDisablesYield &&
                        (leftTurnYieldCooldown <= 0f) &&
                        // 🔧 檢查窗口從 15% 拉到 60%:原本轉彎開始約 0.3 秒後就不再看對向車,
                        //    對向車在那之後才進入偵測框就完全不會停 → 直接轉過去。
                        //    超過 60% 已經轉進路口深處,停在路口中央反而更危險,維持轉完再說。
                        (!isBezierActive || bezierT < 0.6f) &&
                        (distToNextNode < turnYieldDistance || bezierT < 0.6f);
        if (shouldCheckYield)
        {
            //Debug.Log("啟動左轉讓直行偵測原因: " +
            //                $"amILeftTurning: {amILeftTurning}, " +
            //                $"isBezierActive: {isBezierActive}, " +
            //                $"bezierT: {bezierT}, " +
            //                $"distToNextNode: {distToNextNode}");
            // A. 計算偵測盒(OverlapBox)之中心點：
            // 將 Local 偏移量轉換為 World 座標，確保偵測區域隨車頭旋轉
            // (左右偏移植、離地高度、前方偏移)
            Vector3 localCenter = new Vector3(turnDetectionSideOffset, 0.5f, 2.0f + turnForwardOffset);
            Vector3 checkPos = transform.TransformPoint(localCenter);

            // B. 物理碰撞偵測：獲取範圍內所有車輛層級(carLayer)的物體
            Collider[] colliders = Physics.OverlapBox(checkPos, turnBoxSize / 2f, transform.rotation, carLayer);
            foreach (var col in colliders)
            {
                if (col.gameObject == this.gameObject) continue; // 排除自己

                CarNavigation otherCar = col.GetComponentInParent<CarNavigation>();
                if (otherCar == this) continue;   // GetComponentInParent 會把自己的子物件 collider 解析成自己

                // 🔧 對向來車偵測補漏(只擴大「看得到誰」,停讓判定門檻與後續流程完全沒動):
                //    (a) 原本只認 CarNavigation → 白車與救護車在這個檢查裡完全隱形,
                //        灰車左轉會直接切過對向白車/救護車的車頭。
                //    (b) 原本用 GetComponent → collider 掛在子物件上時連對向灰車都抓不到,
                //        改用 GetComponentInParent(白車那支的同一段邏輯一直都是這樣寫)。
                ReverseThreatAICar otherThreat = otherCar == null ? col.GetComponentInParent<ReverseThreatAICar>() : null;

                Transform otherTf = otherCar != null ? otherCar.transform
                                  : (otherThreat != null ? otherThreat.transform : null);
                if (otherTf != null)
                {
                    float directionDot = Vector3.Dot(transform.forward, otherTf.forward);
                    float distanceToOther = Vector3.Distance(transform.position, otherTf.position);

                    // 灰車沿用原本的量測(紅燈旗標 + smoothDrive 動力);白車/救護車沒有這兩個變數,
                    // 改用它自己的紅燈查詢與實際速度。判定門檻(對向 dot、6m 死鎖豁免、1m/s)兩邊相同。
                    bool otherTurningLeft = otherCar != null ? otherCar.IntendsToLeftTurnEarly()
                                                             : otherThreat.IntendsToLeftTurnEarly();
                    float otherSpd = otherCar != null ? otherCar.RealTimeSpeed
                                                      : otherThreat.CurrentHealthSpeed;
                    bool otherStopping = otherCar != null
                        ? (otherCar.isWaitingAtTrafficLight || otherCar.smoothDrive < 0.05f)
                        : (otherThreat.IsWaitingAtTrafficLight() || otherSpd < 1.0f);
                    // C. 停讓判定準則：
                    // 1. 利用內積 (Dot Product) 判定對方是否為對向車 (Dot < -0.5)
                    // 2. 對方必須是直行車 (!amILeftTurning) 或右轉車
                    // 3. 距離需大於安全邊際 (6.0m)，避免在路口中心點發生死鎖 (Deadlock)
                    // 4.如果對方沒有在等紅燈(isWaitingAtTrafficLight)
                    // 5. 如果對方動力極低 (smoothDrive < 0.1f，代表在減速或準備停車)
                    if (directionDot < -0.5f && !otherTurningLeft
                    // 🔧 原本用 distanceToOther > 6.0f 無條件忽略近距離的車(為了防路口中心死鎖),
                    //    但那等於把「已經衝到眼前」這個最危險的情況也一併跳過 → 照樣轉過去。
                    //    改成只有「近距離且對方也停著」才忽略 —— 那才是真的互相讓到死鎖,需要有人先走;
                    //    對方還在動就代表它正衝過來,更應該讓。
                    && !(distanceToOther <= 6.0f && otherSpd < 1.0f) &&
                    !otherStopping)
                    {
                        waitingForOppositeStraightCar = true; // 判定需等待
                        isLeftTurnBlockedStopping = true;     // 【新增】點亮左轉受阻煞停燈！
                        if (hasCheckedIntersection)
                        {
                            hasCheckedIntersection = false;
                            // 強制點亮受阻煞停鎖
                            isLeftTurnBlockedStopping = true;
                        }
                        // 如果它本來就還沒完成首檢，但又被對向車擋住，也要鎖定
                        else if (waitingForOppositeStraightCar)
                        {
                            isLeftTurnBlockedStopping = true;
                        }
                        //if (waitingForOppositeStraightCar)
                        //{
                        //    //Debug.Log($"<color=red>[讓路觸發]</color> 偵測到對向直行車：{col.gameObject.name}，距離：{Vector3.Distance(transform.position, col.transform.position):F1}");
                        //}
                        break; // 只要有一台要讓，就跳出循環
                    }
                }
            }
        }

        // ---------------------------------------------------------
        // 階段 3：扇形雷達偵測 (Forward Raycast Radar)
        // 目的：處理一般路段的「跟車」與「防撞」
        // ---------------------------------------------------------
        float activeFanAngle = GetCurrentRadarAngle(); // 轉彎用寬角，直行用窄角
        float activeDetectionDistance = GetCurrentDetectionDistance(); // 直行看更遠
        float startAngle = -activeFanAngle * 0.5f;
        float angleStep = activeFanAngle / (numberOfRays - 1);

        if (amILeftTurning || amIRightTurning || isBezierActive)
        {
            activeDetectionDistance = 40.0f;
        }

        finalDrivePower = 1.0f; // 初始動力為滿載（1 = 全速, 0 = 停止）
        // 【機車專用】：若機車已經在專用道上，就不需要再鑽車並列
        bool inScooterLaneNow = futureNodes[0] != null && futureNodes[0].isScooterLane;

        for (int i = 0; i < numberOfRays; i++)
        {
            // B. 計算單條射線的方向與長度
            float currentAngle = startAngle + (angleStep * i);
            Quaternion rotation = Quaternion.AngleAxis(currentAngle, transform.up);
            Vector3 rayDirection = rotation * transform.forward;

            // C. 側邊衰減 (Side Attenuation)：側邊的射線長度會比正中央短，符合駕駛視角習慣
            float sideFactor = Mathf.Abs(currentAngle) / (activeFanAngle * 0.5f);
            float rayDistance = activeDetectionDistance * (1.0f - sideFactor * lateralDetectionMult);

            // 如果是直行狀態，且目前這條射線是正中間 (角度接近 0)
            if ((!amILeftTurning && !amIRightTurning) && Mathf.Abs(currentAngle) < 1.0f)
            {
                rayDistance *= straightDetectionMult; // 讓中間這條再長 1.5 倍 (例如從 10m 變成 15m)
            }
            // D. 執行射線碰撞判定
            if (Physics.Raycast(rayOrigin, rayDirection, out RaycastHit hit, rayDistance, carLayer))
            {
                if (hit.collider.gameObject != this.gameObject) // 排除自己
                {
                    // 🚗 路邊靜停、還沒被觸發的側切車 → 當停放車輛忽略,不跟車不煞停
                    ReverseThreatAICar parkedCheck = hit.collider.GetComponentInParent<ReverseThreatAICar>();
                    if (parkedCheck != null && parkedCheck.isStaticParked) continue;

                    // 🔧 在隔壁讓道中的車不算擋路(主車道有路權),否則兩台互相偵測會死鎖
                    if (IsBesideYieldingVehicle(hit.collider)) continue;

                    // 🚧 已在繞行中 → 完全忽略故障車本體與路障,否則會被煞停而動不了、切不出去
                    if (IsBypassingAccident
                        && (hit.collider.CompareTag("RoadBlock")
                            || hit.collider.GetComponentInParent<HazardLights>() != null))
                        continue;

                    // 🚑 避讓中打到救護車本體 → 減速但不煞死,讓靠邊偏移做得完
                    if (isYieldingForAmbulance && hit.collider.GetComponentInParent<AmbulanceAI>() != null)
                    {
                        if (finalDrivePower > 0.3f) finalDrivePower = 0.3f;
                        continue;
                    }

                    // 🚧 三角錐/路障 → 依距離減速;已在繞行中就不再為它煞停
                    if (hit.collider.CompareTag("RoadBlock"))
                    {
                        if (IsBypassingAccident) continue;
                        float blockPower = (hit.distance <= accidentStopDistance) ? 0f : Mathf.Clamp01((hit.distance - accidentStopDistance) / 10f);
                        if (blockPower < finalDrivePower) finalDrivePower = blockPower;
                        continue;
                    }

                    // 🚗 連鎖倒車:前車正在倒車閃避且已逼近 → 我也跟著退
                    if (!isForcedReversing && hit.distance <= playerSafeDistance + 1.5f)
                    {
                        // ⚠️ 跟隨玩家倒車的車不算「前車在倒車」——理由同 3340 那條：
                        //    廣播器會把跟隨車設 isForcedReversing=true，但它是逐幀跟著玩家位移、
                        //    玩家切 D 由廣播器一處收掉。少了這道判斷，後車會誤把它當成倒車中、
                        //    自己接手固定速度自走式連鎖，導致玩家切回 D 後這些車還在退。
                        ReverseThreatAICar threatAhead = hit.collider.GetComponentInParent<ReverseThreatAICar>();
                        if (threatAhead != null && (threatAhead.isReverseAction || threatAhead.isForcedReversing)
                            && !threatAhead.isFollowingPlayerReverse)
                            StartCoroutine(ExecuteChainReverseFromCar(threatAhead.transform));
                        else
                        {
                            CarNavigation navAhead = hit.collider.GetComponentInParent<CarNavigation>();
                            if (navAhead != null && navAhead != this && navAhead.IsForcedReversing
                                && !navAhead.isFollowingPlayerReverse)
                                StartCoroutine(ExecuteChainReverseFromCar(navAhead.transform));
                        }
                    }

                    float thisRayPower = 1.0f; // 預設這條射線是滿動力的
                    // 取得對方車輛元件
                    CarNavigation otherCar = hit.collider.GetComponentInParent<CarNavigation>();
                    if (otherCar == this) otherCar = null;

                    // 🚗 打到的不是灰車(白車/救護車/玩家)→ 也要跟車減速,否則會直接撞上去
                    if (otherCar == null)
                    {
                        ReverseThreatAICar otherThreat = hit.collider.GetComponentInParent<ReverseThreatAICar>();
                        bool otherIsPlayer = hit.collider.CompareTag("Player") || hit.collider.transform.root.CompareTag("Player");
                        // 🚗[危險駕駛AI整合] 鬼切/路霸演員車原本不在白名單裡 → 灰車看不到它、直接穿過去
                        ITrafficSpeedSource otherExt = (otherThreat == null && !otherIsPlayer) ? ExternalTrafficVehicle.Get(hit.collider) : null;
                        if (otherThreat != null || otherIsPlayer || otherExt != null)
                        {
                            Vector3 otherFwd = otherThreat != null ? otherThreat.transform.forward
                                             : otherExt != null ? otherExt.VehicleTransform.forward
                                             : hit.collider.transform.root.forward;
                            float hDot = Vector3.Dot(transform.forward, otherFwd);
                            bool sideRayN = Mathf.Abs(currentAngle) > (activeFanAngle * 0.25f);

                            // 🛵 機車:對方同向、明顯偏在側邊、且對方低速/排隊中 → 可以鑽過去
                            Vector3 toOtherN = hit.collider.transform.position - transform.position; toOtherN.y = 0f;
                            float latDotN = toOtherN.sqrMagnitude > 0.0001f ? Vector3.Dot(toOtherN.normalized, transform.right) : 0f;
                            float otherSpdN = otherThreat != null ? otherThreat.CurrentHealthSpeed
                                            : otherExt != null ? otherExt.CurrentSpeed : 0f; // 🚗[危險駕駛AI整合]
                            // 🛵 轉彎中不准鑽車：上面的「對方在我側邊」是用 transform.right 量的，
                            //    而轉彎時車頭一直在轉，弧線正前方的車會被誤判成「在側邊」→ 完全不減速 → 撞上去。
                            //    鑽車本來就是直線低速穿車陣的行為，過彎時沒有意義。
                            if (isScooter && !isBezierActive && hDot > 0.7f && Mathf.Abs(latDotN) > 0.4f
                                && hit.distance > 1.2f && (isWaitingAtTrafficLight || otherSpdN < 3f))
                            {
                                thisRayPower = 1.0f;                 // 機車鑽車放行
                                if (latDotN > 0f) filterBlockedRight = true; else filterBlockedLeft = true;
                            }
                            else if (hDot < 0f && sideRayN && !MustYieldToRightTurner(hit.collider))
                            {
                                thisRayPower = 1.0f;                 // 側線掃到的對向車,不減速
                            }
                            else if (hit.distance <= followStopDistance)
                            {
                                currentlyBlockedByRay = true;
                                thisRayPower = 0f;                   // 逼近安全距離 → 硬煞停
                            }
                            else
                            {
                                float otherSpd = otherThreat != null ? otherThreat.CurrentHealthSpeed
                                               : otherExt != null ? otherExt.CurrentSpeed : 5f; // 🚗[危險駕駛AI整合]
                                float dynMax = otherSpd > 3f ? Mathf.Max(10f, rayDistance * 0.4f) : rayDistance;
                                if (hit.distance <= dynMax)
                                    thisRayPower = Mathf.Clamp01((hit.distance - followStopDistance) / Mathf.Max(dynMax - followStopDistance, 0.1f));
                            }

                            if (thisRayPower < finalDrivePower) finalDrivePower = thisRayPower;
                            continue;
                        }
                    }

                    // 確保偵測到其他車輛，距離大於 0.1m（避免貼在一起的車互相干擾），才啟動減速
                    if (otherCar != null) //&& hit.distance > 0.1f
                    {
                        // 計算車頭夾角（內積）：1 同向, 0 垂直, -1 對向
                        float headingDot = Vector3.Dot(transform.forward, otherCar.transform.forward);
                        // 判斷是否為邊緣側射線
                        bool isSideRay = Mathf.Abs(currentAngle) > (activeFanAngle * 0.25f);
                        // 【機車專用】機車並排放行判斷
                        // 條件：我自己與對方都是機車、方向大致相同、且對方明顯位在我左側或右側
                        bool scooterSideBySidePass = false;
                        Vector3 toOther = otherCar.transform.position - transform.position;
                        toOther.y = 0f;
                        float lateralDot = 0f;
                        if (toOther.sqrMagnitude > 0.0001f)
                        {
                            lateralDot = Vector3.Dot(toOther.normalized, transform.right); // 計算對方在我左邊還是右邊
                        }
                        // 不依賴會被轉彎fan角度影響的 isSideRay，改用「對方是否明顯偏在我左或右側」判斷
                        bool isLateralPosition = Mathf.Abs(lateralDot) > 0.4f;
                        // 當雙方皆為機車、同向行駛 (>0.7) 且對方位於偏側邊時
                        // 🛵 轉彎中不准鑽車：上面的「對方在我側邊」是用 transform.right 量的，
                        //    而轉彎時車頭一直在轉，弧線正前方的車會被誤判成「在側邊」→ 完全不減速 → 撞上去。
                        //    鑽車本來就是直線低速穿車陣的行為，過彎時沒有意義。
                        if (isScooter && !isBezierActive && !inScooterLaneNow && headingDot > 0.7f && isLateralPosition)
                        {
                            // 🛵 我是機車、對方同向、且對方明顯偏在側邊 → 可以鑽過去
                            //    (不再限定「對方也是機車」,遇到汽車一樣能側向通過)
                            bool inQueueContext = isWaitingAtTrafficLight
                                || otherCar.smoothDrive < 0.3f
                                || otherCar.realTimeSpeed < 3f;   // 前方汽車在排隊/低速也算
                            if (inQueueContext && hit.distance > 1.2f)
                            {
                                scooterSideBySidePass = true; // 觸發並排放行標記
                            }
                        }
                        if (scooterSideBySidePass)
                        {
                            thisRayPower = 1.0f;
                            if (lateralDot > 0f) filterBlockedRight = true; // 紀錄右側有車
                            else filterBlockedLeft = true; // 紀錄左側有車
                        }
                        else
                        {
                            // 內積 < 0 (對向車) 且 (側邊射線掃到 或 右轉中)，忽視車輛前進
                            if (headingDot < 0f && (isSideRay || amIRightTurning) && !MustYieldToRightTurner(hit.collider))
                            {
                                thisRayPower = 1.0f; // 側線對向車不減速，直接忽略
                            }
                            else if (hit.distance <= followStopDistance) // 低於安全距離，強制煞停
                            {
                                // 【修正重點】如果是機車，且打到的車輛明確位於自身側邊 (lateralDot 絕對值較大)
                                // 即使距離 < 1.2m，也不應該視為正前方煞停障礙物，而是忽略該射線（或僅做防禦微減速）
                                if (isScooter && !isBezierActive && isLateralPosition && headingDot > 0.6f)
                                {
                                    thisRayPower = 1.0f; // 不阻擋直線前進
                                    if (lateralDot > 0f) filterBlockedRight = true;
                                    else filterBlockedLeft = true;
                                }
                                // 如果是同向車偏角大（例如隔壁車道準備切進來）
                                else if (isSideRay && headingDot < 0.75f && headingDot >= 0f)
                                {
                                    thisRayPower = 0.4f; // 微微減速防禦
                                    //Debug.Log($"<color=yellow>[雷達防禦]</color> {gameObject.name} 側線掃到斜前方同向車 {hit.collider.name}，微減速 0.4");
                                }
                                else
                                {
                                    // 真正擋在正前方的同向車，或中間射線打到的車
                                    currentlyBlockedByRay = true;
                                    thisRayPower = 0f; // 強制煞停
                                    //Debug.Log($"<color=red>[雷達煞停]</color> {gameObject.name} 正前方遭阻擋！障礙物: {hit.collider.name}，強制煞停！HeadingDot: {headingDot:F2}");
                                }
                            }
                            else // // 5米以外的緩衝區間：動態調整跟車距離
                            {
                                // 【核心優化】動態判定最大偵測邊界
                                float dynamicMaxDistance = rayDistance;
                                // 如果抓得到前車元件，根據前車狀態調整「舒適跟車邊界」
                                if (otherCar != null)
                                {
                                    // 如果前車也在好好開車（實際動力 smoothDrive 大於 0.6f）
                                    if (otherCar.smoothDrive > 0.8f)
                                    {
                                        // 將原本 19.5 米的偵測縮減，直行巡航時不需要看那麼遠才減速
                                        // 讓日常巡航的舒適跟車距離收束在 10~12 米左右
                                        dynamicMaxDistance = Mathf.Max(10f, rayDistance * 0.4f);
                                    }
                                    // 如果前車停紅燈或急煞（smoothDrive < 0.2f），保持 dynamicMaxDistance = rayDistance (19.5m) 確保煞得住
                                }
                                // 使用動態邊界計算線性減速公式
                                if (hit.distance <= dynamicMaxDistance)
                                {
                                    thisRayPower = Mathf.Clamp01((hit.distance - followStopDistance) / (dynamicMaxDistance - followStopDistance));
                                    // 同向偏角車保底
                                    if (isSideRay && headingDot < 0.75f)
                                    {
                                        thisRayPower = Mathf.Max(thisRayPower, 0.5f);
                                    }
                                }
                            }
                        }
                    }
                    // --- 取所有射線中「最慢」的那一個動力值 ---
                    if (thisRayPower < finalDrivePower)
                    {
                        finalDrivePower = thisRayPower;
                    }
                }
            }
            // 直行時多增加2條直線，轉彎時不執行
            if (!amILeftTurning && !amIRightTurning && !isBezierActive)
            {
                float straightRayLength = activeDetectionDistance * straightDetectionMult; // 跟中間那條一樣長
                float RayLength = isScooter ? scooterStraightRaySideSpacing : straightRaySideSpacing;
                float[] sideOffsets = { -RayLength, RayLength };

                foreach (float off in sideOffsets)
                {
                    // 起點往左右平移，方向維持正前方(直線、不彎)
                    Vector3 parallelOrigin = rayOrigin + transform.right * off;
                    if (Physics.Raycast(parallelOrigin, transform.forward, out RaycastHit sideHit, straightRayLength, carLayer))
                    {
                        if (sideHit.collider.gameObject != this.gameObject)
                        {
                            CarNavigation otherCarSide = sideHit.collider.GetComponent<CarNavigation>();
                            bool scooterPassSide = false;
                            // 【機車專用】：平行側線鑽車/並排放行判定
                            if (isScooter && otherCarSide != null && otherCarSide.isScooter)
                            {
                                float headingDotSide = Vector3.Dot(transform.forward, otherCarSide.transform.forward);
                                bool inQueueContextSide = isWaitingAtTrafficLight
                                    || currentStartDelay > 0f
                                    || otherCarSide.smoothDrive < 0.1f;
                                // 條件：雙方同向行駛、處於停等或塞車慢速情境、距離維持在安全邊界以上
                                if (headingDotSide > 0.7f && inQueueContextSide && sideHit.distance > 1.2f)
                                {
                                    scooterPassSide = true;
                                    // off > 0 : 表示右邊有車 ， off <= 0 : 表示左邊有車
                                    if (off > 0f) filterBlockedRight = true;
                                    else filterBlockedLeft = true;
                                }
                            }

                            float thisRayPower;
                            if (scooterPassSide)
                            {
                                thisRayPower = 1.0f;
                            }
                            else if (sideHit.distance <= 3.5f)
                            {
                                currentlyBlockedByRay = true;
                                thisRayPower = 0f;
                            }
                            else
                            {
                                thisRayPower = Mathf.Clamp01((sideHit.distance - 3.5f) / (straightRayLength - 3.5f));
                            }
                            if (thisRayPower < finalDrivePower) finalDrivePower = thisRayPower;
                        }
                    }
                }
            }
        }
        // ---------------------------------------------------------
        // 【機車專用】機車鑽車防抖動處置：將單幀的側邊阻擋標記累積為穩定的避讓決策
        // ---------------------------------------------------------
        if (sideDecisionLockTimer > 0f)
        {
            sideDecisionLockTimer -= Time.deltaTime;
        }

        // 累積兩側阻擋計數器 (連續偵測才算數)
        if (filterBlockedRight && !filterBlockedLeft) { rightBlockStreak++; leftBlockStreak = 0; }
        else if (filterBlockedLeft && !filterBlockedRight) { leftBlockStreak++; rightBlockStreak = 0; }
        else { rightBlockStreak = 0; leftBlockStreak = 0; }

        // 無決策鎖定時，達成條件才更新側切/鑽車方向
        if (sideDecisionLockTimer <= 0f)
        {
            if (rightBlockStreak >= requiredStreak)
            {
                committedSideIsRight = true; // 右邊被擋，鎖定向左閃避
                hasCommittedSide = true;
                sideDecisionLockTimer = sideDecisionLockDuration;
            }
            else if (leftBlockStreak >= requiredStreak)
            {
                committedSideIsRight = false; // 左邊被擋，鎖定向右閃避
                hasCommittedSide = true;
                sideDecisionLockTimer = sideDecisionLockDuration;
            }
        }

        // 如果雙側都沒被擋超過一段時間，逐漸清除鎖定，回到原本巡航偏移
        if (!filterBlockedRight && !filterBlockedLeft && rightBlockStreak == 0 && leftBlockStreak == 0)
        {
            // 保持 hasCommittedSide 不變，讓 MoveCar/UpdateScooterLateralOffset 自行判斷是否要回正
            // 這裡不強制清除，交給下面統一的 targetLateralOffset 計算處理「維持 cruiseLateralOffset」
        }
        // ---------------------------------------------------------
        // 階段 4：無號誌路口邏輯 (Stop/Slow Mechanism)
        // ---------------------------------------------------------
        if (futureNodes[0] != null)
        {
            distToNode = Vector3.Distance(transform.position, futureNodes[0].transform.position);
            // --- A. 「慢」的前置判定 ---
            // 如果當前點本身就是 Slow 點
            bool isUpcomingSlow = (futureNodes[0].intersectionType == WaypointNode.IntersectionType.Slow);
            float totalDistToSlowPoint = distToNode;

            if (!isUpcomingSlow && futureNodes[1] != null)
            {
                if (futureNodes[1].intersectionType == WaypointNode.IntersectionType.Slow)
                {
                    isUpcomingSlow = true;
                    totalDistToSlowPoint += Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                }
            }
            // --- B. 「停」的偵測條件 ---
            // 計算與「上一個節點」的距離（用來追蹤是否剛離開 Stop 點）
            float distToLastNode = lastWaypointNode != null ? Vector3.Distance(transform.position, lastWaypointNode.transform.position) : 999f;
            // 只要「當前點是 Stop」或者「剛離開 Stop 點不到 7 米（轉彎中）」，就認定處於 Stop 路口管轄內
            bool isInStopJurisdiction = IsStopNodeType(futureNodes[0]) ||
                (lastWaypointNode != null && IsStopNodeType(lastWaypointNode) && distToLastNode < 7.0f);
            // 條件 1：即將抵達當前 Stop 點且尚未首檢 (精準首檢範圍)
            bool isApproachingFirstCheck = (IsStopNodeType(futureNodes[0]) && distToNode < 4.0f && !hasCheckedIntersection);
            // 條件 2：狀態鎖。只要曾經左轉受阻，強制維持
            bool isHoldingSecondCheck = isLeftTurnBlockedStopping;
            // 如果目前不是 Stop 點，且距離已經拉開，強制清除左轉受阻的遺留鎖，避免污染下一個一般路口
            if (!isInStopJurisdiction)
            {
                isLeftTurnBlockedStopping = false;
                isHoldingSecondCheck = false;
            }
            // 最終偵測框啟動條件：必須在 Stop 管轄內，且（準備首檢 或 正在執行二檢鎖定）
            // 這樣左轉到一半切換點時，因為 isHoldingSecondCheck = true 且 isInStopJurisdiction = true，綠框就會完美維持！
            bool needIntersectionCheck = (isApproachingFirstCheck || isHoldingSecondCheck) && isInStopJurisdiction;
            // 最終判定：必須是即將首檢，或者是 Stop 遺留的二檢鎖，才開啟左右偵測
            debugShowStopBox = needIntersectionCheck;

            // 只要滿足路口檢查條件，不論當前節點是不是 Stop（應對切換點後的遺留偵測）
            if (needIntersectionCheck || isInStopJurisdiction)
            {
                // 線性減速判定（只有是 Stop 點且還沒首檢時有效）
                if (IsStopNodeType(currentWaypointNode) && distToNode < approachDistance && !hasCheckedIntersection)
                {
                    float stopT = Mathf.Clamp01((distToNode - 1.5f) / (approachDistance - 1.5f));
                    targetPower = Mathf.Min(targetPower, stopT);
                }
                // 執行 Physics.OverlapBox 左右車偵測
                if (needIntersectionCheck)
                {
                    // 動態選擇偵測框尺寸與位移：大路口車道多，要看得更遠更寬
                    bool isBig = (futureNodes[0] != null && futureNodes[0].intersectionType == WaypointNode.IntersectionType.BigStop) ||
                                 (lastWaypointNode != null && lastWaypointNode.intersectionType == WaypointNode.IntersectionType.BigStop);
                    activeStopBoxIsBig = isBig;

                    Vector3 activeBoxSize = isBig ? bigStopBoxSize : stopBoxSize;
                    Vector3 activeOffsetLeft = isBig ? bigStopBoxOffsetLeft : stopBoxOffsetLeft;
                    Vector3 activeOffsetRight = isBig ? bigStopBoxOffsetRight : stopBoxOffsetRight;

                    bool isIntendingToTurnRight = amIRightTurning;

                    if (!isIntendingToTurnRight && futureNodes[0] != null)
                    {
                        // 找到離開 Stop 點之後的下一個導引方向
                        WaypointNode targetAfterStop = null;
                        Vector3 stopNodePos = transform.position;
                        if (IsStopNodeType(futureNodes[0]))
                        {
                            stopNodePos = futureNodes[0].transform.position;
                            // 優先看下下個點 (2) 確保抓到大轉彎；如果路網沒那麼長，退而求其次看 (1)
                            if (futureNodes.Length > 2 && futureNodes[2] != null)
                            {
                                targetAfterStop = futureNodes[2];
                            }
                            else if (futureNodes.Length > 1 && futureNodes[1] != null)
                            {
                                targetAfterStop = futureNodes[1];
                            }
                            //Debug.Log($"<color=magenta>1[路口預判]</color> 即將抵達 Stop 點，從 Stop 點位置 ({stopNodePos}) 預判下一個方向，目標點: {(targetAfterStop != null ? targetAfterStop.transform.position.ToString() : "無")} ");
                        }
                        else if (currentWaypointNode != null && IsStopNodeType(currentWaypointNode))
                        {
                            stopNodePos = currentWaypointNode.transform.position;
                            // 優先看下下個點 (1)；若無則看 (0)
                            if (futureNodes.Length > 1 && futureNodes[1] != null)
                            {
                                targetAfterStop = futureNodes[1];
                            }
                            else
                            {
                                targetAfterStop = futureNodes[0];
                            }
                            //Debug.Log($"<color=magenta>2[路口預判]</color> 在切換點上，從上一個 Stop 點 ({stopNodePos}) 預判下一個方向，目標點: {(targetAfterStop != null ? targetAfterStop.transform.position.ToString() : "無")} ");
                        }

                        if (targetAfterStop != null)
                        {
                            // 改用【基準點】到【下下個點】的總體趨勢向量
                            Vector3 exitDirection = (targetAfterStop.transform.position - stopNodePos).normalized;

                            // 與車子目前的右方向量做 Dot 判定

                            // 與車子目前的右方向量 (transform.right) 做 Dot 判定
                            // 如果夾角小於 45 度 (Dot > 0.7) 或者是明顯的右轉趨勢 (例如 > 0.5)
                            float rightTurnDot = Vector3.Dot(exitDirection, transform.right);
                            if (rightTurnDot > 0.5f)
                            {
                                isIntendingToTurnRight = true;
                                debugShowRightTurningStopBox = isIntendingToTurnRight;
                                // Debug.Log($"<color=cyan>[路口預判]</color> 偵測到前方路徑為右轉趨勢 (Dot: {rightTurnDot:F2})，提前鎖定右轉邏輯。");
                            }
                        }
                    }
                    Vector3 leftCenter = transform.TransformPoint(activeOffsetLeft);
                    Collider[] leftCars = Physics.OverlapBox(leftCenter, activeBoxSize / 2f, transform.rotation, carLayer);

                    bool incomingCarDetected = false;
                    foreach (var c in leftCars) // 左側來車永遠都要檢查
                    {
                        if (c.gameObject != this.gameObject)
                        {
                            Rigidbody rb = c.GetComponentInParent<Rigidbody>();
                            if (rb != null && Vector3.Dot(rb.transform.forward, transform.right) > 0.1f && IsActuallyMoving(c)) incomingCarDetected = true;
                        }
                    }
                    // 2. 只有在「非右轉」(直行或左轉)時，才去啟動右側框偵測
                    if (!isIntendingToTurnRight)
                    {
                        Vector3 rightCenter = transform.TransformPoint(activeOffsetRight);
                        Collider[] rightCars = Physics.OverlapBox(rightCenter, activeBoxSize / 2f, transform.rotation, carLayer);

                        foreach (var c in rightCars)
                        {
                            if (c.gameObject != this.gameObject)
                            {
                                Rigidbody rb = c.GetComponentInParent<Rigidbody>();
                                if (rb != null && Vector3.Dot(rb.transform.forward, -transform.right) > 0.1f && IsActuallyMoving(c)) incomingCarDetected = true;
                            }
                        }
                    }
                    isCarDetectedAtStop = incomingCarDetected;

                    if (incomingCarDetected || waitingForOppositeStraightCar)
                    {
                        targetPower = 0f;
                        if (waitingForOppositeStraightCar) isLeftTurnBlockedStopping = true;
                        hasCheckedIntersection = false;
                    }
                    else // 左右沒車，對向也沒車了
                    {
                        if (isLeftTurnBlockedStopping) // 處理二檢釋放
                        {
                            if (smoothDrive < 0.05f) // 確保原地煞停後才放行
                            {
                                hasCheckedIntersection = true;
                                isLeftTurnBlockedStopping = false; // 二檢真正通過，釋放鎖！
                            }
                            else
                            {
                                targetPower = 0f; // 強制煞停進行確認
                            }
                        }
                        else if (IsStopNodeType(futureNodes[0]) && distToNode < 4.0f && !hasCheckedIntersection)
                        {
                            if (smoothDrive < 0.05f) // 車速已經降到接近 0，法定完成了「停」的動作
                            {
                                hasCheckedIntersection = true; // 首檢通過，正式放行！
                            }
                            else
                            {
                                targetPower = 0f; // 雖然左右沒車，但車速還太快，強制煞車直到完全停下
                            }
                        }
                        isCarDetectedAtStop = false;
                    }
                }
                else if (hasCheckedIntersection)
                {
                    isCarDetectedAtStop = false;
                }
            }
            else
            {
                debugShowStopBox = false; // 如果都不符合，確保關閉偵測框繪製
                isCarDetectedAtStop = false;
            }
            // --- B. 「慢」的機制 ---
            if (isUpcomingSlow)
            {
                if (totalDistToSlowPoint < approachDistance)
                {
                    // 目標時速佔了車隨機最高時速的百分之幾 18/40 = 0.45f；18/30 = 0.6f
                    float requiredSlowPower = targetSlowSpeed / currentRandomSpeed;
                    float baseSlowPower = Mathf.Clamp01(requiredSlowPower);
                    // 計算距離比例 t (0.0 ~ 1.0)
                    float t = Mathf.Clamp01(totalDistToSlowPoint / approachDistance);
                    // 使用 Lerp 讓動力在「目標慢速比例」與「全速 1.0」之間依距離平滑滑動
                    float dynamicSlowPower = Mathf.Lerp(baseSlowPower, 1.0f, t);
                    targetPower = Mathf.Min(targetPower, dynamicSlowPower);
                    //Debug.Log($"<color=yellow>[路口預判減速]</color> 車輛: {gameObject.name}, 總距離剩餘: {totalDistToSlowPoint:F1}m, 減速中");
                }
            }

            // --- C. 徹底遠離路口後的重置 ---
            // 這裡原本只有檢查 distToNode > 20f，如果點已經換了，distToNode 會突然變大，導致提早重置！
            // 我們必須加上條件：只有在「不是在執行二次偵測鎖」的情況下，且距離新點有一段距離，才能重置。
            if (distToNode > 20.0f && hasCheckedIntersection && !isLeftTurnBlockedStopping)
            {
                hasCheckedIntersection = false;
            }
        }
        // ---------------------------------------------------------
        // 階段 5：黃色網狀線防卡死邏輯
        // ---------------------------------------------------------
        debugCurrentExitNode = null;
        if (futureNodes[0] != null && futureNodes[0].isInsideYellowGrid)
        {
            WaypointNode realExitNode = null;
            if (futureNodes[1] != null)
            {
                // 如果下一個點（未來點 1）有勾網狀線，它就是出口
                if (futureNodes[1].isInsideYellowGrid)
                {
                    realExitNode = futureNodes[1];
                }
                // 如果下一個點沒勾，但下下個點（未來點 2）有勾，那未來點 2 就是看穿貝茲後的真正出口！
                else if (futureNodes[2] != null && futureNodes[2].isInsideYellowGrid)
                {
                    realExitNode = futureNodes[2];
                }
                else if (futureNodes[3] != null && futureNodes[2] != null &&
                    futureNodes[3].isInsideYellowGrid &&
                    Vector3.Distance(futureNodes[2].transform.position, futureNodes[3].transform.position) < 5.0f)
                {
                    realExitNode = futureNodes[3];
                }
            }
            // 只有當我們在 A 點前，且「成功找到了有勾網狀線的出口 B 點」時，才進行偵測
            // 這樣當車子人已經移到 B 點時，因為 B 的後面（C點）沒勾，且下下一點也沒勾，realExitNode 就會是 null，自動完美放行！
            if (realExitNode != null)
            {
                float distToEntranceA = Vector3.Distance(transform.position, futureNodes[0].transform.position);
                Vector3 dirToEntranceA = (futureNodes[0].transform.position - transform.position).normalized;
                float dotToA = Vector3.Dot(transform.forward, dirToEntranceA);

                if (dotToA > 0 && distToEntranceA < approachDistance)
                {
                    debugCurrentExitNode = realExitNode; // 框框在 B 點

                    Vector3 checkPosition = realExitNode.transform.TransformPoint(yellowGridBoxOffset);
                    Collider[] carsAtExit = Physics.OverlapBox(checkPosition, yellowGridBoxSize / 2f, realExitNode.transform.rotation, carLayer);
                    bool isExitBlocked = false;

                    foreach (var col in carsAtExit)
                    {
                        if (col.gameObject == this.gameObject) continue;
                        // 🛵 靠邊的機車不算堵住出口(佔的寬度小,旁邊還過得去)
                        CarNavigation scoot = col.GetComponentInParent<CarNavigation>();
                        if (scoot != null && scoot.isScooter && realExitNode != null)
                        {
                            Vector3 relEx = realExitNode.transform.InverseTransformPoint(scoot.transform.position);
                            if (Mathf.Abs(relEx.x) > 1.2f) continue;
                        }

                        CarNavigation otherCar = col.GetComponentInParent<CarNavigation>();
                        if (otherCar == null || otherCar.realTimeSpeed < 5f || otherCar.smoothDrive < 0.6f)
                        {
                            isExitBlocked = true;
                            break;
                        }
                    }

                    debugIsExitBlocked = isExitBlocked;

                    if (isExitBlocked)
                    {
                        float gridT = Mathf.Clamp01((distToEntranceA - 5f) / (approachDistance - 5f));
                        if (distToEntranceA <= 3f) gridT = 0f; // 在 A 點前煞停
                        targetPower = Mathf.Min(targetPower, gridT);
                    }
                }
            }
        }
        // ---------------------------------------------------------
        // 階段 6：綜合判斷與狀態決策 (Decision Making)
        // 優先順序：硬性規則(紅燈/阻擋/延遲) > 行為限制(彎道限速) > 理想巡航
        // ---------------------------------------------------------
        if (exitBezierCooldown > 0f) // 減少出彎冷卻鎖時間
        {
            exitBezierCooldown -= Time.deltaTime;
        }
        // 🚧 繞行中保底動力:車必須保持前進才切得出去(停住就永遠切不動)
        if (IsBypassingAccident && finalDrivePower < 0.35f) finalDrivePower = 0.35f;
        targetPower = Mathf.Min(targetPower, finalDrivePower);

        // 🚑 避讓中、救護車還沒通過、而且前方要轉彎 → 不要轉進救護車的路徑
        if (blockTurnForAmbulance && IsUpcomingTurn())
        {
            // 🔧 但「還在靠邊途中」不能完全停死。
            //    靠邊是靠斜切前進達成的(跟上面故障車繞行的保底動力同一個道理:
            //    「車必須保持前進才切得出去,停住就永遠切不動」),
            //    targetPower = 0 會讓車停在車道中央,偏移量算得再滿也永遠移不過去。
            //    保留低速前進讓它把靠邊做完,靠好之後才真正停住等救護車。
            if (isYieldingForAmbulance && !isPulledOverForAmbulance)
                targetPower = Mathf.Min(targetPower, yieldSlidePower);
            else
                targetPower = 0f;
        }

        // 🚑 路口快速淨空:優先權高於一般減速,讓車趕快離開路口(前方雷達仍會防撞)
        if (clearJunctionForAmbulance) targetPower = Mathf.Max(targetPower, clearJunctionPower);

        // 🤝 回歸禮讓中 → 停在路邊等主車道車流過去(不把車頭伸進去)
        if (isMergeHolding) targetPower = 0f;

        // 🚦 路口淨空:救護車要通過我前方的路口 → 我是橫向車,停在路口前等它離開。
        //    已經開進路口的車不會走到這裡(EvaluateJunctionHold 會回傳 false),
        //    它會沿用 clearJunction 的邏輯趕快開出去,不會停在路口中央。
        if (EvaluateJunctionHold()) targetPower = 0f;
        // 整合雷達阻擋與左轉停讓狀態
        isBlockedByCar = (finalDrivePower <= 0f) || waitingForOppositeStraightCar;
        // A. 彎道線性預減速處理
        if (futureNodes[0] != null)
        {
            float d = Vector3.Distance(transform.position, futureNodes[0].transform.position);
            isIncomingTurn = false;
            brakeDistanceBasis = d; // 實際要用來計算減速的距離

            //只有在「沒有出彎冷卻鎖」的情況下，才允許超前預判彎道
            if (exitBezierCooldown <= 0f)
            {
                // 檢查當前目標點是否就是彎道
                if (futureNodes[1] != null)
                {
                    Vector3 dirToTarget = (futureNodes[0].transform.position - transform.position).normalized;
                    Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
                    // 只要未來的路徑夾角大於 15 度，即視為即將進入彎道路口
                    if (Vector3.Angle(dirToTarget, dirNext) > 15f)
                    {
                        isIncomingTurn = true;
                        brakeDistanceBasis = 0;
                        //Debug.Log($"<color=cyan>[超前預判]</color> 預判到下一個點 {futureNodes[0].name} 是彎道，提前減速距離: {brakeDistanceBasis:F1}m 角度: {Vector3.Angle(dirToTarget, dirNext):F1}°");
                    }
                }
                // 如果下個點還不是彎 檢查下下一個點是否為彎道
                if (!isIncomingTurn && futureNodes[1] != null && futureNodes[2] != null)
                {
                    Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
                    Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;

                    if (Vector3.Angle(dirNext, dirNextNext) > 15f)
                    {
                        isIncomingTurn = true;
                        // 彎道在下下個點（[1]才是入口），所以防禦減速距離 = 車到[0]的距離 + [0]到[1]的直行路段距離
                        brakeDistanceBasis = d;
                        //Debug.Log($"<color=cyan>[超前預判]</color> 預判到下下個點 {futureNodes[1].name} 是彎道，提前減速距離: {brakeDistanceBasis:F1}m 角度: {Vector3.Angle(dirNext, dirNextNext):F1}°");
                    }
                }

                // 狀況 3：拜 [4] 陣列空間所賜，你現在還可以提早預判下下下個點 [2]->[3] 是不是彎道
                if (!isIncomingTurn && futureNodes[1] != null && futureNodes[2] != null && futureNodes[3] != null)
                {
                    Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;
                    Vector3 dirNextNextNext = (futureNodes[3].transform.position - futureNodes[2].transform.position).normalized;

                    if (Vector3.Angle(dirNextNext, dirNextNextNext) > 15f)
                    {
                        isIncomingTurn = true;
                        // 減速距離累加到 [2] 號節點（彎道入口）前的所有直線路段
                        brakeDistanceBasis = d + Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                        //Debug.Log($"<color=cyan>[超前預判]</color> 預判到下下下個點 {futureNodes[2].name} 是彎道，提前減速距離: {brakeDistanceBasis:F1}m角度: {Vector3.Angle(dirNextNext, dirNextNextNext):F1}°");
                    }
                }
            }
            // 1. 處理線性預判減速：讓車輛在靠近路口時有穩定的減速曲線 (0.9 -> 0.8 -> 0.7...)
            if (isIncomingTurn || isBezierActive)
            {
                // 如果偵測到前方有彎，或是「正在過彎中」
                if (brakeDistanceBasis < approachDistance || isBezierActive)
                {
                    // 預設採用左轉預減速的低速 (18f)
                    float activeTurnSpeed = targetTurnSpeed;
                    // 節點勾了「不需左轉禮讓」就不必壓車速，直接用較快的過彎速度
                    bool isYieldDisabled = (futureNodes[0] != null && futureNodes[0].disableLeftTurnYield) ||
                                          (lastWaypointNode != null && lastWaypointNode.disableLeftTurnYield);
                    // 如果已經進入「貝茲曲線執行中」(正在轉彎過程)，一律提高速度
                    if (isBezierActive || isYieldDisabled)
                    {
                        activeTurnSpeed = targetFastTurnSpeed;
                    }
                    //如果是還在預減速階段，但預判等一下是「右轉彎」，也提高速度
                    else if (isIncomingTurn)
                    {
                        // 1. 決定基準點（如果在路口前，以路口 Stop/特殊點為基準；否則以車子當前位置為基準）
                        Vector3 referencePos = transform.position;
                        if (futureNodes[0].intersectionType != WaypointNode.IntersectionType.None)
                        {
                            referencePos = futureNodes[0].transform.position;
                        }

                        // 2. 新邏輯：超前看點！優先看 [3]，沒有就看 [2]，再沒有就看 [1] 或 [0]
                        WaypointNode targetAfterTurn = null;
                        if (futureNodes.Length > 3 && futureNodes[3] != null) targetAfterTurn = futureNodes[3];
                        else if (futureNodes.Length > 2 && futureNodes[2] != null) targetAfterTurn = futureNodes[2];
                        else if (futureNodes.Length > 1 && futureNodes[1] != null) targetAfterTurn = futureNodes[1];
                        else targetAfterTurn = futureNodes[0];

                        if (targetAfterTurn != null)
                        {
                            Vector3 turnTrendDir = (targetAfterTurn.transform.position - referencePos).normalized;
                            float rightTurnDot = Vector3.Dot(turnTrendDir, transform.right);

                            // 【新增 Debug】看看到底有沒有抓對點，Dot 算出來是多少
                            //Debug.Log($"<color=cyan>[右轉預判追蹤]</color> 車輛: {gameObject.name} " +
                            //          $"| 參考基準點: {(futureNodes[0].intersectionType != WaypointNode.IntersectionType.None ? futureNodes[0].name : "車子當前位置")} " +
                            //          $"| 預判目標點: {targetAfterTurn.name} " +
                            //          $"| 算出的 Dot: {rightTurnDot:F2}");

                            // Dot > 0.4 代表這是一個右轉彎趨勢 -> 預減速階段就允許使用較高時速
                            if (rightTurnDot > 0.4f)
                            {
                                activeTurnSpeed = targetFastTurnSpeed;
                            }
                        }
                    }
                    // 目標時速佔了車隨機最高時速的百分之幾 18/40 = 0.45f；18/30 = 0.6f
                    float requiredPower = activeTurnSpeed / currentRandomSpeed;
                    float targetTurnPower = Mathf.Clamp01(requiredPower);
                    // 計算線性比例 t (從 1.0 到 0.0)
                    float a = brakeDistanceBasis - reduceDistance;
                    float b = approachDistance - reduceDistance;
                    float t = 0f; // 避免除以零的情況
                    if (b > 0) t = Mathf.Clamp01(a / b);
                    // 使用 Lerp 讓動力在 0.6(彎道速) 與 1.0(全速) 之間滑動
                    float curveSpeed = Mathf.Lerp(targetTurnPower, 1.0f, t);
                    // 若進入「鎖定區」(即將進入貝茲或已在預備中)，強制維持低速轉彎值
                    if (brakeDistanceBasis <= reduceDistance || isBezierActive)
                    {
                        curveSpeed = targetTurnPower;
                        //這個 Log 會告訴你到底是誰在鎖死
                        //Debug.Log($"<color=red>[低速鎖定]</color> 原因: " +
                        //    $"預判彎道:{isIncomingTurn} " + $"貝茲執行中:{isBezierActive}, ");
                    }
                    targetPower = Mathf.Min(targetPower, curveSpeed);
                    //Debug 數值，你會看到動力是 0.98... 0.85... 0.72... 慢慢掉下去
                    //Debug.Log($"<color=orange>漸進限速:</color>車輛: {gameObject.name} 目標點: {currentWaypointNode.name} 目標速度:{activeTurnSpeed} 距離:{brakeDistanceBasis:F1} 動力:{curveSpeed:F2}");
                }
            }
        }
        // B. 貝茲曲線出口段：從轉彎速慢慢恢復到直線速 (0.8 ~ 1.0)
        if (isBezierActive)
        {
            float activeBezierSpeed = targetFastTurnSpeed; // 過彎中一律允許較高的 targetFastTurnSpeed
            float bezierSpeedLimit = Mathf.Clamp01(activeBezierSpeed / currentRandomSpeed);
            //if (bezierT < 0.8f)
            //{
            //    bezierSpeedLimit = turnSpeedMultiplier;
            //}
            //else
            //{
            //    bezierSpeedLimit = Mathf.Lerp(turnSpeedMultiplier, 1.0f, (bezierT - 0.8f) / 0.2f);
            //    //Debug.Log($"<color=cyan>[貝茲恢復]</color> bezierT: {bezierT:F2}, 速度限制: {bezierSpeedLimit:F2}");
            //}
            // 強制套用鎖定
            targetPower = Mathf.Min(targetPower, bezierSpeedLimit);
        }
        bool isStopNodeActive = (currentWaypointNode.intersectionType == WaypointNode.IntersectionType.Stop && !hasCheckedIntersection && distToNode < 4.0f);
        // C. 停等條件判斷 (紅燈、車阻、起步反應延遲)
        if (isWaitingAtTrafficLight)
        {
            WaypointNode trafficStopTargetNode = null; // 預設最終要聽從的停等目標點
            float distanceToStopLine = distToNode; // 初始為車子到 A 點的實際距離
            // 1. 【由近到遠檢查與防呆阻斷】
            for (int i = 0; i < futureNodes.Length && i <= 3; i++)
            {
                if (futureNodes[i] == null) break;
                // 如果是後續點 (B, C, D)，先累加「前一點到這一個點」的線段距離
                if (i == 1) distanceToStopLine += Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                if (i == 2) distanceToStopLine += Vector3.Distance(futureNodes[1].transform.position, futureNodes[2].transform.position);
                if (i == 3) distanceToStopLine += Vector3.Distance(futureNodes[2].transform.position, futureNodes[3].transform.position);

                // 如果當前這個點就是停等區
                if (futureNodes[i].isTrafficLightStopZone)
                {
                    bool isNodeValid = false; // 預設為不符合條件
                    // 根據點的位置，套用不同的總距離防禦條件
                    if (i == 0) // A 點
                    {
                        isNodeValid = true;
                    }
                    else if (i == 1) // B 點
                    {
                        isNodeValid = true; // B 就不用距離判斷
                    }
                    else if (i == 2) // C 點
                    {
                        if (distanceToStopLine < 50.0f) isNodeValid = true; // A 到 C 距離是否 < 50m（原本 35m，以實測 14.5m 的節點間距永遠不成立）
                    }
                    else if (i == 3) // D 點
                    {
                        if (distanceToStopLine < 60.0f) isNodeValid = true; // A 到 D 距離是否 < 60m（原本 40m，同上）
                    }
                    // 如果通過距離篩選，就是它了！
                    if (isNodeValid)
                    {
                        trafficStopTargetNode = futureNodes[i];
                        break; // 【防呆阻斷】找到第一個符合條件的停等區，就不往後找了！
                    }
                }
            }
            // 2. 【根據最終決策的節點進行動態行為控制】
            if (trafficStopTargetNode != null)
            {
                float idealStopDistance = isScooter ? 0f : 3.5f; // 設定理想精準停等邊界
                if (distanceToStopLine > idealStopDistance)
                {
                    // 距離還遠，進行漸進式蠕行貼近
                    float brakeT = Mathf.Clamp01((distanceToStopLine - idealStopDistance) / 15f);
                    float creepPower = Mathf.Lerp(0f, 0.35f, brakeT);
                    targetPower = Mathf.Min(targetPower, creepPower);
                }
                else
                {
                    targetPower = 0f;
                }
            }
            else
            {
                // 狀況：未來的 4 個點通通沒有勾選停等區。
                // 原本一律鎖死 0 動力，但那會讓「還在停等盒尾端、離停止線還有一段」的車
                // 直接凍在原地 —— 看起來就是前面明明有位置卻不往前貼。
                // 只有「已經駛過停止線」才真的要鎖死（不能衝進路口中央）；
                // 還沒到停止線的低速蠕行貼上去，前方有車時下面的 isBlockedByCar 會另外把它停住。
                bool alreadyPassedStopLine = lastWaypointNode != null && lastWaypointNode.isTrafficLightStopZone;
                targetPower = alreadyPassedStopLine ? 0f : Mathf.Min(targetPower, 0.2f);
            }
        }
        // D.綜合其他原本的硬性阻擋規則（扣除已經單獨處理的 isWaitingAtTrafficLight）
        bool mustStopExceptLight = isBlockedByCar || (currentStartDelay > 0) || isStopNodeActive || isCarDetectedAtStop;
        if (mustStopExceptLight) targetPower = 0f;
        // E. 起步延遲計時
        if (!isWaitingAtTrafficLight && !isBlockedByCar && currentStartDelay > 0)
        {
            currentStartDelay -= Time.deltaTime;
        }
        // 🚶 禮讓背景行人:PedestrianYieldSensor 掃到行人擋在車前時設這個上限(0=停,0~1=蠕行)。
        //    放在最後、餵給 smoothDrive 之前,而且是 Mathf.Min —— 只會更慢,不影響停等/避讓邏輯。
        if (pedestrianYieldPower >= 0f) targetPower = Mathf.Min(targetPower, pedestrianYieldPower);
        // F. 最終動力平滑化 (模擬實車加速度)
        float currentRate = (targetPower > smoothDrive) ? accelerationRate : brakingRate;
        smoothDrive = Mathf.MoveTowards(smoothDrive, targetPower, currentRate * Time.deltaTime);
        // 更新即時速度數值 (Debug 用)
        realTimeSpeed = currentRandomSpeed * smoothDrive;
        // --- 動力監控 Debug Log ---
        // 1(全速) 時顯示
        //if (smoothDrive < 0.99f)
        //{
        //Debug.Log($"<color=white>[動力監控]</color>車輛:{gameObject.name} 最終動力:{smoothDrive:F2}");
        //}
        // ---------------------------------------------------------
        // 階段 5：物理位移執行
        // ---------------------------------------------------------
        if (currentWaypointNode != null && smoothDrive > 0.001f)
        {
            MoveCar(smoothDrive);
        }
        else
        {
            //當車輛完全煞停或沒有導航點時，滾動停滯，前輪平滑回正
            AnimateWheels(transform.forward, 0f);
        }
        UpdateScooterLateralOffset(); //不管有沒有移動，都嘗試把機車滑到該有的偏移位置
        AnimateLights();
    }
    void InitializePath(WaypointNode startingNode)
    {
        if (startingNode == null) return;
        // 第一顆點是起點
        futureNodes[0] = startingNode;
        // 隨機抽第二顆點
        futureNodes[1] = PickRandomChoice(futureNodes[0]);
        // 隨機抽第三顆點
        futureNodes[2] = PickRandomChoice(futureNodes[1]);
        // 隨機抽第四顆點
        futureNodes[3] = PickRandomChoice(futureNodes[2]);
        //Debug.Log($"<color=lime>[路徑初始化成功]</color> {gameObject.name} 的四點預測軌道：[0]:{futureNodes[0]?.name} -> [1]:{futureNodes[1]?.name} -> [2]:{futureNodes[2]?.name} -> [3]:{futureNodes[3]?.name}");
    }
    void AdvanceAndReplenishPath()
    {
        // 陣列元素集體往前遞補
        futureNodes[0] = futureNodes[1];
        futureNodes[1] = futureNodes[2];
        futureNodes[2] = futureNodes[3];
        // 幫隊伍尾端的 [3]隨機挑選一顆新的延伸點
        futureNodes[3] = PickRandomChoice(futureNodes[2]);
        //Debug.Log($"<color=cyan>[路徑更新]</color> {gameObject.name} 的預測軌道更新：底角[0]:{futureNodes[0]?.name} -> 預判[1]:{futureNodes[1]?.name} -> 遠方[2]:{futureNodes[2]?.name} -> 延伸[3]:{futureNodes[3]?.name}");
    }
    // ---------------------------------------------------------
    // 輔助函式：計算貝茲曲線座標
    // ---------------------------------------------------------
    Vector3 GetBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        t = Mathf.Clamp01(t);
        float oneMinusT = 1f - t;
        // 二次貝茲公式：B(t) = (1-t)^2*P0 + 2(1-t)t*P1 + t^2*P2
        return oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2;
    }
    // ---------------------------------------------------------
    // 輔助函式：執行物理移動與轉向
    // ---------------------------------------------------------
    void ApplyFinalMovement(Vector3 moveDirection, float drivePower)
    {
        if (moveDirection == Vector3.zero) return;

        // 為了防止車子因為上下坡度在 Y 軸上亂翹，我們只保留水平面（XZ軸）的轉向方向
        moveDirection.y = 0;
        if (moveDirection == Vector3.zero) return;

        // 1. 處理轉向 (動態調整 Slerp 的速度)
        Quaternion targetRotation = Quaternion.LookRotation(moveDirection);

        // 💡 核心優化：計算當前車頭方向與目標移動方向的「夾角」
        float currentAngle = Vector3.Angle(transform.forward, moveDirection);

        // 設定基礎轉向速度為 4.5f（你可以自由微調它，數值越小越柔和）
        float baseTurnSpeed = 3f;
        float dynamicTurnSpeed;

        if (isBezierActive)
        {
            // 如果已經進入貝茲曲線，代表是真正的大彎，給予比較穩定且略快的轉向反應
            dynamicTurnSpeed = 8.0f;
        }
        else
        {
            // 【小角度微調優化】
            // 利用 Mathf.Lerp，當夾角很小（如 1°~5° 換車道、走直線修正）時，dynamicTurnSpeed 會趨近於 baseTurnSpeed (4.5f)，轉起來極度平滑自然。
            // 當夾角越大（快要進入大彎前）時，動態加快轉速（最高可達 baseTurnSpeed * 2.5f），確保車子在急彎前轉得過去。
            dynamicTurnSpeed = Mathf.Lerp(baseTurnSpeed, baseTurnSpeed * 2.5f, currentAngle / 90f);
        }
        // 讓車頭用計算出來的動態速度，優雅且絲滑地「滑」向目標方向
        float rotSpeed = (isYieldingForAmbulance || isPulledOverForAmbulance || bypassPhase >= 1) ? alignSmoothSpeed : 5f;
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * dynamicTurnSpeed);

        // 2. 處理位移 (沿著目前的車頭方向前進)
        float moveStep = currentRandomSpeed * drivePower * Time.deltaTime;

        // 🟢 移動總閘門：不管上面哪套速度邏輯出錯，這一幀的位移絕不允許穿進前車。
        //    從車頭發一個薄片 BoxCast，位移最多走到「與前車間距 - minGap」為止。
        //    機車要能鑽車，所以box 收窄、間距放小，否則會把原本的鑽車行為整個擋死。
        if (useMoveGate && moveStep > 0f)
        {
            float minGap = isScooter ? 0.6f : 1.5f;
            float frontOffset = isScooter ? 1.0f : 2.0f;
            Vector3 gateHalf = isScooter ? new Vector3(0.35f, 0.5f, 0.05f) : new Vector3(0.9f, 0.5f, 0.05f);
            Vector3 gateOrigin = transform.position + Vector3.up * 0.5f + transform.forward * frontOffset;

            if (Physics.BoxCast(gateOrigin, gateHalf, transform.forward, out RaycastHit gateHit,
                                transform.rotation, moveStep + minGap, carLayer, QueryTriggerInteraction.Ignore))
            {
                CarNavigation gateNav = gateHit.collider.GetComponentInParent<CarNavigation>();
                ReverseThreatAICar gateThreat = gateHit.collider.GetComponentInParent<ReverseThreatAICar>();

                bool gateIsVehicle = !IsMyOwnColliderNav(gateHit.collider) && gateNav != this &&
                                     (IsPlayerColliderNav(gateHit.collider) || gateNav != null || gateThreat != null
                                      || ExternalTrafficVehicle.Is(gateHit.collider)); // 🚗[危險駕駛AI整合]

                // 🔧 閘門的過濾條件要跟前方偵測一致，否則會卡死：
                //    前方偵測排除「對向車」與「隔壁讓道中的車」，所以目標速度保持前進；
                //    閘門若不排除，位移被壓成 0，上游每幀又把速度拉回來 → 車永遠卡在原地。
                if (gateIsVehicle && gateUsesCorridorExclusions && !IsPlayerColliderNav(gateHit.collider))
                {
                    Transform vt = gateNav != null ? gateNav.transform
                                 : (gateThreat != null ? gateThreat.transform
                                 : ExternalTrafficVehicle.TransformOf(gateHit.collider)); // 🚗[危險駕駛AI整合]
                    bool isOncoming = vt != null && Vector3.Dot(transform.forward, vt.forward) < 0f;

                    // ↰ 小路口左轉讓右轉：對方正在右轉且很近時不豁免，閘門要照樣擋住
                    if ((isOncoming || IsBesideYieldingVehicle(gateHit.collider))
                        && !MustYieldToRightTurner(gateHit.collider))
                        gateIsVehicle = false;
                }

                if (gateIsVehicle)
                {
                    moveStep = Mathf.Max(0f, gateHit.distance - minGap);
                }
                else
                {
                    // 被排除的對象仍要防止真的穿模：保留一道硬性最小間距
                    moveStep = Mathf.Min(moveStep, Mathf.Max(0f, gateHit.distance - gateHardMinGap));
                }
            }
        }

        transform.position += transform.forward * moveStep;

        // 3. 紀錄最後方向，供路徑結束 (isPathCompleted) 後持續直線行駛使用
        lastDirection = transform.forward;

        // 將移動方向與當前動力傳入，驅動前輪動效
        AnimateWheels(moveDirection, drivePower);
    }
    // ---------------------------------------------------------
    // 視覺優化：計算並套用前輪的轉向與滾動動效
    // ---------------------------------------------------------
    void AnimateWheels(Vector3 targetMoveDir, float drivePower)
    {
        float targetSteerAngle = 0f;
        // 1. 轉向角度計算 (Y軸)
        if (targetMoveDir != Vector3.zero && drivePower > 0.001f)
        {
            targetSteerAngle = Vector3.SignedAngle(transform.forward, targetMoveDir, transform.up);
            targetSteerAngle = Mathf.Clamp(targetSteerAngle, -maxSteeringAngle, maxSteeringAngle);
        }
        currentSteerRotation = Mathf.MoveTowards(currentSteerRotation, targetSteerAngle, steeringSmoothSpeed * 10f * Time.deltaTime);

        // 2. 滾動角度計算 (X軸)
        float realSpeedMS = currentRandomSpeed * drivePower;
        float angularVelocity = (realSpeedMS / wheelRadius) * Mathf.Rad2Deg;

        currentRollRotation += angularVelocity * Time.deltaTime;
        currentRollRotation = Mathf.Repeat(currentRollRotation, 360f);

        // 3. 實際套用到所有綁定的前輪 (滾動 X + 轉向 Y)
        if (frontLeftWheels != null)
        {
            foreach (var wheel in frontLeftWheels)
            {
                if (wheel != null) wheel.localRotation = Quaternion.Euler(currentRollRotation, currentSteerRotation, 0f);
            }
        }
        if (frontRightWheels != null)
        {
            foreach (var wheel in frontRightWheels)
            {
                if (wheel != null) wheel.localRotation = Quaternion.Euler(currentRollRotation, currentSteerRotation, 0f);
            }
        }

        // 4. 實際套用到所有綁定的後輪 (只有滾動 X，Y軸保持 0 不轉向)
        if (backLeftWheels != null)
        {
            foreach (var wheel in backLeftWheels)
            {
                if (wheel != null) wheel.localRotation = Quaternion.Euler(currentRollRotation, 0f, 0f);
            }
        }
        if (backRightWheels != null)
        {
            foreach (var wheel in backRightWheels)
            {
                if (wheel != null) wheel.localRotation = Quaternion.Euler(currentRollRotation, 0f, 0f);
            }
        }
    }
    // ---------------------------------------------------------
    // 視覺優化：綜合車輛行為動態，即時驅動所有車燈狀態
    // ---------------------------------------------------------
    void AnimateLights()
    {
        // 核心邏輯一：煞車燈（降速/主動減速即亮起）
        // 透過比對「這一幀」與「上一幀」的動力差，抓取車子是否在主動減速
        float driveDelta = smoothDrive - lastSmoothDrive;
        // 只要動力在減少 (低於微小閾值 -0.002f 避免浮點數抖動)，就判定為正在煞車降速
        bool isDecelerating = driveDelta < -0.002f;
        // 煞車燈亮起條件：正在降速 OR 幾乎完全停下 OR 沒有導航點
        bool shouldBrakeLightOn = isDecelerating || (smoothDrive < 0.05f) || (currentWaypointNode == null);
        SetLightsState(brakeLights, shouldBrakeLightOn, brakeColor);
        // 紀錄這一幀的動力，供下一幀比對減速
        lastSmoothDrive = smoothDrive;

        // 核心邏輯二：方向燈
        bool showLeftIndicator = false;
        bool showRightIndicator = false;
        if (!isBezierActive && futureNodes != null)
        {
            if (isIncomingTurn && brakeDistanceBasis < approachDistance)
            {
                WaypointNode targetAfterTurn = null;
                WaypointNode turnEntryNode = futureNodes[0]; // 預設進彎入口是 [0]

                // 超前看點，決定出彎後的目標點
                if (futureNodes.Length > 3 && futureNodes[3] != null) { targetAfterTurn = futureNodes[3]; turnEntryNode = futureNodes[2]; }
                else if (futureNodes.Length > 2 && futureNodes[2] != null) { targetAfterTurn = futureNodes[2]; turnEntryNode = futureNodes[1]; }
                else if (futureNodes.Length > 1 && futureNodes[1] != null) { targetAfterTurn = futureNodes[1]; turnEntryNode = futureNodes[0]; }
                else { targetAfterTurn = futureNodes[0]; turnEntryNode = transform.GetComponent<WaypointNode>(); /* 備用保險 */ }

                if (targetAfterTurn != null && turnEntryNode != null)
                {
                    // 1. 計算「進入彎道前」的道路基準向量 (優先用節點連線，排除車體當前位置與朝向)
                    Vector3 dirIncoming = transform.forward;
                    if (lastWaypointNode != null && turnEntryNode != null && lastWaypointNode != turnEntryNode)
                    {
                        dirIncoming = (turnEntryNode.transform.position - lastWaypointNode.transform.position).normalized;
                    }

                    // 2. 計算「彎道經過後」的道路走向向量
                    Vector3 roadTurnDir = (targetAfterTurn.transform.position - turnEntryNode.transform.position).normalized;

                    // 3. 使用純粹的道路外積 (Vector3.Cross) 來判斷左右轉，不受車體 Offset 或 Angle 影響
                    Vector3 cross = Vector3.Cross(dirIncoming, roadTurnDir);

                    if (cross.y > 0.05f)
                    {
                        showRightIndicator = true;
                    }
                    else if (cross.y < -0.05f)
                    {
                        showLeftIndicator = true;
                    }
                }
            }
        }
        // 核心邏輯三：方向燈閃爍計時器與動態套用 (保持不變)
        SetLightsState(reverseLights, isReversing, reverseColor);

        bool isBlinkCycleOn = (Time.time % (indicatorBlinkRate * 2f)) < indicatorBlinkRate;

        SetLightsState(leftIndicators, showLeftIndicator && isBlinkCycleOn, indicatorColor);
        SetLightsState(rightIndicators, showRightIndicator && isBlinkCycleOn, indicatorColor);
    }

    // 輔助函式：切換車燈狀態（保持物件開啟，僅透過切換自發光顏色來達成點亮/熄滅）
    void SetLightsState(GameObject[] lights, bool state, Color emitColor)
    {
        if (lights == null) return;

        foreach (var lightObj in lights)
        {
            if (lightObj == null) continue;

            // 保持物件永遠是開啟的 (Active)，這樣玻璃反射與外觀質感才不會消失
            if (!lightObj.activeSelf) lightObj.SetActive(true);

            // 做法 1：處理實體 Light 元件（如 Spot Light），開燈時同步更改光源顏色
            Light lightComponent = lightObj.GetComponent<Light>();
            if (lightComponent != null)
            {
                lightComponent.enabled = state;
                if (state) lightComponent.color = emitColor;
            }

            // 做法 2：處理材質球自發光 (Emission)
            Renderer rendererComponent = lightObj.GetComponent<Renderer>();
            if (rendererComponent != null)
            {
                foreach (var mat in rendererComponent.materials)
                {
                    if (state)
                    {
                        // 開燈：啟動自發光，並填入設定的 HDR 顏色
                        mat.EnableKeyword("_EMISSION");
                        if (mat.HasProperty("_EmissionColor"))
                        {
                            mat.SetColor("_EmissionColor", emitColor);
                        }
                    }
                    else
                    {
                        // 關燈：不關閉關鍵字，而是把發光顏色改為「黑色」
                        // 這樣材質球會停止散發光芒，但原本的貼圖顏色與玻璃反光都會完美保留！
                        if (mat.HasProperty("_EmissionColor"))
                        {
                            mat.SetColor("_EmissionColor", Color.black);
                        }
                    }
                }
            }
        }
    }
    void MoveCar(float drivePower)
    {
        // 如果已經在最後直行階段
        if (isFinishingPath)
        {
            ApplyFinalMovement(constantMoveDir, drivePower);
            // 檢查行駛距離
            if (Vector3.Distance(transform.position, finishStartPosition) >= despawnDistance)
            {
                Destroy(gameObject);
            }
            return;
        }
        if (futureNodes[0] == null)
        {
            EnterFinishingMode(transform.forward);
            return;
        }

        currentWaypointNode = futureNodes[0]; // 保持對舊變數的相容，讓 Gizmos 繼續運作
        float distToNode = Vector3.Distance(transform.position, futureNodes[0].transform.position);
        //if (futureNodes[0] != null)
        //{
        //    // 提示：如果嫌畫面太洗，可以改成 if (gameObject.name == "你的測試車輛名稱") 只抓一輛車
        //    Debug.Log($"<color=yellow>[移動監控]</color> 車輛:{gameObject.name} | 目標[0]:{futureNodes[0].name} | 距離:{distToNode:F2}m | 判定距離:{stopDistance}m | 貝茲狀態:{isBezierActive}");
        //}
        //else
        //{
        //    Debug.LogWarning($"<color=red>[警告]</color> {gameObject.name} 的 futureNodes[0] 是空的！車子失去導航目標！");
        //}
        // 1. 偵測是否進入 貝茲觸發區「轉向決策區」
        if (!isBezierActive && distToNode < bezierTriggerDistance)
        {
            if (futureNodes[1] != null && futureNodes[2] != null)
            {
                float pathAngle = 0f;
                if (lastWaypointNode != null)
                {
                    // 使用已定案的 futureNodes 計算夾角，絕不抽錯點
                    Vector3 dir1 = (futureNodes[0].transform.position - lastWaypointNode.transform.position).normalized;
                    Vector3 dir2 = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
                    pathAngle = Vector3.Angle(dir1, dir2);
                }
                // 這個要留不要刪 判斷角度用
                //Debug.Log($"<color=white>[路徑分析]</color> P0:{lastWaypointNode.name} -> P1:{p1Node.name}, P2:{p2Node.name} | 夾角: {pathAngle:F1} 度");
                // 只有夾角大於 15 度才啟動貝茲（代表是真的轉彎）a
                if (pathAngle > 15f)
                {
                    float distP1ToP2 = Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                    // 第二個點距離<20m，才啟動跨節點合併，否則就當作一般轉彎處理
                    if (distP1ToP2 < 25f)
                    {
                        //Debug.Log($"<color=cyan>[大轉彎合併]</color> 偵測到連續路口，目標點: {futureNodes[0].name} -> {futureNodes[1].name}，距離: {distP1ToP2:F1}m，啟動跨節點貝茲曲線");
                        // 執行貝茲曲線：P0(現在), P1(futureNodes[0]), P2(futureNodes[1])
                        StartBezierTurn_Improved(transform.position, futureNodes[0].transform.position, futureNodes[1].transform.position);
                    }
                    else
                    {
                        // 距離太遠，維持原有的短程貝茲
                        StartBezierTurn_Improved(transform.position, futureNodes[0].transform.position, (futureNodes[0].transform.position + futureNodes[1].transform.position) / 2f);
                    }
                }
            }
        }
        Vector3 moveDir;
        if (isBezierActive)
        {
            float speed = currentRandomSpeed * drivePower;
            // 🚑 轉彎進行中遇到救護車:
            //    還沒進路口 → 停住別轉進去;已經在路口裡 → 加速把彎轉完離開(停在路口反而堵死救護車)
            if (blockTurnForAmbulance)
            {
                if (!isInIntersectionZone)
                {
                    smoothDrive = Mathf.Lerp(smoothDrive, 0f, Time.deltaTime * 5f);

                    // 車頭轉回原本車道方向,不要停在「指著救護車」的角度上擋路
                    if (lastWaypointNode != null && futureNodes[0] != null)
                    {
                        Vector3 backDirG = futureNodes[0].transform.position - lastWaypointNode.transform.position;
                        backDirG.y = 0f;
                        if (backDirG.sqrMagnitude > 0.01f)
                            transform.rotation = Quaternion.Slerp(transform.rotation,
                                Quaternion.LookRotation(backDirG.normalized, Vector3.up),
                                Time.deltaTime * alignSmoothSpeed);
                    }

                    // 🔧 還沒進路口就取消這次轉彎,交還給正常導航。
                    //    只停在原地的話 isBezierActive 會一直是 true,
                    //    偏移邏輯永遠進不去 → 想轉彎的車等於完全不讓救護車。
                    //    取消後這一幀就能開始靠邊,救護車過去後 blockTurnForAmbulance 解除,
                    //    到下一個節點會重新觸發轉彎 —— 也就是「先讓完再轉」。
                    isBezierActive = false;
                    amILeftTurning = false;
                    amIRightTurning = false;
                    return;
                }
                smoothDrive = Mathf.Max(smoothDrive, clearJunctionPower);   // 已在路口 → 趕快清空
            }

            bezierT += (speed * Time.deltaTime) / 15f;
            Vector3 nextPos = GetBezierPoint(p0, p1, p2, bezierT);
            moveDir = (nextPos - transform.position).normalized;
            if (bezierT >= 1.0f)
            {
                isBezierActive = false;
                amILeftTurning = false;
                amIRightTurning = false;
                float distP1ToP2 = Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                //Debug.Log($"<color=green>[貝茲結束]</color> 車輛：{gameObject.name}，回歸直線導航。時間：{Time.time}");
                if (distP1ToP2 < 20f)

                {
                    // 因為剛剛是大轉彎，跨越了 0 直接開到了 1 的位置，所以等同於過了兩個點
                    lastWaypointNode = futureNodes[1];
                    AdvanceAndReplenishPath(); // [0]變[1]
                    AdvanceAndReplenishPath(); // [1]變[2]，重新隨機抽最前方的點
                }
                else
                {
                    // 一般轉彎，等同於過了 0 這個點
                    lastWaypointNode = futureNodes[0];
                    AdvanceAndReplenishPath(); // 正常遞補一次
                }
                if (futureNodes[0] == null)
                {
                    Vector3 exitDir = transform.forward; // 用當前車頭當作銷毀方向
                    EnterFinishingMode(exitDir);
                    return;
                }
                //Debug.Log($"<color=cyan>[彎道結束遞補]</color> {gameObject.name} 轉彎完成！新目標：{futureNodes[0]?.name}");
                // 只有在「沒有勾選禁用禮讓」時，才啟動冷卻鎖！
                if (!nodeDisablesYield)
                {
                    leftTurnYieldCooldown = 3.0f; // 轉彎剛完成,偵測左轉偵測框冷卻鎖
                    exitBezierCooldown = 1.5f; // 轉彎剛完成，啟動轉彎預減速冷卻鎖
                }
            }
        }
        else
        {
            // 【汽車&機車】算出一套目標偏移量：若是機車則算偏移，一般汽車直接為 0
            float targetLateralOffset = isScooter ? ClampOffsetForScooterLane(CalculateTargetLateralOffset()) : 0f;
            // 【機車專用】平滑靠近目標偏移量，再把「目標點」往側邊挪，算出帶偏移的行進方向
            currentLateralOffset = Mathf.MoveTowards(currentLateralOffset, targetLateralOffset, offsetSmoothSpeed * Time.deltaTime);
            Vector3 offsetTargetPos = futureNodes[0].transform.position + transform.right * currentLateralOffset;
            moveDir = (offsetTargetPos - transform.position).normalized;

            // 🚑 舊版的避讓偏移已停用 —— 改由 HandleWhiteCarStyleYield() 全權處理,
            //    那個方法在 Update 更早的地方就接管移動並 return,根本不會走到這裡。
            //    程式碼保留是為了方便對照/回退(把下面的 false && 拿掉即可恢復舊行為)。
            if (false && currentYieldOffset > 0.01f && lastWaypointNode != null)
            {
                Vector3 yieldPathDir = futureNodes[0].transform.position - lastWaypointNode.transform.position;
                yieldPathDir.y = 0f;
                if (yieldPathDir.sqrMagnitude > 0.01f)
                {
                    yieldPathDir.Normalize();
                    Vector3 yieldRight = Vector3.Cross(Vector3.up, yieldPathDir);

                    float appliedYieldOffset = currentYieldOffset;
                    if (Physics.Raycast(transform.position + Vector3.up * 0.5f, yieldRight, out RaycastHit rBlockG,
                            currentYieldOffset + 1f, carLayer, QueryTriggerInteraction.Ignore)
                        && rBlockG.collider.transform.root != transform.root)
                        appliedYieldOffset = Mathf.Max(0f, rBlockG.distance - 1.2f);

                    Vector3 diagG = (yieldPathDir + yieldRight).normalized;
                    bool playerAtSideG = false;
                    if (Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.7f, diagG, out RaycastHit dBlockG,
                            currentYieldOffset + 3f, carLayer, QueryTriggerInteraction.Ignore)
                        && dBlockG.collider.transform.root != transform.root)
                    {
                        appliedYieldOffset = Mathf.Min(appliedYieldOffset, Mathf.Max(0f, dBlockG.distance * 0.6f - 0.8f));
                        if (dBlockG.collider.CompareTag("Player") || dBlockG.collider.transform.root.CompareTag("Player"))
                            playerAtSideG = true;
                    }
                    if (playerAtSideG)
                    {
                        playerBlockYieldTimer += Time.deltaTime;
                        if (playerBlockYieldTimer > playerBlockDespawnTime) { ForceFadeOutClear(); return; }
                    }
                    else playerBlockYieldTimer = 0f;

                    Vector3 yieldTargetPos = transform.position + yieldPathDir * yieldCutLookAhead + yieldRight * appliedYieldOffset;
                    moveDir = (yieldTargetPos - transform.position).normalized;

                    // 已靠到 8 成 → 鎖存停穩,車頭轉正對齊路向(屁股收進去,不擋救護車)
                    Vector3 toCarY = transform.position - lastWaypointNode.transform.position; toCarY.y = 0f;
                    // 靠到 8 成就進入「停穩」狀態(車頭轉正、屁股收進去)
                    float latNow = Vector3.Dot(toCarY, yieldRight);
                    if (latNow >= appliedYieldOffset * 0.8f) isPulledOverForAmbulance = true;

                    // 側移進度卡住(被前車/旁車擋住開不動)→ 也視為到位,直接轉正,絕不永遠斜著
                    if (lastYieldLateral > -900f && Mathf.Abs(latNow - lastYieldLateral) < 0.03f)
                    {
                        yieldStallTimer += Time.deltaTime;
                        if (yieldStallTimer > 1.0f) isPulledOverForAmbulance = true;
                    }
                    else yieldStallTimer = 0f;
                    lastYieldLateral = latNow;

                    // 🔧 靠邊到位 → 車頭轉正並「停住」等救護車通過(比照白車)。
                    //    舊版只改了 moveDir,速度完全沒動 → 灰車轉正後仍以一般速度往前開,
                    //    等於一路開一路讓,既不像真的靠邊停讓,也會一直跟著救護車跑。
                    //    例外:我人在路口裡而救護車在後方 → 該快速開出去淨空,不能停在路中央。
                    if (isPulledOverForAmbulance)
                    {
                        moveDir = yieldPathDir;
                        if (!clearJunctionForAmbulance)
                        {
                            smoothDrive = Mathf.Lerp(smoothDrive, 0f, Time.deltaTime * 5f);
                            realTimeSpeed = 0f;
                        }
                    }
                }
            }

            // 🚧 故障車繞行:導航目標往左偏,通過後自動切回
            if (accidentBypassRamp > 0.01f && lastWaypointNode != null)
            {
                Vector3 bDir = futureNodes[0].transform.position - lastWaypointNode.transform.position;
                bDir.y = 0f;
                if (bDir.sqrMagnitude > 0.01f)
                {
                    bDir.Normalize();
                    Vector3 bLeft = -Vector3.Cross(Vector3.up, bDir);
                    Vector3 bypassTarget = futureNodes[0].transform.position + bLeft * accidentBypassRamp;
                    float distToAimG = Vector3.Distance(transform.position, bypassTarget);
                    float fadeG = Mathf.Clamp01(accidentBypassRamp / 0.6f);
                    if (distToAimG < bypassLookAhead) bypassTarget += bDir * ((bypassLookAhead - distToAimG) * fadeG);
                    Vector3 bypassDirG = (bypassTarget - transform.position).normalized;

                    // 🛡️ 偏出去/切回的行進方向上有車 → 不硬切,先沿路直行等淨空(跟白車同一套保護)
                    if (Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.8f, bypassDirG,
                            out RaycastHit bypassBlockG, 6f, carLayer, QueryTriggerInteraction.Ignore)
                        && bypassBlockG.collider.transform.root != transform.root)
                        moveDir = bDir;   // 保持原方向直行,不往側邊擠
                    else
                        moveDir = bypassDirG;
                }
            }


            // 直線路段：到達點後直接遞補換下一個
            if (distToNode < stopDistance + currentYieldOffset + accidentBypassRamp)
            {
                lastWaypointNode = futureNodes[0];
                AdvanceAndReplenishPath(); // 呼叫你寫好的遞補邏輯
                // 遞補後如果頭節點空了，代表地圖到盡頭
                if (futureNodes[0] == null)
                {
                    EnterFinishingMode(moveDir);
                    return;
                }
            }
        }
        ApplyFinalMovement(moveDir, drivePower);
    }
    // 【機車專用】處理機車在靜止或車速極低時的橫向位置微調 (避免靜止時無法鑽車/側切)
    void UpdateScooterLateralOffset()
    {
        if (!isScooter || isBezierActive || isFinishingPath) return;
        if (futureNodes[0] == null) return;
        if (smoothDrive <= 0.001f) return; // 靜止時不做橫向偏移，避免鑽車時無法微調
        // 只有在車子有在移動時，才正常的計算與套用橫向偏移
        float targetLateralOffset = ClampOffsetForScooterLane(CalculateTargetLateralOffset());
        currentLateralOffset = Mathf.MoveTowards(currentLateralOffset, targetLateralOffset, offsetSmoothSpeed * Time.deltaTime);
    }
    // 機車專用道比較窄：算完的巡航偏移要再收斂一次，否則機車會壓到車道邊線
    float ClampOffsetForScooterLane(float offset)
    {
        if (futureNodes[0] != null && futureNodes[0].isScooterLane)
            return Mathf.Clamp(offset, -scooterLaneOffsetRange, scooterLaneOffsetRange);
        return offset;
    }
    // 【機車專用】統一計算機車目標橫向偏移量（結合自然巡航與鑽車避讓邏輯）
    // 優先順序：禁止偏移點 > 轉彎靠側 > 鑽車避讓 > 預設巡航
    float CalculateTargetLateralOffset()
    {
        // 如果節點設定禁止機車偏移 強制回歸 0
        if (futureNodes[0] == null || futureNodes[0].disallowScooterOffset) return 0f;
        // 超前預判轉彎靠側機制
        bool isGoingLeft = false;
        bool isGoingRight = false;
        float targetTurnDistanceBasis = 999f; // 用於計算漸進靠側的目標距離
        // 狀態 A：若已經在過彎（貝茲曲線啟動中），直接繼承轉向狀態
        if (isBezierActive)
        {
            isGoingLeft = amILeftTurning;
            isGoingRight = amIRightTurning;
            targetTurnDistanceBasis = 0f; // 已進入彎道，全額套用靠側偏移
        }
        // 狀態 B：未在過彎且未處於出彎冷卻鎖，進行 [0]->[1]->[2]->[3] 預判
        else if (exitBezierCooldown <= 0f)
        {
            float d0 = Vector3.Distance(transform.position, futureNodes[0].transform.position);
            // --- 預判 1：檢查當前目標點 [0] -> [1] 是否為彎道 ---
            if (futureNodes[1] != null)
            {
                Vector3 dirToTarget = (futureNodes[0].transform.position - transform.position).normalized;
                Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;

                if (Vector3.Angle(dirToTarget, dirNext) > 15f)
                {
                    Vector3 cross = Vector3.Cross(dirToTarget, dirNext);
                    isGoingLeft = cross.y < 0f;
                    isGoingRight = cross.y > 0f;
                    targetTurnDistanceBasis = 0;
                }
            }

            // --- 預判 2：檢查下下個點 [1] -> [2] 是否為彎道 ---
            if (!isGoingLeft && !isGoingRight && futureNodes[1] != null && futureNodes[2] != null)
            {
                Vector3 dirNext = (futureNodes[1].transform.position - futureNodes[0].transform.position).normalized;
                Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;

                if (Vector3.Angle(dirNext, dirNextNext) > 15f)
                {
                    Vector3 cross = Vector3.Cross(dirNext, dirNextNext);
                    isGoingLeft = cross.y < 0f;
                    isGoingRight = cross.y > 0f;
                    targetTurnDistanceBasis = d0;
                }
            }

            // --- 預判 3：檢查下下下個點 [2] -> [3] 是否為彎道 ---
            if (!isGoingLeft && !isGoingRight && futureNodes[1] != null && futureNodes[2] != null && futureNodes[3] != null)
            {
                Vector3 dirNextNext = (futureNodes[2].transform.position - futureNodes[1].transform.position).normalized;
                Vector3 dirNextNextNext = (futureNodes[3].transform.position - futureNodes[2].transform.position).normalized;

                if (Vector3.Angle(dirNextNext, dirNextNextNext) > 15f)
                {
                    Vector3 cross = Vector3.Cross(dirNextNext, dirNextNextNext);
                    isGoingLeft = cross.y < 0f;
                    isGoingRight = cross.y > 0f;
                    targetTurnDistanceBasis = d0 + Vector3.Distance(futureNodes[0].transform.position, futureNodes[1].transform.position);
                }
            }
        }

        // 觸發預判轉彎靠側（僅在離路口 approachDistance 範圍內生效，並隨距離接近而顯著靠側）
        if ((isGoingLeft || isGoingRight) && targetTurnDistanceBasis < (approachDistance + 20))
        {
            float earlyDistance = 20f; // 提前完畢的距離
            // 1. 先把當前距離減去提前量，並限制在 0 以上
            float effectiveDistance = Mathf.Max(0f, targetTurnDistanceBasis - earlyDistance);
            // 2. 有效的變動區間長度只有 (approachDistance - earlyDistance)
            float effectiveRange = approachDistance + 20 - earlyDistance;
            // 3. 計算進度 (確保區間長度大於 0 避免除以 0)
            float progress = 1.0f - Mathf.Clamp01(effectiveDistance / Mathf.Max(0.1f, effectiveRange));
            // 2. 開根號：讓進度迅速拉高
            float turnWeight = Mathf.Sqrt(progress);
            float calculatedOffset = turnLateralOffset * turnWeight;

            if (isGoingLeft)
            {
                float targetOffset = -calculatedOffset;
                if (Mathf.Abs(targetOffset - lastLoggedTargetOffset) > 0.05f)
                {
                    lastLoggedTargetOffset = targetOffset;
                    //.Log($"<color=cyan>[機車 {gameObject.name}]</color> 觸發【左轉預判靠側】：{targetOffset:F2} m (距彎道: {targetTurnDistanceBasis:F1}m)");
                }
                return targetOffset;
            }
            else if (isGoingRight)
            {
                float targetOffset = calculatedOffset;
                if (Mathf.Abs(targetOffset - lastLoggedTargetOffset) > 0.05f)
                {
                    lastLoggedTargetOffset = targetOffset;
                    //Debug.Log($"<color=cyan>[機車 {gameObject.name}]</color> 觸發【右轉預判靠側】：{targetOffset:F2} m (距彎道: {targetTurnDistanceBasis:F1}m)");
                }
                return targetOffset;
            }
        }
        // 預設目標為出生時抽到的隨機巡航偏移量
        float target = cruiseLateralOffset;
        // 允許「鑽車/側切」情境：停紅燈、起步延遲、或低速行駛時
        bool allowFilter = (isWaitingAtTrafficLight || currentStartDelay > 0f || smoothDrive < 0.3f);
        // 如果觸發側切決策
        if (allowFilter && hasCommittedSide)
        {
            // 右側被擋就往左避讓 (+1)，左側被擋就往右避讓 (-1)
            float filterDir = committedSideIsRight ? -1f : 1f; // 右側被擋往左閃，左側被擋往右閃
            float desiredOffset = filterDir * cruiseOffsetRange;
            // 進行實體碰撞射線檢查，確保欲避讓的方向無牆壁或車輛阻擋
            Vector3 checkOrigin = transform.position + Vector3.up * 0.5f;
            Vector3 checkDir = filterDir > 0f ? transform.right : -transform.right;
            float checkDist = Mathf.Abs(desiredOffset) + 1.0f;

            if (!Physics.Raycast(checkOrigin, checkDir, checkDist, carLayer))
            {
                // 安全，允許套用側切偏移
                target = Mathf.Clamp(desiredOffset, -cruiseOffsetRange, cruiseOffsetRange);
            }
        }
        // 安全二次檢查：若最終目標偏移方向有實體障礙物，強制歸零避免穿模
        if (target != 0f)
        {
            Vector3 safetyOrigin = transform.position + Vector3.up * 0.5f;
            Vector3 safetyDir = target > 0f ? transform.right : -transform.right;
            if (Physics.Raycast(safetyOrigin, safetyDir, Mathf.Abs(target) + 0.5f, carLayer))
                target = 0f;
        }
        // 只有當目標偏移量有改變時，才印出 Log，避免洗版
        if (Mathf.Abs(target - lastLoggedTargetOffset) > 0.01f)
        {
            lastLoggedTargetOffset = target;

            //if (target == cruiseLateralOffset)
            //{
            //    Debug.Log($"<color=cyan>[機車 {gameObject.name}]</color> 切換為【正常巡航偏移】：{target:F2} m");
            //}
            //else if (target == 0f)
            //{
            //    Debug.Log($"<color=yellow>[機車 {gameObject.name}]</color> 避障/受限，【回歸路線中央】：0.00 m");
            //}
            //else
            //{
            //    Debug.Log($"<color=green>[機車 {gameObject.name}]</color> 觸發【鑽車/側切偏移】：{target:F2} m");
            //}
        }
        return target;
    }
    // 輔助方法：初始化直行銷毀狀態
    void EnterFinishingMode(Vector3 lastDir)
    {
        isFinishingPath = true;
        finishStartPosition = transform.position;
        // 使用當前車頭方向或最後的路徑方向，確保不會突然轉向
        constantMoveDir = lastDir; // 直接傳入方向，不要再讀取 currentWaypointNode.transform
        // 清空導航變數，防止其他地方誤讀
        currentWaypointNode = null;
        isBezierActive = false;
        //Debug.Log($"<color=cyan>[路徑結束]</color> {gameObject.name} 進入 50m 直行銷毀模式");
    }
    private bool shouldShowLeftTurnBox = false;
    // 修改參數，直接傳入三個確定的座標點
    // 🚑 前方路徑是否即將轉彎(用路網夾角判斷)
    private bool IsUpcomingTurn()
    {
        if (lastWaypointNode == null || futureNodes[0] == null || futureNodes[1] == null) return false;
        Vector3 d1 = futureNodes[0].transform.position - lastWaypointNode.transform.position; d1.y = 0f;
        Vector3 d2 = futureNodes[1].transform.position - futureNodes[0].transform.position; d2.y = 0f;
        if (d1.sqrMagnitude < 0.01f || d2.sqrMagnitude < 0.01f) return false;
        return Vector3.Angle(d1, d2) > 15f;
    }

    void StartBezierTurn_Improved(Vector3 startPoint, Vector3 controlPoint, Vector3 endPoint)
    {
        // 🚑 救護車還沒通過 → 不進轉彎(轉彎會橫切到救護車的行進路徑上,會撞)
        if (blockTurnForAmbulance) return;

        isBezierActive = true;
        bezierT = 0f;
        p0 = startPoint;     // 現在位置
        p1 = controlPoint;   // C_L
        p2 = endPoint;       // -D
        // 重新計算這段貝茲的長度，用來修正 bezierT 的增加速度
        // 如果路徑變長了，bezierT 增加的速度要變慢，才不會閃現
        float approxDistance = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2);
        // 更新轉向判定邏輯
        Vector3 overallTurnDir = (p2 - transform.position).normalized;
        float turnAngle = Vector3.Angle(transform.forward, overallTurnDir);
        Vector3 turnCross = Vector3.Cross(transform.forward, overallTurnDir);
        shouldShowLeftTurnBox = (turnAngle > 10f && turnCross.y < 0);
        amILeftTurning = (turnAngle > 10f && turnCross.y < 0f);
        amIRightTurning = (turnAngle > 10f && turnCross.y > 0f);
        //Debug.Log($"<color=yellow>[貝茲啟動]</color> P0:{p0} -> P1:{p1} -> P2:{p2}");
    }
    // 當進入紅綠燈觸發器區域時觸發
    private void OnTriggerStay(Collider other)
    {
        // 碰到銷毀區
        if (other.CompareTag("Despawn")) Destroy(gameObject);
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Intersection")) isInIntersectionZone = true; // 💀 進入路口範圍
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("TrafficLight")) isWaitingAtTrafficLight = false;
        if (other.CompareTag("Intersection")) isInIntersectionZone = false;
    }
    // 繪製開發輔助線
    void OnDrawGizmos()
    {
        // 1. 雷達射線顏色根據狀態變化
        Color rayColor = new Color(0.420f, 1.0f, 0.486f); ; // 預設綠色
        if (isWaitingAtTrafficLight)
        {
            // 如果偵測到紅燈，優先權最高,顯示紅色
            rayColor = Color.red;
        }
        else if (finalDrivePower < 1.0f)
        { // 只要有減速（包含完全停止）
            if (finalDrivePower <= 0.05f)
                rayColor = new Color(1f, 0.5f, 0f); // 橘色：完全堵塞
            else
                rayColor = Color.yellow; // 黃色：減速跟車中
        }
        Gizmos.color = rayColor; // 套用顏色
        // --- 計算「這一幀」該有的角度 ---
        // 呼叫 GetCurrentRadarAngle()，它會根據當下的 amILeftTurning 決定角度
        float activeFanAngle = GetCurrentRadarAngle();
        float activeDetectionDistance = GetCurrentDetectionDistance();
        float radarFwd = isScooter ? scooterRadarForwardOffset : radarForwardOffset;
        Vector3 rayOrigin = transform.position + Vector3.up * 0.5f + transform.forward * radarFwd;
        float startAngle = -activeFanAngle * 0.5f;
        // 預防 numberOfRays 為 1 導致除以 0
        float angleStep = (numberOfRays > 1) ? activeFanAngle / (numberOfRays - 1) : 0;
        if (amILeftTurning || amIRightTurning || isBezierActive)
        {
            activeDetectionDistance = Mathf.Max(activeDetectionDistance, 25.0f);
        }
        for (int i = 0; i < numberOfRays; i++)
        {
            float currentAngle = startAngle + (angleStep * i);
            Quaternion rotation = Quaternion.AngleAxis(currentAngle, transform.up);
            Vector3 rayDirection = rotation * transform.forward;
            // 計算長度衰減 (與 Update 保持一致)
            float sideFactor = Mathf.Abs(currentAngle) / (activeFanAngle * 0.5f);
            // 這裡計算出的 rayDistance 才是真正射線撞擊的距離
            float rayDistance = activeDetectionDistance * (1.0f - sideFactor * lateralDetectionMult);
            // 繪製雷達線條
            // 如果是直行狀態，且目前這條射線是正中間 (角度接近 0)
            if (!amILeftTurning && !amIRightTurning && Mathf.Abs(currentAngle) < 1.0f)
            {
                rayDistance *= straightDetectionMult; // 讓中間這條再長 1.5 倍 (例如從 10m 變成 15m)
            }
            // --- 修正點：使用 rayDistance 而不是 activeDetectionDistance ---
            Gizmos.DrawRay(rayOrigin, rayDirection * rayDistance);
        }
        if (!amILeftTurning && !amIRightTurning && !isBezierActive)
        {
            float drawStraightLen = activeDetectionDistance * straightDetectionMult;
            Color sideStraightColor = rayColor;
            Gizmos.color = sideStraightColor;
            // 【汽車&機車】自動套用機車專用的平行射線間距
            float RayLength = isScooter ? scooterStraightRaySideSpacing : straightRaySideSpacing;
            Gizmos.DrawRay(rayOrigin + transform.right * -RayLength, transform.forward * drawStraightLen);
            Gizmos.DrawRay(rayOrigin + transform.right * RayLength, transform.forward * drawStraightLen);
        }
        // 2. 繪製左轉偵測區域 (半透明藍色方塊)
        if (shouldCheckYield)
        {
            // --- 判定方塊顯示顏色 ---
            if (waitingForOppositeStraightCar)
            {
                // 偵測到對向車，顯示半透明紅色
                Gizmos.color = new Color(1, 0, 0, 0.4f);
            }
            else
            {
                // 沒偵測到車，顯示半透明藍色
                Gizmos.color = new Color(0, 0, 1, 0.3f);
            }
            Matrix4x4 oldMatrix = Gizmos.matrix;
            // 使用物件的世界矩陣，讓方塊跟隨車子旋轉與移動
            Gizmos.matrix = transform.localToWorldMatrix;
            // 定義在 Local 座標下的中心與大小
            Vector3 localCenter = new Vector3(turnDetectionSideOffset, 0.5f, 2.0f + turnForwardOffset);
            Vector3 boxSize = turnBoxSize;
            // 繪製方塊
            Gizmos.DrawCube(localCenter, boxSize);
            // 繪製方塊邊框 (選配，這樣看起來更清晰)
            Gizmos.DrawWireCube(localCenter, boxSize);
            Gizmos.matrix = oldMatrix;
        }
        // 3. 停讓偵測框 (修正版)
        if (debugShowStopBox)
        {
            // --- 偵測到車變紅色，沒偵測到維持半透明青色 ---
            if (isCarDetectedAtStop)
                Gizmos.color = new Color(1f, 0f, 0f, 0.4f); // 紅色 (有車阻擋)
            else
                Gizmos.color = new Color(0f, 1f, 1f, 0.2f); // 青色 (安全掃描中)
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(activeStopBoxIsBig ? bigStopBoxOffsetLeft : stopBoxOffsetLeft, activeStopBoxIsBig ? bigStopBoxSize : stopBoxSize);
            if (!debugShowRightTurningStopBox)
            {
                Gizmos.DrawCube(activeStopBoxIsBig ? bigStopBoxOffsetRight : stopBoxOffsetRight, activeStopBoxIsBig ? bigStopBoxSize : stopBoxSize);
            }
            Gizmos.matrix = oldMatrix;
        }
        // 繪製黃色網狀線動態偵測框 (與 Update 100% 同步版)
        if (debugCurrentExitNode != null)
        {
            Vector3 checkPosition = debugCurrentExitNode.transform.TransformPoint(yellowGridBoxOffset);
            // 1. 根據 Update 傳過來的堵塞狀態決定顏色
            Gizmos.color = debugIsExitBlocked ? Color.red : Color.green;
            // 2. 畫一條從車頭連接到「目標動態出口」的引導線
            Gizmos.DrawLine(transform.position + Vector3.up * 0.5f, checkPosition);
            // 3. 變更矩陣以符合動態出口的旋轉與位置
            Matrix4x4 originalMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(checkPosition, debugCurrentExitNode.transform.rotation, Vector3.one);
            // 4. 繪製實心立方體與線框
            if (debugIsExitBlocked)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.3f); // 堵塞：透明紅
                Gizmos.DrawCube(Vector3.zero, yellowGridBoxSize);
                Gizmos.color = Color.red;
                Gizmos.DrawWireCube(Vector3.zero, yellowGridBoxSize);
            }
            else
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.2f); // 安全：透明綠
                Gizmos.DrawCube(Vector3.zero, yellowGridBoxSize);
                Gizmos.color = Color.green;
                Gizmos.DrawWireCube(Vector3.zero, yellowGridBoxSize);
            }
            // 5. 還原矩陣
            Gizmos.matrix = originalMatrix;
        }
    }

    // 🚧 故障車三段式繞行:看到→(左側淨空)左移→轉正直行通過→超過設定距離→(右側淨空)切回
    void HandleAccidentBypass()
    {
        bool seeObs = false; Vector3 obsPosG = Vector3.zero;
        if (!isYieldingForAmbulance && !isBezierActive)
        {
            RaycastHit[] bHits = Physics.SphereCastAll(transform.position + Vector3.up * 0.5f, 1.0f,
                transform.forward, accidentBypassDetect, ~0, QueryTriggerInteraction.Ignore);
            foreach (var bh in bHits)
            {
                if (bh.collider.transform.root == transform.root) continue;
                if (!bh.collider.CompareTag("RoadBlock") && bh.collider.GetComponentInParent<HazardLights>() == null) continue;
                Vector3 relB = transform.InverseTransformPoint(bh.collider.bounds.center);
                if (Mathf.Abs(relB.x) < 3f && relB.z > 0f) { seeObs = true; obsPosG = bh.collider.bounds.center; break; }
            }
        }
        if (seeObs) lastAccidentSeenTime = Time.time;   // 被故障車擋著 → 屬於合理停等,不算卡死

        // 🔍 繞行診斷(在 Inspector 勾 Debug Log Detection 才會印)
        if (debugLogDetection)
        {
            string leftHit = "淨空";
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, -transform.right,
                    out RaycastHit dbgL, accidentBypassOffset + 1.5f, ~0, QueryTriggerInteraction.Ignore)
                && dbgL.collider.transform.root != transform.root)
                leftHit = dbgL.collider.name + " (" + dbgL.distance.ToString("F1") + "m)";
            UnityEngine.Debug.Log("<color=orange>🔍灰車繞行</color> " + name +
                " 看到故障車=" + seeObs + " 相位=" + bypassPhase +
                " 偏移=" + accidentBypassRamp.ToString("F2") +
                " 左側=" + leftHit);
        }
        if (seeObs && bypassPhase != 3) bypassObstaclePos = obsPosG;

        switch (bypassPhase)
        {
            case 0:
                bool leftBlockedG = Physics.Raycast(transform.position + Vector3.up * 0.5f, -transform.right,
                        out RaycastHit glh, accidentBypassOffset + 1.5f, ~0, QueryTriggerInteraction.Ignore)
                    && glh.collider.transform.root != transform.root;

                // 🚧 窄路借道:往左前方長距離掃描,有對向來車就先不切(等它過去)
                bool oncomingG = false;
                Vector3 obliqueG = (transform.forward - transform.right * 0.35f).normalized;
                foreach (var oh in Physics.SphereCastAll(transform.position + Vector3.up * 0.5f, 1.0f,
                             obliqueG, oncomingCheckDistance, carLayer, QueryTriggerInteraction.Ignore))
                {
                    if (oh.collider.transform.root == transform.root) continue;
                    Vector3 ofwd = oh.collider.transform.root.forward;
                    var oNav = oh.collider.GetComponentInParent<CarNavigation>();
                    var oThreat = oh.collider.GetComponentInParent<ReverseThreatAICar>();
                    if (oNav != null) ofwd = oNav.transform.forward;
                    else if (oThreat != null) ofwd = oThreat.transform.forward;
                    if (Vector3.Dot(transform.forward, ofwd) < -0.5f) { oncomingG = true; break; } // 對向來車
                }

                // 🔧 繞行開關:AccidentDirector 沒開放繞行 → 停在相位 0,不啟動橫向偏移。
                //    只擋「相位 0 → 1」這個轉換,上面的偵測、lastAccidentSeenTime(合理停等,
                //    避免被卡死看門狗誤刪)、以及跟車減速全部照常運作 —— 車會正常停在
                //    故障車後面,不會撞上去也不會憑空消失,玩家從對向道繞過去即可。
                if (seeObs && !leftBlockedG && !oncomingG && AccidentDirector.AllowAIBypassNow)
                    bypassPhase = 1;
                accidentBypassRamp = Mathf.SmoothDamp(accidentBypassRamp, 0f, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                break;
            case 1:
                accidentBypassRamp = Mathf.SmoothDamp(accidentBypassRamp, accidentBypassOffset, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                if (Mathf.Abs(accidentBypassRamp - accidentBypassOffset) < 0.25f) bypassPhase = 2;
                break;
            case 2:
                accidentBypassRamp = Mathf.SmoothDamp(accidentBypassRamp, accidentBypassOffset, ref bypassOffsetVel, accidentBypassSmoothTime * 0.5f, accidentBypassRampSpeed);
                if (Vector3.Dot(transform.forward, bypassObstaclePos - transform.position) < -accidentPassClearance)
                    bypassPhase = 3;
                break;
            case 3:
                bool gRightBlocked = Physics.Raycast(transform.position + Vector3.up * 0.5f, transform.right,
                        out RaycastHit grh, 3f, ~0, QueryTriggerInteraction.Ignore)
                    && grh.collider.transform.root != transform.root;
                if (!gRightBlocked)
                    accidentBypassRamp = Mathf.SmoothDamp(accidentBypassRamp, 0f, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                if (Mathf.Abs(accidentBypassRamp) < 0.15f) bypassPhase = 0;
                break;
        }
    }

    void HandleStuckDespawn()
    {
        if (isFadingOut) return;

        bool legitWait = isWaitingAtTrafficLight || isCarDetectedAtStop
            || waitingForOppositeStraightCar || isYieldingForAmbulance
            || isForcedReversing || isStaticObstacle
            || isMergeHolding                              // 🤝 等主車道縫隙 = 合理停等
            || (Time.time - lastAccidentSeenTime < 2f);   // 🚧 被故障車擋住 = 合理塞車,不判定卡死

        // 🔒 保底逾時：任一停等旗標連續成立超過 legitWaitTimeout 就不再算「合理停等」。
        //    號誌一輪最長約 40 秒，連續 60 秒還在等一定是某個旗標卡住沒解除
        //    (最常見的是停等區的殘留，車就會在綠燈時仍然釘在原地)。
        //    ⚠️ 救護車避讓與靜態障礙車本來就會長時間成立，排除在外，不受這道保險影響。
        bool timeoutExempt = isYieldingForAmbulance || isStaticObstacle;
        if (legitWait && !timeoutExempt && legitWaitTimeout > 0f)
        {
            legitWaitTimer += Time.deltaTime;
            if (legitWaitTimer > legitWaitTimeout)
            {
                legitWait = false;

                // 讓車自己開走，比在玩家眼前淡出消失自然得多
                if (isWaitingAtTrafficLight)
                {
                    activeRedTriggers.Clear();
                    SetTrafficLightStatus(false);
                    UnityEngine.Debug.LogWarning($"⏰ [停等逾時] {name} 停等超過 {legitWaitTimeout:F0} 秒，強制解除紅燈停等", this);
                }
                legitWaitTimer = 0f;
            }
        }
        else
        {
            legitWaitTimer = 0f;
        }

        bool hasMoved = (transform.position - lastStuckCheckPos).sqrMagnitude > 0.25f;

        if (legitWait || hasMoved)
        {
            stuckDespawnTimer = 0f;
            lastStuckCheckPos = transform.position;
            return;
        }

        if (playerTransformForStuck == null) return;

        float distToPlayer = Vector3.Distance(transform.position, playerTransformForStuck.position);
        if (distToPlayer < playerSafeRadius || distToPlayer > stuckDetectRadius)
        {
            stuckDespawnTimer = 0f;
            return;
        }
        // 🔊 玩家擋在正前方 → 合理等待,不消失
        Vector3 toPlayer = playerTransformForStuck.position - transform.position; toPlayer.y = 0f;
        if (toPlayer.magnitude < playerBlockDistance && Vector3.Dot(transform.forward, toPlayer.normalized) > 0.5f)
        {
            stuckDespawnTimer = 0f;
            return;
        }
        stuckDespawnTimer += Time.deltaTime;
        if (stuckDespawnTimer < bandStuckTime) return;

        UnityEngine.Debug.Log($"<color=magenta>💀 [灰車卡死淡出] {name} 距玩家 {distToPlayer:F0}m</color>");
        isFadingOut = true;
        StartCoroutine(FadeOutAndDestroy(fadeOutDuration));
    }

    System.Collections.IEnumerator FadeOutAndDestroy(float duration)
    {
        // 🔧 先讓這台車退出物理運算。
        //    縮放一旦趨近 0,PhysX 算慣性張量時會除以縮放 → 除以零 → Rigidbody 的
        //    position / centerOfMass 全變成 NaN,Console 會狂噴
        //    "Skipped updating the transform of this Rigidbody because its components are infinite"。
        Rigidbody rbFade = GetComponent<Rigidbody>();
        if (rbFade != null)
        {
            rbFade.velocity = Vector3.zero;
            rbFade.angularVelocity = Vector3.zero;
            rbFade.isKinematic = true;
        }
        foreach (Collider col in GetComponentsInChildren<Collider>(true))
            col.enabled = false;

        Vector3 startScale = transform.localScale;
        // 🔧 目標縮到原本的 1%,不要縮到完全 0(0 會讓縮放矩陣不可逆)
        Vector3 endScale = startScale * 0.01f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            transform.localScale = Vector3.Lerp(startScale, endScale, t / duration);
            yield return null;
        }
        Destroy(gameObject);
    }

    public void ForceFadeOutClear() => ForceFadeOutClear(fadeOutDuration);

    /// <summary>
    /// 指定淡出秒數的版本。給事故走廊清車用 —— 那邊需要依距離調整：
    /// 遠處的車快速收掉(反正看不清楚),近處的慢慢縮小才不刺眼。
    /// </summary>
    public void ForceFadeOutClear(float duration)
    {
        if (isFadingOut) return;
        isFadingOut = true;
        StartCoroutine(FadeOutAndDestroy(Mathf.Max(0.05f, duration)));
    }

    // 🚶 禮讓背景行人的動力上限(0~1;0=停,0.1≈蠕行)。-1 = 不禮讓。
    //    由 PedestrianYieldSensor 設定,套用點在 targetPower 最終化之前(F 段之前)。
    [HideInInspector] public float pedestrianYieldPower = -1f;

    /// <summary>禮讓背景行人:傳入「原速的幾成」(0=停,0~1=蠕行),負數=解除。</summary>
    public void SetPedestrianYield(float factor01)
        => pedestrianYieldPower = factor01 < 0f ? -1f : Mathf.Clamp01(factor01);

    public void SetYieldForAmbulance(bool yield, Transform ambulanceTF = null)
    {
        // 🚑 忽略外部的「解除」訊號(救護車離開觸發區會發),否則會提早回歸;
        //    回歸一律由 Update 的距離判定(ambulancePassClearance)決定
        if (!yield) return;

        // 🔧 逾時強制解除後的冷卻期:救護車的警笛廣播是每幀重發的,
        //    沒有這道鎖的話,剛解除下一幀就又被設回 true,逾時保險等於沒作用。
        if (Time.time < yieldCooldownUntil) return;

        // 🚑 我在救護車「左邊」(內側車道)→ 往右讓只會擋住它,維持原車道正常開
        if (ambulanceTF != null && ambulanceLeftLaneExemptDistance > 0f)
        {
            Vector3 toMeL = transform.position - ambulanceTF.position; toMeL.y = 0f;
            float lateralL = Vector3.Dot(toMeL, ambulanceTF.right);   // 負 = 在救護車左邊
            if (lateralL < -ambulanceLeftLaneExemptDistance)
            {
                if (debugLogDetection && Time.time - lastLeftExemptLogTime > 2f)
                {
                    lastLeftExemptLogTime = Time.time;
                    Debug.Log($"<color=grey>🚑 {name} 在救護車左側 {-lateralL:F1}m(內側車道) → 豁免避讓,維持原車道 —— 往右讓反而會擋住它</color>", this);
                }
                return;
            }
        }

        // 🚑 救護車已在我「前方」= 我是跟在它後面的車 → 不避讓,正常跟車就好
        if (ambulanceTF != null)
        {
            Vector3 toAmb = ambulanceTF.position - transform.position; toAmb.y = 0f;
            if (toAmb.sqrMagnitude > 1f && Vector3.Dot(transform.forward, toAmb.normalized) > 0.3f)
                return;
        }
        isYieldingForAmbulance = true;
        if (ambulanceTF != null) activeAmbulanceTransform = ambulanceTF;
    }

    public void ForceDetour()
    {
        if (futureNodes[0] == null || futureNodes[0].nextChoices == null) return;

        List<WaypointNode> valid = new List<WaypointNode>();
        foreach (var n in futureNodes[0].nextChoices)
        {
            if (n == null || AccidentDirector.BlockedNodes.Contains(n)) continue;
            if (!isScooter && n.isScooterLane) continue;   // 汽車不繞進機車專用道
            valid.Add(n);
        }

        if (valid.Count > 0)
        {
            futureNodes[1] = valid[Random.Range(0, valid.Count)];
            // 往後補齊,清掉舊的通往封路的點
            futureNodes[2] = PickRandomChoice(futureNodes[1]);
            futureNodes[3] = PickRandomChoice(futureNodes[2]);
        }
    }

    private void CheckCloseReversingCar()
    {
        if (isForcedReversing) return;

        Collider[] hits = Physics.OverlapBox(
            transform.TransformPoint(new Vector3(0f, 0.5f, 2.0f)), // 覆蓋車頭前 0~4m
            new Vector3(1.0f, 0.8f, 2.2f),
            transform.rotation, carLayer);

        foreach (var h in hits)
        {
            // ⚠️ 跟隨玩家倒車的車不算「前車在倒車」。
            //    PlayerReverseBroadcaster 會把跟隨中的車設 isForcedReversing = true
            //    (必須設,白車有十幾處靠它暫停行進),但那台車實際上是「逐幀跟著玩家的位移」——
            //    玩家沒動它就一格都不動。
            //    少了這道判斷,前面的車會把它誤判成正在倒車,接手用 minChainReverseSpeed 的
            //    固定速度自己往後滑 —— 於是玩家一切 R 檔、車都還沒動,後面就有車在退了。
            //    這裡只是排除誤判來源,連鎖倒車本身的邏輯一個字都沒改。
            ReverseThreatAICar threatCar = h.GetComponentInParent<ReverseThreatAICar>();
            if (threatCar != null && (threatCar.isReverseAction || threatCar.isForcedReversing)
                && !threatCar.isFollowingPlayerReverse)
            {
                StartCoroutine(ExecuteChainReverseFromCar(threatCar.transform));
                return;
            }

            CarNavigation navCar = h.GetComponentInParent<CarNavigation>();
            if (navCar != null && navCar != this && navCar.IsForcedReversing
                && !navCar.isFollowingPlayerReverse)
            {
                StartCoroutine(ExecuteChainReverseFromCar(navCar.transform));
                return;
            }
        }
    }

    // 🔧 診斷用:節流計時器,避免每幀洗版
    private float lastReverseDiagTime = -999f;
    private float lastYieldDiagTime = -999f;
    private float lastLeftExemptLogTime = -999f;   // 內側車道豁免的診斷節流

    // ==================== 🚑 救護車避讓(移植自白車 ReverseThreatAICar) ====================
    //
    // 灰車原本的避讓是「邊開邊用 currentYieldOffset 把導航目標往旁邊挪」,
    // 跟轉彎、路口、跟車等邏輯糾纏在一起,狀態一多就互相打架。
    // 這裡改成跟白車完全相同的一套:鎖定路段基準 → 斜切靠邊 → 轉正停住 →
    // 二次偏移 → 救護車通過後回歸。整段自成一個方法,接管移動後直接 return,
    // 不受其他導航邏輯干擾。

    [Header("🚑 避讓靠邊 (與白車同一套)")]
    [Tooltip("一次偏移的靠邊距離(公尺)")]
    public float wcEvadeOffset = 3.5f;
    [Tooltip("二次偏移的最大靠邊距離(公尺),救護車卡在後方時才會用到")]
    public float wcEvadeOffsetMax = 5f;
    [Tooltip("救護車速度低於此值才算「真的停住」(m/s),避免它只是路口減速就誤判")]
    public float wcAmbStuckSpeed = 1.0f;
    [Tooltip("救護車卡住持續這麼久才啟動二次偏移(秒)")]
    public float wcAmbStuckTime = 1.5f;
    [Tooltip("斜切靠邊時的前瞻距離(公尺),越大越平緩")]
    public float wcAimLookAhead = 8f;
    [Tooltip("斜切靠邊時的速度(m/s)")]
    public float wcSlideSpeed = 4f;
    [Tooltip("斜切時的轉向速度")]
    public float wcSlideRotSpeed = 3f;

    [Header("🔧 抗 S 型擺盪 (只做濾波,不改避讓/回歸的判斷)")]
    [Tooltip("靠邊目標值的平滑速度(公尺/秒)。設 0 = 關閉平滑,完全回到舊行為")]
    public float wcTargetSmoothSpeed = 1.2f;
    [Tooltip("前方阻擋要連續成立這麼久才轉正停等(秒)。要短,否則會來不及停而撞上前車")]
    public float wcFrontBlockEngageTime = 0.12f;
    [Tooltip("前方要連續淨空這麼久才恢復斜切(秒)。太小會左右擺盪,太大會反應遲鈍")]
    public float wcFrontBlockReleaseTime = 0.45f;

    [Header("🛞 彎道修正")]
    // 「已經讓出去多少」(lateral)是相對於 wcLateralOriginLocked 量的。
    // 原本每次節點推進、只要路段方向差在 30 度內就會重新鎖定原點 ——
    // 直路上沒差,但彎道上路會往車的方向彎過去,重算後的 lateral 就變小,
    // 車以為自己還沒讓到位,而目標也跟著位移 → 實際偏移量一直很小。
    // 開始側移之後就把基準凍住,量到的才是真正的累積偏移量。
    [Tooltip("開始側移後凍結量測基準,避免彎道上偏移量被重算而縮水。取消勾選 = 舊行為")]
    public bool wcFreezeBaselineWhileSliding = true;

    [Range(-1f, 1f)]
    [Tooltip("救護車在後方遠處時,「它有沒有朝我開來」的判定門檻。彎道上救護車車頭與" +
             "「指向我」的夾角本來就大,門檻太高會誤判成它不朝我來而提前解除避讓," +
             "車切回主車道後又被廣播設回來,形成來回切。-0.3 約等於「除非它明顯開往反方向」")]
    public float ambulanceComingTowardDot = -0.3f;

    private Vector3 wcRoadDirLocked = Vector3.zero;
    private Vector3 wcLateralOriginLocked = Vector3.zero;
    private float wcLastLateral = -999f;
    private float wcLastTargetSeen = -999f;
    private float wcStallTimer = 0f;
    private float wcAmbBlockedTimer = 0f;
    private float wcSmoothedTarget = -999f;   // 🔧 平滑後的靠邊目標(-999 = 尚未初始化)
    private bool wcFrontBlocked = false;      // 🔧 遲滯後的「前方被擋」狀態
    private float wcFrontStateTimer = 0f;     // 🔧 目前與 wcFrontBlocked 相反的狀態持續了多久
    private bool wcBaselineFrozen = false;    // 🛞 已經開始側移 → 不再重新鎖定量測基準

    // 🔍 純診斷用:HandleWhiteCarStyleYield 每幀把內部狀態存在這裡,
    //    給 debugLogDetection 的 [灰車避讓] 那段一起印出來。不影響任何行為。
    private string diagYieldPhase = "未接管";
    private string diagReleaseReason = "(這台車還沒解除過)";   // 🔍 上一次是「哪一條路徑」解除避讓的
    private float diagReleaseTime = -999f;
    private float diagLateral, diagRawTarget, diagSmoothTarget;
    // 🔍 二次偏移的三個條件各自的即時值,用來分辨是哪一關沒過
    private bool diagAmbBehind; private float diagAmbDist, diagAmbSpd, diagBlockedTimer;
    private bool diagAmbBlocked;   // 救護車自己回報的「被擋住」

    /// <summary>🚑 重置靠邊狀態(每一輪避讓開始/結束時呼叫)</summary>
    private void ResetWhiteCarStyleYield()
    {
        wcRoadDirLocked = Vector3.zero;
        wcLateralOriginLocked = Vector3.zero;
        wcLastLateral = -999f;
        wcLastTargetSeen = -999f;
        wcStallTimer = 0f;
        wcAmbBlockedTimer = 0f;
        wcSmoothedTarget = -999f;
        wcFrontBlocked = false;
        wcFrontStateTimer = 0f;
        wcBaselineFrozen = false;
    }

    /// <summary>
    /// 🚑 白車式靠邊避讓。回傳 true 代表「這一幀的移動我接管了」,呼叫端要直接 return。
    /// </summary>
    private bool HandleWhiteCarStyleYield()
    {
        if (!isYieldingForAmbulance || lastWaypointNode == null || futureNodes[0] == null)
        {
            // 🔍 沒接管的原因要分得出來:避讓根本沒開,還是節點是 null。
            //    後者會讓車掉回一般導航(往車道中心開),下一幀又被接管往右切 —— 那就是大幅度左右搖擺。
            diagYieldPhase = !isYieldingForAmbulance ? "未接管(沒在避讓)"
                           : (lastWaypointNode == null ? "★未接管(lastWaypointNode=null)"
                                                       : "★未接管(futureNodes[0]=null)");
            if (!isYieldingForAmbulance) ResetWhiteCarStyleYield();
            return false;
        }

        // ---- 鎖定路段基準:整段靠邊用同一組方向與量測原點,不受節點推進/路口轉向影響 ----
        if (wcRoadDirLocked.sqrMagnitude < 0.01f)
        {
            Vector3 d0 = futureNodes[0].transform.position - lastWaypointNode.transform.position;
            d0.y = 0f;
            if (d0.sqrMagnitude < 0.01f) d0 = transform.forward;
            wcRoadDirLocked = d0.normalized;
            wcLateralOriginLocked = lastWaypointNode.transform.position;
        }
        else
        {
            // 緩彎追蹤:新路段方向與鎖定值差 30 度以內才更新,擋掉路口的直角轉向。
            // 🛞 但已經開始側移就不能再動基準 —— 換了原點等於把「讓出去多少」重算,
            //    彎道上那個值會縮水,車就永遠讓不到位。
            Vector3 dNew = wcFreezeBaselineWhileSliding && wcBaselineFrozen
                ? Vector3.zero
                : futureNodes[0].transform.position - lastWaypointNode.transform.position;
            dNew.y = 0f;
            if (dNew.sqrMagnitude > 0.01f && Vector3.Angle(wcRoadDirLocked, dNew.normalized) < 30f)
            {
                wcRoadDirLocked = dNew.normalized;
                wcLateralOriginLocked = lastWaypointNode.transform.position;
            }
        }

        Vector3 roadDir = wcRoadDirLocked;
        if (roadDir.sqrMagnitude < 0.01f) return false;
        Vector3 roadRight = Vector3.Cross(Vector3.up, roadDir);

        Vector3 toCar = transform.position - wcLateralOriginLocked; toCar.y = 0f;
        float lateral = Vector3.Dot(toCar, roadRight);

        // ---- 靠邊目標:一次偏移;救護車真的卡在後方才升級成二次偏移 ----
        float target = wcEvadeOffset;
        if (activeAmbulanceTransform != null)
        {
            Vector3 toAmb = activeAmbulanceTransform.position - transform.position; toAmb.y = 0f;
            bool behindClose = Vector3.Dot(roadDir, toAmb.normalized) < 0.3f && toAmb.magnitude < 18f;
            var ambAI = activeAmbulanceTransform.GetComponentInParent<ReverseThreatAICar>();
            float ambSpd = ambAI != null ? ambAI.CurrentHealthSpeed : 0f;

            // 🚑 優先用救護車自己回報的「被擋住」訊號;速度判斷留著當備援
            //    (例如救護車是被玩家擋住而完全停下,那時 IsBlockedByTraffic 是 false 但速度是 0)。
            AmbulanceAI ambulanceAI = ambAI as AmbulanceAI;
            bool ambStuck = (ambulanceAI != null && ambulanceAI.IsBlockedByTraffic)
                            || ambSpd < wcAmbStuckSpeed;
            if (behindClose && ambStuck) wcAmbBlockedTimer += Time.deltaTime;
            else wcAmbBlockedTimer = 0f;

            // 🔍 診斷:三個條件的即時值
            diagAmbBehind = Vector3.Dot(roadDir, toAmb.normalized) < 0.3f;
            diagAmbDist = toAmb.magnitude;
            diagAmbSpd = ambSpd;
            diagAmbBlocked = ambulanceAI != null && ambulanceAI.IsBlockedByTraffic;
            diagBlockedTimer = wcAmbBlockedTimer;

            if (wcAmbBlockedTimer > wcAmbStuckTime) target = wcEvadeOffsetMax;
        }

        // ---- 右側 / 右前方有東西 → 靠邊目標縮到剛好不撞 ----
        if (Physics.Raycast(transform.position + Vector3.up * 0.5f, roadRight, out RaycastHit rHit,
                target + 1f, carLayer, QueryTriggerInteraction.Ignore)
            && rHit.collider.transform.root != transform.root)
            target = Mathf.Max(0f, rHit.distance - 1.2f);

        Vector3 diagDir = (roadDir + roadRight).normalized;
        bool playerAtSide = false;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.7f, diagDir, out RaycastHit dHit,
                target + 3f, carLayer, QueryTriggerInteraction.Ignore)
            && dHit.collider.transform.root != transform.root)
        {
            target = Mathf.Min(target, Mathf.Max(0f, dHit.distance * 0.6f - 0.8f));
            if (dHit.collider.CompareTag("Player") || dHit.collider.transform.root.CompareTag("Player"))
                playerAtSide = true;
        }

        // ---- 擋住我讓道的是玩家 → 久了就淡出退場,免得永遠讓不了道 ----
        if (playerAtSide)
        {
            playerBlockYieldTimer += Time.deltaTime;
            if (playerBlockYieldTimer > playerBlockDespawnTime) { ForceFadeOutClear(); return true; }
        }
        else playerBlockYieldTimer = 0f;

        // ---- 🔧 抗 S 型 (1/2):把「靠邊目標」平滑掉 ----
        // target 是每幀由 Raycast 重算的:旁邊車道有車經過時它會從 3.5 塌到 1.0,車走了又彈回去。
        // 下面 `lateral < target - 0.2f` 的判斷跟著翻,車子就在「斜切」與「轉正停住」之間來回切換,
        // 走出來的軌跡就是 S 型。這裡只平滑「數值」,不動任何避讓/回歸的判斷條件。
        // 縮小(旁邊突然出現車)要快,才不會撞上去;放大(空間變大了)才慢慢來。
        float rawTarget = target;   // 未平滑的原始值 —— 下面二次偏移的解鎖判定必須用它
        diagLateral = lateral; diagRawTarget = rawTarget;   // 🔍 診斷
        if (wcTargetSmoothSpeed > 0f)
        {
            if (wcSmoothedTarget < -900f) wcSmoothedTarget = target;
            float targetRate = (target < wcSmoothedTarget) ? wcTargetSmoothSpeed * 5f : wcTargetSmoothSpeed;
            wcSmoothedTarget = Mathf.MoveTowards(wcSmoothedTarget, target, targetRate * Time.deltaTime);
            target = wcSmoothedTarget;
        }
        diagSmoothTarget = target;   // 🔍 診斷

        // ---- 側移卡住判定:用「橫向速度」而非每幀位移,才不受幀率影響 ----
        float lateralSpeed = (wcLastLateral > -900f && Time.deltaTime > 0.0001f)
            ? Mathf.Abs(lateral - wcLastLateral) / Time.deltaTime : 999f;
        if (lateralSpeed < 0.15f) wcStallTimer += Time.deltaTime; else wcStallTimer = 0f;
        wcLastLateral = lateral;

        // 二次偏移把目標加大 → 解除「已到位」鎖定,讓車能再往外靠
        // ⚠️ 這裡一定要用 rawTarget:平滑後的 target 每幀只爬 0.02 左右,永遠跨不過 0.1 的門檻,
        //    二次偏移就會失效。rawTarget 在升級的那一幀是 3.5 → 5 的整段跳躍,判定跟舊版完全一樣。
        if (wcLastTargetSeen > -900f && rawTarget > wcLastTargetSeen + 0.1f) wcStallTimer = 0f;
        wcLastTargetSeen = rawTarget;

        bool stalled = wcStallTimer > 1.0f;

        if (lateral < target - 0.2f && !stalled)
        {
            // ---- 斜切靠邊中 ----
            Vector3 aimPoint = transform.position + roadDir * wcAimLookAhead + roadRight * (target - lateral);
            Vector3 aimDir = (aimPoint - transform.position).normalized;

            // 前方有車(救護車還沒過去等)→ 原地轉正等,不往前擠
            // ---- 🔧 抗 S 型 (2/2):這個判斷加一道不對稱遲滯 ----
            // aimDir 是「斜右前方」,多車道上會不停掃到隔壁車道的車。判斷一翻,車頭就在
            // 斜右(aimDir)與轉正(roadDir)之間來回擺 —— 從偏右 20 度轉回正前方,肉眼看就是
            // 「往左轉」,兩邊交替就是使用者看到的 S 型。所以:偵測到阻擋要連續成立一小段
            // 才停(要短,否則來不及停會撞上前車);淨空則要連續成立比較久才恢復斜切(要長,
            // 用來吃掉抖動)。停等/斜切本身的行為一個字都沒改,只是延後了切換的時機。
            bool frontHitNow = Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.8f, aimDir,
                    out RaycastHit fHit, 5.5f, carLayer, QueryTriggerInteraction.Ignore)
                && fHit.collider.transform.root != transform.root;

            if (frontHitNow == wcFrontBlocked) wcFrontStateTimer = 0f;   // 與目前狀態一致 → 沒有待確認的變更
            else
            {
                wcFrontStateTimer += Time.deltaTime;
                float needHold = frontHitNow ? wcFrontBlockEngageTime : wcFrontBlockReleaseTime;
                if (wcFrontStateTimer >= needHold) { wcFrontBlocked = frontHitNow; wcFrontStateTimer = 0f; }
            }

            if (wcFrontBlocked)
            {
                diagYieldPhase = "停等(前方被擋)";   // 🔍 診斷
                wcStallTimer = 0f;   // 等前車是合理停等,不算側移卡住
                transform.rotation = Quaternion.Slerp(transform.rotation,
                    Quaternion.LookRotation(roadDir, Vector3.up), Time.deltaTime * alignSmoothSpeed);
                smoothDrive = Mathf.Lerp(smoothDrive, 0f, Time.deltaTime * 5f);
                realTimeSpeed = 0f;
                isPulledOverForAmbulance = false;
                return true;
            }

            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.LookRotation(aimDir, Vector3.up), Time.deltaTime * wcSlideRotSpeed);
            diagYieldPhase = "斜切靠邊中";   // 🔍 診斷
            wcBaselineFrozen = true;         // 🛞 從這一刻起量測基準固定,彎道上才不會縮水
            transform.position += transform.forward * wcSlideSpeed * Time.deltaTime;
            realTimeSpeed = wcSlideSpeed;
            smoothDrive = 0.4f;
            isPulledOverForAmbulance = false;
            return true;
        }

        // ---- 靠邊到位:轉正、停穩 ----
        diagYieldPhase = stalled ? "已到位(側移卡住強制轉正)" : "已到位(轉正停穩)";   // 🔍 診斷
        Quaternion alignRot = Quaternion.LookRotation(roadDir, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, alignRot, Time.deltaTime * alignSmoothSpeed);
        if (Quaternion.Angle(transform.rotation, alignRot) < 1.5f) transform.rotation = alignRot;
        smoothDrive = Mathf.Lerp(smoothDrive, 0f, Time.deltaTime * 5f);
        realTimeSpeed = 0f;
        isPulledOverForAmbulance = true;
        return true;
    }

    // ==================== 🚦 路口淨空:橫向車讓救護車先過 ====================

    [Header("🛡 避讓/回歸中的車:安全豁免下限")]
    [Tooltip("距離近於此值就絕不把對方當成「在隔壁的讓道車」而忽略,避免撞上去(公尺)")]
    public float besideIgnoreMinDistance = 6f;

    [Header("🚦 路口淨空 (橫向車讓救護車先過)")]
    [Tooltip("收到救護車的路口淨空訊號後,最多停等幾秒就強制放行(防止救護車在別處卡住導致路口永久凍結)")]
    public float junctionHoldTimeout = 9f;

    private float junctionHoldUntil = -999f;    // 訊號有效期(救護車每 0.5 秒重發一次)
    private float junctionHoldStart = -1f;      // 這一輪停等從何時開始,供逾時判定
    private Transform junctionHoldAmbulance = null;

    /// <summary>目前是否正因為救護車要通過路口而停等(供除錯/其他系統查詢)</summary>
    public bool IsHoldingForJunction { get; private set; }

    /// <summary>
    /// 🚦 由 AmbulanceAI 呼叫:「我要通過這個路口,橫向的你先停一下」。
    ///
    /// 只針對救護車當下所在/即將進入的那一個路口,不是全圖同方向的車都停。
    /// 已經開進路口的車不會被停住 —— 那樣只會把路口堵死,它要做的是趕快開出去。
    /// </summary>
    public void HoldForAmbulanceAtJunction(Transform ambulance)
    {
        // 自己正在為救護車避讓(同向車)→ 走避讓那套,不套用路口停等
        if (isYieldingForAmbulance) return;

        junctionHoldUntil = Time.time + 0.8f;   // 略長於廣播間隔,訊號斷了會自動失效
        junctionHoldAmbulance = ambulance;
        if (junctionHoldStart < 0f) junctionHoldStart = Time.time;
    }

    /// <summary>🚦 每幀評估路口停等狀態,回傳「是否應該停在路口前」</summary>
    private bool EvaluateJunctionHold()
    {
        bool signalAlive = Time.time < junctionHoldUntil && junctionHoldAmbulance != null;

        if (!signalAlive)
        {
            junctionHoldStart = -1f;
            junctionHoldAmbulance = null;
            IsHoldingForJunction = false;
            return false;
        }

        // 逾時保險:救護車若在別處卡住,不能讓這個路口永久凍結
        if (junctionHoldStart > 0f && Time.time - junctionHoldStart > junctionHoldTimeout)
        {
            if (debugLogDetection)
                Debug.Log($"<color=orange>🚦 {name} 路口停等逾時 {junctionHoldTimeout} 秒,強制放行</color>");
            IsHoldingForJunction = false;
            return false;
        }

        // 已經開進路口了 → 不能停在路口中央,要趕快開出去
        if (isInIntersectionZone)
        {
            IsHoldingForJunction = false;
            return false;
        }

        IsHoldingForJunction = true;
        return true;
    }

    /// <summary>🔧 倒車偵測診斷輸出。勾選 Inspector 的 Debug Log Detection 才會印,每秒最多一次。</summary>
    private void DiagReverse(string msg)
    {
        lastReverseDiagTime = Time.time;
        Debug.Log($"<color=cyan>[倒車偵測]</color> {name}: {msg}");
    }

    // ==================== 🎺 喇叭系統(比照白車 ReverseThreatAICar) ====================

    [Header("🎺 喇叭功能 (玩家阻擋偵測)")]
    [Tooltip("喇叭音效。建議指定,會用 PlayOneShot 播放,不會蓋掉引擎聲")]
    public AudioClip hornClip;
    // 原本 PlayOneShot 沒帶音量參數,所以喇叭完全繼承引擎聲那顆 AudioSource 的 volume ——
    // 引擎聲本來就調得小,喇叭就跟著小,而且沒有任何地方調得動。
    // PlayOneShot 的 volumeScale 允許大於 1(最終音量 = 來源 volume × 這個值),所以這裡開到 3。
    [Tooltip("喇叭音量倍率。1 = 完全跟著引擎聲那顆 AudioSource 的音量(舊行為)。\n" +
             "可以調到 1 以上讓它更大聲 —— PlayOneShot 的倍率沒有 1 的上限。\n" +
             "★ 調到 3 還是覺得小的話,瓶頸就是引擎 AudioSource 本身的音量與 3D 衰減設定,\n" +
             "   那要改成像救護車那樣開一顆專屬的喇叭 AudioSource")]
    [Range(0f, 5f)] public float hornVolume = 2.5f;
    [Tooltip("往前偵測玩家的距離(公尺)")]
    public float hornDetectDistance = 10f;
    [Tooltip("玩家擋路幾秒後按喇叭(一般路段)")]
    public float hornWaitTime = 3.0f;
    [Tooltip("玩家擋路幾秒後按喇叭(路口,給比較長的寬容期)")]
    public float hornWaitTimeAtIntersection = 5.0f;
    [Tooltip("按完喇叭休息幾秒才能再按")]
    public float hornCooldown = 3.0f;
    [Tooltip("從玩家車頭往前掃多遠,確認玩家自己是不是也被車卡住(公尺)")]
    public float playerBlockedCheckDistance = 12f;
    [Tooltip("掃描起點從玩家中心往前偏移多少,避免掃到玩家自己的車身(公尺)")]
    public float playerBlockedCheckOffset = 3f;

    private float blockedTimer = 0f;
    private bool isHonking = false;

    private bool IsPlayerColliderNav(Collider c)
    {
        if (c == null) return false;
        if (c.CompareTag("Player")) return true;
        if (c.attachedRigidbody != null && c.attachedRigidbody.CompareTag("Player")) return true;
        return c.transform.root.CompareTag("Player");
    }

    private bool IsMyOwnColliderNav(Collider c)
    {
        return c != null && c.transform.IsChildOf(transform);
    }

    /// <summary>
    /// 🔧 判斷這個碰撞體是不是「在我旁邊讓道中」的車(與白車 ReverseThreatAICar 同一套規則)。
    ///
    /// 主車道正常行駛的車 vs 讓道完要回歸的車,兩台並排時會互相把對方當障礙物:
    ///   主車道車 → 偵測到旁邊的車 → 煞停
    ///   回歸車   → 看到主車道有車 → 繼續等
    ///   兩台都停住 → 死鎖
    ///
    /// 路權上主車道優先,所以不該為「還在避讓、且只是在隔壁」的車減速。
    /// 只排除橫向明顯偏開的,真的擋在正前方的照樣要煞(避免追撞)。
    /// </summary>
    private bool IsBesideYieldingVehicle(Collider h)
    {
        if (h == null) return false;

        ReverseThreatAICar otherWhite = h.GetComponentInParent<ReverseThreatAICar>();
        CarNavigation otherGray = otherWhite == null ? h.GetComponentInParent<CarNavigation>() : null;
        if (otherGray == this) return false;

        // 🔧 靜止不動的車(路邊等觸發的側切車 / 靜態障礙)永遠當實體障礙,
        //    它們雖然也會收到避讓廣播讓 IsYieldingForAmbulanceNow 變 true,但根本不會移動。
        if (otherWhite != null && otherWhite.isStaticParked) return false;
        if (otherGray != null && otherGray.isStaticObstacle) return false;

        // 🔧 「避讓中」和「回歸中」都算 —— 兩者都是暫時偏離車道、且會自己歸位的車。
        //    只認避讓中的話,救護車一離開對方就變成「一般障礙物」,
        //    主車道的車煞停 + 回歸車禮讓 → 兩台並排互等到逾時。
        bool otherYielding = (otherWhite != null && otherWhite.IsYieldingForAmbulanceNow)
                          || (otherGray != null && otherGray.IsYieldingForAmbulanceNow);
        bool otherReturning = (otherWhite != null && otherWhite.IsReturningFromYieldNow)
                           || (otherGray != null && otherGray.IsReturningFromYieldNow);
        if (!otherYielding && !otherReturning) return false;

        Transform otherTf = otherWhite != null ? otherWhite.transform
                          : (otherGray != null ? otherGray.transform : h.transform);

        // 🔧 安全鎖 1:我自己正在轉彎時完全不豁免。
        //    轉彎中車頭在轉,transform.InverseTransformPoint 的座標系跟著轉,
        //    正前方的車在旋轉後的 local 座標裡橫向分量會變大 → 被誤判成「在隔壁」→ 直接撞上去。
        if (isBezierActive) return false;

        Vector3 rel = transform.InverseTransformPoint(otherTf.position);

        // 🔧 安全鎖 2:距離太近一律不豁免。再怎麼判斷都不該在快撞上時忽略對方。
        if (rel.magnitude < besideIgnoreMinDistance) return false;

        // 車心在我的座標系裡橫向偏開超過 1.2m → 在隔壁,不是擋在我正前方
        if (Mathf.Abs(rel.x) <= 1.2f) return false;

        // 🔧 安全鎖 3:落在我正前方錐形範圍內的車不算「隔壁」。
        //    橫向偏移相對於前方距離太小(約 27 度錐內)= 它就在我的行進路徑上。
        if (rel.z > 0f && Mathf.Abs(rel.x) < rel.z * 0.5f) return false;

        return true;
    }

    /// <summary>
    /// 🔧 判斷「擋住我的那位玩家」自己前方是不是也被別的車卡住。
    /// 綠燈起步時玩家常常是被前車卡住而不是故意擋路,這種情況按喇叭是錯的。
    /// </summary>
    private bool IsPlayerBlockedAhead(Collider playerCol)
    {
        if (playerCol == null) return false;
        Transform p = playerCol.transform;

        Vector3 origin = p.position + Vector3.up * 0.5f + p.forward * playerBlockedCheckOffset;
        RaycastHit[] hits = Physics.SphereCastAll(origin, 0.6f, p.forward,
            playerBlockedCheckDistance, carLayer, QueryTriggerInteraction.Ignore);

        foreach (RaycastHit h in hits)
        {
            if (h.collider == null) continue;
            if (IsMyOwnColliderNav(h.collider)) continue;   // 掃到我自己不算
            if (IsPlayerColliderNav(h.collider)) continue;  // 掃到玩家自己的車身不算

            if (h.collider.GetComponentInParent<CarNavigation>() != null
             || h.collider.GetComponentInParent<ReverseThreatAICar>() != null)
                return true;                                // 玩家前面真的有車擋著
        }
        return false;
    }

    /// <summary>🎺 偵測玩家擋在正前方不動 → 按喇叭催。紅燈中、或玩家自己也被卡住時不催。</summary>
    private void HandlePlayerBlockDetection()
    {
        RaycastHit hit;
        if (!Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.6f, transform.forward,
                                out hit, hornDetectDistance, carLayer, QueryTriggerInteraction.Ignore))
        {
            blockedTimer = 0f;
            return;
        }

        if (!IsPlayerColliderNav(hit.collider))
        {
            blockedTimer = 0f;
            return;
        }

        Rigidbody playerRb = hit.collider.GetComponentInParent<Rigidbody>();
        float playerSpeed = playerRb != null ? playerRb.velocity.magnitude : 0f;
        if (playerSpeed >= 0.5f)          // 玩家有在動 → 不催
        {
            blockedTimer = 0f;
            return;
        }

        if (isWaitingAtTrafficLight)      // 我自己在等紅燈 → 不干玩家的事
        {
            blockedTimer = 0f;
            return;
        }

        // 🔧 綠燈了但玩家自己前方也被車卡住 → 他不是故意不走,不該對他按喇叭
        if (IsPlayerBlockedAhead(hit.collider))
        {
            blockedTimer = 0f;
            if (debugLogDetection)
                Debug.Log($"<color=orange>🎺</color> {name}:玩家前方也被車擋住,不催他");
            return;
        }

        blockedTimer += Time.deltaTime;
        float requiredTime = isInIntersectionZone ? hornWaitTimeAtIntersection : hornWaitTime;
        if (blockedTimer >= requiredTime && !isHonking)
            TriggerHorn();
    }

    private void TriggerHorn()
    {
        isHonking = true;
        blockedTimer = 0f;

        AudioSource src = GetComponent<AudioSource>();
        // 🔍 一律印出來,方便追查「到底是誰在按、按給誰聽」
        UnityEngine.Debug.Log($"<color=orange>🎺 按喇叭</color> {name}  hornClip={(hornClip != null ? hornClip.name : "無(會改播 AudioSource 既有音效!)")}  離玩家={(playerTransformForStuck != null ? Vector3.Distance(transform.position, playerTransformForStuck.position).ToString("F1") : "?")}m", this);

        if (src != null && hornClip != null) src.PlayOneShot(hornClip, hornVolume);
        // ⚠️ 沒有指定 hornClip 時不要亂播 AudioSource 上的既有音效(那通常是引擎聲,會變成一直有怪聲)
        // else if (src != null) src.Play();

        if (debugLogDetection)
            Debug.Log($"<color=orange>🎺 嗶嗶！</color>{name} 對玩家按喇叭" +
                      $"{(src == null ? " (★這台車沒有 AudioSource,只有訊息沒有聲音)" : "")}");

        StartCoroutine(ResetHornRoutine());
    }

    private IEnumerator ResetHornRoutine()
    {
        yield return new WaitForSeconds(hornCooldown);
        isHonking = false;
    }

    private void CheckPlayerReverseImmediate()
    {
        // 🔙 舊的「每台車自己偵測玩家倒車」路徑,預設關閉。
        //    它的門檻只有「15m 內 + 玩家落在我車頭前方 ±72°」,錐形很寬,
        //    斜前方的車也會被判成要跟著倒;而且每幀都在跑,會搶先把
        //    isForcedReversing 設起來,讓廣播器的呼叫被擋在門外。
        if (!useLegacySelfDetection) return;

        // 🔧 只有勾了 debugLogDetection 才會做診斷輸出,正常執行路徑完全不變
        bool diag = debugLogDetection && Time.time - lastReverseDiagTime > 1f;

        if (isForcedReversing)
        {
            if (diag) DiagReverse("自己正在倒車中 → 跳過");
            return;
        }

        GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
        if (playerObj == null)
        {
            if (diag) DiagReverse("★ 找不到任何 Tag=Player 的物件");
            return;
        }

        float distToPlayer = Vector3.Distance(transform.position, playerObj.transform.position);
        if (distToPlayer > reverseThreatDetectionRange)
        {
            if (diag) DiagReverse($"Player={playerObj.name} | 距離 {distToPlayer:F1} > 上限 {reverseThreatDetectionRange} ★太遠");
            return;
        }

        // 玩家必須大致在車頭前方，避免對側邊或後方的玩家車誤判觸發倒車
        Vector3 dirToPlayer = (playerObj.transform.position - transform.position).normalized;
        float dotToPlayer = Vector3.Dot(transform.forward, dirToPlayer);
        if (dotToPlayer < 0.3f)
        {
            if (diag) DiagReverse($"Player={playerObj.name} | 距離 {distToPlayer:F1} 通過,但 Dot {dotToPlayer:F2} < 0.30 ★玩家不在我車頭前方");
            return;
        }

        // 🟢 優先讀介面（不受輸入裝置影響，未來手把/方向盤只要實作介面就能沿用）
        IPlayerVehicleState playerState = playerObj.GetComponent<IPlayerVehicleState>();
        bool isReversingNow;

        if (playerState != null)
        {
            isReversingNow = playerState.IsReversing;
        }
        else
        {
            // 沒有實作介面的情況下，退回 Rigidbody 速度判斷當保底
            Rigidbody playerRb = playerObj.GetComponent<Rigidbody>();
            isReversingNow = playerRb != null
                && playerRb.velocity.magnitude > 0.01f
                && Vector3.Dot(playerRb.velocity.normalized, playerObj.transform.forward) < 0f;
        }

        if (diag)
            DiagReverse($"Player={playerObj.name} | 距離 {distToPlayer:F1} ✓ | Dot {dotToPlayer:F2} ✓ | " +
                        $"介面={(playerState != null ? "有" : "★沒有(退回 Rigidbody 判斷)")} | IsReversing={isReversingNow}");

        if (isReversingNow)
        {
            if (debugLogDetection) Debug.Log($"<color=lime>[倒車偵測] {name} → 觸發連鎖後退!</color>");
            StartCoroutine(ExecuteChainReverseFromPlayer(playerObj.transform));
        }
    }

    /// <summary>
    /// 由玩家端的 PlayerReverseBroadcaster 直接呼叫，強制開始跟著玩家倒車。
    /// 繞過 CheckPlayerReverseImmediate 的距離與夾角門檻 —— 判定集中在玩家端一處，好調也好查。
    /// </summary>
    public void ForceChainReverseFromPlayer(Transform player)
    {
        if (isForcedReversing || isStaticObstacle || player == null) return;
        StartCoroutine(ExecuteChainReverseFromPlayer(player));
    }

    // ─────────────────────────────────────────────────────────────
    // 🔙 玩家倒車「位移跟隨」：玩家退多少,這台車就退多少
    //    與白車 ReverseThreatAICar 同名的三個方法,行為一致。
    //    位移完全由 PlayerReverseBroadcaster 逐幀餵進來,玩家不動就是 0,
    //    minChainReverseSpeed 的下限在這條路徑上不再參與。
    // ─────────────────────────────────────────────────────────────
    [HideInInspector] public bool isFollowingPlayerReverse = false;

    /// <summary>
    /// 廣播器納入跟隨名單前呼叫。回傳 false = 這台車現在不該被動。
    /// 資格判斷寫在車子這一側,廣播器不需要看得到這些 private 旗標。
    /// </summary>
    public bool BeginPlayerReverseFollow() => BeginPlayerReverseFollow(out _);

    /// <summary>附帶拒絕原因的版本,給廣播器印診斷用。</summary>
    public bool BeginPlayerReverseFollow(out string reason)
    {
        reason = null;
        if (isFollowingPlayerReverse) return true;

        // 🚑 避讓救護車中的車絕對不搶 —— 那套邏輯有自己的狀態機
        if (isYieldingForAmbulance) { reason = "正在避讓救護車";       return false; }
        if (isForcedReversing)      { reason = "已被其他連鎖倒車接管"; return false; }
        if (isStaticObstacle)       { reason = "是靜態路障車";         return false; }

        isFollowingPlayerReverse = true;
        isForcedReversing = true;      // 沿用既有旗標暫停這台車自己的行進邏輯
        smoothDrive = 0f;
        return true;
    }

    /// <summary>
    /// 由廣播器每幀呼叫,meters = 玩家這一幀沿自己車尾方向的實際位移量。
    /// 沿「自己的」forward 退,車子維持在自己的車道方向上。
    /// </summary>
    public void ApplyPlayerReverseDelta(float meters)
    {
        if (!isFollowingPlayerReverse) return;

        bool moving = meters > 0.0001f;
        if (moving) transform.position -= transform.forward * meters;

        realTimeSpeed = (moving && Time.deltaTime > 0f) ? -meters / Time.deltaTime : 0f;
        smoothDrive = 0f;              // 避免解除跟隨時瞬間暴衝
        SetLightsState(reverseLights, moving, reverseColor);
        SetLightsState(brakeLights, false, brakeColor);
    }

    /// <summary>玩家切回前進／離開範圍／被淘汰時呼叫,交還控制權給正常導航。</summary>
    public void EndPlayerReverseFollow()
    {
        if (!isFollowingPlayerReverse) return;

        isFollowingPlayerReverse = false;
        isForcedReversing = false;
        realTimeSpeed = 0f;
        smoothDrive = 0f;
        SetLightsState(reverseLights, false, reverseColor);
    }

    IEnumerator ExecuteChainReverseFromPlayer(Transform player)
    {
        isForcedReversing = true;
        activeThreatTransform = player;

        IPlayerVehicleState playerState = player.GetComponent<IPlayerVehicleState>();
        Rigidbody playerRb = player.GetComponent<Rigidbody>();

        while (player != null)
        {
            float distToPlayer = Vector3.Distance(transform.position, player.position);
            if (distToPlayer > reverseThreatDetectionRange) break;

            float playerSpeed = playerRb != null ? playerRb.velocity.magnitude : 0f;

            bool playerSwitchedToForward = playerState != null
                ? !playerState.IsReversing
                : (playerRb != null && playerSpeed > 0.3f && Vector3.Dot(playerRb.velocity.normalized, playerRb.transform.forward) > 0.2f);

            if (playerSwitchedToForward) break;

            float targetSpeed;
            if (distToPlayer < playerSafeDistance)
            {
                float pushBackSpeed = (1f - (distToPlayer / playerSafeDistance)) * 7f;
                targetSpeed = Mathf.Max(pushBackSpeed, 0.5f);
            }
            else
            {
                float followFactor = Mathf.Clamp01((distToPlayer - playerSafeDistance) / (reverseThreatDetectionRange - playerSafeDistance));
                targetSpeed = Mathf.Lerp(0f, playerSpeed * 0.9f, followFactor);
            }

            targetSpeed = Mathf.Clamp(targetSpeed, minChainReverseSpeed, 10f);
            transform.position -= transform.forward * targetSpeed * Time.deltaTime;

            realTimeSpeed = -targetSpeed;
            smoothDrive = 0f; // 避免解除倒車時瞬間暴衝
            SetLightsState(reverseLights, true, reverseColor);
            SetLightsState(brakeLights, false, brakeColor);

            yield return null;
        }

        isForcedReversing = false;
        activeThreatTransform = null;
        smoothDrive = 0f;
        SetLightsState(reverseLights, false, reverseColor);

        yield return new WaitForSeconds(0.3f);
    }

    IEnumerator ExecuteChainReverseFromCar(Transform frontCar)
    {
        isForcedReversing = true;
        activeThreatTransform = frontCar;

        while (frontCar != null && IsTransformStillReversing(frontCar))
        {
            float distToFront = Vector3.Distance(transform.position, frontCar.position);
            float speedOffset = (playerSafeDistance - distToFront) * 2.0f;
            float targetSpeed = Mathf.Clamp(chainReverseSpeed + speedOffset, minChainReverseSpeed, 10f);

            transform.position -= transform.forward * targetSpeed * Time.deltaTime;
            realTimeSpeed = -targetSpeed;
            smoothDrive = 0f;
            SetLightsState(reverseLights, true, reverseColor);
            SetLightsState(brakeLights, false, brakeColor);

            yield return null;
        }

        isForcedReversing = false;
        activeThreatTransform = null;
        smoothDrive = 0f;
        SetLightsState(reverseLights, false, reverseColor);
    }

    // ── 🎯 指定路線（AccidentDirector 對向補車專用）────────────────────
    // 補出來的車如果照常在路口隨機選邊，很可能在抵達事故點之前就轉走了 ——
    // 那台車就白補了，玩家繞行時對向照樣沒車。
    // 這裡讓補車端可以指定「開頭這幾步一定要走這條」，走完就自動清空，
    // 之後完全回到原本的隨機選路。沒設定 forcedRoute 的車一行邏輯都不受影響。
    [HideInInspector] public List<WaypointNode> forcedRoute;
    private int forcedRouteIndex = 0;

    /// <summary>指定開頭要走的路線。傳 null 或空清單＝取消指定。</summary>
    public void SetForcedRoute(List<WaypointNode> route)
    {
        forcedRoute = route;
        forcedRouteIndex = 0;
    }

    /// <summary>指定路線裡下一個「接得上 fromNode」的節點。沒有就回 null，交還給隨機選路。</summary>
    private WaypointNode TakeForcedChoice(WaypointNode fromNode)
    {
        if (forcedRoute == null || forcedRouteIndex >= forcedRoute.Count) return null;

        // 允許往前找幾格：節點推進與這裡的消耗速度不一定同步
        for (int i = forcedRouteIndex; i < forcedRoute.Count; i++)
        {
            WaypointNode want = forcedRoute[i];
            if (want == null) continue;
            if (!fromNode.nextChoices.Contains(want)) continue;

            forcedRouteIndex = i + 1;
            if (forcedRouteIndex >= forcedRoute.Count) forcedRoute = null;   // 走完就放手
            return want;
        }
        return null;
    }

    private WaypointNode PickRandomChoice(WaypointNode fromNode)
    {
        if (fromNode == null || fromNode.nextChoices == null || fromNode.nextChoices.Count == 0)
            return null;

        // 🎯 有指定路線就照走（只影響被指定的那幾台補車）
        WaypointNode forced = TakeForcedChoice(fromNode);
        if (forced != null) return forced;

        // 汽車不能開進機車專用道(isScooterLane)，機車兩種車道都能走。
        // 如果選項全是機車道就不過濾 —— 寧可讓汽車借道，也不要回傳 null 讓車卡死在原地。
        List<WaypointNode> pool = new List<WaypointNode>();
        foreach (var n in fromNode.nextChoices)
        {
            if (n == null) continue;
            if (!isScooter && n.isScooterLane) continue;
            pool.Add(n);
        }
        if (pool.Count == 0)
        {
            foreach (var n in fromNode.nextChoices)
                if (n != null) pool.Add(n);
        }
        if (pool.Count == 0) return null;

        if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedNodes != null && AccidentDirector.BlockedNodes.Count > 0)
        {
            List<WaypointNode> valid = new List<WaypointNode>();
            foreach (var n in pool)
                if (!AccidentDirector.BlockedNodes.Contains(n)) valid.Add(n);

            if (valid.Count > 0)
                return valid[Random.Range(0, valid.Count)];
            // 全部選項都被封(死路)才退回原本隨機,避免回傳 null 卡住
        }

        return pool[Random.Range(0, pool.Count)];
    }

    private bool IsTransformStillReversing(Transform t)
    {
        ReverseThreatAICar threatCar = t.GetComponentInParent<ReverseThreatAICar>();
        if (threatCar != null) return threatCar.isReverseAction || threatCar.isForcedReversing;

        CarNavigation nav = t.GetComponentInParent<CarNavigation>();
        if (nav != null) return nav.IsForcedReversing;

        return false;
    }

    // 🚦 判斷「橫向來車」是不是真的在動:停著的車不算來車,否則雙方互等會永遠僵持
    private bool IsActuallyMoving(Collider c)
    {
        var otherNav = c.GetComponentInParent<CarNavigation>();
        if (otherNav != null) return otherNav.RealTimeSpeed > 1.0f;

        var otherThreat = c.GetComponentInParent<ReverseThreatAICar>();
        if (otherThreat != null)
        {
            if (otherThreat.isStaticParked) return false;      // 路邊靜停的演員車不算來車
            return otherThreat.CurrentHealthSpeed > 1.0f;
        }
        var rb2 = c.GetComponentInParent<Rigidbody>();
        if (rb2 != null) return rb2.velocity.magnitude > 1.0f;  // 玩家車
        return true;
    }




}