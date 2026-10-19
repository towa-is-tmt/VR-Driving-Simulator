using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class AccidentDirector : MonoBehaviour
{
    // 全域開關，供 TrafficDiverter 使用
    public static bool IsAccidentActive = false;

    /// <summary>
    /// 🚗 目前進行中的事故是否開放 AI 車繞行。由車輛端(CarNavigation / ReverseThreatAICar)查詢。
    /// 預設 false —— AI 車只會停在故障車後面,玩家自己從對向道繞過去。
    /// </summary>
    public static bool AllowAIBypassNow = false;

    /// <summary>🚫 事故進行中的禁止生成中心點(生成器查詢用)</summary>
    public static Vector3 ActiveAccidentPos = Vector3.zero;
    /// <summary>🚫 事故進行中的禁止生成半徑,0 = 沒有事故進行中</summary>
    public static float ActiveNoSpawnRadius = 0f;

    /// <summary>🚫 指定路線上的禁止生成節點(灰車路網)。有內容時半徑不再自動撐大</summary>
    public static readonly System.Collections.Generic.HashSet<WaypointNode> NoSpawnNodesGray
        = new System.Collections.Generic.HashSet<WaypointNode>();
    /// <summary>🚫 指定路線上的禁止生成節點(白車路網)</summary>
    public static readonly System.Collections.Generic.HashSet<TrafficPathNode> NoSpawnNodesWhite
        = new System.Collections.Generic.HashSet<TrafficPathNode>();

    [Header("生成設定")]
    public GameObject accidentPrefab;
    public Transform spawnPoint;

    [Header("🚧 路障與清場設定")]
    public GameObject roadBlockObject;

    [Header("精準封路偵測設定")]
    public Transform boxCenter;       // 請放一個空物件在路口封鎖區中心
    public Vector3 boxSize = new Vector3(5f, 2f, 10f); // 調整此尺寸以覆蓋目標車道
    public LayerMask aiCarLayer;       // 務必勾選 AI 車輛所在的 Layer

    [Header("距離與清場設定")]
    public float clearRadius = 100f;
    public float despawnDistance = 50f;

    [Header("🧹 走廊清車（觸發盒 → 故障車 這段路）")]
    // 舊版是以故障車為圓心的球體清一次。球體會誤傷旁邊平行的街道,
    // 又涵蓋不到「觸發盒 → 故障車」中間那段(那才是玩家真正會開過去的路)。
    // 改成沿著兩點之間拉一條膠囊,才會跟著路走。
    [Tooltip("走廊半寬(公尺)。從觸發盒到故障車的連線往兩側各算這麼寬")]
    public float clearCorridorHalfWidth = 12f;
    [Tooltip("用縮小淡出取代直接消失。關掉就是瞬間 Destroy")]
    public bool clearWithFadeOut = true;
    [Tooltip("玩家這個距離內的車不清,避免在鼻子前縮小消失。想要整段完全淨空就設 0")]
    public float clearPlayerSafeRadius = 25f;
    [Tooltip("事故期間每隔幾秒重掃一次。禁生成路線只擋『新生成』,擋不住從側邊路口『開進來』的")]
    public float clearRecheckInterval = 2f;
    [Tooltip("連路邊靜態停放的演員車也一起清。預設不清 —— 它們停在路邊不擋路,而且是其他危險因子的演員")]
    public bool clearStaticParkedToo = false;
    // 走廊半寬 12m = 總寬 24m,一般雙向道連人行道也才 12~16m,所以對向車道整條都被涵蓋。
    // 而且這支腳本本身就有一套對向車流節奏控制(oncomingLimitRadius / maxOncomingPerBatch),
    // 它刻意放行對向車讓玩家有機會借道 —— 一邊放行一邊清除等於兩套系統打架。
    [Tooltip("只清跟走廊同方向的車。關掉的話對向車道的車也會被清,會跟對向節奏控制衝突")]
    public bool clearOnlySameDirection = true;
    [Range(0f, 1f)]
    [Tooltip("車頭與走廊方向的最小相符度。0.5≈夾角 60 度內算同向")]
    public float clearSameDirectionDot = 0.5f;
    // 車子自己的 fadeOutDuration 是 0.5 秒,近距離看就是「啪」一下。
    // 這裡改成依距離調整:遠處反正看不清楚(尤其之後上了 LOD),快速收掉就好;
    // 近處慢慢縮小,視覺上柔和很多。
    [Tooltip("近處車輛的淡出秒數。慢一點比較不刺眼")]
    public float clearFadeSecondsNear = 1.5f;
    [Tooltip("遠處車輛的淡出秒數。看不清楚,快速收掉即可")]
    public float clearFadeSecondsFar = 0.3f;
    [Tooltip("離玩家超過這個距離就算「遠處」,中間依比例內插")]
    public float clearFadeFarDistance = 120f;

    [Header("🎯 危險感知：反應量測")]
    // 故障車是持久障礙，從生成到玩家經過可能隔很久。
    // 如果生成當下就註冊，基準車速會記到玩家還在幾百公尺外的時候，反應判定完全失真。
    // 所以改成「玩家接近到這個距離」才註冊，基準才有意義。
    [Tooltip("玩家接近到多少公尺才開始量測反應。應該設在玩家肉眼剛看得到故障車的距離")]
    public float hazardNoticeDistance = 60f;
    [Tooltip("關掉就不做反應量測與事後回饋")]
    public bool enableHazardTracking = true;

    [Header("⚠️ 事前提示")]
    // 故障車跟其他危險因子不同，它是靜止障礙物 —— 真實世界你遠遠就看得到，
    // 還有三角錐和故障燈。被前方車輛擋住視線屬於「不公平的資訊剝奪」，不是設計出來的危險，
    // 所以這裡給提示是在還原真實，而不是削弱難度。
    [Tooltip("玩家接近時顯示的提示。留空就不顯示")]
    [TextArea(2, 3)]
    public string noticeMessage = "前方發生事故\n請繞開並注意對向來車";
    public Color noticeColor = new Color(1f, 0.62f, 0.05f);
    [Tooltip("提示顯示幾秒")]
    public float noticeDisplaySeconds = 5f;
    [Tooltip("玩家還沒繞開的話，每隔幾秒再提示一次。設 0 就只提示一次")]
    public float noticeRepeatSeconds = 10f;
    [Tooltip("提示出現時播放的警示音。建議用 聲音/警示_事故提醒.wav")]
    public AudioClip noticeSound;
    [Tooltip("播放用的 AudioSource。留空會自動新增一個 2D 音源")]
    public AudioSource noticeAudioSource;
    [Range(0f, 2f)]
    public float noticeVolume = 0.9f;

    private bool hazardRegistered = false;
    private float nextNoticeTime = -1f;
    [Tooltip("🚫 事故進行中,這個半徑內的節點一律不生新車(公尺)。0 = 不用球形範圍,只靠路線。\n\n" +
             "clearRadius 只在生成當下清一次場,之後生成器照樣會在附近補車 ——\n" +
             "尤其是「觸發盒到故障車」之間那段,節點不在封鎖清單裡,新車就一直冒出來。\n\n" +
             "※ 走「路線模式」時就照這個值,不再自動撐大(避免半徑太大誤傷隔壁的路)。\n" +
             "※ 沒有路線可用時會退回球形模式,那時半徑會自動撐到涵蓋觸發盒。")]
    public float noSpawnRadius = 120f;

    [Header("🚫 禁生成路線")]
    [Tooltip("勾選後,自動用「觸發盒 → spawnPoint」這兩個現成的位置推算路段,\n" +
             "找出各路網離它們最近、且行進方向一致的節點當頭尾,中間的節點全部禁生成。\n" +
             "完全不用手動拉節點。")]
    public bool autoNoSpawnRoute = true;

    [Tooltip("自動找節點時,節點行進方向與「觸發盒→故障車」方向的最小相符度。\n" +
             "0.5 約 60 度。調低會比較寬鬆但可能抓到對向車道的節點")]
    [Range(0f, 1f)] public float routeNodeDirDot = 0.5f;

    [Tooltip("(選填)手動覆寫頭尾節點。留空就用上面的自動推算")]
    public WaypointNode noSpawnRouteStartGray;
    public WaypointNode noSpawnRouteEndGray;
    public TrafficPathNode noSpawnRouteStartWhite;
    public TrafficPathNode noSpawnRouteEndWhite;
    [Tooltip("走訪的節點數上限,單純的安全夾(正常情況碰不到)")]
    public int noSpawnRouteMaxNodes = 80;

    /// <summary>有沒有要用路線模式(自動推算 或 手動指定了頭尾)</summary>
    private bool UsingNoSpawnRoute =>
        autoNoSpawnRoute
     || (noSpawnRouteStartGray != null && noSpawnRouteEndGray != null)
     || (noSpawnRouteStartWhite != null && noSpawnRouteEndWhite != null);

    /// <summary>玩家從觸發盒往故障車的行進方向</summary>
    private Vector3 RouteTravelDir
    {
        get
        {
            if (spawnPoint == null) return transform.forward;
            Vector3 d = spawnPoint.position - transform.position; d.y = 0f;
            return d.sqrMagnitude > 0.01f ? d.normalized : spawnPoint.forward;
        }
    }

    /// <summary>找離 pos 最近、且行進方向與玩家路線一致的灰車節點</summary>
    private WaypointNode FindRouteNodeGray(Vector3 pos)
    {
        WaypointNode best = null; float bestD = float.MaxValue;
        Vector3 travel = RouteTravelDir;
        foreach (WaypointNode n in FindObjectsByType<WaypointNode>(FindObjectsSortMode.None))
        {
            if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
            if (n.nextChoices[0] == null) continue;
            Vector3 d = n.nextChoices[0].transform.position - n.transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) continue;
            if (Vector3.Dot(d.normalized, travel) < routeNodeDirDot) continue;   // 排除對向/橫向車道
            float dist = Vector3.Distance(n.transform.position, pos);
            if (dist < bestD) { bestD = dist; best = n; }
        }
        return best;
    }

    /// <summary>找離 pos 最近、且行進方向與玩家路線一致的白車節點</summary>
    private TrafficPathNode FindRouteNodeWhite(Vector3 pos)
    {
        TrafficPathNode best = null; float bestD = float.MaxValue;
        Vector3 travel = RouteTravelDir;
        foreach (TrafficPathNode n in FindObjectsByType<TrafficPathNode>(FindObjectsSortMode.None))
        {
            if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
            if (n.nextChoices[0] == null) continue;
            Vector3 d = n.nextChoices[0].transform.position - n.transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) continue;
            if (Vector3.Dot(d.normalized, travel) < routeNodeDirDot) continue;
            float dist = Vector3.Distance(n.transform.position, pos);
            if (dist < bestD) { bestD = dist; best = n; }
        }
        return best;
    }

    // 實際使用的頭尾:手動有拉就用手動的,否則自動推算
    private WaypointNode RouteStartGray => noSpawnRouteStartGray != null ? noSpawnRouteStartGray
        : (autoNoSpawnRoute ? FindRouteNodeGray(transform.position) : null);
    private WaypointNode RouteEndGray => noSpawnRouteEndGray != null ? noSpawnRouteEndGray
        : (autoNoSpawnRoute && spawnPoint != null ? FindRouteNodeGray(spawnPoint.position) : null);
    private TrafficPathNode RouteStartWhite => noSpawnRouteStartWhite != null ? noSpawnRouteStartWhite
        : (autoNoSpawnRoute ? FindRouteNodeWhite(transform.position) : null);
    private TrafficPathNode RouteEndWhite => noSpawnRouteEndWhite != null ? noSpawnRouteEndWhite
        : (autoNoSpawnRoute && spawnPoint != null ? FindRouteNodeWhite(spawnPoint.position) : null);
    // 栏位区加(第 23 行 clearRadius 附近)
    [Header("🚧 封閉的節點(給灰車尋路避開)")]
    public static System.Collections.Generic.List<WaypointNode> BlockedNodes = new System.Collections.Generic.List<WaypointNode>();
    public System.Collections.Generic.List<WaypointNode> blockedNodesSetup = new System.Collections.Generic.List<WaypointNode>();
    [Header("🚧 封閉的節點(給白車尋路避開)")]
    // 🚧 白車路網(TrafficPathNode)的封閉節點
    public static System.Collections.Generic.List<TrafficPathNode> BlockedPathNodes = new System.Collections.Generic.List<TrafficPathNode>();
    public System.Collections.Generic.List<TrafficPathNode> blockedPathNodesSetup = new System.Collections.Generic.List<TrafficPathNode>();
    [Header("💨 生成遮蔽特效")]
    public GameObject smokeEffectPrefab; // 拉一個煙霧/塵土 ParticleSystem prefab
    [Tooltip("煙往事故車後方偏移幾公尺")]
    public float smokeBackOffset = 3f;
    private GameObject spawnedAccident;
    private Transform playerTransform;

    
    

    [Header("🚗 AI 車繞行 (逐點設定)")]
    [Tooltip("勾選後,這個事故點的 AI 車會嘗試繞過故障車;不勾則單純停在後面等。\n" +
             "預設不勾 —— 繞行的偏移量需要逐街道調整,而生成時已清掉周圍 100m 的車,\n" +
             "現場通常只剩一兩台漏網之魚,讓它們停著即可,玩家從對向道繞過去。")]
    public bool allowAIBypass = false;

    [Header("🚦 對向車流節奏 (讓玩家有空檔繞過去)")]
    [Tooltip("關掉就完全不干預對向車流")]
    public bool enableOncomingControl = true;
    [Tooltip("以事故點為中心,多大範圍內的對向車要納入計數(公尺)")]
    public float oncomingLimitRadius = 60f;
    [Tooltip("一批放行幾台對向車。※ 下面的隨機化打開時，這個值只當作沒隨機到時的後備")]
    public int maxOncomingPerBatch = 2;
    [Tooltip("一批車開出範圍後,要空窗幾秒才放下一批。這段空窗就是玩家繞過去的機會")]
    public float oncomingGapDuration = 8f;

    // 固定「2 台 → 空 8 秒」的話，玩家跑幾次就會發現「數兩台車過去就能走」——
    // 這時他練的是數車，不是判斷間距。真正該練的能力叫 gap acceptance：
    // 判斷「這個空檔夠不夠我過去」，而要練這個，空窗就必須有長有短、
    // 而且要有些短到不該用，玩家得學會放棄不夠的空檔等下一個。
    [Tooltip("每一批重新擲一次批次量與空窗長度。關掉＝用上面兩個固定值（比賽展示建議關掉）")]
    public bool randomizeOncomingRhythm = true;
    [Tooltip("每批放行台數的隨機範圍（含頭尾）")]
    public Vector2Int oncomingBatchRange = new Vector2Int(1, 3);
    [Tooltip("空窗秒數的隨機範圍。繞過故障車約需 6~7 秒，所以下限要設得比它短——\n" +
             "「這個空檔不夠、要等下一個」正是這裡要訓練的判斷")]
    public Vector2 oncomingGapRange = new Vector2(5f, 12f);

    // 這一批實際用的值（隨機化關閉時就等於上面兩個固定欄位）
    private int currentBatchSize;
    private float currentGapDuration;
    [Tooltip("節點/車輛距離「事故點前後軸線」多遠以內才算同一條路(公尺),用來排除隔壁平行街道。\n" +
             "※ 只在下方兩份「手動指定」清單留空時才會用到")]
    public float sameRoadLateralTolerance = 8f;
    [Tooltip("與事故點朝向的夾角超過多少度才算對向(-0.5 約 120 度)。\n" +
             "※ 只在下方兩份「手動指定」清單留空時才會用到")]
    [Range(-1f, 0f)] public float oncomingDirectionDot = -0.5f;

    [Header("🚙 對向上游補車 (讓玩家繞行時真的有車要判斷)")]
    // 對向車流本來就稀疏,常常故障車一出來對向空空如也 → 玩家直接開過去,什麼都沒練到。
    //
    // 刻意「不」在玩家要繞過去的位置生成 —— 那會讓車憑空出現在視野裡,
    // 穿幫比沒車還嚴重,而且真實世界的車不會這樣冒出來。
    // 改成在對向車流的「上游」補,讓它自己開過來:
    //   玩家收到提示(100m)到抵達故障車約 7 秒;對向車以 10m/s 開,100m 要 10 秒 ——
    //   所以生成在 80~120m 上游,車子會在玩家正要繞的時候剛好開到,全程在畫面外進場。
    //
    // 補車採「缺才補」而不是固定每隔幾秒硬塞:玩家過去之後就停,不會愈積愈多。
    [Tooltip("關掉＝完全不補對向車，維持舊行為")]
    public bool enableOncomingSeeding = true;
    [Tooltip("要補的車輛 prefab。通常就是灰車 prefab（GrayTrafficSpawner 上那顆）")]
    public GameObject oncomingCarPrefab;
    [Tooltip("補車位置離事故點的距離範圍（公尺）。要夠遠才不會被玩家看到憑空出現")]
    public Vector2 seedDistanceRange = new Vector2(80f, 120f);
    [Tooltip("走廊內維持幾台對向車。已經夠多就不補")]
    public int seedTargetCount = 3;
    [Tooltip("幾秒檢查一次要不要補")]
    public float seedCheckInterval = 4f;
    [Tooltip("整場事故最多補幾台，防止玩家卡在原地時無限增生")]
    public int seedMaxTotal = 12;
    [Tooltip("玩家離事故點超過這個距離就先不補（他還沒過來，補了也是白補）")]
    public float seedPlayerMaxDistance = 200f;
    // 路線搜尋的三個參數。實測發現「走不到事故點」是最容易卡住的一關，原因是
    // 灰車節點排得很密（1253 個），80~120m 往往要走 20~40 個節點才到 ——
    // 舊版寫死 20 步根本走不完，於是每個候選起點都被判定成走不到。
    [Tooltip("路線最多走幾個節點。節點排得密就要調大：80~120m 常常需要 20~40 步")]
    public int seedRouteMaxSteps = 60;
    [Tooltip("走到離事故點多近算抵達（公尺）。對向車道不會穿過事故點而是從旁邊經過，所以至少要涵蓋路寬")]
    public float seedRouteArriveRadius = 18f;
    [Tooltip("容許路線暫時繞遠多少公尺才判定為背離。彎道會先繞遠再彎回來，設太小會在彎道中途放棄")]
    public float seedRouteDivergeTolerance = 20f;

    [Header("🎯 補車生成點手動指定（最直接，建議優先用這個）")]
    // 幾何搜尋要同時滿足距離、朝向、對向、走廊、路網可達五個條件，
    // 而每個事故點的路網形狀都不一樣 —— 一個路口、一段彎道就可能讓候選歸零。
    // 直接把「對向車該從哪裡出現」的節點拉進來最快，也最可控：
    // 有拉東西就完全以這份清單為準，上面那一整套幾何判定全部跳過。
    //
    // 怎麼挑：在 Scene 視窗沿對向車道往「來車方向」回推 80~120m，
    // 挑那條車道上的節點（可以多拉幾個，每次隨機挑一個沒被佔用的，出場位置才有變化）。
    [Tooltip("對向補車要從哪些節點生成。拉了就完全以此為準，不再做任何幾何搜尋。\n" +
             "建議拉 2~4 個沿對向車道分佈的節點，出場位置才不會每次都一樣。\n" +
             "※ 生成點在路口分岔上、車可能轉錯邊的話，改用下面的「指定完整路線」")]
    public List<WaypointNode> seedSpawnNodes = new List<WaypointNode>();

    /// <summary>一條手動指定的補車路線：第一個節點是生成點，後面是它必須依序經過的節點。</summary>
    [System.Serializable]
    public class SeedRoute
    {
        [Tooltip("第一個 = 生成點，後面依序是「一定要走這邊」的節點。\n" +
                 "只要填到過了分岔路口就好，之後會自動放手回到正常的隨機選路。\n" +
                 "相鄰兩個節點必須是路網上真的接在一起的（後者要在前者的 Next Choices 裡）")]
        public List<WaypointNode> nodes = new List<WaypointNode>();
    }

    [Tooltip("指定完整路線。生成點在分岔路口、車會轉錯邊時用這個。\n" +
             "可以放多條，每次隨機挑一條沒被佔用的。優先度高於上面的 Seed Spawn Nodes")]
    public List<SeedRoute> seedRoutes = new List<SeedRoute>();

    [Tooltip("路線走不到事故點時，仍用「已經走出來的那一段」當指定路線。\n" +
             "關掉＝走不到就整個放棄（嚴格但常常一台都補不出來）")]
    public bool seedAllowPartialRoute = true;

    [Header("🚗 繞行中補車（訓練「發現對向有車要退回」）")]
    // 你要的情境：玩家已經切出去了，這時對向來車出現 → 必須放棄繞行退回原車道。
    // 這是真實世界最常見的超車失誤，也是最值得練的一課。
    //
    // ⚠️ 刻意「不」在玩家跨線的當下把車生在他面前 —— 那是憑空出現，穿幫而且教錯東西：
    //    真實世界那台車本來就在，是你沒看清楚就切出去。所以這裡一樣生在上游，
    //    讓它自己開過來，玩家跨線後約 5~6 秒進入視野，剛好是「已經切出去才發現」的時機。
    //
    // 另外只有「跨線當下對向是空的」才補 —— 本來就有車的話玩家就該等，
    // 那種情況再補等於懲罰他做對的判斷。
    [Tooltip("關掉＝不做這個情境，只靠平常的補車節奏")]
    public bool seedOnBypassCommit = true;
    [Tooltip("玩家跨線當下，最近的對向來車超過這個距離（或根本沒車）才補。\n" +
             "本來就有車就不補 —— 那時玩家該做的是等，不是被追加考驗")]
    public float commitSeedMinGap = 45f;

    [Tooltip("印出每次補車與跳過的原因")]
    public bool logSeeding = false;

    private int seededThisEvent = 0;
    private WaypointNode[] cachedGrayNodes;

    [Header("📊 繞行評分 (取代通用的反應時間分級)")]
    // 故障車該問的不是「你幾秒內反應」，而是這個科目真正在教的三件事：
    //   ① 到達前有沒有減速
    //   ② 跨到對向車道那一刻，對向有沒有來車（這是唯一會出人命的那個判斷）
    //   ③ 有沒有在該等的時候等
    // 這三項全部可以從現有資料算出來 —— 車速、橫向位置、對向車距離都是現成的。
    [Tooltip("關掉＝退回通用的反應時間分級（不建議，那組門檻是為突發事件設的）")]
    public bool enableBypassScoring = true;
    [Tooltip("橫向偏離事故點車道中心超過這麼多公尺，視為已經跨進對向車道")]
    public float centerLineOffset = 2.0f;
    // 車道軸線有兩種取法：
    //   spawnPoint.forward —— 事故車自己的擺放角度。擺歪了判定就跟著歪，
    //                          而且它假設故障車停在車道正中央。
    //   路網 —— 白車路網(TrafficPathNode)沿著真實車道鋪設，753 個節點覆蓋很密，
    //          用「最近節點 → 它的下一個節點」當方向，比 prefab 的擺放角度可靠得多。
    // 路網也讓橫向原點落在真正的車道中心線上，而不是故障車停的位置。
    [Tooltip("用白車路網取車道軸線（較準）。關掉＝改用事故車自己的朝向 spawnPoint.forward")]
    public bool useNetworkLaneAxis = true;
    [Tooltip("找最近路網節點的搜尋半徑（公尺）。這個範圍內找不到節點就自動退回 spawnPoint.forward")]
    public float laneAxisSearchRadius = 25f;

    private Vector3 laneAxisOrigin;      // 車道中心線上的一點
    private Vector3 laneAxisDir;         // 車道方向（單位向量）
    private bool laneAxisResolved = false;
    [Tooltip("事故點前後這個範圍內才進行評分（公尺）")]
    public float bypassZoneLength = 35f;
    // 舊版是「離開整個評分區（35m）」才結算 —— 玩家已經開出去快 3 秒才跳評語與語音，
    // 那時他早就不在想剛剛那件事了，回饋跟行為之間斷掉，教學效果大打折扣。
    // 結算點要跟「通過」這個動作貼近，所以另外給一個小得多的門檻。
    [Tooltip("玩家超過事故點這麼多公尺就立刻結算並播評語（公尺）。\n" +
             "要跟通過的動作貼近才有教學效果，建議 8~15")]
    public float bypassSettleDistance = 10f;
    [Tooltip("跨線的瞬間，最近的對向來車在這個距離內 → 判定為危險繞行（公尺）")]
    public float dangerousOncomingDistance = 35f;

    // 🚗「發現對向來車卻沒退回」——這一項本來是缺的。
    //    seedOnBypassCommit 會在「跨線當下對向淨空」時補一台車進來，程式碼註解寫著
    //    「接下來考的是發現後怎麼處理」,但實際上沒有任何地方在評這件事:
    //    跨線快照說淨空 → danger=false;沒撞到 → 也不算失敗 → 一路硬撐過去照樣「安全通過」。
    //    這裡補上:跨線後對向車逼近到門檻內就該退回原車道,沒退回就是失敗。
    [Header("🚗 發現對向來車卻沒退回")]
    [Tooltip("關掉＝維持舊行為：不評這一項")]
    public bool scoreRetreat = true;
    [Tooltip("跨線之後，對向來車逼近到這個距離內就該退回原車道（公尺）。\n" +
             "要比上面的危險門檻大 —— 那個是「不該跨」，這個是「跨了之後該退」")]
    public float retreatOncomingDistance = 45f;
    [Tooltip("多久重新量一次對向距離（秒）。不必每幀")]
    public float retreatSampleInterval = 0.2f;
    [Tooltip("通過事故點時車速高於這個值 → 判定為未減速（km/h）")]
    public float safePassSpeedKmH = 30f;
    [Tooltip("印出繞行評分的判定過程")]
    public bool logBypassScoring = false;


    [Header("繞行事後語音")]
    // 刻意不走 HazardAwarenessTracker 的 hazardVoices 那張表 —— 那張表是用
    // 「撞到／未察覺」去配語音的，跟繞行的結局（危險繞行／速度偏快）對不起來。
    // 這裡直接依評分結果挑對應的那一句，透過 tracker 的同一顆音源播出去。
    [Tooltip("對向有車卻仍跨越中心線時播。建議 聲音/語音提示/05_對向有來車應停等空檔再繞行.wav")]
    public AudioClip voiceDangerousBypass;
    [Tooltip("通過事故現場未減速時播。建議 聲音/語音提示/06_通過事故現場請減速.wav")]
    public AudioClip voiceFastPass;

    [Header("繞行評語文字")]
    // 這段文字是「事後講評」，但原本的寫法（例如「經過事故現場應減速」）跟事前的提示
    // 長得一模一樣 —— 玩家已經開過去了才跳出來，讀起來像一句遲到的指示，很怪。
    // 冠上「評分：」就變成明確的講評，時態問題就消失了。
    //
    // ⚠️ 只加在文字上。語音是獨立的音檔（05/06 那兩句），本來就不會念到這兩個字，
    //    所以不需要、也不應該為了這個去動音檔。
    [Tooltip("評語文字的前綴，只影響畫面文字，不影響語音。\n" +
             "事後講評跟事前提示用同一句話會很怪，加個「評分：」才看得出是在講剛才那段。\n" +
             "留空 = 不加前綴（舊行為）")]
    public string bypassScorePrefix = "評分：";
    public string bypassSafeMessage = "繞行安全\n有先確認對向再通過";
    public string bypassDangerMessage = "危險繞行\n對向有來車時不可跨越，應停等空檔";
    public string bypassFastMessage = "通過速度偏快\n經過事故現場應減速";
    // 💥 撞到了卻顯示「繞行安全」是這套評分最嚴重的破口：
    //    danger 只看跨線那一瞬間的快照、tooFast 只看車速，兩者都不知道有沒有撞到。
    //    實測就出現過「跨線時對向 38m(未達 35m 門檻) + 車速 26km/h(未達 30) → 安全通過」，
    //    但同一場玩家其實已經撞上補進來的對向車。撞到永遠是最重的結果，要蓋過其他判定。
    [Tooltip("演出期間發生碰撞時的評語。碰撞優先序最高，會蓋過危險繞行與速度偏快")]
    public string bypassCrashMessage = "與對向車發生碰撞\n對向有來車時應退回原車道等空檔";
    [Tooltip("跨線後對向來車逼近、玩家卻沒退回原車道時的評語")]
    public string bypassNoRetreatMessage = "對向來車接近仍未退回\n發現來車應先退回原車道等空檔";
    public Color bypassGoodColor = new Color(0.25f, 1f, 0.35f);
    public Color bypassBadColor = new Color(1f, 0.22f, 0.18f);
    public float bypassMessageSeconds = 5f;

    // 繞行評分的執行階段狀態
    private bool bypassScored = false;         // 本次事件已經評過分
    private bool bypassCrossed = false;        // 玩家已經跨進對向車道
    private float crossOncomingDistance = -1f; // 跨線瞬間最近的對向來車距離
    private bool retreatNeeded = false;        // 跨線後對向來車曾經逼近到門檻內 → 這時該退回
    private bool retreatDone = false;          // 逼近之後玩家確實退回自己車道了
    private float retreatMinOncoming = -1f;    // 逼近期間量到的最近距離（評語與 log 用）
    private float nextRetreatSampleTime = 0f;  // 對向距離的取樣節流
    private float bypassPassSpeedKmH = -1f;    // 通過事故點時的車速
    private bool bypassEnteredZone = false;

    [Header("🎯 對向節點手動指定 (彎道專用)")]
    [Tooltip("留空 = 用上面的直線走廊自動偵測(適用直路)。\n\n" +
             "彎道請手動把「對向車會從那裡開過來」的節點拉進來 —— 直線走廊在彎道上會歪掉:\n" +
             "沿弧線走 60m,路會橫向偏離直線約 18m,遠超過容許值,遠端會切出路面之外。\n" +
             "有拉東西就完全以這份清單為準,不再做幾何判定。")]
    public System.Collections.Generic.List<WaypointNode> oncomingNodesOverrideGray
        = new System.Collections.Generic.List<WaypointNode>();
    [Tooltip("白車路網(TrafficPathNode)的手動指定清單,用法同上")]
    public System.Collections.Generic.List<TrafficPathNode> oncomingNodesOverrideWhite
        = new System.Collections.Generic.List<TrafficPathNode>();

    /// <summary>有沒有使用手動指定(任一份清單有內容就算)</summary>
    private bool UsingManualOncomingNodes =>
        (oncomingNodesOverrideGray != null && oncomingNodesOverrideGray.Count > 0)
     || (oncomingNodesOverrideWhite != null && oncomingNodesOverrideWhite.Count > 0);

    // 對向節奏的執行階段狀態
    private readonly System.Collections.Generic.HashSet<GameObject> oncomingSeenThisBatch
        = new System.Collections.Generic.HashSet<GameObject>();
    private bool oncomingGateOpen = true;          // 閘門開著 = 對向車可以進來
    private float oncomingCooldownUntil = -999f;   // 空窗結束時間
    private readonly System.Collections.Generic.List<WaypointNode> oncomingBlockedGray
        = new System.Collections.Generic.List<WaypointNode>();
    private readonly System.Collections.Generic.List<TrafficPathNode> oncomingBlockedWhite
        = new System.Collections.Generic.List<TrafficPathNode>();

    [Header("🧭 觸發方向判定")]
    // 觸發盒本身沒有方向性,只要碰到就算。於是玩家迴轉、走錯向、或倒車退進盒子裡,
    // 故障車照樣生成 —— 而故障車是生在 spawnPoint,也就是玩家「原本該去的方向」,
    // 結果就是整場事故出現在玩家背後,完全沒有意義。
    //
    // 這裡要求玩家「確實朝著故障車的方向前進」才算數。方向優先用實際移動方向
    // (這樣倒車退進盒子也擋得掉),車幾乎沒動時才退回車頭朝向。
    // 沒通過只是 return,狀態仍然是 Ready —— 玩家之後正著開回來還是會觸發。
    [Tooltip("要求玩家朝著故障車的方向前進才觸發。取消勾選 = 舊行為(碰到就生成)")]
    public bool requirePlayerFacingAccident = true;
    [Range(-1f, 1f)]
    [Tooltip("方向點積門檻。0.5 約等於夾角 60 度內;調高會更嚴格,調低則連斜著進來也算")]
    public float triggerFacingDot = 0.5f;
    [Tooltip("時速低於這個值(m/s)就改用車頭朝向判斷,因為靜止時速度方向是雜訊")]
    public float triggerFacingMinSpeed = 1.0f;
    [Tooltip("印出每次被方向判定擋下來的原因")]
    public bool logFacingReject = false;

    [Header("🔁 可重複設定")]
    [Tooltip("勾選後,故障車演完+玩家離開,冷卻一段時間會重新武裝,可再次觸發;不勾則一次性")]
    public bool repeatable = true;
    [Tooltip("演完後,過幾秒此點重新待命(這段時間內同一點不會再觸發)")]
    public float rearmCooldown = 30f;

    // 💡 重新定義更精準的狀態流程
    private enum EventState { Ready, Spawned_Clearing, RoadBlocked, PlayerArrived, Done, Cooldown }
    private EventState currentState = EventState.Ready;

    /// <summary>
    /// 🧭 玩家是不是「朝著故障車的方向」進到觸發盒的。
    /// 方向基準用「觸發盒 → 故障車生成點」,也就是玩家原本該走的路線方向。
    /// </summary>
    private bool PlayerHeadingIntoAccident(Collider playerCol, out string reason)
    {
        reason = null;
        if (!requirePlayerFacingAccident || spawnPoint == null) return true;

        Vector3 approach = spawnPoint.position - transform.position; approach.y = 0f;
        if (approach.sqrMagnitude < 0.01f) return true;   // 觸發盒與生成點重疊,無從判斷方向
        approach.Normalize();

        // 車頭朝向要從「真正的車體」拿 —— 玩家的碰撞體可能掛在子物件上,
        // 而 transform.root 是整座城市(RRRR),兩者都不能直接用。
        Rigidbody rb = playerCol.GetComponentInParent<Rigidbody>();
        Transform body = rb != null ? rb.transform : playerCol.transform;

        // 優先用實際移動方向:這樣「倒車退進盒子」也會被正確擋掉(車頭朝前但實際往後走)。
        Vector3 heading = body.forward;
        string src = "車頭朝向";
        if (rb != null)
        {
            Vector3 v = rb.velocity; v.y = 0f;
            if (v.magnitude >= triggerFacingMinSpeed) { heading = v.normalized; src = "移動方向"; }
        }
        heading.y = 0f;
        if (heading.sqrMagnitude < 0.01f) return true;
        heading.Normalize();

        float dot = Vector3.Dot(heading, approach);
        if (dot >= triggerFacingDot) return true;

        reason = $"{src} 與故障車方向的點積 {dot:F2} < 門檻 {triggerFacingDot:F2}";
        return false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player") && currentState == EventState.Ready)
        {
            // 🧭 方向不對就當作沒踩到。狀態保持 Ready,玩家正著開回來還是會觸發。
            if (!PlayerHeadingIntoAccident(other, out string facingReason))
            {
                if (logFacingReject)
                    UnityEngine.Debug.Log($"<color=grey>🧭 [故障車] 玩家方向不對,不生成 —— {facingReason}</color>", this);
                return;
            }

            // 🎬 先問導演,沒核准就當作沒踩到
            if (DangerEventDirector.Instance != null && !DangerEventDirector.Instance.RequestEvent("故障車")) return;
            if (IsAmbulanceOnScene())
            {
                UnityEngine.Debug.Log("偵測到場上有救護車，取消生成。");
                return;
            }

            playerTransform = other.transform;
            ClearAndSpawn();
        }
    }

    bool IsAmbulanceOnScene()
    {
        return GameObject.FindWithTag("Ambulance") != null;
    }

    void ClearAndSpawn()
    {
        // 🚀 需求 1：故障車準備生成，全域繞道「立刻觸發」！
        IsAccidentActive = true;
        if (DangerEventDirector.Instance != null) DangerEventDirector.Instance.BeginLongEvent("故障車");
        // 🔧 一定要「複製」而不是指派參考。
        //    舊版 BlockedNodes = blockedNodesSetup 讓靜態清單與 Inspector 上的清單
        //    變成同一個 List 物件,之後任何動態 Add(例如對向節奏要封鎖的節點)
        //    都會直接寫進你在 Inspector 拉好的資料,而且事故結束也清不掉,
        //    下次觸發就殘留上次的內容,越積越多。
        BlockedNodes = new System.Collections.Generic.List<WaypointNode>(blockedNodesSetup);
        BlockedPathNodes = new System.Collections.Generic.List<TrafficPathNode>(blockedPathNodesSetup);

        AllowAIBypassNow = allowAIBypass;   // 🚗 這個事故點是否開放繞行(逐點設定)

        // 🚫 設定禁止生成區
        ActiveAccidentPos = spawnPoint != null ? spawnPoint.position : transform.position;
        NoSpawnNodesGray.Clear();
        NoSpawnNodesWhite.Clear();

        if (UsingNoSpawnRoute)
        {
            // 有指定路線 → 沿路網走訪,半徑照設定值不撐大(避免誤傷隔壁的路)
            foreach (WaypointNode n in CollectRouteGray(RouteStartGray, RouteEndGray))
                NoSpawnNodesGray.Add(n);
            foreach (TrafficPathNode n in CollectRouteWhite(RouteStartWhite, RouteEndWhite))
                NoSpawnNodesWhite.Add(n);
            ActiveNoSpawnRadius = noSpawnRadius;
            UnityEngine.Debug.Log($"🚫 事故禁生成區:路線模式 —— 灰車 {NoSpawnNodesGray.Count} 個節點 / " +
                                  $"白車 {NoSpawnNodesWhite.Count} 個節點,另加半徑 {ActiveNoSpawnRadius:F0}m");
        }
        else
        {
            // 沒指定路線 → 半徑自動撐到涵蓋觸發盒
            float triggerToAccident = spawnPoint != null
                ? Vector3.Distance(transform.position, spawnPoint.position) + 20f : 0f;
            ActiveNoSpawnRadius = Mathf.Max(noSpawnRadius, triggerToAccident);
            UnityEngine.Debug.Log($"🚫 事故禁生成區:半徑模式 {ActiveNoSpawnRadius:F0}m" +
                                  $"(設定值 {noSpawnRadius:F0} / 觸發盒距離 {triggerToAccident:F0})");
        }

        // 對向節奏歸零,從「閘門開啟」開始
        oncomingSeenThisBatch.Clear();
        oncomingBlockedGray.Clear();
        oncomingBlockedWhite.Clear();
        oncomingGateOpen = true;
        oncomingCooldownUntil = -999f;
        RollOncomingRhythm();          // 🎲 第一批的節奏
        seededThisEvent = 0;

        // 📊 繞行評分歸零
        bypassScored = false;
        bypassCrossed = false;
        bypassEnteredZone = false;
        crossOncomingDistance = -1f;
        bypassPassSpeedKmH = -1f;
        retreatNeeded = false;
        retreatDone = false;
        retreatMinOncoming = -1f;
        nextRetreatSampleTime = 0f;
        laneAxisResolved = false;      // 車道軸線在玩家進場時重算一次
        ValidateSeedRoutes();                    // 手動路線接不起來的話現在就報，不要等到現場才發現
        StartCoroutine(OncomingSeedRoutine());   // 🚙 對向上游補車，讓玩家繞行時真的有車要判斷

        // 🧹 清空「觸發盒 → 故障車」這段路。
        //    舊版是以故障車為圓心的 clearRadius 球體、而且用 Destroy() 直接消失。
        //    改成沿路的走廊 + 淡出縮小,並在事故期間持續重掃(側邊路口還是會有車開進來)。
        ClearCorridorOnce(true);
        StartCoroutine(CorridorClearRoutine());

        if (accidentPrefab != null && spawnPoint != null)
        {
            spawnedAccident = Instantiate(accidentPrefab, spawnPoint.position, spawnPoint.rotation);
            // 生成時在原地放一團煙,遮住 pop 的瞬間 + 增加事故真實感
            if (smokeEffectPrefab != null)
            {
                // 煙往事故車後方偏移(spawnPoint 的 -forward 方向)
                Vector3 smokePos = spawnPoint.position - spawnPoint.forward * smokeBackOffset;
                GameObject smoke = Instantiate(smokeEffectPrefab, smokePos, Quaternion.identity);
                Destroy(smoke, 2.5f); // 3 秒後自動清掉
            }

            // 進入「生成與清場監控」狀態
            currentState = EventState.Spawned_Clearing;

            // 啟動清場協程
            StartCoroutine(WaitToBlockRoad());
            UnityEngine.Debug.Log("事故車已生成，全域繞道啟動，紅框監控中...");
        }
    }

    /// <summary>
    /// 清掉「觸發盒 → 故障車」走廊裡的車。
    ///
    /// 用 OverlapCapsule 而不是 OverlapSphere —— 膠囊是沿著兩點之間的線段長出來的，
    /// 所以它跟著路走，不會像球體那樣把旁邊平行的街道一起掃進來。
    /// </summary>
    private int ClearCorridorOnce(bool verbose)
    {
        if (spawnPoint == null) return 0;

        // 走廊方向 = 觸發盒 → 故障車,也就是玩家的行進方向
        Vector3 corridorDir = spawnPoint.position - transform.position;
        corridorDir.y = 0f;
        corridorDir = corridorDir.sqrMagnitude > 0.01f ? corridorDir.normalized : transform.forward;

        Collider[] hits = Physics.OverlapCapsule(
            transform.position, spawnPoint.position, clearCorridorHalfWidth, aiCarLayer);

        Transform player = playerTransformForClear;
        int cleared = 0;
        var done = new System.Collections.Generic.HashSet<GameObject>();

        foreach (var hit in hits)
        {
            if (hit == null) continue;

            ReverseThreatAICar white = hit.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation gray = white == null ? hit.GetComponentInParent<CarNavigation>() : null;
            if (white == null && gray == null) continue;

            // 🚑 救護車是白車的子類,它有自己的任務流程,絕對不能被清掉
            if (white is AmbulanceAI) continue;

            // 路邊演員車預設留著 —— 它們停在路邊不擋路,而且是側切等危險因子的演員
            if (!clearStaticParkedToo)
            {
                if (white != null && white.isStaticParked) continue;
                if (gray != null && gray.isStaticObstacle) continue;
            }

            // 🚗 只清跟走廊同方向的車。對向車道的車不在玩家要走的路線上,
            //    而且對向節奏控制正在管理它們,清掉會跟那套系統打架。
            if (clearOnlySameDirection)
            {
                Transform carTf = white != null ? white.transform : gray.transform;
                if (Vector3.Dot(corridorDir, carTf.forward) < clearSameDirectionDot) continue;
            }

            GameObject go = white != null ? white.gameObject : gray.gameObject;
            if (!done.Add(go)) continue;   // 一台車有多個碰撞體

            // 玩家鼻子前的車不要蒸發 —— 遠處靠 LOD 看不清楚沒關係,近處一定看得到
            if (player != null && clearPlayerSafeRadius > 0f &&
                Vector3.Distance(player.position, go.transform.position) < clearPlayerSafeRadius)
                continue;

            if (clearWithFadeOut)
            {
                // 縮小淡出。它會先關碰撞體、把 Rigidbody 設成 kinematic，
                // 避免縮放趨近 0 時 PhysX 算慣性張量除以零噴 NaN
                float fade = FadeSecondsFor(player, go.transform.position);
                if (white != null) white.ForceFadeOutClear(fade);
                else gray.ForceFadeOutClear(fade);
            }
            else Destroy(go);

            cleared++;
        }

        if (cleared > 0 && verbose)
            UnityEngine.Debug.Log($"🧹 [事故走廊清車] 清除 {cleared} 台" +
                                  $"（走廊半寬 {clearCorridorHalfWidth}m、玩家保護 {clearPlayerSafeRadius}m、" +
                                  $"{(clearWithFadeOut ? "縮小淡出" : "直接消失")}）");
        return cleared;
    }

    /// <summary>
    /// 這台車該用多長的淡出。離玩家越近縮得越慢，越遠收得越快。
    /// 找不到玩家就一律用近處的秒數（保守，寧可慢一點也不要突兀）。
    /// </summary>
    private float FadeSecondsFor(Transform player, Vector3 carPos)
    {
        if (player == null || clearFadeFarDistance <= 0f) return clearFadeSecondsNear;

        float t = Mathf.Clamp01(Vector3.Distance(player.position, carPos) / clearFadeFarDistance);
        return Mathf.Lerp(clearFadeSecondsNear, clearFadeSecondsFar, t);
    }

    /// <summary>
    /// 事故期間持續把走廊清乾淨。
    /// 禁生成路線（NoSpawnNodesWhite/Gray）只擋「在那裡生新車」，
    /// 擋不住原本就在別處、後來從側邊路口開進這段路的車，兩者是互補的。
    /// </summary>
    IEnumerator CorridorClearRoutine()
    {
        // spawnedAccident 在事故收尾時會被 Destroy，用它當結束條件最直接
        while (spawnedAccident != null)
        {
            yield return new WaitForSeconds(Mathf.Max(0.25f, clearRecheckInterval));
            if (spawnedAccident == null) yield break;
            ClearCorridorOnce(false);   // 後續重掃不洗版，只有第一次印
        }
    }

    /// <summary>清車時要保護的玩家。找不到就回 null，代表不做保護。</summary>
    private Transform playerTransformForClear
    {
        get
        {
            if (cachedPlayerForClear == null)
            {
                GameObject p = GameObject.FindGameObjectWithTag("Player");
                if (p != null) cachedPlayerForClear = p.transform;
            }
            return cachedPlayerForClear;
        }
    }
    private Transform cachedPlayerForClear;

    IEnumerator WaitToBlockRoad()
    {
        // 🟢 有放封路偵測框才等車道淨空;沒放就跳過偵測,直接封路
        if (boxCenter != null)
        {
            bool isClear = false;
            while (!isClear)
            {
                Collider[] hits = Physics.OverlapBox(boxCenter.position, boxSize / 2f, boxCenter.rotation, aiCarLayer);

                bool foundAI = false;
                foreach (var hit in hits)
                {
                    if (hit.CompareTag("Player") || hit.gameObject.name == "CAR")
                        continue;

                    if (hit.GetComponentInParent<ReverseThreatAICar>() != null)
                    {
                        foundAI = true;
                        break;
                    }
                }

                if (!foundAI) isClear = true;
                else yield return new WaitForSeconds(0.2f);
            }
        }

        if (currentState == EventState.Spawned_Clearing)
        {
            if (roadBlockObject != null)
            {
                roadBlockObject.SetActive(true);
            }
            currentState = EventState.RoadBlocked;
            UnityEngine.Debug.Log(boxCenter != null
                ? "✅ 目標車道已淨空，三角錐封鎖完成！"
                : "✅ (未設偵測框) 直接封鎖完成！");
        }
    }

    private void Update()
    {
        // 🚦 對向車流節奏:事故進行中就持續運作(不需要玩家已到場)
        if (IsAccidentActive && currentState != EventState.Ready
            && currentState != EventState.Done && currentState != EventState.Cooldown)
        {
            UpdateOncomingRhythm();
            UpdateBypassScoring();   // 📊 追蹤玩家怎麼繞過去的，通過後結算一次
        }

        if (playerTransform == null) return;

        float distance = Vector3.Distance(playerTransform.position, spawnPoint.position);

        // 玩家進入可視距離才註冊，這樣「基準車速」記的是他看到故障車當下的速度
        if (enableHazardTracking && !hazardRegistered
            && IsAccidentActive && distance <= hazardNoticeDistance
            && HazardAwarenessTracker.Instance != null)
        {
            // Silent：故障車在前方，後照鏡方位不對；它也是靜止的，沒有可以「升級」的手段。
            // 教學價值放在事後回饋那句話上。
            // autoFeedback:false —— 故障車不吃通用的反應時間分級。
            // 那組門檻(1/1.5/2.5秒)依據的是「非預期事件」的反應時間研究，
            // 而故障車是靜止的、100m 外就看得到、還主動給過文字提示 ——
            // 玩家隔幾秒才減速是正常駕駛行為，甚至應該先觀察對向再決定，
            // 用突發事件的碼尺去量它，那段「觀察」會被記成反應遲緩。
            // 這裡改由下面的繞行評分負責評語（見 UpdateBypassScoring）。
            HazardAwarenessTracker.Instance.RegisterHazard(
                "故障車", spawnPoint, HazardStyle.Silent, 0f, autoFeedback: !enableBypassScoring);
            hazardRegistered = true;

            // 事前提示：即使視線被其他車擋住，玩家也知道前方有事故該繞開
            ShowAccidentNotice($"玩家進入 {hazardNoticeDistance:F0}m，顯示事故提示");
        }

        // 玩家一直沒有繞開就每隔一段時間再提示一次，直到他做出反應
        if (hazardRegistered && noticeRepeatSeconds > 0f && nextNoticeTime > 0f
            && Time.time >= nextNoticeTime
            && HazardAwarenessTracker.Instance != null
            && !HazardAwarenessTracker.Instance.HasReacted(spawnPoint))
        {
            ShowAccidentNotice("玩家仍未繞開，重複提示");
        }

        switch (currentState)
        {
            case EventState.RoadBlocked:
                // 🚀 需求 2：三角錐已經秀出來了，當玩家車轉過來且貼近到 25 公尺內（原本註解寫5m但代碼是25f，這裡維持25f）
                if (distance < 25f)
                {
                    currentState = EventState.PlayerArrived;

                    // 三角錐功成身退，立刻消失開放 VR 通行
                    if (roadBlockObject != null)
                    {
                        roadBlockObject.SetActive(false);
                        UnityEngine.Debug.Log("🚧 玩家已轉進該車道（距離<25m），三角錐隱藏開放通行。");
                    }
                }
                break;

            case EventState.Spawned_Clearing:
                // 防呆機制：萬一玩家開極快，三角錐還沒生出來玩家就已經到了車禍點
                if (distance < 5f)
                {
                    // 直接沒收三角錐（因為玩家都到了，生出來會穿模插在玩家車上）
                    currentState = EventState.PlayerArrived;
                    UnityEngine.Debug.Log("⚠️ 玩家行駛速度過快，在三角錐生成前已到達，取消三角錐生成。");
                }
                break;

            case EventState.PlayerArrived:
                // 玩家通過並開遠後，觸發結束清場
                if (distance > despawnDistance)
                {
                    EndAccidentEvent();
                }
                break;
        }
    }

    // ==================== 🚦 對向車流節奏 ====================
    //
    // 目的:讓玩家有明確的空檔可以切到對向道、繞過故障車。
    //
    // 節奏:放行 N 台 → 這批開出範圍 → 空窗 M 秒 → 再放 N 台 → ...
    //
    // 「怎麼擋住多出來的車」完全沿用既有的封鎖節點機制:
    //   有分岔       → 車自己選別條路(選路時會過濾封鎖節點)
    //   沒分岔+玩家後方 → 縮小淡出退場(CarNavigation 既有邏輯)
    //   沒分岔+玩家前方 → 照常開過來,不會在玩家眼前憑空消失
    //
    // 所以這裡只負責「什麼時候把對向入口節點加進/移出封鎖清單」。

    // ==================== 🚫 禁生成路線走訪 ====================
    //
    // 觸發盒到故障車很長時,球形半徑會大到誤傷隔壁的路。
    // 改成從「起點」沿 nextChoices 廣度優先走訪,碰到「終點」就停止往下展開,
    // 沿途經過的節點全部列入禁生成 —— 只涵蓋這條路線,不影響旁邊的道路。
    //
    // 遇到分岔會全部收集(那些支線的車一樣會開進事故區),
    // 並用 noSpawnRouteMaxNodes 夾住,路網有環也不會無限展開。

    /// <summary>
    /// 灰車路網:收集「起點到得了、而且走得到終點」的節點。
    ///
    /// 只做正向走訪的話,終點之前岔開的支線也會被一路跟下去,
    /// 那些跟事故無關的路會被誤判成禁生成區。
    /// 所以做兩次走訪再取交集,把「回不到終點」的支線排掉。
    /// (環不會造成無限迴圈 —— visited/forward 集合保證每個節點只處理一次)
    /// </summary>
    private System.Collections.Generic.List<WaypointNode> CollectRouteGray(WaypointNode start, WaypointNode end)
    {
        var result = new System.Collections.Generic.List<WaypointNode>();
        if (start == null || end == null) return result;
        int cap = Mathf.Max(noSpawnRouteMaxNodes * 4, 200);

        // ① 正向:從起點沿 nextChoices 走,到終點就不再往下展開
        var forward = new System.Collections.Generic.HashSet<WaypointNode> { start };
        var q = new System.Collections.Generic.Queue<WaypointNode>();
        q.Enqueue(start);
        while (q.Count > 0 && forward.Count < cap)
        {
            WaypointNode cur = q.Dequeue();
            if (cur == end || cur.nextChoices == null) continue;
            foreach (WaypointNode nx in cur.nextChoices)
            {
                if (nx == null || forward.Contains(nx)) continue;
                forward.Add(nx); q.Enqueue(nx);
            }
        }

        // ② 反向:從終點回溯(誰的 nextChoices 指向我),只在正向可達的範圍內找
        var all = FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
        var backward = new System.Collections.Generic.HashSet<WaypointNode> { end };
        var q2 = new System.Collections.Generic.Queue<WaypointNode>();
        q2.Enqueue(end);
        while (q2.Count > 0 && backward.Count < cap)
        {
            WaypointNode cur = q2.Dequeue();
            foreach (WaypointNode n in all)
            {
                if (n == null || backward.Contains(n) || !forward.Contains(n)) continue;
                if (n.nextChoices == null || !n.nextChoices.Contains(cur)) continue;
                backward.Add(n); q2.Enqueue(n);
            }
        }

        // ③ 交集 = 真正在「起點 → 終點」路線上的節點
        foreach (WaypointNode n in forward)
            if (backward.Contains(n) && result.Count < noSpawnRouteMaxNodes) result.Add(n);
        return result;
    }

    /// <summary>白車路網:作法同 CollectRouteGray</summary>
    private System.Collections.Generic.List<TrafficPathNode> CollectRouteWhite(TrafficPathNode start, TrafficPathNode end)
    {
        var result = new System.Collections.Generic.List<TrafficPathNode>();
        if (start == null || end == null) return result;
        int cap = Mathf.Max(noSpawnRouteMaxNodes * 4, 200);

        var forward = new System.Collections.Generic.HashSet<TrafficPathNode> { start };
        var q = new System.Collections.Generic.Queue<TrafficPathNode>();
        q.Enqueue(start);
        while (q.Count > 0 && forward.Count < cap)
        {
            TrafficPathNode cur = q.Dequeue();
            if (cur == end || cur.nextChoices == null) continue;
            foreach (TrafficPathNode nx in cur.nextChoices)
            {
                if (nx == null || forward.Contains(nx)) continue;
                forward.Add(nx); q.Enqueue(nx);
            }
        }

        var all = FindObjectsByType<TrafficPathNode>(FindObjectsSortMode.None);
        var backward = new System.Collections.Generic.HashSet<TrafficPathNode> { end };
        var q2 = new System.Collections.Generic.Queue<TrafficPathNode>();
        q2.Enqueue(end);
        while (q2.Count > 0 && backward.Count < cap)
        {
            TrafficPathNode cur = q2.Dequeue();
            foreach (TrafficPathNode n in all)
            {
                if (n == null || backward.Contains(n) || !forward.Contains(n)) continue;
                if (n.nextChoices == null || !n.nextChoices.Contains(cur)) continue;
                backward.Add(n); q2.Enqueue(n);
            }
        }

        foreach (TrafficPathNode n in forward)
            if (backward.Contains(n) && result.Count < noSpawnRouteMaxNodes) result.Add(n);
        return result;
    }

    /// <summary>是不是「對向」——方向相反 + 在同一條路上(排除隔壁平行街道)</summary>
    private bool IsOncoming(Vector3 pos, Vector3 forward)
    {
        Vector3 accForward = spawnPoint.forward; accForward.y = 0f;
        if (accForward.sqrMagnitude < 0.01f) return false;
        accForward.Normalize();

        Vector3 f = forward; f.y = 0f;
        if (f.sqrMagnitude < 0.01f) return false;
        if (Vector3.Dot(accForward, f.normalized) > oncomingDirectionDot) return false;  // 不是反向

        // 到「事故點前後軸線」的垂直距離 → 排除隔壁平行街道
        Vector3 toIt = pos - spawnPoint.position; toIt.y = 0f;
        Vector3 right = Vector3.Cross(Vector3.up, accForward);
        return Mathf.Abs(Vector3.Dot(toIt, right)) <= sameRoadLateralTolerance;
    }

    /// <summary>
    /// 🎯 手動指定模式:這台車是不是走在「對向清單」的路線上。
    /// 看它目前的導航目標節點在不在清單裡 —— 沿著路網判斷,不受彎道影響。
    /// </summary>
    private bool IsCarOnManualOncomingRoute(CarNavigation gray, ReverseThreatAICar white)
    {
        if (gray != null)
        {
            WaypointNode t = gray.CurrentTargetNode;
            return t != null && oncomingNodesOverrideGray != null && oncomingNodesOverrideGray.Contains(t);
        }
        if (white != null)
        {
            TrafficPathNode t = white.currentWaypointNode;
            return t != null && oncomingNodesOverrideWhite != null && oncomingNodesOverrideWhite.Contains(t);
        }
        return false;
    }

    /// <summary>把範圍內的對向入口節點加進封鎖清單(閘門關閉)</summary>
    private void CloseOncomingGate()
    {
        if (!oncomingGateOpen) return;
        oncomingGateOpen = false;

        if (UsingManualOncomingNodes)
        {
            // 🎯 彎道:完全以手動清單為準,不做任何幾何判定
            if (oncomingNodesOverrideGray != null)
                foreach (WaypointNode n in oncomingNodesOverrideGray)
                {
                    if (n == null || BlockedNodes.Contains(n)) continue;
                    BlockedNodes.Add(n);
                    oncomingBlockedGray.Add(n);
                }
            if (oncomingNodesOverrideWhite != null)
                foreach (TrafficPathNode n in oncomingNodesOverrideWhite)
                {
                    if (n == null || BlockedPathNodes.Contains(n)) continue;
                    BlockedPathNodes.Add(n);
                    oncomingBlockedWhite.Add(n);
                }
        }
        else
        {
            // 直路:用直線走廊自動偵測
            foreach (WaypointNode n in FindObjectsByType<WaypointNode>(FindObjectsSortMode.None))
            {
                if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
                if (Vector3.Distance(n.transform.position, spawnPoint.position) > oncomingLimitRadius) continue;
                Vector3 dir = n.nextChoices[0].transform.position - n.transform.position;
                if (!IsOncoming(n.transform.position, dir)) continue;
                if (BlockedNodes.Contains(n)) continue;
                BlockedNodes.Add(n);
                oncomingBlockedGray.Add(n);
            }

            foreach (TrafficPathNode n in FindObjectsByType<TrafficPathNode>(FindObjectsSortMode.None))
            {
                if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
                if (Vector3.Distance(n.transform.position, spawnPoint.position) > oncomingLimitRadius) continue;
                Vector3 dir = n.nextChoices[0].transform.position - n.transform.position;
                if (!IsOncoming(n.transform.position, dir)) continue;
                if (BlockedPathNodes.Contains(n)) continue;
                BlockedPathNodes.Add(n);
                oncomingBlockedWhite.Add(n);
            }
        }

        UnityEngine.Debug.Log($"<color=orange>🚦 對向已達 {maxOncomingPerBatch} 台,關閉入口" +
                              $"({(UsingManualOncomingNodes ? "手動指定" : "自動偵測")}:" +
                              $"封鎖 {oncomingBlockedGray.Count} 個灰車節點 / {oncomingBlockedWhite.Count} 個白車節點)</color>");
    }

    /// <summary>把這一輪封鎖的對向節點移出清單(閘門開啟)。只移除自己加的,不動 Inspector 原本的設定</summary>
    private void OpenOncomingGate()
    {
        foreach (WaypointNode n in oncomingBlockedGray) BlockedNodes.Remove(n);
        foreach (TrafficPathNode n in oncomingBlockedWhite) BlockedPathNodes.Remove(n);
        oncomingBlockedGray.Clear();
        oncomingBlockedWhite.Clear();
        oncomingSeenThisBatch.Clear();
        oncomingGateOpen = true;
    }

    /// <summary>
    /// 🚙 對向上游補車：每隔一段時間看走廊裡的對向車夠不夠，不夠就在上游補一台。
    /// 事故物件消失（事件結束）或玩家已經開過去就自然結束。
    /// </summary>
    IEnumerator OncomingSeedRoutine()
    {
        // 第一次補刻意提早一點，讓玩家從收到提示到抵達的這段路上就看得到對向有車
        yield return new WaitForSeconds(Mathf.Min(2f, seedCheckInterval));

        while (spawnedAccident != null)
        {
            TrySeedOncoming();
            yield return new WaitForSeconds(Mathf.Max(0.5f, seedCheckInterval));
        }
    }

    /// <param name="force">
    /// true = 繞行中的追加補車：跳過「走廊內已經夠多」的檢查（那個檢查是給常態節奏用的，
    /// 這裡的目的就是刻意製造一台來車）。其餘的上限與距離保護照舊，不會無限增生。
    /// </param>
    private void TrySeedOncoming(bool force = false)
    {
        if (!enableOncomingSeeding || oncomingCarPrefab == null || spawnPoint == null) return;

        if (seededThisEvent >= seedMaxTotal)
        {
            if (logSeeding) UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 已達本次上限 {seedMaxTotal} 台，停止補車</color>");
            return;
        }

        // playerTransform 只有在玩家踩過觸發盒後才有值，這裡沿用既有的快取查找當備援
        Transform player = playerTransform != null ? playerTransform : playerTransformForClear;
        if (player == null) return;

        Vector3 toAccident = spawnPoint.position - player.position;
        toAccident.y = 0f;

        if (toAccident.magnitude > seedPlayerMaxDistance)
        {
            if (logSeeding) UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 玩家還在 {toAccident.magnitude:F0}m 外，先不補</color>");
            return;
        }

        // 事故點已經在玩家後方一段距離 → 他繞過去了，這場戲結束，不用再補
        if (Vector3.Dot(toAccident, player.forward) < -25f)
        {
            if (logSeeding) UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 玩家已通過事故點，停止補車</color>");
            return;
        }

        int inZone = CountOncomingInZone();
        if (!force && inZone >= seedTargetCount)
        {
            if (logSeeding) UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 走廊內已有 {inZone} 台（目標 {seedTargetCount}），不補</color>");
            return;
        }

        // 🎯 有手動指定完整路線就優先用 —— 生成點在分岔路口時，只指定生成點不夠，
        //    車還是會在那個路口隨機選邊。
        SeedRoute picked = PickManualSeedRoute();
        if (picked != null)
        {
            SpawnSeedCar(picked.nodes[0], new List<WaypointNode>(picked.nodes.GetRange(1, picked.nodes.Count - 1)), inZone);
            return;
        }

        WaypointNode node = FindUpstreamOncomingNode();
        if (node == null)
        {
            if (logSeeding) UnityEngine.Debug.LogWarning($"<color=orange>🚙 [對向補車] 找不到合適的上游節點" +
                $"（距離 {seedDistanceRange.x:F0}~{seedDistanceRange.y:F0}m、行進方向朝著事故點、" +
                $"且沿路網走得到事故點）</color>", this);
            return;
        }

        List<WaypointNode> route = BuildRouteToAccident(node);

        // 手動指定的生成點不做可達性把關（使用者說從這裡出來就從這裡出來），
        // 真的算不出路線就退回「照它自己的第一個路徑點走」，至少車會動。
        if (route == null || route.Count == 0)
        {
            if (node.nextChoices == null || node.nextChoices.Count == 0) return;
            route = new List<WaypointNode> { node.nextChoices[0] };
        }

        SpawnSeedCar(node, route, inZone);
    }

    /// <summary>
    /// 檢查手動路線的相鄰節點在路網上是不是真的接得起來。
    /// 接不起來的話 SetForcedRoute 會靜靜地跳過那一段（車就在路口隨機轉走），
    /// 這種錯很難從畫面上看出來，所以事件開始時主動檢查並報位置。
    /// </summary>
    private void ValidateSeedRoutes()
    {
        if (seedRoutes == null) return;

        for (int r = 0; r < seedRoutes.Count; r++)
        {
            SeedRoute sr = seedRoutes[r];
            if (sr == null || sr.nodes == null) continue;

            for (int i = 0; i < sr.nodes.Count - 1; i++)
            {
                WaypointNode a = sr.nodes[i], b = sr.nodes[i + 1];
                if (a == null || b == null)
                {
                    UnityEngine.Debug.LogWarning($"<color=orange>🚙 [對向補車] 手動路線 #{r} 的第 {i} / {i + 1} 個節點是空的</color>", this);
                    continue;
                }
                if (a.nextChoices == null || !a.nextChoices.Contains(b))
                    UnityEngine.Debug.LogWarning(
                        $"<color=orange>🚙 [對向補車] 手動路線 #{r}：「{a.name}」接不到「{b.name}」" +
                        $"（後者不在前者的 Next Choices 裡）。這一段會被跳過，車可能在路口轉錯邊。\n" +
                        $"請改拉路網上真的相鄰的節點</color>", a);
            }
        }
    }

    /// <summary>從 seedRoutes 挑一條可用的手動路線。沒設定或都被佔用就回 null。</summary>
    private SeedRoute PickManualSeedRoute()
    {
        if (seedRoutes == null || seedRoutes.Count == 0) return null;

        List<SeedRoute> free = new List<SeedRoute>();
        foreach (SeedRoute r in seedRoutes)
        {
            if (r == null || r.nodes == null || r.nodes.Count < 2) continue;   // 至少要有生成點+一個路徑點
            if (r.nodes[0] == null) continue;
            if (Physics.CheckSphere(r.nodes[0].transform.position, 3f, aiCarLayer)) continue;  // 生成點已經有車
            free.Add(r);
        }

        if (free.Count == 0)
        {
            if (logSeeding)
                UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 手動路線 {seedRoutes.Count} 條目前都被佔用" +
                    $"（或節點少於 2 個），這次不補</color>", this);
            return null;
        }
        return free[Random.Range(0, free.Count)];
    }

    /// <summary>在指定節點生成一台對向車，並指定它開頭要走的路線。</summary>
    private void SpawnSeedCar(WaypointNode node, List<WaypointNode> route, int inZone)
    {
        if (node == null || route == null || route.Count == 0) return;

        WaypointNode next = route[0];
        if (next == null) return;

        Quaternion rot = Quaternion.LookRotation(
            (next.transform.position - node.transform.position).normalized, Vector3.up);

        GameObject go = Instantiate(oncomingCarPrefab, node.transform.position, rot);
        SpawnGroundSnap.Apply(go);
        go.name = "Oncoming_Seed_" + Time.time;

        CarNavigation nav = go.GetComponentInParent<CarNavigation>();
        if (nav != null)
        {
            nav.currentWaypointNode = next;
            // 🎯 指定路線：不讓它在中途的路口隨機轉走，走完這段就恢復正常隨機選路
            nav.SetForcedRoute(route);
        }
        else if (logSeeding)
        {
            UnityEngine.Debug.LogWarning($"<color=orange>🚙 [對向補車] {oncomingCarPrefab.name} 上找不到 CarNavigation" +
                $"（是不是設成白車 prefab 了？補車必須用灰車）</color>", this);
        }

        seededThisEvent++;
        if (logSeeding)
            UnityEngine.Debug.Log($"<color=lime>🚙 [對向補車] 在 {node.name} 補一台" +
                $"（離事故點 {Vector3.Distance(node.transform.position, spawnPoint.position):F0}m，" +
                $"指定路線 {route.Count} 段，走廊內 {inZone}→{inZone + 1} 台，本次累計 {seededThisEvent}）</color>", node);
    }

    /// <summary>
    /// 📊 繞行評分。每幀追蹤玩家在事故點附近的位置與速度，
    /// 在他通過之後結算一次，講評的是「這次繞得安不安全」而不是「你幾秒內反應」。
    /// </summary>
    /// <summary>
    /// 決定車道軸線：優先用白車路網（沿真實車道鋪設，比 prefab 擺放角度可靠），
    /// 找不到節點才退回事故車自己的朝向。事件開始時算一次就好，路不會動。
    /// </summary>
    private void ResolveLaneAxis()
    {
        laneAxisResolved = true;
        laneAxisOrigin = spawnPoint.position;
        laneAxisDir = spawnPoint.forward; laneAxisDir.y = 0f;
        if (laneAxisDir.sqrMagnitude > 0.01f) laneAxisDir.Normalize();

        if (!useNetworkLaneAxis) return;

        TrafficPathNode nearest = null;
        float bestD = laneAxisSearchRadius;

        foreach (TrafficPathNode n in FindObjectsOfType<TrafficPathNode>())
        {
            if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
            if (n.nextChoices[0] == null) continue;

            float d = Vector3.Distance(n.transform.position, spawnPoint.position);
            if (d < bestD) { bestD = d; nearest = n; }
        }

        if (nearest == null)
        {
            if (logBypassScoring)
                UnityEngine.Debug.LogWarning($"<color=orange>📊 [繞行] {laneAxisSearchRadius:F0}m 內找不到白車路網節點，" +
                    $"退回用 spawnPoint.forward 當車道軸線（準度較差）</color>", this);
            return;
        }

        Vector3 dir = nearest.nextChoices[0].transform.position - nearest.transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) return;

        // 方向要跟事故車朝向大致一致，否則會抓到對向那條的節點
        Vector3 accFwdRef = spawnPoint.forward; accFwdRef.y = 0f;
        if (accFwdRef.sqrMagnitude > 0.01f
            && Vector3.Dot(dir.normalized, accFwdRef.normalized) < 0f) return;

        laneAxisDir = dir.normalized;
        laneAxisOrigin = nearest.transform.position;   // 橫向原點落在車道中心線上，不是故障車停的位置

        if (logBypassScoring)
            UnityEngine.Debug.Log($"<color=cyan>📊 [繞行] 車道軸線取自路網節點 {nearest.name}" +
                $"（離事故點 {bestD:F1}m）</color>", nearest);
    }

    private void UpdateBypassScoring()
    {
        if (!enableBypassScoring || bypassScored || spawnPoint == null) return;

        Transform player = playerTransform != null ? playerTransform : playerTransformForClear;
        if (player == null) return;

        if (!laneAxisResolved) ResolveLaneAxis();

        Vector3 accFwd = laneAxisDir;
        if (accFwd.sqrMagnitude < 0.01f) return;
        Vector3 accRight = Vector3.Cross(Vector3.up, accFwd);

        // 縱向仍以事故點為基準（要判斷有沒有開過去），橫向則以車道中心線為基準
        Vector3 relAcc = player.position - spawnPoint.position; relAcc.y = 0f;
        float along = Vector3.Dot(relAcc, accFwd);      // 正 = 已經超過事故點

        Vector3 relLane = player.position - laneAxisOrigin; relLane.y = 0f;
        float lateral = Vector3.Dot(relLane, accRight);  // 台灣靠右行駛 → 繞行是往左，值會變負

        if (Mathf.Abs(along) > bypassZoneLength)
        {
            // 已經開過去了才結算（進過區域 + 現在在事故點前方）
            if (bypassEnteredZone && along > 0f) FinishBypassScoring();
            return;
        }

        bypassEnteredZone = true;

        // 🏁 一超過事故點就結算，不等他開出整個評分區 ——
        //    回饋要緊接在行為之後才有教學效果
        if (along > bypassSettleDistance)
        {
            FinishBypassScoring();
            return;
        }

        // 跨線瞬間：記下當下最近的對向來車有多遠 —— 這一刻的判斷才是這個科目的核心
        if (!bypassCrossed && Mathf.Abs(lateral) > centerLineOffset)
        {
            bypassCrossed = true;
            crossOncomingDistance = NearestOncomingDistance();
            if (logBypassScoring)
                UnityEngine.Debug.Log($"<color=cyan>📊 [繞行] 玩家跨線（橫向 {lateral:F1}m），" +
                    $"當下最近對向來車 {(crossOncomingDistance < 0f ? "無" : crossOncomingDistance.ToString("F0") + "m")}</color>");

            // 🚗 跨線當下對向是空的 → 在上游補一台，讓它幾秒後開進來。
            //    玩家會在「已經切出去」的狀態下發現對向有車，必須退回原車道 —— 那正是要練的。
            //    注意評分用的 crossOncomingDistance 上面已經記好了，所以這台不會害他被判危險繞行：
            //    他跨線那一刻確實是淨空的，判斷沒錯，接下來考的是「發現後怎麼處理」。
            if (seedOnBypassCommit
                && (crossOncomingDistance < 0f || crossOncomingDistance > commitSeedMinGap))
            {
                if (logBypassScoring)
                    UnityEngine.Debug.Log($"<color=cyan>🚗 [繞行] 跨線時對向淨空 → 上游補一台，" +
                        $"訓練「切出去才發現對向有車」的退回判斷</color>");
                TrySeedOncoming(true);
            }
        }

        // 🚗 跨線之後：對向來車逼近到門檻內 → 這一刻就該退回原車道等空檔。
        //    跟 crossOncomingDistance 的差別是那個只有跨線瞬間一次快照，
        //    這裡是「還壓在對向車道上的整段期間」都持續在看。
        if (scoreRetreat && bypassCrossed && !retreatDone)
        {
            bool overLine = Mathf.Abs(lateral) > centerLineOffset;

            if (overLine && Time.time >= nextRetreatSampleTime)
            {
                nextRetreatSampleTime = Time.time + Mathf.Max(0.05f, retreatSampleInterval);
                float d = NearestOncomingDistance();
                if (d >= 0f && d <= retreatOncomingDistance)
                {
                    if (!retreatNeeded && logBypassScoring)
                        UnityEngine.Debug.Log($"<color=cyan>🚗 [繞行] 對向來車逼近到 {d:F0}m" +
                            $"（門檻 {retreatOncomingDistance:F0}m），玩家還壓在對向車道 → 現在該退回</color>");
                    retreatNeeded = true;
                    retreatMinOncoming = retreatMinOncoming < 0f ? d : Mathf.Min(retreatMinOncoming, d);
                }
            }

            // 退回自己車道。用 0.8 倍做遲滯，免得剛好壓在門檻上時反覆判定
            if (retreatNeeded && Mathf.Abs(lateral) < centerLineOffset * 0.8f)
            {
                retreatDone = true;
                if (logBypassScoring)
                    UnityEngine.Debug.Log($"<color=lime>🚗 [繞行] 玩家已退回原車道（橫向 {lateral:F1}m）→ 判斷正確</color>");
            }
        }

        // 通過事故點正側方時記一次車速
        if (bypassPassSpeedKmH < 0f && Mathf.Abs(along) < 6f)
        {
            Rigidbody rb = player.GetComponentInParent<Rigidbody>();
            bypassPassSpeedKmH = rb != null ? rb.velocity.magnitude * 3.6f : 0f;
        }
    }

    private void FinishBypassScoring()
    {
        if (bypassScored) return;
        bypassScored = true;

        bool danger = bypassCrossed && crossOncomingDistance >= 0f
                      && crossOncomingDistance < dangerousOncomingDistance;
        bool tooFast = bypassPassSpeedKmH > safePassSpeedKmH;
        // 🚗 對向來車逼近過、但玩家一路壓在對向車道沒退回
        bool notRetreated = scoreRetreat && retreatNeeded && !retreatDone;

        string msg;
        Color color;

        // 💥 撞到最重，先判 —— 理由寫在 bypassCrashMessage 宣告處
        bool crashed = HazardAwarenessTracker.Instance != null
                       && HazardAwarenessTracker.Instance.AnyActiveHazardCollided();

        if (crashed)
        {
            msg = bypassCrashMessage;
            color = bypassBadColor;
        }
        else if (danger)
        {
            msg = bypassDangerMessage;
            color = bypassBadColor;
        }
        else if (notRetreated)
        {
            msg = bypassNoRetreatMessage;
            color = bypassBadColor;
        }
        else if (tooFast)
        {
            msg = bypassFastMessage;
            color = bypassBadColor;
        }
        else
        {
            msg = bypassSafeMessage;
            color = bypassGoodColor;
        }

        // 📊 只有畫面文字冠上「評分：」，底下那句語音不受影響 —— 理由寫在欄位宣告處
        if (!string.IsNullOrEmpty(bypassScorePrefix) && !string.IsNullOrEmpty(msg))
            msg = bypassScorePrefix + msg;

        if (HazardAwarenessTracker.Instance != null && !string.IsNullOrEmpty(msg))
            HazardAwarenessTracker.Instance.ShowMessage(msg, color, bypassMessageSeconds);

        // 🔊 事後語音：依實際的失敗原因挑那一句，安全通過不播（做對事不需要被說教）
        // 撞到／沒退回都沿用「對向有來車應停等空檔再繞行」那句 —— 三者的成因是同一個
        AudioClip voice = (crashed || danger || notRetreated)
            ? voiceDangerousBypass
            : (tooFast ? voiceFastPass : null);
        if (voice != null && HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.PlayVoice(voice);

        // 📊 只計次，不扣分 —— ScoreManager 的分數是模擬考場項目(S彎/坡道/停車)的成績，
        //    危險應變是另一個維度的能力，混進同一個分數裡兩邊都會失真。
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.ReportBypassResult(danger, tooFast);

        UnityEngine.Debug.Log($"<color={(crashed || danger || notRetreated || tooFast ? "orange" : "lime")}>📊 [繞行評分] " +
            $"碰撞={crashed} " +
            $"跨線={bypassCrossed} 跨線時對向距離={(crossOncomingDistance < 0f ? "無車" : crossOncomingDistance.ToString("F0") + "m")}" +
            $"（危險門檻 {dangerousOncomingDistance:F0}m）" +
            $" 該退回={retreatNeeded}" +
            $"{(retreatNeeded ? $"（對向逼近到 {retreatMinOncoming:F0}m，門檻 {retreatOncomingDistance:F0}m）" : "")}" +
            $" 有退回={retreatDone}" +
            $" 通過車速={bypassPassSpeedKmH:F0}km/h（門檻 {safePassSpeedKmH:F0}）" +
            $" → {(crashed ? "發生碰撞" : danger ? "危險繞行" : notRetreated ? "未退回" : tooFast ? "速度偏快" : "安全通過")}</color>");
    }

    /// <summary>走廊內最近的對向來車離事故點多遠。沒有車回 -1。</summary>
    private float NearestOncomingDistance()
    {
        float best = -1f;
        Collider[] hits = Physics.OverlapSphere(spawnPoint.position, oncomingLimitRadius, aiCarLayer);
        foreach (Collider c in hits)
        {
            if (c == null) continue;
            CarNavigation gray = c.GetComponentInParent<CarNavigation>();
            ReverseThreatAICar white = gray == null ? c.GetComponentInParent<ReverseThreatAICar>() : null;
            Transform tf = gray != null ? gray.transform : (white != null ? white.transform : null);
            if (tf == null) continue;
            if (white is AmbulanceAI) continue;
            if (gray != null && gray.isStaticObstacle) continue;
            if (white != null && white.isStaticParked) continue;

            if (UsingManualOncomingNodes)
            {
                if (!IsCarOnManualOncomingRoute(gray, white)) continue;
            }
            else if (!IsOncoming(tf.position, tf.forward)) continue;

            // 只算「還沒過來」的:已經開過事故點的車不構成威脅
            Vector3 accFwd = spawnPoint.forward; accFwd.y = 0f;
            Vector3 toCar = tf.position - spawnPoint.position; toCar.y = 0f;
            if (Vector3.Dot(toCar, accFwd.normalized) < 0f) continue;

            float d = toCar.magnitude;
            if (best < 0f || d < best) best = d;
        }
        return best;
    }

    /// <summary>走廊內目前有幾台對向車。跟 UpdateOncomingRhythm 用同一套判定，但不動批次狀態。</summary>
    private int CountOncomingInZone()
    {
        int n = 0;
        Collider[] hits = Physics.OverlapSphere(spawnPoint.position, oncomingLimitRadius, aiCarLayer);
        foreach (Collider c in hits)
        {
            if (c == null) continue;
            CarNavigation gray = c.GetComponentInParent<CarNavigation>();
            ReverseThreatAICar white = gray == null ? c.GetComponentInParent<ReverseThreatAICar>() : null;
            Transform tf = gray != null ? gray.transform : (white != null ? white.transform : null);
            if (tf == null) continue;
            if (white is AmbulanceAI) continue;
            if (gray != null && gray.isStaticObstacle) continue;
            if (white != null && white.isStaticParked) continue;

            if (UsingManualOncomingNodes)
            {
                if (!IsCarOnManualOncomingRoute(gray, white)) continue;
            }
            else if (!IsOncoming(tf.position, tf.forward)) continue;

            n++;
        }
        return n;
    }

    /// <summary>
    /// 從起點沿路網往事故點走，做出一條「一定會經過事故點旁邊」的路線。
    ///
    /// 為什麼需要這個：補出來的車照常在路口隨機選邊，80~120m 的路上通常至少有一個路口，
    /// 車很可能在抵達之前就轉走 —— 那台車就白補了，玩家繞行時對向照樣沒車。
    /// 這裡每一步都挑「最接近事故點」的分支，走到夠近就收工，再把路線交給那台車照走。
    ///
    /// 回傳 null 代表這個起點根本走不到事故點（例如中間全是背離的分支），該起點要淘汰。
    /// </summary>
    private List<WaypointNode> BuildRouteToAccident(WaypointNode start)
    {
        if (start == null) return null;
        int maxSteps = Mathf.Max(10, seedRouteMaxSteps);

        List<WaypointNode> route = new List<WaypointNode>();
        HashSet<WaypointNode> visited = new HashSet<WaypointNode> { start };
        WaypointNode cur = start;

        float bestDist = Vector3.Distance(cur.transform.position, spawnPoint.position);

        for (int step = 0; step < maxSteps; step++)
        {
            if (cur.nextChoices == null || cur.nextChoices.Count == 0) break;

            WaypointNode best = null;
            float bestD = float.MaxValue;

            foreach (WaypointNode n in cur.nextChoices)
            {
                if (n == null || visited.Contains(n)) continue;
                float d = Vector3.Distance(n.transform.position, spawnPoint.position);
                if (d < bestD) { bestD = d; best = n; }
            }

            if (best == null) break;

            // 選出來的分支反而離事故點更遠 → 開始背離就停。
            // 容許值不能設太小：彎道上路會先繞遠再彎回來，5m 的容許值會在彎道中途就放棄。
            if (bestD > bestDist + seedRouteDivergeTolerance) break;

            route.Add(best);
            visited.Add(best);
            cur = best;
            bestDist = Mathf.Min(bestDist, bestD);

            // 已經開到事故點旁邊了，後面隨他去。
            // 注意對向車道不會穿過事故點，是從旁邊經過 —— 所以這個半徑至少要涵蓋路寬。
            if (bestD <= seedRouteArriveRadius) return route;
        }

        // 沒走到事故點附近。記下最近走到多遠、走了幾步，供診斷判斷是
        // 「差一點」還是「路網接不過去」還是「步數用完」
        lastRouteClosest = bestDist;
        lastRouteSteps = route.Count;

        // 走出來的那一段至少方向是對的，用它總比一台都不補好 ——
        // 車沿著這段開的時候通常已經經過事故點附近了，之後就算岔開影響也不大。
        if (seedAllowPartialRoute && route.Count > 0) return route;

        return null;
    }

    /// <summary>上一次 BuildRouteToAccident 失敗時，最近走到離事故點幾公尺（診斷用）</summary>
    private float lastRouteClosest = -1f;
    /// <summary>上一次 BuildRouteToAccident 失敗時走了幾步（診斷用）</summary>
    private int lastRouteSteps = 0;

    /// <summary>
    /// 找對向車流的「上游」節點：離事故點在指定距離區間內、而且它的行進方向是朝著事故點的。
    ///
    /// 「行進方向朝著事故點」這個條件就足以判斷上游，不需要反查路網的前驅節點 ——
    /// 對向車就是從那個方向開過來的。
    /// </summary>
    private WaypointNode FindUpstreamOncomingNode()
    {
        // 🎯 有手動指定就完全以清單為準，跳過整套幾何判定。
        //    幾何搜尋要同時滿足五個條件，路網形狀一變就可能全軍覆沒；
        //    使用者直接指定「對向車該從哪出現」既最快也最可控。
        if (seedSpawnNodes != null && seedSpawnNodes.Count > 0)
        {
            List<WaypointNode> free = new List<WaypointNode>();
            foreach (WaypointNode n in seedSpawnNodes)
            {
                if (n == null || n.nextChoices == null || n.nextChoices.Count == 0) continue;
                if (Physics.CheckSphere(n.transform.position, 3f, aiCarLayer)) continue;  // 那裡已經有車
                free.Add(n);
            }

            if (free.Count == 0)
            {
                if (logSeeding)
                    UnityEngine.Debug.Log($"<color=grey>🚙 [對向補車] 手動指定的 {seedSpawnNodes.Count} 個生成點" +
                        $"目前都被佔用或沒接路徑，這次不補</color>", this);
                return null;
            }
            return free[Random.Range(0, free.Count)];
        }

        if (cachedGrayNodes == null || cachedGrayNodes.Length == 0)
            cachedGrayNodes = FindObjectsOfType<WaypointNode>();

        float lo = Mathf.Min(seedDistanceRange.x, seedDistanceRange.y);
        float hi = Mathf.Max(seedDistanceRange.x, seedDistanceRange.y);

        List<WaypointNode> candidates = new List<WaypointNode>();

        // 每一關淘汰了幾個 —— 「找不到」時要知道是卡在哪一關，否則只能亂猜
        int total = 0, rejDist = 0, rejHeading = 0, rejDirection = 0,
            rejCorridor = 0, rejOccupied = 0, rejRoute = 0;
        List<string> routeFailDetail = new List<string>();

        foreach (WaypointNode node in cachedGrayNodes)
        {
            if (node == null || node.nextChoices == null || node.nextChoices.Count == 0) continue;
            if (node.nextChoices[0] == null) continue;
            total++;

            Vector3 toAcc = spawnPoint.position - node.transform.position;
            toAcc.y = 0f;
            float d = toAcc.magnitude;
            if (d < lo || d > hi) { rejDist++; continue; }

            // 這個節點的行進方向
            Vector3 travel = node.nextChoices[0].transform.position - node.transform.position;
            travel.y = 0f;
            if (travel.sqrMagnitude < 0.01f) continue;

            // 行進方向要朝著事故點 → 它在上游
            if (Vector3.Dot(travel.normalized, toAcc.normalized) < 0.6f) { rejHeading++; continue; }

            // ⚠️ 方向判定一律要做，手動清單模式也不能跳過。
            //    「朝著事故點開」這個條件，玩家自己那條車道的上游節點同樣成立 ——
            //    少了這一關就會把車補到玩家同向的車道上。
            Vector3 accFwd = spawnPoint.forward; accFwd.y = 0f;
            if (accFwd.sqrMagnitude < 0.01f) continue;
            if (Vector3.Dot(accFwd.normalized, travel.normalized) > oncomingDirectionDot) { rejDirection++; continue; }

            // 「同一條路」的橫向走廊檢查只在直路做 —— 彎道上這個判定會歪掉，
            // 那正是手動清單模式存在的理由，所以那個模式只靠上面的方向判定。
            if (!UsingManualOncomingNodes && !IsOncoming(node.transform.position, travel.normalized))
            { rejCorridor++; continue; }

            // 那個位置不能已經有車
            if (Physics.CheckSphere(node.transform.position, 3f, aiCarLayer)) { rejOccupied++; continue; }

            // ⚠️ 最後一關：沿路網真的走得到事故點嗎。
            //    路網有分岔，「方向朝著事故點」不代表走得到 —— 中間一個路口就可能整條岔開，
            //    補出來的車就白補了。走不到的起點直接淘汰。
            if (BuildRouteToAccident(node) == null)
            {
                rejRoute++;
                // 只差一點 = 抵達半徑不夠；差很多 = 路網真的接不過去。這兩個要分開處理，
                // 所以把每個失敗節點「最近走到幾公尺、走了幾步」收集起來一起印。
                routeFailDetail.Add($"{node.name}(起點{d:F0}m→最近{lastRouteClosest:F0}m，走了{lastRouteSteps}步)");
                continue;
            }

            candidates.Add(node);
        }

        if (candidates.Count == 0)
        {
            if (logSeeding)
                UnityEngine.Debug.LogWarning(
                    $"<color=orange>🚙 [對向補車] 候選淘汰統計（灰車節點共 {total} 個）：\n" +
                    $"  距離不在 {lo:F0}~{hi:F0}m → 淘汰 {rejDist}\n" +
                    $"  行進方向沒朝著事故點 → 淘汰 {rejHeading}\n" +
                    $"  不是對向（方向沒跟故障車相反）→ 淘汰 {rejDirection}\n" +
                    $"  不在同一條路的走廊內（容許 {sameRoadLateralTolerance:F0}m）→ 淘汰 {rejCorridor}\n" +
                    $"  節點上已經有車 → 淘汰 {rejOccupied}\n" +
                    $"  沿路網走不到事故點 → 淘汰 {rejRoute}" +
                    (routeFailDetail.Count > 0
                        ? $"\n     ↳ {string.Join(" ／ ", routeFailDetail)}" +
                          $"\n     ↳ 目前設定：抵達半徑 {seedRouteArriveRadius:F0}m、步數上限 {seedRouteMaxSteps}、背離容許 {seedRouteDivergeTolerance:F0}m"
                        : "") + "\n" +
                    $"  ★ 剩下的那一關淘汰最多的就是要放寬的地方</color>", this);
            return null;
        }
        return candidates[Random.Range(0, candidates.Count)];
    }

    /// <summary>這一批要放幾台。沒擲過(0)就退回 Inspector 的固定值。</summary>
    private int CurrentBatchSize => currentBatchSize > 0 ? currentBatchSize : Mathf.Max(1, maxOncomingPerBatch);

    /// <summary>這一批之後要空窗幾秒。同上。</summary>
    private float CurrentGapDuration => currentGapDuration > 0f ? currentGapDuration : oncomingGapDuration;

    /// <summary>擲出下一批的節奏。隨機化關掉時就固定用 Inspector 的兩個值。</summary>
    private void RollOncomingRhythm()
    {
        if (!randomizeOncomingRhythm)
        {
            currentBatchSize = Mathf.Max(1, maxOncomingPerBatch);
            currentGapDuration = oncomingGapDuration;
            return;
        }

        int lo = Mathf.Max(1, Mathf.Min(oncomingBatchRange.x, oncomingBatchRange.y));
        int hi = Mathf.Max(lo, Mathf.Max(oncomingBatchRange.x, oncomingBatchRange.y));
        currentBatchSize = Random.Range(lo, hi + 1);      // int 版是右開區間，所以 +1

        float gLo = Mathf.Min(oncomingGapRange.x, oncomingGapRange.y);
        float gHi = Mathf.Max(oncomingGapRange.x, oncomingGapRange.y);
        currentGapDuration = Random.Range(gLo, gHi);
    }

    private void UpdateOncomingRhythm()
    {
        if (!enableOncomingControl || spawnPoint == null) return;

        // 這一幀範圍內有幾台對向車
        int inZone = 0;
        Collider[] hits = Physics.OverlapSphere(spawnPoint.position, oncomingLimitRadius, aiCarLayer);
        foreach (Collider c in hits)
        {
            if (c == null) continue;
            CarNavigation gray = c.GetComponentInParent<CarNavigation>();
            ReverseThreatAICar white = gray == null ? c.GetComponentInParent<ReverseThreatAICar>() : null;
            Transform tf = gray != null ? gray.transform : (white != null ? white.transform : null);
            if (tf == null) continue;
            if (white is AmbulanceAI) continue;                 // 救護車不受這套節奏限制
            if (gray != null && gray.isStaticObstacle) continue;
            if (white != null && white.isStaticParked) continue;

            // 🎯 彎道(手動指定)時改看「這台車的目標節點在不在清單裡」,
            //    因為直線走廊在彎道上會歪掉,幾何判定不可靠。
            if (UsingManualOncomingNodes)
            {
                if (!IsCarOnManualOncomingRoute(gray, white)) continue;
            }
            else
            {
                if (!IsOncoming(tf.position, tf.forward)) continue;
            }

            inZone++;
            oncomingSeenThisBatch.Add(tf.root.gameObject);      // 這批放行過誰(用 root 去重)
        }

        if (oncomingGateOpen)
        {
            // 這批已經放夠了 → 關閘,後續的車自己改道或淡出
            if (oncomingSeenThisBatch.Count >= CurrentBatchSize) CloseOncomingGate();
        }
        else
        {
            // 閘門關著:等這批車全部開出範圍,才開始算空窗
            if (inZone > 0)
            {
                oncomingCooldownUntil = Time.time + CurrentGapDuration;   // 還有車 → 持續往後推
            }
            else if (Time.time >= oncomingCooldownUntil)
            {
                float used = CurrentGapDuration;
                RollOncomingRhythm();   // 🎲 下一批重新擲，玩家背不起來節奏
                OpenOncomingGate();
                UnityEngine.Debug.Log($"<color=lime>🚦 空窗 {used:F1} 秒結束,放行下一批對向車" +
                                      $"（下一批 {CurrentBatchSize} 台、空窗 {CurrentGapDuration:F1} 秒）</color>");
            }
        }
    }

    /// <summary>
    /// 顯示事故提示並播放警示音。初次提示與重複提示共用，避免兩邊邏輯走鐘。
    /// </summary>
    private void ShowAccidentNotice(string logReason)
    {
        if (string.IsNullOrEmpty(noticeMessage)) return;
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.ShowMessage(noticeMessage, noticeColor, noticeDisplaySeconds);

        PlayNoticeSound();
        nextNoticeTime = Time.time + noticeRepeatSeconds;
        UnityEngine.Debug.Log($"<color=orange>⚠ [故障車] {logReason}</color>");
    }

    /// <summary>
    /// 警示音用 2D 播放：這是給玩家的系統提示，不是場景中的聲音，
    /// 不該有方位感也不該隨距離衰減（AccidentDirector 本身可能離玩家很遠）。
    /// </summary>
    private void PlayNoticeSound()
    {
        if (noticeSound == null) return;

        if (noticeAudioSource == null)
        {
            noticeAudioSource = gameObject.AddComponent<AudioSource>();
            noticeAudioSource.playOnAwake = false;
            noticeAudioSource.loop = false;
            noticeAudioSource.spatialBlend = 0f;
            noticeAudioSource.dopplerLevel = 0f;
        }
        noticeAudioSource.PlayOneShot(noticeSound, noticeVolume);
    }

    void EndAccidentEvent()
    {
        // 玩家已經通過並開遠，這時結算反應最準確
        if (hazardRegistered && HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.UnregisterHazard(spawnPoint);
        hazardRegistered = false;
        nextNoticeTime = -1f;

        if (spawnedAccident != null) Destroy(spawnedAccident);
        if (roadBlockObject != null) roadBlockObject.SetActive(false);
        IsAccidentActive = false;

        AllowAIBypassNow = false;   // 🚗 事故結束,繞行旗標歸位
        ActiveNoSpawnRadius = 0f;   // 🚫 解除禁止生成區
        NoSpawnNodesGray.Clear();
        NoSpawnNodesWhite.Clear();

        // 🚦 對向節奏收工:把自己加的封鎖節點移除,清單還原成 Inspector 的原始內容
        OpenOncomingGate();
        BlockedNodes = new System.Collections.Generic.List<WaypointNode>(blockedNodesSetup);
        BlockedPathNodes = new System.Collections.Generic.List<TrafficPathNode>(blockedPathNodesSetup);
        if (DangerEventDirector.Instance != null) DangerEventDirector.Instance.EndLongEvent();
        // 原本這裡有將剩餘車速調回 10f 的邏輯，因為前面已經把尷尬車移除了，這裡留著做安全保護
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, clearRadius, aiCarLayer);
        foreach (var hit in hitColliders)
        {
            ReverseThreatAICar aiCar = hit.GetComponentInParent<ReverseThreatAICar>();
            if (aiCar != null) aiCar.moveSpeed = 10f;
        }

        // 🔁 可重複:進入冷卻,冷卻後重新武裝;否則永久 Done
        if (repeatable)
        {
            currentState = EventState.Cooldown;
            StartCoroutine(RearmRoutine());
            UnityEngine.Debug.Log($"🏁 現場清理完成,{rearmCooldown} 秒後此點重新待命。");
        }
        else
        {
            currentState = EventState.Done;
            UnityEngine.Debug.Log("🏁 玩家已遠離，現場完全清理(一次性,不再觸發)。");
        }
    }

    // 🔁 重新武裝:冷卻後把狀態清乾淨,回到 Ready 可再次觸發
    System.Collections.IEnumerator RearmRoutine()
    {
        yield return new WaitForSeconds(rearmCooldown);
        spawnedAccident = null;
        playerTransform = null;
        currentState = EventState.Ready;
        UnityEngine.Debug.Log($"🔁 [{gameObject.name}] 故障車點已重新武裝,可再次觸發。");
    }

    private void OnDrawGizmosSelected()
    {
        // 🚦 對向車流的偵測範圍
        //    橘色長條 = 判定「同一條路」的走廊(寬 = Same Road Lateral Tolerance × 2)
        //    半透明球 = Oncoming Limit Radius
        //    藍色箭頭 = 事故點朝向,對向 = 與它相反的方向
        if (enableOncomingControl && spawnPoint != null)
        {
            Gizmos.matrix = Matrix4x4.identity;

            if (UsingManualOncomingNodes)
            {
                // 🎯 手動指定:把清單裡的節點畫成球+折線,一眼看出實際封鎖了哪一段路
                Gizmos.color = new Color(1f, 0.5f, 0f, 1f);
                Vector3 prev = Vector3.zero; bool hasPrev = false;
                if (oncomingNodesOverrideGray != null)
                    foreach (WaypointNode n in oncomingNodesOverrideGray)
                    {
                        if (n == null) continue;
                        Gizmos.DrawSphere(n.transform.position + Vector3.up * 0.6f, 0.8f);
                        if (hasPrev) Gizmos.DrawLine(prev, n.transform.position + Vector3.up * 0.6f);
                        prev = n.transform.position + Vector3.up * 0.6f; hasPrev = true;
                    }

                Gizmos.color = new Color(1f, 0.85f, 0f, 1f);
                hasPrev = false;
                if (oncomingNodesOverrideWhite != null)
                    foreach (TrafficPathNode n in oncomingNodesOverrideWhite)
                    {
                        if (n == null) continue;
                        Gizmos.DrawSphere(n.transform.position + Vector3.up * 1.2f, 0.8f);
                        if (hasPrev) Gizmos.DrawLine(prev, n.transform.position + Vector3.up * 1.2f);
                        prev = n.transform.position + Vector3.up * 1.2f; hasPrev = true;
                    }

                // 從事故點拉一條線到第一個指定節點,確認方向沒拉反
                if (oncomingNodesOverrideGray != null && oncomingNodesOverrideGray.Count > 0
                    && oncomingNodesOverrideGray[0] != null)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawLine(spawnPoint.position + Vector3.up,
                                    oncomingNodesOverrideGray[0].transform.position + Vector3.up * 0.6f);
                }
            }
            else
            {
                // 直路:自動偵測用的直線走廊
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.85f);
                Gizmos.matrix = Matrix4x4.TRS(spawnPoint.position, spawnPoint.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero,
                    new Vector3(sameRoadLateralTolerance * 2f, 2f, oncomingLimitRadius * 2f));
                Gizmos.matrix = Matrix4x4.identity;

                Gizmos.color = new Color(1f, 0.5f, 0f, 0.25f);
                Gizmos.DrawWireSphere(spawnPoint.position, oncomingLimitRadius);
            }

            // 事故點朝向(藍) / 對向來車方向(紅)
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(spawnPoint.position + Vector3.up, spawnPoint.forward * 8f);
            Gizmos.color = Color.red;
            Gizmos.DrawRay(spawnPoint.position + Vector3.up * 1.2f, -spawnPoint.forward * 8f);
        }

        // 🚫 禁止生成區(紫色)
        if (spawnPoint != null)
        {
            Gizmos.matrix = Matrix4x4.identity;

            if (UsingNoSpawnRoute)
            {
                // 路線模式:把走訪到的節點標成紫色方塊,一眼看出涵蓋到哪
                Gizmos.color = new Color(0.7f, 0.2f, 1f, 1f);
                WaypointNode sg = RouteStartGray, eg = RouteEndGray;
                TrafficPathNode sw = RouteStartWhite, ew = RouteEndWhite;

                foreach (WaypointNode n in CollectRouteGray(sg, eg))
                    if (n != null) Gizmos.DrawCube(n.transform.position + Vector3.up * 1.6f, Vector3.one * 1.4f);
                foreach (TrafficPathNode n in CollectRouteWhite(sw, ew))
                    if (n != null) Gizmos.DrawCube(n.transform.position + Vector3.up * 2.2f, Vector3.one * 1.4f);

                // 頭(綠) / 尾(紅) 標記,確認自動推算抓對了車道
                Gizmos.color = Color.green;
                if (sg != null) Gizmos.DrawSphere(sg.transform.position + Vector3.up * 3f, 1.6f);
                if (sw != null) Gizmos.DrawSphere(sw.transform.position + Vector3.up * 3.6f, 1.6f);
                Gizmos.color = Color.red;
                if (eg != null) Gizmos.DrawSphere(eg.transform.position + Vector3.up * 3f, 1.6f);
                if (ew != null) Gizmos.DrawSphere(ew.transform.position + Vector3.up * 3.6f, 1.6f);

                // 路線模式下半徑不撐大,照設定值畫
                Gizmos.color = new Color(0.7f, 0.2f, 1f, 0.35f);
                Gizmos.DrawWireSphere(spawnPoint.position, noSpawnRadius);
            }
            else
            {
                float shownRadius = Mathf.Max(noSpawnRadius,
                    Vector3.Distance(transform.position, spawnPoint.position) + 20f);
                Gizmos.color = new Color(0.7f, 0.2f, 1f, 0.9f);
                Gizmos.DrawWireSphere(spawnPoint.position, shownRadius);
            }

            // 觸發盒 → 故障車 的連線
            Gizmos.color = new Color(0.7f, 0.2f, 1f, 1f);
            Gizmos.DrawLine(transform.position + Vector3.up * 0.5f, spawnPoint.position + Vector3.up * 0.5f);
        }

        // 1. 🟥 畫出原本的精確長方體封路紅框
        if (boxCenter != null)
        {
            Gizmos.color = Color.red;
            Gizmos.matrix = Matrix4x4.TRS(boxCenter.position, boxCenter.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, boxSize);
        }

        // 2. 🟡 【新增】畫出方案 B 銷毀半路車的大球體範圍
        // 以 AccidentDirector 自身位置為球心，clearRadius 為半徑
        Gizmos.matrix = Matrix4x4.identity; // 重設矩陣，確保球體不會受到 boxCenter 的旋轉影響

        // 畫出球體的線框（黃色）
        // 🧹 走廊清車的實際範圍：觸發盒 → 故障車 之間的膠囊。
        //    畫成兩端球體 + 連線，調 clearCorridorHalfWidth 時可以直接目視對照路寬。
        if (spawnPoint != null)
        {
            Gizmos.color = new Color(1f, 0.92f, 0.016f, 0.25f);
            Gizmos.DrawSphere(transform.position, clearCorridorHalfWidth);
            Gizmos.DrawSphere(spawnPoint.position, clearCorridorHalfWidth);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, clearCorridorHalfWidth);
            Gizmos.DrawWireSphere(spawnPoint.position, clearCorridorHalfWidth);
            Gizmos.DrawLine(transform.position, spawnPoint.position);

            // 玩家保護半徑畫在觸發盒上當參考（實際是以玩家為中心，執行時才知道位置）
            if (clearPlayerSafeRadius > 0f)
            {
                Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
                Gizmos.DrawWireSphere(transform.position, clearPlayerSafeRadius);
            }
        }
    }
}