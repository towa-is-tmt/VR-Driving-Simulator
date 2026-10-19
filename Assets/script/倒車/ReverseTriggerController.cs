using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class ReverseTriggerController : MonoBehaviour
{
    [Header("🎯 這個觸發盒的專屬演員 (建議用 Tools 自動綁定)")]
    [Tooltip("有指定 → 只從名單挑，不再靠距離與角度猜，也不會挑到對向車道的車。留空 → 退回舊的動態搜尋(40m 內、玩家前方 ±60°)。名單裡的車全都在冷卻或演出中時，會自動退回動態搜尋，事件不會消失。")]
    public List<ReverseThreatAICar> reverseAICars = new List<ReverseThreatAICar>();

    [Header("📏 距離與視野限制")]
    public float maxTriggerDistance = 40f;
    // 舊行為:有指定專屬演員就完全跳過方位篩選(「盒子的位置就是意圖」)。
    // 但一個盒子的範圍裡如果站了兩台側切車,挑到的可能是玩家背後那台或對向那台 ——
    // 戲照演,玩家卻完全沒看到,一次觸發就這樣浪費掉。
    // 打開這個開關之後,名單裡的車一樣要通過「在玩家前方 + 車頭與玩家同向」兩道篩選。
    [Tooltip("即使有指定專屬演員(Reverse AI Cars),也要求它在玩家前方、且車頭與玩家同向。\n" +
             "關掉＝完全信任名單，回到舊行為")]
    public bool boundActorsMustBeAhead = true;
    // 原本只有 dot > 0.1(±84 度的大錐形)且完全沒有橫向檢查,
    // 所以會挑到「側前方 84 度、40 公尺外、隔壁街」的車去演急煞 ——
    // 玩家完全看不到事件發生在哪,只聽到聲音。
    [Tooltip("★ 目標車相對玩家的橫向容許距離(m)。超過視為隔壁車道,不挑它。單一車道建議 2.0~3.0")]
    public float laneTolerance = 2.5f;
    [Range(0f, 1f)]
    [Tooltip("★ 目標必須在玩家前方多少角度內。0.1≈±84 度太寬,建議 0.5(±60 度)以上")]
    public float frontDotThreshold = 0.5f;
    public bool onlyTriggerInFrontOfPlayer = true;

    [Header("🔍 雷達偵測圖層設定")]
    public LayerMask aiCarLayer; // 🚀 1. 修正：務必在 Inspector 上勾選你 AI 車的 Layer (例如 AICar)！

    [Header("🎲 危機事件隨機權重 (三個加起來不需要是 1)")]
    [Tooltip("執行無預警倒車的權重")]
    public float reverseWeight = 1f;
    [Tooltip("執行惡意急煞的權重")]
    public float brakeWeight = 1f;
    [Tooltip("執行路口鬼探頭/路邊衝出的權重")]
    public float rushOutWeight = 1f;

    [Header("🆕 側邊切入動態補位設定")]
    public GameObject aiCarPrefab;
    public Transform parkSpawnPoint;
    public TrafficPathNode initialWaypoint;

    [Header("⏳ 機關冷卻設定")]
    [Tooltip("觸發一次後，要過幾秒才可以再次被玩家觸發（防止同一次轉彎重複判定）")]
    public float cooldownTime = 3f;

    [Header("⏳ 新車延遲生成設定")]
    [Tooltip("舊車衝出後，過幾秒才在原地生成新車補位")]
    public float spawnDelayTime = 7f;

    [Header("⏱ 側邊衝出的發動時機 (TTC)")]
    // 原本是玩家一碰到觸發區就立刻衝出，發動點固定在同一個「位置」，
    // 所以玩家開得慢就有大把時間反應，完全沒有壓迫感。
    // 改成看 TTC（還剩幾秒會撞上），不管車速快慢，衝出瞬間都是「剛好來不及從容反應」。
    [Tooltip("等玩家距離衝出車只剩這麼多秒才發動。數字越小越驚險")]
    public float rushTriggerTTC = 1.8f;
    // ⚠️ 固定 TTC 有一個隱藏的不公平：TTC 是線性的，但煞停需求隨速度平方成長。
    //    1.8 秒在 30 km/h 是「反應 1 秒 + 煞 0.7 秒」剛好夠；到 70 km/h 光殺速度就要 3~4 秒。
    //    時間給得一樣多，要做的事卻是三倍 —— 高速會變成閃不掉的懲罰，不是危險感知訓練。
    //    所以門檻改成隨逼近速度加碼，讓各速度的間距都停在「像誤判、但拚一下閃得掉」。
    [Tooltip("TTC 門檻隨逼近速度加碼的係數（秒 / 每 m/s）。實際門檻 = 基準 + 逼近速度 × 這個值。\n" +
             "0.05 → 70 km/h(19.4 m/s) 時側切門檻從 1.8 變成約 2.8 秒。\n" +
             "預告門檻會加上同一個量，維持預告到衝出之間固定的提前量。\n" +
             "0 = 關掉，回到固定 TTC（舊行為）")]
    public float ttcSpeedScalePerMps = 0.05f;

    // 🚦 間距接受（gap acceptance）：真實駕駛路邊起步切入車流的臨界間距約 5~7 秒，
    //    低於這個值就是等。所以「看到車開很快還硬切出來」在真實世界不會發生 ——
    //    真實世界會發生的是「誤判」：吃掉一個 2.5~3 秒的間距，不是 1.8 秒配 70 km/h。
    //    超過這個逼近速度就不切，維持方向燈閃著、車停在原地等玩家過去。
    //    放棄本身也是看得懂的結果：玩家會看到「他本來要出來，看到我這個速度就停住了」。
    //
    //    ⚠️ 這條只是上限，不能拿來取代上面的 ttcSpeedScalePerMps ——
    //       只有放棄而沒有加碼的話，高速玩家等於永遠遇不到危險，超速反而被獎勵。
    [Tooltip("逼近速度超過這個值（公尺/秒）就放棄本次切出：車不動、方向燈續閃、等玩家過去。\n" +
             "18 ≈ 65 km/h。真實駕駛看到車開這麼快不會切出來，硬切玩家也閃不掉。\n" +
             "0 = 關掉，任何速度都照切（舊行為）")]
    public float gapRefuseClosingSpeed = 18f;
    [Tooltip("側切的「預告」門檻，要比上面的發動 TTC 大。玩家逼近到這個時間時，"+
        "側切車先打方向燈、前輪轉出，讓玩家有機會提早察覺。設 0 = 不預告，維持舊行為")]
    public float sideCutTelegraphTTC = 3.5f;
    // 固定值的話玩家跑三五次就把時序背起來了 —— 之後測到的是肌肉記憶，不是判讀線索的能力，
    // 而「能不能讀懂路邊停放車要出來」正是這個訓練項目要練的東西。
    // 每次觸發在 基準 ± 抖動 之間重擲，玩家就必須真的盯著方向燈與前輪。
    //
    // ⚠️ 比賽展示時建議設 0：demo 的節奏要可預期，不要現場開出一個極端值。
    //    要拿分數互相比較時也該設 0 或收窄，否則不同玩家拿到的難度不一樣。
    [Tooltip("預告時機的隨機抖動（秒）。實際門檻 = 上面的基準 ± 這個值。\n" +
             "0 = 固定不隨機（比賽展示 / 要比較分數時建議設 0）")]
    public float sideCutTelegraphTTCJitter = 0.7f;
    [Tooltip("預告開始後，至少要經過這麼久才允許衝出。玩家踩到觸發盒時距離已經很近的話，TTC 一開始就低於預告門檻，"+
        "預告和衝出會擠在同一瞬間等於沒有預告；這個下限保證玩家一定有這麼多秒可以反應。0 = 不保證")]
    public float telegraphMinLeadSeconds = 1.2f;
    [Tooltip("急煞與倒車的 TTC 門檻。刻意比側切短 ——\n側切是「旁邊冒出一台車」，需要時間反應；\n急煞和倒車是「前車突然變化」，真實世界能反應的時間本來就更短")]
    public float frontHazardTriggerTTC = 1.4f;
    [Tooltip("保底距離：玩家幾乎停住時 TTC 會無限大，距離小於這個值就直接發動")]
    public float rushMinTriggerDistance = 8f;
    // ⚠️ 上面那條保底排在所有判斷的最前面，而且原本沒有任何方位或速度把關，於是：
    //    ① 它沒有 TTC 那條的「逼近速度 > 0.5」檢查 —— 玩家開過去之後 TTC 路徑會自己失效
    //       （逼近速度變負），這條不會。dist 是中心到中心的平面距離，路邊停放車離車道中心
    //       只有 3~4 公尺，所以「貼著它開過去」必定讓 dist 掉到 8 以下 → 車從側後方冒出來。
    //    ② 它排在預告提前量的 continue 之前，會直接繞過 telegraphMinLeadSeconds。
    //    ③ 它也排在 abandonDistanceGrowth 之前，改道判定救不了 —— 貼身經過時最近距離才 3~4m。
    //    保底本來要解的是「玩家幾乎停住 → TTC 無限大 → 事件卡住不發生」，那是低速情境；
    //    高速時 TTC 那條早在 25~35 公尺外就發動了，根本用不到保底。所以把它限縮回低速就好，
    //    不會少掉任何原本該發動的場合。
    [Tooltip("保底距離只在逼近速度低於這個值時才算數（公尺/秒，5 ≈ 18 km/h）。\n" +
             "保底是為了解決「玩家快停住時 TTC 無限大、事件卡住」，那是低速情境；\n" +
             "高速時 TTC 門檻早就發動了，不該再讓保底在玩家已經並排／開過去時補一刀。\n" +
             "設很大 = 恢復舊行為（任何速度都吃保底）")]
    public float rushMinTriggerMaxClosingSpeed = 5f;
    [Tooltip("演員車必須落在玩家車頭方向的這個角度內才發動（車頭方向的內積）。\n" +
             "1 = 正前方、0 = 正側面、負值 = 已經在玩家後方。0.5 ≈ 前方 60 度內。\n" +
             "並排或已經超車過去才發動的話，玩家看到的是「從側後方冒出來」，閃不掉也學不到東西。\n" +
             "設 -1 = 不做方位把關（舊行為）")]
    public float actorAheadDotThreshold = 0.5f;
    [Tooltip("等待上限。超過就直接發動，避免玩家停在原地導致事件卡住不發生")]
    public float rushMaxWaitSeconds = 6f;
    // 觸發盒跟演員車之間隔著路口時，玩家可能踩到盒子之後就從別的路口轉走了。
    // 舊版沒有「玩家跑掉」的概念，等到逾時還是會照常發動 —— 側切車對著空氣切出來，
    // 而且那台車已經預告到一半(燈亮著、車頭斜出來)，之後就一直維持那個怪姿勢。
    // 這裡改成：離最近點又拉開這麼多公尺，就判定玩家不來了，取消整場戲並把車收回原位。
    [Tooltip("玩家離「本次最靠近的距離」又拉開超過這麼多公尺，就視為他改道走了，\n" +
             "取消這次事件並讓預告中的車收回方向燈與探出的車身。0 = 不做放棄判定")]
    public float abandonDistanceGrowth = 12f;

    [Header("🎬 演員車保底（沒車經過就自己生一台）")]
    // 急煞/倒車要的是「玩家正前方、同車道、行駛中的白車」。車流是隨機的，
    // 那個位置常常剛好沒車 —— 於是整個事件就靜靜地不發生，觸發盒等於白做。
    // 這裡在挑不到演員時，於指定路點生一台白車來演，保證玩家經過就一定遇得到。
    //
    // ⚠️ 生成點要選在「玩家前方、同向、看不到的地方」（例如彎道後、路口對面），
    //    否則玩家會看到車憑空出現。
    [Tooltip("挑不到演員車時，自己生一台來演。關掉＝維持舊行為（沒車就不發生）")]
    public bool guaranteeActor = false;
    [Tooltip("要生成的白車 prefab（必須掛 ReverseThreatAICar）")]
    public GameObject guaranteedActorPrefab;
    [Tooltip("生成位置的路點。可以填多個，會挑「玩家看不到而且夠遠」的那一個")]
    public List<TrafficPathNode> guaranteedActorNodes = new List<TrafficPathNode>();

    // 沒有遮蔽物的路段要怎麼藏？用「距離 + 視野」代替實體遮蔽：
    //   ① 夠遠 —— 100 公尺外的車只有幾個像素，出現的那一幀很難被察覺
    //   ② 不在畫面內 —— 玩家的視野只有約 60 度，側前方、彎道後、路口另一側都在外面
    // 兩個條件都不滿足就寧可不生（少演一次），也不要讓玩家看到車憑空出現。
    [Tooltip("生成點離玩家至少要這麼遠（公尺）。太近一定會被看到，建議 80 以上")]
    public float guaranteedActorMinDistance = 90f;
    [Tooltip("生成點必須在玩家攝影機視野外。找不到符合的位置就這次不生")]
    public bool guaranteedActorRequireOffscreen = true;
    [Tooltip("判定視野時往內縮的比例，避免剛好卡在畫面邊緣。0.1 = 邊緣 10% 也算看得到")]
    [Range(0f, 0.4f)] public float guaranteedActorScreenMargin = 0.12f;
    // 不需要「暖機時間」欄位：新生的車速度是 0，而 requireMovingActor 那套本來就是
    // 「演員車停著就繼續等」，它自然會等到車跑起來才發動。

    [Header("🚶 急煞的原因：行人穿越")]
    // 舊版急煞是「車在空曠路上突然煞停」，沒有理由 —— 玩家會歸類成「遊戲在考我」，
    // 學不到東西。給它一個看得見的原因之後，因果就完整了：
    //   前車煞車 → 喔前面有人在過馬路 → 我剛剛應該把視線放遠、看穿前車
    // 「看穿前車」正是防禦駕駛最核心、也最帶得走的一個習慣。
    //
    // 順序很重要：行人先踏出來，隔一個駕駛反應時間，前車才煞車。
    // 反過來的話因果就顛倒了，玩家會覺得是行人被車嚇到才走出來。
    [Tooltip("急煞事件要搭配的行人。留空＝維持舊行為（沒有原因的急煞）")]
    public CrossingPedestrian brakeCausePedestrian;
    [Tooltip("行人跑到車前之後，隔多久前車才煞車。這是模擬前車駕駛的反應時間，建議 0.6~1.2 秒")]
    public float pedestrianToBrakeDelay = 0.8f;

    [Tooltip("等行人真的跑到車的正前方才煞車。關掉＝她一踏出人行道就煞（那時人還在路側，看起來像無故煞停）")]
    public bool waitUntilPedestrianInFront = true;
    [Tooltip("算「在車前方」的走廊半寬（公尺）。約一個車道")]
    public float pedestrianLaneHalfWidth = 2.5f;
    [Tooltip("等不到行人進入車前時的保底秒數，逾時就照常煞車，避免事件卡住不發生")]
    public float pedestrianInFrontTimeout = 4f;

    [Header("🤝 互動式側切")]
    // 真實世界要切出來的駕駛是會看你的：你減速他就走，你不減速他多半會等。
    // 固定腳本「時間到就一定用同一種兇法切出」學不到東西 —— 玩家會歸因成運氣。
    // 這裡把「玩家有沒有讀到預告線索」變成結局的分歧點：
    //   有讓 → 平順匯入，安全通過（玩家做對事要有對應的結果）
    //   沒讓 → 維持原本的兇狠切法，該有的危險照樣發生
    [Tooltip("從預告開始到發動為止，玩家車速掉了幾 km/h 才算「有讀到線索並讓行」。\n" +
             "設太小會把自然的速度浮動也算成有讓，建議 6~12")]
    public float yieldSpeedDropKmH = 8f;
    [Tooltip("印出每次側切發動時量到的速度變化與判定結果，用來確認門檻設得對不對")]
    public bool logYieldDecision = true;

    [Header("🎬 淨空檢查")]
    // 玩家與演員車之間如果卡著別的車，這場戲等於演給空氣看：
    // 側切車會被自己的前方偵測擋住、就算切出來也是切在那台車後面，
    // 急煞和倒車則是被前車擋住視線，玩家根本看不到。
    // 所以發動前要先確認舞台是淨空的。
    [Tooltip("關掉的話不檢查，維持「時間到就一定發動」")]
    public bool requireClearPathToPlayer = true;
    [Tooltip("檢查走廊的半寬。約一個車道寬，太寬會把隔壁車道的車也算成擋路")]
    public float clearCheckHalfWidth = 1.6f;
    [Tooltip("玩家正前方至少要有這麼多公尺的淨空，側切車才塞得進去。\n車長約 4.5m，前後再各留一點反應空間 → 建議 8")]
    public float requiredFrontGap = 8f;
    [Tooltip("往前掃描多遠來尋找前車。超過這個距離就視為前方無車")]
    public float frontScanRange = 40f;
    [Tooltip("印出每次判定時量到的前方間距，用來確認門檻設得對不對")]
    public bool logClearCheck = true;
    [Tooltip("等待期間路徑一直沒淨空的話，逾時後是否還是硬發動")]
    public bool fireEvenIfBlocked = false;

    [Tooltip("【只在號誌路口附近的觸發盒打開】黃燈期間也暫停計時。\n" +
             "黃燈時車還在走（isWaitingAtTrafficLight 還是 false），但下一刻就要轉紅停下 ——\n" +
             "那時發動事件，行人會對著一排正在停的車衝出來。\n" +
             "⚠️ 時相是全域的，沒有號誌的路段請不要打開")]
    public bool avoidYellowPhase = false;
    // ⚠️ 這兩個開關檢查的是「全域」號誌時相 —— 全場只有一顆 TrafficSystem_Brain，
    //    所有路口同步跳，程式無從得知「這個觸發盒旁邊那個路口」現在是什麼燈。
    //    所以：
    //      ✅ 觸發盒設在號誌路口附近 → 打開，全域時相就等於這個路口的時相
    //      ❌ 觸發盒設在沒有號誌的路段 → 保持關閉，否則會被不相干的號誌拖著等
    //    預設關閉，需要的盒子再自己打開。
    [Tooltip("【只在號誌路口附近的觸發盒打開】綠燈剩餘時間不夠跑完整套行人流程就等下一輪。\n" +
             "只看「現在是不是黃燈」不夠 —— 綠燈剩 2 秒時發動，等行人跑到車前早就轉紅了。\n" +
             "⚠️ 全場只有一顆號誌大腦、時相是全域的，所以沒有號誌的路段請不要打開")]
    public bool requireEnoughGreenTime = false;
    [Tooltip("估算所需綠燈時間時額外加的安全餘裕（秒）")]
    public float greenTimeSafetyMargin = 2f;

    private bool isCooldown = false;
    private float lastGreenWaitLogTime = -999f;   // 「等紅燈中」訊息的節流
    private float lastGapRefuseLogTime = -999f;   // 「間距不可接受，這次不切」訊息的節流
    private TrafficSystem_Brain cachedBrain;

    /// <summary>
    /// 現在是不是黃燈時相（含左轉黃燈）。
    /// 全場只有一顆 Brain，時相是全域的，所以查一次就代表整個路網的狀態。
    /// </summary>
    private bool IsAnyYellowNow()
    {
        if (cachedBrain == null) cachedBrain = FindFirstObjectByType<TrafficSystem_Brain>();
        if (cachedBrain == null) return false;

        return cachedBrain.normalPhase == TrafficSystem_Brain.NormalPhase.A_to_C_Yellow
            || cachedBrain.normalPhase == TrafficSystem_Brain.NormalPhase.B_to_D_Yellow
            || cachedBrain.specialPhase == TrafficSystem_Brain.SpecialPhase.A_to_C_Yellow
            || cachedBrain.specialPhase == TrafficSystem_Brain.SpecialPhase.B_to_D_Yellow
            || cachedBrain.specialPhase == TrafficSystem_Brain.SpecialPhase.B_to_D_LeftYellow;
    }

    /// <summary>
    /// 綠燈剩下的時間夠不夠跑完整套行人流程。
    ///
    /// 只檢查「現在是不是黃燈」不夠：行人要走人行道、站著張望、再跑進車道，
    /// 整套約 5 秒。綠燈只剩 2 秒時發動，等她跑到車前早就轉紅、整排車都停了。
    /// 所以要問的是「還剩多久」，不是「現在是什麼燈」。
    ///
    /// 沒有 Brain（沒號誌的路段）就一律回 true，不擋。
    /// </summary>
    private bool HasEnoughGreenTime()
    {
        if (!requireEnoughGreenTime) return true;
        if (cachedBrain == null) cachedBrain = FindFirstObjectByType<TrafficSystem_Brain>();
        if (cachedBrain == null) return true;
        if (!cachedBrain.IsNormalGreenNow) return false;   // 不是綠燈就是不夠

        float need = pedestrianToBrakeDelay + greenTimeSafetyMargin;
        if (brakeCausePedestrian != null)
            need += brakeCausePedestrian.EstimatedStepOutDelay
                  + brakeCausePedestrian.EstimatedRunIntoLaneTime;

        return cachedBrain.NormalGreenRemaining >= need;
    }

    private void OnTriggerEnter(Collider other)
    {
        // 進盒子卻沒動靜時，第一個要確認的就是「到底有沒有進來、是不是還在冷卻」——
        // 這兩件事原本都不會留下任何痕跡
        // 📦 這一行要印出「是哪顆 Collider 進來的」——
        //    下面第一行就把 other.transform 當成玩家位置，所有 TTC、距離、方位都從它量。
        //    它跟 PlayerHazardScanner 用的 Rigidbody transform 不保證是同一顆，
        //    原點對不上的話兩套系統對「玩家在哪」的認知就不一致，而且不會有任何錯誤訊息。
        if (logClearCheck && other.CompareTag("Player"))
        {
            Rigidbody probeRb = other.GetComponentInParent<Rigidbody>();
            string rbName = probeRb != null ? probeRb.name : "找不到";
            float originGap = probeRb != null
                ? Vector3.Distance(other.transform.position, probeRb.transform.position)
                : -1f;
            UnityEngine.Debug.Log($"<color=white>📦 [觸發盒] {name} 被玩家觸發" +
                                  $"{(isCooldown ? "，但還在冷卻中 → 這次不算" : "")}" +
                                  $"｜量距離用的 Collider 是「{other.name}」" +
                                  $"｜Rigidbody 在「{rbName}」" +
                                  $"｜兩者原點相距 {(originGap < 0f ? "?" : originGap.ToString("F2"))} m</color>", this);
        }

        // 確保冷卻時間過了
        if (other.CompareTag("Player") && !isCooldown)
        {
            Transform playerTransform = other.transform;
            ReverseThreatAICar bestTargetCar = null;
            float closestDistance = float.MaxValue;

            // 🚀 修正：OverlapSphere 加入 aiCarLayer 參數，精準過濾非車輛的物件！
            Collider[] surroundingCars = Physics.OverlapSphere(transform.position, maxTriggerDistance, aiCarLayer);

            List<ReverseThreatAICar> detectedAICars = new List<ReverseThreatAICar>();
            foreach (var col in surroundingCars)
            {
                ReverseThreatAICar aiScript = col.GetComponentInParent<ReverseThreatAICar>();
                if (aiScript != null && !detectedAICars.Contains(aiScript))
                {
                    detectedAICars.Add(aiScript);
                }
            }

            // 🎯 綁定優先：這個觸發盒若在 Inspector 指定了專屬演員(Reverse AI Cars)，就先從名單挑。
            //    盒子擺在哪裡本身就是「要演哪一台」的意圖 —— 純動態搜尋只問「在不在玩家前方 ±60°」，
            //    兩個盒子只距離 18m、兩台車分屬中央分隔島兩側時，會挑到對向車道 25m 外的那台。
            //    名單裡的車全都在忙(冷卻中/演出中)才退回動態搜尋，事件不會整個消失。
            List<ReverseThreatAICar> boundActors = new List<ReverseThreatAICar>();
            foreach (var bound in reverseAICars)
                if (bound != null && (bound.CanTakeAction() || bound.IsIdleParked)) boundActors.Add(bound);

            bool useBoundActors = boundActors.Count > 0;
            List<ReverseThreatAICar> candidatePool = useBoundActors ? boundActors : detectedAICars;

            // 1. 篩選出人在前方且最靠近的演員車
            foreach (ReverseThreatAICar aiCar in candidatePool)
            {
                if (aiCar == null) continue;

                // 用 IsIdleParked 而不是 isStaticParked：
                // 後者只問「是不是路邊演員車」，會把「剛演完還在冷卻」的也一起放行；
                // 前者多問一句「現在有沒有在忙」，冷卻中的演員車就選不上了。
                if (!aiCar.CanTakeAction() && !aiCar.IsIdleParked) continue;

                float dist = Vector3.Distance(transform.position, aiCar.transform.position);
                if (dist > maxTriggerDistance) continue;

                // 🎯 綁定的車也要在玩家前方且同向 —— 一個盒子涵蓋兩台側切車時，
                //    沒有這道篩選就可能挑到玩家背後或對向那台，玩家根本互動不到。
                if (useBoundActors && boundActorsMustBeAhead)
                {
                    Vector3 toBound = aiCar.transform.position - playerTransform.position;
                    toBound.y = 0f;
                    if (toBound.sqrMagnitude > 0.01f &&
                        Vector3.Dot(playerTransform.forward, toBound.normalized) < frontDotThreshold)
                    {
                        if (logClearCheck)
                            UnityEngine.Debug.Log($"<color=grey>🎯 [選角] 跳過 {aiCar.name}：不在玩家前方</color>");
                        continue;
                    }

                    if (Vector3.Dot(playerTransform.forward, aiCar.transform.forward) <= 0f)
                    {
                        if (logClearCheck)
                            UnityEngine.Debug.Log($"<color=grey>🎯 [選角] 跳過 {aiCar.name}：車頭與玩家反向(對向車道)</color>");
                        continue;
                    }
                }

                if (!useBoundActors && onlyTriggerInFrontOfPlayer)
                {
                    Vector3 toAI = aiCar.transform.position - playerTransform.position;
                    toAI.y = 0;

                    if (Vector3.Dot(playerTransform.forward, toAI.normalized) < frontDotThreshold) continue;

                    // 🚧 靜停演員車額外要求「車頭跟玩家同向」。
                    //    它們跳過下面的橫向檢查(路邊停放車本來就不在玩家車道上)，
                    //    少了這道保險就會挑到中央分隔島對面、車頭朝反方向的車 ——
                    //    玩家看到的是對向車道莫名其妙有車切出來。
                    if (aiCar.isStaticParked &&
                        Vector3.Dot(playerTransform.forward, aiCar.transform.forward) <= 0f) continue;

                    // ⚠️ 橫向檢查只對「行駛中的車」有意義。
                    //    側切演員是路邊停放車,它本來就停在車道外側(離玩家車道 3~6m),
                    //    拿「同車道」的標準去篩它會一台都選不上,側切等於整個失效。
                    //    停放車只要在前方就算數,橫向由它自己的觸發盒位置決定。
                    if (!aiCar.isStaticParked)
                    {
                        // 橫向用「AI 車自己的車道軸」量,不是玩家的 ——
                        // 玩家變換車道時車身是斜的,用玩家的 right 量出來的值會失真。
                        if (Mathf.Abs(Vector3.Dot(-toAI, aiCar.transform.right)) > laneTolerance) continue;
                    }
                }

                if (dist < closestDistance)
                {
                    closestDistance = dist;
                    bestTargetCar = aiCar;
                }
            }

            // 挑不到演員車的話，原本整段就這樣靜靜結束 —— 玩家踩了盒子什麼都沒發生，
            // 而且完全沒有線索。急煞盒特別容易踩到這個坑：它要的是「行駛中的車」，
            // 玩家前方剛好沒車就一台都挑不到。
            // 🎬 挑不到演員 → 自己生一台。車流是隨機的，玩家前方常常剛好沒車，
            //    整個事件就靜靜地不發生，觸發盒等於白做。
            if (bestTargetCar == null && guaranteeActor
                && guaranteedActorPrefab != null
                && guaranteedActorNodes != null && guaranteedActorNodes.Count > 0)
            {
                bestTargetCar = SpawnGuaranteedActor();
                if (bestTargetCar != null && logClearCheck)
                    UnityEngine.Debug.Log($"<color=lime>🎬 [觸發盒] {name} 前方沒有可用的車，" +
                                          $"已生成演員車 {bestTargetCar.name}</color>", this);
            }

            if (bestTargetCar == null && logClearCheck)
            {
                UnityEngine.Debug.LogWarning(
                    $"<color=orange>📦 [觸發盒] {name} 挑不到演員車，這次不觸發。\n" +
                    $"  範圍 {maxTriggerDistance:F0}m 內偵測到 {detectedAICars.Count} 台車" +
                    $"（其中綁定名單可用 {boundActors.Count} 台，本次用{(useBoundActors ? "名單" : "動態搜尋")}）\n" +
                    $"  急煞/倒車需要「行駛中」的車，側切需要路邊靜停車 —— " +
                    $"如果這是急煞盒，請確認 Reverse AI Cars 已清空，且玩家前方真的有車在跑</color>", this);
            }

            // 2. 找到演員後，依據權重池擲骰子！
            if (bestTargetCar != null)
            {
                float finalRushWeight = rushOutWeight;

                if (!bestTargetCar.IsWaitingAtTrafficLight() && !bestTargetCar.isStaticParked)
                {
                    finalRushWeight = 0f;
                }

                float totalWeight = reverseWeight + brakeWeight + finalRushWeight;

                // 🎲 先擲骰決定要演哪一齣
                int chosenAction;   // 0=倒車 1=急煞 2=側邊衝出
                string chosenName;
                if (bestTargetCar.isStaticParked)
                {
                    // 🪨 路邊靜停車只會演「側邊切入」:它的倒車/急煞都被 CanTakeAction() 擋掉,
                    //    擲到那兩齣等於白白消耗一次觸發(玩家開過去什麼都沒發生),直接指定側切。
                    chosenAction = 2; chosenName = "側邊衝出";
                }
                else if (totalWeight <= 0f)
                {
                    // 舊版這裡硬指定成「急煞」,等於無視 Inspector 的權重設定 ——
                    // 你把三個權重都設 0(或只留側切)時,對「行駛中的車」side-cut 權重會被歸零,
                    // 總和變 0 就掉進這裡,結果照樣演急煞。權重 0 應該就是「這次不演」。
                    if (logClearCheck)
                        UnityEngine.Debug.Log($"<color=grey>🎲 [觸發盒] {bestTargetCar.name} 三項權重合計為 0，這次不觸發</color>");
                    return;
                }
                else
                {
                    float diceRoll = UnityEngine.Random.Range(0f, totalWeight);
                    if (diceRoll < reverseWeight) { chosenAction = 0; chosenName = "倒車"; }
                    else if (diceRoll < reverseWeight + brakeWeight) { chosenAction = 1; chosenName = "急煞"; }
                    else { chosenAction = 2; chosenName = "側邊衝出"; }
                }

                // 🎬 問導演:沒核准就當作沒踩到(注意放在自身冷卻之前,否決不消耗冷卻)
                if (DangerEventDirector.Instance != null && !DangerEventDirector.Instance.RequestEvent(chosenName))
                {
                    // 導演有全域的節奏控制（兩個危險因子之間要隔多久等），被否決也是靜默的
                    if (logClearCheck)
                        UnityEngine.Debug.Log($"<color=grey>🎬 [觸發盒] {name} 擲到「{chosenName}」，" +
                                              $"但被 DangerEventDirector 否決（全域節奏控制），這次不演</color>", this);
                    return;
                }

                StartCoroutine(TriggerCooldownRoutine());

                // 三種危險因子都改成等玩家逼近到指定 TTC 才發動。
                // 碰到觸發區只是「進入待命」，實際發動時機由玩家的接近速度決定 ——
                // 這樣不管玩家開快開慢，事件發生的瞬間都一樣緊迫。
                switch (chosenAction)
                {
                    case 0:
                        StartCoroutine(TriggerWhenPlayerCloseRoutine(
                            bestTargetCar, playerTransform, frontHazardTriggerTTC,
                            (c, _) => c.TriggerReverseFromZone(), "隨機倒車", "orange",
                            requireMovingActor: true));
                        break;

                    case 1:
                        StartCoroutine(TriggerWhenPlayerCloseRoutine(
                            bestTargetCar, playerTransform, frontHazardTriggerTTC,
                            (c, _) => StartCoroutine(BrakeWithPedestrianCause(c, playerTransform)), "隨機急煞", "red",
                            requireMovingActor: true));
                        break;

                    case 2:
                        StartCoroutine(TriggerWhenPlayerCloseRoutine(
                            bestTargetCar, playerTransform, rushTriggerTTC,
                            (c, yielded) => { SpawnReplacementIfNeeded(c); c.TriggerIntersectionRush(true, yielded); },
                            "隨機衝出", "cyan", telegraphSideCut: true));
                        break;
                }
            }
        }
    }
    /// <summary>
    /// 等玩家真的逼近到危險距離才發動事件。倒車 / 急煞 / 側邊衝出共用同一套。
    ///
    /// 判定用 TTC（Time To Collision）：剩餘距離 ÷ 玩家朝前的速度。
    /// 這樣不管玩家開 20 還是 60 km/h，事件發生的瞬間對他來說都一樣緊迫 ——
    /// 危險感來自「時間不夠」，不是來自車速本身。
    /// </summary>
    System.Collections.IEnumerator TriggerWhenPlayerCloseRoutine(
        ReverseThreatAICar car, Transform player, float ttcThreshold,
        System.Action<ReverseThreatAICar, bool> onTrigger, string label, string colorTag,
        bool telegraphSideCut = false, bool requireMovingActor = false)
    {
        Rigidbody playerRb = player != null ? player.GetComponentInParent<Rigidbody>() : null;
        float waited = 0f;
        float telegraphStartTime = -1f;   // 🔶 預告從何時開始（-1 = 還沒開始）
        string reason = $"等待逾時 {rushMaxWaitSeconds:F0}s";

        // 🤝 互動式側切：預告開始那一刻的車速當基準，跟發動當下比對就知道玩家有沒有讓。
        float speedAtTelegraph = -1f;
        float lastApproachSpeed = 0f;
        float minDistSeen = float.MaxValue;   // 🚪 本次玩家最靠近過的距離，用來判定他是不是改道走了

        // 🎲 這一次的預告門檻：每次觸發重擲，玩家背不起來時序。
        //    夾在發動門檻之上，否則抖動抖到比 rushTriggerTTC 還小就等於整個預告失效。
        float telegraphTTCThisRun = sideCutTelegraphTTC;
        if (telegraphTTCThisRun > 0f && sideCutTelegraphTTCJitter > 0.001f)
        {
            telegraphTTCThisRun = Mathf.Max(
                ttcThreshold + 0.1f,
                sideCutTelegraphTTC + UnityEngine.Random.Range(-sideCutTelegraphTTCJitter, sideCutTelegraphTTCJitter));
        }

        while (waited < rushMaxWaitSeconds)
        {
            if (car == null) yield break;   // 車被回收了，整個事件取消
            if (player == null) break;

            Vector3 flat = car.transform.position - player.position;
            flat.y = 0f;
            float dist = flat.magnitude;

            // 真正朝「演員車」逼近的速度分量 —— TTC 與下面的保底把關都要用這個。
            // 舊版直接拿玩家自己的前進速度當接近速度，等於假設他一路朝這台車開；
            // 玩家在路口轉走之後那個值照樣很大，TTC 一樣會掉到門檻以下 → 對著空氣演一場。
            // 🔺 這段原本算在迴圈後半，為了給 rushMinTriggerDistance 那條保底做速度把關而移上來。
            float closingSpeed = (playerRb != null && dist > 0.01f)
                ? Vector3.Dot(playerRb.velocity, flat / dist)
                : 0f;

            // 演員車還在不在玩家車頭前方 —— 並排（≈0）或已被超過（負值）就不該再發動。
            bool actorAhead = IsActorAhead(flat, dist, player, playerRb);

            // 🚦 急煞/倒車需要「正在行駛」的演員車：停著的車演急煞沒有意義
            //    （玩家看到的是一台停著的車突然亮煞車燈），而且行人這時穿過一排靜止的車
            //    也教不到東西。停著就繼續等 —— 車陣一動起來就會正常發動，
            //    不像之前直接取消整場戲，那會變成「行人從來不出現」。
            bool actorStalled = requireMovingActor && car.CurrentHealthSpeed < 0.5f;

            // 🚦 演員車正在等紅燈 → 這段等待不計入逾時。
            //    紅燈時整排車都是停的，行人衝出來一點危險都沒有，事件等於白演。
            //    但也不該因此取消 —— 等綠燈車流起步再演才是對的時機，
            //    所以把時間凍住，讓它一直等到號誌放行。
            // 黃燈也要一起等：那一刻車還在走，isWaitingAtTrafficLight 仍是 false，
            // 計時照跑 —— 等發動時號誌剛好轉紅，整排車停下，
            // 行人就對著一排靜止的車衝出來，事件一樣白演。
            bool waitingForGreen = actorStalled && car.IsWaitingAtTrafficLight();
            if (avoidYellowPhase && !waitingForGreen && IsAnyYellowNow()) waitingForGreen = true;

            // 綠燈剩太短也要等下一輪：整套行人流程約 5 秒，
            // 剩 2 秒時發動，等她跑到車前早就轉紅、整排車都停了。
            if (!waitingForGreen && requireEnoughGreenTime && !HasEnoughGreenTime())
                waitingForGreen = true;
            if (waitingForGreen)
            {
                if (logClearCheck && Time.time - lastGreenWaitLogTime > 2f)
                {
                    lastGreenWaitLogTime = Time.time;
                    UnityEngine.Debug.Log($"<color=grey>🚦 [{label}] " +
                        $"{(car.IsWaitingAtTrafficLight() ? $"{car.name} 正在等紅燈" : "目前是黃燈時相")}，" +
                        $"暫停計時，等綠燈車流起步再演</color>", this);
                }
                yield return null;
                continue;   // 不累加 waited
            }

            // 🛡 保底距離：只在「低速逼近、而且車還在前方」時才算數。
            //    這兩個把關是後補的，原因寫在 rushMinTriggerDistance 的宣告處 ——
            //    簡單說：保底要解的是低速卡住，高速根本走不到這裡，
            //    沒有把關時它會在玩家並排／已經開過去的瞬間補一刀。
            if (!actorStalled && dist <= rushMinTriggerDistance
                && actorAhead
                && closingSpeed <= rushMinTriggerMaxClosingSpeed)
            {
                reason = $"已逼近 {dist:F1}m（逼近 {closingSpeed * 3.6f:F0} km/h）";
                break;
            }

            // 🚪 玩家改道判定：離「本次最靠近的距離」又拉開太多 → 他從別的路口走了。
            //    取消整場戲，並把預告到一半的車收回原位（燈熄、輪子回正、車身退回）。
            if (dist < minDistSeen) minDistSeen = dist;
            else if (abandonDistanceGrowth > 0f && dist > minDistSeen + abandonDistanceGrowth)
            {
                UnityEngine.Debug.Log($"<color=grey>🚪 [{label}] 玩家已離開（最近 {minDistSeen:F1}m → 現在 {dist:F1}m），" +
                                      $"取消本次事件並收回預告</color>");
                car.EndSideCutInTelegraph();
                yield break;
            }

            // 玩家自己的車速 —— 給「有沒有減速讓行」的判定用。
            //
            // ⚠️ 舊版是 Dot(velocity, player.forward)，那是個隱藏的地雷：
            //    playerTransform 取的是「進入觸發盒的那顆 Collider 自己的 Transform」(OnTriggerEnter
            //    的 other.transform)，不是你指定的玩家車。玩家車階層 RRRR→SSS→playerCAR→vehicle
            //    裡，Collider 掛在哪一層、那一層的 Z 軸朝哪，跟車子實際往哪開是兩回事。
            //    forward 偏掉的話這個內積會趨近 0 →
            //      ① speedAtTelegraph ≈ 0 → 讓行判定的 log 被 `speedAtTelegraph > 0f` 擋掉，完全不印
            //      ② playerYielded 永遠 false → 玩家再怎麼讓都拿不到平順匯入
            //    而 closingSpeed 用的是「玩家→演員車」的方向、沒碰 forward，所以預告與衝出照常運作 ——
            //    「用 forward 的全壞、不用的全好」就是當初查出這條的線索。
            //
            //    量「玩家車速」本來就不需要方向，用水平速度的長度就好，任何階層都不會錯。
            //    去掉 y 是因為上下坡的垂直分量不算行進速度。
            float ownSpeed = 0f;
            if (playerRb != null)
            {
                Vector3 v = playerRb.velocity;
                v.y = 0f;
                ownSpeed = v.magnitude;
            }
            lastApproachSpeed = ownSpeed;   // 🤝 供發動時的「有沒有讓」判定

            // closingSpeed 已在迴圈開頭算好（保底把關要用），這裡直接沿用。

            if (closingSpeed > 0.5f)
            {
                float ttc = dist / closingSpeed;

                // ⏱ 門檻隨逼近速度加碼 —— 理由寫在 ttcSpeedScalePerMps 的宣告處。
                //    預告門檻加上「同一個量」，這樣預告到衝出之間的秒數不會被壓縮：
                //    兩邊都加同一個常數，差值不變，玩家拿到的提前量在各速度下一致。
                float ttcSpeedBonus       = closingSpeed * ttcSpeedScalePerMps;
                float effectiveTriggerTTC = ttcThreshold + ttcSpeedBonus;
                float effectiveTelegraphTTC = telegraphTTCThisRun > 0f
                    ? telegraphTTCThisRun + ttcSpeedBonus
                    : 0f;

                // 🔶 側切預告：比發動門檻更早一步，讓車先打燈、前輪轉出。
                //    只是提早給線索，發動時機仍由下面的門檻決定。
                if (telegraphSideCut && effectiveTelegraphTTC > 0f && ttc <= effectiveTelegraphTTC
                    && telegraphStartTime < 0f)
                {
                    car.BeginSideCutInTelegraph(player);
                    telegraphStartTime = Time.time;
                    speedAtTelegraph   = ownSpeed;   // 讓行判定比的是玩家自己的車速，不是逼近速度
                }
                if (ttc <= effectiveTriggerTTC && !actorStalled)
                {
                    // 🚦 間距接受：玩家逼近得太快 → 真實駕駛不會切出來，這次就不切。
                    //    車維持不動、方向燈續閃（預告不收），等玩家過去；
                    //    過去之後由發動前的方位把關 IsActorAhead 取消整場戲並收回預告。
                    //    ⚠️ 這段不累加 waited —— 累加的話 rushMaxWaitSeconds 逾時會硬發動，
                    //       等於白擋。高逼近速度本來就只會持續幾秒，不會卡住。
                    if (gapRefuseClosingSpeed > 0f && closingSpeed > gapRefuseClosingSpeed)
                    {
                        if (logClearCheck && Time.time - lastGapRefuseLogTime > 1f)
                        {
                            lastGapRefuseLogTime = Time.time;
                            UnityEngine.Debug.Log($"<color=grey>🚦 [{label}] 玩家逼近 {closingSpeed * 3.6f:F0} km/h " +
                                $"> 門檻 {gapRefuseClosingSpeed * 3.6f:F0} km/h，間距不可接受 → 這次不切，等他過去</color>", this);
                        }
                        yield return null;
                        continue;   // 不累加 waited
                    }

                    // 🔶 預告時間還不夠 → 再等一下。寧可晚一點切出，也不要玩家還沒看到燈就衝出來。
                    if (telegraphStartTime >= 0f && telegraphMinLeadSeconds > 0f
                        && Time.time - telegraphStartTime < telegraphMinLeadSeconds)
                    {
                        waited += Time.deltaTime;
                        yield return null;
                        continue;
                    }

                    // 時機到了，但舞台被擋住的話繼續等 —— 中間那台車可能開走
                    if (requireClearPathToPlayer && !IsPathToPlayerClear(car, player, dist))
                    {
                        waited += Time.deltaTime;
                        yield return null;
                        continue;
                    }

                    reason = $"TTC {ttc:F1}s ≤ 門檻 {effectiveTriggerTTC:F1}s" +
                             $"（基準 {ttcThreshold:F1} + 速度加碼 {ttcSpeedBonus:F1}）" +
                             $"，距離 {dist:F1}m，逼近 {closingSpeed * 3.6f:F0} km/h";
                    break;
                }
            }

            waited += Time.deltaTime;
            yield return null;
        }

        if (car == null) yield break;

        // 等到逾時演員車還是停著（整段路都在塞車）→ 這次不演。
        // 硬演的話就是「停著的車突然亮煞車燈」，玩家只會覺得莫名其妙。
        if (requireMovingActor && car.CurrentHealthSpeed < 0.5f)
        {
            if (logClearCheck)
                UnityEngine.Debug.Log($"<color=grey>🚦 [{label}] {car.name} 到逾時都還是靜止" +
                                      $"（速度 {car.CurrentHealthSpeed:F1}），本次取消</color>", this);
            yield break;
        }

        // 🛡 發動前的最後一道方位把關：演員車已經在玩家後方（或並排）→ 取消，不要從側後方冒出來。
        //    保底距離那條已經擋過一次，但 rushMaxWaitSeconds 逾時是另一個入口 ——
        //    它同樣不看方位，玩家開過去之後計時器一到照樣發動。同一個根因、兩個入口，一起補。
        //    MeasureFrontGap 幫不上忙：它掃的是玩家「前方」的盒子，而且明確排除演員車本身，
        //    車在玩家後面時它回傳無限大，檢查照樣通過。
        if (player != null && !IsActorAhead(car.transform.position - player.position, -1f, player, playerRb))
        {
            UnityEngine.Debug.Log($"<color=grey>🚪 [{label}] 發動前檢查：{car.name} 已不在玩家前方" +
                                  $"（玩家已經開過去了），本次取消並收回預告</color>", this);
            car.EndSideCutInTelegraph();
            yield break;
        }

        // 逾時但前方仍塞不下：預設放棄，不要演一場玩家看不到的戲
        if (requireClearPathToPlayer && !fireEvenIfBlocked && player != null)
        {
            float finalGap = MeasureFrontGap(car, player);
            if (finalGap < requiredFrontGap)
            {
                string g = float.IsInfinity(finalGap) ? "無限" : $"{finalGap:F1}m";
                UnityEngine.Debug.Log($"<color=grey>🎬 [{label}] 玩家前方只有 {g} 淨空（需 {requiredFrontGap}m），本次取消</color>");
                car.EndSideCutInTelegraph();   // 🔶 事件取消 → 收掉方向燈與轉出的前輪
                yield break;
            }
        }

        // 🤝 玩家有沒有讀到預告並讓行 —— 決定這次是「平順匯入」還是「兇狠切出」。
        //    沒進過預告階段（speedAtTelegraph < 0，例如急煞/倒車，或側切一觸發就已經很近）
        //    就沒有基準可比，一律當成沒讓，維持原本行為。
        float dropKmH = speedAtTelegraph > 0f ? (speedAtTelegraph - lastApproachSpeed) * 3.6f : 0f;
        bool playerYielded = speedAtTelegraph > 0f && dropKmH >= yieldSpeedDropKmH;

        if (telegraphSideCut && logYieldDecision && speedAtTelegraph > 0f)
        {
            float leadUsed = telegraphStartTime >= 0f ? Time.time - telegraphStartTime : 0f;
            UnityEngine.Debug.Log(playerYielded
                ? $"<color=lime>🤝 [{label}] 玩家減速 {dropKmH:F0} km/h（門檻 {yieldSpeedDropKmH:F0}）→ 判定有讓，平順匯入" +
                  $" | 預告門檻 {telegraphTTCThisRun:F1}s、實際給了 {leadUsed:F1}s</color>"
                : $"<color=orange>🤝 [{label}] 玩家只減速 {dropKmH:F0} km/h（門檻 {yieldSpeedDropKmH:F0}）→ 判定沒讓，兇狠切出" +
                  $" | 預告門檻 {telegraphTTCThisRun:F1}s、實際給了 {leadUsed:F1}s</color>");
        }

        onTrigger(car, playerYielded);
        UnityEngine.Debug.Log($"<color={colorTag}>🎲 [{label}] {car.gameObject.name} 發動！觸發原因：{reason}</color>");
    }

    /// <summary>
    /// 玩家「正前方」到演員車那個距離為止，有沒有其他車輛擋著。
    ///
    /// 刻意沿玩家的行進方向掃，而不是沿「玩家→演員車」的連線：
    ///   側切車最後會切進玩家的車道（rushAimAtPlayerLane），落點在玩家正前方，
    ///   所以該確認的是那段路是不是空的。演員車停在路邊、方位偏側前方，
    ///   沿連線掃的話盒子是斜的，會漏掉正前方那台真正擋路的車。
    ///
    /// 急煞與倒車同理 —— 演員應該是你正在跟的那台車，中間卡別的車就看不到事件。
    /// </summary>
    bool IsPathToPlayerClear(ReverseThreatAICar car, Transform player, float dist)
    {
        if (player == null || car == null) return true;

        float gap = MeasureFrontGap(car, player);
        bool ok = gap >= requiredFrontGap;

        if (logClearCheck && !ok)
        {
            string g = float.IsInfinity(gap) ? "無限（前方無車）" : $"{gap:F1}m";
            UnityEngine.Debug.Log($"<color=grey>🎬 玩家前方只有 {g} 淨空（需要 {requiredFrontGap}m），側切車塞不進去 → 繼續等</color>");
        }
        return ok;
    }

    /// <summary>
    /// 量玩家正前方到最近一台車的實際間距，沒有車就回 Infinity。
    ///
    /// 為什麼不是「玩家到演員車之間整段淨空」：
    ///   那個距離會隨演員車遠近浮動，判準不一致，而且跟「切得進來嗎」沒有直接關係。
    ///   真正該問的是「玩家正前方有沒有足夠塞下一台車的空間」——
    ///   沒有的話側切車根本切不到玩家前面，那場戲就白演了。
    /// </summary>
    /// <summary>
    /// 演員車是不是還落在玩家車頭前方的錐形範圍內。
    /// 並排（內積 ≈ 0）或已被超過（負值）就不該發動 —— 那是「從側後方冒出來」，
    /// 玩家既閃不掉也讀不到因果，學不到東西。
    /// </summary>
    /// <param name="flat">玩家 → 演員車的向量，y 會在這裡被歸零，不必先處理</param>
    /// <param name="dist">已經算好的水平距離；傳負值 = 由本函式自己量</param>
    /// <param name="playerRb">有傳且車在動 → 用「實際行進方向」當前方；留空 = 退回用 player.forward</param>
    bool IsActorAhead(Vector3 flat, float dist, Transform player, Rigidbody playerRb = null)
    {
        if (actorAheadDotThreshold <= -1f) return true;   // 把關關閉 = 舊行為
        if (player == null) return true;

        flat.y = 0f;
        if (dist < 0f) dist = flat.magnitude;
        if (dist <= 0.01f) return true;                   // 幾乎重疊，方位沒有意義

        // 「前方」優先用實際行進方向，不用 player.forward ——
        // 理由同 ownSpeed 那段：playerTransform 是進盒子的那顆 Collider，它的 Z 軸不保證是車頭方向。
        // 車頭方向錯的話，正前方的演員車算出來的內積會落在 0 附近而不是 0.95，
        // 於是門檻 0.5 擋掉一切、要降到 0 才動 —— 那等於只剩「不要在正後方」一條保護。
        // 速度方向沒有階層問題：車往哪開就是哪個方向。
        Vector3 fwd = Vector3.zero;
        if (playerRb != null)
        {
            Vector3 v = playerRb.velocity;
            v.y = 0f;
            // 太慢時速度方向會被雜訊主導（停等時 velocity 幾乎是隨機的小抖動），
            // 那種情況沒有可靠的行進方向可用，退回 forward 至少是穩定值。
            if (v.sqrMagnitude > 1f) fwd = v;   // 1 m/s 以上才採用
        }
        if (fwd.sqrMagnitude < 0.0001f) fwd = player.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return true;

        return Vector3.Dot(flat / dist, fwd.normalized) >= actorAheadDotThreshold;
    }

    float MeasureFrontGap(ReverseThreatAICar actor, Transform player)
    {
        Vector3 center = player.position + Vector3.up * 0.5f + player.forward * (frontScanRange * 0.5f);
        Vector3 halfExtents = new Vector3(clearCheckHalfWidth, 0.9f, frontScanRange * 0.5f);

        Collider[] hits = Physics.OverlapBox(center, halfExtents, player.rotation, ~0, QueryTriggerInteraction.Ignore);

        float nearest = Mathf.Infinity;
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.transform.IsChildOf(player.root)) continue;                 // 玩家自己
            if (actor != null && h.transform.IsChildOf(actor.transform.root)) continue;   // 演員車本身不算擋路

            if (h.GetComponentInParent<ReverseThreatAICar>() == null
                && h.GetComponentInParent<CarNavigation>() == null) continue; // 只算車輛

            float d = Vector3.Distance(player.position, h.bounds.ClosestPoint(player.position));
            if (d < nearest) nearest = d;
        }
        return nearest;
    }

    /// <summary>
    /// 🚶 讓急煞有個看得見的原因：行人先踏出人行道，隔一個駕駛反應時間，前車才煞車。
    ///
    /// 順序不能顛倒 —— 車先煞、人再走出來的話，玩家讀到的因果會反過來
    /// （變成行人被車嚇到才走），教學意義就沒了。
    /// 沒指定行人就直接煞車，維持舊行為。
    /// </summary>
    /// <summary>
    /// 🚪 玩家是不是已經「改道走了」，不再往這台演員車的方向前進。
    ///
    /// 只在玩家「真的在動」時才判定：轉去別條路一定是移動中，方向可靠。
    /// 速度太慢（停等、龜速）時方向會被雜訊主導，這種時候一律回 false（＝還沒改道），
    /// 寧可晚一點收也不要把「停在原地等」誤判成改道。
    /// 用實際速度方向而不是 forward —— 玩家車階層的 collider Z 軸不保證是車頭方向。
    /// </summary>
    private bool PlayerTurnedAwayFromActor(ReverseThreatAICar car, Transform player, Rigidbody playerRb)
    {
        if (car == null || player == null || playerRb == null) return false;

        Vector3 v = playerRb.velocity; v.y = 0f;
        if (v.sqrMagnitude < 1f) return false;          // < 1 m/s：方向不可靠，不判改道

        Vector3 flat = car.transform.position - player.position; flat.y = 0f;
        float d = flat.magnitude;
        if (d < 0.5f) return false;                      // 幾乎重疊，方位沒有意義

        // 演員車還在不在玩家實際行進方向的前方錐形內（同 actorAheadDotThreshold，預設 ±60 度）。
        // 玩家在路口轉走 → 行進方向偏離指向演員車的方向 → 內積掉到門檻以下 → 判定改道。
        return Vector3.Dot(flat / d, v.normalized) < actorAheadDotThreshold;
    }

    System.Collections.IEnumerator BrakeWithPedestrianCause(ReverseThreatAICar car, Transform player)
    {
        // 🚪 玩家改道就收：踩到盒子之後在路口轉去別條路的話，這台演員車已經跟玩家無關 ——
        //    行人再衝出來也是演給空氣看。所以整個流程每一段都盯著玩家：
        //      發動煞車「之前」改道 → 收回行人、直接不煞（沒 RegisterHazard 就不會有評語）
        //      發動煞車「之後」改道 → 收回行人 + CancelHazard 撤掉評語（當沒發生過）
        //    量方向用 Rigidbody，不用進盒子那顆 collider 的 forward。
        Rigidbody playerRb = player != null ? player.GetComponentInParent<Rigidbody>() : null;

        // 🚦 前車正在等紅燈（或根本沒在動）→ 整個事件取消。
        //    停著的車「急煞」本來就沒有意義，而行人這時走出來也只是穿過一排靜止的車，
        //    玩家學不到任何東西。這是最直接的擋法：不演，而不是想辦法讓它看起來合理。
        //
        //    為什麼會走到這裡：TTC 判定之外還有一條「距離小於 rushMinTriggerDistance 就發動」
        //    的保底，玩家慢慢滑到停等車陣後方就會踩到那一條。
        //    「演員車必須在動」的把關已經移到 TriggerWhenPlayerCloseRoutine 的
        //    requireMovingActor —— 那裡是「停著就繼續等」，等車陣動起來才發動；
        //    在這裡擋等於整場取消，行人就永遠不會出現。這支只負責因果順序。
        // 沒指定行人就直接煞車（舊行為）—— 但這個要講出來，否則「行人沒衝出」查不到原因。
        // 最常見的狀況：發動的是另一個沒設行人的觸發盒。
        if (brakeCausePedestrian == null)
        {
            if (logClearCheck)
                UnityEngine.Debug.LogWarning($"<color=orange>🚶 [急煞] {name} 沒有指定 " +
                    $"Brake Cause Pedestrian，這次只有煞車、沒有行人</color>", this);
        }
        else if (brakeCausePedestrian.IsCrossing)
        {
            if (logClearCheck)
                UnityEngine.Debug.LogWarning($"<color=orange>🚶 [急煞] 行人 " +
                    $"{brakeCausePedestrian.name} 上一次的穿越還沒結束，這次不重播</color>", this);
        }

        if (brakeCausePedestrian != null && !brakeCausePedestrian.IsCrossing)
        {
            brakeCausePedestrian.gameObject.SetActive(true);   // 上一次穿越完可能被關掉了

            // 🚶 把演員車傳進去：穿越路徑要相對「那台會煞車的車」算，
            //    才保證行人出現在它的前方。行人的位置寫死在場景裡的話，
            //    演員車是動態挑的，兩者對不起來 —— 行人會出現在車陣後方。
            // 也把車速傳進去：等待期間車還在跑，穿越點要用「屆時的位置」算，
            // 否則設定的前方距離會被車自己吃掉，行人變成在車頭旁邊冒出來。
            brakeCausePedestrian.BeginCrossing(
                car != null ? car.transform : null,
                car != null ? car.CurrentHealthSpeed : 0f);

            // ① 等她真的踏出人行道。
            //    逾時要照行人自己的參數算，不能寫死 —— 之前寫死 3 秒，而流程是
            //    「走人行道 2 秒 + 張望 1.2 秒 = 3.2 秒」，保底比流程還短，
            //    時間一到就直接去煞車了，那時行人根本還在張望。
            float stepOutTimeout = brakeCausePedestrian.EstimatedStepOutDelay + 2f;
            float guard = 0f;
            while (!brakeCausePedestrian.HasSteppedOut && guard < stepOutTimeout)
            {
                // 🚪 還沒踏出人行道玩家就改道 → 直接收回，什麼都沒發生（也還沒登記危險因子）
                if (PlayerTurnedAwayFromActor(car, player, playerRb))
                {
                    if (logClearCheck)
                        UnityEngine.Debug.Log($"<color=grey>🚪 [急煞] 行人還沒踏出，玩家已改道 → 取消穿越、不煞</color>", this);
                    brakeCausePedestrian.CancelCrossing();
                    yield break;
                }
                guard += Time.deltaTime;
                yield return null;
            }

            // ② 再等她真的跑到車的正前方才煞。
            //    「踏出人行道」只代表她開始動了，那時人還在路側 ——
            //    在那個時間點煞車，玩家看到的是「前車無故煞停，旁邊有個人在走」。
            //    要等她確實擋在車前，因果才讀得出來。
            if (waitUntilPedestrianInFront && car != null)
            {
                float inFrontGuard = 0f;
                while (!brakeCausePedestrian.IsInFrontOf(car.transform, pedestrianLaneHalfWidth)
                       && inFrontGuard < pedestrianInFrontTimeout)
                {
                    // 🚪 行人已衝出、正跑向車道，但玩家在這時轉走 → 讓行人消失、不煞
                    //    （還沒 TriggerBrakeFromZone，所以不會有評語）
                    if (PlayerTurnedAwayFromActor(car, player, playerRb))
                    {
                        if (logClearCheck)
                            UnityEngine.Debug.Log($"<color=grey>🚪 [急煞] 行人衝出中，玩家已改道 → 讓行人消失、不煞</color>", this);
                        brakeCausePedestrian.CancelCrossing();
                        yield break;
                    }
                    inFrontGuard += Time.deltaTime;
                    yield return null;
                }

                if (logClearCheck)
                    UnityEngine.Debug.Log(inFrontGuard < pedestrianInFrontTimeout
                        ? $"<color=cyan>🚶 [急煞] 行人已進入 {car.name} 的行進路線，開始煞車</color>"
                        : $"<color=orange>🚶 [急煞] 等了 {pedestrianInFrontTimeout:F1} 秒行人都沒進到車前" +
                          $"（位置沒對上？），還是照常煞車</color>", this);
            }

            // ③ 行人到車前到「前車真的煞」之間的反應時間 —— 這段也盯著玩家
            float brakeDelay = 0f;
            while (brakeDelay < pedestrianToBrakeDelay)
            {
                if (PlayerTurnedAwayFromActor(car, player, playerRb))
                {
                    if (logClearCheck)
                        UnityEngine.Debug.Log($"<color=grey>🚪 [急煞] 煞車前玩家已改道 → 讓行人消失、不煞</color>", this);
                    brakeCausePedestrian.CancelCrossing();
                    yield break;
                }
                brakeDelay += Time.deltaTime;
                yield return null;
            }
        }

        if (car != null) car.TriggerBrakeFromZone();

        // ④ 煞車已發動後的最後一段：玩家在評語跳出來之前才轉走（例如剛好在路口右轉離開）——
        //    收回行人 + CancelHazard 把「前車急煞」評語撤掉，當這件事沒發生過。
        //    評語是在 brakeDuration + brakeHoldSeconds + hazardReactionWindowSeconds 之後才由
        //    UnregisterHazard 顯示，所以在那之前 CancelHazard 就來得及攔下。
        //    沒指定行人（純急煞、舊行為）就不做這段，維持原樣。
        if (car != null && player != null && brakeCausePedestrian != null)
        {
            float watchWindow = car.brakeDuration + car.brakeHoldSeconds + car.hazardReactionWindowSeconds;
            float watched = 0f;
            while (watched < watchWindow)
            {
                if (PlayerTurnedAwayFromActor(car, player, playerRb))
                {
                    if (logClearCheck)
                        UnityEngine.Debug.Log($"<color=grey>🚪 [急煞] 急煞已發動、玩家隨即改道 → 收回行人並撤銷評語（當沒發生）</color>", this);
                    brakeCausePedestrian.CancelCrossing();
                    if (HazardAwarenessTracker.Instance != null)
                        HazardAwarenessTracker.Instance.CancelHazard(car.transform);
                    yield break;
                }
                watched += Time.deltaTime;
                yield return null;
            }
        }
    }

    /// <summary>
    /// 🎬 在指定路點生一台白車來當演員。
    ///
    /// 生成點被別的車佔著就放棄（回 null）——那種情況本來就有車在跑，
    /// 只是不在挑選條件內，硬生會疊車。
    /// </summary>
    /// <summary>
    /// 從候選路點裡挑一個「玩家看不到」的。沒有遮蔽物的路段就靠距離與視野角度：
    /// 100 公尺外的車只有幾個像素，而玩家視野只有約 60 度，側前方與彎道後都在畫面外。
    /// 都不符合就回 null —— 少演一次，也不要讓玩家看到車憑空出現。
    /// </summary>
    TrafficPathNode PickHiddenSpawnNode()
    {
        if (guaranteedActorNodes == null || guaranteedActorNodes.Count == 0) return null;

        Transform player = GameObject.FindGameObjectWithTag("Player")?.transform;
        Camera cam = Camera.main;

        List<TrafficPathNode> ok = new List<TrafficPathNode>();
        string why = "";

        foreach (TrafficPathNode n in guaranteedActorNodes)
        {
            if (n == null) continue;
            if (Physics.CheckSphere(n.transform.position, 3f, aiCarLayer)) { why = "已經有車"; continue; }

            if (player != null)
            {
                float d = Vector3.Distance(player.position, n.transform.position);
                if (d < guaranteedActorMinDistance) { why = $"離玩家只有 {d:F0}m"; continue; }
            }

            if (guaranteedActorRequireOffscreen && cam != null && IsOnScreen(cam, n.transform.position))
            { why = "在玩家畫面內"; continue; }

            ok.Add(n);
        }

        if (ok.Count == 0)
        {
            if (logClearCheck)
                UnityEngine.Debug.Log($"<color=grey>🎬 [觸發盒] {name} 的 {guaranteedActorNodes.Count} 個生成點" +
                    $"目前都不適合（最後一個原因：{why}），這次不補演員 —— " +
                    $"寧可少演一次也不要讓車憑空出現在玩家眼前</color>", this);
            return null;
        }
        return ok[UnityEngine.Random.Range(0, ok.Count)];
    }

    /// <summary>這個世界座標是不是落在玩家畫面裡（邊緣往內縮 screenMargin 當緩衝）</summary>
    bool IsOnScreen(Camera cam, Vector3 worldPos)
    {
        Vector3 vp = cam.WorldToViewportPoint(worldPos);
        if (vp.z <= 0f) return false;               // 在攝影機後方

        float m = guaranteedActorScreenMargin;
        return vp.x > -m && vp.x < 1f + m && vp.y > -m && vp.y < 1f + m;
    }

    ReverseThreatAICar SpawnGuaranteedActor()
    {
        TrafficPathNode guaranteedActorNode = PickHiddenSpawnNode();
        if (guaranteedActorNode == null) return null;

        // 車頭朝下一個路點，否則它會用出生時的朝向開一小段才轉正
        TrafficPathNode next = (guaranteedActorNode.nextChoices != null
                                && guaranteedActorNode.nextChoices.Count > 0)
            // 這支檔案有 using System，裸寫 Random 會跟 System.Random 撞名，一律寫全名
            ? guaranteedActorNode.nextChoices[UnityEngine.Random.Range(0, guaranteedActorNode.nextChoices.Count)]
            : null;
        if (next == null)
        {
            UnityEngine.Debug.LogWarning($"[觸發盒] {name} 的 Guaranteed Actor Node" +
                $"（{guaranteedActorNode.name}）沒有接續路點，生出來的車不會動", this);
            return null;
        }

        Vector3 dir = next.transform.position - guaranteedActorNode.transform.position;
        dir.y = 0f;
        Quaternion rot = dir.sqrMagnitude > 0.01f
            ? Quaternion.LookRotation(dir.normalized, Vector3.up)
            : guaranteedActorNode.transform.rotation;

        GameObject go = Instantiate(guaranteedActorPrefab, guaranteedActorNode.transform.position, rot);
        SpawnGroundSnap.Apply(go);
        go.name = "Actor_Guaranteed_" + Time.time;

        ReverseThreatAICar car = go.GetComponentInParent<ReverseThreatAICar>();
        if (car == null)
        {
            UnityEngine.Debug.LogWarning($"[觸發盒] {name} 的 Guaranteed Actor Prefab" +
                $"（{guaranteedActorPrefab.name}）沒有 ReverseThreatAICar，不能當演員車", this);
            Destroy(go);
            return null;
        }

        car.isStaticParked = false;          // 要會跑，不是路邊停放
        car.currentWaypointNode = next;
        return car;
    }

    /// <summary>側邊切入專用：舊車衝出後，延遲在原地補一台新的靜停車。</summary>
    void SpawnReplacementIfNeeded(ReverseThreatAICar car)
    {
        if (!car.isStaticParked || aiCarPrefab == null) return;

        Vector3 spawnPos = parkSpawnPoint != null ? parkSpawnPoint.position : car.transform.position;
        Quaternion spawnRot = parkSpawnPoint != null ? parkSpawnPoint.rotation : car.transform.rotation;
        StartCoroutine(DelaySpawnNewCarRoutine(spawnPos, spawnRot));
    }


    // 延遲補車的協程
    System.Collections.IEnumerator DelaySpawnNewCarRoutine(Vector3 spawnPos, Quaternion spawnRot)
    {
        UnityEngine.Debug.Log($"⏳ 舊車已衝出，預計於 {spawnDelayTime} 秒後在路邊補位新車...");
        yield return new WaitForSeconds(spawnDelayTime);

        if (aiCarPrefab != null)
        {
            GameObject newCarGo = Instantiate(aiCarPrefab, spawnPos, spawnRot);
            newCarGo.name = "SideCutIn_AI_Replacer_" + Time.time;

            ReverseThreatAICar newAiScript = newCarGo.GetComponentInParent<ReverseThreatAICar>();
            if (newAiScript != null)
            {
                newAiScript.isStaticParked = true;
                newAiScript.currentWaypointNode = initialWaypoint;

                // 💡 動態雷達版其實已經不需要這行，但留著可以讓你在 Inspector 上看見新車名字，方便除錯
                reverseAICars.Add(newAiScript);
                UnityEngine.Debug.Log($"<color=green>🆕 [延遲補位成功] 新車 {newCarGo.name} 已於路邊靜態就緒！</color>");
            }
        }
    }

    System.Collections.IEnumerator TriggerCooldownRoutine()
    {
        isCooldown = true;
        yield return new WaitForSeconds(cooldownTime);
        isCooldown = false;
        UnityEngine.Debug.Log("<color=white>🔄 Trigger 機關冷卻結束。</color>");
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, maxTriggerDistance);
    }
}
