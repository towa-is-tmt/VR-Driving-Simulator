using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;
public class AmbulanceAI : ReverseThreatAICar
{
    [Header("🚑 ambulance 救護車專屬：爆閃燈光設定")]
    public float sirenEmissionIntensity = 8.0f;
    [Tooltip("閃爍頻率 (秒)")]
    public float flashInterval = 0.08f;
    [Tooltip("雙擊爆閃的間隔延遲 (秒)")]
    public float doubleFlashDelay = 0.25f;
    public static bool IsAmbulanceActive = false; // 🚑 場上有救護車在跑

    /// <summary>
    /// 🚑 我現在是不是「真的被前方車輛擋住」——供避讓中的車判斷要不要二次偏移。
    ///
    /// 為什麼不讓它們自己看我的速度:被擋住時我是「蠕行」不是「停住」
    /// (isAIAhead 那條分支刻意用 intersectionSlowSpeed 前進,註解寫著「絕不停」),
    /// 所以速度會停在 10 附近,永遠低不過對方 1.0 的門檻 —— 二次偏移因此從來不會觸發。
    /// 而把門檻調高到 8 又會把「路口減速」誤判成被擋住。
    /// 直接回答「我是不是被擋住」才是正確的訊號。
    /// </summary>
    public bool IsBlockedByTraffic { get; private set; }

    [Tooltip("格子 0 放左側燈組，格子 1 放右側燈組")]
    public List<MeshRenderer> sirenLightRenderers = new List<MeshRenderer>();

    private Material leftSirenMat;
    private Material rightSirenMat;

    [Header("🚥 救護車專屬：路口與彎道安全速限")]
    public float intersectionSlowSpeed = 5f;

    [Header("🎯 救護車專屬：預判繞道設定")]
    public float obstacleDetectionDistance = 100f;
    public float stopCriticalDistance = 20f;
    public float narrowOvertakeOffset = 1.2f;
    [Tooltip("🚑 救護車固定走在路網中線往左偏這麼多公尺的線上(車流靠右讓,救護車走左半邊)")]
    public float ambulanceLaneLeftOffset = 2f;

    [Header("🚑 左側車道:什麼時候收回中線")]
    // 收回中線是為了避開一個瞄準 bug:救護車專屬的側偏分支用「進入路口的方向」算,
    // 出口方向差 90 度時瞄準點會瞬移,車頭去追就是抖動+原地打轉。
    // 但那個 bug 只在「真的轉彎」時成立 —— 直行通過路口時進出方向幾乎相同,分支是好的。
    //
    // 舊條件 (isInIntersection || isForcedCurveSlow) 對「任何 intersectionType != None 的節點」
    // 都會成立,連直行通過也算,於是救護車每過一個路口就放棄左側位置、過完再滑回去,
    // 白白讓出超車優勢還多晃兩下。改成看實際規劃路線的轉角。
    [Tooltip("只有真的要轉彎才收回中線。取消勾選 = 舊行為(靠近任何路口都收回,直行通過也會)")]
    public bool laneReturnOnlyWhenTurning = true;
    [Tooltip("規劃路線的轉角超過幾度才算要轉彎。路口直行約 0~10 度,真的轉彎是 70~90 度")]
    public float laneReturnTurnAngle = 30f;
    [Tooltip("離轉彎點多近才開始收回中線(公尺)。laneSwitchSpeed 3.5 收掉 2m 只要 0.6 秒,20m 很夠")]
    public float laneReturnDistance = 20f;
    public float laneSwitchSpeed = 3.5f;
    [Tooltip("印出目前的左偏量是用預設值還是節點覆寫值,以及來自哪顆節點")]
    public bool logLaneOffsetSource = false;
    private float lastLaneOffsetLogTime = -999f;
    private float lastLoggedLaneOffset = -999f;

    [Header("🔊 救護車專屬：開路喇叭設定")]
    [Tooltip("用來播放喇叭聲的 AudioSource 元件")]
    public AudioSource hornAudioSource;
    [Tooltip("喇叭警告音效檔 (.mp3 / .wav)")]
    public AudioClip hornSound;
    [Tooltip("每次按喇叭的冷卻間隔時間（秒）")]
    public float hornCooldown = 3f;
    [Tooltip("喇叭音量倍率。1 = 音效檔原始音量，可以調到 1 以上讓它更大聲")]
    [Range(0f, 5f)]
    public float ambulanceHornVolume = 2.5f;   // 改名避免與父類別 ReverseThreatAICar.hornVolume 序列化撞名
    [Tooltip("開始衰減的距離（公尺）。在這個距離內都是最大音量")]
    public float hornMinDistance = 8f;
    [Tooltip("完全聽不到的距離（公尺）。調大 = 更遠就聽得到救護車接近")]
    public float hornMaxDistance = 90f;

    [Header("🚨 循環警笛（獨立於喇叭，生成即播放）")]
    [Tooltip("播放警笛的 AudioSource。留空會自動在自己身上新增一個，不會影響原本的喇叭 AudioSource")]
    public AudioSource sirenAudioSource;
    [Tooltip("警笛音效檔，必須是可循環的 loop 素材")]
    public AudioClip sirenClip;
    [Range(0f, 5f)]
    public float sirenVolume = 1.6f;
    [Tooltip("這個距離內都是最大音量")]
    public float sirenMinDistance = 12f;
    [Tooltip("超過這個距離就完全聽不到。建議比喇叭遠，讓玩家更早察覺")]
    public float sirenMaxDistance = 140f;
    [Tooltip("玩家遲遲沒反應時，每升一級警笛音量加乘多少")]
    public float sirenVolumePerEscalation = 0.5f;
    [Tooltip("貼近玩家時警笛音調升到幾倍（1 = 不變。1.18 約高三個半音，聽起來明顯更急促）")]
    public float sirenPitchWhenClose = 1.18f;
    [Tooltip("進入這個距離內開始逐漸加急（公尺）")]
    public float sirenUrgentDistance = 45f;
    [Tooltip("警笛淡入秒數。救護車是在後方 60m 憑空生成的，若直接用全音量會像「突然出現在身後」；\n淡入可以模擬它其實是從更遠處一路駛來的")]
    public float sirenFadeInSeconds = 4f;
    [Tooltip("警笛淡出秒數。救護車回收前先把聲音收掉，不然是「聲音突然被切斷」。\n" +
             "這段時間內救護車會繼續往前開，聽起來就是它開遠了")]
    public float sirenFadeOutSeconds = 1.5f;

    [Header("🚀 出生起步")]
    // 救護車是在玩家後方 60m 憑空生成的。直接用全速起跑,在後照鏡裡會像「一台車突然衝出來」。
    // 先慢速起步再平滑加到巡航速度,看起來就像它本來就從更遠處一路開過來。
    [Tooltip("出生瞬間的速度倍率。0.4 = 只有巡航速度的四成")]
    [Range(0.1f, 1f)]
    public float spawnStartSpeedFactor = 0.4f;
    [Tooltip("花幾秒從起步速度平滑爬到巡航速度")]
    public float spawnRampSeconds = 3f;

    [Header("🏁 超越玩家後加速離開")]
    [Tooltip("關掉的話維持原本巡航速度，直到拉開 100m 才銷毀")]
    public bool enableEscapeBoost = true;
    [Tooltip("超越玩家後，巡航速度變成原本的幾倍")]
    public float escapeSpeedMultiplier = 1.6f;
    [Tooltip("超越玩家並拉開這個距離後才開始加速，避免剛並排就暴衝（公尺）")]
    public float escapeTriggerDistance = 12f;
    [Tooltip("花幾秒平滑爬升到目標速度。設 0 等於瞬間切換")]
    public float escapeRampSeconds = 3f;

    [Header("📣 後方接近鳴笛")]
    // 原本的鳴笛只在「玩家擋在救護車前面」時觸發，條件很窄。
    // 這裡補上「救護車從後方接近玩家」的情境 —— 也就是真實世界裡最常見的那一種。
    // 共用 nextHornTime / hornCooldown，所以不會跟原本的鳴笛互相打架或連按。
    [Tooltip("關掉就只保留原本「玩家擋在前面」才鳴笛的行為")]
    public bool enableRearApproachHorn = true;
    [Tooltip("救護車在玩家後方多少公尺內開始鳴笛")]
    public float rearHornDistance = 25f;
    [Tooltip("近到這個距離就不再鳴笛，避免貼著臉一直按（公尺）")]
    public float rearHornMinDistance = 5f;

    [Header("📢 警笛廣播盒(前方走廊提前避讓通知)")]
    public float sirenBoxLength = 50f;    // 走廊長度:救護車前方 50m
    public float sirenBoxWidth = 7f;      // 走廊寬度:自己車道+半條鄰道
    // 廣播盒原本以救護車為中心。救護車改走內側車道之後,盒子跟著偏到左邊,
    // 右邊那幾條車道就掃不完整 —— 那些車收不到避讓訊號。
    // 這個偏移讓盒子往右移,重新蓋住整個路面。
    [Tooltip("廣播盒的橫向偏移(公尺)。正 = 往右移,負 = 往左。救護車走內側車道時要往右移才蓋得住其他車道")]
    public float sirenBoxLateralOffset = 0f;
    public float sirenBroadcastInterval = 0.5f; // 每 0.5 秒廣播一次

    [Header("🏍 機車淨空 (機車避讓效果不佳,直接請它們退場)")]
    // 機車會鑽車、車身常是斜的,避讓判定容易漏掉它們,結果堵在救護車前面。
    // 與其硬修避讓,不如把「救護車到玩家之間」那段路上的機車請走。
    //
    // 只清「玩家後方」的 —— 這是專案既有的慣例(故障車封鎖節點也是這樣做):
    // 玩家看不到的地方才淡出,眼前的絕不憑空消失。而救護車是從玩家後方追上來的,
    // 需要淨空的正好就是那一段,所以這個限制不會影響效果。
    [Tooltip("救護車到玩家之間的機車直接縮小淡出。取消勾選 = 完全不清")]
    public bool clearScootersToPlayer = true;
    [Tooltip("清除走廊的半寬(公尺)。太寬會連隔壁街的機車一起清掉")]
    public float scooterClearHalfWidth = 8f;
    [Tooltip("玩家這個半徑內的機車一律留著,免得在眼前縮掉")]
    public float scooterClearPlayerSafeRadius = 18f;
    [Tooltip("縮小淡出要幾秒。太短會像瞬間消失")]
    public float scooterClearFadeSeconds = 1.2f;
    [Tooltip("印出清掉了哪幾台,用來確認有沒有生效")]
    public bool logScooterClear = false;
    private float nextSirenBroadcastTime = 0f;

    [Header("🚑 救護車專屬：起步安全偵測")]
    // 將變數名稱改為獨特的，避開父類別的名稱衝突
    public Vector3 ambulanceStartOffset = new Vector3(0f, 1f, 6f);
    public Vector3 ambulanceStartSize = new Vector3(3.5f, 2f, 12f);
    //public float ambulanceBlockCheckLength = 5f; // 往前檢查範圍，比原本 3m 長很多
    //public float safeLateralClearance =0.1f;      // 玩家側移超過這個距離才算真正讓開
    [Header("🚑 卡死清除設定")]
    public float stuckClearTime = 3f; // 被 AI 車擋住多久後清除它
    [Tooltip("🔧 正在讓道的車享有的寬容倍率。它要等我通過 ambulanceReturnAheadDistance 公尺才回歸,需要遠比一般擋路車更多時間。此類車不套用路口上限")]
    public float yieldingBlockerClearMultiplier = 3f;
    [Tooltip("擋路車有正當理由停著(等紅燈、路口等橫向來車)時的寬容秒數。設長一點,別把守規矩的車清掉")]
    public float legitWaitClearTime = 20f;
    [Tooltip("擋路車既沒在讓道、也沒有正當理由停著 —— 很可能沒收到避讓指令。這種等再久也不會讓開,短時間就清")]
    public float noYieldBlockerClearTime = 4f;
    private float stuckTimer = 0f;
    private Vector3 ambStuckLastPos; // 卡死判定用:上次位置
    private bool isWaitingForClearance = false;

    private float originalAmbulanceSpeed;
    private Coroutine sirenFlashCoroutine;
    private float currentOvertakeOffset = 0f;
    private float appliedOvertakeOffset = 0f; // 🚑 已實際套到位置的繞行偏移(用差值套用,過車後自動歸位)
    private int overtakeSign = 0; // 🚑 繞行方向鎖定(0=沒繞,-1=往左,+1=往右),過了車才解鎖,避免多射線亂跳
    private bool isForcedCurveSlow = false; // 是否因為剛出生在彎道而被強迫減速

    private Transform playerTransform;
    private float nextHornTime = 0f; // 喇叭冷卻計時器
    private float sirenStartTime = 0f; // 警笛開始播放的時間，用來算淡入
    private bool despawning = false;   // 收工中：警笛淡出，時間到才真的 Destroy
    private float despawnStartTime = 0f;
    private bool isFollowingPlayer = false;

    public float followDistance = 20f;

    [Header("🚑 追趕玩家：巡航速度隨玩家浮動")]
    [Tooltip("追趕時的速度 = 玩家車速 × 這個倍率 + 下面的加成。設 0 = 關閉追趕，回到原本的固定速度")]
    public float chaseSpeedFactor = 1.25f;
    [Tooltip("追趕時額外加的速度，確保玩家很慢或靜止時救護車還是會前進")]
    public float chaseSpeedBonus = 3f;
    [Tooltip("追趕速度上限，避免玩家飆車時救護車跟著失控。路口與彎道的安全限速不受這個影響")]
    public float maxChaseSpeed = 40f;
    public float followTriggerDistance = 15f;
    private List<ReverseThreatAICar> yieldingCars = new List<ReverseThreatAICar>();

    private void Awake()
    {
        // 備份原始巡航速度
        originalAmbulanceSpeed = moveSpeed > 1f ? moveSpeed : 15f;
    }
    // 🟢 卡死清除:「實際位置沒動」+ 前方盒內有 AI 車 → 計時清除;玩家擋路 → 交給鳴笛,永不清除
    private void HandleStuckBlockerClear()
    {
        if (isWaitingForClearance) { stuckTimer = 0f; return; }

        // 用實際位移判斷卡死,不用指令速度(指令速度在側面擦撞卡死時不會歸零,會漏判)
        bool hasMoved = (transform.position - ambStuckLastPos).sqrMagnitude > 0.25f; // 0.5m 內算沒動
        if (hasMoved)
        {
            stuckTimer = 0f;
            ambStuckLastPos = transform.position;
            return;
        }

        // 盒子加寬到 ±2.2m,涵蓋側前方擦撞卡死的車
        Collider[] hits = Physics.OverlapBox(
            transform.TransformPoint(new Vector3(0f, 0.5f, 6f)),
            new Vector3(2.2f, 0.8f, 5.5f),
            transform.rotation, detectionLayer);

        GameObject blocker = null;
        foreach (var h in hits)
        {
            if (h.transform.IsChildOf(transform)) continue; // 🚑 自己的碰撞體,跳過(否則會把自己當障礙)
            if (IsPlayerCollider(h)) { stuckTimer = 0f; return; } // 玩家擋路 → 只鳴笛

            // 🔧 偵測盒半寬 2.2m 會掃到隔壁車道、以及正在靠邊讓道的車。
            //    那些車並沒有擋住我的行進路線,把它們當 blocker 會讓救護車停在原地空等,
            //    而救護車一停,前方讓道中的車會因為「救護車停住」的逾時保險而放棄避讓 → 惡性循環。
            //    只採計「真的在我正前方車道上」的車。
            if (!IsBlockingMyLane(h)) continue;

            ReverseThreatAICar c = h.GetComponentInParent<ReverseThreatAICar>();
            if (c != null && c != this && !(c is AmbulanceAI)) { blocker = c.gameObject; continue; }

            CarNavigation nav = h.GetComponentInParent<CarNavigation>();
            if (nav != null) blocker = nav.gameObject;
        }

        if (blocker == null) { stuckTimer = 0f; return; }

        // 擋路車是否正在避讓(還在滑/做二次偏移)→ 給它更多時間靠邊,別急著清
        bool blockerYielding = false;
        var wb = blocker.GetComponent<ReverseThreatAICar>();
        if (wb != null) blockerYielding = wb.IsYieldingForAmbulanceNow;
        else { var gb = blocker.GetComponent<CarNavigation>(); if (gb != null) blockerYielding = gb.IsYieldingForAmbulanceNow; }

        // 🔧 讓道中的車與「真的擋路不動的車」分開計時,不再共用同一條公式。
        //
        //    舊版:clearThreshold = blockerYielding ? stuckClearTime * 1.5f : stuckClearTime;
        //         if (isInIntersection) clearThreshold = Min(clearThreshold, stuckClearTime * 0.7f);
        //
        //    第二行的用意是「我卡在路口最糟,快點清掉擋路的」,但它無差別地把
        //    讓道車的寬容也砍到 0.7 倍(預設只剩 2.1 秒)。而白車要等的是
        //    「救護車超過車頭 ambulanceReturnAheadDistance 公尺並維持 0.8 秒」——
        //    那個距離設 22m 時光是通過就要 3 秒起跳,根本等不完就被淡出清除,
        //    看起來就是「白車還沒等到救護車離開就消失了」。
        //
        //    正在讓道的車是在做正確的事,清掉它對救護車沒有好處(車憑空消失,
        //    後面的車還要重新排隊),所以它不套用路口上限,並改用獨立的寬容倍率。
        //    真的擋路不動的車則維持原本行為,「路口要快點清障」的原意完全保留。
        // 🔧 依「為什麼停著」分級,不再只看「停多久」。
        //
        //    只看時間的話會兩頭不討好:設短了,白車在路口合理等待卻被清掉;
        //    設長了,真的卡死的車要等 stuckClearTime × 倍率才清,救護車卡在原地動不了。
        //    而白車其實有現成的狀態可以問,直接問它「你在等什麼」就能分開處理。
        float clearThreshold;
        string blockerReason;

        var wbCar = blocker.GetComponent<ReverseThreatAICar>();
        var gbCar = blocker.GetComponent<CarNavigation>();

        bool legitWait = false;
        bool returning = false;
        if (wbCar != null)
        {
            legitWait = wbCar.IsWaitingAtTrafficLight()      // 等紅燈
                     || wbCar.IsCarDetectedAtStopNow         // 無號誌路口等橫向來車
                     || wbCar.IsHoldingForJunction;          // 路口淨空等待中
            returning = wbCar.IsReturningFromYieldNow;
        }
        else if (gbCar != null)
        {
            legitWait = gbCar.IsWaitingAtTrafficLightNow      // 等紅燈
                     || gbCar.IsCarDetectedAtStopNow;        // 無號誌路口等橫向來車
            returning = gbCar.IsReturningFromYieldNow;
        }

        // 🔑 正在回歸主車道的車,isYieldingForAmbulance 已經是 false ——
        //    少了這一行它會被歸到「未進入避讓狀態」那類,4 秒就被清掉。
        //    它明明是剛讓完路、正要歸位的車,那是最不該清的對象。
        if (returning) blockerYielding = true;

        if (legitWait)
        {
            // 它有正當理由停著(而且那個理由跟我無關),給很長的寬容,別把守規矩的車清掉
            clearThreshold = legitWaitClearTime;
            blockerReason = "合理停等(紅燈/路口)";
        }
        else if (blockerYielding)
        {
            // 正在讓道 = 在做正確的事,給它時間靠邊
            clearThreshold = stuckClearTime * yieldingBlockerClearMultiplier;
            blockerReason = "讓道中";
        }
        else
        {
            // 🚨 既沒有正當理由停著,也沒有進入避讓狀態 ——
            //    很可能它根本沒收到避讓指令(觸發距離不夠、被別的狀態卡住、旗標沒設起來)。
            //    這種是純粹的路障,等再久也不會自己讓開,短時間就清掉。
            clearThreshold = noYieldBlockerClearTime;
            blockerReason = "★未進入避讓狀態(可能沒收到指令)";
        }

        stuckTimer += Time.deltaTime;
        if (stuckTimer >= clearThreshold)
        {
            UnityEngine.Debug.Log($"<color=red>🚑 前方 {blocker.name} 擋路超過 {clearThreshold:F1} 秒,淡出清除讓路</color>" +
                $"  (原因={blockerReason}, 我在路口內={isInIntersection})");
            var whiteBlocker = blocker.GetComponent<ReverseThreatAICar>();
            if (whiteBlocker != null) whiteBlocker.ForceFadeOutClear();
            else
            {
                var grayBlocker = blocker.GetComponent<CarNavigation>();
                if (grayBlocker != null) grayBlocker.ForceFadeOutClear();
                else Destroy(blocker);
            }
            stuckTimer = 0f;
            ambStuckLastPos = transform.position;
        }
    }
    [Header("🔧 卡死判定:什麼才算「真的擋住我」")]
    [Tooltip("擋路車的車心必須在我正前方這個橫向範圍內才算擋路(公尺)。偵測盒半寬 2.2m 會掃到隔壁車道,這裡再收窄一次")]
    public float blockerLaneHalfWidth = 1.3f;
    [Tooltip("車心必須在我前方至少這麼遠才算擋路,排除貼在車尾/車側的車(公尺)")]
    public float blockerMinAhead = 1.5f;
    [Tooltip("已經靠邊到這個橫向距離以外的讓道車,視為已讓開,不算擋路(公尺)")]
    public float yieldedAsideDistance = 1.6f;

    /// <summary>
    /// 🔧 判斷這個碰撞體是不是「真的擋在我的行進車道上」。
    ///
    /// 原本只要落進 OverlapBox(半寬 2.2m)就算擋路,但那個寬度涵蓋隔壁車道,
    /// 也涵蓋正在往右靠邊、屁股還沒完全收進去的讓道車。
    /// 把它們當 blocker → 救護車停住空等 → 前方讓道車因「救護車停住」的逾時保險放棄避讓
    /// → 更沒人讓路 → 救護車更卡。必須把判定收窄到「正前方同車道」。
    /// </summary>
    private bool IsBlockingMyLane(Collider h)
    {
        if (h == null) return false;

        ReverseThreatAICar w = h.GetComponentInParent<ReverseThreatAICar>();
        CarNavigation g = w == null ? h.GetComponentInParent<CarNavigation>() : null;
        Transform tf = w != null ? w.transform : (g != null ? g.transform : h.transform);

        Vector3 rel = transform.InverseTransformPoint(tf.position);

        // 車心不在我正前方(太靠側邊 / 在我身側或後方)→ 不算擋路
        if (Mathf.Abs(rel.x) > blockerLaneHalfWidth) return false;
        if (rel.z < blockerMinAhead) return false;

        // 🔧 靜止不動的車(路邊等觸發的側切車 / 靜態障礙)不適用「已讓開」的寬容,
        //    它們收到避讓廣播後 IsYieldingForAmbulanceNow 也會是 true,但永遠不會移動。
        bool staticBlocker = (w != null && w.isStaticParked) || (g != null && g.isStaticObstacle);

        // 已經明顯讓到旁邊的車 → 不算擋路(給它把動作做完,別急著清)
        bool yielding = !staticBlocker
                     && ((w != null && w.IsYieldingForAmbulanceNow)
                      || (g != null && g.IsYieldingForAmbulanceNow));
        if (yielding && Mathf.Abs(rel.x) > yieldedAsideDistance) return false;

        return true;
    }

    [Header("🚦 路口淨空 (橫向車讓我先過)")]
    [Tooltip("關掉就完全不影響橫向車,回到純靠駕駛自己避讓的行為")]
    public bool enableJunctionHold = true;
    [Tooltip("作用範圍的中心,從車頭往前多遠(公尺)。要涵蓋「即將進入的那個路口」")]
    public float junctionHoldLookAhead = 16f;
    [Tooltip("作用半徑(公尺)。刻意做小,只影響當下這個路口,不會讓整條南北向都停住")]
    public float junctionHoldRadius = 26f;
    [Tooltip("與我的行進方向夾角超過多少度才算「橫向車」。0.5 約等於 60 度")]
    [Range(0f, 1f)] public float crossTrafficDot = 0.5f;

    /// <summary>
    /// 🚦 路口淨空廣播:只叫「我即將通過的那個路口」附近的橫向車停一下。
    ///
    /// 刻意不做號誌優先(不去改紅綠燈)——台灣實務上救護車就是鳴笛闖紅燈、
    /// 靠其他駕駛自己避讓,幫它開綠燈既不真實也會削弱情境的訓練價值。
    /// 這裡模擬的是「橫向駕駛看到救護車,自己在路口前停下來讓」。
    ///
    /// 三個安全設計:
    ///   1. 範圍小(半徑 26m),只影響當下路口,不是全圖同方向的車都停
    ///   2. 已經開進路口的車不會被停住,它要趕快開出去(在 CarNavigation 端判斷)
    ///   3. 停等有逾時上限,救護車若在別處卡住不會讓路口永久凍結
    /// </summary>
    private void BroadcastJunctionHold()
    {
        if (!enableJunctionHold) return;

        Vector3 center = transform.TransformPoint(new Vector3(0f, 1f, junctionHoldLookAhead));
        Collider[] hits = Physics.OverlapSphere(center, junctionHoldRadius, detectionLayer);

        foreach (var h in hits)
        {
            if (IsPlayerCollider(h)) continue;          // 玩家不受控,靠他自己判斷
            if (h.transform.IsChildOf(transform)) continue;

            CarNavigation nav = h.GetComponentInParent<CarNavigation>();
            ReverseThreatAICar white = nav == null ? h.GetComponentInParent<ReverseThreatAICar>() : null;
            if (white == this || white is AmbulanceAI) continue;   // 自己與其他救護車不算

            Transform otherTf = nav != null ? nav.transform : (white != null ? white.transform : null);
            if (otherTf == null) continue;
            if (nav != null && nav.isStaticObstacle) continue;     // 路邊靜止物不用管
            if (white != null && white.isStaticParked) continue;   // 路邊等觸發的側切車不用管

            // 只針對橫向車:同向的走避讓那套,對向的本來就不會擋到我
            float d = Vector3.Dot(transform.forward, otherTf.forward);
            if (Mathf.Abs(d) > crossTrafficDot) continue;

            if (nav != null) nav.HoldForAmbulanceAtJunction(transform);
            else if (white != null) white.HoldForAmbulanceAtJunction(transform);
        }
    }

    // 📢 警笛廣播:前方走廊內所有同向 AI 車統一收到避讓訊號(白車走避讓點,灰車靠邊停,各自處理)
    private void BroadcastSirenYield()
    {
        if (Time.time < nextSirenBroadcastTime) return;
        nextSirenBroadcastTime = Time.time + sirenBroadcastInterval;

        BroadcastJunctionHold();   // 🚦 同一個節拍順便廣播路口淨空
        ClearScootersBetweenPlayer();   // 🏍 同一個節拍清掉擋路的機車

        Vector3 center = transform.TransformPoint(
            new Vector3(sirenBoxLateralOffset, 1f, sirenBoxLength * 0.5f));
        Vector3 halfExtents = new Vector3(sirenBoxWidth * 0.5f, 1.5f, sirenBoxLength * 0.5f);
        Collider[] hits = Physics.OverlapBox(center, halfExtents, transform.rotation, detectionLayer);

        foreach (var h in hits)
        {
            if (IsPlayerCollider(h)) continue; // 玩家不吃避讓指令,交給鳴笛/跟車邏輯

            ReverseThreatAICar car = h.GetComponentInParent<ReverseThreatAICar>();
            if (car != null && car != this && !(car is AmbulanceAI))
            {
                if (Vector3.Dot(transform.forward, car.transform.forward) > 0.4f) // 同向才讓
                {
                    car.SetYieldForAmbulance(true, this.transform);
                    if (!yieldingCars.Contains(car)) yieldingCars.Add(car);
                }
                continue;
            }

            CarNavigation nav = h.GetComponentInParent<CarNavigation>();
            if (nav != null && Vector3.Dot(transform.forward, nav.transform.forward) > 0.4f)
            {
                nav.SetYieldForAmbulance(true, this.transform);
            }
        }
    }
    /// <summary>
    /// 🏍 把「救護車 → 玩家」這條線上的機車縮小淡出。
    ///
    /// 用 OverlapCapsule 而不是 OverlapSphere:膠囊沿著兩點之間的線段長出來,
    /// 所以它跟著路走,不會像球體那樣把旁邊平行街道的機車一起掃進來。
    /// (跟 AccidentDirector 的走廊清車同一個做法)
    /// </summary>
    private void ClearScootersBetweenPlayer()
    {
        if (!clearScootersToPlayer || playerTransform == null) return;

        Collider[] hits = Physics.OverlapCapsule(transform.position, playerTransform.position,
                                                 scooterClearHalfWidth, detectionLayer);
        int cleared = 0;
        var done = new System.Collections.Generic.HashSet<GameObject>();

        foreach (var h in hits)
        {
            if (h == null || IsPlayerCollider(h)) continue;

            CarNavigation nav = h.GetComponentInParent<CarNavigation>();
            if (nav == null || !nav.isScooter) continue;      // 只清機車,汽車照原本的避讓流程走
            if (nav.isStaticObstacle) continue;               // 路邊靜態停放的不擋路,留著當佈景
            if (!done.Add(nav.gameObject)) continue;          // 一台車可能有多個碰撞體

            Vector3 toScooter = nav.transform.position - playerTransform.position;
            toScooter.y = 0f;

            // 玩家附近的不動 —— 近距離縮小一定看得到
            if (toScooter.magnitude < scooterClearPlayerSafeRadius) continue;

            // 只清玩家「後方」的。救護車從後方追上來,要淨空的正好是這一段;
            // 前方的機車玩家正在看,絕不能讓它憑空縮掉。
            if (Vector3.Dot(playerTransform.forward, toScooter) >= 0f) continue;

            nav.ForceFadeOutClear(Mathf.Max(0.05f, scooterClearFadeSeconds));
            cleared++;
            if (logScooterClear)
                UnityEngine.Debug.Log($"<color=grey>🏍 [救護車淨空] 請走機車「{nav.name}」" +
                                      $"(離玩家 {toScooter.magnitude:F0}m,在玩家後方)</color>", nav);
        }

        if (cleared > 0 && logScooterClear)
            UnityEngine.Debug.Log($"<color=grey>🏍 [救護車淨空] 這一輪共請走 {cleared} 台機車</color>", this);
    }

    protected override void Start()
    {
        base.Start();
        if (DangerEventDirector.Instance != null) DangerEventDirector.Instance.BeginLongEvent("救護車");
        IsAmbulanceActive = true;
        // 動態鎖定場上的 VR 玩家車輛
        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
        {
            playerTransform = playerObj.transform;
        }

        // 確保自動抓取喇叭音效組件
        if (hornAudioSource == null)
        {
            hornAudioSource = GetComponent<AudioSource>();
        }

        // 設定成 3D 音效並套用距離參數：救護車越靠近玩家越大聲。
        // 若 spatialBlend 是 0（2D），不管離多遠音量都一樣，聽起來就很平、很小聲。
        if (hornAudioSource != null)
        {
            hornAudioSource.spatialBlend = 1f;                     // 1 = 完全 3D
            hornAudioSource.rolloffMode = AudioRolloffMode.Linear; // 線性衰減，比預設的對數衰減好調
            hornAudioSource.minDistance = hornMinDistance;
            hornAudioSource.maxDistance = hornMaxDistance;
            hornAudioSource.dopplerLevel = 0.3f;                   // 一點都卜勒效果，接近時音調升高更有壓迫感
        }

        // 初始化爆閃燈材質（實例化避免污染 Prefab）
        if (sirenLightRenderers.Count > 0 && sirenLightRenderers[0] != null)
            leftSirenMat = sirenLightRenderers[0].material;
        if (sirenLightRenderers.Count > 1 && sirenLightRenderers[1] != null)
            rightSirenMat = sirenLightRenderers[1].material;

        // 啟動警報燈
        if (sirenFlashCoroutine == null)
            sirenFlashCoroutine = StartCoroutine(SirenFlashRoutine());

        // ── 以下為「危險感知」相關的新增功能，不影響上面任何行進/避讓邏輯 ──
        SetupAndPlaySiren();
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.RegisterHazard("救護車", transform);
        StartCoroutine(SirenEscalationRoutine());
        StartCoroutine(SpawnRampRoutine());
        StartCoroutine(EscapeAfterOvertakeRoutine());
        StartCoroutine(RearApproachHornRoutine());
    }
    private void OnDestroy()
    {
        if (DangerEventDirector.Instance != null) DangerEventDirector.Instance.EndLongEvent();
        IsAmbulanceActive = false;

        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(transform);
    }

    /// <summary>
    /// 建立並啟動循環警笛。刻意使用獨立的 AudioSource，
    /// 避免跟 hornAudioSource 的 PlayOneShot 互相干擾。
    /// </summary>
    private void SetupAndPlaySiren()
    {
        if (sirenClip == null)
        {
            UnityEngine.Debug.LogWarning("[救護車] 沒有指定 Siren Clip，循環警笛不會播放。", this);
            return;
        }

        if (sirenAudioSource == null)
            sirenAudioSource = gameObject.AddComponent<AudioSource>();

        sirenAudioSource.clip = sirenClip;
        sirenAudioSource.loop = true;
        sirenAudioSource.playOnAwake = false;
        // 從 0 開始，由 SirenEscalationRoutine 在 sirenFadeInSeconds 內拉上來
        sirenAudioSource.volume = 0f;
        sirenStartTime = Time.time;
        sirenAudioSource.spatialBlend = 1f;                     // 完全 3D，才會由遠而近
        sirenAudioSource.rolloffMode = AudioRolloffMode.Linear;
        sirenAudioSource.minDistance = sirenMinDistance;
        sirenAudioSource.maxDistance = sirenMaxDistance;
        sirenAudioSource.dopplerLevel = 0.5f;                   // 接近時音調升高，逼近感更強
        sirenAudioSource.Play();
    }

    /// <summary>
    /// 救護車位於玩家後方並接近時，週期性鳴笛提醒。
    ///
    /// 這是純新增的判定，不影響原本「玩家擋在前方」的鳴笛、停車或避讓流程。
    /// 刻意共用 nextHornTime 與 hornCooldown：
    ///   - 兩種鳴笛不會同時響，也不會疊成連環噪音
    ///   - 玩家一直沒反應時，SirenEscalationRoutine 縮短的 hornCooldown 會自動套用到這裡
    /// </summary>
    private System.Collections.IEnumerator RearApproachHornRoutine()
    {
        if (!enableRearApproachHorn) yield break;

        while (true)
        {
            if (playerTransform != null)
            {
                // 換算到玩家的本地座標：z < 0 代表救護車在玩家後方
                Vector3 local = playerTransform.InverseTransformPoint(transform.position);
                float dist = local.magnitude;

                if (local.z < 0f
                    && dist <= rearHornDistance
                    && dist >= rearHornMinDistance
                    && Time.time >= nextHornTime)
                {
                    PlayWarningHorn("後方救護車接近，請靠右禮讓");
                    nextHornTime = Time.time + hornCooldown;
                }
            }
            yield return new WaitForSeconds(0.25f);
        }
    }

    /// <summary>
    /// 救護車超越玩家之後，把巡航速度上調，讓它盡快駛離視線。
    ///
    /// 只寫入 originalAmbulanceSpeed 這個「速度上限」變數，既有的路口減速、
    /// 彎道降速、障礙物停車等判斷全部照常運作（它們都以這個值為基準做比例縮放）。
    /// 完全不觸碰 BroadcastSirenYield、SetYieldForAmbulance 或 yieldingCars 的回歸流程。
    /// </summary>
    /// <summary>
    /// 出生後的速度爬升。
    ///
    /// 直接改 originalAmbulanceSpeed（跟 EscapeAfterOvertakeRoutine 同一個做法），
    /// 所以不會動到任何行進、避讓、路徑邏輯 —— 那些照樣讀這個值。
    /// 跑完就把值還原成備份的巡航速度，之後的逃離加速從正確的基準往上乘。
    /// </summary>
    private System.Collections.IEnumerator SpawnRampRoutine()
    {
        if (spawnRampSeconds <= 0f || spawnStartSpeedFactor >= 1f) yield break;

        float cruise = originalAmbulanceSpeed;          // Awake() 已備份好的巡航速度
        float start = cruise * spawnStartSpeedFactor;

        UnityEngine.Debug.Log($"<color=cyan>🚑 [出生起步] {start:F0} → {cruise:F0}，歷時 {spawnRampSeconds:F1} 秒</color>", this);

        float t = 0f;
        while (t < spawnRampSeconds)
        {
            t += Time.deltaTime;
            originalAmbulanceSpeed = Mathf.Lerp(start, cruise, t / spawnRampSeconds);
            yield return null;
        }
        originalAmbulanceSpeed = cruise;   // 收尾,避免浮點誤差留下零頭
    }

    private System.Collections.IEnumerator EscapeAfterOvertakeRoutine()
    {
        if (!enableEscapeBoost) yield break;

        float cruiseSpeed = originalAmbulanceSpeed;   // Awake() 已備份好
        float passedDistance = 0f;

        // ── 階段一：等待救護車真的超越玩家 ──
        while (true)
        {
            if (playerTransform != null)
            {
                Vector3 toAmbulance = transform.position - playerTransform.position;
                float dist = toAmbulance.magnitude;

                // 點積 > 0 代表救護車已經跑到玩家前方（與 CheckOvertakeAndDestroy 同一套判斷）
                bool isAhead = Vector3.Dot(playerTransform.forward, toAmbulance.normalized) > 0f;

                if (isAhead && dist >= escapeTriggerDistance)
                {
                    passedDistance = dist;
                    break;
                }
            }
            yield return new WaitForSeconds(0.2f);
        }

        // ── 階段二：在 escapeRampSeconds 內平滑爬升，不是瞬間跳上去 ──
        float targetSpeed = cruiseSpeed * escapeSpeedMultiplier;
        UnityEngine.Debug.Log($"<color=cyan>🏁 [救護車] 已超越玩家 {passedDistance:F0}m，開始加速離開" +
                              $"（{cruiseSpeed:F0} → {targetSpeed:F0}，歷時 {escapeRampSeconds:F1} 秒）</color>");

        float ramp = Mathf.Max(0.01f, escapeRampSeconds);
        float elapsed = 0f;
        while (elapsed < ramp)
        {
            elapsed += Time.deltaTime;
            originalAmbulanceSpeed = Mathf.Lerp(cruiseSpeed, targetSpeed, elapsed / ramp);
            yield return null;
        }

        originalAmbulanceSpeed = targetSpeed;   // 收尾，避免浮點誤差留下零頭
        // 加速後就固定住，玩家迴轉也不會變回慢速
    }

    /// <summary>
    /// 玩家一直沒察覺時，逐級加大警笛音量並縮短喇叭間隔。
    /// 只動音效參數，不碰路徑、避讓或回歸邏輯。
    /// </summary>
    private System.Collections.IEnumerator SirenEscalationRoutine()
    {
        float baseHornCooldown = hornCooldown;

        while (true)
        {
            var tracker = HazardAwarenessTracker.Instance;
            int level = (tracker != null) ? tracker.GetEscalationLevel(transform) : 0;

            if (sirenAudioSource != null)
            {
                // 淡入係數：剛生成時從 0 爬到 1，避免警笛在玩家背後憑空全音量響起
                float fade = (sirenFadeInSeconds <= 0f)
                    ? 1f
                    : Mathf.Clamp01((Time.time - sirenStartTime) / sirenFadeInSeconds);

                // 🔇 收工淡出：必須乘進這一行，不能從外面改 volume ——
                //    這個迴圈每輪都會重算音量，外面設的值下一輪就被蓋掉了
                if (despawning && sirenFadeOutSeconds > 0f)
                    fade *= 1f - Mathf.Clamp01((Time.time - despawnStartTime) / sirenFadeOutSeconds);

                sirenAudioSource.volume = (sirenVolume + level * sirenVolumePerEscalation) * fade;

                // 越靠近玩家音調越高 —— 這是讓聲音「聽起來急促」的主要來源，
                // 比單純放大音量更能製造壓迫感。
                if (playerTransform != null)
                {
                    float d = Vector3.Distance(transform.position, playerTransform.position);
                    float t = Mathf.InverseLerp(sirenUrgentDistance, 0f, d);
                    sirenAudioSource.pitch = Mathf.Lerp(1f, sirenPitchWhenClose, t);
                }
            }

            // 等級越高喇叭按越密（最短不低於 0.8 秒，避免變成純噪音）
            if (tracker != null)
                hornCooldown = Mathf.Max(0.8f, baseHornCooldown - level * 0.8f);

            yield return new WaitForSeconds(0.1f);
        }
    }
    // 供 Spawner 調用的彎道降速初始化。
    // 註:Update 每幀會依實際路網幾何重算 isForcedCurveSlow,所以這個值會在下一幀被覆寫。
    // 真的生在彎道上的話重算結果同樣是 true,行為不變;生在直路上則會正確地回到巡航速度。
    public void InitializeSlowSpeedForCurve()
    {
        isForcedCurveSlow = true;
    }
    // AmbulanceAI.cs
    // 🟢 新增：真正的多型覆寫，不管誰呼叫都直接忽略紅綠燈訊號
    public override void SetTrafficLightStatus(bool stop)
    {
        isWaitingAtTrafficLight = false; // 直接強制保持不等待，且不啟動任何冷卻/延遲流程
    }

    protected override void Update()
    {
        // 1. 救護車無視紅綠燈號誌
        SetTrafficLightStatus(false);

        // 2. 隨時檢查是否已經超越 VR 玩家 50 公尺，是的話就安全回收銷毀
        CheckOvertakeAndDestroy();
        // 🚑 保底：路網逼得只能走窄路時，等玩家離遠看不清了再低調收工，絕不在玩家眼前憑空消失
        if (currentWaypointNode != null && currentWaypointNode.isNarrowRoad)
        {
            float distToPlayerForExit = playerTransform != null
                ? Vector3.Distance(transform.position, playerTransform.position)
                : Mathf.Infinity;

            if (distToPlayerForExit > 60f)
            {
                foreach (var car in yieldingCars)
                    if (car != null) car.ScheduleDestroyAfterAmbulanceLeft(5f);
                yieldingCars.Clear();
                UnityEngine.Debug.Log($"<color=red>🚑 [窄路收工] 玩家已遠離,救護車低調銷毀。節點:{currentWaypointNode.name}</color>");
                BeginDespawn("窄路收工");
                return;
            }
        }
        HandleStuckBlockerClear();
        BroadcastSirenYield();
        // 3. 基礎速度決策（過彎預判）
        float targetSpeed = originalAmbulanceSpeed;

        // ⚠️ 彎道預判一定要「每幀重算」。舊版寫成:
        //        if (isInIntersection || isForcedCurveSlow) { 降速 }
        //        else if (...)                             { 這裡才會把旗標設回 false }
        //    旗標一旦變 true 就永遠走上面那條,else 再也不會執行 —— 於是
        //    isForcedCurveSlow 卡死在 true,救護車從第一次接近彎道之後就被永久釘在
        //    intersectionSlowSpeed。症狀就是「只有出生那一下是快的,之後一直慢」。
        bool curveSlowNow = false;
        if (currentWaypointNode != null)
        {
            // 🟢 改用「路徑本身的轉彎角度」判斷，跟父類別邏輯一致，
            // 不再依賴車身當下角度，減少誤判與速度跳動
            float pathTurnAngle = 0f;
            if (lastWaypointNode != null)
            {
                Vector3 incomingDir = (currentWaypointNode.transform.position - lastWaypointNode.transform.position);
                Vector3 outgoingDir = currentWaypointNode.nextChoices != null && currentWaypointNode.nextChoices.Count > 0
                    ? (currentWaypointNode.nextChoices[0].transform.position - currentWaypointNode.transform.position)
                    : (currentWaypointNode.transform.position - transform.position);

                incomingDir.y = 0; outgoingDir.y = 0;
                pathTurnAngle = Vector3.Angle(incomingDir, outgoingDir);
            }

            float distanceToNode = Vector3.Distance(transform.position, currentWaypointNode.transform.position);
            bool isCurveSign = currentWaypointNode.intersectionType != TrafficPathNode.IntersectionType.None;
            bool isSharpCurveAngle = pathTurnAngle > 20f;

            // 🟢 救護車速度較快，煞停距離要拉長，改成 25m 前就開始預判減速
            float curveApproachDistance = 25f;

            if ((isCurveSign || isSharpCurveAngle) && distanceToNode < curveApproachDistance)
            {
                float curveSafetyFactor = Mathf.Clamp01(distanceToNode / curveApproachDistance);

                // 🟢 彎越急，允許的最低速度越低
                float sharpnessFactor = Mathf.Clamp01(pathTurnAngle / 90f);
                float curveMinSpeed = Mathf.Lerp(intersectionSlowSpeed, intersectionSlowSpeed * 0.5f, sharpnessFactor);

                targetSpeed = Mathf.Lerp(curveMinSpeed, originalAmbulanceSpeed, curveSafetyFactor);
                curveSlowNow = true;
            }
        }

        // 每幀無條件重寫,不再有「設得起來、關不掉」的問題
        isForcedCurveSlow = curveSlowNow;

        // 已經開進路口 → 在彎道預判之上再壓一次(路口比彎道更嚴格),
        // 但這一段不碰 isForcedCurveSlow,所以離開路口後速度會自己回來。
        if (isInIntersection) targetSpeed = Mathf.Min(targetSpeed, intersectionSlowSpeed);
        // 🟢 新增：救護車專屬的起步阻塞偵測
        // 只有在車速低於 2f 且前方有障礙時才啟動
        // 🟢 修改名稱以對應上面的變數
        // 🟢 修改：起步阻塞偵測改為「僅偵測玩家」f
        // 🟢 修改：偵測盒同時針對 Player 與 其他 AI 車輛
        // 🟢 修改：起步阻塞偵測改用本地座標判斷，取代 OverlapBox
        // 🟢 改用 OverlapBox，直接沿用你在 Inspector 調整的黃色偵測盒
        bool playerDetectedInFront = false;

        Collider[] hitColliders = Physics.OverlapBox(
            transform.TransformPoint(ambulanceStartOffset),
            ambulanceStartSize / 2f,
            transform.rotation,
            detectionLayer);

        foreach (var col in hitColliders)
        {
            if (IsPlayerCollider(col))
            {
                playerDetectedInFront = true;
                break;
            }
        }

        // 只要玩家還在盒子範圍內，就必須等待
        if (playerDetectedInFront)
        {
            isWaitingForClearance = true;
        }
        else
        {
            isWaitingForClearance = false;
        }
        // 🟢 執行邏輯：如果正在等待玩家避讓，強制速度為 0，並觸發鳴笛
        if (isWaitingForClearance)
        {
            calculatedTargetSpeed = 0f;
            if (Time.time >= nextHornTime)
            {
                PlayWarningHorn();
                nextHornTime = Time.time + hornCooldown;
            }
            // 跳過後續的超車邏輯，直接執行父類別物理移動
            base.Update();
            return;
        }
        // 4. 多射線前方障礙物偵測 (修改處：分離玩家與 AI 的偵測邏輯)
        RaycastHit hit;
        Vector3 rayOrigin = transform.position + transform.forward * 1.5f + Vector3.up * 0.6f;
        Vector3[] rayDirections =
        {
        Quaternion.Euler(0,-20,0) * transform.forward,
        Quaternion.Euler(0,-12,0) * transform.forward,
        transform.forward,
        Quaternion.Euler(0,12,0) * transform.forward,
        Quaternion.Euler(0,20,0) * transform.forward
    };

        float closestPlayerDistance = obstacleDetectionDistance;
        float closestAIDistance = obstacleDetectionDistance;
        bool isPlayerAhead = false;
        bool isAIAhead = false;
        bool frontBlockerIsYielding = false; // 前方擋路的是否為「避讓中」的車(可貼近繞行)新加
        float targetOffset = 0f;
        float closestAIRelX = 0f; // 最近那台擋路車的橫向位置(用來決定往哪邊繞)
        
        foreach (var dir in rayDirections)
        {
            UnityEngine.Debug.DrawRay(rayOrigin, dir * obstacleDetectionDistance, Color.green);

            if (Physics.Raycast(rayOrigin, dir, out hit, obstacleDetectionDistance, detectionLayer, QueryTriggerInteraction.Ignore))
            {
                bool isPlayer = IsPlayerCollider(hit.collider);
                ReverseThreatAICar frontCar = isPlayer ? null : hit.collider.GetComponentInParent<ReverseThreatAICar>();

                if (isPlayer)
                {
                    isPlayerAhead = true;
                    closestPlayerDistance = Mathf.Min(hit.distance, closestPlayerDistance);
                }
                else if (frontCar != null && frontCar != this)
                {
                    bool sameDir = Vector3.Dot(transform.forward, frontCar.transform.forward) > 0.4f; // 只叫同向車讓
                    // 🚑 用「射線實際打到的點」判斷擋不擋路(車身歪斜時屁股才抓得到),不用車中心
                    Vector3 relHit = transform.InverseTransformPoint(hit.point);

                    if (Mathf.Abs(relHit.x) > 2.2f)
                    {
                        if (sameDir) frontCar.SetYieldForAmbulance(true, this.transform);
                        continue;
                    }

                    isAIAhead = true;
                    if (hit.distance < closestAIDistance) { closestAIDistance = hit.distance; closestAIRelX = relHit.x; }
                    if (sameDir) frontCar.SetYieldForAmbulance(true, this.transform);
                    // targetOffset 移到迴圈外統一算(避免多射線互相覆蓋、方向亂跳)
                }
                else
                {
                    CarNavigation navAhead = hit.collider.GetComponentInParent<CarNavigation>();
                    if (navAhead != null)
                    {
                        bool sameDirN = Vector3.Dot(transform.forward, navAhead.transform.forward) > 0.4f; // 只叫同向車讓
                        // 🚑 用射線命中點判斷(車身歪斜屁股才抓得到),不用車中心
                        Vector3 relHitN = transform.InverseTransformPoint(hit.point);
                        float passableMargin = navAhead.IsPulledOverForAmbulanceNow ? 1.5f : 2.2f;
                        if (Mathf.Abs(relHitN.x) > passableMargin)
                        {
                            if (sameDirN) navAhead.SetYieldForAmbulance(true, this.transform);
                            continue;
                        }

                        isAIAhead = true;
                        if (hit.distance < closestAIDistance) { closestAIDistance = hit.distance; closestAIRelX = relHitN.x; }
                        if (navAhead.IsYieldingForAmbulanceNow) frontBlockerIsYielding = true; // ← 加這行新加
                        if (sameDirN) navAhead.SetYieldForAmbulance(true, this.transform);
                        // targetOffset 移到迴圈外統一算
                    }
                }
               
            }
        }

        // 🚑 對外公開「我被擋住了」。用 stopCriticalDistance 當門檻:
        //    前方有 AI 車而且已經逼近到要收速的距離,才算真的被擋 ——
        //    遠處看得到車不算,那時我還在全速追。
        IsBlockedByTraffic = isAIAhead && closestAIDistance <= stopCriticalDistance;

        // 🚑 救護車固定走在「路網中線往左偏 ambulanceLaneLeftOffset」的線上(不走路網中線),
        //    車流都靠右讓 → 救護車天生就在左半邊,自然避開靠右的車,不用反應式繞行
        //    🔧 但路口/彎道中必須先把左偏收回中線。navLateralOffset != 0 會讓基底類別走進
        //    救護車專屬的瞄準分支(ReverseThreatAICar 的 1664 行),那條分支的側偏是用
        //    「進入路口的方向」算的,而出口方向差 90 度 —— 瞄準點被放到轉彎點的側後方,
        //    還沿著進入方向往前延伸,等於瞄著路口對面直直衝。節點推進的那一幀 aSegDir
        //    整個轉 90 度,瞄準點瞬移到另一邊,車頭去追就是「抖一下 + 原地打轉」。
        //    收回 0 之後那個條件不成立,自動落回白車那條連續的 pure-pursuit 瞄準。
        //    isForcedCurveSlow 在彎前 25m 就亮,laneSwitchSpeed=3.5 收掉 2m 只要 0.57 秒,
        //    進路口前早就回到中線;出彎後旗標熄滅會自己滑回左偏,「走左側」的行為完全保留。
        bool inTurnNow = laneReturnOnlyWhenTurning
                       ? WillActuallyTurnSoon()
                       : (isInIntersection || isForcedCurveSlow);   // 舊行為
        // 🚑 路段可以覆寫左偏量:節點上的 ambulanceLaneOffsetOverride >= 0 就用它。
        //    負數代表「沒設」—— 不能用 0 當哨兵,因為 0 本身是有意義的值
        //    (三線道上讓救護車走車道正中央,不要被推向內側車道)。
        float baseLaneOffset = ambulanceLaneLeftOffset;
        bool fromNode = false;
        if (currentWaypointNode != null && currentWaypointNode.ambulanceLaneOffsetOverride >= 0f)
        {
            baseLaneOffset = currentWaypointNode.ambulanceLaneOffsetOverride;
            fromNode = true;
        }

        if (logLaneOffsetSource && !Mathf.Approximately(baseLaneOffset, lastLoggedLaneOffset)
            && Time.time - lastLaneOffsetLogTime > 0.5f)
        {
            lastLaneOffsetLogTime = Time.time;
            lastLoggedLaneOffset = baseLaneOffset;
            UnityEngine.Debug.Log($"<color=cyan>🚑 [左偏量] {baseLaneOffset:F2}m ← " +
                (fromNode ? $"節點「{currentWaypointNode.name}」的覆寫值"
                          : $"救護車預設值 ambulanceLaneLeftOffset") + "</color>", this);
        }

        float desiredLaneOffset = inTurnNow ? 0f : -baseLaneOffset; // 負 = 左
        currentOvertakeOffset = Mathf.MoveTowards(currentOvertakeOffset, desiredLaneOffset, laneSwitchSpeed * Time.deltaTime);
        ambulanceEvadeOffset = currentOvertakeOffset;
        navLateralOffset = currentOvertakeOffset;

        // 6. 速度決策邏輯 (區分玩家與 AI)
        // 🚑 追趕用的速度上限。跟車、接近、繞行三種情況共用，所以拉到分支外面算一次。
        Rigidbody playerRb = playerTransform != null ? playerTransform.GetComponent<Rigidbody>() : null;
        float playerSpeed = playerRb != null ? playerRb.velocity.magnitude : 0f;

        // ⚠️ 路口與彎道的減速是安全限速，追趕不能覆蓋掉它
        bool curveLimited = isInIntersection || isForcedCurveSlow;
        float chaseSpeed = Mathf.Min(maxChaseSpeed, playerSpeed * chaseSpeedFactor + chaseSpeedBonus);
        float chaseCap = (curveLimited || chaseSpeedFactor <= 0f)
                         ? targetSpeed
                         : Mathf.Max(targetSpeed, chaseSpeed);

        if (isPlayerAhead)
        {
            // 進入跟車模式
            if (closestPlayerDistance < followTriggerDistance)
            {
                isFollowingPlayer = true;
            }

            // 持續鳴笛
            if (Time.time >= nextHornTime)
            {
                PlayWarningHorn();
                nextHornTime = Time.time + hornCooldown;
            }

            if (isFollowingPlayer)
            {
                if (closestPlayerDistance < followDistance)
                {
                    calculatedTargetSpeed = 0f;
                }
                else
                {
                    // 上限從固定的 targetSpeed 換成 chaseCap，玩家開得比巡航速度快時才跟得住
                    calculatedTargetSpeed = Mathf.Min(playerSpeed + 2f, chaseCap);
                }
            }
            else
            {
                // 原本是 Lerp(0, targetSpeed, (dist-20)/(100-20)) —— 那是把玩家當成「前方障礙物」
                // 在減速，60m 外只剩五成速度，玩家開快一點就永遠追不上，變成玩家在等救護車。
                // 這個階段救護車該做的是追上去，只有真的逼近到跟車距離才收速。
                float taperStart = Mathf.Max(followTriggerDistance, stopCriticalDistance + 5f);
                calculatedTargetSpeed =
                    (closestPlayerDistance <= stopCriticalDistance)
                    ? 0f
                    : (closestPlayerDistance >= taperStart
                        ? chaseCap
                        : Mathf.Lerp(0f, chaseCap,
                            (closestPlayerDistance - stopCriticalDistance)
                            / (taperStart - stopCriticalDistance)));
            }
        }
        else if (isAIAhead)
        {
            // 🚑 正在往側邊繞行(導航目標已偏)→ 絕不停,保持蠕行前進,才能真的繞過去
            //    (停下的車 HandleNavigation 不會移動也不會轉向,offset 就沒作用)
            //    🔧 轉彎中 navLateralOffset 被刻意收回 0(見上面的 inTurnNow),不能因此
            //    誤判成「沒在繞行」—— 那會讓救護車在路口內遇到前車時從蠕行變成直接停死。
            bool overtaking = Mathf.Abs(navLateralOffset) > 0.3f || inTurnNow;

            float aiStopDistance = frontBlockerIsYielding ? 4f : 8f;

            // 🚑 救護車永遠走在中線左偏 2m 上，所以 overtaking 幾乎恆為 true —— 原本只要前方
            //    偵測到任何車就一律降到 intersectionSlowSpeed(10)，就算那台車還在 90m 外也一樣，
            //    比玩家還慢，當然追不上。改成只有真的貼近要繞過去時才蠕行。
            if (overtaking && closestAIDistance > stopCriticalDistance)
                calculatedTargetSpeed = chaseCap;              // 前車還遠 → 照常追趕
            else if (overtaking)
                calculatedTargetSpeed = intersectionSlowSpeed; // 真的要繞過去了 → 蠕行,不停
            else if (closestAIDistance <= aiStopDistance)
                calculatedTargetSpeed = 0f;
            else if (closestAIDistance <= stopCriticalDistance)
                calculatedTargetSpeed = intersectionSlowSpeed;
            else
                calculatedTargetSpeed = targetSpeed;
        }
        else
        {
            if (isFollowingPlayer)
            {
                Vector3 toPlayer = playerTransform.position - transform.position;
                float dot = Vector3.Dot(transform.forward, toPlayer.normalized);

                if (dot < 0f)
                {
                    isFollowingPlayer = false;
                    UnityEngine.Debug.Log("<color=green>🚑 玩家已完成避讓</color>");
                }
            }

            // 🟢 新增：射線可能因為玩家轉向偏出角度而完全沒打中，
            // 用直線距離做保底檢查，避免瞬間全速衝出撞上還沒避讓完的玩家
            float directDistanceToPlayer = (playerTransform != null)
                ? Vector3.Distance(transform.position, playerTransform.position)
                : Mathf.Infinity;

            if (directDistanceToPlayer < obstacleDetectionDistance)
            {
                float safetyProgress = Mathf.Clamp01(
                    (directDistanceToPlayer - stopCriticalDistance) /
                    (obstacleDetectionDistance - stopCriticalDistance));

                calculatedTargetSpeed = Mathf.Lerp(0f, targetSpeed, safetyProgress);
            }
            else
            {
                calculatedTargetSpeed = targetSpeed;
            }
        }

        // 7. 呼叫父類別執行最終物理移動
        base.Update();
    }
    /// <summary>
    /// 🚑 這台車「接下來真的會轉彎嗎」。
    ///
    /// 關鍵是用 GetPlannedNode(1) —— 也就是這台車實際規劃要走的下一個節點,
    /// 而不是 currentWaypointNode.nextChoices[0](清單第一項,在多分岔路口常常不是它要走的路)。
    /// </summary>
    private bool WillActuallyTurnSoon()
    {
        if (isBezierActive) return true;   // 已經在轉了,offset 一定要維持 0
        if (currentWaypointNode == null || lastWaypointNode == null) return false;

        TrafficPathNode next = GetPlannedNode(1);
        if (next == null) return false;

        Vector3 inDir = currentWaypointNode.transform.position - lastWaypointNode.transform.position;
        Vector3 outDir = next.transform.position - currentWaypointNode.transform.position;
        inDir.y = 0f; outDir.y = 0f;
        if (inDir.sqrMagnitude < 0.01f || outDir.sqrMagnitude < 0.01f) return false;

        if (Vector3.Angle(inDir, outDir) < laneReturnTurnAngle) return false;   // 直行 → 保持左側

        // 角度夠大,但還很遠就先不收 —— 免得整段路都貼著中線開
        return Vector3.Distance(transform.position, currentWaypointNode.transform.position) < laneReturnDistance;
    }

    // 🚑 救護車選路：有寬路分支就絕不進窄路
    protected override TrafficPathNode PickNextChoice(TrafficPathNode from)
    {
        List<TrafficPathNode> wideChoices = new List<TrafficPathNode>();
        foreach (var n in from.nextChoices)
            if (n != null && !n.isNarrowRoad) wideChoices.Add(n);

        if (wideChoices.Count > 0)
            return wideChoices[Random.Range(0, wideChoices.Count)];

        return from.nextChoices[Random.Range(0, from.nextChoices.Count)]; // 全是窄路才不得已走
    }
    /// <summary>
    /// 執行播放警告喇叭
    /// </summary>
    // reason 有預設值，所以原本兩個呼叫點的行為完全不變
    private void PlayWarningHorn(string reason = "前方玩家佔據車道")
    {
        if (hornAudioSource != null && hornSound != null)
        {
            hornAudioSource.PlayOneShot(hornSound, ambulanceHornVolume);
            UnityEngine.Debug.Log($"<color=orange>📢 [救護車警告] {reason}！嗶嗶——請讓開！</color>");
        }
    }

    private void CheckOvertakeAndDestroy()
    {
        if (playerTransform == null) return;

        float distanceToPlayer = Vector3.Distance(transform.position, playerTransform.position);

        if (distanceToPlayer >= 100f)
        {
            Vector3 dirToAmbulance = (transform.position - playerTransform.position).normalized;
            float dotProduct = Vector3.Dot(playerTransform.forward, dirToAmbulance);

            if (dotProduct > 0f)
            {
                UnityEngine.Debug.Log($"<color=red>🚑 [救護車任務結束] 已成功超越 VR 玩家並拉開距離 ({distanceToPlayer:F1}m)，安全回收銷毀。</color>");

                // 🟢 新增：救護車消失前，通知所有還在避讓自己的車，5秒後如果還卡著就自動銷毀
                foreach (var car in yieldingCars)
                {
                    if (car != null)
                    {
                        car.ScheduleDestroyAfterAmbulanceLeft(5f);
                    }
                }
                yieldingCars.Clear();

                BeginDespawn("超越玩家並拉開距離");
            }
        }
    }

    /// <summary>
    /// 開始收工：先讓警笛淡出，時間到才真的 Destroy。
    ///
    /// 原本兩處都是直接 Destroy(gameObject)，聲音是「被切斷」而不是「開遠了」。
    /// 淡出這段期間救護車照常行駛，所以聽起來就是它一路開走。
    ///
    /// 必須可重複呼叫且無副作用 —— CheckOvertakeAndDestroy 是每幀從 Update 進來的，
    /// 淡出的這 1.5 秒內它會一直再呼叫這個函式。
    /// </summary>
    private void BeginDespawn(string reason)
    {
        if (despawning) return;

        despawning = true;
        despawnStartTime = Time.time;

        if (sirenFadeOutSeconds <= 0f) { Destroy(gameObject); return; }

        UnityEngine.Debug.Log($"<color=red>🚑 [收工] {reason}，警笛淡出 {sirenFadeOutSeconds} 秒後回收</color>", this);
        StartCoroutine(DespawnAfterSirenFade());
    }

    private System.Collections.IEnumerator DespawnAfterSirenFade()
    {
        // 車體縮小交給繼承自 ReverseThreatAICar 的 ForceFadeOutClear()（縮到 1% 再刪除）。
        // 它需要 fadeOutDuration 秒，所以提前這麼多啟動，讓「聲音收完」與「車體縮完」同時結束 ——
        // 否則車已經縮不見了，警笛還在空氣中響。
        float shrinkLead = Mathf.Min(fadeOutDuration, sirenFadeOutSeconds);
        yield return new WaitForSeconds(Mathf.Max(0f, sirenFadeOutSeconds - shrinkLead));

        // 保底：萬一 SirenEscalationRoutine 已經停了，音量沒被拉到 0，這裡直接靜音
        if (sirenAudioSource != null && shrinkLead >= sirenFadeOutSeconds)
            sirenAudioSource.volume = 0f;

        // 縮小淡出而不是瞬間消失。它會先關碰撞體、把 Rigidbody 設成 kinematic，
        // 避免縮放趨近 0 時 PhysX 算慣性張量除以零噴 NaN
        ForceFadeOutClear();
    }

    private IEnumerator SirenFlashRoutine()
    {
        while (true)
        {
            // 左側爆閃 (雙擊)
            SetLightStatusDirect(true, true); SetLightStatusDirect(false, false);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(true, false);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(true, true);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(true, false);
            yield return new WaitForSeconds(doubleFlashDelay);

            // 右側爆閃 (雙擊)
            SetLightStatusDirect(true, false); SetLightStatusDirect(false, true);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(false, false);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(false, true);
            yield return new WaitForSeconds(flashInterval);
            SetLightStatusDirect(false, false);
            yield return new WaitForSeconds(doubleFlashDelay);
        }
    }

    private void SetLightStatusDirect(bool isLeft, bool isOn)
    {
        Material targetMat = isLeft ? leftSirenMat : rightSirenMat;
        MeshRenderer targetRenderer = (isLeft && sirenLightRenderers.Count > 0) ? sirenLightRenderers[0] : ((!isLeft && sirenLightRenderers.Count > 1) ? sirenLightRenderers[1] : null);

        if (targetMat != null && targetRenderer != null)
        {
            if (isOn)
            {
                targetMat.EnableKeyword("_EMISSION");
                targetMat.SetColor("_EmissionColor", (isLeft ? Color.red : Color.blue) * sirenEmissionIntensity);
            }
            else
            {
                targetMat.SetColor("_EmissionColor", Color.clear);
                targetMat.DisableKeyword("_EMISSION");
            }
            DynamicGI.SetEmissive(targetRenderer, targetMat.GetColor("_EmissionColor"));
        }
    }

    protected override void OnTriggerEnter(Collider other)
    {
        base.OnTriggerEnter(other); // 🟢 讓 Intersection/WideRoad 旗標照
        // 🟢 三重過濾：在我前方＋同方向＋在我的車道走廊內，才需要避讓
        // （對向車、隔壁車道的車不再被誤傷）
        ReverseThreatAICar car = other.GetComponentInParent<ReverseThreatAICar>();
        if (car != null && !(car is AmbulanceAI))
        {
            Vector3 rel = transform.InverseTransformPoint(car.transform.position);
            bool inMyLaneAhead = rel.z > 0f && Mathf.Abs(rel.x) < 3.5f; // 3.5≈半車道寬+餘裕，依地圖調
            bool sameDirection = Vector3.Dot(transform.forward, car.transform.forward) > 0.4f;

            if (inMyLaneAhead && sameDirection)
            {
                car.SetYieldForAmbulance(true, this.transform);
                if (!yieldingCars.Contains(car)) yieldingCars.Add(car);
            }
            return;
        }

        // 🟢 CarNavigation 車也比照同一套過濾（方案2的B2）
        CarNavigation navCar = other.GetComponentInParent<CarNavigation>();
        if (navCar != null)
        {
            Vector3 relNav = transform.InverseTransformPoint(navCar.transform.position);
            bool navInLaneAhead = relNav.z > 0f && Mathf.Abs(relNav.x) < 3.5f;
            bool navSameDirection = Vector3.Dot(transform.forward, navCar.transform.forward) > 0.4f;

            if (navInLaneAhead && navSameDirection)
                navCar.SetYieldForAmbulance(true, this.transform);
        }
    }

    protected override void OnTriggerExit(Collider other)
    {
        base.OnTriggerExit(other);
        ReverseThreatAICar car = other.GetComponentInParent<ReverseThreatAICar>();
        if (car != null)
        {
            car.SetYieldForAmbulance(false);
            yieldingCars.Remove(car);
        }

        CarNavigation navCarExit = other.GetComponentInParent<CarNavigation>();
        if (navCarExit != null) navCarExit.SetYieldForAmbulance(false);
    }
    private void OnDrawGizmosSelected()
    {
        // 🚦 路口淨空作用範圍(黃色球):範圍內的「橫向」灰車會在路口前停等
        if (enableJunctionHold)
        {
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = new Color(1f, 0.85f, 0f, 0.9f);
            Gizmos.DrawWireSphere(transform.TransformPoint(new Vector3(0f, 1f, junctionHoldLookAhead)),
                                  junctionHoldRadius);
        }

        Gizmos.color = Color.cyan;

        // 不要使用父類別的 Matrix，直接用世界座標
        Vector3 center = transform.TransformPoint(ambulanceStartOffset);

        // 這是最保險的畫法，不依賴任何父類別的 Matrix 設定
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.DrawWireCube(center, ambulanceStartSize);
        // 📢 警笛廣播盒(洋紅色):前方 50m 走廊,掃到的同向 AI 車會收到避讓訊號
        Gizmos.color = Color.magenta;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(
            new Vector3(sirenBoxLateralOffset, 1f, sirenBoxLength * 0.5f),
            new Vector3(sirenBoxWidth, 3f, sirenBoxLength));
        Gizmos.matrix = Matrix4x4.identity;
    }
}