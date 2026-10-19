
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;

public class ReverseThreatAICar : MonoBehaviour
{
    [Header("🌟 路邊靜態停靠設定")]
    public bool isStaticParked = false;

    [Header("🪨 靜停姿態鎖 (玩家撞不動)")]
    [Tooltip("靜停期間把 Rigidbody 轉成 Kinematic + FreezeAll,玩家(含 G923 全力撞)推不動;被觸發側切時自動解鎖還原")]
    public bool lockParkedPose = true;
    [Tooltip("靜停期間偏離停放原點超過這個公尺數就拉回(雙保險,防止其他腳本用 MovePosition 推走)")]
    public float parkedPoseTolerance = 0.02f;

    public bool debugLogDetection = false; // 🟢 診斷用：勾選才輸出偵測 Log

    [Header("1. 隨機路徑設定")]
    public TrafficPathNode currentWaypointNode;
    protected TrafficPathNode lastWaypointNode;
    public float rotationSpeed = 5f;
    public float arrivalDistance = 2.0f;
    private TrafficPathNode savedNormalWaypointNode = null;

    [Header("📡 扇形多射線雷達")]
    public int numberOfRays = 5;
    public float fanAngle = 50f;              // 直行時的扇形夾角
    public float turnFanAngle = 90f;          // 轉彎（貝茲）時的扇形夾角
    public float lateralDetectionMult = 0.5f; // 側邊射線長度衰減係數
    public float straightCenterMult = 1.2f;     // 直行時正中央射線的加長倍數

    [Header("🔵 貝茲平滑轉彎設定")]
    public float bezierTriggerDistance = 10f;      // 距離路口多近開始啟動貝茲曲線
    public float bezierTurnAngleThreshold = 15f;   // 夾角超過幾度才視為「真的在轉彎」
    public float mergeNodeDistanceThreshold = 25f; // 下一個點距離小於這個值，合併成跨節點大轉彎
    public float targetTurnSpeed = 9f;             // 一般轉彎目標時速
    public float targetFastTurnSpeed = 14f;        // 貝茲執行中 / 右轉預判 允許的較高時速
    public float exitBezierCooldownTime = 1.5f;    // 出彎後，暫時鎖住「彎道降速」判斷的冷卻時間
    [Range(0f, 1f)]
    public float curveThroughNodeWeight = 0.9f; // 1=曲線精準通過節點(但進彎會先外擺) 0=舊式抄內側(不外擺但切西瓜)

    // 🚑 改成 protected:救護車要用它判斷「現在是不是真的在轉彎」。純可見度調整,行為不變。
    protected bool isBezierActive = false;
    private float bezierT = 0f;
    private Vector3 bezierP0, bezierP1, bezierP2;
    private TrafficPathNode bezierMergeTargetNode = null; // 如果是跨節點合併，貝茲結束後要切到這個點
    private float exitBezierCooldown = 0f;

    [Header("2. 基礎移動與煞車")]
    public float moveSpeed = 10f;
    public float brakeHardness = 15f;
    public float startDelayMax = 0.8f;      // 起步反應延遲上限
    public float accelerationRate = 3.5f;   // 加速度 (每秒能提升多少速度)
    private float currentStartDelay = 0f;

    [Header("3. 倒車威脅設定 (危險因子)")]
    public float reverseWaitTime = 0.5f;
    public float reverseDuration = 1.5f;
    public float reverseSpeed = 2.0f;
    public float cooldownTime = 5.0f;

    [Header("⚠️ 3.5 連鎖避讓設定 (危險因子)")]
    public bool enableChainEvade = true;
    public float chainReverseSpeed = 2.0f;

    [Tooltip("玩家切 R 檔時，後方多少公尺內的車要跟著倒。\n不能沿用 detectionDistance（10m），那是一般跟車偵測距離 ——\n正常跟車距離就有 15~30m，用 10m 的話後車根本不在範圍內")]
    public float playerReverseChainDistance = 25f;
    [Tooltip("橫向容許距離。超過的話算隔壁車道，不需要跟著倒")]
    public float playerReverseChainLateral = 3f;
    [Tooltip("退讓速度至少是玩家倒車速度的幾倍。1.0 = 剛好跟上，1.15 = 略快一點好拉開距離")]
    public float chainKeepUpFactor = 1.15f;
    [Tooltip("舊的「每台車自己偵測玩家倒車」路徑。現在改由玩家車的 PlayerReverseBroadcaster 統一廣播，\n這條預設關閉；開著會在錯誤時機搶先設起 isForcedReversing，把廣播器的呼叫擋掉")]
    public bool useLegacySelfDetection = false;
    [Tooltip("🔧 連鎖後退的最低速度。原本寫死 0.1(每秒 10 公分,肉眼幾乎看不出來),建議 1.5~2.5")]
    public float minChainReverseSpeed = 1.8f;

    [Header("4. 跟車與偵測系統")]
    public float detectionDistance = 15f;
    public float safeDistance = 8f;
    public LayerMask detectionLayer;
    public float forwardDetectionRadius = 1.0f; // 🟢 提升為欄位，方便 Inspector 調整，也讓 Gizmo 讀取到同一個值

    [Header("🔭 提早預判減速 (獨立於安全距離偵測，不影響原本的球狀跟車判斷)")]
    public float earlyWarningDistance = 25f;      // 提早預判的偵測距離，通常要比 detectionDistance 更遠
    public float earlyWarningRadius = 1.2f;       // 這個獨立雷達的偵測半徑
    public float frontCarStableSpeedThreshold = 8f; // 前車速度高於這個值才算「開得穩」，低於就提早減速

    [Header("5. 紅綠燈狀態")]
    [SerializeField] protected bool isWaitingAtTrafficLight = false; // 🟢 private 改 protected
    public float trafficLightIdealStopDistance = 3.5f; // 離停止線多近算是「已停好」
    public float trafficLightCreepRange = 15f;          // 蠕行減速的緩衝距離
    public float trafficLightCreepMaxPower = 0.35f;      // 蠕行時允許的最高動力比例

    [Header("🛑 惡意急煞設定")]
    [Header("🛑 急煞強度（兩段）")]
    // 現實裡「前車煞車」絕大多數是 3~5 m/s² 的一般減速，而且不會煞到全停就停在車道上；
    // 8~9 m/s²（≈0.9g，接近 ABS 極限）那種是真正的緊急煞車，一年遇不到幾次。
    // 舊版每一次都用 9 m/s² 煞到 0 —— 那不是「前車在開車」，那是「遊戲在考我」，
    // 玩家會把它歸類成機關而不是路況，學習遷移等於零。
    //
    // ⚠️ 緊急那一段之後應該綁到「看得見的原因」（行人穿越／號誌轉紅／機車切入），
    //    現在先用機率，等行人系統做好再改成由原因觸發。
    [Tooltip("關掉＝每次都用下面的 Brake Deceleration 煞到全停（舊行為）")]
    public bool useTwoTierBraking = true;
    [Tooltip("多少比例的急煞是「真正的緊急煞車」（有輪胎聲、煞到全停）。其餘是一般減速")]
    [Range(0f, 1f)] public float emergencyBrakeChance = 0.25f;
    [Tooltip("一般減速的減速度（m/s²）。真實的前車煞車大多落在 3~5")]
    public float normalBrakeDeceleration = 4f;
    [Tooltip("一般減速只降到原本速度的幾成，不會停死在車道上。0.35 = 降到三成五後繼續開")]
    [Range(0f, 1f)] public float normalBrakeTargetPercent = 0.35f;

    public float brakeDuration = 2.0f;
    // 煞完立刻恢復的話，玩家還在反應、前車就已經開走了 —— 看起來像「煞假的」。
    // 真實情況是前車要等行人走完才會再起步，這段停等本身也是玩家該學的：
    // 前車停著就是還有狀況沒解除，不要急著超它。
    [Tooltip("煞到底之後維持低速/停住幾秒才恢復正常行駛。行人穿越大約要 3 秒，建議 2~4")]
    public float brakeHoldSeconds = 2.5f;
    [Tooltip("急煞的減速度 (m/s²)。真車全力煞車約 8~9，數字越大停得越急。\n" +
             "從 30km/h(8.3m/s) 以 9 m/s² 煞停約 0.9 秒，之後停在原地直到 Brake Duration 結束")]
    public float brakeDeceleration = 9f;
    private bool isBrakingAction = false;

    [Header("⚙️ 彎道減速設定")]
    public float InclineBrakeAngle = 30f;
    public float minCornerSpeedPercent = 0.3f;

    [Header("🚀 起步安全偵測")]
    public Vector3 startBoxSize = new Vector3(2.0f, 1.5f, 10f); // 寬 2m，長 10m
    public Vector3 startBoxOffset = new Vector3(0f, 0.5f, 5f); // 往前偏移 5m (讓盒子剛好覆蓋前 10m)
    private float startBlockedTimer = 0f; // 🟢 新增：記錄「無法起步」持續了多久
    public float maxStartBlockedTime = 3f; // 🟢 新增：超過這個秒數強制解除死結

    [Header("🚀 路邊停靠衝出設定")]
    // rushOutSpeedBoost 已移除：那是「衝出瞬間速度從 0 一幀跳到 5 m/s」的插隊，
    // 跟轉向瞬移一起造成衝出很突兀。現在一律由 rushAccelerationRate(30) 爬上去，
    // 0→5 m/s 只要 0.17 秒，一樣猛但是連續的。
    [Tooltip("衝出時的速度倍率(相對 moveSpeed)。原本固定 1，等於用一般車流速度匯入，玩家感覺不到危險；\n1.8 會明顯地「切」進來")]
    public float rushOutSpeedMultiplier = 1.8f;
    [Tooltip("維持高速衝刺幾秒，之後在剩餘時間內平順降回一般速度")]
    public float rushBurstSeconds = 1.6f;
    [Tooltip("危險因子結束後再等幾秒才結算玩家反應，避免把「慢半拍但有反應」誤判成沒反應。\n側邊切入、急煞、倒車共用這個值")]
    public float hazardReactionWindowSeconds = 2f;
    [Tooltip("衝出時把目標點往玩家所在的橫向位置靠。\n關掉的話車只會開向自己的路網節點，看起來像「併入隔壁車道」，玩家不會覺得危險")]
    public bool rushAimAtPlayerLane = true;
    [Tooltip("最多可以偏離原本路線幾公尺。設太大車會開到對向車道或人行道上")]
    public float rushMaxLateralShift = 2.5f;
    [Tooltip("切入的前段忽略玩家車的擋路判定。\n關掉的話玩家一逼近，車就會因為安全偵測而停在原地不敢切出來 —— 那正是側邊切入失效的原因")]
    public bool rushIgnorePlayerWhileCommitting = true;

    // 切入承諾階段：只有這段期間會忽略玩家，之後恢復正常的前方偵測
    private bool rushCommitPhase = false;
    // 這次衝刺是不是「針對玩家的側邊切入」。路口淨空衝刺為 false
    private bool rushIsPlayerSideCutIn = false;

    // 上一幀的實際位移速度。輪胎轉動用這個，不用指令速度
    private float actualSpeed = 0f;

    [Header("🚗 排隊等待（避免一直往前擠）")]
    [Tooltip("前方障礙在這個距離內就視為「正常跟車排隊」，不算死鎖")]
    public float frontBlockedDistance = 10f;
    [Tooltip("排隊時要停多久才允許蠕行脫困。一般死鎖是 2 秒，排隊要明顯更久，\n這樣車子才會像灰車一樣安靜等前車，而不是每 2 秒往前擠一次")]
    public float frontBlockedStallTimeout = 12f;

    [Header("🚧 移動閘門")]
    [Tooltip("讓移動閘門套用跟前方偵測相同的排除條件（對向車、隔壁讓道中的車）。\n關掉會回到原本行為，但可能出現「輪胎想動車身卡住」的死鎖")]
    public bool gateUsesCorridorExclusions = true;
    [Tooltip("被排除的對象仍保留的硬性最小間距，純粹防止穿模（公尺）")]
    public float gateHardMinGap = 0.6f;

    // 衝出期間快取的玩家 Transform，只在 TriggerIntersectionRush 時抓一次
    private Transform rushPlayerTf;

    // 由 ExecuteRushRoutine 控制的即時倍率：衝刺期間是 rushOutSpeedMultiplier，之後衰減回 1
    private float rushSpeedFactor = 1f;

    [Header("🔊 側邊衝出音效")]
    [Tooltip("衝出瞬間播放的輪胎尖叫聲。專案內建素材：PROMETEO - Car Controller/Sounds/TireScreech.wav")]
    public AudioClip rushScreechClip;
    [Tooltip("播放用的 AudioSource。留空會自動新增一個，不會影響車上原有的音效元件")]
    public AudioSource rushAudioSource;
    [Range(0f, 3f)]
    public float rushScreechVolume = 1.5f;
    [Tooltip("這個距離內都是最大音量")]
    public float rushScreechMinDistance = 6f;
    [Tooltip("超過就聽不到。輪胎聲傳不遠，不用設太大")]
    public float rushScreechMaxDistance = 70f;

    [Header("🛑 急煞音效（與側邊衝出分開）")]
    [Tooltip("急煞專用的輪胎聲。專案內建素材：聲音/BrakeScreech_Short.wav（0.5 秒）。\n" +
             "留空 = 沿用上面側切的 clip，但會依 Brake Screech Max Seconds 裁短")]
    public AudioClip brakeScreechClip;
    [Tooltip("播放用的 AudioSource。留空會自動新增。\n刻意不跟側切共用 —— 兩個事件時間相近時才不會互相打斷")]
    public AudioSource brakeAudioSource;
    [Range(0f, 3f)]
    [Tooltip("音量倍率。跟側切一樣可以超過 1")]
    public float brakeScreechVolume = 1.3f;
    [Tooltip("最長播多久（秒）。側切是持續打滑所以聲音長，急煞是短促一下。\n0 = 不裁切，整段播完。用專屬短音檔時設 0 就好")]
    public float brakeScreechMaxSeconds = 0.6f;
    [Tooltip("裁切收尾的淡出時間。直接 Stop() 會有爆音")]
    public float brakeScreechFadeOut = 0.08f;
    [Tooltip("音高隨機範圍。拉高會更短促尖銳，比較像急煞而不是長滑行")]
    public Vector2 brakeScreechPitchRange = new Vector2(1.05f, 1.18f);
    public float rushAccelerationRate = 30f;  // 衝出瞬間專用的加速倍率，比一般 brakeHardness 更快

    [Header("🛑 路口預判與停讓設定 (整合1456)")]
    public float slowIntersectionSpeedPercent = 0.4f;
    public float approachDistance = 25f; // Slow/Stop 開始預判的距離
    public Vector3 stopBoxOffsetLeft = new Vector3(-13f, 0.5f, 12f);
    public Vector3 stopBoxOffsetRight = new Vector3(10f, 0.5f, 12f);
    public Vector3 stopBoxSize = new Vector3(18f, 2f, 10f);

    [Header("🛑 大路口停讓偵測框 (BigStop 節點專用)")]
    [Tooltip("大路口車道多，左右要看得更寬更遠。節點的 Intersection Type 設成 BigStop 時自動改用這一組")]
    public Vector3 bigStopBoxOffsetLeft = new Vector3(-16f, 0.5f, 12f);
    public Vector3 bigStopBoxOffsetRight = new Vector3(13f, 0.5f, 12f);
    public Vector3 bigStopBoxSize = new Vector3(24f, 2f, 14f);

    [Header("↰ 小路口：左轉車讓右轉車")]
    [Tooltip("我正在左轉、對方正在右轉且在這個距離內時，不再把它當「對向車」豁免掉，會煞停讓它。設 0 = 關閉這條規則")]
    public float leftYieldToRightTurnerRadius = 12f;
    private bool bezierIsLeftTurn = false;   // 這次貝茲轉彎是不是左轉
    private bool bezierIsRightTurn = false;  // 這次貝茲轉彎是不是右轉

    [Header("↰ 左轉讓對向直行（移植自 CarNavigation）")]
    public float turnYieldDistance = 15f;       // 離路口多近才開始檢查對向來車
    public float turnDetectionSideOffset = -2f; // 偵測框左右位移（負值偏左）
    public float turnForwardOffset = 21f;       // 偵測框中心往前距離
    public Vector3 turnBoxSize = new Vector3(7f, 2f, 40f);
    private float leftTurnYieldCooldown = 0f;

    // 🟢 Gizmos 偵錯用狀態（只影響顯示，不影響邏輯）
    private bool debugShowTurnYieldBox = false;   // 左轉讓路框是否啟動中
    private bool debugTurnYieldBlocked = false;   // 左轉讓路框是否偵測到對向車
    private TrafficPathNode debugYellowGridExitNode = null; // 黃網出口節點（null=沒在檢查）
    private bool debugYellowGridBlocked = false;  // 黃網出口是否堵塞

    [Header("🟡 黃色網狀線偵測設定（移植自 CarNavigation）")]
    public Vector3 yellowGridBoxSize = new Vector3(7f, 2f, 5f);   // 出口偵測框 (寬, 高, 長)
    public Vector3 yellowGridBoxOffset = new Vector3(-2f, 0f, 0f); // 相對出口節點的偏移

    private bool hasCheckedIntersection = false;
    private bool isCarDetectedAtStop = false;

    [HideInInspector] public bool baseHasMetPlayer = false;
    [HideInInspector] public Transform basePlayerVehicle;

    [Header("🚑 內側車道豁免")]
    // 避讓一律往右(ambulanceEvadeOffset 是沿 yRoadRight 的正值)。三線道上,
    // 位在救護車「左邊」的內側車道車如果照樣往右讓,會直接切進救護車要走的線 ——
    // 二次偏移還會讓它切得更深,反而把路擋死。
    //
    // 用幾何判斷而不是標節點:場景裡三線道以上的路段有 9 個區塊,逐一標記
    // 工作量大又容易在改路網時漏掉。
    //
    // 這道判斷很安全 —— 警笛廣播本來就只通知同向車(Dot(forward,forward) > 0.4),
    // 所以落在「救護車左邊」的只可能是同向的內側車道車,不會誤傷對向車。
    //
    // 門檻 2.5m:救護車走在自己那條鏈的左偏 2m 上,同車道的車離它 0~2m,
    // 內側車道的車離它約 3~5m(實測車道間距中位數 5.1m 減去救護車的 2m 左偏)。
    [Tooltip("車在救護車「左邊」超過這個距離就不避讓,維持原本車道正常行駛。" +
             "設 0 = 關閉這道豁免(回到舊行為:所有同向車都往右讓)")]
    public float ambulanceLeftLaneExemptDistance = 2.5f;
    private float lastLeftExemptLogTime = -999f;   // 內側車道豁免的診斷節流

    [Header("🚑 Ambulance Evade System")]
    [SerializeField] protected bool isYieldingForAmbulance = false;
    private bool isReturningFromYield = false; // 🚑 回歸中(只用於左方向燈顯示,移動交給正常導航)
    private float returnBlinkTimer = 0f;       // 回歸左燈持續時間

    /// <summary>
    /// 🔧 目前是否處於「剛解除避讓、正在切回主車道」的階段。
    ///
    /// 這段期間車子仍斜停在路邊、車身壓在車道邊緣,但 isYieldingForAmbulance 已經是 false。
    /// 主車道的車若把它當成一般障礙物就會煞停,而它自己又在禮讓主車道 → 兩台互等。
    /// 供 IsBesideYieldingVehicle() 判斷用,純唯讀,不影響任何既有流程。
    /// </summary>
    ///
    /// 🔧 舊版只看 isReturningFromYield,而那個旗標只活 returnBlinkTimer 的 2.5 秒
    ///    (原註解自己寫「只為了打左燈」)。2.5 秒內若還沒切回車道,白車就從
    ///    「回歸中」變回「一般障礙物」→ 主車道的車開始為它煞停 →
    ///    它自己也偵測到主車道的車而停下 → 兩台互等到逾時保底。
    ///
    ///    改成對齊灰車 CarNavigation.IsReturningFromYieldNow 的「時間窗 或 殘留偏移」寫法:
    ///    白車沒有 currentYieldOffset 那種持續衰減的變數,改用「車身離車道中線的實際橫向距離」
    ///    量測 —— 只要人還壓在車道邊緣沒回到中線,就一直保有「回歸中」身分。
    ///
    ///    兩道保險避免變成永久豁免:
    ///      (1) 解除避讓後最多維持 returnIdentityMaxTime 秒
    ///      (2) 故障車繞行中(bypassPhase >= 1)的側偏不算,那是另一套邏輯
    ///    另外 IsBesideYieldingVehicle() 原有的三道安全鎖(距離下限、橫向 1.2m、正前方
    ///    27° 錐)全部照舊 —— 白車真的切進主車道時 rel.x 變小,豁免自動失效,對方照樣會煞。
    public bool IsReturningFromYieldNow =>
        isReturningFromYield
        || (!isYieldingForAmbulance
            && bypassPhase == 0
            && Time.time - lastYieldReleaseTime < returnIdentityMaxTime
            && GetLateralDistanceFromLaneCenter() > returnLaneOffsetThreshold);

    [Header("🤝 回歸主車道:身分持續判定")]
    [Tooltip("解除避讓後,只要車身離車道中線還超過這個距離,就一直算「回歸中」,主車道的車才會持續豁免它(公尺)")]
    public float returnLaneOffsetThreshold = 0.8f;
    [Tooltip("上面那條的保險上限:解除避讓後最多維持這麼久的「回歸中」身分,避免永久豁免(秒)")]
    public float returnIdentityMaxTime = 12f;
    [Tooltip("🤝 切回主車道前禮讓:主車道的車速度高於這個值才等它先走(對齊灰車的 1m/s);設 0 = 只要有車就等")]
    public float mergeHoldMinSpeed = 1f;
    [Tooltip("🤝 禮讓最久幾秒就強制切回(免得永遠等不到縫隙)")]
    public float mergeHoldTimeout = 8f;

    /// <summary>🤝 車身離目前路段中線的橫向距離(公尺)。算不出路段方向時回 0(當作已在中線上)。</summary>
    private float GetLateralDistanceFromLaneCenter()
    {
        if (currentWaypointNode == null) return 0f;

        Vector3 segDir = (lastWaypointNode != null && lastWaypointNode != currentWaypointNode)
            ? currentWaypointNode.transform.position - lastWaypointNode.transform.position
            : currentWaypointNode.transform.position - transform.position;
        segDir.y = 0f;
        if (segDir.sqrMagnitude < 0.01f) return 0f;
        segDir.Normalize();

        Vector3 origin = (lastWaypointNode != null && lastWaypointNode != currentWaypointNode)
            ? lastWaypointNode.transform.position
            : currentWaypointNode.transform.position;
        Vector3 toCar = transform.position - origin; toCar.y = 0f;

        return Mathf.Abs(Vector3.Dot(toCar, Vector3.Cross(Vector3.up, segDir)));
    }
    private float mutualStallTimer = 0f;       // 🤝 互相禮讓死鎖計時(停太久就蠕行脫困)
    private bool isMergeHolding = false;       // 🤝 切回主車道前,禮讓主車道來車中(停等,不把車頭伸進去)
    private float mergeHoldTimer = 0f;         // 禮讓太久的保底計時
    [Tooltip("🚑 救護車要開到超過白車車頭這麼多公尺,白車才開始回主車道(調大=晚點回歸)")]
    public float ambulanceReturnAheadDistance = 8f;
    public float ambulanceEvadeOffset = 3.5f;
    public float ambulanceForwardPush = 2.5f;
    [HideInInspector] public float navLateralOffset = 0f; // 🚑 救護車繞行用:把導航目標往側邊偏(交給節點跟隨開過去)

    [Header("🚧 故障車繞行")]
    [Tooltip("遇到故障車/三角錐時往左偏多少公尺繞過")]
    public float accidentBypassOffset = 2.2f;
    [Tooltip("前方多遠開始偵測故障車")]
    public float accidentBypassDetect = 16f;
    [Tooltip("偏出去/切回來的速度")]
    public float accidentBypassRampSpeed = 1.5f;
    [Tooltip("超過故障車多少公尺後才切回主車道")]
    public float accidentPassClearance = 10f;
    [Tooltip("繞行時的最低速度(真實世界也要快速通過,免得對向來車)")]
    public float accidentBypassSpeed = 7f;
    [Tooltip("切換車道的柔順度(秒):越大越溫柔,越小越果斷")]
    public float accidentBypassSmoothTime = 1.8f;
    [Tooltip("變換車道時的前瞻距離:越大車頭轉得越小越平順(建議 15~25)")]
    public float bypassLookAhead = 20f;
    [Tooltip("窄路借道前,往左前方看多遠確認沒有對向來車(公尺)")]
    public float oncomingCheckDistance = 30f;
    [Range(0.2f, 1f)]
    [Tooltip("變換車道時的轉向柔化:越小車頭轉得越慢越柔")]
    public float bypassTurnSoftness = 0.55f;
    [Tooltip("每一刻最多橫向追趕幾公尺:越小車頭角度越小、越不會過衝甩頭(建議 0.8~1.5)")]
    public float bypassMaxApproach = 1.0f;
    [Tooltip("所有「轉正/對齊車道」動作的平滑度:越小越像真車緩緩回正(建議 2~4)")]
    public float alignSmoothSpeed = 2.5f;
    [Tooltip("🚑 救護車在後方且我人在路口內時,用這個速度快速開出路口")]
    public float clearJunctionSpeed = 10f;
    [Tooltip("離中線多少公尺內就不再修正(真車也不會硬拉回正中央)")]
    public float laneCenterDeadzone = 0.35f;
    [Header("🔊 避讓時玩家擋在側前方")]
    [Tooltip("玩家擋住幾秒後開始按喇叭催")]
    public float playerBlockHonkDelay = 2f;
    [Tooltip("玩家一直不讓,幾秒後淡出消失(免得永遠卡著讓不了道)")]
    public float playerBlockDespawnTime = 10f;
    private float playerBlockYieldTimer = 0f;
    private float nextYieldHonkTime = 0f;
    private float pathAimHoldTimer = 0f; // 繞行結束後仍用平滑路徑跟隨的緩衝時間
    // ⚠️ 故障車繞行專用的側偏量,跟救護車的 navLateralOffset 完全分開,兩套邏輯互不影響
    private float accidentLateralOffset = 0f;
    private int bypassPhase = 0;              // 0=無 1=左移中 2=直行通過中 3=切回中
    private Vector3 bypassObstaclePos;        // 記住故障車位置(物件可能被清掉)
    private float lastAccidentSeenTime = -999f; // 🚧 最後一次看到故障車的時間(合理停等判定)
    private float bypassOffsetVel = 0f;       // SmoothDamp 用速度暫存
    [Header("🔍 抽動診斷")]
    public bool debugSnapLog = false;         // 勾選後,車頭單幀轉動過大就印出當下狀態
    private float prevHeadingY = -999f;
    private string prevNodeName = "";
    private float prevNavOffset = 0f;
    private Vector3 lastAimDir = Vector3.zero;   // 這一幀的瞄準方向
    private Vector3 prevAimDir = Vector3.zero;   // 上一幀的瞄準方向
    public float ambulanceEvadeOffsetMax = 5f;
    [Tooltip("救護車速度低於這個值才算「被卡住」(m/s)")]
    public float ambulanceStuckSpeed = 1.0f;
    [Tooltip("救護車連續卡住幾秒才觸發二次偏移(避免它只是路口減速就誤判)")]
    public float ambulanceStuckTime = 1.5f;
    private float ambBlockedTimer = 0f;
    private float lastYieldLateral = -999f;   // 上一幀的靠邊進度
    private float yieldStallTimer = 0f;       // 側移卡住多久(卡住就強制轉正,不要一直斜著)      // 🚑 救護車還卡後方時,二次偏移的最大靠邊距離
    private float lastYieldTargetSeen = -999f; // 🔧 上一幀的靠邊目標,用來偵測「二次偏移把目標加大了」以解除卡住鎖定

    [Header("🔧 避讓抗左右擺盪 (只做濾波,不改避讓/回歸的判斷)")]
    // 兩個每幀重算、又沒有遲滯的判斷,合起來就是「車在多車道上左右搖擺」的來源:
    //   (1) yTarget 由 Raycast 每幀重算 —— 隔壁車道有車經過就從 3.5 塌到 1.0,車走了又彈回去
    //   (2) 前方 5.5m 的 SphereCast 一翻,車頭就在「斜右」與「轉正」之間跳
    // 從偏右 20 度轉回正前方,肉眼看就是「往左轉」,兩邊交替就是搖擺。
    // 這一組欄位只加濾波,不動任何避讓或回歸的判斷條件。
    [Tooltip("靠邊目標值的平滑速度(公尺/秒)。設 0 = 關閉平滑,完全回到舊行為")]
    public float yieldTargetSmoothSpeed = 1.2f;
    [Tooltip("前方阻擋要連續成立這麼久才轉正停等(秒)。要短,否則來不及停會撞上前車")]
    public float yieldFrontBlockEngageTime = 0.12f;
    [Tooltip("前方要連續淨空這麼久才恢復斜切(秒)。太小會左右擺盪,太大會反應遲鈍")]
    public float yieldFrontBlockReleaseTime = 0.45f;

    private float smoothedYieldTarget = -999f;   // 🔧 平滑後的靠邊目標(-999 = 尚未初始化)
    private bool yieldFrontBlocked = false;      // 🔧 遲滯後的「前方被擋」狀態
    private float yieldFrontStateTimer = 0f;     // 🔧 目前與上面那個狀態相反的情況持續了多久
    // 🔧 靠邊期間鎖定的路段基準(方向 + 橫向量測原點)。
    //    每幀從節點重算會因為節點推進、路口轉向而跳動,導致車頭失控;整段用同一組才穩。
    [Header("🛞 彎道修正")]
    // yLateral(已經讓出去多少)是相對於 yieldLateralOriginLocked 量的。
    // 原本每次節點推進、只要路段方向差在 30 度內就會重新鎖定原點 ——
    // 直路上沒差,但彎道上路會往車的方向彎過去,重算後的 yLateral 就變小,
    // 車以為自己還沒讓到位,而目標也跟著位移 → 實際偏移量一直很小。
    // 開始側移之後就把基準凍住,量到的才是真正的累積偏移量。
    // (灰車 CarNavigation 的 wcFreezeBaselineWhileSliding 是同一件事,已實測有效)
    [Tooltip("開始側移後凍結量測基準,避免彎道上偏移量被重算而縮水。取消勾選 = 舊行為")]
    public bool yieldFreezeBaselineWhileSliding = true;

    private bool yieldBaselineFrozen = false;   // 🛞 已開始側移 → 不再重新鎖定量測基準
    private Vector3 yieldRoadDirLocked = Vector3.zero;
    private Vector3 yieldLateralOriginLocked = Vector3.zero;
    public float ambulanceLateralSlideSpeed = 1.5f; // 🚑 避讓橫向滑移速度(被前車卡住也能靠邊)
    public bool useDynamicOffsetOnly = true;        // 🚑 true=白車一律用動態偏移,不去避讓點
    public bool isInWideRoad = false;
    public float narrowEscapeSpeed = 16f;
    private float lastYieldReleaseTime = -999f;
    public float yieldReEntryCooldown = 1.0f; // 剛解除避讓後，這段時間內不接受重新被拖入避讓
    // 加在欄位區
    protected float nearestForwardObstacleDistance = Mathf.Infinity;
    public float minSafeGapToLateralYield = 6f; // 可在 Inspector 調
    private float smoothedFrontHeadingDot = 1f; // 🟢 新增：平滑車頭夾角，避免轉彎時判斷抖動


    private float ambulancePassedTimer = 0f;
    private bool isUsingOffsetFallback = false; // 新增：明確記錄當前是否啟動動態偏移備援
    private bool isParkedAtYieldNode = false; // 🟢 新增：明確記錄「目前是否死等在避讓點」

    protected bool isInIntersection = false;
    protected float originalNormalSpeed;

    [Header("🛑 煞車燈實體綁定")]
    public MeshRenderer leftBrakeLightRenderer;
    public MeshRenderer rightBrakeLightRenderer;
    [Header("💡 倒車燈實體綁定")]
    public MeshRenderer[] reverseLightRenderers; // 變成陣列，名稱建議改成複數

    [Header("💡 方向燈實體綁定")]
    public MeshRenderer leftFrontRenderer;
    public MeshRenderer leftRearRenderer;
    public MeshRenderer rightFrontRenderer;
    public MeshRenderer rightRearRenderer;

    public float blinkSecondsAhead = 5.0f;
    public float blinkFadeSpeed = 1.8f;
    public float maxEmissionIntensity = 7.0f;
    public float turnAngleThreshold = 15f;
    public float turnOffDelay = 2.5f;
    [Range(0.1f, 0.9f)] public float lightOnRatio = 0.55f;
    public float smoothTransitionSpeed = 6.5f;

    [Header("🛑 煞車燈觸發與淡入淡出設定")]
    public float brakeDecelerationThreshold = 3.0f;
    [ColorUsage(true, true)] public Color brakeOnColor = new Color(3f, 0f, 0f);
    public float brakeFadeInSpeed = 12.0f;
    public float brakeFadeOutSpeed = 5.0f;

    private Material leftBrakeMat;
    private Material rightBrakeMat;
    private float currentBrakeWeight = 0f;

    [Header("🚨 緊急煞車設定")]
    public float emergencyThreshold = 0.8f;   // 煞車權重超過多少時觸發閃爍
    public float emergencyBlinkFrequency = 5.0f; // 每秒閃爍幾次

    [Header("🛞 輪子綁定與參數")]
    public Transform leftFrontWheel;
    public Transform rightFrontWheel;
    public Transform leftRearWheel;
    public Transform rightRearWheel;
    public float maxSteeringAngle = 35f;
    public float wheelSteerSpeed = 10f;
    public float wheelRollSpeedMultiplier = 1.5f;

    [Header("🔶 側切預告 (玩家逼近時先打方向燈＋前輪轉出)")]
    [Tooltip("關掉＝維持舊行為：沒有任何預告，直接衝出")]
    public bool sideCutTelegraphEnabled = true;
    [Tooltip("往哪一側切出，由車自己判斷（看它接下來要去的路點在哪一側）。取消勾選＝改用下面角度的正負號手動指定")]
    public bool telegraphAutoSide = true;
    [Tooltip("前輪轉出的角度大小。自動判斷關掉時，負值＝往左、正值＝往右")]
    public float telegraphSteerAngle = 20f;
    [Tooltip("前輪從打直轉到定位的速度。越大轉得越快")]
    public float telegraphSteerSpeed = 3.5f;

    [Tooltip("車身往車道側探出的上限（公尺）。跟下面的前進上限誰先到就停。0 = 不限制橫向")]
    public float telegraphCreepDistance = 0.45f;
    [Tooltip("往前蹭的上限（公尺）。這條是避免蹭到前面那台停放車，通常它會比橫向上限先到")]
    public float telegraphMaxCreepForward = 1.2f;
    [Tooltip("車身往前蹭的速度（公尺/秒）。慢一點比較像在猶豫、也比較好被看見")]
    public float telegraphCreepSpeed = 0.45f;
    [Tooltip("軸距（公尺），決定同樣的前輪轉角能把車頭轉出多少。留 0 = 開場自動從前後輪的實際距離量，通常不用填")]
    public float telegraphWheelbase = 0f;

    // 線索要照「玩家需要多少提前量」由長到短排，而不是照「駕駛實際做了什麼」排：
    //   方向燈 = 他要往哪一側出來（要換道還是要減速）→ 玩家得做決策，需要最長的提前量
    //   靜止車煞車燈亮 = 車上有人腳踩著，可能踩一分鐘 → 幾乎沒有資訊量
    //   煞車燈熄滅 = 他現在要動了 → 只需要 0.5~1 秒
    // 自排要踩煞車才能退出 P 檔，所以真車確實常常煞車燈先亮，但那是駕駛端的機械順序，
    // 對判讀沒有幫助。而且紅色煞車燈就在同一組尾燈上，會蓋掉旁邊那顆琥珀方向燈的辨識度，
    // 兩個燈同時亮等於把「往哪」和「現在」壓成同一個事件，玩家只讀得到一個線索。
    [Tooltip("預告最前面先「只閃方向燈」幾秒，煞車燈這段還不亮。\n" +
             "目的是讓「他要往哪一側出來」這個線索有自己的辨識窗口，不被同一組尾燈的紅光蓋掉。\n" +
             "⚠️ 這段 + 下面的煞車猶豫時間，總和要小於 ReverseTriggerController 的 Telegraph Min Lead Seconds，\n" +
             "否則最短提前量的情況下車還沒開始蹭出去就被觸發衝出，等於沒有預告。\n" +
             "0 = 關掉這一段，維持舊行為（方向燈與煞車燈同時亮）")]
    public float telegraphSignalLeadSeconds = 0.4f;

    [Tooltip("方向燈前導結束後，再「踩著煞車猶豫」幾秒：煞車燈亮＋方向燈續閃＋前輪轉出，但車還不動。\n" +
             "時間到才鬆煞車開始蹭出去 —— 煞車燈熄滅是真實世界判讀「他真的要動了」最強的那個線索。\n" +
             "0 = 關掉這一段，維持舊行為（方向燈前導結束就直接蹭）")]
    public float telegraphBrakeHoldSeconds = 0.7f;

    [Header("🤝 互動式側切 (依玩家有沒有反應決定切多兇)")]
    [Tooltip("關掉＝維持舊行為：不管玩家有沒有減速，一律用兇狠版切出")]
    public bool interactiveSideCut = true;
    [Tooltip("玩家有反應時的速度倍率。1 = 用一般車流速度平順匯入，不搶不逼。\n" +
             "沒反應時仍走原本的 Rush Out Speed Multiplier")]
    public float politeRushSpeedMultiplier = 1f;

    // 預告狀態：由 ReverseTriggerController 在玩家逼近到預告 TTC 時開啟，
    // 真正衝出（TriggerIntersectionRush）時關閉，之後轉向交還給正常的循跡邏輯。
    private bool    isTelegraphing = false;
    private float   telegraphSteerCurrent = 0f;
    private float   telegraphSide = -1f;          // -1 = 往左切出, +1 = 往右
    private Vector3 telegraphOriginPos;           // 探出前的原始位置（事件取消要還原）
    private Quaternion telegraphOriginRot;        // 探出前的原始車頭方向（同上，不還原會越歪越多）
    private float   telegraphCreepCurrent = 0f;   // 已經往前蹭了多少（公尺）
    private float   telegraphElapsed = 0f;        // 預告開始到現在幾秒（煞車段↔蹭出段的分界）
    private float   resolvedWheelbase = 2.7f;     // 開場量到的軸距，供轉向幾何使用
    private bool    rushPlayerYielded = false;    // 這次切出時玩家有沒有先減速（決定切得兇不兇）

    /// <summary>
    /// 預告的三段時序（elapsed 由 UpdateSideCutTelegraph 累加）：
    ///   [0, 前導)                    只閃方向燈 ＋ 前輪轉出，煞車燈不亮、車不動
    ///   [前導, 前導+猶豫)            方向燈續閃 ＋ 煞車燈亮，車仍不動
    ///   [前導+猶豫, 衝出)            煞車燈熄滅 ＋ 開始蹭出去
    /// </summary>
    private float TelegraphStillDuration => telegraphSignalLeadSeconds + telegraphBrakeHoldSeconds;

    /// <summary>
    /// 預告期間是否還踩著煞車 → 煞車燈亮。
    /// 熄滅是「他真的要動了」的最後一個線索，時序上緊接著就是蹭出去。
    /// 🔶 前導那段刻意不亮 —— 讓方向燈先有一個不被紅光蓋住的辨識窗口。
    /// </summary>
    private bool TelegraphBrakeOn => isTelegraphing
                                     && telegraphElapsed >= telegraphSignalLeadSeconds
                                     && telegraphElapsed < TelegraphStillDuration;

    /// <summary>
    /// 預告期間車身是否還釘著不動。
    /// ⚠️ 不能用 !TelegraphBrakeOn 代替 —— 那個在「前導段」也是 false，
    ///    會變成方向燈才剛開始閃車就先蹭出去了，順序整個反過來。
    /// </summary>
    private bool TelegraphHoldStill => isTelegraphing && telegraphElapsed < TelegraphStillDuration;

    [Header("🎺 喇叭功能 (玩家阻擋偵測)")]
    public float hornWaitTime = 2.0f; // 偵測到擋路後，等幾秒才按

    [Tooltip("喇叭音效。留空 = 沿用車上第一顆 AudioSource 原本的 clip(舊行為)。\n" +
             "⚠️ 切車.prefab 上那顆目前掛的是 hum.wav，不是喇叭聲 —— 建議指定 聲音/喇叭_雙音.wav")]
    public AudioClip hornClip;
    [Tooltip("喇叭音量倍率。只在有指定 Horn Clip 時生效")]
    public float hornVolume = 1f;
    private AudioSource playerHornAudioSource; // 🔧 原名 hornAudioSource：與子類別 AmbulanceAI 的公開欄位同名，Unity 會報「同一欄位名被序列化多次」

    [Header("🎺 側切後對玩家按喇叭")]
    // 真實路況裡，被切進來之後最常聽到的就是喇叭。這一聲同時是三件事：
    //   ① 真實感 —— 比任何音效都更像實際會遇到的狀況
    //   ② 危險訊號 —— 告訴玩家「你離太近了」，接上跟車距離的教學
    //   ③ 互動的結果 —— 玩家有讓就不會被按，做對事的回饋要一致
    [Tooltip("關掉＝維持舊行為：側切後不按喇叭")]
    public bool postCutHornEnabled = true;
    [Tooltip("玩家在這個距離內才按（公尺）。太遠按了沒有意義")]
    public float postCutHornDistance = 9f;
    [Tooltip("切出後隔多久才允許按（秒）。切出瞬間就按太急，像是預謀好的")]
    public float postCutHornDelay = 0.4f;
    [Tooltip("每秒印一次「為什麼這一幀沒按喇叭」，用來確認是哪個條件沒過。查完記得關掉")]
    public bool logPostCutHorn = false;

    private bool  postCutHornDone = false;   // 一次側切只按一聲，不連環按
    private float rushStartTime = -999f;
    private float lastHornDiagTime = -999f;
    private float blockedTimer = 0f;
    private bool isHonking = false;

    private float currentSteerAngle = 0f;
    protected float currentHealthSpeed;
    public float CurrentHealthSpeed => currentHealthSpeed;
    private Vector3 previousPosition;

    [HideInInspector] public bool isReverseAction = false;
    [HideInInspector] public bool isForcedReversing = false;
    [Header("⏱ 路口互卡逾時銷毀")]
    public float intersectionStuckTimeout = 8f; // 路口內靜止超過這個秒數就自我銷毀
    private float intersectionStuckTimer = 0f;
    public float maxWaitReturnTime = 6f;  // 等淨空最多幾秒，逾時強制回歸
    private float waitReturnTimer = 0f;
    [Header("💀 卡死自毀(玩家範圍內不消失,只在中間帶淡出)")]
    public float stuckDetectRadius = 120f;   // 超過這距離交給 200m 栓繩,不歸這套
    public float playerSafeRadius = 60f;     // 玩家這範圍內的車絕不消失(含後照鏡看得到的)
    public float bandStuckTime = 5f;         // 只在 playerSafe~detect 中間帶卡這麼久才淡出
    public float fadeOutDuration = 0.5f;
    private float anywhereStuckTimer = 0f;
    private Vector3 anywhereStuckLastPos;
    private bool isFadingOut = false;
    public float playerBlockDistance = 15f; // 玩家擋在這距離內的正前方 → 合理等待,不消失
    // 🟢 路口互卡保險：在路口內、非合理等待狀態下靜止太久 → 自我銷毀讓補車機制接手
    private void HandleIntersectionDeadlock()
    {
        if (this is AmbulanceAI) return; // ⚠️ 救護車絕不自毀，它有自己的卡死清除邏輯
        if (isFadingOut) return;
        bool stuckCandidate = isInIntersection
            && currentHealthSpeed < 0.3f
            && !isWaitingAtTrafficLight
            && !isStaticParked
            && !isReverseAction && !isForcedReversing && !isBrakingAction
            && !isParkedAtYieldNode
            // 🔧 這四個都是「照劇本在停」,不是死鎖,不該被倒數自毀。
            //    少了它們,白車在路口附近避讓/回歸時一邊正常演出、一邊被自己的看門狗
            //    倒數 intersectionStuckTimeout 秒 → 時間到直接 Destroy,車就憑空消失。
            //    同一支檔案的全地圖看門狗 HandleAnywhereStuckDespawn() 的 legitState
            //    早就把這些列為合理停等,這裡是補成一致,沒有動避讓/回歸本身的任何邏輯。
            && !isYieldingForAmbulance      // 避讓中(靠邊讓救護車)
            && !IsReturningFromYieldNow     // 回歸中(整段切回主車道的過程,不只左燈那 2.5 秒)
            && !isMergeHolding              // 禮讓主車道來車、等縫隙中
            && !IsHoldingForJunction;       // 救護車要過路口,我是橫向車停在路口前等

        if (!stuckCandidate)
        {
            intersectionStuckTimer = 0f;
            return;
        }

        // 玩家在附近擋路 → 不銷毀（喇叭邏輯處理），計時歸零
        Transform p = GetPlayerTransform();
        if (p != null && Vector3.Distance(transform.position, p.position) < 12f)
        {
            intersectionStuckTimer = 0f;
            return;
        }

        intersectionStuckTimer += Time.deltaTime;
        if (intersectionStuckTimer >= intersectionStuckTimeout)
        {
            if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == gameObject.GetInstanceID())
            {
                currentWaypointNode.isOccupied = false;
                currentWaypointNode.occupiedByVehicleID = 0;
            }
            UnityEngine.Debug.Log($"<color=magenta>⚠️ {name} 路口互卡逾時 {intersectionStuckTimeout}s，自我銷毀</color>");
            Destroy(gameObject);
        }
    }
    //💀 全地圖卡死看門狗(白車特化版):所有「合法靜止」的演出/停等狀態都排除,只殺真正的死鎖
    //💀 卡死看門狗(範圍+視野+雙秒數):真死鎖才清,視野外瞬清、視野內淡出
    private void HandleAnywhereStuckDespawn()
    {
        if (this is AmbulanceAI) return; // ⚠️ 救護車絕不自毀
        if (isFadingOut) return;         // 正在淡出,別重複觸發

        bool legitState = isStaticParked || isWaitingAtTrafficLight
            || isReverseAction || isBrakingAction || isForcedReversing
            || isRushingOut || isInCooldown
            || isYieldingForAmbulance || isReturningFromYield || isParkedAtYieldNode
            || isMergeHolding                              // 🤝 等主車道縫隙 = 合理停等,不是卡死
            || (Time.time - lastAccidentSeenTime < 2f);   // 🚧 被故障車擋住 = 合理塞車

        bool hasMoved = (transform.position - anywhereStuckLastPos).sqrMagnitude > 0.25f;

        if (legitState || hasMoved)
        {
            anywhereStuckTimer = 0f;
            anywhereStuckLastPos = transform.position;
            return;
        }

        Transform p = GetPlayerTransform();
        if (p == null) return;

        // 玩家範圍內 → 絕不消失(含後照鏡看得到的);太遠(>偵測範圍)→ 交給 200m 栓繩
        float distToPlayer = Vector3.Distance(transform.position, p.position);
        if (distToPlayer < playerSafeRadius || distToPlayer > stuckDetectRadius)
        {
            anywhereStuckTimer = 0f;
            return;
        }
        // 🔊 玩家擋在正前方 → 這是合理等待(該按喇叭催,不是蒸發),計時歸零不消失
        Vector3 toPlayer = p.position - transform.position; toPlayer.y = 0f;
        if (toPlayer.magnitude < playerBlockDistance && Vector3.Dot(transform.forward, toPlayer.normalized) > 0.5f)
        {
            anywhereStuckTimer = 0f;
            return;
        }
        anywhereStuckTimer += Time.deltaTime;
        if (anywhereStuckTimer < bandStuckTime) return;

        if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == gameObject.GetInstanceID())
        {
            currentWaypointNode.isOccupied = false;
            currentWaypointNode.occupiedByVehicleID = 0;
        }

        UnityEngine.Debug.Log($"<color=magenta>💀 [白車卡死淡出] {name} 距玩家 {distToPlayer:F0}m</color>");
        isFadingOut = true;
        StartCoroutine(FadeOutAndDestroy(fadeOutDuration));
    }

    // 🚧 故障車繞行:前方有故障車/三角錐 → 往左偏一點繞過;完全通過後切回。
    //    往左偏前先確認左側淨空、切回前先確認右側淨空,不會撞到旁邊的車
    private void HandleAccidentBypass()
    {
        if (pathAimHoldTimer > 0f) pathAimHoldTimer -= Time.deltaTime;

        if (this is AmbulanceAI) return;          // 救護車有自己的固定左偏,不走這套

        // 🚧 走進封鎖路段的車:在玩家「後方」就淡出消失(玩家看不到,免得硬擠事故現場);
        //    在玩家前方的則照常繞行,才不會突兀
        if (AccidentDirector.IsAccidentActive && currentWaypointNode != null
            && AccidentDirector.BlockedPathNodes != null
            && AccidentDirector.BlockedPathNodes.Contains(currentWaypointNode))
        {
            Transform p = GetPlayerTransform();
            if (p != null)
            {
                Vector3 toMe = transform.position - p.position; toMe.y = 0f;
                if (Vector3.Dot(p.forward, toMe) < 0f) { ForceFadeOutClear(); return; } // 在玩家後方 → 淡出
            }
        }

        if (isYieldingForAmbulance)               // 避讓救護車優先,不併用
        {
            bypassPhase = 0;
            accidentLateralOffset = Mathf.SmoothDamp(accidentLateralOffset, 0f, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
            return;
        }

        // 掃所有 Layer,只用 Tag/元件辨識故障物(避免 Layer 沒設對而偵測不到)
        bool seeObstacle = false; Vector3 obsPos = Vector3.zero;
        RaycastHit[] hits = Physics.SphereCastAll(transform.position + Vector3.up * 0.5f, 1.0f,
            transform.forward, accidentBypassDetect, ~0, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (IsMyOwnCollider(h.collider)) continue;
            if (!h.collider.CompareTag("RoadBlock") && h.collider.GetComponentInParent<HazardLights>() == null) continue;
            Vector3 rel = transform.InverseTransformPoint(h.collider.bounds.center);
            if (Mathf.Abs(rel.x) < 3f && rel.z > 0f) { seeObstacle = true; obsPos = h.collider.bounds.center; break; }
        }
        if (seeObstacle) lastAccidentSeenTime = Time.time;   // 被故障車擋著 → 合理停等,不算卡死
        if (seeObstacle && bypassPhase != 3) bypassObstaclePos = obsPos;

        switch (bypassPhase)
        {
            case 0: // 還沒繞:看到故障車 + 左側淨空 + 對向無來車 → 開始左移
                bool leftBlockedW = Physics.Raycast(transform.position + Vector3.up * 0.5f, -transform.right,
                        out RaycastHit lh0, accidentBypassOffset + 1.5f, ~0, QueryTriggerInteraction.Ignore)
                    && !IsMyOwnCollider(lh0.collider);

                // 🚧 窄路借道:往左前方長距離掃描,有對向來車就先不切
                bool oncomingW = false;
                Vector3 obliqueW = (transform.forward - transform.right * 0.35f).normalized;
                foreach (var oh in Physics.SphereCastAll(transform.position + Vector3.up * 0.5f, 1.0f,
                             obliqueW, oncomingCheckDistance, detectionLayer, QueryTriggerInteraction.Ignore))
                {
                    if (IsMyOwnCollider(oh.collider)) continue;
                    var oT = oh.collider.GetComponentInParent<ReverseThreatAICar>();
                    var oN = oh.collider.GetComponentInParent<CarNavigation>();
                    Vector3 ofwd = oT != null ? oT.transform.forward : (oN != null ? oN.transform.forward : oh.collider.transform.root.forward);
                    if (Vector3.Dot(transform.forward, ofwd) < -0.5f) { oncomingW = true; break; }
                }

                // 🔧 繞行開關:AccidentDirector 沒開放繞行 → 停在相位 0,不啟動橫向偏移。
                //    只擋「相位 0 → 1」這個轉換,上面的偵測、lastAccidentSeenTime(合理停等,
                //    避免被卡死看門狗誤刪)、以及跟車減速全部照常運作。
                if (seeObstacle && !leftBlockedW && !oncomingW && AccidentDirector.AllowAIBypassNow)
                    bypassPhase = 1;
                accidentLateralOffset = Mathf.SmoothDamp(accidentLateralOffset, 0f, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                break;

            case 1: // 左移中(承諾繞行,不再猶豫)
                accidentLateralOffset = Mathf.SmoothDamp(accidentLateralOffset, -accidentBypassOffset, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                currentHealthSpeed = Mathf.Max(currentHealthSpeed, accidentBypassSpeed);
                if (Mathf.Abs(accidentLateralOffset + accidentBypassOffset) < 0.25f) bypassPhase = 2;
                break;

            case 2: // 保持偏移、轉正直行快速通過;超過故障車 accidentPassClearance 公尺才進入切回
                accidentLateralOffset = Mathf.SmoothDamp(accidentLateralOffset, -accidentBypassOffset, ref bypassOffsetVel, accidentBypassSmoothTime * 0.5f, accidentBypassRampSpeed);
                currentHealthSpeed = Mathf.Max(currentHealthSpeed, accidentBypassSpeed);
                if (Vector3.Dot(transform.forward, bypassObstaclePos - transform.position) < -accidentPassClearance)
                    bypassPhase = 3;
                break;

            case 3: // 切回:右側淨空才切,沒淨空就保持在左側等
                bool rightBlocked = Physics.Raycast(transform.position + Vector3.up * 0.5f, transform.right,
                        out RaycastHit rh0, 3f, ~0, QueryTriggerInteraction.Ignore)
                    && !IsMyOwnCollider(rh0.collider);
                if (!rightBlocked)
                    accidentLateralOffset = Mathf.SmoothDamp(accidentLateralOffset, 0f, ref bypassOffsetVel, accidentBypassSmoothTime, accidentBypassRampSpeed);
                if (Mathf.Abs(accidentLateralOffset) < 0.15f) { bypassPhase = 0; pathAimHoldTimer = 3f; }
                break;
        }
    }

    // 🚑 給外部(救護車清路 / 多退機制)呼叫:淡出清除,不瞬間消失
    public void ForceFadeOutClear() => ForceFadeOutClear(fadeOutDuration);

    /// <summary>
    /// 指定淡出秒數的版本。給事故走廊清車用 —— 那邊需要依距離調整：
    /// 遠處的車快速收掉（反正看不清楚），近處的慢慢縮小才不刺眼。
    /// </summary>
    public void ForceFadeOutClear(float duration)
    {
        if (isFadingOut) return;
        if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == gameObject.GetInstanceID())
        {
            currentWaypointNode.isOccupied = false;
            currentWaypointNode.occupiedByVehicleID = 0;
        }
        isFadingOut = true;
        StartCoroutine(FadeOutAndDestroy(Mathf.Max(0.05f, duration)));
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
    private bool isInCooldown = false;
    private bool isRushingOut = false;
    protected float calculatedTargetSpeed;

    public enum IndicatorState { None, Left, Right }
    [SerializeField] private IndicatorState currentIndicatorMode = IndicatorState.None;

    private Material leftFrontMat;
    private Material leftRearMat;
    private Material rightFrontMat;
    private Material rightRearMat;

    private float turnOffTimer = 0f;
    private float pulseTime = 0f;
    private float currentBlinkWeight = 0f;

    private Transform activeAmbulanceTransform = null;
    private Color indicatorBaseColor = new Color(1f, 0.4f, 0f);
    private float parkedAtYieldTimer = 0f;
    public float maxParkedAtYieldTime = 8f; // 死等超過這個秒數，強制放棄避讓、恢復正常

    private readonly List<TrafficPathNode> plannedRoute = new List<TrafficPathNode>(); // 🟢 預抽路線（最多 3 格），模仿 CarNavigation 的 futureNodes
    private static Transform cachedPlayerTransform;
    private static float lastPlayerSearchTime = -999f;
    private bool incomingTurnLastFrame = false; // 上一幀是否預判到即將轉彎，供扇形提前放寬角度
    [ContextMenu("🔍 偵測自我診斷")]
    private void DebugSelfCheck()
    {
        // 1. 我自己的 Collider 分佈在哪些 Layer 上
        foreach (var c in GetComponentsInChildren<Collider>())
            UnityEngine.Debug.Log($"[自我診斷] 我的Collider: {c.name} | Layer={LayerMask.LayerToName(c.gameObject.layer)} | isTrigger={c.isTrigger} | enabled={c.enabled}");

        // 2. 周圍 20m 內所有車輛的 Collider（不做 Layer 過濾，全部掃出來），
        //    逐一標出「我的 detectionLayer 到底看不看得到它」
        foreach (var h in Physics.OverlapSphere(transform.position, 20f))
        {
            if (IsMyOwnCollider(h)) continue;

            bool isVehicle = IsPlayerCollider(h)
                || h.GetComponentInParent<ReverseThreatAICar>() != null
                || h.GetComponentInParent<CarNavigation>() != null
                || ExternalTrafficVehicle.Is(h); // 🚗[危險駕駛AI整合] 鬼切/路霸演員車也算車
            if (!isVehicle) continue;

            bool visibleToMe = (detectionLayer.value & (1 << h.gameObject.layer)) != 0;
            UnityEngine.Debug.Log(
                $"[自我診斷] 附近車輛: {h.transform.root.name} 的Collider [{h.name}] " +
                $"| Layer={LayerMask.LayerToName(h.gameObject.layer)} " +
                $"| isTrigger={h.isTrigger} " +
                $"| <b>我的雷達看得到={visibleToMe}</b>");
        }
    }

    // 🟢 共用玩家快取：找到就一直用，找不到時每 2 秒才重掃一次場景
    private static Transform GetPlayerTransform()
    {
        if (cachedPlayerTransform == null && Time.time - lastPlayerSearchTime > 2f)
        {
            lastPlayerSearchTime = Time.time;
            GameObject p = GameObject.FindGameObjectWithTag("Player");
            if (p != null) cachedPlayerTransform = p.transform;
        }
        return cachedPlayerTransform;
    }
    // 預先抽好下一個節點，讓「貝茲預判」和「實際換點」用同一個目標（模仿 CarNavigation 的 futureNodes）
    // 🟢 取得「currentWaypointNode 之後第 n 格」的預抽節點（n 從 1 開始，最多建議 3）
    // 計畫斷鏈（節點被避讓/合併等外部操作切換過）會自動作廢重抽，不需要手動清
    // 🚑 改成 protected:救護車要用實際規劃的下一個節點判斷會不會轉彎
    //    (不能用 nextChoices[0] —— 那是清單第一項,不見得是這台車真正要走的路)。
    protected TrafficPathNode GetPlannedNode(int stepsAhead)
    {
        if (currentWaypointNode == null) return null;

        // 1. 驗證既有計畫是否還接得上目前節點，從斷掉的地方起全部作廢
        TrafficPathNode anchor = currentWaypointNode;
        for (int i = 0; i < plannedRoute.Count; i++)
        {
            if (plannedRoute[i] == null || anchor.nextChoices == null || !anchor.nextChoices.Contains(plannedRoute[i])
    || (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedPathNodes.Contains(plannedRoute[i])))
            {
                plannedRoute.RemoveRange(i, plannedRoute.Count - i);
                break;
            }
            anchor = plannedRoute[i];
        }

        // 2. 長度不夠就往後隨機補抽
        while (plannedRoute.Count < stepsAhead)
        {
            TrafficPathNode tail = plannedRoute.Count == 0 ? currentWaypointNode : plannedRoute[plannedRoute.Count - 1];
            if (tail == null || tail.nextChoices == null || tail.nextChoices.Count == 0) return null; // 路網到頭
            plannedRoute.Add(PickNextChoice(tail));
        }

        return plannedRoute[stepsAhead - 1];
    }

    // 🟢 從節點的分支中挑下一個點。一般車隨機挑；子類別（救護車）可覆寫改變選路偏好
    // ── 🎯 指定路線（前置演員佈署專用）────────────────────────────────
    // 生出來的車如果照常在路口隨機選邊，很可能開到別條路去，
    // 玩家到達急煞觸發盒時前方還是沒車。這裡讓佈署端指定「開頭這幾步一定要走這條」，
    // 走完自動清空，之後完全回到原本的隨機選路。
    // 沒設定 forcedRoute 的車一行邏輯都不受影響（跟灰車 CarNavigation 那套對稱）。
    [HideInInspector] public List<TrafficPathNode> forcedRoute;
    private int forcedRouteIndex = 0;

    /// <summary>指定開頭要走的路線。傳 null 或空清單＝取消指定。</summary>
    public void SetForcedRoute(List<TrafficPathNode> route)
    {
        forcedRoute = route;
        forcedRouteIndex = 0;
    }

    /// <summary>指定路線裡下一個「接得上 from」的節點。沒有就回 null，交還給隨機選路。</summary>
    private TrafficPathNode TakeForcedChoice(TrafficPathNode from)
    {
        if (forcedRoute == null || forcedRouteIndex >= forcedRoute.Count) return null;

        for (int i = forcedRouteIndex; i < forcedRoute.Count; i++)
        {
            TrafficPathNode want = forcedRoute[i];
            if (want == null) continue;
            if (!from.nextChoices.Contains(want)) continue;

            forcedRouteIndex = i + 1;
            if (forcedRouteIndex >= forcedRoute.Count) forcedRoute = null;   // 走完就放手
            return want;
        }
        return null;
    }

    protected virtual TrafficPathNode PickNextChoice(TrafficPathNode from)
    {
        // 🎯 有指定路線就照走（只影響被指定的那幾台佈署車）
        TrafficPathNode forced = TakeForcedChoice(from);
        if (forced != null) return forced;

        // 🚧 事故進行中,抽分岔時排除封閉節點;事故結束自動恢復
        if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedPathNodes != null && AccidentDirector.BlockedPathNodes.Count > 0)
        {
            List<TrafficPathNode> valid = new List<TrafficPathNode>();
            foreach (var n in from.nextChoices)
                if (n != null && !AccidentDirector.BlockedPathNodes.Contains(n)) valid.Add(n);

            if (valid.Count > 0)
                return valid[Random.Range(0, valid.Count)];
        }
        return from.nextChoices[Random.Range(0, from.nextChoices.Count)];
    }

    // 相容舊呼叫端：等同取第 1 格
    private TrafficPathNode PeekPlannedNextNode() => GetPlannedNode(1);
    // 🟢 供 CarNavigation 的左轉讓路偵測查詢：這台車接下來是否打算左轉
    public bool IntendsToLeftTurnEarly()
    {
        if (currentWaypointNode == null) return false;

        TrafficPathNode next = PeekPlannedNextNode();
        if (next == null) return false;

        Vector3 dirToTarget = (currentWaypointNode.transform.position - transform.position).normalized;
        Vector3 dirNext = (next.transform.position - currentWaypointNode.transform.position).normalized;

        if (Vector3.Angle(dirToTarget, dirNext) > 15f)
        {
            return Vector3.Cross(dirToTarget, dirNext).y < 0f; // 叉積 Y < 0 = 左轉
        }
        return false;
    }
    /// <summary>
    /// 未來幾個節點內會不會左轉。專門給紅綠燈判定用。
    ///
    /// 為什麼不直接用 IntendsToLeftTurnEarly()：那支只看「車 → 當前節點 → 下一節點」這一段，
    /// 車停在停止線時真正的轉彎往往還在再下一個節點，於是左轉車被判成直行車，
    /// 主綠燈一亮就放它左轉出去，跟對向直行車對撞。
    /// 這裡另開一支而不是把原本那支改遠，是因為原本那支還被「左轉讓直行」用著 ——
    /// 讓它看更遠會害車在彎道上提早停下來等根本不存在的對向車。
    /// </summary>
    public bool WillTurnLeftWithin(int steps = 2)
    {
        if (currentWaypointNode == null) return false;

        Vector3 fromPos = transform.position;
        TrafficPathNode at = currentWaypointNode;

        for (int i = 1; i <= steps; i++)
        {
            TrafficPathNode next = GetPlannedNode(i);
            if (at == null || next == null) return false;

            Vector3 dirIn = at.transform.position - fromPos;
            Vector3 dirOut = next.transform.position - at.transform.position;
            dirIn.y = 0f; dirOut.y = 0f;

            // 車剛好停在節點上時 dirIn 會趨近 0，方向是雜訊，這一段直接跳過往下看
            if (dirIn.sqrMagnitude > 0.01f && dirOut.sqrMagnitude > 0.01f)
            {
                dirIn.Normalize(); dirOut.Normalize();
                if (Vector3.Angle(dirIn, dirOut) > 15f)
                    return Vector3.Cross(dirIn, dirOut).y < 0f;   // 叉積 Y < 0 = 左轉
            }
            fromPos = at.transform.position;
            at = next;
        }
        return false;
    }

    protected virtual void Start()
    {
        moveSpeed = moveSpeed * Random.Range(0.85f, 1.15f);
        originalNormalSpeed = moveSpeed;
        currentHealthSpeed = isWaitingAtTrafficLight ? 0f : moveSpeed;
        previousPosition = transform.position;

        // 🚶 會動的車自動掛上「行人禮讓感測器」——免改 prefab,所有生成的車都生效。
        //    救護車(AmbulanceAI)在趕任務,不該為行人蠕行,排除。
        if (!isStaticParked && !(this is AmbulanceAI) && GetComponent<PedestrianYieldSensor>() == null)
            gameObject.AddComponent<PedestrianYieldSensor>();

        if (currentWaypointNode != null && !isStaticParked)
        {
            Vector3 targetPosition = currentWaypointNode.transform.position;
            targetPosition.y = transform.position.y;
            transform.LookAt(targetPosition);
            lastWaypointNode = currentWaypointNode;
        }

        // 🪨 一出生就是路邊靜停車 → 立刻上鎖(在第一次物理更新之前),玩家貼著停也撞不動
        if (isStaticParked) ApplyParkLock();

        ResolveWheelbase();   // 🔶 側切預告的轉向幾何要用

        if (leftFrontRenderer != null) leftFrontMat = leftFrontRenderer.material;
        if (leftRearRenderer != null) leftRearMat = leftRearRenderer.material;
        if (rightFrontRenderer != null) rightFrontMat = rightFrontRenderer.material;
        if (rightRearRenderer != null) rightRearMat = rightRearRenderer.material;
        if (leftBrakeLightRenderer != null) leftBrakeMat = leftBrakeLightRenderer.material;
        if (rightBrakeLightRenderer != null) rightBrakeMat = rightBrakeLightRenderer.material;

        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false);
    }

    protected virtual void Update()
    {
        SyncParkLockState(); // 🪨 靜停 ↔ 行駛 的狀態切換,同步鎖定/解鎖物理

        // 🔧 排定銷毀後,每幀重新確認「是不是真的還卡在避讓狀態」。
        //    這條的原意是清掉「救護車都走了還杵在避讓點的殭屍車」,
        //    但舊版註解寫「不再重複檢查狀態」→ 只要排定當下在避讓,5 秒後一律銷毀,
        //    完全不管這期間車子有沒有正常回歸主車道 → 好好在跑的車也被清掉。
        if (ambulanceGoneTimestamp > 0f)
        {
            bool stillStuckYielding = isYieldingForAmbulance || isParkedAtYieldNode;

            if (!stillStuckYielding)
            {
                // 已經自己回歸正常了 → 取消銷毀排程
                ambulanceGoneTimestamp = -1f;
            }
            else if (Time.time - ambulanceGoneTimestamp >= destroyAfterAmbulanceGoneDelay)
            {
                // 🔧 救護車已經不在了,先試著讓它自然回歸;真的還卡著才銷毀
                if (activeAmbulanceTransform == null && IsReturnPathClear())
                {
                    UnityEngine.Debug.Log($"<color=cyan>🚑 {name} 救護車已離場且主車道淨空,取消銷毀改為正常回歸</color>");
                    ambulanceGoneTimestamp = -1f;
                    isYieldingForAmbulance = false;
                    isParkedAtYieldNode = false;
                    isUsingOffsetFallback = false;
                    ambulancePassedTimer = 0f;
                    waitReturnTimer = 0f;
                    lastYieldReleaseTime = Time.time;
                    if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
                    {
                        currentWaypointNode.isOccupied = false;
                        currentWaypointNode.occupiedByVehicleID = 0;
                    }
                }
                else
                {
                    UnityEngine.Debug.Log($"<color=magenta>⚠️ {name} 救護車離場後仍卡在避讓狀態 {destroyAfterAmbulanceGoneDelay}秒，銷毀讓補車機制接手</color>");

                    if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
                    {
                        currentWaypointNode.isOccupied = false;
                        currentWaypointNode.occupiedByVehicleID = 0;
                    }

                    Destroy(gameObject);
                    return;
                }
            }
        }

        if (isStaticParked)
        {
            calculatedTargetSpeed = 0f;
            currentHealthSpeed = 0f;
            HoldParkedPose();   // 🪨 穩重如山:被撞也維持停放姿態

            // 🔶 側切預告是靜停期間唯一被允許動的東西。
            //    這個區塊會 return，所以下面那一輪的 ManageAutoIndicators / UpdateSideCutTelegraph
            //    靜停車根本走不到；而且原本這裡每幀強制關燈，預告的方向燈也會被壓掉。
            //    順序要在 HoldParkedPose() 之後 —— 它會把車釘回錨點，先挪會被拉回去。
            if (isTelegraphing)
            {
                // 🔴 煞車燈：預告前段踩著煞車(燈亮)、後段鬆開(燈滅)才開始蹭出去。
                //    「煞車燈熄滅」是真實世界判讀路邊車要不要出來最強的線索,補上這一段。
                ManageAutoIndicators(TelegraphBrakeOn);   // 走預告分支，打判斷出來那一邊的方向燈
                UpdateSideCutTelegraph();                 // 前輪轉出 ＋ 車身斜著探出
            }
            else
            {
                SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false);
            }
            return;
        }
        // 🟢 新增：精準過濾，只認得「真正算是車輛」的物件
        // 不管 Layer 裡混了什麼（紅綠燈號誌桿、感測器等），一律被忽略
        DetectOverlapCrash();
        HandleForwardDetection();

        // 🤝 切回主車道要禮讓主車道正在行駛的車:左側(主車道)有「還在動」的車 → 原地停等,
        //    不把車頭伸進去擋路,等它開過去、出現縫隙再切(逾時保底避免永遠等)
        isMergeHolding = false;
        if (returnBlinkTimer > 0f)
        {
            Vector3 mergeBoxCenter = transform.position + Vector3.up * 0.5f
                                     - transform.right * 2.2f - transform.forward * 2f;
            Collider[] laneCars = Physics.OverlapBox(mergeBoxCenter, new Vector3(1.8f, 1f, 9f),
                                                    transform.rotation, detectionLayer);
            foreach (var lc in laneCars)
            {
                if (IsMyOwnCollider(lc)) continue;
                var otherWhite = lc.GetComponentInParent<ReverseThreatAICar>();
                var otherGray = otherWhite == null ? lc.GetComponentInParent<CarNavigation>() : null;
                float otherSpd = otherWhite != null ? otherWhite.CurrentHealthSpeed
                               : (otherGray != null ? otherGray.RealTimeSpeed : -1f);
                // 🔧 這條的歷史:
                //    最早是「對方在動(>1m/s)」才讓 → 對方因為偵測到我而煞停時我就不讓 → 反向死鎖,
                //    所以改成 otherSpd >= 0f(主車道只要有車就讓,路權歸主車道)。
                //    但那個改法讓「對方已經停死了我還在空等」變成常態 —— 兩台一起停到 8 秒逾時。
                //
                //    現在主雷達已經會豁免「在隔壁回歸中」的車(見 HandleForwardDetection),
                //    主車道的車不會再因為我而煞停,所以可以安全地改回「看實際速度」,
                //    跟灰車 CarNavigation 的 mergeHold(oSpd > 1f)對齊:
                //    對方真的在跑 → 我讓;對方自己停著(紅燈/前方堵住/它也在等我)→ 不空等,照常切回,
                //    實際要不要煞由我自己的前方雷達與保險桿把關,不會撞上去。
                bool otherIsYielding = (otherWhite != null && otherWhite.IsYieldingForAmbulanceNow)
                                    || (otherGray != null && otherGray.IsYieldingForAmbulanceNow);
                if (!otherIsYielding && otherSpd > mergeHoldMinSpeed) { isMergeHolding = true; break; } // 主車道有車在動 → 我讓它先走
            }

            if (isMergeHolding)
            {
                mergeHoldTimer += Time.deltaTime;
                if (mergeHoldTimer > mergeHoldTimeout) isMergeHolding = false; // 逾時保底,免得永遠等不到
                else calculatedTargetSpeed = 0f;
            }
            else mergeHoldTimer = 0f;
        }
        else mergeHoldTimer = 0f;

        // 🚦 路口淨空:救護車要通過我前方的路口 → 我是橫向車,停在路口前等它離開。
        //    這是獨立於避讓系統的新增功能,不影響 isYieldingForAmbulance 的任何流程
        //    (避讓中的車在 HoldForAmbulanceAtJunction 就被排除,根本不會進來)。
        if (EvaluateJunctionHold()) calculatedTargetSpeed = 0f;

        if (!isReverseAction && !isForcedReversing && !isBrakingAction)
        {
            float rate = isRushingOut
                ? rushAccelerationRate
                : (calculatedTargetSpeed > currentHealthSpeed) ? accelerationRate : brakeHardness;
            currentHealthSpeed = Mathf.MoveTowards(currentHealthSpeed, calculatedTargetSpeed, rate * Time.deltaTime);
        }

        float realSpeed = (transform.position - previousPosition).magnitude / Time.deltaTime;
        actualSpeed = realSpeed;   // 供輪胎轉動使用，避免被閘門擋住時輪胎空轉
        bool isBraking = currentHealthSpeed < 0.5f || isWaitingAtTrafficLight || isBrakingAction || (calculatedTargetSpeed < realSpeed * 0.6f);
        previousPosition = transform.position;

        // 🤝 僵局破除:兩台車互相禮讓都停死時(切回主車道 vs 主車道來車),
        //    停太久就允許慢速蠕行脫困;實際位移仍由下方移動閘門與雷達保護,不會撞上去
        bool legitStop = isWaitingAtTrafficLight || isYieldingForAmbulance || isStaticParked
            || isBrakingAction || isReverseAction || isForcedReversing || isCarDetectedAtStop
            || isMergeHolding; // 🤝 禮讓主車道來車是合理停等,不算死鎖

        // 🚗 前方有車擋著 = 正常排隊跟車，不是死鎖。
        //    這種情況原本也吃 2 秒的死鎖判定，導致白車在路口排隊時每 2 秒就往前蠕行一次，
        //    看起來一直想擠上去，跟灰車安靜排隊的行為完全不同。
        //    這裡不是直接豁免（真的卡死還是要能脫困），而是把等待上限拉長。
        bool blockedByFrontVehicle = nearestForwardObstacleDistance < frontBlockedDistance;
        float stallLimit = blockedByFrontVehicle ? frontBlockedStallTimeout : 2f;

        if (!legitStop && realSpeed < 0.3f)
        {
            mutualStallTimer += Time.deltaTime;
            if (mutualStallTimer > stallLimit)
                currentHealthSpeed = Mathf.Max(currentHealthSpeed, 2f); // 蠕行脫困
        }
        else mutualStallTimer = 0f;

        HandleAccidentBypass();
        HandleNavigation();

        // 🔍 抽動診斷:單幀車頭轉太多就印出當下狀態,抓出是誰造成的
        if (debugSnapLog)
        {
            float headNow = transform.eulerAngles.y;
            string nodeNow = currentWaypointNode != null ? currentWaypointNode.name : "null";
            if (prevHeadingY > -900f)
            {
                float dHead = Mathf.Abs(Mathf.DeltaAngle(prevHeadingY, headNow));
                float dAim = (prevAimDir != Vector3.zero && lastAimDir != Vector3.zero)
                    ? Vector3.Angle(prevAimDir, lastAimDir) : -1f;
                if (dHead > 6f)
                    UnityEngine.Debug.Log($"<color=red>🔍抽動</color> {name} 單幀轉={dHead:F1}° 瞄準變={dAim:F1}° dt={Time.deltaTime * 1000f:F0}ms " +
                        $"| 相位={bypassPhase} | 偏移={navLateralOffset:F2}(前幀{prevNavOffset:F2}) | 節點={nodeNow}(前幀{prevNodeName}) " +
                        $"| 速度={currentHealthSpeed:F1}");
            }
            prevHeadingY = headNow; prevNodeName = nodeNow; prevNavOffset = navLateralOffset; prevAimDir = lastAimDir;
        }
        HandleIntersectionDeadlock();
        HandleAnywhereStuckDespawn();
        ManageAutoIndicators(isBraking);
        UpdateSideCutTelegraph();
        UpdatePostCutHorn();
        HandleAllWheelsRolling();
    }
    // ==================== 🚦 路口淨空:橫向車讓救護車先過(獨立系統,不動避讓邏輯) ====================

    [Header("🛡 避讓/回歸中的車:安全豁免下限")]
    [Tooltip("距離近於此值就絕不把對方當成「在隔壁的讓道車」而忽略,避免撞上去(公尺)")]
    public float besideIgnoreMinDistance = 6f;

    [Header("🚦 路口淨空 (橫向車讓救護車先過)")]
    [Tooltip("收到救護車的路口淨空訊號後,最多停等幾秒就強制放行(防止救護車在別處卡住導致路口永久凍結)")]
    public float junctionHoldTimeout = 9f;

    private float junctionHoldUntil = -999f;    // 訊號有效期(救護車每 0.5 秒重發一次)
    private float junctionHoldStart = -1f;      // 這一輪停等從何時開始,供逾時判定
    private Transform junctionHoldAmbulance = null;

    /// <summary>目前是否正因為救護車要通過路口而停等</summary>
    public bool IsHoldingForJunction { get; private set; }

    /// <summary>
    /// 🚦 由 AmbulanceAI 呼叫:「我要通過這個路口,橫向的你先停一下」。
    ///
    /// 完全獨立於避讓系統:避讓中的車在這裡就被排除,不會走到停等邏輯,
    /// 所以 isYieldingForAmbulance 那整套流程一行都不受影響。
    /// </summary>
    public void HoldForAmbulanceAtJunction(Transform ambulance)
    {
        if (isYieldingForAmbulance) return;   // 同向避讓中的車走避讓那套,不套用路口停等
        if (isStaticParked) return;           // 路邊等觸發的側切車本來就不動
        if (this is AmbulanceAI) return;      // 救護車自己不停

        junctionHoldUntil = Time.time + 0.8f; // 略長於廣播間隔,訊號斷了會自動失效
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
                UnityEngine.Debug.Log($"<color=orange>🚦 {name} 路口停等逾時 {junctionHoldTimeout} 秒,強制放行</color>");
            IsHoldingForJunction = false;
            return false;
        }

        // 已經開進路口了 → 不能停在路口中央,要趕快開出去
        if (isInIntersection)
        {
            IsHoldingForJunction = false;
            return false;
        }

        IsHoldingForJunction = true;
        return true;
    }

    private CarNavigation GetCarNavigationIfRelevant(Collider col)
    {
        if (IsMyOwnCollider(col)) return null; // 排除自己
        return col.GetComponentInParent<CarNavigation>();
    }
    void HandleNavigation()
    {
        if (isReverseAction || isForcedReversing || isBrakingAction) return;

        // 🚑 回歸左燈計時(移動由正常導航負責,這裡只控制燈)
        if (returnBlinkTimer > 0f)
        {
            returnBlinkTimer -= Time.deltaTime;
            if (returnBlinkTimer <= 0f) isReturningFromYield = false;
        }

        // ==========================================
        // 🚑 救護車離去與轉向判定 (安全放行機制)
        // ==========================================
        if (isYieldingForAmbulance)
        {
            isWaitingAtTrafficLight = false; // 🚑 避讓時每幀強制解除紅燈,停在紅燈的車也會讓道(不只靠觸發器)

            bool shouldReleaseYield = false;

            if (activeAmbulanceTransform != null)
            {
                Vector3 relativePos = transform.InverseTransformPoint(activeAmbulanceTransform.position);
                float distanceToAmbulance = Vector3.Distance(transform.position, activeAmbulanceTransform.position);

                
                // 🚑 只有救護車開到超過我車頭 ambulanceReturnAheadDistance 公尺才回歸(可調)
                if (relativePos.z > ambulanceReturnAheadDistance)
                {
                    ambulancePassedTimer += Time.deltaTime;
                    if (ambulancePassedTimer >= 0.8f)
                    {
                        shouldReleaseYield = true;
                    }
                }
                else
                {
                    ambulancePassedTimer = 0f;
                }
            }
            else
            {
                shouldReleaseYield = true; // 救護車意外消失直接解鎖
            }

            if (shouldReleaseYield)
            {
                // 🟢 回歸前確認主車道淨空；等太久就強制回歸（移動閘門＋雷達會保護不撞車）
                bool forceReturn = waitReturnTimer >= maxWaitReturnTime;

                if (!forceReturn && !IsReturnPathClear())
                {
                    waitReturnTimer += Time.deltaTime;
                    // 🔧 這裡原本有 ambulancePassedTimer = 0f,已移除。
                    //
                    //    救護車既然已經超過我車頭 ambulanceReturnAheadDistance 公尺,
                    //    就不會再退回來,沒有理由每次都重新確認 0.8 秒。
                    //    但歸零會讓下一幀 shouldReleaseYield 變回 false,
                    //    要再等 0.8 秒才會重新進到這個分支 →
                    //    waitReturnTimer 每 0.8 秒才累加一幀(約 0.016 秒),
                    //    maxWaitReturnTime = 6 秒實際上要跑近 5 分鐘才會觸發,
                    //    「等太久就強制回歸」那道保險等同於不存在,車就一直卡著。
                }
                else
                {
                    waitReturnTimer = 0f;
                    isYieldingForAmbulance = false;
                    ambulancePassedTimer = 0f;
                    lastYieldReleaseTime = Time.time;
                    activeAmbulanceTransform = null;
                    // 🔧 解鎖路段基準,下一輪避讓重新鎖定
                    yieldRoadDirLocked = Vector3.zero; yieldLateralOriginLocked = Vector3.zero;
                    yieldBaselineFrozen = false;

                    // 🧭 避讓期間往前開過頭 → 把已經在車身後方的節點先跳掉,回歸才不會往後折返
                    for (int skip = 0; skip < 4 && savedNormalWaypointNode != null; skip++)
                    {
                        Vector3 toSaved = savedNormalWaypointNode.transform.position - transform.position;
                        toSaved.y = 0f;
                        if (toSaved.sqrMagnitude < 0.01f || Vector3.Dot(transform.forward, toSaved.normalized) >= 0f) break;
                        TrafficPathNode nextAhead = savedNormalWaypointNode.nextChoices != null && savedNormalWaypointNode.nextChoices.Count > 0
                            ? savedNormalWaypointNode.nextChoices[0] : null;
                        if (nextAhead == null) break;
                        lastWaypointNode = savedNormalWaypointNode;
                        savedNormalWaypointNode = nextAhead;
                    }

                    // 🚑 回歸交給「正常導航」平滑開回車道(跟灰車一樣,不再自己算斜切 → 不抖)
                    if (savedNormalWaypointNode != null)
                    {
                        if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
                        {
                            currentWaypointNode.isOccupied = false;
                            currentWaypointNode.occupiedByVehicleID = 0;
                        }
                        currentWaypointNode = savedNormalWaypointNode;
                        savedNormalWaypointNode = null;
                    }
                    isUsingOffsetFallback = false;
                    isParkedAtYieldNode = false;
                    isReturningFromYield = true; // 只為了打左燈
                    returnBlinkTimer = 2.5f;
                    // 節點佔用歸還、savedNormalWaypointNode 還原,等回歸完成(斜切到主車道)後在乾淨路徑內做
                }
            }
        }

        if (currentWaypointNode == null) return;
        // 🟢 道路寬窄改由路徑節點的 isNarrowRoad 決定，不再依賴 WideRoad Trigger
        // 🔧 救護車避讓期間,黃色網狀線節點一律視為寬路。
        //    依據:救護車只在寬路節點生成(AmbulanceSpawner:114)、選路時有寬路就絕不進窄路
        //    (AmbulanceAI:530)、真被逼進窄路也會直接收工銷毀(AmbulanceAI:227)。
        //    所以場上有救護車在運作時它必定在寬路 —— 避讓中遇到的黃網節點必然是
        //    寬路上的網狀線,那個 isNarrowRoad 標記在此情境下沒有意義。
        //    (場景 85 個黃網節點有 63 個被勾成 isNarrowRoad,會讓避讓在網狀線上根本無法啟動)
        isInWideRoad = !currentWaypointNode.isNarrowRoad
                       || (isYieldingForAmbulance && currentWaypointNode.isInsideYellowGrid);

        // 🚑 避讓中、救護車還在附近 → 停在原地等,不要轉彎進它的路徑
        if (isYieldingForAmbulance && activeAmbulanceTransform != null && !isBezierActive)
        {
            float dAmbT = Vector3.Distance(transform.position, activeAmbulanceTransform.position);
            if (dAmbT < ambulanceReturnAheadDistance)
            {
                TrafficPathNode nx = PeekPlannedNextNode();
                if (nx != null && lastWaypointNode != null && currentWaypointNode != null)
                {
                    Vector3 dA = currentWaypointNode.transform.position - lastWaypointNode.transform.position; dA.y = 0f;
                    Vector3 dB = nx.transform.position - currentWaypointNode.transform.position; dB.y = 0f;
                    if (dA.sqrMagnitude > 0.01f && dB.sqrMagnitude > 0.01f && Vector3.Angle(dA, dB) > 15f)
                    {
                        currentHealthSpeed = Mathf.Lerp(currentHealthSpeed, 0f, Time.deltaTime * 5f);
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(dA.normalized, Vector3.up), Time.deltaTime * alignSmoothSpeed);
                        return;   // 停等,不轉彎
                    }
                }
            }
        }

        // 🔵 貝茲曲線觸發判斷：只有在還沒啟動、且不是避讓/倒車狀態時才檢查
        if (!isBezierActive && !isYieldingForAmbulance)
        {
            float distToCurrentNode = Vector3.Distance(transform.position, currentWaypointNode.transform.position);
            // 🟢 節點有設定專屬觸發距離就用節點的，沒設定才用車輛預設值
            float activeTriggerDist = currentWaypointNode.bezierTriggerOverride > 0f
                ? currentWaypointNode.bezierTriggerOverride
                : bezierTriggerDistance;
            if (distToCurrentNode < activeTriggerDist)
            {
                TrafficPathNode nextNode = PeekPlannedNextNode(); // 🟢 改用預抽點，跟實際換點一致
                if (nextNode != null)
                {
                    // 🟢 改用「上一節點→目前節點」的路徑段方向，跟 CarNavigation 一致，不受車身偏移影響
                    Vector3 dirToTarget = lastWaypointNode != null
                        ? (currentWaypointNode.transform.position - lastWaypointNode.transform.position).normalized
                        : (currentWaypointNode.transform.position - transform.position).normalized;
                    Vector3 dirNext = (nextNode.transform.position - currentWaypointNode.transform.position).normalized;
                    float turnAngle = Vector3.Angle(dirToTarget, dirNext);

                    if (turnAngle > bezierTurnAngleThreshold)
                    {
                        float distNodeToNext = Vector3.Distance(currentWaypointNode.transform.position, nextNode.transform.position);
                        if (distNodeToNext < mergeNodeDistanceThreshold)
                        {
                            // 跨節點大轉彎：P0=現在位置, P1=currentWaypointNode, P2=nextNode
                            StartBezierTurn(transform.position, currentWaypointNode.transform.position, nextNode.transform.position, nextNode);
                        }
                        else
                        {
                            // 距離太遠，用短程貝茲（P2 取中點）
                            Vector3 midPoint = (currentWaypointNode.transform.position + nextNode.transform.position) / 2f;
                            StartBezierTurn(transform.position, currentWaypointNode.transform.position, midPoint, null);
                        }
                    }
                }
            }
        }

        Vector3 targetPos = transform.position;

        // ==========================================
        // 🛣️ 【寬路雙軌制避讓】決策與目標點鎖定
        // ==========================================
        if (isYieldingForAmbulance && isInWideRoad && !isInIntersection)
        {
            bool safeToCommitLateralYield = nearestForwardObstacleDistance > minSafeGapToLateralYield;

            if (savedNormalWaypointNode == null && safeToCommitLateralYield)
            {
                if (useDynamicOffsetOnly)
                {
                    // 🚑 一律用動態偏移,不去避讓點(避讓點程式碼保留,設 false 可切回)
                    savedNormalWaypointNode = currentWaypointNode;
                    isUsingOffsetFallback = true;
                    isParkedAtYieldNode = false;
                    // 🔧 新一輪避讓:重置側移卡住偵測與路段基準,不沿用上一輪的殘留狀態
                    yieldStallTimer = 0f; lastYieldLateral = -999f; lastYieldTargetSeen = -999f; ambBlockedTimer = 0f;
                    smoothedYieldTarget = -999f; yieldFrontBlocked = false; yieldFrontStateTimer = 0f;
                    yieldRoadDirLocked = Vector3.zero; yieldLateralOriginLocked = Vector3.zero;
                    yieldBaselineFrozen = false;
                }
                else if (currentWaypointNode.yieldNode != null && !currentWaypointNode.yieldNode.isOccupied)
                {
                    // 🟢 新增：邏輯旗標沒佔用，還要再用物理檢查確認該位置實際上真的是空的
                    Collider[] yieldSpotCheck = Physics.OverlapSphere(
                        currentWaypointNode.yieldNode.transform.position,
                        2.0f, // 依車身大小微調的檢查半徑
                        detectionLayer);

                    bool yieldSpotReallyClear = true;
                    foreach (var c in yieldSpotCheck)
                    {
                        if (!IsMyOwnCollider(c))
                        {
                            yieldSpotReallyClear = false;
                            break;
                        }
                    }

                    if (yieldSpotReallyClear)
                    {
                        savedNormalWaypointNode = currentWaypointNode;
                        currentWaypointNode = currentWaypointNode.yieldNode;
                        currentWaypointNode.isOccupied = true;
                        currentWaypointNode.occupiedByVehicleID = this.gameObject.GetInstanceID();
                        isUsingOffsetFallback = false;
                        isParkedAtYieldNode = true;
                    }
                    else
                    {
                        // 物理上真的有東西擋著，即使邏輯旗標顯示未佔用，也改用動態偏移備援
                        savedNormalWaypointNode = currentWaypointNode;
                        isUsingOffsetFallback = true;
                        isParkedAtYieldNode = false;
                    }
                }
                else
                {
                    savedNormalWaypointNode = currentWaypointNode;
                    isUsingOffsetFallback = true;
                    isParkedAtYieldNode = false;
                }

            }
            else if (currentWaypointNode.isOccupied && currentWaypointNode.occupiedByVehicleID != this.gameObject.GetInstanceID())
            {
                isUsingOffsetFallback = true;
                isParkedAtYieldNode = false; // 🟢 新增：改用動態偏移，不再算死等在避讓點
            }
            // 還不安全時什麼都不做：targetPos 會落到下面 else 分支，
            // 也就是照原車道正常前進，速度則靠 HandleForwardDetection 對前車的減速邏輯自然放慢
        }

        // 🚑 避讓靠邊的「乾淨專用移動」:只管靠邊(斜切→到位停穩轉正)。
        //    回歸不在這裡做——放行時直接交回正常導航,像灰車那樣自然開回車道,才不會抖。
        // 🔧 「已經偏出去了就必須維持」——比照灰車 CarNavigation 的作法。
        //    黃色網狀線都畫在路口,車一進 Intersection 觸發區 isInIntersection 就變 true,
        //    舊版硬性排除路口 → 靠邊邏輯整段被跳過 → 目標點變回主車道節點
        //    → 車子斜切回車道停住,擋住還沒過去的救護車。
        //    路口限制只該擋「開始偏移」,不該把已經讓好的車拉回車道。
        // 🔧 執行階段不再檢查 isInWideRoad / isInIntersection。
        //    這兩個旗標每幀由 currentWaypointNode 重算(isInWideRoad 見下方 917 行),
        //    車子在避讓中往前推進、或路口路段轉 90° 時都會翻掉,
        //    一翻掉整段靠邊邏輯就被跳過 → 落到正常導航 → 車子開向下一個路徑節點
        //    (黃網節點就是這樣被當成目標的)。
        //
        //    「要不要開始靠邊」由下面的決策區塊把關(那裡有 isInWideRoad && !isInIntersection),
        //    一旦承諾就會鎖定 savedNormalWaypointNode,而它只在正式回歸時才清空 ——
        //    所以 savedNormalWaypointNode != null 本身就是最可靠的「避讓進行中」旗標。
        if (isYieldingForAmbulance && savedNormalWaypointNode != null
            && lastWaypointNode != null && !isBezierActive)
        {
            // 🔧 鎖定路段基準:整段靠邊過程用同一組方向與量測原點。
            //
            //    舊版每幀從 lastWaypointNode 重算方向,有兩個致命問題:
            //    (a) 節點推進時基準點跳動 → yLateral 忽大忽小
            //    (b) 進路口時路段轉 90° → yRoadRight 跟著轉 → 已經讓好的偏移量瞬間失效
            //    再加上「與車頭反向就翻正」的防呆,會形成回授迴圈:
            //    車頭偏過頭 → 方向翻轉 → yLateral 變號 → 以為還要往右 → 越轉越右。
            if (yieldRoadDirLocked.sqrMagnitude < 0.01f)
            {
                Vector3 d0 = (currentWaypointNode != null && currentWaypointNode != lastWaypointNode)
                    ? currentWaypointNode.transform.position - lastWaypointNode.transform.position
                    : savedNormalWaypointNode.transform.position - lastWaypointNode.transform.position;
                d0.y = 0f;
                if (d0.sqrMagnitude < 0.01f) d0 = transform.forward;
                yieldRoadDirLocked = d0.normalized;
                yieldLateralOriginLocked = lastWaypointNode.transform.position;
            }
            else if (currentWaypointNode != null && currentWaypointNode != lastWaypointNode
                     && !(yieldFreezeBaselineWhileSliding && yieldBaselineFrozen))
            {
                // 緩彎追蹤:新路段方向與鎖定值差 30° 以內才更新,擋掉路口的 90° 轉向。
                // 🛞 已經開始側移就不再進來 —— 換了原點等於把「讓出去多少」重算,
                //    彎道上那個值會縮水,車就永遠讓不到位。
                Vector3 dNew = currentWaypointNode.transform.position - lastWaypointNode.transform.position;
                dNew.y = 0f;
                if (dNew.sqrMagnitude > 0.01f && Vector3.Angle(yieldRoadDirLocked, dNew.normalized) < 30f)
                {
                    yieldRoadDirLocked = dNew.normalized;
                    yieldLateralOriginLocked = lastWaypointNode.transform.position;
                }
            }

            Vector3 yRoadDir = yieldRoadDirLocked;
            if (yRoadDir.sqrMagnitude > 0.01f)
            {
                Vector3 yRoadRight = Vector3.Cross(Vector3.up, yRoadDir);
                Vector3 yToCar = transform.position - yieldLateralOriginLocked; yToCar.y = 0f;
                float yLateral = Vector3.Dot(yToCar, yRoadRight);

                float yTarget = ambulanceEvadeOffset;
                if (activeAmbulanceTransform != null)
                {
                    Vector3 toAmb = activeAmbulanceTransform.position - transform.position; toAmb.y = 0f;
                    bool behindClose = Vector3.Dot(yRoadDir, toAmb.normalized) < 0.3f && toAmb.magnitude < 18f;

                    // 🚑 只有救護車「真的停住」才算被我卡住(它在路口只是減速觀察 → 不需要二次偏移)
                    var ambAI = activeAmbulanceTransform.GetComponentInParent<ReverseThreatAICar>();
                    float ambSpd = ambAI != null ? ambAI.CurrentHealthSpeed : 0f;
                    // 🚑 優先用救護車自己回報的「被擋住」訊號;速度判斷留著當備援
                    AmbulanceAI ambulanceAI = ambAI as AmbulanceAI;
                    bool ambStuck = (ambulanceAI != null && ambulanceAI.IsBlockedByTraffic)
                                    || ambSpd < ambulanceStuckSpeed;
                    if (behindClose && ambStuck) ambBlockedTimer += Time.deltaTime;
                    else ambBlockedTimer = 0f;

                    if (ambBlockedTimer > ambulanceStuckTime)
                        yTarget = ambulanceEvadeOffsetMax; // 確定卡住了 → 二次偏移讓更多
                }
                // 🚗 右側有車(路邊側切車等)→ 靠邊目標縮到剛好不撞
                if (Physics.Raycast(transform.position + Vector3.up * 0.5f, yRoadRight, out RaycastHit rBlock, yTarget + 1f, detectionLayer, QueryTriggerInteraction.Ignore)
                    && !IsMyOwnCollider(rBlock.collider))
                    yTarget = Mathf.Max(0f, rBlock.distance - 1.2f);

                // 🚗 斜前方(右前 45°)也要看:前面一點的路邊車會被斜切撞到
                Vector3 diagDir = (yRoadDir + yRoadRight).normalized;
                bool playerAtSide = false;
                if (Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.7f, diagDir,
                        out RaycastHit dBlock, yTarget + 3f, detectionLayer, QueryTriggerInteraction.Ignore)
                    && !IsMyOwnCollider(dBlock.collider))
                {
                    yTarget = Mathf.Min(yTarget, Mathf.Max(0f, dBlock.distance * 0.6f - 0.8f));
                    if (IsPlayerCollider(dBlock.collider)) playerAtSide = true;
                }
                if (!playerAtSide
                    && Physics.Raycast(transform.position + Vector3.up * 0.5f, yRoadRight, out RaycastHit pSide, yTarget + 1.5f, detectionLayer, QueryTriggerInteraction.Ignore)
                    && IsPlayerCollider(pSide.collider))
                    playerAtSide = true;

                // 🔊 擋住我讓道的是玩家 → 先按喇叭催,一直不讓就淡出消失(免得永遠讓不了道)
                if (playerAtSide)
                {
                    playerBlockYieldTimer += Time.deltaTime;
                    if (playerBlockYieldTimer > playerBlockHonkDelay && Time.time >= nextYieldHonkTime && !isHonking)
                    {
                        TriggerHorn();
                        nextYieldHonkTime = Time.time + 3f;
                    }
                    if (playerBlockYieldTimer > playerBlockDespawnTime)
                    {
                        UnityEngine.Debug.Log($"<color=magenta>🚗 {name} 讓道時被玩家擋住超過 {playerBlockDespawnTime}s,淡出退場</color>");
                        ForceFadeOutClear();
                        return;
                    }
                }
                else playerBlockYieldTimer = 0f;

                // ---- 🔧 抗擺盪 (1/2):把「靠邊目標」平滑掉 ----
                // yTarget 是每幀由 Raycast 重算的,旁邊車道有車經過時會塌下去、車走了又彈回來,
                // 下面 `yLateral < yTarget - 0.2f` 跟著翻,車就在「斜切」與「停等轉正」之間來回。
                // 縮小(旁邊突然有車)要快,才不會撞上去;放大(空間變大了)才慢慢來。
                float rawYieldTarget = yTarget;   // 未平滑的原始值 —— 二次偏移的解鎖判定必須用它
                if (yieldTargetSmoothSpeed > 0f)
                {
                    if (smoothedYieldTarget < -900f) smoothedYieldTarget = yTarget;
                    float yRate = (yTarget < smoothedYieldTarget) ? yieldTargetSmoothSpeed * 5f : yieldTargetSmoothSpeed;
                    smoothedYieldTarget = Mathf.MoveTowards(smoothedYieldTarget, yTarget, yRate * Time.deltaTime);
                    yTarget = smoothedYieldTarget;
                }

                // 側移進度卡住(被旁車擋住開不動)→ 視為「已到位」,直接轉正,絕不永遠斜著
                // 🔧 改用「橫向速度」判斷。原本是每幀位移 < 0.03m 就算卡住,60fps 下等於
                //    要求 1.8 m/s 的側移速度,但正常斜切只有 ~1.3 m/s → 整段斜切都被誤判成
                //    卡住,1 秒後就放棄 → 偏移只做了一小段、二次偏移也永遠進不去。
                float lateralSpeed = (lastYieldLateral > -900f && Time.deltaTime > 0.0001f)
                    ? Mathf.Abs(yLateral - lastYieldLateral) / Time.deltaTime
                    : 999f;
                if (lateralSpeed < 0.15f)
                    yieldStallTimer += Time.deltaTime;
                else
                    yieldStallTimer = 0f;
                lastYieldLateral = yLateral;

                // 🔧 二次偏移把目標加大 → 解除「已到位」鎖定,讓車能再往外靠
                // ⚠️ 這裡一定要用 rawYieldTarget:平滑後的 yTarget 每幀只爬 0.02 左右,
                //    永遠跨不過 0.1 的門檻,二次偏移就會失效。
                if (lastYieldTargetSeen > -900f && rawYieldTarget > lastYieldTargetSeen + 0.1f)
                    yieldStallTimer = 0f;
                lastYieldTargetSeen = rawYieldTarget;

                bool yieldStalled = yieldStallTimer > 1.0f;

                // 只准往右追目標,偏多了就地停等(不往回蹭)
                if (yLateral < yTarget - 0.2f && !yieldStalled)
                {
                    Vector3 aimPoint = transform.position + yRoadDir * 8f + yRoadRight * (yTarget - yLateral);
                    Vector3 aimDir = (aimPoint - transform.position).normalized;

                    // 前方有車(救護車還沒過去等)→ 原地轉正等,不往前擠
                    // ---- 🔧 抗擺盪 (2/2):這個判斷加一道不對稱遲滯 ----
                    // aimDir 是斜右前方,多車道上會不停掃到隔壁車道的車。判斷一翻,
                    // 車頭就在斜右(aimDir)與轉正(yRoadDir)之間來回擺。
                    // 偵測到阻擋要連續成立一小段才停(短,免得撞上前車);
                    // 淨空要連續成立比較久才恢復斜切(長,用來吃掉抖動)。
                    bool frontHitNow = Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.8f, aimDir,
                            out RaycastHit fHit, 5.5f, detectionLayer, QueryTriggerInteraction.Ignore)
                        && !IsMyOwnCollider(fHit.collider);

                    if (frontHitNow == yieldFrontBlocked) yieldFrontStateTimer = 0f;
                    else
                    {
                        yieldFrontStateTimer += Time.deltaTime;
                        float needHold = frontHitNow ? yieldFrontBlockEngageTime : yieldFrontBlockReleaseTime;
                        if (yieldFrontStateTimer >= needHold) { yieldFrontBlocked = frontHitNow; yieldFrontStateTimer = 0f; }
                    }

                    if (yieldFrontBlocked)
                    {
                        // 🔧 等前車讓開屬於合理等待,不算「側移卡住」,前方清空後要能繼續靠邊
                        yieldStallTimer = 0f;
                        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(yRoadDir, Vector3.up), Time.deltaTime * alignSmoothSpeed);
                        currentHealthSpeed = 0f;
                        return;
                    }

                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(aimDir, Vector3.up), Time.deltaTime * rotationSpeed);
                    yieldBaselineFrozen = true;   // 🛞 從這一刻起量測基準固定,彎道上才不會縮水
                    currentHealthSpeed = Mathf.Min(originalNormalSpeed * 0.4f, 9f);
                    transform.position += transform.forward * currentHealthSpeed * Time.deltaTime;
                    return;
                }
                else
                {
                    // 靠邊到位:停穩轉正
                    Quaternion alignRot = Quaternion.LookRotation(yRoadDir, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, alignRot, Time.deltaTime * alignSmoothSpeed);
                    // 🔧 幾乎轉正就直接對齊:Slerp 是漸近的,永遠差一點點,看起來就是微斜
                    if (Quaternion.Angle(transform.rotation, alignRot) < 1.5f)
                        transform.rotation = alignRot;
                    currentHealthSpeed = 0f;
                    return;
                }
            }
        }

        Vector3 direction;
        float distanceToTarget;
        Vector3 dirToTargetNormalize;
        float angleToTarget;

        if (isBezierActive)
        {
            // 🚑 轉彎進行中,但救護車正在附近(橫向/對向通過)→ 凍結在原地,不要轉進它的路徑
            if (isYieldingForAmbulance && activeAmbulanceTransform != null)
            {
                Vector3 toAmbB = activeAmbulanceTransform.position - transform.position; toAmbB.y = 0f;
                if (toAmbB.magnitude < ambulanceReturnAheadDistance)
                {
                    Vector3 ambFwdB = activeAmbulanceTransform.forward; ambFwdB.y = 0f;
                    bool behindB = Vector3.Dot(transform.forward, toAmbB.normalized) < -0.3f;
                    bool sameDirB = ambFwdB.sqrMagnitude > 0.01f
                                    && Vector3.Dot(transform.forward, ambFwdB.normalized) > 0.5f;
                    if (!(behindB && sameDirB))   // 不是「在我後方且同向」→ 它會橫切我的路徑
                    {
                        if (!isInIntersection)
                        {
                            currentHealthSpeed = Mathf.Lerp(currentHealthSpeed, 0f, Time.deltaTime * 5f);

                            // 車頭轉回原本車道方向,不要停在「指著救護車」的角度上擋路
                            if (lastWaypointNode != null && currentWaypointNode != null)
                            {
                                Vector3 backDir = currentWaypointNode.transform.position - lastWaypointNode.transform.position;
                                backDir.y = 0f;
                                if (backDir.sqrMagnitude > 0.01f)
                                    transform.rotation = Quaternion.Slerp(transform.rotation,
                                        Quaternion.LookRotation(backDir.normalized, Vector3.up),
                                        Time.deltaTime * alignSmoothSpeed);
                            }
                            return;   // 還沒進路口 → 停住別轉進去
                        }
                        // 已經在路口裡 → 照原本速度把彎轉完離開(不加速,避免看起來像衝出去)
                    }
                }
            }

            // 🔵 貝茲模式：目標點是曲線上的下一個取樣點，而不是路點本身
            float speedForBezier = currentHealthSpeed;
            bezierT += (speedForBezier * Time.deltaTime) / 15f; // 15f 是曲線長度基準，可依車速微調
            Vector3 bezierNextPos = GetBezierPoint(bezierP0, bezierP1, bezierP2, bezierT);

            targetPos = bezierNextPos;
            targetPos.y = transform.position.y;
            direction = targetPos - transform.position;
            distanceToTarget = Vector3.Distance(transform.position, targetPos);
            dirToTargetNormalize = direction.normalized;
            angleToTarget = Vector3.Angle(transform.forward, dirToTargetNormalize);
        }
        else
        {
            // 🔧 同上:執行階段用 isUsingOffsetFallback(承諾靠邊時設 true,回歸時設 false)當旗標,
            //    不再檢查每幀重算的 isInWideRoad / isInIntersection,
            //    否則推進到黃網節點或進入路口時瞄準點會被拉回路徑上。
            if (isYieldingForAmbulance && isUsingOffsetFallback)
            {
                // 🚑 用「路段方向」算靠邊點(節點自身旋轉常常沒對準路,會讓車斜著開)
                // 🔧 改用 currentWaypointNode 當參考:savedNormalWaypointNode 在避讓中節點前進後
                //    會落到車的後方,舊算法會把路的方向算成反向 → 車頭轉向救護車。
                Transform referenceNode = currentWaypointNode != null ? currentWaypointNode.transform
                    : (savedNormalWaypointNode != null ? savedNormalWaypointNode.transform : null);
                Vector3 fbDir;
                if (referenceNode != null && lastWaypointNode != null && referenceNode != lastWaypointNode.transform)
                    fbDir = referenceNode.position - lastWaypointNode.transform.position;
                else if (referenceNode != null)
                    fbDir = referenceNode.forward;
                else
                    fbDir = transform.forward;
                fbDir.y = 0f;
                if (fbDir.sqrMagnitude < 0.01f) fbDir = transform.forward;
                // 🔧 防呆:方向與車頭相反一律翻正,絕不把目標點放到車的後方
                if (Vector3.Dot(fbDir.normalized, transform.forward) < 0f) fbDir = -fbDir;
                fbDir.Normalize();
                Vector3 fbRight = Vector3.Cross(Vector3.up, fbDir);

                // 已經靠到位 → 目標改成「正前方沿路的點」,車頭自然轉正(不再一直斜著追側邊的點)
                Vector3 fbToCar = transform.position - (lastWaypointNode != null
                    ? lastWaypointNode.transform.position : referenceNode.position);
                fbToCar.y = 0f;
                float fbLateral = Vector3.Dot(fbToCar, fbRight);

                if (fbLateral >= ambulanceEvadeOffset * 0.8f)
                    targetPos = transform.position + fbDir * 12f + fbRight * fbLateral;   // 轉正、平行路邊直行
                else
                    targetPos = transform.position + fbDir * 10f + fbRight * ambulanceEvadeOffset; // 還沒到位 → 斜切靠邊
            }
            else
            {
                targetPos = currentWaypointNode.transform.position;

                // 🚗 側邊衝出:把目標點往玩家所在的橫向位置靠,讓車真的切進玩家的行進路線。
                //    只瞄準自己的路網節點時,車會停在原本車道上,看起來只是「併入隔壁車道」,
                //    大腦會判定成正常車流,危險感就消失了。
                if (isRushingOut && rushAimAtPlayerLane && rushPlayerTf != null && !(this is AmbulanceAI))
                {
                    Vector3 rSegDir = (lastWaypointNode != null)
                        ? (currentWaypointNode.transform.position - lastWaypointNode.transform.position)
                        : (currentWaypointNode.transform.position - transform.position);
                    rSegDir.y = 0f;

                    if (rSegDir.sqrMagnitude > 0.01f)
                    {
                        rSegDir.Normalize();
                        Vector3 rSegRight = Vector3.Cross(Vector3.up, rSegDir);

                        // 玩家相對於節點的橫向距離,正值代表在路線右側
                        Vector3 nodeToPlayer = rushPlayerTf.position - currentWaypointNode.transform.position;
                        nodeToPlayer.y = 0f;
                        float playerLateral = Vector3.Dot(nodeToPlayer, rSegRight);

                        float shift = Mathf.Clamp(playerLateral, -rushMaxLateralShift, rushMaxLateralShift);
                        targetPos = currentWaypointNode.transform.position + rSegRight * shift;
                    }
                }
                // 🚑 繞行:瞄準「自己前方一小段(沿路方向)+ 側偏」,立刻產生明顯側移繞過去
                //    (瞄遠處節點的話,偏移要開到很近才生效,一近就被前向閘門擋住,來不及)
                // 🚑 救護車:用原本那套(節點平行點 + 近距離往前延伸),不套用繞行的新演算法
                else if (this is AmbulanceAI && navLateralOffset != 0f)
                {
                    Vector3 aSegDir = (lastWaypointNode != null)
                        ? (currentWaypointNode.transform.position - lastWaypointNode.transform.position)
                        : (currentWaypointNode.transform.position - transform.position);
                    aSegDir.y = 0f;
                    if (aSegDir.sqrMagnitude > 0.01f)
                    {
                        aSegDir.Normalize();
                        Vector3 aSegRight = Vector3.Cross(Vector3.up, aSegDir);
                        targetPos = currentWaypointNode.transform.position + aSegRight * navLateralOffset;

                        float aDist = Vector3.Distance(transform.position, targetPos);
                        float aFade = Mathf.Clamp01(Mathf.Abs(navLateralOffset) / 0.6f);
                        if (aDist < 12f) targetPos += aSegDir * ((12f - aDist) * aFade);
                    }
                }
                else if (accidentLateralOffset != 0f || pathAimHoldTimer > 0f)
                {
                    // 🎯 沿路徑滑動的前瞻點(pure-pursuit):不再把瞄準點釘在節點上,
                    //    所以換節點的那一幀目標是連續的,車頭不會抽一下
                    // ⚠️ 基準線一定要用「路網的兩個節點」,不能用車子自己的位置當起點,
                    //    否則偏移會以車為基準每幀累加,變成無止盡往左飄
                    Vector3 bPos = currentWaypointNode.transform.position;
                    Vector3 segDir;
                    Vector3 aPos;
                    if (lastWaypointNode != null)
                    {
                        aPos = lastWaypointNode.transform.position;
                        segDir = bPos - aPos; segDir.y = 0f;
                    }
                    else
                    {
                        // 沒有上一節點:用「節點→下一節點」當路方向,基準線仍然通過節點,不通過車
                        TrafficPathNode n1 = PeekPlannedNextNode();
                        segDir = n1 != null ? (n1.transform.position - bPos) : transform.forward;
                        segDir.y = 0f;
                        if (segDir.sqrMagnitude < 0.01f) segDir = transform.forward;
                        aPos = bPos - segDir.normalized * 20f;
                    }
                    if (segDir.sqrMagnitude < 0.01f) segDir = transform.forward;
                    segDir.Normalize();

                    float lookAhead = bypassLookAhead;
                    Vector3 flatCar = new Vector3(transform.position.x, 0f, transform.position.z);
                    Vector3 flatA = new Vector3(aPos.x, 0f, aPos.z);
                    Vector3 flatB = new Vector3(bPos.x, 0f, bPos.z);
                    float along = Vector3.Dot(flatCar - flatA, segDir);       // 車在本段上的投影位置
                    float distToEnd = Vector3.Distance(flatA, flatB) - along; // 距離本段終點還多遠

                    // 下一段方向(用來平滑過渡,不能瞬間切換,否則路一彎偏移點就橫向瞬移)
                    TrafficPathNode nxt = PeekPlannedNextNode();
                    Vector3 dir2 = nxt != null ? (nxt.transform.position - bPos) : Vector3.zero;
                    dir2.y = 0f;
                    bool hasDir2 = dir2.sqrMagnitude > 0.01f;
                    if (hasDir2) dir2.Normalize();
                    // 🛡️ 防呆1:下一段若是往回走(路口分支),不拿它延伸,否則前瞻點會跑到車子後面
                    if (hasDir2 && Vector3.Dot(segDir, dir2) < 0.1f) hasDir2 = false;

                    Vector3 aimPt;
                    if (distToEnd >= lookAhead)
                        aimPt = aPos + segDir * (along + lookAhead);
                    else
                    {
                        float remain = lookAhead - Mathf.Max(distToEnd, 0f);
                        aimPt = hasDir2 ? bPos + dir2 * remain : bPos + segDir * remain;
                    }

                    // 側偏方向:兩段之間依「前瞻點已跨過多少」平滑內插 → 過節點時完全連續,不抽
                    Vector3 rightA = Vector3.Cross(Vector3.up, segDir);
                    Vector3 rightB = hasDir2 ? Vector3.Cross(Vector3.up, dir2) : rightA;
                    float blend = distToEnd >= lookAhead ? 0f : Mathf.Clamp01((lookAhead - distToEnd) / lookAhead);
                    Vector3 aimRight = Vector3.Slerp(rightA, rightB, blend).normalized;

                    // 🎯 限制「橫向追趕量」:瞄準點只比車目前的橫向位置多偏一點點,
                    //    接近角永遠很小 → 不會為了追偏移線而轉過頭再修正回來(消除過衝甩頭)
                    Vector3 carFromLine = transform.position - aPos; carFromLine.y = 0f;
                    float actualLateral = Vector3.Dot(carFromLine, aimRight);
                    float lateralError = accidentLateralOffset - actualLateral;
                    // 死區:偏差很小就不修正,避免「已經幾乎正了還硬拉回中線」的不自然抽動
                    if (Mathf.Abs(lateralError) < laneCenterDeadzone) lateralError = 0f;
                    float aimLateral = actualLateral + Mathf.Clamp(lateralError, -bypassMaxApproach, bypassMaxApproach);

                    targetPos = aimPt + aimRight * aimLateral;

                    // 🛡️ 防呆2:算出來的瞄準點若跑到車子側後方,一律改用「正前方」的偏移點,
                    //    避免車頭大幅甩動(這就是 Log 看到瞄準變 179° 的來源)
                    Vector3 toAim = targetPos - transform.position; toAim.y = 0f;
                    if (Vector3.Dot(transform.forward, toAim) < 2f)
                        targetPos = bPos + segDir * lookAhead + aimRight * accidentLateralOffset; // 仍以節點為基準,不以車為基準
                }
            }

            targetPos.y = transform.position.y;
            direction = targetPos - transform.position;
            distanceToTarget = Vector3.Distance(transform.position, targetPos);
            dirToTargetNormalize = direction.normalized;
            angleToTarget = Vector3.Angle(transform.forward, dirToTargetNormalize);
        }

        // 🚨 【安全卡死脫困】正常行駛下若開過頭(角度 > 80)，立刻換下一個點，防自轉
        // 🟢 新增：排除剛結束倒車/被拖著走的情況，避免倒車後車頭角度暫時異常就被誤判成"開過頭"而跳點
        bool recentlyReversed = isForcedReversing || isReverseAction;
        // 🟢 修正誤判跳點：夾角大不代表開過頭（轉彎/避讓中車頭本來就會偏），
        // 必須「節點確實已經在我身後」才允許跳點脫困
        bool nodeIsBehindMe = transform.InverseTransformPoint(targetPos).z < 0f;
        // 🚧 側向偏移中(繞故障車/救護車讓道)經過節點時,距離近+角度大是正常的,
        //    不能當成「開過頭」跳點,否則目標瞬間換掉、速度砍半,車頭會抽一下
        bool lateralOffsetActive = Mathf.Abs(accidentLateralOffset) > 0.1f;
        if (!isYieldingForAmbulance && !recentlyReversed && !isBezierActive && !lateralOffsetActive
            && distanceToTarget < 4.0f && angleToTarget > 80f && nodeIsBehindMe)
        {
            currentHealthSpeed = Mathf.Max(currentHealthSpeed * 0.5f, 2f);
            GotoNextWaypoint();
            return;
        }

        // 🚧 側向偏移中:節點被開過去了就「安靜地」換下一個(不砍速度、不 return),避免抽動
        if (lateralOffsetActive && !isBezierActive && nodeIsBehindMe && distanceToTarget < 6.0f)
        {
            GotoNextWaypoint();
            if (currentWaypointNode == null) return;
        }

        // 🚨 【安全卡死脫困】寬路避讓模式下若開過頭，直接就地平移煞停，絕對不繞圈
        if (isYieldingForAmbulance && isInWideRoad && distanceToTarget < 4.0f && angleToTarget > 80f && !isBezierActive)
        {
            currentHealthSpeed = Mathf.Lerp(currentHealthSpeed, 0f, Time.deltaTime * 5f);
            currentSteerAngle = 0f;
            return;
        }

        // 轉向控制
        float dynamicRotationSpeed = rotationSpeed;
        // 🚧 變換車道(繞故障車)期間轉向柔化,車頭不會急轉(救護車除外,牠要保持原本的靈活轉向)
        if (!(this is AmbulanceAI) && Mathf.Abs(accidentLateralOffset) > 0.05f) dynamicRotationSpeed *= bypassTurnSoftness;
        //if (angleToTarget > 20f) dynamicRotationSpeed = rotationSpeed * 2.5f;

        if (direction != Vector3.zero && currentHealthSpeed > 0.1f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, dynamicRotationSpeed * Time.deltaTime);
            lastAimDir = dirToTargetNormalize; // 診斷用:記錄這幀的瞄準方向
        }

        float targetSteerAngle = 0f;
        if (direction != Vector3.zero)
        {
            targetSteerAngle = Mathf.Clamp(Vector3.SignedAngle(transform.forward, dirToTargetNormalize, Vector3.up), -maxSteeringAngle, maxSteeringAngle);
        }
        currentSteerAngle = Mathf.Lerp(currentSteerAngle, targetSteerAngle, Time.deltaTime * wheelSteerSpeed);

        if (leftFrontWheel != null) leftFrontWheel.localRotation = Quaternion.Euler(leftFrontWheel.localRotation.eulerAngles.x, currentSteerAngle, leftFrontWheel.localRotation.eulerAngles.z);
        if (rightFrontWheel != null) rightFrontWheel.localRotation = Quaternion.Euler(rightFrontWheel.localRotation.eulerAngles.x, currentSteerAngle, rightFrontWheel.localRotation.eulerAngles.z);

        // 實際移動物體
        // 🟢 移動總閘門：不管上面哪套速度邏輯出錯，這一幀的位移絕不允許穿進前車。
        // 從車頭發一個薄片 BoxCast，位移最多走到「與前車間距 - minGap」為止
        float desiredStep = currentHealthSpeed * Time.deltaTime;
        if (desiredStep > 0f)
        {
            float minGap = 1.5f;      // 保底間距（車頭到前車車尾）
            float frontOffset = 2.0f; // 車身中心到車頭的距離，依你的車長微調
            Vector3 gateOrigin = transform.position + Vector3.up * 0.5f + transform.forward * frontOffset;

            if (Physics.BoxCast(gateOrigin, new Vector3(0.9f, 0.5f, 0.05f), transform.forward, out RaycastHit gateHit,
                                transform.rotation, desiredStep + minGap, detectionLayer, QueryTriggerInteraction.Ignore))
            {
                bool gateIsVehicle = !IsMyOwnCollider(gateHit.collider) &&
                                     (IsPlayerCollider(gateHit.collider)
                                      || gateHit.collider.GetComponentInParent<ReverseThreatAICar>() != null
                                      || gateHit.collider.GetComponentInParent<CarNavigation>() != null
                                      || ExternalTrafficVehicle.Is(gateHit.collider)); // 🚗[危險駕駛AI整合]

                // 🔧 閘門的過濾條件要跟前方偵測一致，否則會卡死：
                //    前方偵測排除「對向車」與「隔壁讓道中的車」（見 GetNearestVehicleInFrontCorridor），
                //    所以 calculatedTargetSpeed 保持前進；但閘門原本不排除，位移被壓成 0。
                //    上游每幀又把速度拉回來 → 車子永遠卡在原地。
                //    轉彎時閘門的 BoxCast 掃到隔壁車道也是同樣的狀況。
                // 🚗 側切承諾階段：閘門要跟前方偵測採同一套規則。
                //    只有 GetNearestVehicleInFrontCorridor 忽略玩家、閘門卻照擋的話，
                //    玩家一逼近就把 desiredStep 壓成 0，連 currentHealthSpeed 也一起壓 ——
                //    車會原地把整段側切演完：輪胎聲有、危險感知有紀錄，就是不動。
                //    排除後走下面的 hardFloor 分支，仍保留 gateHardMinGap 的硬性間距，不會穿進玩家車裡。
                if (gateIsVehicle && rushCommitPhase && rushIgnorePlayerWhileCommitting
                    && IsPlayerCollider(gateHit.collider))
                    gateIsVehicle = false;

                if (gateIsVehicle && gateUsesCorridorExclusions && !IsPlayerCollider(gateHit.collider))
                {
                    Transform vt = GetVehicleTransform(gateHit.collider);
                    bool isOncoming = vt != null && Vector3.Dot(transform.forward, vt.forward) < 0f;
                    // ↰ 小路口左轉讓右轉：對方正在右轉且很近時不豁免，閘門要照樣擋住
                    if ((isOncoming || IsBesideYieldingVehicle(gateHit.collider))
                        && !MustYieldToRightTurner(gateHit.collider))
                        gateIsVehicle = false;
                }

                if (gateIsVehicle)
                {
                    desiredStep = Mathf.Max(0f, gateHit.distance - minGap);
                    // 同步把速度壓下來，下一幀不會又想衝
                    currentHealthSpeed = Mathf.Min(currentHealthSpeed, desiredStep / Mathf.Max(Time.deltaTime, 0.0001f));
                    // 🔧 連目標速度也一起壓：只壓 currentHealthSpeed 的話，
                    //    下一幀 MoveTowards 又會朝 calculatedTargetSpeed 拉回去，變成持續拉鋸
                    calculatedTargetSpeed = Mathf.Min(calculatedTargetSpeed, currentHealthSpeed);
                }
                else
                {
                    // 被排除的對象仍要防止真的穿模：保留一道硬性最小間距
                    float hardFloor = Mathf.Max(0f, gateHit.distance - gateHardMinGap);
                    desiredStep = Mathf.Min(desiredStep, hardFloor);
                }
            }
        }
        transform.position += transform.forward * desiredStep;

        // 🔵 貝茲曲線結束判定
        if (isBezierActive && bezierT >= 1.0f)
        {
            isBezierActive = false;
            // 節點勾了「不需左轉禮讓」時不上冷卻鎖 —— 這種路口本來就不做禮讓判定，
            // 上了鎖只會讓後面連著的一般路口在 3 秒內也跟著不檢查。
            //（刻意只跳過禮讓冷卻，不動 exitBezierCooldown：那個管的是轉彎預減速，跟禮讓無關）
            if (!(currentWaypointNode != null && currentWaypointNode.disableLeftTurnYield))
                leftTurnYieldCooldown = 3.0f;
            exitBezierCooldown = exitBezierCooldownTime;

            lastWaypointNode = currentWaypointNode;

            if (bezierMergeTargetNode != null)
            {
                // 跨節點合併：直接跳兩格
                currentWaypointNode = bezierMergeTargetNode;
                if (plannedRoute.Count > 0) plannedRoute.RemoveAt(0); // 合併跳過一格，計畫同步前移
                GotoNextWaypoint(); // 從合併後的點再往下一格
            }
            else
            {
                // 短程貝茲：只算過了一格
                GotoNextWaypoint();
            }

            bezierMergeTargetNode = null;
            return; // 這一幀已經處理完路點切換，跳過下面的到達判定，避免重複觸發
        }

        if (exitBezierCooldown > 0f) exitBezierCooldown -= Time.deltaTime;
        // 到達判定
        if (isParkedAtYieldNode)
        {
            if (distanceToTarget < 1.8f)
            {
                currentHealthSpeed = Mathf.Lerp(currentHealthSpeed, 0f, Time.deltaTime * 5f);

                // 🟢 新增：死等逾時保護，避免任何漏接的釋放機制導致永久卡死
                parkedAtYieldTimer += Time.deltaTime;
                if (parkedAtYieldTimer > maxParkedAtYieldTime)
                {
                    UnityEngine.Debug.Log($"<color=magenta>⚠️ {name} 避讓死等逾時 {parkedAtYieldTimer:F1}s，直接銷毀讓補車機制接手</color>");

                    // 🟢 修改：不再強制恢復行駛，直接銷毀讓場上車輛數量偵測去補新車
                    if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
                    {
                        currentWaypointNode.isOccupied = false;
                        currentWaypointNode.occupiedByVehicleID = 0;
                    }

                    Destroy(gameObject);
                    return; // 銷毀後直接跳出，避免下面繼續存取已經要被銷毀的物件
                }
            }
        }
        else
        {
            // 🟢 修正：合併成一個 else，用巢狀 if 處理「不是死等避讓點」的情況
            parkedAtYieldTimer = 0f; // 沒在死等狀態就重置計時

            if (!isInWideRoad && !isBezierActive)
            {
                if (distanceToTarget < arrivalDistance)
                {
                    GotoNextWaypoint();
                }
            }
        }

    }
    // 🟢 判斷 collider 是否屬於玩家車——Tag 可能只掛在根物件，子物件 Collider 要往上找
    protected static bool IsPlayerCollider(Collider c)
    {
        if (c.CompareTag("Player")) return true;
        if (c.attachedRigidbody != null && c.attachedRigidbody.CompareTag("Player")) return true;
        return c.transform.root.CompareTag("Player");
    }
    // 🟢 回歸主車道前的淨空檢查：主車道併入區（含後方來車段）有車就先不回去
    private bool IsReturnPathClear()
    {
        // 🔧 回歸目標改用「我現在位置的左側一個車道寬」,不再用 savedNormalWaypointNode。
        //
        //    savedNormalWaypointNode 是避讓開始那一刻鎖定的,而靠邊過程中車子一路往前滑,
        //    十幾公尺後那個節點早就落在車身後方。舊算法把「舊節點」與「現在位置」取中點
        //    再往後偏 3 公尺,結果 20 公尺長的偵測框整個蓋在車後方的車道上 →
        //    只要後面有任何車經過就判定「主車道有車」→ 永遠等不到淨空,救護車走了也不回歸。
        //
        //    真正該檢查的是「我要切進去的那段車道現在有沒有車」,所以框以車身為中心。
        Vector3 returnPos = transform.position - transform.right * ambulanceEvadeOffset;
        Vector3 center = (returnPos + transform.position) * 0.5f + Vector3.up * 0.5f;
        Vector3 halfExtents = new Vector3(2.5f, 1.0f, 8f);

        Collider[] hits = Physics.OverlapBox(center, halfExtents, transform.rotation, detectionLayer);
        foreach (var h in hits)
        {
            if (IsMyOwnCollider(h)) continue;

            ReverseThreatAICar t = h.GetComponentInParent<ReverseThreatAICar>();
            if (t == this) continue;
            if (t != null && t.IsYieldingForAmbulanceNow) continue; // 🟢 還在避讓中的同伴不算障礙，避免互相卡死

            bool isVehicle = IsPlayerCollider(h) || t != null || h.GetComponentInParent<CarNavigation>() != null
                             || ExternalTrafficVehicle.Is(h); // 🚗[危險駕駛AI整合]
            if (isVehicle) return false; // 併入區有車，等下一個空檔
        }
        return true;
    }
    // 🟢 共用：近距離保險桿偵測（車頭前方約 0~4m 是否有車/玩家）
    private bool IsVehicleInFrontBumper()
    {
        Vector3 bumperCenter = transform.TransformPoint(new Vector3(0f, 0.5f, 2.0f));
        Collider[] hits = Physics.OverlapBox(bumperCenter, new Vector3(0.9f, 0.75f, 2.0f), transform.rotation, detectionLayer);
        foreach (var b in hits)
        {
            if (IsMyOwnCollider(b)) continue;
            // 🔧 在隔壁讓道中的車不算擋路(主車道有路權)
            if (IsBesideYieldingVehicle(b)) continue;
            bool isVehicle = IsPlayerCollider(b)
                 || b.GetComponentInParent<ReverseThreatAICar>() != null
                 || b.GetComponentInParent<CarNavigation>() != null
                 || ExternalTrafficVehicle.Is(b); // 🚗[危險駕駛AI整合] 鬼切/路霸演員車也算車
            if (isVehicle) return true;
        }
        return false;
    }
    // 🟢 判斷這個 collider 是不是「我自己車身」的一部分。
    // 絕不能用 transform.root 比對——所有車掛在「車流同捆包」底下時 root 全部相同，
    // 會把別台車誤判成自己而跳過，整個偵測系統集體失明
    private bool IsMyOwnCollider(Collider c)
    {
        return c.GetComponentInParent<ReverseThreatAICar>() == this;
    }

    // 🟢 取得 collider 所屬「車輛本體」的 Transform（不能用 root，root 是車流同捆包）
    private static Transform GetVehicleTransform(Collider c)
    {
        ReverseThreatAICar t = c.GetComponentInParent<ReverseThreatAICar>();
        if (t != null) return t.transform;
        CarNavigation n = c.GetComponentInParent<CarNavigation>();
        if (n != null) return n.transform;
        Transform ext = ExternalTrafficVehicle.TransformOf(c); // 🚗[危險駕駛AI整合]
        if (ext != null) return ext;
        return c.attachedRigidbody != null ? c.attachedRigidbody.transform : c.transform;
    }
    // 🟢 跟車走廊：一條車頭前方 2~17m、寬 2.2m 的 OverlapBox，直接量測最近同向車的實際間距。
    // 不靠射線，所以不受 Trigger 遮蔽、射線縫隙、SphereCast 起點盲區影響——追撞的最後保險
    /// <summary>
    /// 🔧 判斷這個碰撞體是不是「在我旁邊讓道中」的車。
    ///
    /// 主車道正常行駛的車 vs 讓道完要回歸的車,兩台並排時會互相把對方當障礙物:
    ///   主車道車 → 偵測到旁邊的車 → 煞停
    ///   回歸車   → IsReturnPathClear() 看到主車道有車 → 繼續等
    ///   兩台都停住 → 死鎖
    ///
    /// 路權上主車道優先,所以主車道的車不該為「還在避讓、且只是在隔壁」的車減速。
    /// 只排除「橫向明顯偏開」的,真的擋在正前方的照樣要煞(避免對撞)。
    /// </summary>
    private bool IsBesideYieldingVehicle(Collider h)
    {
        var otherWhite = h.GetComponentInParent<ReverseThreatAICar>();
        var otherGray = otherWhite == null ? h.GetComponentInParent<CarNavigation>() : null;

        // 🔧 靜止不動的車(路邊等觸發的側切車 / 靜態障礙車)永遠當實體障礙。
        //    它們也會收到救護車的避讓廣播讓 IsYieldingForAmbulanceNow 變 true,
        //    但根本不會移動 —— 豁免它們等於直接撞上去。
        //    (灰車 CarNavigation.IsBesideYieldingVehicle 早就有這道,白車這邊漏了;
        //     豁免接上主雷達後這個缺口會從保險桿範圍放大到整個雷達距離,必須補齊)
        if (otherWhite != null && otherWhite.isStaticParked) return false;
        if (otherGray != null && otherGray.isStaticObstacle) return false;

        // 🔧 「避讓中」和「回歸中」都算 —— 兩者都是暫時偏離車道、且會自己歸位的車。
        //    只認避讓中的話,救護車一離開對方就變成「一般障礙物」,
        //    主車道的車煞停 + 回歸車禮讓 → 兩台並排互等到逾時。
        bool otherYielding = (otherWhite != null && otherWhite != this && otherWhite.IsYieldingForAmbulanceNow)
                          || (otherGray != null && otherGray.IsYieldingForAmbulanceNow);
        bool otherReturning = (otherWhite != null && otherWhite != this && otherWhite.IsReturningFromYieldNow)
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

    private float GetNearestVehicleInFrontCorridor()
    {
        Vector3 center = transform.TransformPoint(new Vector3(0f, 0.5f, 2f + 7.5f));
        Vector3 halfExtents = new Vector3(1.1f, 0.9f, 7.5f);
        Collider[] hits = Physics.OverlapBox(center, halfExtents, transform.rotation, detectionLayer);

        float nearest = Mathf.Infinity;
        foreach (var h in hits)
        {
            if (IsMyOwnCollider(h)) continue; // 排除自己

            // 🚗 側邊切入的承諾階段：忽略玩家車。
            // 這個走廊涵蓋車頭前方 2~17m，玩家一逼近就會讓 rushGap 歸零、車停在原地，
            // 結果是「有輪胎聲但車沒切出來」。切入的本質就是要擋到玩家，這段期間必須無視他。
            if (rushCommitPhase && rushIgnorePlayerWhileCommitting && IsPlayerCollider(h)) continue;

            bool isVehicle = IsPlayerCollider(h)
                || h.GetComponentInParent<ReverseThreatAICar>() != null
                || h.GetComponentInParent<CarNavigation>() != null
                || ExternalTrafficVehicle.Is(h); // 🚗[危險駕駛AI整合] 鬼切/路霸演員車也算車
            if (!isVehicle) continue;

            // 對向車不算，避免窄路會車被煞死
            if (Vector3.Dot(transform.forward, GetVehicleTransform(h).forward) < 0f) continue;

            // 🔧 在隔壁讓道中的車不算擋路(主車道有路權),否則兩台互相偵測會死鎖
            if (IsBesideYieldingVehicle(h)) continue;

            // 用碰撞體邊界最近點算「實際間距」，不是車心到車心
            float d = Vector3.Distance(transform.position, h.bounds.ClosestPoint(transform.position));
            if (d < nearest) nearest = d;
        }
        return nearest;
    }
    void HandleForwardDetection()
    {
        // 基礎狀態檢查：如果是倒車中或煞車中，直接跳過
        if (isReverseAction || isForcedReversing || isBrakingAction) return;
        if (isRushingOut)
        {
            // 🟢 衝刺歸衝刺，跟車照跟：用走廊實測前方間距做完整減速曲線，
            // 不再只靠 4m 保險桿（那個距離對衝刺速度來說根本煞不住）
            // 衝刺速度上限改用倍率控制。原本這裡是 moveSpeed，等於衝出時跟一般車流同速。
            float rushTopSpeed = moveSpeed * rushSpeedFactor;

            float rushGap = GetNearestVehicleInFrontCorridor();
            if (rushGap <= safeDistance)
            {
                calculatedTargetSpeed = 0f;
            }
            else if (rushGap < 15f)
            {
                float rushT = Mathf.Clamp01((rushGap - safeDistance) / (15f - safeDistance));
                calculatedTargetSpeed = rushTopSpeed * rushT;
            }
            else
            {
                calculatedTargetSpeed = rushTopSpeed;
            }
            return;
        }
        // 🟢 新增：每一幀重置，避免用到上一幀的舊數值
        nearestForwardObstacleDistance = Mathf.Infinity;
        debugShowTurnYieldBox = false;
        debugTurnYieldBlocked = false;
        debugYellowGridExitNode = null;
        debugYellowGridBlocked = false;
        if (currentStartDelay > 0f)
        {
            currentStartDelay -= Time.deltaTime;
        }
        // 🟢 新增：優先用介面即時判斷玩家是否倒車，不依賴 SphereCast 是否命中
        if (!isForcedReversing)
        {
            CheckPlayerReverseImmediate();
            CheckCloseReversingCar(); // 🟢 貼身距離的倒車連鎖保險
        }


        // 初始化基礎變數
        float baseTargetSpeed = originalNormalSpeed;
        bool canStartMoving = true;

        // ==========================================
        // 🟢【新增：起步安全確認機制】
        // ==========================================
        // 只有在靜止狀態時才進行嚴格檢查
        bool blockedByPlayer = false; // 🟢 新增：記錄擋路的是不是玩家

        if (calculatedTargetSpeed <= 0.1f)
        {
            Vector3 boxCenter = transform.TransformPoint(startBoxOffset);
            Collider[] startHits = Physics.OverlapBox(boxCenter, startBoxSize / 2f, transform.rotation, detectionLayer);

            foreach (var h in startHits)
            {
                if (IsMyOwnCollider(h)) continue;

                if (IsPlayerCollider(h))
                {
                    canStartMoving = false;
                    blockedByPlayer = true;
                }
                else if (h.GetComponentInParent<ReverseThreatAICar>() != null || h.GetComponentInParent<CarNavigation>() != null)
                {
                    canStartMoving = false;
                }
            }
        }

        if (!canStartMoving)
        {
            // 🟢 新增：只要擋路的是玩家，就不啟動卡死銷毀倒數，改成按喇叭催促，直到玩家自己讓開
            if (blockedByPlayer)
            {
                startBlockedTimer = 0f;
                baseTargetSpeed = 0f;
                calculatedTargetSpeed = 0f;
                HandlePlayerBlockDetection();
                return;
            }

            startBlockedTimer += Time.deltaTime;

            if (startBlockedTimer > maxStartBlockedTime)
            {
                UnityEngine.Debug.Log($"<color=magenta>⚠️ {name} 起步偵測疑似死結，逾時 {startBlockedTimer:F1}s，直接銷毀</color>");

                if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
                {
                    currentWaypointNode.isOccupied = false;
                    currentWaypointNode.occupiedByVehicleID = 0;
                }

                Destroy(gameObject);
                return;
            }
            else
            {
                baseTargetSpeed = 0f;
                calculatedTargetSpeed = 0f;
                return;
            }
        }
        else
        {
            startBlockedTimer = 0f;
        }
        // ==========================================
        // 🚑 救護車核心權限：無視紅綠燈
        // ==========================================
        if (isYieldingForAmbulance || isForcedReversing || isReverseAction)
        {
            isWaitingAtTrafficLight = false;
        }
        else if (isWaitingAtTrafficLight)
        {
            bool activeNodeIsStopZone = currentWaypointNode != null && currentWaypointNode.isTrafficLightStopZone;
            float trafficLightSpeedCap;

            if (activeNodeIsStopZone)
            {
                float distToStopNode = Vector3.Distance(transform.position, currentWaypointNode.transform.position);

                if (distToStopNode > trafficLightIdealStopDistance)
                {
                    float brakeT = Mathf.Clamp01((distToStopNode - trafficLightIdealStopDistance) / trafficLightCreepRange);
                    trafficLightSpeedCap = Mathf.Lerp(0f, originalNormalSpeed * trafficLightCreepMaxPower, brakeT);
                }
                else
                {
                    trafficLightSpeedCap = 0f;
                }
            }
            else
            {
                // 🚦 不是排頭(目標不是停止線節點)→ 一樣給蠕行上限,讓跟車雷達決定要不要往前補位,
                //    前車開走留出空間時才會跟上去保持安全距離(不再整台鎖死不動)
                trafficLightSpeedCap = originalNormalSpeed * trafficLightCreepMaxPower;
            }

            // 🟢 不再 return，把蠕行速度當作「上限」，讓下面的跟車雷達繼續運作
            baseTargetSpeed = Mathf.Min(baseTargetSpeed, trafficLightSpeedCap);
        }
        // 🟢 起步反應延遲：剛轉綠燈的這段時間內強制鎖速為 0
        if (!isWaitingAtTrafficLight && currentStartDelay > 0f)
        {
            calculatedTargetSpeed = 0f;
            //return;
        }

        Vector3 origin = transform.position + Vector3.up * 0.5f;
        bool hasObstacleAhead = false;
        // 🟢 近距離保險桿：SphereCast 起點球內的物體偵測不到（物理引擎特性），
        // 前車貼太近時雷達會瞎掉，用 OverlapBox 補這個盲區
        // 🟢 近距離保險桿：SphereCast 起點球內的物體偵測不到（物理引擎特性），改用共用方法補盲區
        if (IsVehicleInFrontBumper())
        {
            nearestForwardObstacleDistance = Mathf.Min(nearestForwardObstacleDistance, 2.0f);
            baseTargetSpeed = 0f;
            hasObstacleAhead = true;
        }
        // 🟢 跟車走廊：不管扇形雷達有沒有看到，前方 17m 內的同向車都會被記錄到最近障礙距離，
        // 由函式結尾的 followFactor 區塊統一執行減速（8m 內歸零）
        nearestForwardObstacleDistance = Mathf.Min(nearestForwardObstacleDistance, GetNearestVehicleInFrontCorridor());
        // ==========================================
        // 1. 📡 扇形多射線雷達與跟車（取代單一 SphereCast 粗管）
        // ⚠️ 特殊行為全部保留：連鎖倒車、救護車訊號轉發、紅燈蠕行壓速
        // ==========================================
        float activeFanAngle = (isBezierActive || incomingTurnLastFrame) ? turnFanAngle : fanAngle;
        float angleStep = activeFanAngle / Mathf.Max(numberOfRays - 1, 1);
        float startAngle = -activeFanAngle * 0.5f;
        float fanPower = 1.0f; // 所有射線取最小動力：1=全速、0=煞停

        // 扇形中心跟著方向盤角度轉，過彎時整把扇子掃進弧線內
        Vector3 castDir = Quaternion.AngleAxis(currentSteerAngle, Vector3.up) * transform.forward;

        for (int i = 0; i < numberOfRays; i++)
        {
            float currentAngle = startAngle + angleStep * i;
            Vector3 rayDir = Quaternion.AngleAxis(currentAngle, Vector3.up) * castDir;

            // 側邊射線比較短；直行時正中央射線加長看更遠
            float sideFactor = Mathf.Abs(currentAngle) / (activeFanAngle * 0.5f);
            float rayDistance = detectionDistance * (1f - sideFactor * lateralDetectionMult);
            bool isCenterRay = Mathf.Abs(currentAngle) < 1f;
            bool isSideRay = Mathf.Abs(currentAngle) > activeFanAngle * 0.25f;
            if (!isBezierActive && isCenterRay) rayDistance *= straightCenterMult;

            // 🟢 兩個修正合一：
            // 1. QueryTriggerInteraction.Ignore：射線不再被紅綠燈/路口觸發區的表面擋住而報廢
            // 2. Raycast 換 SphereCast（半徑 0.6）：細線變細圓管，窄路弧度上前車稍微偏移也掃得到
            if (!Physics.SphereCast(origin, 0.6f, rayDir, out RaycastHit rayHit, rayDistance, detectionLayer, QueryTriggerInteraction.Ignore)) continue;
            if (IsMyOwnCollider(rayHit.collider)) continue; // 排除自己
            
            // --- 統一辨識前方物件 ---
            ReverseThreatAICar frontCar = rayHit.collider.GetComponentInParent<ReverseThreatAICar>();
            if (frontCar == this) frontCar = null;
            CarNavigation navCar = frontCar == null ? rayHit.collider.GetComponentInParent<CarNavigation>() : null;
            // 🚗 路邊靜停、還沒被觸發的側切車 → 當停放車輛忽略,不跟車不煞停
            if (frontCar != null && frontCar.isStaticParked) continue;

            // 🔧 在隔壁避讓中/回歸中的車不算擋路(主車道有路權),否則兩台互相偵測會死鎖。
            //    灰車的扇形雷達早就有這道豁免(CarNavigation 的同一位置),白車的主雷達
            //    卻只在保險桿盒與跟車走廊有 → 主車道那台若也是白車,它一定為回歸車煞停,
            //    回歸車又在禮讓它 → 白車對白車必卡。這裡補齊,兩種車的主雷達行為一致。
            //    IsBesideYieldingVehicle() 自身的三道安全鎖(自己轉彎中不豁免、距離下限
            //    besideIgnoreMinDistance、橫向 1.2m 與正前方 27° 錐)照舊 —— 真的擋在
            //    我行進路線上的車一律照煞,不會因為對方在避讓就直接撞上去。
            if (IsBesideYieldingVehicle(rayHit.collider)) continue;

            bool isPlayer = IsPlayerCollider(rayHit.collider);
            bool isRoadBlock = rayHit.collider.CompareTag("RoadBlock");

            // 🚧 已在繞行中 → 完全忽略故障車本體與路障,否則會被煞停而切不出去
            if (bypassPhase >= 1 && (isRoadBlock || rayHit.collider.GetComponentInParent<HazardLights>() != null))
                continue;

            // 不是車輛也不是路障（號誌桿、感測器等）→ 這條射線忽略
            // 🚗[危險駕駛AI整合] 外部車輛（鬼切/路霸演員車）原本會被當成號誌桿忽略 → 直接穿過去
            bool isExternalCar = frontCar == null && navCar == null && !isPlayer && ExternalTrafficVehicle.Is(rayHit.collider);
            if (frontCar == null && navCar == null && !isPlayer && !isRoadBlock && !isExternalCar) continue;

            float distance = rayHit.distance;
            float thisRayPower = 1.0f;

            if (isRoadBlock)
            {
                // 🚧 只有路障真的擋在「我這條車道的走廊」上才減速;
                // 對向道/隔壁道的三角錐用命中點橫向偏移排除,照常通行
                Vector3 localHit = transform.InverseTransformPoint(rayHit.point);
                if (Mathf.Abs(localHit.x) > 2.0f) continue; // 命中點在走廊外 → 這條射線忽略

                hasObstacleAhead = true;
                thisRayPower = Mathf.Clamp01((distance - safeDistance) / Mathf.Max(rayDistance - safeDistance, 0.1f));
            }
            else
            {
                Transform otherTf = frontCar != null ? frontCar.transform
                                  : navCar != null ? navCar.transform
                                  : GetVehicleTransform(rayHit.collider);

                Rigidbody otherRb = isPlayer ? rayHit.collider.GetComponentInParent<Rigidbody>() : null;
                float otherSpeed = frontCar != null ? frontCar.CurrentHealthSpeed
                                 : navCar != null ? navCar.RealTimeSpeed
                                 : (otherRb != null ? otherRb.velocity.magnitude
                                 : ExternalTrafficVehicle.SpeedOf(rayHit.collider, 0f)); // 🚗[危險駕駛AI整合]

                // 🟢 紅燈停等「或」無號誌路口停讓中，都視為「前車已決定要停」
                bool otherWaitingAtLight = (frontCar != null && (frontCar.IsWaitingAtTrafficLight() || frontCar.IsCarDetectedAtStopNow))
                                         || (navCar != null && (navCar.IsWaitingAtTrafficLightNow || navCar.IsCarDetectedAtStopNow));

                float headingDot = Vector3.Dot(transform.forward, otherTf.forward);

                
                // 🟢 對向車三層處理：
                // 1) 側邊射線掃到 → 交會路過，忽略
                // 2) 貝茲轉彎中掃到 → 也忽略：路權在進彎前已由左轉讓路框把關，
                //    彎中掃到只是弧線掠過對向車道，不該煞停卡死
                // 3) 直行時中央射線掃到，且對方真的在我正前方走廊內（逆向侵入）→ 才減速
                // ↰ 小路口左轉讓右轉：對方正在右轉且很近時，整段對向豁免都不套用
                if (headingDot < 0f && !MustYieldToRightTurner(otherTf))
                {
                    if (isSideRay || isBezierActive) continue;

                    Vector3 localOncoming = transform.InverseTransformPoint(otherTf.position);
                    if (Mathf.Abs(localOncoming.x) > 1.2f) continue; // 在對向車道上，不在我的走廊
                }

                // ⚠️ 救護車邏輯依賴的兩個變數照舊填值，行為不變
                hasObstacleAhead = true;
                nearestForwardObstacleDistance = Mathf.Min(nearestForwardObstacleDistance, distance);

                // --- ⚠️ 原有特殊行為，原樣保留 ---
                if (frontCar != null)
                {
                    // 窄路避讓中轉發救護車訊號（條件與原本相同）
                    if (isYieldingForAmbulance && !isInWideRoad && activeAmbulanceTransform != null
                        && Vector3.Distance(activeAmbulanceTransform.position, frontCar.transform.position) < 30f)
                    {
                        frontCar.SetYieldForAmbulance(true, activeAmbulanceTransform);
                    }

                    // 前車正在倒車 → 連鎖倒車（條件與原本相同）
                    // ⚠️ 跟隨玩家倒車的車不算「前車在倒車」——理由同 CheckCloseReversingCar：
                    //    PlayerReverseBroadcaster 會把跟隨車設 isForcedReversing=true（暫停它自己的行進），
                    //    但那台車是逐幀跟著玩家位移、玩家切 D 就由廣播器一處收掉。少了這道判斷，
                    //    後車會把它當成正在倒車、接手用固定速度自走式連鎖，於是玩家切回 D 後這些車還在退。
                    if (enableChainEvade && (frontCar.isReverseAction || frontCar.isForcedReversing)
                        && !frontCar.isFollowingPlayerReverse
                        && distance <= safeDistance + 1.2f)
                    {
                        StartCoroutine(ExecuteChainReverseRoutine(frontCar));
                        return;
                    }
                }
                // 🟢 前車是倒車中的 CarNavigation → 一樣連鎖後退，不再呆站著被倒進來
                if (navCar != null && enableChainEvade && navCar.IsForcedReversing
                    && !navCar.isFollowingPlayerReverse
                    && distance <= safeDistance + 1.2f)
                {
                    StartCoroutine(ExecuteChainReverseFromTransform(navCar.transform));
                    return;
                }
                // 🟢 跟車速度上限：正前方（非側邊）偵測距離內的車，
                // 把自己的目標速度直接壓到「前車速度 + 2」——距離減速之外再加一層速度跟隨，
                // 後車不再用全速去追一台慢車
                if (!isSideRay && distance < detectionDistance)
                {
                    baseTargetSpeed = Mathf.Min(baseTargetSpeed, otherSpeed + 2f);
                }
                // --- 分級動力 ---
                bool otherDrivingWell = otherSpeed > frontCarStableSpeedThreshold && !otherWaitingAtLight;

                if (distance <= safeDistance)
                {
                    // 🟢 修正：2m 內是硬停區，不分側邊/正前方一律強制歸零。
                    // 「側邊車不全停」的防禦只屬於中距離跟車判斷，不屬於快撞上的距離
                    thisRayPower = 0f;
                }
                else
                {
                    float dynamicMaxDistance =
                    Mathf.Max(
                        safeDistance * 2.5f,
                        rayDistance * 0.75f
                    );

                    if (distance <= dynamicMaxDistance)
                    {
                        thisRayPower = Mathf.Clamp01((distance - safeDistance) / (dynamicMaxDistance - safeDistance));


                        // 側邊同向斜角車保底：最多減到一半，不整台煞死
                        // 🟢 修正：保底只在安全距離外生效，4m 內交給線性減速自然收斂到停
                        if (isSideRay && headingDot < 0.75f && distance > safeDistance) thisRayPower = Mathf.Max(thisRayPower, 0.5f);

                        // 前車在等紅燈：中央射線直接把動力壓到貼著前車速度
                        if (otherWaitingAtLight && !isSideRay)
                        {
                            float waitCapPower = Mathf.Clamp01((otherSpeed + 1f) / originalNormalSpeed);
                            thisRayPower = Mathf.Min(thisRayPower, waitCapPower);
                        }
                    }
                }
            }

            if (thisRayPower < fanPower) fanPower = thisRayPower;
        }

        if (fanPower < 1.0f)
        {
            baseTargetSpeed = Mathf.Min(baseTargetSpeed, originalNormalSpeed * fanPower);
        }

        // 🟢 新增：全方位安全網——不管 SphereCast 有沒有打中，都用直線距離+寬扇形角度做保底節流
        // 避免玩家剛好偏出偵測角度時，AI 車瞬間彈回全速衝向玩家
        Transform playerSafetyTf = GetPlayerTransform();
        GameObject playerObjForSafety = playerSafetyTf != null ? playerSafetyTf.gameObject : null;
        if (playerObjForSafety != null)
        {
            Vector3 localPlayerCheck = transform.InverseTransformPoint(playerObjForSafety.transform.position);

            // 🟢 修正：縮小橫向範圍到接近半個車道寬，避免跨車道誤判
            float lateralCheckRange = 1.5f; // 依實際車道寬度微調，建議略小於半個車道寬

            bool playerRoughlyAhead = localPlayerCheck.z > -2f
                                       && localPlayerCheck.z < detectionDistance
                                       && Mathf.Abs(localPlayerCheck.x) < lateralCheckRange;

            // 🟢 新增：確認玩家車跟這台 AI 車朝向大致相同（同車道方向），排除對向車道/橫向路口的誤判
            bool sameDirection = playerObjForSafety.transform != null &&
                Vector3.Dot(transform.forward, playerObjForSafety.transform.forward) > 0.5f;

            if (playerRoughlyAhead && sameDirection)
            {
                float directDist = Vector3.Distance(transform.position, playerObjForSafety.transform.position);
                nearestForwardObstacleDistance = Mathf.Min(nearestForwardObstacleDistance, directDist);

                if (directDist <= safeDistance)
                {
                    baseTargetSpeed = 0f;
                }
                else
                {
                    float progress = Mathf.Clamp01((directDist - safeDistance) / (detectionDistance - safeDistance));
                    baseTargetSpeed = Mathf.Min(baseTargetSpeed, originalNormalSpeed * progress);
                }
            }
        }
        // ==========================================
        // ↰ 左轉讓對向直行（移植自 CarNavigation 階段2）
        // ==========================================
        if (leftTurnYieldCooldown > 0f) leftTurnYieldCooldown -= Time.deltaTime;

        bool shouldCheckYield = !isYieldingForAmbulance
                                // 🔧 救護車豁免:鳴笛執行任務時有絕對路權,不該停下來等對向直行車。
                                //    這裡少了豁免,救護車在路口左轉會被對向車擋住反覆煞停/起步(一頓一頓),
                                //    而且對向車一進一出偵測框就切換一次,明明可以直接通過卻過不去。
                                //    黃網(2487 行)、自毀(305/344 行)、繞行(400 行)都已有同樣的豁免。
                                && !(this is AmbulanceAI)
                                // ↰ 節點勾了「不需左轉禮讓」就整個跳過對向讓直行偵測
                                && !(currentWaypointNode != null && currentWaypointNode.disableLeftTurnYield)
                                && leftTurnYieldCooldown <= 0f
                                // 🔧 檢查窗口從 15% 拉到 60%:原本轉彎開始約 0.3 秒後就不再看對向車,
                                //    對向車在那之後才進入偵測框就完全不會停 → 直接轉過去。
                                //    超過 60% 已經轉進路口深處,這時停在路口中央反而更危險,維持轉完再說。
                                && (!isBezierActive || bezierT < 0.6f)
                                && IntendsToLeftTurnEarly()
                                && currentWaypointNode != null
                                && Vector3.Distance(transform.position, currentWaypointNode.transform.position) < turnYieldDistance;

        if (shouldCheckYield)
        {
            debugShowTurnYieldBox = true;
            Vector3 yieldCheckPos = transform.TransformPoint(new Vector3(turnDetectionSideOffset, 0.5f, 2.0f + turnForwardOffset));
            Collider[] oncoming = Physics.OverlapBox(yieldCheckPos, turnBoxSize / 2f, transform.rotation, detectionLayer);

            foreach (var col in oncoming)
            {
                if (IsMyOwnCollider(col)) continue;

                Transform otherTf = null;
                float otherSpeed = 0f;
                bool otherTurningLeft = false;

                ReverseThreatAICar otherThreat = col.GetComponentInParent<ReverseThreatAICar>();
                CarNavigation otherNav = col.GetComponentInParent<CarNavigation>();

                if (otherThreat != null && otherThreat != this)
                {
                    otherTf = otherThreat.transform;
                    otherSpeed = otherThreat.CurrentHealthSpeed;
                    otherTurningLeft = otherThreat.IntendsToLeftTurnEarly();
                }
                else if (otherNav != null)
                {
                    otherTf = otherNav.transform;
                    otherSpeed = otherNav.RealTimeSpeed;
                    otherTurningLeft = otherNav.IntendsToLeftTurnEarly();
                }

                if (otherTf == null) continue;

                float directionDot = Vector3.Dot(transform.forward, otherTf.forward);
                float distToOther = Vector3.Distance(transform.position, otherTf.position);

                // 對向直行車 (dot < -0.5)、對方不是左轉、對方有在動
                // 🔧 原本用 distToOther > 6.0f 無條件忽略近距離的車(為了防路口中心死鎖),
                //    但那等於把「已經衝到眼前」這個最危險的情況也一併跳過 → 照樣轉過去。
                //    改成只有「近距離且對方也停著」才忽略 —— 那才是真的互相讓到死鎖,需要有人先走;
                //    對方還在動就代表它正衝過來,更應該讓。
                bool deadlockClose = distToOther <= 6.0f && otherSpeed < 1.0f;
                if (directionDot < -0.5f && !otherTurningLeft && !deadlockClose && otherSpeed > 1.0f)
                {
                    debugTurnYieldBlocked = true;
                    baseTargetSpeed = 0f; // 停等對向車先過
                    break;
                }
            }
        }
        // ==========================================
        // 🚑 2. 救護車避讓限速分支 (窄路與寬路速限計算)
        // ==========================================
        if (isYieldingForAmbulance)
        {
            if (isInIntersection) baseTargetSpeed = originalNormalSpeed * 1.2f;
            else if (!isInWideRoad)
            {
                if (!hasObstacleAhead) baseTargetSpeed = narrowEscapeSpeed;
            }
            else
            {
                if (savedNormalWaypointNode != null && !isUsingOffsetFallback)
                {
                    float distToYieldNode = Vector3.Distance(transform.position, currentWaypointNode.transform.position);
                    baseTargetSpeed = (distToYieldNode > 1.8f) ? originalNormalSpeed * 1.2f : 0f;
                }
                else
                {
                    baseTargetSpeed = hasObstacleAhead ? Mathf.Min(baseTargetSpeed, originalNormalSpeed * 0.25f) : originalNormalSpeed * 0.55f;
                }
            }
        }

        // ==========================================
        // 🛑 3. Stop 與 Slow 路口機制 (最高安全權限)
        // ==========================================
        if (currentWaypointNode != null && baseTargetSpeed > 0f)
        {
            float distToNode = Vector3.Distance(transform.position, currentWaypointNode.transform.position);

            // A. 彎道降速
            // 🟢 修改：改用「路徑本身的轉彎角度」判斷，而非車輛暫時位置的角度，避免誤判與速度跳動


            if (isBezierActive)
            {
                // 貝茲執行中：允許較高的過彎時速，且不再乘上原本的硬煞比例
                float bezierSpeedCap = Mathf.Clamp01(targetFastTurnSpeed / originalNormalSpeed) * originalNormalSpeed;
                baseTargetSpeed = Mathf.Min(baseTargetSpeed, bezierSpeedCap);
            }
            else if (exitBezierCooldown <= 0f)
            {
                // 🟢 跨節點彎道預判（模仿 CarNavigation 的 brakeDistanceBasis）：
                // 看「這一段」和「下一段」路徑是否轉彎，用累加距離做平滑線性減速
                TrafficPathNode n1 = PeekPlannedNextNode();
                bool incomingTurn = false;
                float brakeDistanceBasis = 0f;
                float turnCrossY = 0f;

                if (n1 != null)
                {
                    Vector3 d0 = (currentWaypointNode.transform.position - transform.position).normalized;
                    Vector3 d1 = (n1.transform.position - currentWaypointNode.transform.position).normalized;

                    if (Vector3.Angle(d0, d1) > 15f)
                    {
                        incomingTurn = true;
                        brakeDistanceBasis = 0f; // 彎道入口就是當前節點，立即進入低速鎖定
                        turnCrossY = Vector3.Cross(d0, d1).y;
                    }
                    else
                    {
                        TrafficPathNode n2 = GetPlannedNode(2); // 🟢 看「實際會走」的下下格，不再猜 nextChoices[0]
                        if (n2 != null)
                        {
                            Vector3 d2 = (n2.transform.position - n1.transform.position).normalized;
                            if (Vector3.Angle(d1, d2) > 15f)
                            {
                                incomingTurn = true;
                                brakeDistanceBasis = distToNode; // 彎道入口在下一個節點，先走完這段直線
                                turnCrossY = Vector3.Cross(d1, d2).y;
                            }
                        }
                    }
                }

                if (incomingTurn && brakeDistanceBasis < approachDistance)
                {
                    // 右轉允許較高過彎速，跟 CarNavigation 一致
                    float activeTurnSpeed = turnCrossY > 0f ? targetFastTurnSpeed : targetTurnSpeed;
                    float t = Mathf.Clamp01((brakeDistanceBasis - 10f) / (approachDistance - 10f));
                    float curveCap = Mathf.Lerp(activeTurnSpeed, originalNormalSpeed, t);
                    baseTargetSpeed = Mathf.Min(baseTargetSpeed, curveCap);
                }
                incomingTurnLastFrame = incomingTurn; // 記給下一幀的扇形角度用
            }

            // B. Slow 提前預判減速 (25m 前)
            bool isUpcomingSlow = false;
            float totalDistToSlowPoint = distToNode;

            if (currentWaypointNode.intersectionType == TrafficPathNode.IntersectionType.Slow)
            {
                isUpcomingSlow = true;
            }
            else
            {
                TrafficPathNode nextNode = GetPlannedNode(1); // 🟢 讀實際計畫
                if (nextNode != null)
                {
                    if (nextNode.intersectionType == TrafficPathNode.IntersectionType.Slow)
                    {
                        isUpcomingSlow = true;
                        totalDistToSlowPoint += Vector3.Distance(currentWaypointNode.transform.position, nextNode.transform.position);
                    }
                }
            }

            if (isUpcomingSlow && totalDistToSlowPoint < approachDistance)
            {
                baseTargetSpeed = Mathf.Min(baseTargetSpeed, originalNormalSpeed * slowIntersectionSpeedPercent);
            }

            // C. Stop 停讓與盒狀偵測 (即便窄路無視紅燈，衝到路口遭遇橫向車依舊擁有最高停讓權限)
            if (IsStopNodeType(currentWaypointNode))
            {
                if (distToNode < approachDistance && !hasCheckedIntersection)
                {
                    // 線性減速
                    float stopT = Mathf.Clamp01((distToNode - 4f) / (approachDistance - 4f));
                    baseTargetSpeed = Mathf.Min(baseTargetSpeed, originalNormalSpeed * stopT);

                    // 距離路口 10m 內啟動左右偵測盒
                    if (distToNode < 10.0f)
                    {
                        // 大路口車道多，改用較寬較長的偵測框
                        bool isBigStop = currentWaypointNode.intersectionType == TrafficPathNode.IntersectionType.BigStop;
                        Vector3 activeBoxSize = isBigStop ? bigStopBoxSize : stopBoxSize;
                        Vector3 activeOffsetLeft = isBigStop ? bigStopBoxOffsetLeft : stopBoxOffsetLeft;
                        Vector3 activeOffsetRight = isBigStop ? bigStopBoxOffsetRight : stopBoxOffsetRight;

                        Vector3 leftCenter = transform.TransformPoint(activeOffsetLeft);
                        Vector3 rightCenter = transform.TransformPoint(activeOffsetRight);

                        Collider[] leftCars = Physics.OverlapBox(leftCenter, activeBoxSize / 2f, transform.rotation, detectionLayer);
                        Collider[] rightCars = Physics.OverlapBox(rightCenter, activeBoxSize / 2f, transform.rotation, detectionLayer);

                        bool incomingCarDetected = false;

                        // 🟢 1. 判定我是不是正在被背後的救護車推著走
                        bool isAmbulancePushingMeFromBehind = false;
                        if (isYieldingForAmbulance && activeAmbulanceTransform != null)
                        {
                            Vector3 dirFromAmbulanceToMe = (transform.position - activeAmbulanceTransform.position).normalized;
                            float directionDot = Vector3.Dot(activeAmbulanceTransform.forward, transform.forward);
                            float positionDot = Vector3.Dot(activeAmbulanceTransform.forward, dirFromAmbulanceToMe);

                            // 方向平行且在救護車前方
                            if (directionDot > 0.7f && positionDot > 0.5f)
                            {
                                isAmbulancePushingMeFromBehind = true;
                            }
                        }

                        // 檢查左側橫向來車
                        // 檢查左側橫向來車
                        foreach (var c in leftCars)
                        {
                            // 🟢 修正：不再依賴 Rigidbody，直接用車輛腳本本身的 Transform 與速度
                            ReverseThreatAICar crossThreatCar = c.GetComponentInParent<ReverseThreatAICar>();
                            CarNavigation crossNavCar = c.GetComponentInParent<CarNavigation>();

                            Transform otherTf = null;
                            float crossSpeed = 0f;

                            if (crossThreatCar != null)
                            {
                                otherTf = crossThreatCar.transform;
                                crossSpeed = crossThreatCar.CurrentHealthSpeed;
                            }
                            else if (crossNavCar != null)
                            {
                                otherTf = crossNavCar.transform;
                                crossSpeed = crossNavCar.RealTimeSpeed; // 需在 CarNavigation.cs 補上 public float RealTimeSpeed => realTimeSpeed;
                            }

                            if (otherTf == null || otherTf == this.transform) continue;

                            float dot = Vector3.Dot(otherTf.forward, transform.right);
                            if (dot > 0.4f)
                            {
                                if (crossSpeed < 1.0f || (isAmbulancePushingMeFromBehind && crossSpeed < 3.0f))
                                {
                                    continue;
                                }
                                incomingCarDetected = true;
                            }
                        }

                        // 檢查右側橫向來車
                        foreach (var c in rightCars)
                        {
                            // 🟢 修正：不再依賴 Rigidbody，直接用車輛腳本本身的 Transform 與速度
                            ReverseThreatAICar crossThreatCar = c.GetComponentInParent<ReverseThreatAICar>();
                            CarNavigation crossNavCar = c.GetComponentInParent<CarNavigation>();

                            Transform otherTf = null;
                            float crossSpeed = 0f;

                            if (crossThreatCar != null)
                            {
                                otherTf = crossThreatCar.transform;
                                crossSpeed = crossThreatCar.CurrentHealthSpeed;
                            }
                            else if (crossNavCar != null)
                            {
                                otherTf = crossNavCar.transform;
                                crossSpeed = crossNavCar.RealTimeSpeed;
                            }

                            if (otherTf == null || otherTf == this.transform) continue;

                            float dot = Vector3.Dot(otherTf.forward, -transform.right);
                            if (dot > 0.4f)
                            {
                                if (crossSpeed < 1.0f || (isAmbulancePushingMeFromBehind && crossSpeed < 3.0f))
                                {
                                    continue;
                                }
                                incomingCarDetected = true;
                            }
                        }

                        isCarDetectedAtStop = incomingCarDetected;

                        if (incomingCarDetected)
                        {
                            // 🛑 橫向真的有車快速衝過來，為了不攔腰撞上，最高權限煞停
                            baseTargetSpeed = 0f;
                        }
                        else
                        {
                            // 🟢 沒車，或是左右兩邊的車都只是安全煞停在那裡
                            hasCheckedIntersection = true;
                            isCarDetectedAtStop = false;

                            // 如果我是因為被救護車推著走才衝路口的，強制給予窄路逃逸時速衝過去
                            if (isAmbulancePushingMeFromBehind)
                            {
                                baseTargetSpeed = narrowEscapeSpeed;
                            }
                        }
                    }
                }
            }

            // 離開路口後重置檢查標記
            if (distToNode > 5.0f && hasCheckedIntersection)
            {
                hasCheckedIntersection = false;
            }
            // 🟡 黃色網狀線防卡死：出口被堵住就先停在網狀線外，不開進去卡在裡面（移植自 CarNavigation）
            // 🚑 救護車有優先權,不受黃網格規則約束(否則出口一直有車就永遠卡在原地)
            // 🔧 正在為救護車讓道的車也豁免黃網規則:出口有車就煞停在網狀線外的話,
            //    等於整台車停在路中間擋住後方的救護車。讓道時應該盡快通過並靠邊。
            if (currentWaypointNode.isInsideYellowGrid && !(this is AmbulanceAI) && !isYieldingForAmbulance)
            {
                // 找出真正的出口節點：下一點有勾就是出口；下一點沒勾但下下點有勾，下下點才是出口
                TrafficPathNode realExitNode = null;
                if (currentWaypointNode.nextChoices != null && currentWaypointNode.nextChoices.Count > 0)
                {
                    TrafficPathNode next1 = currentWaypointNode.nextChoices[0];
                    if (next1.isInsideYellowGrid) realExitNode = next1;
                    else if (next1.nextChoices != null && next1.nextChoices.Count > 0 && next1.nextChoices[0].isInsideYellowGrid)
                        realExitNode = next1.nextChoices[0];
                }

                if (realExitNode != null)
                {
                    Vector3 dirToEntrance = (currentWaypointNode.transform.position - transform.position).normalized;
                    float dotToEntrance = Vector3.Dot(transform.forward, dirToEntrance);

                    // 只有「入口還在我前方、且進入預判距離」時才檢查；人已經在網狀線裡就不再煞停，趕快開出去
                    if (dotToEntrance > 0f && distToNode < approachDistance)
                    {
                        debugYellowGridExitNode = realExitNode;
                        Vector3 checkPosition = realExitNode.transform.TransformPoint(yellowGridBoxOffset);
                        Collider[] carsAtExit = Physics.OverlapBox(checkPosition, yellowGridBoxSize / 2f, realExitNode.transform.rotation, detectionLayer);
                        bool isExitBlocked = false;

                        foreach (var col in carsAtExit)
                        {
                            if (IsMyOwnCollider(col)) continue; // 排除自己

                            // 出口上只要有「開很慢或停住」的車，不論哪一種車系，都算堵住
                            ReverseThreatAICar otherThreat = col.GetComponentInParent<ReverseThreatAICar>();
                            CarNavigation otherNav = col.GetComponentInParent<CarNavigation>();

                            // 🛵 靠邊的機車不算堵住出口(它佔的寬度小,旁邊還過得去)
                            if (otherNav != null && otherNav.isScooter)
                            {
                                Vector3 relToExit = realExitNode.transform.InverseTransformPoint(otherNav.transform.position);
                                if (Mathf.Abs(relToExit.x) > 1.2f) continue; // 偏離出口中心 → 不算擋路
                            }

                            if (otherThreat != null && otherThreat != this && otherThreat.CurrentHealthSpeed < 5f) { isExitBlocked = true; break; }
                            else if (otherNav != null && otherNav.RealTimeSpeed < 5f) { isExitBlocked = true; break; }
                            else if (IsPlayerCollider(col)) { isExitBlocked = true; break; }
                        }
                        debugYellowGridBlocked = isExitBlocked;
                        if (isExitBlocked)
                        {
                            // 依距離線性減速，貼近入口 3m 內直接煞停在網狀線外
                            float gridT = Mathf.Clamp01((distToNode - 5f) / (approachDistance - 5f));
                            if (distToNode <= 3f) gridT = 0f;
                            baseTargetSpeed = Mathf.Min(baseTargetSpeed, originalNormalSpeed * gridT);
                        }
                    }
                }
            }
        }
        // 🟢 新增：獨立預警雷達，在所有既有判斷都算完之後，疊加最後一層提早減速保護
        HandleEarlyWarningDetection(ref baseTargetSpeed);
        if (nearestForwardObstacleDistance < 15f)
        {
            float followFactor =
                Mathf.Clamp01(
                    (nearestForwardObstacleDistance - safeDistance)
                    /
                    (15f - safeDistance)
                );

            baseTargetSpeed =
                Mathf.Min(
                    baseTargetSpeed,
                    originalNormalSpeed * followFactor
                );
        }
        calculatedTargetSpeed = baseTargetSpeed;

        // 🎯 危險因子待命中：先放慢讓玩家追上來，逼近到發動距離才真的演。
        //    用 Mathf.Min 是刻意的 —— 它只能讓車更慢，永遠不會加速，
        //    所以紅燈停等／路口停讓／跟車減速那些把速度設成 0 的邏輯完全不受影響。
        if (hazardArmSpeedCap >= 0f)
            calculatedTargetSpeed = Mathf.Min(calculatedTargetSpeed, hazardArmSpeedCap);

        // 🚶 禮讓背景行人：PedestrianYieldSensor 掃到行人擋在車前時會設這個上限。
        //    同樣用 Mathf.Min —— 只會更慢、不會加速，跟紅燈停等/跟車那些邏輯不打架。
        if (pedestrianYieldCap >= 0f)
            calculatedTargetSpeed = Mathf.Min(calculatedTargetSpeed, pedestrianYieldCap);

        // 🟢 新增：在算出目標速度後，檢查是否需要對玩家按喇叭
        HandlePlayerBlockDetection();
    }
    // 🟢 近距離倒車連鎖保險（跟 CarNavigation 那份對稱）：
    // 同時補上「前車是倒車中的 CarNavigation」白車原本不會連鎖的缺口
    private void CheckCloseReversingCar()
    {
        if (!enableChainEvade || isForcedReversing || isReverseAction || isBrakingAction) return;

        Collider[] hits = Physics.OverlapBox(
            transform.TransformPoint(new Vector3(0f, 0.5f, 2.0f)), // 覆蓋車頭前 0~4m
            new Vector3(1.0f, 0.8f, 2.2f),
            transform.rotation, detectionLayer);

        foreach (var h in hits)
        {
            if (IsMyOwnCollider(h)) continue;

            // ⚠️ 跟隨玩家倒車的車不算「前車在倒車」。
            //    PlayerReverseBroadcaster 會把跟隨中的車設 isForcedReversing = true
            //    (必須設,白車有十幾處靠它暫停行進),但那台車實際上是「逐幀跟著玩家的位移」——
            //    玩家沒動它就一格都不動。
            //    少了這道判斷,前面的車會把它誤判成正在倒車,接手用 minChainReverseSpeed 的
            //    固定速度自己往後滑 —— 於是玩家一切 R 檔、車都還沒動,後面就有車在退了。
            //    這裡只是排除誤判來源,連鎖倒車本身的邏輯一個字都沒改。
            ReverseThreatAICar threatCar = h.GetComponentInParent<ReverseThreatAICar>();
            if (threatCar != null && threatCar != this
                && (threatCar.isReverseAction || threatCar.isForcedReversing)
                && !threatCar.isFollowingPlayerReverse)
            {
                StartCoroutine(ExecuteChainReverseRoutine(threatCar));
                return;
            }

            CarNavigation navCar = h.GetComponentInParent<CarNavigation>();
            if (navCar != null && navCar.IsForcedReversing
                && !navCar.isFollowingPlayerReverse)
            {
                StartCoroutine(ExecuteChainReverseFromTransform(navCar.transform));
                return;
            }
        }
    }

    // 🟢 Transform 版連鎖倒車：跟著倒車中的 CarNavigation 後退
    IEnumerator ExecuteChainReverseFromTransform(Transform frontTf)
    {
        isForcedReversing = true;

        while (frontTf != null)
        {
            CarNavigation nav = frontTf.GetComponentInParent<CarNavigation>();
            if (nav == null || !nav.IsForcedReversing) break;

            float distToFront = Vector3.Distance(transform.position, frontTf.position);
            float speedOffset = (safeDistance - distToFront) * 2.0f;
            float targetSpeed = Mathf.Clamp(chainReverseSpeed + speedOffset, minChainReverseSpeed, 10f);

            transform.position -= transform.forward * targetSpeed * Time.deltaTime;
            currentHealthSpeed = -targetSpeed;
            SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, true, false);

            yield return null;
        }

        isForcedReversing = false;
        currentHealthSpeed = 0f;
        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, false, false);
        GetComponent<Rigidbody>().WakeUp();
    }
    // 🟢 新增：獨立的提早預判雷達，跟原本的安全距離 SphereCast 完全分開
    // 目的：不管前車是不是紅燈、急煞、還是倒車，只要它「現在開得不穩」，就提早依距離拉遠減速起點
    void HandleEarlyWarningDetection(ref float baseTargetSpeed)
    {
        RaycastHit warnHit;
        Vector3 warnOrigin = transform.position + Vector3.up * 0.5f;

        if (!Physics.SphereCast(warnOrigin, earlyWarningRadius, transform.forward, out warnHit, earlyWarningDistance, detectionLayer, QueryTriggerInteraction.Ignore))
            return;

        if (IsMyOwnCollider(warnHit.collider)) return; // 排除自己

        Vector3 localPos = transform.InverseTransformPoint(warnHit.collider.transform.position);
        bool isInMyLaneCorridor = localPos.z > 0f && Mathf.Abs(localPos.x) < 2.2f;
        if (!isInMyLaneCorridor) return;

        float otherSpeed;
        bool otherWaitingAtLight = false;
        ReverseThreatAICar otherThreatCar = warnHit.collider.GetComponentInParent<ReverseThreatAICar>();
        CarNavigation otherNavCar = warnHit.collider.GetComponentInParent<CarNavigation>();

        if (otherThreatCar != null && otherThreatCar != this)
        {
            otherSpeed = otherThreatCar.CurrentHealthSpeed;
            otherWaitingAtLight = otherThreatCar.IsWaitingAtTrafficLight() || otherThreatCar.IsCarDetectedAtStopNow;
        }
        else if (otherNavCar != null)
        {
            otherSpeed = otherNavCar.RealTimeSpeed;
            otherWaitingAtLight = otherNavCar.IsWaitingAtTrafficLightNow || otherNavCar.IsCarDetectedAtStopNow;
        }
        else if (ExternalTrafficVehicle.Is(warnHit.collider)) // 🚗[危險駕駛AI整合] 鬼切/路霸演員車也要提早預判減速
        {
            otherSpeed = ExternalTrafficVehicle.SpeedOf(warnHit.collider, 0f);
        }
        else
        {
            return; // 不是車輛（號誌桿、感測器等），不處理
        }

        // 🟢 前車「已決定要停」（紅燈旗標瞬間為 true）就直接視為不穩定，
        // 不等它的實際速度慢慢降下來，消除紅燈剛觸發那 0.7 秒的偵測空窗
        bool frontCarDrivingWell = otherSpeed > frontCarStableSpeedThreshold && !otherWaitingAtLight;
        if (frontCarDrivingWell) return;

        // 🟢 等紅燈的前車：用它「即將降到的蠕行速度」當基準，而不是當下還沒降下來的瞬時速度
        float referenceSpeed = otherWaitingAtLight
            ? Mathf.Min(otherSpeed, originalNormalSpeed * trafficLightCreepMaxPower)
            : otherSpeed;

        float progress = Mathf.Clamp01((warnHit.distance - safeDistance) / (earlyWarningDistance - safeDistance));
        float earlyCap = Mathf.Lerp(referenceSpeed + 1f, originalNormalSpeed, progress);

        baseTargetSpeed = Mathf.Min(baseTargetSpeed, earlyCap);
    }
    // 診斷輸出的節流計時（比照灰車 CarNavigation 的 DiagReverse）
    private float lastReverseDiagTime = -999f;

    private void DiagReverse(string msg)
    {
        lastReverseDiagTime = Time.time;
        UnityEngine.Debug.Log($"<color=orange>[白車倒車診斷] {name} | {msg}</color>", this);
    }

    private void CheckPlayerReverseImmediate()
    {
        // 🔙 舊的「每台車自己偵測玩家倒車」路徑。
        //    現在改由玩家車上的 PlayerReverseBroadcaster 主動廣播，這條是多餘的，
        //    而且它每幀都在跑，可能在錯誤時機把 isForcedReversing 設起來，
        //    導致廣播器的呼叫被「已在倒車中」擋掉。預設關閉。
        if (!useLegacySelfDetection) return;

        // 只有勾了 debugLogDetection 才輸出，正常執行路徑完全不變
        bool diag = debugLogDetection && Time.time - lastReverseDiagTime > 1f;

        Transform playerTf = GetPlayerTransform();
        if (playerTf == null)
        {
            if (diag) DiagReverse("★ 找不到任何 Tag=Player 的物件");
            return;
        }
        GameObject playerObj = playerTf.gameObject;

        float distToPlayer = Vector3.Distance(transform.position, playerObj.transform.position);
        if (distToPlayer > playerReverseChainDistance)
        {
            if (diag) DiagReverse($"距離 {distToPlayer:F1} > 上限 {playerReverseChainDistance} ★太遠");
            return;
        }

        // 🚗 只有「玩家後方」的車需要跟著倒。
        //    前方的車看到你倒車不需要也往後退，那不合理。
        Vector3 local = playerObj.transform.InverseTransformPoint(transform.position);
        if (local.z > 0f)
        {
            if (diag) DiagReverse($"距離 {distToPlayer:F1} ✓ | 但我在玩家前方 {local.z:F1}m ★不需要跟著倒");
            return;
        }
        if (Mathf.Abs(local.x) > playerReverseChainLateral)
        {
            if (diag) DiagReverse($"距離 {distToPlayer:F1} ✓ | 後方 {-local.z:F1}m ✓ | " +
                                  $"但橫向 {Mathf.Abs(local.x):F1} > {playerReverseChainLateral} ★在隔壁車道");
            return;
        }

        // 🟢 統一走共用方法：優先讀介面，退回 Rigidbody 判斷
        IPlayerVehicleState st = playerObj.GetComponent<IPlayerVehicleState>();
        bool reversing = IsPlayerReversingNow(playerObj);

        if (diag)
            DiagReverse($"距離 {distToPlayer:F1} ✓ | 後方 {-local.z:F1}m ✓ | 橫向 {Mathf.Abs(local.x):F1} ✓ | " +
                        $"介面={(st != null ? "有" : "★沒有(退回 Rigidbody 判斷)")} | IsReversing={reversing}");

        if (reversing)
        {
            UnityEngine.Debug.Log($"<color=lime>🔙 [連鎖倒車] {name} 觸發，開始跟著倒</color>", this);
            StartCoroutine(ExecuteChainReverseFromPlayer(playerTf));
        }
    }
    // 🟢 共用：判斷玩家當下是否正在倒車（優先讀介面，退回 Rigidbody 判斷）
    private bool IsPlayerReversingNow(GameObject playerObj)
    {
        if (playerObj == null) return false;

        IPlayerVehicleState playerState = playerObj.GetComponent<IPlayerVehicleState>();
        if (playerState != null) return playerState.IsReversing;

        Rigidbody playerRb = playerObj.GetComponent<Rigidbody>();
        return playerRb != null
            && playerRb.velocity.magnitude > 0.01f
            && Vector3.Dot(playerRb.velocity.normalized, playerObj.transform.forward) < 0f;
    }
    void HandlePlayerBlockDetection()
    {
        // 🟢 細線換圓管＋忽略Trigger＋Tag往根物件找，三個修正合一
        RaycastHit hit;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.5f, 0.6f, transform.forward, out hit, 10f, detectionLayer, QueryTriggerInteraction.Ignore))
        {
            if (IsPlayerCollider(hit.collider))
            {
                Rigidbody playerRb = hit.collider.GetComponentInParent<Rigidbody>();

                float playerSpeed = (playerRb != null) ? playerRb.velocity.magnitude : 0f;

                // 判斷玩家是否幾乎靜止 (< 0.5m/s)
                bool isPlayerStopped = playerSpeed < 0.5f;

                if (isPlayerStopped)
                {
                    // 決定觸發條件的閾值 (情境 A: 任意路段, 情境 B: 綠燈路口)
                    // 如果是紅燈 (isWaitingAtTrafficLight == true)，我們完全不理會玩家
                    if (isWaitingAtTrafficLight)
                    {
                        blockedTimer = 0f;
                    }
                    // 🔧 綠燈了但玩家自己前方也被車卡住 → 他不是故意不走,不該對他按喇叭
                    else if (IsPlayerBlockedAhead(hit.collider))
                    {
                        blockedTimer = 0f;
                        if (debugLogDetection)
                            UnityEngine.Debug.Log($"🎺 {name}:玩家前方也被車擋住,不催他");
                    }
                    else
                    {
                        // 🟢 綠燈路口 或 任意路段上的惡意停靠
                        blockedTimer += Time.deltaTime;

                        // 判斷按喇叭的秒數
                        // 情境 A (隨意路上)：若非路口，可能只需要 2 秒就按喇叭
                        // 情境 B (綠燈起步)：若在路口，則給予 4 秒寬容期
                        float requiredTime = isInIntersection ? 5.0f : 3.0f;

                        if (blockedTimer >= requiredTime && !isHonking)
                        {
                            TriggerHorn();
                        }
                    }
                }
                else
                {
                    // 玩家有在移動，重置計時
                    blockedTimer = 0f;
                }
            }
            else
            {
                blockedTimer = 0f;
            }
        }
        else
        {
            blockedTimer = 0f;
        }
    }
    [Header("🔧 喇叭:玩家被前車擋住時不催")]
    [Tooltip("從玩家車頭往前掃多遠,確認玩家自己是不是也被車卡住(公尺)")]
    public float playerBlockedCheckDistance = 12f;
    [Tooltip("掃描起點從玩家中心往前偏移多少,避免掃到玩家自己的車身(公尺)")]
    public float playerBlockedCheckOffset = 3f;

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
            playerBlockedCheckDistance, detectionLayer, QueryTriggerInteraction.Ignore);

        foreach (RaycastHit h in hits)
        {
            if (h.collider == null) continue;
            if (IsMyOwnCollider(h.collider)) continue;    // 掃到我自己不算
            if (IsPlayerCollider(h.collider)) continue;   // 掃到玩家自己的車身不算

            if (h.collider.GetComponentInParent<ReverseThreatAICar>() != null
             || h.collider.GetComponentInParent<CarNavigation>() != null)
                return true;                              // 玩家前面真的有車擋著
        }
        return false;
    }

    void TriggerHorn()
    {
        if (debugLogDetection)
        {
            UnityEngine.Debug.Log($"🎺 喇叭觸發！目前紅燈狀態為: {isWaitingAtTrafficLight}");
        }

        isHonking = true;

        if (hornClip != null)
        {
            // 有指定專屬喇叭音檔 → 用獨立的 AudioSource 播，不去搶車上其他音效的來源。
            // (GetComponent<AudioSource>() 會拿到「第一顆」，而衝出音效/急煞音效都會動態
            //  AddComponent，順序不保證,拿到哪一顆是碰運氣。)
            if (playerHornAudioSource == null)
            {
                playerHornAudioSource = gameObject.AddComponent<AudioSource>();
                playerHornAudioSource.playOnAwake = false;
                playerHornAudioSource.loop = false;
                playerHornAudioSource.spatialBlend = 1f;   // 3D:玩家要聽得出是哪個方向按的
                playerHornAudioSource.rolloffMode = AudioRolloffMode.Linear;
                playerHornAudioSource.minDistance = 5f;
                playerHornAudioSource.maxDistance = 45f;
            }
            playerHornAudioSource.PlayOneShot(hornClip, hornVolume);
        }
        else
        {
            // 舊行為:播車上第一顆 AudioSource 原本的 clip
            AudioSource audio = GetComponent<AudioSource>();
            if (audio == null || audio.clip == null)
            {
                // 原本是靜默失敗,「沒聲音」完全查不出原因 —— 比照輪胎聲改成明確警告
                UnityEngine.Debug.LogWarning(
                    $"[喇叭] {name} 沒有指定 Horn Clip,車上也找不到帶 clip 的 AudioSource,喇叭不會有聲音。" +
                    $"建議在 prefab 指定 聲音/喇叭_雙音.wav", this);
            }
            else audio.Play();
        }
        if (debugLogDetection)
        {
            UnityEngine.Debug.Log("🎺 嗶嗶！AI 正在對玩家按喇叭！");
        }

        StartCoroutine(ResetHornRoutine());
    }

    /// <summary>
    /// 🎺 切出去之後，玩家還逼在後面很近 → 按一聲喇叭。
    ///
    /// 幾何上是「玩家在我後方且距離很近」——切進玩家前面之後本來就是這個相對位置，
    /// 這一聲等於在說「你離太近了」，剛好接上跟車距離那套教學。
    ///
    /// 玩家有讓(rushPlayerYielded)就不按：那是一次正常匯入，沒有按喇叭的理由，
    /// 而且做對事卻被按喇叭會把互動式側切的回饋弄反。
    /// </summary>
    private void UpdatePostCutHorn()
    {
        if (!postCutHornEnabled || postCutHornDone) return;

        string block = null;

        if (!isRushingOut) block = "沒在衝出中(isRushingOut=false)";
        else if (!rushIsPlayerSideCutIn) block = "不是針對玩家的側切(路口淨空衝刺不按)";
        else if (rushPlayerYielded) block = "玩家有讓 → 正常匯入，不按";
        else if (isHonking) block = "上一聲喇叭還在冷卻(3秒)";
        else if (Time.time - rushStartTime < postCutHornDelay)
            block = $"切出後還不到 {postCutHornDelay:F1} 秒";

        Transform p = null;
        if (block == null)
        {
            p = rushPlayerTf != null ? rushPlayerTf : GetPlayerTransform();
            if (p == null) block = "找不到玩家(Player tag 沒設?)";
        }

        Vector3 flat = Vector3.zero;
        if (block == null)
        {
            flat = p.position - transform.position;
            flat.y = 0f;
            float d = flat.magnitude;
            if (d > postCutHornDistance)
                block = $"玩家距離 {d:F1}m > 門檻 {postCutHornDistance:F1}m";
            else if (Vector3.Dot(flat, transform.forward) > 0f)
                block = $"玩家在我前方(已經超過去了)，距離 {d:F1}m";
        }

        if (block != null)
        {
            // 只在「衝出中」才診斷，否則平常每秒都會印一行沒意義的訊息
            if (logPostCutHorn && isRushingOut && Time.time - lastHornDiagTime > 1f)
            {
                lastHornDiagTime = Time.time;
                UnityEngine.Debug.Log($"<color=grey>🎺 [喇叭診斷] {name} 沒按，原因：{block}</color>", this);
            }
            return;
        }

        postCutHornDone = true;
        TriggerHorn();
    }

    IEnumerator ResetHornRoutine()
    {
        yield return new WaitForSeconds(3.0f); // 按完休息 3 秒再繼續按
        isHonking = false;
    }

    public void GotoNextWaypoint()
    {
        if (isYieldingForAmbulance && isInWideRoad && !isUsingOffsetFallback)
            return;

        if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
        {
            currentWaypointNode.isOccupied = false;
            currentWaypointNode.occupiedByVehicleID = 0;
        }

        bool isReallyLost = currentWaypointNode == null; // 只有真的沒有點才觸發保底搜尋

        if (isReallyLost)
        {
            TrafficPathNode nearest = FindNearestWaypointNode();
            if (nearest != null) currentWaypointNode = nearest;
        }
        else
        {
            lastWaypointNode = currentWaypointNode;
            TrafficPathNode next = GetPlannedNode(1);
            if (next != null)
            {
                currentWaypointNode = next;
                if (plannedRoute.Count > 0) plannedRoute.RemoveAt(0); // 頭一格用掉，後面的計畫保留繼續沿用
            }
        }
    }
    Vector3 GetBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        t = Mathf.Clamp01(t);
        float oneMinusT = 1f - t;
        return oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2;
    }

    // 供其他車輛查詢「我現在是不是正在轉彎，往哪邊轉」。灰車有 amILeftTurning/amIRightTurning，白車原本沒有。
    public bool IsLeftTurningNow => isBezierActive && bezierIsLeftTurn;
    public bool IsRightTurningNow => isBezierActive && bezierIsRightTurn;

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

        ReverseThreatAICar threat = other.GetComponentInParent<ReverseThreatAICar>();
        if (threat != null && threat != this)
        {
            otherTf = threat.transform; otherTurningRight = threat.IsRightTurningNow; otherSpeed = threat.CurrentHealthSpeed;
        }
        else if (threat == null)
        {
            CarNavigation nav = other.GetComponentInParent<CarNavigation>();
            if (nav != null) { otherTf = nav.transform; otherTurningRight = nav.IsRightTurningNow; otherSpeed = nav.RealTimeSpeed; }
        }

        if (otherTf == null || !otherTurningRight) return false;

        // 對方停著不動就不該扣住我 —— 否則排隊等紅燈的車會把左轉車永遠釘在原地
        if (otherSpeed < 0.5f) return false;

        // 只讓「在我前方」的車擋。後方或側後方的車不構成轉彎衝突。
        Vector3 local = transform.InverseTransformPoint(otherTf.position);
        if (local.z <= 0f) return false;

        return local.magnitude <= leftYieldToRightTurnerRadius;
    }

    void StartBezierTurn(Vector3 startPoint, Vector3 controlPoint, Vector3 endPoint, TrafficPathNode mergeTarget)
    {
        isBezierActive = true;

        // ↰ 記下這次轉彎的方向，給「小路口左轉車讓右轉車」查詢用
        bezierIsLeftTurn = false;
        bezierIsRightTurn = false;
        {
            Vector3 inDir = controlPoint - startPoint;
            Vector3 outDir = endPoint - controlPoint;
            inDir.y = 0f; outDir.y = 0f;
            if (inDir.sqrMagnitude > 0.01f && outDir.sqrMagnitude > 0.01f)
            {
                inDir.Normalize(); outDir.Normalize();
                if (Vector3.Angle(inDir, outDir) > 15f)
                {
                    if (Vector3.Cross(inDir, outDir).y < 0f) bezierIsLeftTurn = true;
                    else bezierIsRightTurn = true;
                }
            }
        }
        bezierT = 0f;
        bezierP0 = startPoint;
        // 🟢 修正抄捷徑：原本 controlPoint（節點）只是拉力方向，曲線永遠不會真的經過它，
        // 窄路口就會切進隔壁車道。改用反推公式，讓曲線在 t=0.5 時「剛好通過」節點——
        // 節點擺哪，車的轉彎軌跡就壓在哪
        // 🟢 控制點權重混合：在「精準通過節點」與「不外擺」之間取捨
        Vector3 passThroughControl = 2f * controlPoint - 0.5f * (startPoint + endPoint);
        bezierP1 = Vector3.Lerp(controlPoint, passThroughControl, curveThroughNodeWeight);
        bezierP2 = endPoint;
        bezierMergeTargetNode = mergeTarget;
    }
    private TrafficPathNode FindNearestWaypointNode()
    {
        TrafficPathNode[] allNodes = UnityEngine.Object.FindObjectsByType<TrafficPathNode>(FindObjectsSortMode.None);
        TrafficPathNode closest = null;
        float minDist = Mathf.Infinity;

        foreach (var node in allNodes)
        {
            // 【關鍵優化】計算節點相對車頭的方向
            Vector3 dirToNode = (node.transform.position - transform.position).normalized;
            float dot = Vector3.Dot(transform.forward, dirToNode);

            // 條件 1: 節點必須在車頭前方 (Dot > 0)
            // 條件 2: 距離必須在合理範圍內
            // 條件 3: 比當前找到的點更近
            if (dot > 0.2f)
            {
                // 🟢 節點的路徑走向要跟我的車頭大致同向，避免迷路時撿到對向車道的點
                Vector3 nodeDir = (node.nextChoices != null && node.nextChoices.Count > 0)
                    ? (node.nextChoices[0].transform.position - node.transform.position).normalized
                    : dirToNode;
                if (Vector3.Dot(transform.forward, nodeDir) < 0.3f) continue;
                float dist = Vector3.Distance(transform.position, node.transform.position);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = node;
                }
            }
        }

        // 如果真的找不到前方的點，退而求其次選一個最近的點
        return closest != null ? closest : currentWaypointNode;
    }
    // 🟢 追撞黑盒子 v2：不靠物理碰撞事件，直接偵測「有別的車疊進我的車身」
    // 🟢 追撞黑盒子 v2：不靠物理碰撞事件，直接偵測「有別的車疊進我的車身」
    private float lastCrashLogTime = -999f;
    private void DetectOverlapCrash()
    {
        if (Time.time - lastCrashLogTime < 2f) return; // 同一事故不要洗版

        Collider[] hits = Physics.OverlapBox(
            transform.position + Vector3.up * 0.5f,
            new Vector3(0.8f, 0.5f, 1.8f), // 約半台車身的範圍，依你的車尺寸微調
            transform.rotation, detectionLayer);

        foreach (var h in hits)
        {
            if (IsMyOwnCollider(h)) continue;

            bool isVehicle = IsPlayerCollider(h)
                 || h.GetComponentInParent<ReverseThreatAICar>() != null
                || h.GetComponentInParent<CarNavigation>() != null;
            if (!isVehicle) continue;

            lastCrashLogTime = Time.time;
            UnityEngine.Debug.LogError(
                $"💥 [重疊黑盒子] {name} 與 {h.transform.root.name} 車身重疊！\n" +
                $"speed={currentHealthSpeed:F1} target={calculatedTargetSpeed:F1} nearest={nearestForwardObstacleDistance:F1}\n" +
                $"rush={isRushingOut} yieldAmb={isYieldingForAmbulance} redLight={isWaitingAtTrafficLight} " +
                $"reverse={isReverseAction}/{isForcedReversing} brakeAct={isBrakingAction} bezier={isBezierActive}");
            break;
        }
    }
    private void OnDisable()
    {
        if (currentWaypointNode != null && currentWaypointNode.isOccupied && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
        {
            currentWaypointNode.isOccupied = false;
            currentWaypointNode.occupiedByVehicleID = 0;
        }

        // 車子在事件進行中被回收時的保險：沒註冊過的話這裡是空操作
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(transform);
    }

    public bool CanTakeAction() => !isStaticParked && !isReverseAction && !isForcedReversing && !isBrakingAction && !isRushingOut && !isInCooldown;

    // ── 🎯 危險因子「待命限速」──────────────────────────────
    // PlayerHazardScanner 鎖定這台車之後，先讓它慢下來等玩家追上，
    // 逼近到發動距離才真的演急煞／倒車。-1 = 沒有限速。
    // 實際套用點在 HandleForwardDetection 算完 calculatedTargetSpeed 之後。
    [HideInInspector] public float hazardArmSpeedCap = -1f;

    /// <summary>設定待命限速（m/s）。負數等同解除。</summary>
    public void SetHazardArmSpeedCap(float cap) => hazardArmSpeedCap = Mathf.Max(0f, cap);

    /// <summary>解除待命限速，交還速度控制權給正常行駛邏輯。</summary>
    public void ClearHazardArmSpeedCap() => hazardArmSpeedCap = -1f;

    /// <summary>目前是否被雷達鎖定並限速中（供診斷用）</summary>
    public bool IsHazardArmed => hazardArmSpeedCap >= 0f;

    // 🚶 禮讓背景行人的限速（m/s）。-1 = 不禮讓。由 PedestrianYieldSensor 設定，
    //    跟 hazardArmSpeedCap 分開,兩套不會互相覆蓋。套用點在上面 calculatedTargetSpeed 之後。
    [HideInInspector] public float pedestrianYieldCap = -1f;

    /// <summary>禮讓背景行人:傳入「原速的幾成」(0=停,0~1=蠕行),負數=解除。</summary>
    public void SetPedestrianYield(float factor01)
        => pedestrianYieldCap = factor01 < 0f ? -1f : Mathf.Max(0f, factor01) * originalNormalSpeed;

    public bool IsWaitingAtTrafficLight() => isWaitingAtTrafficLight;
    public bool IsYieldingForAmbulanceNow => isYieldingForAmbulance; // 🟢 供回歸淨空檢查排除避讓同伴
    public bool IsCarDetectedAtStopNow => isCarDetectedAtStop; // 🟢 供後車查詢：正在無號誌路口停等橫向來車

    public void TriggerBrakeFromZone() { if (CanTakeAction()) StartCoroutine(ExecuteBrakeRoutine()); }

    IEnumerator ExecuteBrakeRoutine()
    {
        isBrakingAction = true;

        // 🛑 這次是「一般減速」還是「真正的緊急煞車」
        bool isEmergency = !useTwoTierBraking || Random.value < emergencyBrakeChance;

        float decel = isEmergency ? brakeDeceleration : normalBrakeDeceleration;
        float targetSpeed = isEmergency ? 0f : moveSpeed * normalBrakeTargetPercent;
        calculatedTargetSpeed = targetSpeed;

        // 急煞的輪胎尖叫：真實世界這就是玩家察覺前車暴力煞停的第一個線索，
        // 比煞車燈更早進入意識（聲音不需要你正在看著那個方向）。
        // 用急煞專屬的短音，不跟側切共用 —— 側切那段是長滑行，套在急煞上很不對。
        //
        // 🛑 只有緊急煞車才叫：輪胎會叫是因為減速度超過抓地極限，
        //    4 m/s² 的一般減速物理上不會叫，配上尖叫聲會跟側切那顆一樣「怪怪的」。
        if (isEmergency) PlayBrakeScreech();

        // Silent 註冊：前方突發事件，事前提示會毀掉突發性，後照鏡方位也不對
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.RegisterHazard("前車急煞", transform, HazardStyle.Silent);

        // 🛑 主動把速度拉到 0。
        //
        //    這裡不能只設 calculatedTargetSpeed —— 把它積分進 currentHealthSpeed 的那一行
        //    （Update 裡的 Mathf.MoveTowards）條件是 !isBrakingAction，一進急煞就被自己擋掉，
        //    所以 calculatedTargetSpeed = 0 永遠讀不進去。車會用原本的速度滑完整個
        //    brakeDuration —— 煞車燈有亮、輪胎聲有響，就是不會減速，玩家完全感覺不到。
        //
        //    倒車那支 ExecuteReverseRoutine 是自己推位移所以沒這個問題，急煞少了這一步。
        float brakeTimer = 0f;
        while (brakeTimer < brakeDuration)
        {
            currentHealthSpeed = Mathf.MoveTowards(currentHealthSpeed, targetSpeed, decel * Time.deltaTime);
            brakeTimer += Time.deltaTime;
            yield return null;
        }

        // 🛑 維持煞停狀態一段時間才恢復。
        //    煞完立刻走的話玩家還在反應、前車就開了 —— 看起來像「煞假的」。
        //    真實情況是前車要等行人走完才起步，而且這段停等本身就是教材：
        //    前車停著代表狀況還沒解除，不該急著超它。
        float holdTimer = 0f;
        while (holdTimer < brakeHoldSeconds)
        {
            currentHealthSpeed = Mathf.MoveTowards(currentHealthSpeed, targetSpeed, decel * Time.deltaTime);
            calculatedTargetSpeed = targetSpeed;
            holdTimer += Time.deltaTime;
            yield return null;
        }

        isBrakingAction = false;

        // 多等一段時間才結算，避免把「慢半拍但有煞車」誤判成沒反應
        yield return new WaitForSeconds(hazardReactionWindowSeconds);
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(transform);

        isInCooldown = true;
        yield return new WaitForSeconds(cooldownTime);
        isInCooldown = false;
    }

    public void TriggerReverseFromZone() { if (CanTakeAction()) StartCoroutine(ExecuteReverseRoutine()); }

    IEnumerator ExecuteReverseRoutine()
    {
        isReverseAction = true;
        calculatedTargetSpeed = 0; currentHealthSpeed = 0;

        // Silent 註冊：車在你前方倒過來，同樣是前方突發事件
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.RegisterHazard("前車倒車", transform, HazardStyle.Silent);

        yield return new WaitForSeconds(reverseWaitTime);

        float timer = 0;
        while (timer < reverseDuration)
        {
            transform.position -= transform.forward * reverseSpeed * Time.deltaTime;
            timer += Time.deltaTime;

            // 【新增這行：倒車時亮燈】
            SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, true, false);

            yield return null;
        }

        isReverseAction = false;

        // 【新增這行：倒車結束關燈】
        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, false, false);

        // 多等一段時間才結算玩家反應
        yield return new WaitForSeconds(hazardReactionWindowSeconds);
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(transform);

        isInCooldown = true;
        yield return new WaitForSeconds(cooldownTime);
        isInCooldown = false;
    }
    // 2. 連鎖跟隨版本
    IEnumerator ExecuteChainReverseRoutine(ReverseThreatAICar frontCar)
    {
        isForcedReversing = true;

        while (frontCar != null &&
               (frontCar.isReverseAction || frontCar.isForcedReversing))
        {
            float distToFront =
                Vector3.Distance(transform.position,
                                 frontCar.transform.position);

            float frontSpeed =
                Mathf.Abs(frontCar.currentHealthSpeed);

            // 平滑補償
            float speedOffset =
                (safeDistance - distToFront) * 2.0f;

            float targetSpeed =
                Mathf.Clamp(frontSpeed + speedOffset,
                            0.1f,
                            10f);

            transform.position -=
                transform.forward * targetSpeed * Time.deltaTime;

            currentHealthSpeed = -targetSpeed;

            SetIndicatorAndBrakeLogic(
                IndicatorState.None,
                0f,
                false,
                true,
                false);

            yield return null;
        }

        // 恢復正常行駛
        isForcedReversing = false;
        currentHealthSpeed = 0f;

        SetIndicatorAndBrakeLogic(
            IndicatorState.None,
            0f,
            false,
            false,
            false);

        GetComponent<Rigidbody>().WakeUp();

        //GotoNextWaypoint();
    }
    /// <summary>
    /// 由玩家端的 PlayerReverseBroadcaster 直接呼叫，強制開始跟著玩家倒車。
    ///
    /// 繞過 CheckPlayerReverseImmediate 的所有門檻（距離、方位、橫向、介面判斷）——
    /// 那一連串條件任何一個沒過都會靜默失敗，很難查。改成由玩家端統一決定要通知誰，
    /// 判定邏輯集中在一個地方，好調也好懂。
    /// </summary>
    public void ForceChainReverseFromPlayer()
    {
        // 已經在倒車 / 正在演其他危險因子就不重複觸發。
        // 每個原因都印出來 —— 原本是一行 return，被擋掉時完全看不出是哪個條件。
        if (isForcedReversing) { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 已在倒車中，忽略</color>", this); return; }
        if (isReverseAction)   { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 正在演倒車危險因子，忽略</color>", this); return; }
        if (isStaticParked)    { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 是靜態停放車，忽略</color>", this); return; }
        if (isRushingOut)      { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 正在側切衝出，忽略</color>", this); return; }

        UnityEngine.Debug.Log($"<color=lime>🔙 {name} 通過所有條件，啟動倒車協程</color>", this);
        StartCoroutine(ExecuteChainReverseFromPlayer(GetPlayerTransform()));
    }

    /// <summary>
    /// 廣播器直接把玩家 Transform 傳進來的版本。
    /// 不依賴 GetPlayerTransform() 那個 static 快取 —— 快取失效時協程會空跑一輪就結束，
    /// 而且完全沒有跡象，非常難查。
    /// </summary>
    public void ForceChainReverseFromPlayer(Transform playerTf)
    {
        if (isForcedReversing) { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 已在倒車中，忽略</color>", this); return; }
        if (isReverseAction)   { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 正在演倒車危險因子，忽略</color>", this); return; }
        if (isStaticParked)    { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 是靜態停放車，忽略</color>", this); return; }
        if (isRushingOut)      { UnityEngine.Debug.Log($"<color=grey>🔙 {name} 正在側切衝出，忽略</color>", this); return; }
        if (playerTf == null)  { UnityEngine.Debug.LogWarning($"🔙 {name} 廣播器傳進來的玩家 Transform 是 null", this); return; }

        UnityEngine.Debug.Log($"<color=lime>🔙 {name} 通過所有條件，啟動倒車協程（玩家由廣播器指定）</color>", this);
        StartCoroutine(ExecuteChainReverseFromPlayer(playerTf));
    }

    // ─────────────────────────────────────────────────────────────
    // 🔙 玩家倒車「位移跟隨」：玩家退多少，這台車就退多少
    //
    // 跟上面那組協程的差別：協程是自己算一個 targetSpeed 每幀推車，
    // 速度來源是「離玩家多遠」而不是「玩家實際退了多少」，還被
    // minChainReverseSpeed 鉗了下限 —— 結果玩家掛 R 檔停著不動，
    // 車照樣以 1.8 m/s 自己往後滑。
    //
    // 這組改成純被動：位移完全由 PlayerReverseBroadcaster 逐幀餵進來，
    // 玩家不動就是 0，一格都不會動。速度公式與下限在這條路徑上不再參與。
    // ─────────────────────────────────────────────────────────────
    [HideInInspector] public bool isFollowingPlayerReverse = false;

    /// <summary>
    /// 廣播器要把這台車納入跟隨名單前呼叫。
    /// 回傳 false = 這台車現在有更重要的事要做（演危險因子／避讓救護車／淡出中），不要動它。
    /// 資格判斷寫在車子這一側，廣播器不需要看得到這些 private 旗標。
    /// </summary>
    public bool BeginPlayerReverseFollow() => BeginPlayerReverseFollow(out _);

    /// <summary>附帶拒絕原因的版本，給廣播器印診斷用。被擋掉時能一眼看出是哪一關。</summary>
    public bool BeginPlayerReverseFollow(out string reason)
    {
        reason = null;
        if (isFollowingPlayerReverse) return true;

        // 🚑 避讓／回歸救護車中的車絕對不搶 —— 那套邏輯有自己的狀態機
        if (isYieldingForAmbulance) { reason = "正在避讓救護車";            return false; }
        if (isReturningFromYield)   { reason = "正在回歸主車道";            return false; }
        if (isForcedReversing)      { reason = "已被其他連鎖倒車接管";      return false; }
        if (isReverseAction)        { reason = "正在演倒車危險因子";        return false; }
        if (isBrakingAction)        { reason = "正在演急煞危險因子";        return false; }
        if (isRushingOut)           { reason = "正在側切衝出";              return false; }
        if (isStaticParked)         { reason = "是路邊靜態停放車";          return false; }
        if (isFadingOut)            { reason = "正在淡出回收";              return false; }

        isFollowingPlayerReverse = true;
        isForcedReversing = true;      // 沿用既有旗標暫停這台車自己的行進邏輯
        currentHealthSpeed = 0f;
        calculatedTargetSpeed = 0f;
        return true;
    }

    /// <summary>
    /// 由廣播器每幀呼叫，meters = 玩家這一幀沿自己車尾方向的實際位移量。
    /// 沿「自己的」forward 退，所以車子維持在自己的車道方向上，不會被拖成斜的。
    /// </summary>
    public void ApplyPlayerReverseDelta(float meters)
    {
        if (!isFollowingPlayerReverse) return;

        bool moving = meters > 0.0001f;
        if (moving) transform.position -= transform.forward * meters;

        currentHealthSpeed = (moving && Time.deltaTime > 0f) ? -meters / Time.deltaTime : 0f;
        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, moving, false);
    }

    /// <summary>玩家切回前進／離開範圍／被淘汰時呼叫，交還控制權給正常導航。</summary>
    public void EndPlayerReverseFollow()
    {
        if (!isFollowingPlayerReverse) return;

        isFollowingPlayerReverse = false;
        isForcedReversing = false;
        currentHealthSpeed = 0f;
        calculatedTargetSpeed = 0f;
        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, false, false);

        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null) rb.WakeUp();
    }

    IEnumerator ExecuteChainReverseFromPlayer(Transform chainPlayerTf)
    {
        isForcedReversing = true;
        GameObject player = chainPlayerTf != null ? chainPlayerTf.gameObject : null;
        IPlayerVehicleState playerState = player != null ? player.GetComponent<IPlayerVehicleState>() : null;

        if (player == null)
        {
            // GetPlayerTransform 是 static 快取，取不到的話整個 while 直接跳過，
            // 協程等於什麼都沒做就結束 —— 這種情況原本完全沒有任何跡象
            UnityEngine.Debug.LogWarning($"🔙 {name} 協程啟動了但 GetPlayerTransform() 回傳 null，無法倒車", this);
            isForcedReversing = false;
            yield break;
        }

        int frame = 0;
        while (player != null)
        {
            float distToPlayer = Vector3.Distance(transform.position, player.transform.position);
            // 用連鎖專屬距離，不能用 detectionDistance（10m）——
            // 否則車一退開超過 10m 連鎖就中斷，玩家還在倒但後車已經不退了
            if (distToPlayer > playerReverseChainDistance)
            {
                UnityEngine.Debug.Log($"<color=grey>🔙 {name} 距離 {distToPlayer:F1} > {playerReverseChainDistance}，連鎖結束</color>", this);
                break;
            }

            Rigidbody playerRb = player.GetComponent<Rigidbody>();
            float playerSpeed = playerRb != null ? playerRb.velocity.magnitude : 0f;

            // 🟢 優先用介面判斷是否已切回前進
            bool playerSwitchedToForward = playerState != null
                ? !playerState.IsReversing
                : (playerRb != null && playerSpeed > 0.3f && Vector3.Dot(playerRb.velocity.normalized, playerRb.transform.forward) > 0.2f);

            if (playerSwitchedToForward)
            {
                UnityEngine.Debug.Log($"<color=grey>🔙 {name} 玩家已切回前進，連鎖結束" +
                                      $"（介面={(playerState != null ? "有" : "無")}）</color>", this);
                break;
            }

            // 這裡拉開距離的速度計算維持原樣，用 playerSpeed 或 reverseSpeed 都可以
            float targetSpeed;
            if (distToPlayer < safeDistance)
            {
                float pushBackSpeed = (1f - (distToPlayer / safeDistance)) * 7f;
                targetSpeed = Mathf.Max(pushBackSpeed, 0.5f);
            }
            else
            {
                float followFactor = Mathf.Clamp01((distToPlayer - safeDistance) / (detectionDistance - safeDistance));
                targetSpeed = Mathf.Lerp(0f, playerSpeed * 0.9f, followFactor);
            }

            // 🔙 至少要跟上玩家的倒車速度，否則距離會一直被拉近。
            //    上面那段在 5~10m 之間是「距離越遠退越快」，方向剛好相反 ——
            //    玩家從 13m 逼近到 5m 的過程中它只用下限速度在退，當然追不上。
            float playerReverseSpeed = playerRb != null
                ? Mathf.Max(0f, -Vector3.Dot(playerRb.velocity, player.transform.forward))
                : 0f;
            targetSpeed = Mathf.Max(targetSpeed, playerReverseSpeed * chainKeepUpFactor);

            targetSpeed = Mathf.Clamp(targetSpeed, minChainReverseSpeed, 10f);

            // 前 3 幀印出實際位移，確認協程真的在推動車子
            if (frame < 3)
            {
                frame++;
                Vector3 before = transform.position;
                transform.position -= transform.forward * targetSpeed * Time.deltaTime;
                UnityEngine.Debug.Log($"<color=lime>🔙 {name} 倒車中｜距離 {distToPlayer:F1}m｜" +
                                      $"速度 {targetSpeed:F2}｜本幀位移 {Vector3.Distance(before, transform.position):F4}m</color>", this);
            }
            else
            {
                transform.position -= transform.forward * targetSpeed * Time.deltaTime;
            }
            currentHealthSpeed = -targetSpeed;
            SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, true, false);

            yield return null;
        }

        isForcedReversing = false;
        currentHealthSpeed = 0f;
        calculatedTargetSpeed = 0f;

        SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, false, false, false);
        GetComponent<Rigidbody>().WakeUp();

        yield return new WaitForSeconds(0.3f);

        // 🟢 只有真的到點附近才前進，避免倒車後距離變遠卻被強制跳點
        float distToNode = currentWaypointNode != null
            ? Vector3.Distance(transform.position, currentWaypointNode.transform.position)
            : Mathf.Infinity;

        if (distToNode < arrivalDistance)
        {
            GotoNextWaypoint();
        }
    }


    void ManageAutoIndicators(bool isBraking)
    {
        // 🚑 避讓中亮右燈(靠邊);回歸中亮左燈(切回主車道)——強制蓋過自動判定,用原本的閃爍脈衝驅動
        if (isYieldingForAmbulance || isReturningFromYield)
        {
            currentIndicatorMode = isReturningFromYield ? IndicatorState.Left : IndicatorState.Right;
            turnOffTimer = turnOffDelay;

            pulseTime += Time.deltaTime * blinkFadeSpeed;
            float yieldBlinkNorm = pulseTime % 1.0f;
            currentBlinkWeight = Mathf.Lerp(currentBlinkWeight, (yieldBlinkNorm < lightOnRatio) ? 1f : 0f, Time.deltaTime * 6.0f);
            SetIndicatorAndBrakeLogic(currentIndicatorMode, currentBlinkWeight * maxEmissionIntensity, isBraking);
            return;
        }

        // 🔶 側切預告：靜停等觸發的側切車，在玩家逼近時先打方向燈告知「我要出來了」。
        //    刻意放在避讓/回歸判斷「之後」—— 兩者同時成立時以避讓為準，不動核心邏輯。
        if (isTelegraphing)
        {
            currentIndicatorMode = telegraphSide < 0f ? IndicatorState.Left : IndicatorState.Right;
            turnOffTimer = turnOffDelay;

            pulseTime += Time.deltaTime * blinkFadeSpeed;
            float telegraphBlinkNorm = pulseTime % 1.0f;
            currentBlinkWeight = Mathf.Lerp(currentBlinkWeight, (telegraphBlinkNorm < lightOnRatio) ? 1f : 0f, Time.deltaTime * 6.0f);
            SetIndicatorAndBrakeLogic(currentIndicatorMode, currentBlinkWeight * maxEmissionIntensity, isBraking);
            return;
        }

        if (currentWaypointNode == null || currentWaypointNode.transform == null)
        {
            SetIndicatorAndBrakeLogic(IndicatorState.None, 0f, isBraking);
            return;
        }

        IndicatorState targetMode = IndicatorState.None;
        Transform targetWP = currentWaypointNode.transform;
        float distanceToNextWP = Vector3.Distance(transform.position, targetWP.position);
        float dynamicBlinkDistance = Mathf.Max(currentHealthSpeed, 5f) * blinkSecondsAhead;

        if (distanceToNextWP < dynamicBlinkDistance)
        {
            TrafficPathNode nextWP = GetPlannedNode(1); // 🟢 讀實際計畫，分岔路口不再打錯邊
            if (nextWP != null && nextWP.transform != null)
            {
                // 🟢 定錨防抖：改用「上一節點→目前節點」的固定路徑向量當基準，
                // 不受車身晃動影響，燈號不會在門檻邊緣來回跳
                Vector3 currentToNext = lastWaypointNode != null
                    ? (targetWP.position - lastWaypointNode.transform.position)
                    : (targetWP.position - transform.position);
                Vector3 nextToFuture = nextWP.transform.position - targetWP.position;

                currentToNext.y = 0; nextToFuture.y = 0;
                float turnAngle = Vector3.SignedAngle(currentToNext, nextToFuture, Vector3.up);
                if (turnAngle > turnAngleThreshold) targetMode = IndicatorState.Right;
                else if (turnAngle < -turnAngleThreshold) targetMode = IndicatorState.Left;
            }
        }

        if (targetMode != IndicatorState.None)
        {
            currentIndicatorMode = targetMode;
            turnOffTimer = turnOffDelay;
        }
        else
        {
            if (turnOffTimer > 0f) turnOffTimer -= Time.deltaTime;
            else currentIndicatorMode = IndicatorState.None;
        }

        if (currentIndicatorMode != IndicatorState.None)
        {
            pulseTime += Time.deltaTime * blinkFadeSpeed;
            float normalizedTime = pulseTime % 1.0f;
            currentBlinkWeight = Mathf.Lerp(currentBlinkWeight, (normalizedTime < lightOnRatio) ? 1f : 0f, Time.deltaTime * 6.0f);
            SetIndicatorAndBrakeLogic(currentIndicatorMode, currentBlinkWeight * maxEmissionIntensity, isBraking);
        }
        else
        {
            pulseTime = 0f;
            currentBlinkWeight = Mathf.Lerp(currentBlinkWeight, 0f, Time.deltaTime * 4.0f);
            SetIndicatorAndBrakeLogic(IndicatorState.None, currentBlinkWeight * maxEmissionIntensity, isBraking);
        }
    }

    void SetIndicatorAndBrakeLogic(IndicatorState mode, float blinkIntensity, bool isBraking, bool isReversing = false, bool isAtTrafficLight = false)
    {
        /// A. 處理倒車燈 (修改顏色邏輯)
        if (reverseLightRenderers != null)
        {
            // 定義一個高亮度的白色 (HDR Intensity = 3)
            // 這樣在任何環境下，這個燈都會非常明顯
            Color reverseEmissionColor = isReversing ? new Color(3f, 3f, 3f) : Color.black;

            foreach (var renderer in reverseLightRenderers)
            {
                if (renderer != null)
                {
                    // 設定顏色
                    renderer.material.SetColor("_EmissionColor", reverseEmissionColor);

                    // 強制啟用該材質的 Emission 關鍵字 (防止有些 Shader 被關閉)
                    if (isReversing) renderer.material.EnableKeyword("_EMISSION");
                    else renderer.material.DisableKeyword("_EMISSION");
                }
            }
        }

        // B. 處理煞車燈 (包含平滑過渡與緊急閃爍判斷)

        // 1. 計算平滑權重
        currentBrakeWeight = Mathf.Lerp(currentBrakeWeight, isBraking ? 1.0f : 0f, Time.deltaTime * (isBraking ? brakeFadeInSpeed : brakeFadeOutSpeed));

        // 2. 獲取當前速度 (判斷是否觸發閃爍的門檻)
        float currentSpeed = GetComponent<Rigidbody>() != null ? GetComponent<Rigidbody>().velocity.magnitude : 0f;

        // 3. 邏輯判斷
        if (isBraking && (currentBrakeWeight > emergencyThreshold) && (currentSpeed > 5.0f))
        {
            // 緊急狀態：高速且急煞時，使用閃爍效果
            float flash = Mathf.Sin(Time.time * emergencyBlinkFrequency * Mathf.PI * 2) > 0 ? 1.0f : 0f;
            UpdateSingleBrakeLight(leftBrakeMat, flash);
            UpdateSingleBrakeLight(rightBrakeMat, flash);
        }
        else
        {
            // 正常狀態：包含正常煞車與減速，使用平滑權重實現漸變效果
            UpdateSingleBrakeLight(leftBrakeMat, currentBrakeWeight);
            UpdateSingleBrakeLight(rightBrakeMat, currentBrakeWeight);
        }

        // C. 方向燈邏輯 (保持原有設定)
        float leftOrange = (mode == IndicatorState.Left) ? blinkIntensity : 0f;
        float rightOrange = (mode == IndicatorState.Right) ? blinkIntensity : 0f;

        UpdateLightEmission(leftFrontMat, leftOrange, indicatorBaseColor);
        UpdateLightEmission(leftRearMat, leftOrange, indicatorBaseColor);
        UpdateLightEmission(rightFrontMat, rightOrange, indicatorBaseColor);
        UpdateLightEmission(rightRearMat, rightOrange, indicatorBaseColor);
    }
    // 🟢 新增：救護車已經離開/消失，這台車如果還在避讓，5秒後自動銷毀讓補車機制接手
    private float ambulanceGoneTimestamp = -1f;
    public float destroyAfterAmbulanceGoneDelay = 5f;

    public void ScheduleDestroyAfterAmbulanceLeft(float delay)
    {
        // 🟢 只有「現在」真的還在避讓/死等，才排定銷毀
        if (isYieldingForAmbulance || isParkedAtYieldNode)
        {
            ambulanceGoneTimestamp = Time.time;
            destroyAfterAmbulanceGoneDelay = delay;
            UnityEngine.Debug.Log($"<color=yellow>⏱ {name} 目前仍在避讓，已排定 {delay}秒後銷毀</color>");
        }
    }


    void UpdateSingleBrakeLight(Material mat, float weight)
    {
        if (mat == null) return;
        if (weight > 0.01f) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", brakeOnColor * weight); }
        else { mat.SetColor("_EmissionColor", Color.clear); mat.DisableKeyword("_EMISSION"); }
    }

    void UpdateLightEmission(Material mat, float weight, Color baseColor)
    {
        if (mat == null) return;
        mat.EnableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", baseColor * Mathf.Max(weight, 0.03f));
    }

    public void SetYieldForAmbulance(bool yield, Transform ambulanceTF = null)
    {
        if (yield)
        {
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
                        UnityEngine.Debug.Log($"<color=grey>🚑 {name} 在救護車左側 {-lateralL:F1}m(內側車道) → 豁免避讓,維持原車道 —— 往右讓反而會擋住它</color>", this);
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

            if (isYieldingForAmbulance)
                return;
            // 🟢 新增：剛解除避讓不久，暫時拒絕重新進入避讓狀態，避免被連鎖拖回
            if (Time.time - lastYieldReleaseTime < yieldReEntryCooldown)
            {
                UnityEngine.Debug.Log($"<color=cyan>🚑 {name} 剛解除避讓，暫時忽略重複的避讓訊號</color>");
                return;
            }
            StopAllCoroutines();

            isBrakingAction = false;
            isReverseAction = false;
            isForcedReversing = false;
            isInCooldown = false;
            isWaitingAtTrafficLight = false;

            if (currentHealthSpeed < 0f)
            {
                currentHealthSpeed = 0f;
            }

            isYieldingForAmbulance = true;
            ambulancePassedTimer = 0f;

            if (ambulanceTF != null)
                activeAmbulanceTransform = ambulanceTF;
        }
        else
        {
            // 🔧 忽略外部的「解除」訊號(比照灰車 CarNavigation 的作法)。
            //
            //    AmbulanceAI.OnTriggerExit 會在車子離開救護車觸發盒時發這個訊號,
            //    但「靠邊讓道」本身就會把車移出那個盒子 —— 讓得越確實、離開得越快。
            //    舊版在這裡把 activeAmbulanceTransform 設成 null,而 HandleNavigation
            //    的釋放邏輯遇到 null 會判定「救護車消失」→ 立刻回歸,
            //    結果就是:偏移完成→轉正→不到一秒切回主車道停在路中間擋住救護車。
            //
            //    回歸時機一律由 HandleNavigation 的距離判定決定
            //    (救護車超過車頭 ambulanceReturnAheadDistance 公尺並維持 0.8 秒)。
            //    救護車若真的被銷毀,activeAmbulanceTransform 自然會變成 null,
            //    那條「意外消失」的保險依然有效。
            return;
        }
    }
    // ==================== 🪨 靜停姿態鎖 (路邊等觸發的側切車:撞不動) ====================
    //
    // 原本靜停只是「腳本不驅動它」(Update 直接 return),Rigidbody 仍是動態的
    // → 玩家車(尤其 G923 方向盤實際車速較高)側方停車擦撞,車就被物理推歪、推走,
    //   之後被觸發側切時起點已經偏掉。
    //
    // 這裡在靜停期間把 Rigidbody 轉成 Kinematic + FreezeAll,任何碰撞衝量都不再作用在它身上
    // (玩家車照樣會被正常彈開,手感等同撞到牆),並記住停放姿態每幀釘回去當雙保險。
    // 觸發側切(TriggerIntersectionRush)時原樣還原,後續衝出流程完全不受影響。

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

    /// <summary>🪨 進入靜停:記下停放姿態並關閉物理反應,撞不動</summary>
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
            rb.isKinematic = true;                            // 不再接受任何碰撞衝量
            rb.constraints = RigidbodyConstraints.FreezeAll;  // 雙保險
        }
        parkLockActive = true;
    }

    /// <summary>🔶 側切預告挪動車身後，把靜停錨點同步成目前姿態，避免被鎖拉回原位</summary>
    private void SyncParkAnchorToCurrentPose()
    {
        if (!parkLockActive) return;
        parkedAnchorPos = transform.position;
        parkedAnchorRot = transform.rotation;
    }

    /// <summary>🪨 解除靜停鎖,把 Rigidbody 設定原樣還原(供側切觸發呼叫)</summary>
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

    /// <summary>🪨 靜停期間每幀把車釘回停放原點(擋掉非碰撞來源的位移)</summary>
    private void HoldParkedPose()
    {
        if (!parkLockActive) return;

        if ((transform.position - parkedAnchorPos).sqrMagnitude > parkedPoseTolerance * parkedPoseTolerance)
            transform.position = parkedAnchorPos;
        if (Quaternion.Angle(transform.rotation, parkedAnchorRot) > 0.1f)
            transform.rotation = parkedAnchorRot;
    }

    /// <summary>🪨 同步靜停狀態 → 鎖定狀態(Inspector 手改、延遲補位生成、車輛池回收都吃得到)</summary>
    private void SyncParkLockState()
    {
        if (isStaticParked) ApplyParkLock();
        else if (parkLockActive) ReleaseParkLock();
    }

    protected virtual void LateUpdate()
    {
        // 在所有 Update 與物理插值之後再確認一次,確保靜停車這一幀畫出來的位置就是停放原點
        if (isStaticParked) HoldParkedPose();
    }

    // 🟢 新增：強制讓車輛立刻離開避讓狀態，回到原本路徑
    public void ForceLeaveYieldNode()
    {
        if (!isYieldingForAmbulance && !isParkedAtYieldNode) return;

        UnityEngine.Debug.Log($"<color=orange>🚨 {name} 被強制要求離開避讓狀態</color>");

        isYieldingForAmbulance = false;
        isParkedAtYieldNode = false;
        isUsingOffsetFallback = false;
        activeAmbulanceTransform = null;
        ambulancePassedTimer = 0f;
        parkedAtYieldTimer = 0f;
        lastYieldReleaseTime = Time.time;

        if (savedNormalWaypointNode != null)
        {
            if (currentWaypointNode != null && currentWaypointNode.occupiedByVehicleID == this.gameObject.GetInstanceID())
            {
                currentWaypointNode.isOccupied = false;
                currentWaypointNode.occupiedByVehicleID = 0;
            }
            currentWaypointNode = savedNormalWaypointNode;
            savedNormalWaypointNode = null;
        }
    }
    /// <param name="isPlayerSideCutIn">
    /// true  = 針對玩家的「側邊切入」危險因子（由 ReverseTriggerController 經 TTC 判定後呼叫）
    /// false = 路口淨空衝刺（IntersectionManager 呼叫，跟玩家無關）
    ///
    /// 這兩者共用同一套衝刺動作，但只有前者該播輪胎聲、註冊危險因子、瞄準玩家車道。
    /// 沒有這個區分的話，路口淨空的車也會發出輪胎聲並被記成「側邊衝出未反應」。
    /// </param>
    /// <summary>
    /// 「是路邊演員車，而且現在沒在忙」。
    ///
    /// CanTakeAction() 把「是不是停放車」和「有沒有在忙」綁在同一個判斷裡，
    /// 而它第一個條件就是 !isStaticParked —— 所以路邊演員車永遠回傳 false。
    /// 觸發器為了選得到演員車，只好整個豁免 CanTakeAction()，
    /// 結果連「還在冷卻」「正在演別的」也一起豁免掉了。
    /// 這個屬性把兩件事拆開：停放狀態照舊豁免，忙碌狀態照樣要擋。
    /// </summary>
    public bool IsIdleParked => isStaticParked
        && !isRushingOut && !isReverseAction && !isBrakingAction
        && !isForcedReversing && !isInCooldown && !isFadingOut;

    // ==================== 🔶 側切預告 ====================

    /// <summary>
    /// 量出軸距，供側切預告的轉向幾何使用。
    /// 直接從輪子的 Transform 量世界距離，所以各 prefab 的縮放差異會自動吃進去，
    /// 不用逐台在 Inspector 填。Inspector 有填 telegraphWheelbase 就以它為準。
    /// </summary>
    private void ResolveWheelbase()
    {
        if (telegraphWheelbase > 0.01f) { resolvedWheelbase = telegraphWheelbase; return; }

        if (leftFrontWheel != null && leftRearWheel != null)
        {
            float d = Vector3.Distance(leftFrontWheel.position, leftRearWheel.position);
            if (d > 0.5f) { resolvedWheelbase = d; return; }
        }
        if (rightFrontWheel != null && rightRearWheel != null)
        {
            float d = Vector3.Distance(rightFrontWheel.position, rightRearWheel.position);
            if (d > 0.5f) { resolvedWheelbase = d; return; }
        }

        resolvedWheelbase = 2.7f;   // 輪子沒綁 → 退回一般房車軸距
    }

    /// <summary>
    /// 玩家逼近到「預告 TTC」時由 ReverseTriggerController 呼叫：開始打方向燈、前輪轉出，
    /// 讓玩家有機會提早察覺。真正的衝出仍由既有的 rushTriggerTTC 決定，時序邏輯完全沒動。
    /// </summary>
    public void BeginSideCutInTelegraph(Transform player = null)
    {
        if (!sideCutTelegraphEnabled) return;
        if (isTelegraphing) return;
        // 只有「路邊靜停等觸發」的演員車才需要預告；已經在跑的車不適用
        if (!isStaticParked) return;
        if (isRushingOut || isReverseAction || isBrakingAction
            || isForcedReversing || isInCooldown || isFadingOut) return;

        telegraphSide         = ResolveTelegraphSide(player);
        telegraphOriginPos    = transform.position;
        telegraphOriginRot    = transform.rotation;
        telegraphCreepCurrent = 0f;
        telegraphElapsed      = 0f;
        isTelegraphing        = true;
        turnOffTimer          = turnOffDelay;

        // 🔶 閃爍相位歸零 —— pulseTime 是全車共用的累加器，不歸零的話預告開始時它停在哪個相位
        //    完全隨機：最壞情況落在熄滅段，方向燈要再等約 0.25 秒（週期 1/blinkFadeSpeed，
        //    熄滅段佔 1-lightOnRatio）才第一次亮，再加淡入約 0.2 秒。
        //    那 0.45 秒正是「看起來煞車燈先亮」的主因 —— 兩個燈本來就是同一幀開始的。
        pulseTime = 0f;
        currentBlinkWeight = 0f;   // 淡入從 0 起算，第一次亮才會是完整的一次閃爍
    }

    /// <summary>
    /// 判斷這台車要往哪一側切出：-1 = 左、+1 = 右。
    /// 每台側切車停放的方位、要匯入的車道都不一樣，寫死方向會有一半的車打錯燈。
    /// </summary>
    private float ResolveTelegraphSide(Transform player)
    {
        if (!telegraphAutoSide)
            return telegraphSteerAngle < 0f ? -1f : 1f;

        // 1️⃣ 最準：看它接下來要去的路點在車身的哪一側 —— 那就是它要匯入的車道
        if (currentWaypointNode != null && currentWaypointNode.transform != null)
        {
            Vector3 toNode = currentWaypointNode.transform.position - transform.position;
            toNode.y = 0f;
            if (toNode.sqrMagnitude > 0.25f)
            {
                float d = Vector3.Dot(toNode.normalized, transform.right);
                if (Mathf.Abs(d) > 0.15f) return Mathf.Sign(d);
            }
        }

        // 2️⃣ 備援：玩家在哪一側就往哪一側切 —— 側切的目的本來就是切進玩家的車道
        if (player != null)
        {
            Vector3 toPlayer = player.position - transform.position;
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude > 0.25f)
            {
                float d = Vector3.Dot(toPlayer.normalized, transform.right);
                if (Mathf.Abs(d) > 0.15f) return Mathf.Sign(d);
            }
        }

        // 3️⃣ 都判不出來（路點正前方、玩家正後方）→ 靠右停放最常見，預設往左
        return -1f;
    }

    /// <summary>取消預告（事件被取消或逾時清場時呼叫）：燈熄、輪子回正、車身退回原位</summary>
    public void EndSideCutInTelegraph()
    {
        if (!isTelegraphing) return;
        isTelegraphing = false;

        // 探出去的車身要退回來，否則反覆「預告→取消」會讓車一路飄進車道。
        // 車頭也要一起轉回去 —— 現在蹭出去會改變朝向，只還原位置的話取消幾次就越歪越多。
        if (telegraphCreepCurrent > 0.001f)
        {
            transform.position = telegraphOriginPos;
            transform.rotation = telegraphOriginRot;
            SyncParkAnchorToCurrentPose();
        }
        telegraphCreepCurrent = 0f;

        // 前輪轉回正 —— UpdateSideCutTelegraph 之後不再寫入輪子，這裡不歸零會一直斜著
        telegraphSteerCurrent = 0f;
        if (leftFrontWheel != null)
            leftFrontWheel.localRotation = Quaternion.Euler(
                leftFrontWheel.localRotation.eulerAngles.x, 0f, leftFrontWheel.localRotation.eulerAngles.z);
        if (rightFrontWheel != null)
            rightFrontWheel.localRotation = Quaternion.Euler(
                rightFrontWheel.localRotation.eulerAngles.x, 0f, rightFrontWheel.localRotation.eulerAngles.z);
    }

    /// <summary>
    /// 預告期間把前輪轉出去。刻意獨立成一支：靜停的車不會跑到正常的移動／轉向流程，
    /// 那邊的 currentSteerAngle 套用不到，所以這裡自己算自己套。
    /// </summary>
    private void UpdateSideCutTelegraph()
    {
        // 不在預告 → 立刻把輪子的控制權交還給正常轉向邏輯。
        // 這一支排在每幀最後，若還繼續寫入會蓋掉衝出後的真實轉向，造成輪子回正又轉回去的抽動。
        if (!isTelegraphing)
        {
            telegraphSteerCurrent = 0f;
            return;
        }

        telegraphElapsed += Time.deltaTime;

        // ── 前輪轉出 ──
        float steerTarget = telegraphSide * Mathf.Abs(telegraphSteerAngle);
        telegraphSteerCurrent = Mathf.Lerp(telegraphSteerCurrent, steerTarget, Time.deltaTime * telegraphSteerSpeed);


        // ── 車身斜著蹭出去 ──
        // 舊版是「沿 transform.right 直接橫移、車頭完全不轉」，車身與行進方向不平行 ——
        // 那在物理上就是側滑，所以看起來是漂移的。而且前輪轉幾度跟車身怎麼走毫無關聯，
        // telegraphSteerAngle 純粹是裝飾。
        //
        // 改用腳踏車模型：車只沿自己的車頭方向前進，前輪轉角決定車頭每幀轉多少
        //     Δψ = (d / L) · tan(δ)
        // 橫向位移變成「往前開 + 前輪有轉角」的結果，而不是獨立指定的橫移量 ——
        // 這才是真車切出來的方式，而且 telegraphSteerAngle 從此會真的影響切出的弧度。
        //
        // 靜停鎖每幀會把車釘回 parkedAnchorPos/Rot，所以挪完要同步更新錨點，否則會被拉回去。
        // 🔴 方向燈前導段與煞車猶豫段都不蹭 —— 方向燈閃、前輪轉出，但車不動。
        //    真車就是先猶豫一下才鬆煞車，這兩拍也讓玩家有時間把線索看進去。
        //    注意只跳過位移，下面的前輪轉向照寫（轉出來的輪子正是這段的主要線索）。
        if (!TelegraphHoldStill && telegraphCreepSpeed > 0.001f && telegraphMaxCreepForward > 0.001f)
        {
            // 已經往目標側偏出多少（相對出發時的車身座標系量，車頭轉動不會污染這個量測）
            float lateralNow = Vector3.Dot(transform.position - telegraphOriginPos,
                                           telegraphOriginRot * Vector3.right) * telegraphSide;

            bool reachedLateral = telegraphCreepDistance > 0.001f && lateralNow >= telegraphCreepDistance;
            bool reachedForward = telegraphCreepCurrent >= telegraphMaxCreepForward;

            if (!reachedLateral && !reachedForward)
            {
                float d = telegraphCreepSpeed * Time.deltaTime;
                float headingDelta = (d / resolvedWheelbase) * Mathf.Tan(telegraphSteerCurrent * Mathf.Deg2Rad);

                transform.Rotate(0f, headingDelta * Mathf.Rad2Deg, 0f, Space.World);
                transform.position += transform.forward * d;
                telegraphCreepCurrent += d;
                SyncParkAnchorToCurrentPose();

                // 車身在動，輪子就要跟著滾 —— 蹭出來卻輪子不轉會讓整個預告顯得很假。
                // 這裡自己算，不走 HandleAllWheelsRolling()：靜停分支沒有更新 actualSpeed。
                // 滾動存在輪子的 x 軸，下面的轉向只寫 y 軸，兩者不衝突。
                float rollAngle = d * wheelRollSpeedMultiplier * Mathf.Rad2Deg;
                if (leftFrontWheel != null)  leftFrontWheel.Rotate(Vector3.right, rollAngle, Space.Self);
                if (rightFrontWheel != null) rightFrontWheel.Rotate(Vector3.right, rollAngle, Space.Self);
                if (leftRearWheel != null)   leftRearWheel.Rotate(Vector3.right, rollAngle, Space.Self);
                if (rightRearWheel != null)  rightRearWheel.Rotate(Vector3.right, rollAngle, Space.Self);
            }
        }

        if (leftFrontWheel != null)
            leftFrontWheel.localRotation = Quaternion.Euler(
                leftFrontWheel.localRotation.eulerAngles.x, telegraphSteerCurrent, leftFrontWheel.localRotation.eulerAngles.z);
        if (rightFrontWheel != null)
            rightFrontWheel.localRotation = Quaternion.Euler(
                rightFrontWheel.localRotation.eulerAngles.x, telegraphSteerCurrent, rightFrontWheel.localRotation.eulerAngles.z);
    }

    // ====================================================

    /// <param name="playerYielded">
    /// 🤝 互動式側切：預告期間玩家有沒有先減速。
    ///
    /// true  = 玩家讀到線索並讓了 → 平順匯入（不搶不逼、不播輪胎聲、不瞄準玩家車道）。
    ///         真實世界的駕駛看到你讓他就是這樣出來的，而且玩家做對事要有對應的結果。
    /// false = 玩家維持原速沒反應 → 維持原本的兇狠切法，該有的危險照樣發生。
    ///
    /// 兩種情況都照常註冊危險因子，反應量測不受影響。
    /// </param>
    public void TriggerIntersectionRush(bool isPlayerSideCutIn = false, bool playerYielded = false)
    {
        // 🔒 守衛。另外兩個危險因子的入口都有 if (CanTakeAction())，只有這支沒有，
        //    所以就算車根本演不出來，PlayTyreScreech() 還是會先把聲音播出去 ——
        //    玩家會聽到側切聲卻看不到車切出來。
        //
        //    這裡刻意不檢查 isStaticParked：那是路邊演員車該有的狀態，
        //    擋掉的話側切就永遠不會發生。只擋「真正在忙」的狀態。
        if (isRushingOut || isReverseAction || isBrakingAction
            || isForcedReversing || isInCooldown || isFadingOut) return;

        rushIsPlayerSideCutIn = isPlayerSideCutIn;
        rushPlayerYielded = interactiveSideCut && isPlayerSideCutIn && playerYielded;
        rushStartTime = Time.time;
        postCutHornDone = false;   // 🎺 每次側切重新計算，一次事件只按一聲

        // 📏 玩家實際拿到多少預告時間 —— 事後回饋要拿它跟反應延遲一起講,
        //    「你有 2.4 秒預告」跟「你有 0.9 秒預告」對同樣的反應延遲是完全不同的評價。
        //    必須在關掉 isTelegraphing 之前讀,而且沒預告過就是 0(不能用上一輪的殘值)。
        float telegraphLead = isTelegraphing ? telegraphElapsed : 0f;

        isTelegraphing = false;   // 🔶 預告結束，轉向交還給正常循跡

        if (isStaticParked)
        {
            isStaticParked = false;
            ReleaseParkLock(); // 🪨 被玩家觸發了才解鎖:從這一刻起才會動、才吃物理

            // 這裡原本有一行 transform.LookAt(路點) —— 那是「單幀」把車頭扳到正對路點。
            // 預告才剛花兩秒把車頭慢慢轉出 6~10 度,結果衝出瞬間被硬轉到底,
            // 停放車跟車道有夾角時那是一次 20~40 度的跳轉 —— 突兀感就是這樣來的。
            //
            // 拿掉之後由 HandleNavigation 每幀用 rotationSpeed 做 Slerp 平順轉過去
            // (正常行駛本來就是走那條路徑),預告已經先轉出去的那點角度剛好變成起手式。
            if (currentWaypointNode != null)
                lastWaypointNode = currentWaypointNode;   // 這行有別的用途,要留著
        }
        // 這裡原本是 currentHealthSpeed = Max(currentHealthSpeed, rushOutSpeedBoost) ——
        // 速度從 0 一幀跳到 5 m/s(18 km/h),跟上面的轉向瞬移是同一種突兀。
        // rushAccelerationRate 已經是 30,從 0 爬到 5 m/s 只要 0.17 秒,
        // 視覺上一樣猛但是連續的,所以不需要再插隊這一下。
        //
        // 🤝 玩家已經讓了更不該搶 —— 從靜止正常起步才像「他看到你讓他才出來」。

        // 以下三項只屬於「針對玩家的側邊切入」，路口淨空衝刺不該有
        if (isPlayerSideCutIn)
        {
            // 🤝 玩家有讓 → 這是一次正常匯入，不是險象。輪胎聲會把「你做對了」演成「你差點出事」。
            if (!rushPlayerYielded) PlayTyreScreech();

            // 快取玩家位置，衝出期間用來決定要切到哪個橫向位置。
            // 🤝 玩家有讓的話不瞄準玩家車道 —— 讓它照自己的路點平順匯入，不要貼上去逼車。
            if (rushPlayerYielded)
            {
                rushPlayerTf = null;
            }
            else
            {
                GameObject rushPlayerGo = GameObject.FindGameObjectWithTag("Player");
                rushPlayerTf = rushPlayerGo != null ? rushPlayerGo.transform : null;
            }

            // 用 Silent 註冊：只量測玩家反應與事後回饋，
            // 不參與後照鏡警示（方位不對）也不做未察覺升級（事件只有 2.5 秒）。
            // 兩種結局都要註冊 —— 玩家有讓也該留下「反應了、而且反應得夠早」的紀錄。
            if (HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.RegisterHazard(
                    "側邊衝出", transform, HazardStyle.Silent, telegraphLead);
        }
        else
        {
            rushPlayerTf = null;
        }

        StartCoroutine(ExecuteRushRoutine());
    }

    /// <summary>
    /// 輪胎尖叫聲。側邊衝出與急煞共用 —— 兩者都是輪胎失去抓地力的同一種物理現象。
    ///
    /// 刻意用獨立的 AudioSource，不跟車上原有的引擎音效元件共用，避免互相打斷。
    /// 必須是 3D 音效 —— 玩家要能從聲音判斷「車從哪個方向切過來」，
    /// 這個方位資訊才是真正製造危險感的東西，2D 音效沒有這個效果。
    /// </summary>
    private void PlayTyreScreech()
    {
        if (rushScreechClip == null)
        {
            // 原本是靜默 return，結果「沒聲音」完全查不出原因。改成明確警告。
            UnityEngine.Debug.LogWarning(
                $"[輪胎聲] {name} 沒有指定 Rush Screech Clip，不會有輪胎尖叫聲。" +
                $"請在該車的 prefab 上設定（側切車是 模型/切車.prefab）", this);
            return;
        }

        if (rushAudioSource == null)
            rushAudioSource = gameObject.AddComponent<AudioSource>();

        rushAudioSource.playOnAwake = false;
        rushAudioSource.loop = false;
        rushAudioSource.spatialBlend = 1f;                      // 完全 3D
        rushAudioSource.rolloffMode = AudioRolloffMode.Linear;
        rushAudioSource.minDistance = rushScreechMinDistance;
        rushAudioSource.maxDistance = rushScreechMaxDistance;
        rushAudioSource.dopplerLevel = 0.4f;

        // 每次略微改變音高，避免重複觸發時聽起來像同一段罐頭音效
        rushAudioSource.pitch = UnityEngine.Random.Range(0.94f, 1.09f);
        rushAudioSource.PlayOneShot(rushScreechClip, rushScreechVolume);

        UnityEngine.Debug.Log($"<color=cyan>🔊 [輪胎聲] {name} 播放「{rushScreechClip.name}」" +
                              $"音量 {rushScreechVolume}，3D 衰減 {rushScreechMinDistance}~{rushScreechMaxDistance}m</color>", this);
    }

    private Coroutine brakeScreechRoutine;

    /// <summary>
    /// 急煞專用的輪胎聲，跟側邊衝出分開。
    ///
    /// 為什麼不共用 PlayTyreScreech：側切是「持續失控滑行」，聲音本來就該長；
    /// 急煞是「輪胎鎖死的短促一下」，用同一段長音聽起來像車一直在滑，很不對。
    ///
    /// 沒指定 brakeScreechClip 時會沿用側切那顆，但照樣依 brakeScreechMaxSeconds 裁短，
    /// 所以就算還沒準備專屬素材也聽得出差別。
    /// </summary>
    private void PlayBrakeScreech()
    {
        AudioClip clip = brakeScreechClip != null ? brakeScreechClip : rushScreechClip;
        if (clip == null)
        {
            UnityEngine.Debug.LogWarning(
                $"[輪胎聲] {name} 急煞與側切的 clip 都沒指定，急煞不會有聲音。" +
                $"建議指定 聲音/BrakeScreech_Short.wav", this);
            return;
        }

        if (brakeAudioSource == null)
        {
            brakeAudioSource = gameObject.AddComponent<AudioSource>();
            brakeAudioSource.playOnAwake = false;
        }

        brakeAudioSource.loop = false;
        brakeAudioSource.spatialBlend = 1f;                     // 完全 3D，玩家要能聽出方位
        brakeAudioSource.rolloffMode = AudioRolloffMode.Linear;
        brakeAudioSource.minDistance = rushScreechMinDistance;  // 3D 衰減跟側切共用同一組
        brakeAudioSource.maxDistance = rushScreechMaxDistance;
        brakeAudioSource.dopplerLevel = 0.4f;
        brakeAudioSource.pitch = UnityEngine.Random.Range(brakeScreechPitchRange.x, brakeScreechPitchRange.y);

        // 用 PlayOneShot 才能保留 >1 的音量倍率（AudioSource.volume 上限是 1）。
        // 而 AudioSource.Stop() 會連 PlayOneShot 播出去的聲音一起停掉，
        // 所以「要裁切」跟「要大於 1 的音量」兩件事可以同時成立。
        brakeAudioSource.volume = 1f;
        brakeAudioSource.PlayOneShot(clip, brakeScreechVolume);

        float naturalLength = clip.length / Mathf.Max(0.01f, brakeAudioSource.pitch);
        if (brakeScreechMaxSeconds > 0f && brakeScreechMaxSeconds < naturalLength)
        {
            if (brakeScreechRoutine != null) StopCoroutine(brakeScreechRoutine);
            brakeScreechRoutine = StartCoroutine(TrimBrakeScreechRoutine());
        }

        UnityEngine.Debug.Log($"<color=cyan>🔊 [輪胎聲] {name} 急煞播放「{clip.name}」" +
                              $"音量 {brakeScreechVolume}、音高 {brakeAudioSource.pitch:F2}、" +
                              $"長度 {(brakeScreechMaxSeconds > 0f ? Mathf.Min(brakeScreechMaxSeconds, naturalLength) : naturalLength):F2}s" +
                              $"{(brakeScreechClip == null ? "（沿用側切 clip，已裁短）" : "")}</color>", this);
    }

    /// <summary>播到指定長度就淡出並停止。硬切會有爆音，所以留一小段淡出。</summary>
    IEnumerator TrimBrakeScreechRoutine()
    {
        float fade = Mathf.Max(0f, brakeScreechFadeOut);
        yield return new WaitForSeconds(Mathf.Max(0f, brakeScreechMaxSeconds - fade));

        for (float elapsed = 0f; elapsed < fade; elapsed += Time.deltaTime)
        {
            if (brakeAudioSource == null) yield break;
            brakeAudioSource.volume = 1f - (elapsed / fade);
            yield return null;
        }

        if (brakeAudioSource != null)
        {
            brakeAudioSource.Stop();
            brakeAudioSource.volume = 1f;   // 還原，下次才不會是靜音
        }
        brakeScreechRoutine = null;
    }

    IEnumerator ExecuteRushRoutine()
    {
        const float totalRushSeconds = 2.5f;   // 維持原本的總時長，不影響其他依賴這個節奏的邏輯

        isRushingOut = true;
        isWaitingAtTrafficLight = false;

        // 第一段：高速切入。這段期間忽略玩家的擋路判定，車才切得出來
        // 🤝 玩家已經讓了 → 用平順倍率匯入，不飆也不搶。
        float peak = rushPlayerYielded
            ? Mathf.Max(0.5f, politeRushSpeedMultiplier)
            : Mathf.Max(1f, rushOutSpeedMultiplier);
        rushSpeedFactor = peak;
        // 只有針對玩家的切入才忽略玩家擋路；路口淨空衝刺仍要正常閃避玩家。
        // 🤝 玩家有讓的情況也不該忽略他 —— 該正常偵測、該煞就煞，這才是「安全匯入」。
        rushCommitPhase = rushIsPlayerSideCutIn && !rushPlayerYielded;
        calculatedTargetSpeed = moveSpeed * peak;

        float burst = Mathf.Clamp(rushBurstSeconds, 0f, totalRushSeconds);
        yield return new WaitForSeconds(burst);

        rushCommitPhase = false;   // 切完了，恢復正常的前方偵測與跟車

        // 第二段：切進來之後平順降回一般車流速度，模擬「切完就恢復正常行駛」
        float decay = totalRushSeconds - burst;
        if (decay > 0f)
        {
            float t = 0f;
            while (t < decay)
            {
                t += Time.deltaTime;
                rushSpeedFactor = Mathf.Lerp(peak, 1f, t / decay);
                yield return null;
            }
        }

        rushSpeedFactor = 1f;
        isRushingOut = false;

        // 再多等一段時間才結算：玩家的煞車反應常常發生在車已經切完之後，
        // 太早結算會把「有反應但慢了半拍」誤判成完全沒反應。
        if (rushIsPlayerSideCutIn)
        {
            yield return new WaitForSeconds(hazardReactionWindowSeconds);
            if (HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.UnregisterHazard(transform);
            rushIsPlayerSideCutIn = false;
            rushPlayerYielded = false;
        }
    }

    void HandleAllWheelsRolling()
    {
        // 用「實際位移速度」而不是指令速度 currentHealthSpeed。
        // 移動總閘門（BoxCast）可能把這一幀的位移壓到 0，但上游的速度邏輯
        // 下一幀又會把 currentHealthSpeed 拉回來 —— 用指令速度就會出現
        // 「輪胎一直轉但車身卡住不動」的詭異畫面。
        float movementDelta = isReverseAction ? -reverseSpeed : actualSpeed;
        if (Mathf.Abs(movementDelta) > 0.05f)
        {
            float rollAngle = (movementDelta * wheelRollSpeedMultiplier) * Time.deltaTime * Mathf.Rad2Deg;
            if (leftFrontWheel != null) leftFrontWheel.Rotate(Vector3.right, rollAngle, Space.Self);
            if (rightFrontWheel != null) rightFrontWheel.Rotate(Vector3.right, rollAngle, Space.Self);
            if (leftRearWheel != null) leftRearWheel.Rotate(Vector3.right, rollAngle, Space.Self);
            if (rightRearWheel != null) rightRearWheel.Rotate(Vector3.right, rollAngle, Space.Self);
        }
    }

    // Stop 與 BigStop 一律視為「停」路口，差別只在停讓偵測框的大小
    private bool IsStopNodeType(TrafficPathNode node)
    {
        if (node == null) return false;
        return node.intersectionType == TrafficPathNode.IntersectionType.Stop ||
               node.intersectionType == TrafficPathNode.IntersectionType.BigStop;
    }

    // 🚦 記錄「現在有哪些停等區要這台車停」。新路網一個路口有好幾個停等盒互相重疊，
    //    一台車會同時壓在兩三個上面；只用單一 bool 的話，先離開的那個盒子會把其他盒子的
    //    停等指令一起解除，車就會在紅燈中途溜出去。
    private HashSet<Collider> activeRedTriggers = new HashSet<Collider>();

    /// <summary>由 TrafficLightTrigger 的 OnTriggerStay 呼叫：更新單一停等區的紅燈狀態</summary>
    public void UpdateTrafficLightStatus(Collider triggerCollider, bool shouldStop)
    {
        if (shouldStop) activeRedTriggers.Add(triggerCollider);
        else activeRedTriggers.Remove(triggerCollider);

        // 一律走 virtual 的 SetTrafficLightStatus —— 救護車的 override 才會照樣接手把紅燈清掉，
        // 靜停/衝出/冷卻中的守衛也才會生效。
        SetTrafficLightStatus(activeRedTriggers.Count > 0);
    }

    /// <summary>由 TrafficLightTrigger 的 OnTriggerExit 呼叫：完全離開某個停等區</summary>
    public void RemoveTrafficLightTrigger(Collider triggerCollider)
    {
        activeRedTriggers.Remove(triggerCollider);
        SetTrafficLightStatus(activeRedTriggers.Count > 0);
    }

    public virtual void SetTrafficLightStatus(bool stop)
    {
        if (isStaticParked || isRushingOut || isInCooldown) return;
        if (isYieldingForAmbulance) { isWaitingAtTrafficLight = false; return; } // 避讓時無視紅燈,且強制解除既有的紅燈停等(否則停在紅燈的車不會讓)

        if (stop)
        {
            isWaitingAtTrafficLight = true;

            // 轉紅了就把還沒到期的起步協程收掉，免得它稍後把停等狀態清掉
            if (greenGoRoutine != null) { StopCoroutine(greenGoRoutine); greenGoRoutine = null; }
        }
        else
        {
            // 🟢 新增：轉綠燈瞬間才產生隨機反應延遲，紅燈中不會累加
            if (isWaitingAtTrafficLight)
            {
                currentStartDelay = Random.Range(0.2f, startDelayMax);
            }

            // ⚠️ 只有「還沒有在跑」時才起一個新的。
            //
            //    這支每一幀會被呼叫很多次（路口十幾個停等盒各自呼叫一次）。
            //    我一度在這裡先 StopCoroutine 再重啟 —— 結果是協程每幀被砍掉重來，
            //    那個 WaitForSeconds 永遠等不完，isWaitingAtTrafficLight 就再也不會被清掉，
            //    綠燈了車也不動。要重新確認狀態應該在協程「到期時」做，不是靠重啟。
            if (greenGoRoutine == null)
                greenGoRoutine = StartCoroutine(DelayGreenLightGoRoutine());
        }
    }

    private Coroutine greenGoRoutine;

    IEnumerator DelayGreenLightGoRoutine()
    {
        yield return new WaitForSeconds(Random.Range(0f, 1.5f));

        // ⚠️ 一定要重新確認「現在是不是真的可以走」。
        //
        //    這個路口有十幾個重疊的停等盒，每個盒子各自呼叫 UpdateTrafficLightStatus。
        //    只要其中任何一個送出一次「不停」，這裡就被排下去；1.5 秒後舊版是
        //    **無條件**把 isWaitingAtTrafficLight 設成 false ——
        //    即使那時候其他盒子還在說「停」，車還是會直接闖過紅燈。
        //    這就是「白車在某些路口不看紅綠燈」的成因。
        if (activeRedTriggers.Count > 0)
        {
            isWaitingAtTrafficLight = true;   // 還有盒子壓著 → 維持停等
            greenGoRoutine = null;
            yield break;
        }

        isWaitingAtTrafficLight = false;
        greenGoRoutine = null;
    }

    /// <summary>診斷用：現在有哪些停等盒壓著這台車（供 TrafficLightTrigger 的 log 顯示）</summary>
    public string DescribeActiveRedTriggers()
    {
        if (activeRedTriggers == null || activeRedTriggers.Count == 0)
            return $"沒有任何盒子壓著（isWaitingAtTrafficLight={isWaitingAtTrafficLight}）";

        string s = "";
        foreach (Collider c in activeRedTriggers)
            if (c != null) s += c.name + "  ";
        return $"{activeRedTriggers.Count} 個：{s}（isWaitingAtTrafficLight={isWaitingAtTrafficLight}）";
    }
    protected virtual void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Intersection")) isInIntersection = true;
        if (other.CompareTag("WideRoad")) isInWideRoad = true;
    }

    protected virtual void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Intersection")) isInIntersection = false;
        if (other.CompareTag("WideRoad")) isInWideRoad = false;
    }
    
    void OnDrawGizmos()
    {
        // --- 原有的路口停讓偵測繪圖 ---
        if (!hasCheckedIntersection && IsStopNodeType(currentWaypointNode))
        {
            float dist = Vector3.Distance(transform.position, currentWaypointNode.transform.position);
            if (dist < 10.0f)
            {
                Gizmos.color = isCarDetectedAtStop ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 1f, 1f, 0.2f);

                Matrix4x4 oldMatrix = Gizmos.matrix;
                Gizmos.matrix = transform.localToWorldMatrix;
                bool gizmoBigStop = currentWaypointNode.intersectionType == TrafficPathNode.IntersectionType.BigStop;
                Gizmos.DrawCube(gizmoBigStop ? bigStopBoxOffsetLeft : stopBoxOffsetLeft,
                                gizmoBigStop ? bigStopBoxSize : stopBoxSize);
                Gizmos.DrawCube(gizmoBigStop ? bigStopBoxOffsetRight : stopBoxOffsetRight,
                                gizmoBigStop ? bigStopBoxSize : stopBoxSize);
                Gizmos.matrix = oldMatrix;
            }
        }

        // --- 新增：起步偵測盒 (改為隨時顯示，只要點選物件或在視窗內即可) ---
        Gizmos.color = Color.yellow;
        Matrix4x4 startMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(startBoxOffset, startBoxSize); // 顯示外框
        Gizmos.matrix = startMatrix;

        // --- 喇叭偵測射線 ---
        Gizmos.color = Color.red;
        Gizmos.DrawRay(transform.position + Vector3.up * 0.5f, transform.forward * 10f);
        // 🟢 新增：畫出 SphereCast 的偵測範圍（起點球、終點球、以及連接兩者的圓柱側線）
        // --- ↰ 左轉讓直行偵測框（藍=掃描中安全、紅=偵測到對向直行車）---
        if (debugShowTurnYieldBox)
        {
            Gizmos.color = debugTurnYieldBlocked ? new Color(1f, 0f, 0f, 0.35f) : new Color(0f, 0.4f, 1f, 0.25f);
            Matrix4x4 turnMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(new Vector3(turnDetectionSideOffset, 0.5f, 2.0f + turnForwardOffset), turnBoxSize);
            Gizmos.matrix = turnMatrix;
        }

        // --- 🟡 黃網出口偵測框（綠=暢通、紅=堵塞）＋車頭到出口的連線 ---
        if (debugYellowGridExitNode != null)
        {
            Gizmos.color = debugYellowGridBlocked ? new Color(1f, 0f, 0f, 0.4f) : new Color(0f, 1f, 0f, 0.3f);
            Matrix4x4 gridMatrix = Gizmos.matrix;
            Gizmos.matrix = debugYellowGridExitNode.transform.localToWorldMatrix;
            Gizmos.DrawCube(yellowGridBoxOffset, yellowGridBoxSize);
            Gizmos.matrix = gridMatrix;

            Gizmos.color = debugYellowGridBlocked ? Color.red : Color.green;
            Gizmos.DrawLine(transform.position + Vector3.up * 0.5f,
                            debugYellowGridExitNode.transform.TransformPoint(yellowGridBoxOffset));
        }
        // --- 🔵 貝茲軌跡預覽：轉彎中畫出實際會走的曲線，方便對照節點調位置 ---
        if (isBezierActive)
        {
            Gizmos.color = Color.magenta;
            Vector3 prevPt = bezierP0;
            for (int i = 1; i <= 20; i++)
            {
                Vector3 pt = GetBezierPoint(bezierP0, bezierP1, bezierP2, i / 20f);
                Gizmos.DrawLine(prevPt, pt);
                prevPt = pt;
            }
        }
        DrawSphereCastGizmo();
    }
    private void DrawSphereCastGizmo()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 castDir = Quaternion.AngleAxis(currentSteerAngle, Vector3.up) * transform.forward;
        float activeFanAngle = (isBezierActive || incomingTurnLastFrame) ? turnFanAngle : fanAngle;
        float angleStep = activeFanAngle / Mathf.Max(numberOfRays - 1, 1);
        float startAngle = -activeFanAngle * 0.5f;

        Gizmos.color = new Color(0f, 1f, 1f, 0.5f); // 青色半透明

        for (int i = 0; i < numberOfRays; i++)
        {
            float currentAngle = startAngle + angleStep * i;
            Vector3 rayDir = Quaternion.AngleAxis(currentAngle, Vector3.up) * castDir;
            float sideFactor = Mathf.Abs(currentAngle) / (activeFanAngle * 0.5f);
            float rayDistance = detectionDistance * (1f - sideFactor * lateralDetectionMult);
            if (!isBezierActive && Mathf.Abs(currentAngle) < 1f) rayDistance *= straightCenterMult;
            Gizmos.DrawRay(origin, rayDir * rayDistance);
        }
    }
}
