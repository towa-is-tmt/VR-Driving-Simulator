using System.Collections;
using UnityEngine;

/// <summary>
/// 🚶 穿越馬路的行人 —— 給「前車急煞」當看得見的原因。
///
/// 為什麼要有這支：舊版的急煞是「車在空曠路上突然煞停」，沒有任何理由。
/// 玩家的大腦會把它歸類成「遊戲在考我」而不是「路況發生了」，
/// 一旦被歸類成前者，學習遷移等於零 —— 他學到的是「這個路口有機關」，
/// 不是「開車要把視線放遠、要看穿前車」。
/// 有了行人，因果就完整了：前車煞車 → 喔前面有人在過馬路 → 我剛剛應該看更遠。
///
/// 為什麼用程式驅動骨骼而不是播動畫：
/// 專案裡沒有任何走路動畫（車流資料/anin 只有 Mixamo 的 Driving.fbx），
/// 而人物模型是 Generic 骨架（animationType: 3），Humanoid 的重定向與 IK 都用不了。
/// 對「從行駛中的車看出去、距離 20~50m、只出現幾秒」的路人來說，
/// 程序化的擺腿＋擺手＋身體起伏已經足夠讀成走路，不值得為它卡在素材上。
///
/// 骨架命名沿用專案既有的 Biped 慣例（LThigh/LCalf/LFoot、LUpperArm/LForearm）。
/// 找不到的骨骼會跳過，不會報錯 —— 換模型時最多就是少擺一隻手。
/// </summary>
public class CrossingPedestrian : MonoBehaviour
{
    [Header("定位方式")]
    // 固定的 Start/End Point 有個根本問題：行人的位置是場景裡寫死的，
    // 但演員車是每次動態挑的（40m 內玩家前方那台）—— 兩者沒有任何關聯，
    // 所以行人常常出現在車陣後方或旁邊，玩家看到的因果完全不成立。
    //
    // 打開這個之後，穿越路徑改成「相對演員車」即時算出來：
    // 一定是從演員車前方的路側走向對側，玩家看到的順序才會是 玩家 → 前車 → 行人。
    [Tooltip("穿越路徑相對「演員車」即時計算，保證出現在前車的前方。\n" +
             "關掉＝用下面固定的 Start/End Point（行人可能出現在車陣後方）")]
    public bool positionRelativeToActor = true;
    [Tooltip("在演員車前方多少公尺處穿越。要留給前車煞停的距離，建議 15~25")]
    public float aheadOfActor = 20f;
    [Tooltip("從車的哪一側出發（公尺）。正 = 車的右側，負 = 左側")]
    public float kerbSideOffset = 6f;
    [Tooltip("穿越的總寬度（公尺）。從出發側走到對側的距離")]
    public float crossDistance = 12f;

    // Ahead Of Actor 設得夠遠時，穿越點可能落在「演員車前面還有一兩台車」的位置。
    // 那樣因果就顛倒了：離行人最近的是前面那幾台，卻是後面的演員車在煞車。
    // 所以放置時要往前掃，發現有車就把穿越點拉到那台車的後方，
    // 保證「演員車就是離行人最近的那一台」。
    [Tooltip("放置穿越點時，若演員車與該點之間還有別的車，就把穿越點往回拉到那台車後方。\n" +
             "關掉＝完全照 Ahead Of Actor（前面有車時因果會看起來很怪）")]
    public bool keepActorNearest = true;
    [Tooltip("拉回來時要離前方那台車留多少餘裕（公尺）")]
    public float nearestCarClearance = 6f;
    [Tooltip("往前掃描車輛時要看哪些層。至少要包含 AI 車的層")]
    public LayerMask vehicleLayer = ~0;

    [Header("固定路徑（Position Relative To Actor 關閉時才用）")]
    [Tooltip("起點（人行道上待命的位置）。留空就用物件目前的位置")]
    public Transform startPoint;
    [Tooltip("終點（對面人行道）")]
    public Transform endPoint;

    // 固定路徑的問題一直是「位置對了但時機不對」：她一收到訊號就開始走，
    // 而演員車那時可能還很遠或已經開過去了。
    // 打開這個之後，她會先在起點等，直到車開到「剛好夠她跑到車道中間」的距離才起步 ——
    // 路徑由你手動指定（走在真正的行人穿越道上），時機由程式算。
    [Tooltip("在起點等到演員車開到適當距離才起步。關掉＝一收到訊號就走（位置對但時機隨機）")]
    public bool timeFixedPathToActor = true;
    [Tooltip("她跑到車道中間時，車應該還剩多少距離（公尺）。這就是留給前車煞停的空間")]
    public float fixedPathCarGap = 25f;
    [Tooltip("等車的上限（秒）。逾時就直接起步，避免事件卡住不發生")]
    public float fixedPathWaitTimeout = 12f;

    // 動態模式下即時算出來的起終點
    private Vector3 dynamicStart, dynamicEnd, pavementStart;
    private Transform waitForCar;      // 固定路徑計時要盯的車（演員車，或玩家計時模式的玩家車）
    private float waitForCarSpeed;     // 它當下的速度，用來換算距離
    private bool useDynamic = false;
    private Coroutine crossRoutine;    // 這次穿越的協程 handle，玩家改道時要能中止它

    // 🚗 玩家計時模式（BeginCrossingTimedToPlayer）用的狀態
    private Rigidbody waitForCarRb;    // 玩家車的 Rigidbody，等待期間每幀重讀車速用
    private bool sampleLiveSpeed = false;   // true = 每幀重讀車速（玩家速度變化大，不能只抓一次）
    private bool clearCorridorNow = false;  // 這次穿越要不要清「玩家 → 穿越線」的走廊
    private Coroutine corridorRoutine;      // 清走廊的協程 handle

    [Header("步行參數")]
    [Tooltip("步行速度（公尺/秒）。成人正常步行約 1.2~1.5，趕路約 1.8")]
    public float walkSpeed = 1.4f;
    // 直接出現在路邊再衝出去有兩個問題：出現的那一刻玩家看得到（憑空冒出來），
    // 而且「站著張望」那一拍在靜止狀態下根本看不出來。
    // 改成先沿人行道走一段再到路口：現身時人在移動中、位置也離路口更遠，
    // 玩家的注意力在車道上，比較不會抓到那一幀；走到路口停下的「頓」也才讀得出來。
    [Tooltip("先沿人行道往前走這麼多公尺才到穿越點（公尺）。0 = 直接出現在穿越點")]
    public float pavementApproachDistance = 8f;

    [Tooltip("走到路口後、踏出去前，站著張望幾秒。要看得出來的話至少 1.0")]
    public float lookBeforeCrossing = 1.2f;
    [Tooltip("張望時播的動畫狀態（例如 Woman_Idle）。留空＝維持上一個動作")]
    public string lookStateName = "";
    // 到終點就停下來的話，跑步動畫會在原地繼續播到消失為止（Idle State Name 留空時尤其明顯）。
    // 讓她轉向繼續跑離開，動畫自然是對的，而且是「跑掉」而不是「原地消失」——
    // 真人過完馬路本來就會繼續走，停在路邊發呆反而奇怪。
    //
    // 跑的方向刻意選「玩家來的方向」= 玩家車的後方：她會離開玩家的視野，
    // 消失那一刻多半已經在畫面外，看不到 pop。
    [Tooltip("抵達終點後轉向繼續跑離開的距離（公尺）。0 = 到終點就停下")]
    public float exitRunDistance = 20f;

    [Tooltip("跑離開之後幾秒消失。0 = 留在原地不消失")]
    public float despawnAfterSeconds = 3f;

    [Header("動畫（有指定就優先用，程序化步態自動停用）")]
    // 程序化步態是「沒有動畫檔時的備案」。真的有動畫一定要用動畫 ——
    // 手調骨骼永遠做不出正確的跑姿，這點實測已經證明過了。
    [Tooltip("角色的 Animator。留空＝改用下面的程序化步態")]
    public Animator animator;
    [Tooltip("跑步／走路狀態在 Animator Controller 裡的名稱（例如 Male_Run）")]
    public string moveStateName = "Male_Run";
    [Tooltip("待機狀態的名稱。走完會切回這個，留空就不切")]
    public string idleStateName = "Male_Idle";

    /// <summary>有 Animator 就交給動畫，不再用程序化步態</summary>
    private bool UseAnimator => animator != null && !string.IsNullOrEmpty(moveStateName);

    [Header("步態（程序化，只在沒指定 Animator 時才用）")]
    // 預設值是「跑步」的：橫越馬路的行人本來就會小跑，而且跑步姿勢的辨識度比走路高，
    // 玩家從行駛中的車看出去更容易一眼認出「有人衝出來」。
    // 走路的話：Steps 1.9、Thigh 28、Knee 最大 35、Arm 22、Elbow 25、Lean 0。
    [Tooltip("每秒幾步。走路約 1.8~2.0，慢跑約 2.6~3.0")]
    public float stepsPerSecond = 2.8f;
    [Tooltip("大腿前後擺動的角度。走路約 28，跑步約 40~50")]
    public float thighSwingAngle = 45f;

    [Tooltip("膝蓋在「收腿」時的最大彎曲角度。跑步時腳跟會接近臀部，約 80~95")]
    public float kneeBendMax = 88f;
    [Tooltip("膝蓋最小彎曲角度。跑步時膝蓋全程都保持一點彎，不會打直")]
    public float kneeBendMin = 18f;
    [Tooltip("膝蓋彎錯邊（往前反折）就打勾")]
    public bool invertKneeBend = false;

    [Tooltip("肩膀前後擺動的角度。跑步約 35~45")]
    public float armSwingAngle = 40f;
    [Tooltip("手肘固定彎曲的角度。跑步姿勢手肘約 85~90 度且全程維持，不會打直")]
    public float elbowBendAngle = 85f;
    [Tooltip("手臂擺反了（跟同側腳同方向）就打勾")]
    public bool invertArmSwing = false;
    [Tooltip("手肘彎錯邊就打勾")]
    public bool invertElbowBend = false;

    [Tooltip("身體前傾角度。跑步時軀幹會前傾約 5~10 度")]
    public float bodyLeanAngle = 8f;
    [Tooltip("上下起伏的幅度（公尺）。跑步比走路明顯，約 0.05~0.08")]
    public float bodyBobHeight = 0.06f;

    [Header("貼地")]
    // 起點/終點的空物件高度不可能剛好等於路面，而且路面本身有高低起伏 ——
    // 只用起終點內插一定會浮空或陷進地面。每幀往下打一條射線把腳放到地上。
    [Tooltip("關掉＝完全照起點/終點的高度走（會浮空）")]
    public bool snapToGround = true;
    [Tooltip("射線要打到哪些層。至少要包含路面與人行道")]
    public LayerMask groundLayer = ~0;
    [Tooltip("從腳底往上多高開始往下打射線（公尺）")]
    public float groundRayHeight = 2f;
    [Tooltip("貼地後再往上墊多少（公尺）。模型原點不在腳底時用這個修")]
    public float groundOffset = 0f;
    [Tooltip("高度變化的平滑速度。0 = 直接貼齊（路面接縫會抖）。越大越快跟上，8~15 之間通常剛好")]
    public float groundSnapSmooth = 10f;

    [Header("顯示時機")]
    // 行人平常站在場景裡不動很出戲，而且事件發動時會「瞬移」到演員車前方 ——
    // 玩家如果剛好在看那個方向就會看到人憑空消失又出現。
    // 藏起來等到要用才現身，順便也讓瞬移完全看不見。
    //
    // 用關閉 Renderer 而不是 SetActive(false)：整個物件停用的話協程會被中斷，
    // 而且 Animator 重新啟用時狀態會亂掉。關 Renderer 只影響畫面，邏輯照跑。
    [Tooltip("平常隱藏，事件發動、站到定位之後才現身。走完會再度隱藏")]
    public bool hideUntilCrossing = true;

    private Renderer[] cachedRenderers;

    [Header("被撞判定")]
    // 為什麼不掛 Collider 用物理：
    //   非 Kinematic → 她是用 transform.position += 推的，PhysX 會把她彈飛
    //   Kinematic    → 她變成不可推動的牆，車撞上去直接停死，比穿過去更怪
    // 沒有倒地動畫的話兩種都難看。所以用腳本每幀量距離，只負責「記錄這次撞到了」，
    // 完全不產生物理反應 —— 教學上真正重要的是「撞到要被判定為失敗」，不是物理表現。
    //
    // ⚠️ 沒有這段的話，玩家把她輾過去，危險感知照樣給「反應良好」。
    [Tooltip("關掉＝完全不判定被撞（玩家輾過去不會被記錄）")]
    public bool detectVehicleHit = true;
    [Tooltip("車輛中心離她多近算撞到（公尺）。車身半寬約 1m，行人半徑約 0.3m，建議 1.2~1.8")]
    public float hitRadius = 1.5f;
    [Tooltip("要偵測哪些層的車輛。玩家車與 AI 車的層都要包含")]
    public LayerMask hitLayer = ~0;

    /// <summary>這次穿越有沒有被車撞到</summary>
    public bool WasHit { get; private set; }

    [Header("🚗 對玩家清出走廊（只在玩家計時模式 BeginCrossingTimedToPlayer 才用）")]
    // 塞車時,玩家到行人之間常常卡著別的車 —— 行人衝出的位置就被推到別台車前面,
    // 玩家(被堵住停下)根本沒感覺。這裡沿用故障車那套「膠囊走廊 + 縮小淡出」,
    // 把「玩家車 → 穿越線」那段路清乾淨,確保行人衝出時玩家正前方是空的、有壓迫感。
    [Tooltip("玩家踩到觸發盒後,把「玩家車 → 行人穿越線」那段路的車縮小淡出清掉。關掉＝不清")]
    public bool clearCorridorForPlayer = true;
    [Tooltip("走廊半寬(公尺)。約一個車道;太寬會把隔壁車道的車也清掉")]
    public float corridorHalfWidth = 6f;
    [Tooltip("走廊在穿越線之外再往前延伸多少公尺(行人前面也留一小段淨空)")]
    public float corridorAheadOfCrossing = 6f;
    [Tooltip("玩家鼻子前這個距離內的車不清,避免貼臉縮小消失(公尺)")]
    public float corridorPlayerSafeRadius = 10f;
    [Tooltip("清走廊的重掃間隔(秒)。側邊路口會有車開進這段路,只清一次擋不住")]
    public float corridorRecheckInterval = 1f;
    [Tooltip("被清的車縮小淡出的秒數")]
    public float corridorFadeSeconds = 0.8f;

    [Header("診斷")]
    public bool logCrossing = false;

    /// <summary>目前是否正在穿越（含站著張望那段）</summary>
    public bool IsCrossing { get; private set; }
    /// <summary>是否已經踏出人行道開始移動 —— 前車該在這之後才煞車</summary>
    public bool HasSteppedOut { get; private set; }

    /// <summary>
    /// 從「開始這次穿越」到「真的踏出人行道」預計要多久（秒）。
    /// 沿人行道走過來 ＋ 站著張望，兩段加起來。
    /// 外部等待時用這個算逾時，不要自己寫死秒數 ——
    /// 這些參數會被調整，寫死的話一改就不同步（曾經因此讓前車在行人還在張望時就煞車）。
    /// </summary>
    public float EstimatedStepOutDelay
    {
        get
        {
            float approach = (positionRelativeToActor && pavementApproachDistance > 0.1f && walkSpeed > 0.01f)
                ? pavementApproachDistance / walkSpeed : 0f;
            return approach + Mathf.Max(0f, lookBeforeCrossing);
        }
    }

    /// <summary>
    /// 從「踏出人行道」到「跑進車道」預計要多久（秒）。
    /// 她從路側 kerbSideOffset 公尺處起跑，要跑到車道中線才算擋住車。
    /// </summary>
    public float EstimatedRunIntoLaneTime =>
        walkSpeed > 0.01f ? Mathf.Abs(kerbSideOffset) / walkSpeed : 0f;

    /// <summary>
    /// 行人是不是已經跑到這台車的正前方（進入它的行進走廊）。
    /// 「踏出人行道」只代表她開始動了，這時她還在路側 —— 那時煞車太早，
    /// 玩家看到的是「前車無故煞停，旁邊有個人在走」。要等她真的擋在車前才合理。
    /// </summary>
    public bool IsInFrontOf(Transform car, float laneHalfWidth = 2.5f, float maxAhead = 60f)
    {
        if (car == null) return false;

        Vector3 rel = transform.position - car.position; rel.y = 0f;
        Vector3 fwd = car.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) return false;
        fwd.Normalize();

        float ahead = Vector3.Dot(rel, fwd);
        if (ahead <= 0f || ahead > maxAhead) return false;          // 在後方或太遠

        float lateral = Mathf.Abs(Vector3.Dot(rel, Vector3.Cross(Vector3.up, fwd)));
        return lateral <= laneHalfWidth;
    }

    // 骨骼參照，開場找一次
    private Transform lThigh, rThigh, lCalf, rCalf, lUpperArm, rUpperArm, lForearm, rForearm, spine, pelvis;
    private Quaternion lThigh0, rThigh0, lCalf0, rCalf0, lUpperArm0, rUpperArm0, lForearm0, rForearm0, spine0;
    private Vector3 pelvis0Pos;
    private float gaitTime = 0f;

    // 動畫狀態的保險用：離開目標狀態多久了、名稱是否曾經對上過、有沒有報過警告
    private float outOfStateTimer = 0f;
    private bool stateNameVerified = false;
    private bool warnedStateName = false;

    private void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>(true);
        if (animator != null)
            // 位移完全由程式控制（要走到指定的終點），動畫只負責姿勢 ——
            // 留著 root motion 的話動畫自己會把角色帶走，兩邊打架
            animator.applyRootMotion = false;
        else
            CacheBones();

        if (!positionRelativeToActor && startPoint != null) transform.position = startPoint.position;

        cachedRenderers = GetComponentsInChildren<Renderer>(true);
        if (hideUntilCrossing) SetVisible(false);
    }

    /// <summary>只開關畫面，不動 GameObject 的啟用狀態（協程與 Animator 要繼續跑）</summary>
    private void SetVisible(bool visible)
    {
        if (cachedRenderers == null) return;
        foreach (Renderer r in cachedRenderers)
            if (r != null) r.enabled = visible;
    }

    /// <summary>
    /// 找骨骼。專案裡至少有兩套命名並存，所以每個部位都給多個候選名：
    ///   汽車駕駛模型 = 3ds Max Biped（LThigh / LCalf / LUpperArm / Pelvis）
    ///   路人與機車騎士 = Mixamo 標準（LeftUpLeg / LeftLeg / LeftArm / Hips）
    /// 只認一套的話換個模型就整個不會動，而且是「靜靜地不動」最難查。
    /// </summary>
    private void CacheBones()
    {
        lThigh    = FindBone("LeftUpLeg", "LThigh");
        rThigh    = FindBone("RightUpLeg", "RThigh");
        lCalf     = FindBone("LeftLeg", "LCalf");
        rCalf     = FindBone("RightLeg", "RCalf");
        lUpperArm = FindBone("LeftArm", "LUpperArm");
        rUpperArm = FindBone("RightArm", "RUpperArm");
        lForearm  = FindBone("LeftForeArm", "LForearm");
        rForearm  = FindBone("RightForeArm", "RForearm");
        spine     = FindBone("Spine", "Spine1");
        pelvis    = FindBone("Hips", "Pelvis");

        if (lThigh != null) lThigh0 = lThigh.localRotation;
        if (rThigh != null) rThigh0 = rThigh.localRotation;
        if (lCalf != null) lCalf0 = lCalf.localRotation;
        if (rCalf != null) rCalf0 = rCalf.localRotation;
        if (lUpperArm != null) lUpperArm0 = lUpperArm.localRotation;
        if (rUpperArm != null) rUpperArm0 = rUpperArm.localRotation;
        if (lForearm != null) lForearm0 = lForearm.localRotation;
        if (rForearm != null) rForearm0 = rForearm.localRotation;
        if (spine != null) spine0 = spine.localRotation;
        if (pelvis != null) pelvis0Pos = pelvis.localPosition;

        // 骨骼沒找到就是「靜靜地不會動」，最難查 —— 一定要主動報，而且要報出實際的骨骼名字
        if (lThigh == null || rThigh == null || lUpperArm == null)
        {
            string sample = "";
            int n = 0;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t == transform) continue;
                if (n++ < 25) sample += t.name + "  ";
            }
            Debug.LogError(
                $"[行人] {name} 找不到走路需要的骨骼，動作不會有任何擺動（會變成漂浮）。\n" +
                $"  大腿 L/R = {(lThigh ? "✔" : "✘")}/{(rThigh ? "✔" : "✘")}   " +
                $"小腿 = {(lCalf ? "✔" : "✘")}/{(rCalf ? "✔" : "✘")}   " +
                $"上臂 = {(lUpperArm ? "✔" : "✘")}/{(rUpperArm ? "✔" : "✘")}   " +
                $"Hips = {(pelvis ? "✔" : "✘")}\n" +
                $"  這個模型實際的骨骼名稱（前 25 個）：{sample}\n" +
                $"  把對應的名字加進 CacheBones() 的候選清單即可", this);
        }
        else if (logCrossing)
        {
            Debug.Log($"<color=lime>[行人] {name} 骨骼綁定成功：" +
                      $"{lThigh.name} / {lCalf?.name} / {lUpperArm.name} / {pelvis?.name}</color>", this);
        }
    }

    /// <summary>依序試多個候選名。找不到再試「結尾符合」，處理 mixamorig: 這類前綴。</summary>
    private Transform FindBone(params string[] candidates)
    {
        Transform[] all = GetComponentsInChildren<Transform>(true);

        foreach (string want in candidates)
        {
            foreach (Transform t in all)
                if (t.name == want) return t;
        }
        // 退一步：容忍 "mixamorig:LeftUpLeg" 這種帶前綴的命名
        foreach (string want in candidates)
        {
            foreach (Transform t in all)
                if (t.name.EndsWith(want, System.StringComparison.OrdinalIgnoreCase)) return t;
        }
        return null;
    }

    /// <summary>
    /// 🚶 開始穿越。由 ReverseTriggerController 在急煞事件發動前呼叫 ——
    /// 行人先踏出來，前車才煞車，因果順序才對。
    /// </summary>
    public void BeginCrossing() => BeginCrossing(null);

    /// <param name="actorCar">
    /// 要為這台車製造煞車理由的演員車。傳進來就用「相對它的位置」算穿越路徑，
    /// 保證行人出現在它的前方 —— 傳 null 或關掉 positionRelativeToActor 就用固定的 Start/End Point。
    /// </param>
    public void BeginCrossing(Transform actorCar) => BeginCrossing(actorCar, 0f);

    /// <param name="actorSpeed">
    /// 演員車目前的速度（m/s）。用來預測它在行人真正踏出人行道時會開到哪裡 ——
    /// 不預測的話「車前方 20 公尺」會在等待期間被車自己吃掉，行人等於在車頭旁邊冒出來。
    /// </param>
    public void BeginCrossing(Transform actorCar, float actorSpeed)
    {
        if (IsCrossing) return;

        // 舊路徑不用玩家計時 / 清道,把旗標歸零(避免上一次玩家計時模式殘留)
        sampleLiveSpeed = false;
        clearCorridorNow = false;
        waitForCarRb = null;

        useDynamic = positionRelativeToActor && actorCar != null;
        waitForCar = actorCar;
        waitForCarSpeed = actorSpeed;

        if (useDynamic)
        {
            Vector3 fwd = actorCar.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) { useDynamic = false; }
            else
            {
                fwd.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, fwd);

                // 從現在到「行人踏出人行道」中間有兩段：沿人行道走過去 + 站著張望，
                // 車在這段時間會往前跑一大截（時速 47km/h ≈ 每秒 13 公尺）。
                // 要用它「屆時的位置」當基準，Ahead Of Actor 才是玩家真正看到的距離。
                float approachTime = walkSpeed > 0.01f ? pavementApproachDistance / walkSpeed : 0f;
                float leadTime = approachTime + lookBeforeCrossing;
                Vector3 predicted = actorCar.position + fwd * (actorSpeed * leadTime);

                // 演員車與穿越點之間如果還夾著別的車，因果會顛倒 ——
                // 離行人最近的是前面那幾台，卻是後面的演員車在煞車。
                // 掃一下把距離收到「最前面那台車的後方」，演員車才會是最靠近行人的那台。
                float ahead = aheadOfActor;
                if (keepActorNearest)
                {
                    float blocked = DistanceToNextVehicle(actorCar, fwd, aheadOfActor);
                    if (blocked > 0f)
                        ahead = Mathf.Max(4f, blocked - nearestCarClearance);
                }

                dynamicStart = predicted + fwd * ahead + right * kerbSideOffset;
                dynamicEnd = dynamicStart - right * crossDistance;
                // 人行道上的進場點：從穿越點往「車來的反方向」退一段，
                // 這樣她是迎著玩家走過來的，現身位置也更遠
                pavementStart = dynamicStart + fwd * pavementApproachDistance;
            }
        }

        if (!useDynamic && endPoint == null)
        {
            Debug.LogWarning($"[行人] {name} 沒有演員車也沒有 End Point，不會穿越", this);
            return;
        }
        crossRoutine = StartCoroutine(CrossRoutine());
    }

    /// <summary>
    /// 🚶🚗 玩家計時的固定路徑穿越 —— 給「行人對玩家衝出」用。
    ///
    /// 跟前車煞車那套的差別:
    ///   • 路線固定:走場景裡的 Start/End Point(真正的穿越線),不動態切到玩家車頭前。
    ///   • 時機看玩家:用玩家車的車速與距離,算她何時起步才會「剛好」擋在玩家前方。
    ///   • 清走廊:把「玩家車 → 穿越線」那段路的車縮小淡出,確保衝出時玩家正前方是空的。
    ///   • 沒有演員車、沒有前車煞車 —— 行人本身就是危險因子(被撞會回報 HazardAwarenessTracker)。
    /// </summary>
    /// <param name="playerCar">玩家車的車體 Transform(建議傳 Rigidbody 那顆,車頭方向才準)</param>
    public void BeginCrossingTimedToPlayer(Transform playerCar)
    {
        if (IsCrossing) return;
        if (playerCar == null)
        {
            Debug.LogWarning($"[行人] {name} BeginCrossingTimedToPlayer 沒有傳玩家車,取消", this);
            return;
        }
        if (startPoint == null || endPoint == null)
        {
            Debug.LogWarning($"[行人] {name} 玩家計時模式需要固定的 Start/End Point(場景裡的穿越線),取消", this);
            return;
        }

        useDynamic = false;                 // 一律走固定路徑,不做動態切位
        waitForCar = playerCar;
        waitForCarRb = playerCar.GetComponentInParent<Rigidbody>();
        waitForCarSpeed = waitForCarRb != null ? waitForCarRb.velocity.magnitude : 0f;
        sampleLiveSpeed = true;             // 玩家速度變化大,等待期間每幀重讀
        clearCorridorNow = clearCorridorForPlayer;

        crossRoutine = StartCoroutine(CrossRoutine());
        if (clearCorridorNow)
            corridorRoutine = StartCoroutine(ClearCorridorRoutine());
    }

    /// <summary>
    /// 🚪 中止這次穿越並讓行人立刻消失（不跑「過完馬路再離場」那一整套）。
    ///
    /// 給「玩家踩到觸發盒之後卻轉去別條路」用：這台演員車已經跟玩家無關，
    /// 行人再衝出來也是演給空氣看。玩家已經轉向、看不到這邊，直接隱藏最乾淨；
    /// 硬讓她走完或把她拉回人行道，反而可能被餘光瞄到「有人瞬間消失/倒退」。
    ///
    /// 只負責收行人本身。對應的「前車急煞」評語要不要撤，由呼叫端
    /// (ReverseTriggerController) 決定 —— 那是危險感知的帳，不歸行人管。
    /// </summary>
    public void CancelCrossing()
    {
        if (!IsCrossing) return;

        if (crossRoutine != null) { StopCoroutine(crossRoutine); crossRoutine = null; }
        if (corridorRoutine != null) { StopCoroutine(corridorRoutine); corridorRoutine = null; }

        // 姿勢還原，避免下次現身時還停在跑步的半截姿勢
        if (UseAnimator)
        {
            if (!string.IsNullOrEmpty(idleStateName)) animator.Play(idleStateName, 0, 0f);
        }
        else ResetPose();

        IsCrossing = false;
        HasSteppedOut = false;
        SetVisible(false);   // 直接消失（只關 Renderer，物件與協程狀態不動，下次還能重用）

        if (logCrossing)
            Debug.Log($"<color=grey>🚪 [行人] {name} 玩家已改道，穿越取消、直接隱藏</color>", this);
    }

    /// <summary>
    /// 從演員車往前掃，回傳「最近的另一台車」離它多遠。沒有車就回 -1。
    /// 用來把穿越點收到那台車後方，確保演員車是離行人最近的一台。
    /// </summary>
    private float DistanceToNextVehicle(Transform actorCar, Vector3 fwd, float range)
    {
        Vector3 center = actorCar.position + Vector3.up * 0.5f + fwd * (range * 0.5f);
        Vector3 half = new Vector3(2.2f, 1f, range * 0.5f);

        Collider[] hits = Physics.OverlapBox(center, half, Quaternion.LookRotation(fwd, Vector3.up),
                                             vehicleLayer, QueryTriggerInteraction.Ignore);
        float nearest = -1f;

        foreach (Collider c in hits)
        {
            if (c == null) continue;
            if (c.transform.IsChildOf(actorCar.root)) continue;   // 演員車自己

            bool isVehicle = c.GetComponentInParent<ReverseThreatAICar>() != null
                          || c.GetComponentInParent<CarNavigation>() != null;
            if (!isVehicle) continue;

            Vector3 to = c.transform.position - actorCar.position; to.y = 0f;
            float d = Vector3.Dot(to, fwd);
            if (d <= 1f) continue;                                 // 在後方或並排的不算

            if (nearest < 0f || d < nearest) nearest = d;
        }
        return nearest;
    }

    /// <summary>
    /// 從起點跑到「擋住這台車」需要幾秒。
    /// 取穿越路徑上離「車的行進軸線」最近的那一點，算她跑到那裡的距離 ÷ 速度。
    /// 固定路徑是手拉的，跟車道的相對位置每個點都不一樣，所以要實際投影算，不能寫死。
    /// </summary>
    private float TimeToReachLaneOf(Transform car)
    {
        if (car == null || walkSpeed < 0.01f) return 0f;

        Vector3 a = CrossStart, b = CrossEnd;
        Vector3 seg = b - a; seg.y = 0f;
        if (seg.sqrMagnitude < 0.01f) return 0f;

        // 車的行進軸線：以車為原點、沿 forward 的直線。找路徑上離這條線最近的點。
        Vector3 fwd = car.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) return 0f;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);

        // 把路徑兩端投影到車的橫向軸，找出橫向偏移為 0 的那個比例
        float la = Vector3.Dot(a - car.position, right);
        float lb = Vector3.Dot(b - car.position, right);

        float t = Mathf.Approximately(la, lb) ? 0.5f : Mathf.Clamp01(la / (la - lb));
        float distAlongPath = seg.magnitude * t;

        return distAlongPath / walkSpeed;
    }

    /// <summary>這次穿越的起點／終點（動態模式與固定模式共用出口）</summary>
    private Vector3 CrossStart => useDynamic ? dynamicStart
                                : (startPoint != null ? startPoint.position : transform.position);
    private Vector3 CrossEnd => useDynamic ? dynamicEnd : endPoint.position;

    private IEnumerator CrossRoutine()
    {
        IsCrossing = true;
        HasSteppedOut = false;

        // ① 人行道進場：從穿越點往後退一段現身，沿著人行道走過來。
        //    現身時人在移動中、離路口也更遠，玩家的注意力在車道上，比較不會抓到那一幀。
        bool doApproach = useDynamic && pavementApproachDistance > 0.1f;
        transform.position = doApproach ? pavementStart : CrossStart;
        SnapToGround(instant: true);

        // 動態模式要走人行道過來，那段本來就該看得見。
        // 固定路徑模式會先「站在起點等車」，那段**不能**現身 ——
        // 她會在原地站好幾秒（等車的時間），而乾淨的 Controller 只有跑步狀態，
        // 玩家看到的就是一個人在路邊原地跑步等你，非常出戲。
        if (doApproach) SetVisible(true);

        if (doApproach)
        {
            if (UseAnimator) animator.Play(moveStateName, 0, 0f);
            while (true)
            {
                Vector3 toKerb = dynamicStart - transform.position;
                toKerb.y = 0f;
                if (toKerb.magnitude < 0.2f) break;

                LookAlong(toKerb, false);
                transform.position += toKerb.normalized * walkSpeed * Time.deltaTime;
                SnapToGround();

                if (!UseAnimator) { gaitTime += Time.deltaTime; ApplyGait(gaitTime); }
                yield return null;
            }
        }

        // ①-b 固定路徑模式：站在起點等車開到適當距離才起步。
        //     手動指定的路徑位置是對的，但時機一直是隨機的 —— 她一收到訊號就走，
        //     車那時可能還在很遠或已經開過去了。這裡把時機也算進來：
        //     等到「車距離 = 她跑到車道中間所需時間 × 車速 + 留給煞停的距離」才放行。
        if (!useDynamic && (timeFixedPathToActor || sampleLiveSpeed) && waitForCar != null)
        {
            float timeToLane = TimeToReachLaneOf(waitForCar);
            float waited = 0f;

            while (waited < fixedPathWaitTimeout)
            {
                if (waitForCar == null) break;

                Vector3 toCross = transform.position - waitForCar.position; toCross.y = 0f;
                Vector3 fwd = waitForCar.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) break;

                float along = Vector3.Dot(toCross, fwd.normalized);   // 車還差多遠到穿越點
                // 玩家計時模式每幀重讀車速(玩家速度變化大);演員車模式用開場抓的那次
                float speed = (sampleLiveSpeed && waitForCarRb != null)
                    ? waitForCarRb.velocity.magnitude : waitForCarSpeed;
                speed = speed > 0.1f ? speed : 1f;
                float wantDistance = timeToLane * speed + fixedPathCarGap;

                // 車已經開過穿越點 → 這次沒機會了，別再等
                if (along < 0f)
                {
                    if (logCrossing)
                        Debug.Log($"<color=grey>🚶 [行人] {name} 車已經開過穿越點，這次不走</color>", this);
                    ResetPose();
                    IsCrossing = false;
                    SetVisible(!hideUntilCrossing);
                    yield break;
                }

                if (along <= wantDistance) break;   // 時機到了

                waited += Time.deltaTime;
                yield return null;
            }

            if (logCrossing)
                Debug.Log($"<color=cyan>🚶 [行人] {name} 車已到定位，準備起步" +
                          $"（跑到車道需 {timeToLane:F1} 秒，留 {fixedPathCarGap:F0}m 煞停距離）</color>", this);
        }

        // ② 現身 + 張望。
        //
        //    ⚠️ 這裡才現身（固定路徑模式）—— 等車那段必須是隱形的，否則玩家老遠就
        //    看到一個人站在路邊等他，整個事件變成「明擺著的機關」。
        //
        //    ⚠️ 「張望」在這個科目其實是雙面刃：她看了、看到車了、還是走出來 ——
        //    玩家會覺得這個行人是故意的。真實世界會衝出來的行人是**沒有看**的
        //    （低頭看手機、追球的小孩、被前方車輛擋住視線）。
        //    所以 Look Before Crossing 設 0 反而更合理，那代表「她根本沒注意到你」。
        FaceTarget(true);
        SetVisible(true);

        if (lookBeforeCrossing > 0f)
        {
            if (UseAnimator && !string.IsNullOrEmpty(lookStateName))
                animator.Play(lookStateName, 0, 0f);
            else if (!UseAnimator) ResetPose();

            yield return new WaitForSeconds(lookBeforeCrossing);
        }

        HasSteppedOut = true;
        outOfStateTimer = 0f;
        stateNameVerified = false;
        if (UseAnimator) animator.Play(moveStateName, 0, 0f);
        if (logCrossing) Debug.Log($"<color=cyan>🚶 [行人] {name} 踏出人行道，開始穿越"
            + $"（{(UseAnimator ? "動畫 " + moveStateName : "程序化步態")}）</color>", this);

        // 穿越的保底：只靠「離終點夠近」當結束條件是不夠的。
        //
        // 角色身上如果有 Collider，撞到牆時物理會每幀把她推回來 —— 位移淨值是 0，
        // 但距離門檻永遠達不到，於是她就站在原地一直播跑步動畫。
        // 終點被拉到牆裡、或路面高低差讓貼地一直把她拉走，也會有同樣結果。
        //
        // 所以另外給兩道出口：走太久、或連續沒有實質前進。
        float crossTimeout = walkSpeed > 0.01f
            ? Vector3.Distance(CrossStart, CrossEnd) / walkSpeed * 2.5f + 3f
            : 10f;
        float crossElapsed = 0f;
        float noProgressTimer = 0f;
        Vector3 lastPos = transform.position;

        while (true)
        {
            Vector3 to = CrossEnd - transform.position;
            to.y = 0f;
            if (to.magnitude < 0.15f) break;

            crossElapsed += Time.deltaTime;
            if (crossElapsed > crossTimeout)
            {
                if (logCrossing)
                    Debug.LogWarning($"<color=orange>🚶 [行人] {name} 穿越逾時" +
                        $"（{crossTimeout:F1} 秒，還差 {to.magnitude:F1}m），提前結束</color>", this);
                break;
            }

            // 卡住偵測：這一幀有沒有真的移動
            float moved = Vector3.Distance(transform.position, lastPos);
            noProgressTimer = moved < walkSpeed * Time.deltaTime * 0.25f
                ? noProgressTimer + Time.deltaTime
                : 0f;
            lastPos = transform.position;

            if (noProgressTimer > 1f)
            {
                if (logCrossing)
                    Debug.LogWarning($"<color=orange>🚶 [行人] {name} 卡住不動超過 1 秒" +
                        $"（還差 {to.magnitude:F1}m，多半是撞到牆或終點在障礙物裡），提前結束</color>", this);
                break;
            }

            FaceTarget(false);
            transform.position += to.normalized * walkSpeed * Time.deltaTime;
            SnapToGround();

            if (UseAnimator)
            {
                // Controller 裡若還有 Idle/Jump/Walk 等狀態，跑步播完可能被 Exit Time 帶走，
                // 所以要能把它拉回來。
                //
                // ⚠️ 但這段必須非常小心：名稱只要對不上（大小寫、空白），IsName() 就永遠 false，
                //    變成每一幀都從第 0 格重播 —— 畫面上就是「一直卡頓」。
                //    所以只在「連續離開狀態超過 outOfStateGrace」才拉，而且會主動報名稱不符。
                bool inState = animator.GetCurrentAnimatorStateInfo(0).IsName(moveStateName);

                if (inState) { outOfStateTimer = 0f; stateNameVerified = true; }
                else if (!animator.IsInTransition(0))
                {
                    outOfStateTimer += Time.deltaTime;
                    if (outOfStateTimer >= 0.15f)
                    {
                        outOfStateTimer = 0f;
                        if (!stateNameVerified && !warnedStateName)
                        {
                            warnedStateName = true;
                            Debug.LogError(
                                $"[行人] {name} 的 Move State Name「{moveStateName}」在 Animator Controller 裡找不到" +
                                $"對應狀態，動畫會每幀重播（看起來一直卡頓）。\n" +
                                $"  請打開 Animator 視窗，把狀態方塊上的名字一字不差地填進來（注意大小寫）", this);
                        }
                        else animator.Play(moveStateName, 0, 0f);
                    }
                }
            }
            else
            {
                gaitTime += Time.deltaTime;
                ApplyGait(gaitTime);
            }
            yield return null;
        }

        // ④ 過完馬路後轉向繼續跑離開。
        //    停在終點的話跑步動畫會在原地空播到消失（Idle State Name 留空時尤其明顯）。
        //    往「玩家來的方向」跑 = 離開他的視野，消失時多半已經在畫面外。
        // 被撞到的話不跑離場 —— 人被撞了還若無其事跑走很荒謬，直接進入消失流程
        if (exitRunDistance > 0.1f && !WasHit)
        {
            Vector3 exitDir;
            if (waitForCar != null)
            {
                exitDir = -waitForCar.forward;      // 玩家車的後方
                exitDir.y = 0f;
            }
            else exitDir = transform.forward;       // 沒有參照就照原方向繼續

            if (exitDir.sqrMagnitude > 0.01f)
            {
                exitDir.Normalize();
                Vector3 exitTarget = transform.position + exitDir * exitRunDistance;

                float exitTimeout = exitRunDistance / Mathf.Max(0.1f, walkSpeed) * 2.5f + 2f;
                float exitElapsed = 0f;
                float exitStuck = 0f;
                Vector3 exitLast = transform.position;

                while (Vector3.Distance(transform.position, exitTarget) > 0.3f)
                {
                    exitElapsed += Time.deltaTime;
                    if (exitElapsed > exitTimeout) break;

                    float movedExit = Vector3.Distance(transform.position, exitLast);
                    exitStuck = movedExit < walkSpeed * Time.deltaTime * 0.25f
                        ? exitStuck + Time.deltaTime : 0f;
                    exitLast = transform.position;
                    if (exitStuck > 1f) break;      // 撞到東西就別硬跑

                    LookAlong(exitDir, false);
                    transform.position += exitDir * walkSpeed * Time.deltaTime;
                    SnapToGround();

                    if (!UseAnimator) { gaitTime += Time.deltaTime; ApplyGait(gaitTime); }
                    yield return null;
                }

                if (logCrossing) Debug.Log($"<color=cyan>🚶 [行人] {name} 已跑離現場</color>", this);
            }
        }

        if (UseAnimator)
        {
            if (!string.IsNullOrEmpty(idleStateName)) animator.Play(idleStateName, 0, 0f);
        }
        else ResetPose();

        IsCrossing = false;
        HasSteppedOut = false;
        if (logCrossing) Debug.Log($"<color=cyan>🚶 [行人] {name} 已抵達對面</color>", this);

        if (despawnAfterSeconds > 0f)
        {
            yield return new WaitForSeconds(despawnAfterSeconds);
            // 藏起來而不是停用物件 —— 下次事件才不必靠外部先 SetActive 回來
            SetVisible(false);
        }
    }

    /// <summary>
    /// 每幀檢查有沒有車輛壓到她身上。只記錄、不推物理。
    /// 回傳 true 代表這一幀被撞到（同一次穿越只會回報一次）。
    /// </summary>
    private bool CheckVehicleHit()
    {
        if (!detectVehicleHit || WasHit) return false;

        Collider[] hits = Physics.OverlapSphere(transform.position + Vector3.up * 0.8f,
                                                hitRadius, hitLayer, QueryTriggerInteraction.Ignore);
        foreach (Collider c in hits)
        {
            if (c == null) continue;
            if (c.transform.IsChildOf(transform)) continue;      // 自己身上的

            Rigidbody rb = c.attachedRigidbody;
            bool isVehicle = c.GetComponentInParent<ReverseThreatAICar>() != null
                          || c.GetComponentInParent<CarNavigation>() != null
                          || (rb != null && rb.CompareTag("Player"))
                          || c.CompareTag("Player");
            if (!isVehicle) continue;

            WasHit = true;

            // 力道用撞擊車速估：20 m/s 以上算滿。給危險感知的 severity 是 0~1。
            float speed = rb != null ? rb.velocity.magnitude : 0f;
            float severity = Mathf.Clamp01(speed / 20f);

            // 傳自己的 transform —— HazardAwarenessTracker 在 countAnyCollisionDuringHazard
            // 關閉時會比對「撞到的物件是不是這個危險因子的 source」，而 source 就是我。
            if (HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.ReportCollision(transform, severity);

            Debug.LogWarning($"<color=orange>💥 [行人] {name} 被「{c.transform.root.name}」撞到" +
                             $"（車速 {speed * 3.6f:F0} km/h，力道 {severity:F2}）</color>", this);
            return true;
        }
        return false;
    }

    /// <summary>把腳放到地面上。路面有起伏，只靠起終點高度內插一定會浮空或陷進去。</summary>
    /// <param name="instant">true = 瞬間貼齊（放置到起點時用，否則人會從空中慢慢飄下來）</param>
    private void SnapToGround(bool instant = false)
    {
        if (!snapToGround) return;

        Vector3 from = transform.position + Vector3.up * groundRayHeight;

        // 不能用單發 Raycast：行人自己身上如果有 Collider，射線會先打到自己 ——
        // 那會把腳「貼」到自己的頭頂，人就一路往上飛。改成掃全部再濾掉自己。
        RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, groundRayHeight * 3f,
                                               groundLayer, QueryTriggerInteraction.Ignore);
        float bestY = float.NegativeInfinity;
        bool found = false;

        foreach (RaycastHit h in hits)
        {
            if (h.collider == null) continue;
            if (h.collider.transform.IsChildOf(transform)) continue;   // 自己身上的碰撞體

            // 🚗 車輛不算地面。行人穿越時前方常常剛好有車，射線打到車頂就會
            //    把人「貼」到車上，過去之後再掉回地面 —— 就是那個爬上車又卡回來的現象。
            //    用元件判斷而不是靠 Layer 設定，這樣不管 Ground Layer 怎麼設都不會出事。
            if (h.collider.GetComponentInParent<ReverseThreatAICar>() != null) continue;
            if (h.collider.GetComponentInParent<CarNavigation>() != null) continue;
            if (h.collider.attachedRigidbody != null
                && h.collider.attachedRigidbody.CompareTag("Player")) continue;

            if (h.point.y > bestY) { bestY = h.point.y; found = true; }
        }

        if (!found) return;

        // 高度用平滑的，不要瞬間跳 —— 路面接縫或短暫打空都會造成一格一格的抖動
        Vector3 p = transform.position;
        float want = bestY + groundOffset;
        p.y = (instant || groundSnapSmooth <= 0f)
            ? want
            : Mathf.Lerp(p.y, want, 1f - Mathf.Exp(-groundSnapSmooth * Time.deltaTime));
        transform.position = p;
    }

    private void FaceTarget(bool instant) => LookAlong(CrossEnd - transform.position, instant);

    /// <summary>轉向指定方向（水平）。進場沿人行道走、穿越時朝對面，共用這一支。</summary>
    private void LookAlong(Vector3 dir, bool instant)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;

        Quaternion want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = instant
            ? want
            : Quaternion.Slerp(transform.rotation, want, Time.deltaTime * 6f);
    }

    /// <summary>
    /// 程序化步態：大腿前後擺、小腿在後擺時收起、手臂反相位擺動、身體隨步伐起伏。
    /// 全部相對「開場時的原始姿勢」疊加，所以不會把模型的初始站姿弄壞。
    /// </summary>
    private void ApplyGait(float t)
    {
        float phase = t * stepsPerSecond * Mathf.PI * 2f;
        float swing = Mathf.Sin(phase);

        // 大腿：正角度 = 往前抬。左右腳差半個週期
        SetBone(lThigh, lThigh0, swing * thighSwingAngle);
        SetBone(rThigh, rThigh0, -swing * thighSwingAngle);

        // 膝蓋：跑步時全程都保持彎曲（kneeBendMin），只是收腿那段彎得更多。
        //   舊版用 Max(0, sin) 讓另外半個週期完全打直 —— 那就是你看到的「直直地跑」。
        //   彎曲的高峰落在「腳離地、往前收」那一段，比大腿擺動晚約四分之一週期。
        float kneeSign = invertKneeBend ? 1f : -1f;
        float lKnee = kneeBendMin + (kneeBendMax - kneeBendMin) * Curve01(phase - Mathf.PI * 0.5f);
        float rKnee = kneeBendMin + (kneeBendMax - kneeBendMin) * Curve01(phase + Mathf.PI * 0.5f);
        SetBone(lCalf, lCalf0, kneeSign * lKnee);
        SetBone(rCalf, rCalf0, kneeSign * rKnee);

        // 肩膀：跟同側的腿相反相位
        float armSign = invertArmSwing ? 1f : -1f;
        SetBone(lUpperArm, lUpperArm0, armSign * swing * armSwingAngle);
        SetBone(rUpperArm, rUpperArm0, -armSign * swing * armSwingAngle);

        // 手肘：跑步姿勢是「固定彎約 90 度」全程維持，不隨擺動變化 ——
        // 讓手肘跟著大幅開合就是之前看起來像打鼓的原因。
        float elbowSign = invertElbowBend ? 1f : -1f;
        SetBone(lForearm, lForearm0, elbowSign * elbowBendAngle);
        SetBone(rForearm, rForearm0, elbowSign * elbowBendAngle);

        // 身體起伏：疊在 Pelvis 這根骨頭上，不能動根物件 ——
        // 根物件的位置正在被走路位移驅動，兩邊同時寫會互相打架。
        // 一步起伏兩次（左右腳各一次），所以用 |sin| 而不是 sin。
        if (bodyBobHeight > 0f && pelvis != null)
        {
            float bob = Mathf.Abs(Mathf.Sin(phase)) * bodyBobHeight;
            pelvis.localPosition = pelvis0Pos + Vector3.up * bob;
        }

        // 軀幹前傾 —— 跑步跟走路最明顯的差別之一，少了這個怎麼調都像在快走
        if (spine != null)
            SetBone(spine, spine0, bodyLeanAngle);
    }

    /// <summary>把 sin 從 -1~1 映射到 0~1，當作「收腿程度」的曲線</summary>
    private static float Curve01(float phase) => (Mathf.Sin(phase) + 1f) * 0.5f;

    /// <summary>
    /// 把肢體繞「角色的左右軸」擺動 —— 那就是走路時腿前後擺的那個軸。
    ///
    /// 不能用骨頭自己的本地軸（舊版的做法）：每根骨頭的本地軸向都不一樣，
    /// 同一個軸套下去，手臂會變成打鼓、腿會變成繞自己的長軸扭轉（看起來完全沒動）。
    ///
    /// 做法是把角色的 right 向量換算到這根骨頭的「父空間」，再前乘上去 ——
    /// 前乘＝在父空間旋轉整條肢體（腿往前抬），後乘＝繞骨頭自己的軸自轉（沒有用）。
    /// 這樣完全不必知道模型的軸向慣例，換任何模型都對。
    /// </summary>
    private void SetBone(Transform bone, Quaternion initial, float angle)
    {
        if (bone == null) return;
        if (Mathf.Abs(angle) < 0.01f) { bone.localRotation = initial; return; }

        Vector3 axis = bone.parent != null
            ? bone.parent.InverseTransformDirection(transform.right)
            : transform.right;

        bone.localRotation = Quaternion.AngleAxis(angle, axis) * initial;
    }

    /// <summary>走完把姿勢還原成原始站姿</summary>
    private void ResetPose()
    {
        SetBone(lThigh, lThigh0, 0f); SetBone(rThigh, rThigh0, 0f);
        SetBone(lCalf, lCalf0, 0f); SetBone(rCalf, rCalf0, 0f);
        SetBone(lUpperArm, lUpperArm0, 0f); SetBone(rUpperArm, rUpperArm0, 0f);
        SetBone(lForearm, lForearm0, 0f); SetBone(rForearm, rForearm0, 0f);
        if (spine != null) spine.localRotation = spine0;
        if (pelvis != null) pelvis.localPosition = pelvis0Pos;
    }

    // ==================== 🚗 對玩家清出走廊 ====================
    //
    // 沿用故障車(AccidentDirector)那套:沿「玩家車 → 穿越線」拉一條膠囊,
    // 把裡面的車 ForceFadeOutClear 縮小淡出。膠囊跟著路走,不會像球體那樣
    // 誤掃到旁邊平行的街道。過程中每隔幾秒重掃,擋掉從側邊路口開進來的車。

    /// <summary>穿越期間持續把「玩家 → 穿越線」的走廊清乾淨。</summary>
    private IEnumerator ClearCorridorRoutine()
    {
        // 一開始就先清一次 —— 玩家還在接近,先把路讓出來,他才有淨空的視野與跑道
        ClearCorridorOnce();
        while (IsCrossing)
        {
            yield return new WaitForSeconds(Mathf.Max(0.25f, corridorRecheckInterval));
            if (!IsCrossing) break;
            ClearCorridorOnce();
        }
        corridorRoutine = null;
    }

    /// <summary>掃一次走廊,把裡面的 AI 車縮小淡出(玩家鼻子前與救護車、路邊靜停演員車除外)。</summary>
    private void ClearCorridorOnce()
    {
        if (waitForCar == null) return;

        Vector3 corridorStart = waitForCar.position;
        Vector3 lanePoint = LanePointOnCrossing(waitForCar);
        Vector3 fwd = waitForCar.forward; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized
            : (lanePoint - corridorStart).normalized;
        Vector3 corridorEnd = lanePoint + fwd * corridorAheadOfCrossing;

        Collider[] hits = Physics.OverlapCapsule(corridorStart, corridorEnd,
            corridorHalfWidth, vehicleLayer, QueryTriggerInteraction.Ignore);

        var done = new System.Collections.Generic.HashSet<GameObject>();
        int cleared = 0;

        foreach (Collider c in hits)
        {
            if (c == null) continue;

            ReverseThreatAICar white = c.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation gray = white == null ? c.GetComponentInParent<CarNavigation>() : null;
            if (white == null && gray == null) continue;

            // 🚑 救護車有自己的任務流程,絕對不能清
            if (white is AmbulanceAI) continue;

            // 路邊靜停演員車不清 —— 它們不擋路,而且是側切等其他危險因子的演員
            if (white != null && white.isStaticParked) continue;
            if (gray != null && gray.isStaticObstacle) continue;

            GameObject go = white != null ? white.gameObject : gray.gameObject;
            if (!done.Add(go)) continue;   // 一台車可能有多個碰撞體

            // 玩家鼻子前極近的車不清,避免貼臉縮小消失
            if (corridorPlayerSafeRadius > 0f &&
                Vector3.Distance(corridorStart, go.transform.position) < corridorPlayerSafeRadius)
                continue;

            if (white != null) white.ForceFadeOutClear(corridorFadeSeconds);
            else gray.ForceFadeOutClear(corridorFadeSeconds);
            cleared++;
        }

        if (logCrossing && cleared > 0)
            Debug.Log($"<color=grey>🧹 [行人清道] {name} 清除 {cleared} 台" +
                      $"(走廊半寬 {corridorHalfWidth}m、玩家保護 {corridorPlayerSafeRadius}m)</color>", this);
    }

    /// <summary>穿越線(Start→End)上最靠近車行進軸的那一點 —— 也就是這台車會被行人擋住的位置。</summary>
    private Vector3 LanePointOnCrossing(Transform car)
    {
        Vector3 a = CrossStart, b = CrossEnd;
        Vector3 fwd = car.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) return (a + b) * 0.5f;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);

        float la = Vector3.Dot(a - car.position, right);
        float lb = Vector3.Dot(b - car.position, right);
        float t = Mathf.Approximately(la, lb) ? 0.5f : Mathf.Clamp01(la / (la - lb));
        return a + (b - a) * t;
    }

    private void OnDrawGizmosSelected()
    {
        if (positionRelativeToActor)
        {
            // 動態模式的實際路徑要等執行時才知道（相對演員車算），
            // 但三個距離參數的「形狀」可以先預覽：把這個物件自己當成假想的演員車，
            // 畫出屆時會產生的穿越路徑，方便目視調 Ahead / Side / Cross 三個值。
            Vector3 fwd = transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);

            Vector3 s = transform.position + fwd * aheadOfActor + right * kerbSideOffset;
            Vector3 e = s - right * crossDistance;

            // 車 → 穿越點：這段是「前車還有多少距離可以煞停」
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.8f);
            Gizmos.DrawLine(transform.position, transform.position + fwd * aheadOfActor);

            // 實際穿越路徑
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(s, e);
            Gizmos.DrawWireSphere(s, 0.4f);   // 起點（路側）
            Gizmos.DrawWireSphere(e, 0.4f);   // 終點（對側）

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 0.3f);   // 假想的演員車位置
            return;
        }
        if (startPoint == null || endPoint == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(startPoint.position, endPoint.position);
        Gizmos.DrawWireSphere(startPoint.position, 0.3f);
        Gizmos.DrawWireSphere(endPoint.position, 0.3f);
    }
}
