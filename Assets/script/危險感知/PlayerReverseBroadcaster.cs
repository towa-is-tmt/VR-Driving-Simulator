using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

/// <summary>
/// 🔙 玩家倒車位移跟隨
///
/// 掛在玩家車上（有 Rigidbody、且實作 IPlayerVehicleState 的那個物件）。
///
/// 【做什麼】
///    玩家切到 R 檔並實際往後移動時，正後方同車道的 AI 車（白車 ReverseThreatAICar
///    與灰車 CarNavigation）跟著退開同樣的距離。玩家退 0.5m，後車就退 0.5m；
///    玩家掛著 R 檔但腳沒踩、車沒動，後車一格都不會動。
///
/// 【為什麼是位移跟隨，不是速度控制】
///    舊版是廣播器通知一次，AI 車各自跑協程算 targetSpeed 推自己。那個速度是從
///    「離玩家多遠」推出來的，跟玩家實際退了多少無關，而且被 minChainReverseSpeed
///    鉗了下限（1.8 m/s）—— 玩家一掛 R 檔，範圍內的車就自己往後滑，看起來像鬼推車。
///    現在位移由這裡逐幀算好餵過去，AI 車那側純被動，數量關係一定對得上。
///
/// 【為什麼判定集中在玩家端】
///    白車與灰車各自有一套 CheckPlayerReverseImmediate，門檻不一樣、任何一道沒過
///    都是靜默失敗，出問題極難定位。改成玩家端統一決定「誰該跟」，只有一份邏輯。
/// </summary>
public class PlayerReverseBroadcaster : MonoBehaviour
{
    [Header("跟隨名單條件")]
    [Tooltip("後方多少公尺內的 AI 車要跟著退。要涵蓋正常跟車距離，市區約 15~30m")]
    public float broadcastRange = 25f;
    [Tooltip("橫向容許距離。超過視為隔壁車道，不跟。單一車道建議 1.8~2.2，設到 4 會把隔壁車道整排掃進來")]
    public float lateralTolerance = 1.8f;
    [Tooltip("車輛必須在玩家後方至少這麼多公尺（避免抓到並排的車）")]
    public float minBehindDistance = 1f;
    [Tooltip("車頭方向必須與玩家同向。1=完全同向，0.5≈夾角 60 度內。防止對向車被抓進來然後朝你倒過來")]
    public float sameHeadingDot = 0.5f;
    [Tooltip("橫向距離同時用「玩家車身框」與「對方車身框」量，取比較寬鬆的那個。 " +
             "關掉 = 只用玩家車身框（舊行為）：玩家車身一斜，走廊就跟著歪出車道，斜著倒車不會觸發連鎖")]
    public bool measureInBothFrames = true;

    [Header("節奏")]
    [Tooltip("多久重掃一次名單，把後來才靠近的車補進來")]
    public float rescanInterval = 0.5f;
    [Tooltip("距離超過 broadcastRange + 這個緩衝才踢出名單。留緩衝是避免在邊界上反覆進出")]
    public float releaseMargin = 5f;

    [Header("R 檔來源")]
    [Tooltip("留空 = 自動尋找（優先選讀真實檔位的 CarPhysicsController）。\n" +
             "玩家車上可能同時有多個實作 IPlayerVehicleState 的元件，對「倒車」的定義卻不一樣：\n" +
             "  CarPhysicsController → 排檔桿真的在 R（G923／VR 車走這條）\n" +
             "  DriveCar / KeyboardCarController → 按住鍵盤 S 鍵\n" +
             "自動選錯的話，把正確的那顆元件拖進來指定")]
    public MonoBehaviour reverseStateSource;

    [Header("診斷")]
    [Tooltip("只在「加入名單／離開名單」時輸出，不會每幀刷")]
    public bool logBroadcast = true;

    private IPlayerVehicleState vehicleState;

    // 真正在移動的那個 Transform。
    // playerCAR 的結構是「外層 playerCAR → 子物件 vehicle ONLY LOD0（Rigidbody 在這）」，
    // 掛在外層的話 transform.position 永遠不動 → 量到的位移恆為 0 → 後車一格都不動，
    // 而 R 檔偵測卻是正常的（往下找得到 DriveCar），症狀非常難排。
    // 所以位置一律從 Rigidbody 所在的物件量，掛在哪一層都不影響。
    private Transform motion;

    private bool following = false;
    [Header("🔙 要「真的在後退」才跟隨")]
    // vehicleState.IsReversing 的語意是「排檔桿在 R」，不是「車正在往後移動」。
    // 只看它的話，玩家打上 R 檔還沒放煞車、或只是誤打一下，後方整排車就開始退 ——
    // 真實世界不會這樣：後車看到的是倒車燈亮起(那是提醒)，真正要退是等你動了才退。
    [Tooltip("關掉＝維持舊行為：一打 R 檔後車就開始跟著退")]
    public bool requireActualReverseMotion = true;
    [Tooltip("往後的速度超過這個值才算「真的在後退」（公尺/秒）。0.3 約等於慢慢滑動")]
    public float minReverseSpeed = 0.3f;
    [Tooltip("後退停下之後，還維持跟隨狀態多久（秒）。\n" +
             "倒車過程中本來就會停停走走，沒有這個緩衝的話後車會一直進進出出跟隨狀態")]
    public float reverseMotionHoldSeconds = 1.0f;
    private float lastRealReverseTime = -999f;

    private Vector3 lastPlayerPos;
    private float nextScanTime = 0f;

    // 分開存是因為兩邊沒有共同基底類別，硬要抽介面會動到兩個大檔的宣告
    private readonly List<ReverseThreatAICar> whiteFollowers = new List<ReverseThreatAICar>();
    private readonly List<CarNavigation> grayFollowers = new List<CarNavigation>();

    // 已經回報過「為什麼沒被納入」的車，每段 R 檔期間各報一次就好
    private readonly HashSet<GameObject> rejectReported = new HashSet<GameObject>();
    private bool scanSummaryDone = false;

    private void Start()
    {
        // 位置量測基準：Rigidbody 所在的那個物件才是真正會動的
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponentInChildren<Rigidbody>();
        motion = rb != null ? rb.transform : transform;

        ResolveReverseSource();

        lastPlayerPos = motion.position;

        // 開場印一次，掛錯層或選錯 R 檔來源時一眼就看得出來
        UnityEngine.Debug.Log($"<color=orange>🔙 [倒車跟隨] 已啟動｜元件掛在「{name}」｜量位移的物件是「{motion.name}」" +
                      $"｜R 檔來源 {(vehicleState != null ? vehicleState.GetType().Name : "★找不到")}</color>", this);
    }

    /// <summary>
    /// 決定要問「哪一顆元件」玩家有沒有在倒車。
    ///
    /// 這台車上可能同時有好幾顆實作 IPlayerVehicleState 的元件，而它們的定義不一樣：
    ///   CarPhysicsController.IsReversing → 排檔桿真的在 R（G923／VR 車走這條）
    ///   DriveCar.IsReversing             → 按住鍵盤 S 鍵
    /// 隨便拿第一顆（GetComponent 的行為）就會抓到 DriveCar，結果打 R 檔完全沒反應。
    /// 所以這裡明確排序：Inspector 指定 > 讀真實檔位的 > 其他。
    /// </summary>
    private void ResolveReverseSource()
    {
        // 1️⃣ Inspector 手動指定的最優先
        if (reverseStateSource != null)
        {
            vehicleState = reverseStateSource as IPlayerVehicleState;
            if (vehicleState != null) return;

            UnityEngine.Debug.LogWarning($"[倒車跟隨] 指定的「{reverseStateSource.GetType().Name}」沒有實作 " +
                           $"IPlayerVehicleState，改用自動尋找。", this);
        }

        // 2️⃣ 自動尋找：從玩家車本體往下掃（含未啟用的），優先挑讀真實檔位的那顆。
        //
        //    ⚠️ 一樣不能用 root —— 玩家車掛在 RRRR → SSS 底下，用 root 等於
        //       拿整個場景去搜尋介面，可能撈到完全不相干物件上的元件。
        //       先掃玩家車本體，真的找不到才逐層往上放寬。
        var found = motion.GetComponentsInChildren<IPlayerVehicleState>(true);

        if (found.Length == 0 && motion.parent != null)
            found = motion.parent.GetComponentsInChildren<IPlayerVehicleState>(true);

        foreach (var s in found)
            if (s is CarPhysicsController) { vehicleState = s; break; }

        if (vehicleState == null && found.Length > 0) vehicleState = found[0];

        if (vehicleState == null)
        {
            UnityEngine.Debug.LogError("[倒車跟隨] 這台車上找不到任何實作 IPlayerVehicleState 的元件，無法得知是否掛 R 檔。", this);
            return;
        }

        // 有多顆時把清單印出來，選錯了才知道要拖哪一顆進 reverseStateSource
        if (found.Length > 1)
        {
            var names = new List<string>();
            foreach (var s in found) names.Add(s.GetType().Name);
            UnityEngine.Debug.LogWarning($"[倒車跟隨] 這台車上有 {found.Length} 顆倒車狀態元件（{string.Join("、", names)}），" +
                           $"自動選了「{vehicleState.GetType().Name}」。若打 R 檔沒反應，" +
                           $"請把正確的那顆拖進 Inspector 的 Reverse State Source。", this);
        }
    }

    // 用 LateUpdate：所有 Update 與物理位移都結束後才量這一幀的實際位移，量到的才是最終值
    private void LateUpdate()
    {
        if (vehicleState == null) return;

        // 1️⃣ 這一幀玩家沿「自己車尾方向」真正走了多少
        //    ⚠️ 這段搬到 following 判斷「之前」——「要不要開始跟隨」現在也需要它。
        //       順便修掉一個潛在問題：舊版沒跟隨時 lastPlayerPos 是停滯的，
        //       StartFollowing 有歸零才沒出事，等於靠另一行在補。
        Vector3 move = motion.position - lastPlayerPos;
        lastPlayerPos = motion.position;

        float delta = Vector3.Dot(move, -motion.forward);
        if (delta < 0f) delta = 0f;    // 前進或純側移不算，後車不該因此往後退

        // 🔙 「排檔在 R」≠「正在後退」。要兩個同時成立才叫後車跟著退。
        //    停下來的那幾拍靠 reverseMotionHoldSeconds 撐住，倒車本來就會停停走走。
        bool gearReverse = vehicleState.IsReversing;
        float backSpeed = Time.deltaTime > 0.0001f ? delta / Time.deltaTime : 0f;
        if (gearReverse && backSpeed >= minReverseSpeed) lastRealReverseTime = Time.time;

        bool reversing = !requireActualReverseMotion
            ? gearReverse
            : gearReverse && (Time.time - lastRealReverseTime) <= reverseMotionHoldSeconds;

        if (reversing && !following) StartFollowing();
        else if (!reversing && following)
            StopFollowing(gearReverse ? "玩家打著 R 檔但沒有後退" : "玩家切回前進");

        if (!following) return;

        // 2️⃣ 補名單：後來才開到後面的車也要跟上
        if (Time.time >= nextScanTime)
        {
            nextScanTime = Time.time + rescanInterval;
            ScanForFollowers();
        }

        // 3️⃣ 同一段位移原封不動發給名單上每一台
        ApplyDeltaToFollowers(delta);
    }

    private void StartFollowing()
    {
        following = true;
        lastPlayerPos = motion.position;    // 歸零，切檔那一瞬間不該產生位移
        nextScanTime = 0f;                  // 立刻掃一次
        rejectReported.Clear();
        scanSummaryDone = false;
        if (logBroadcast) UnityEngine.Debug.Log("<color=orange>🔙 [倒車跟隨] 玩家切到 R 檔</color>", this);
    }

    private void StopFollowing(string reason)
    {
        foreach (var w in whiteFollowers) if (w != null) w.EndPlayerReverseFollow();
        foreach (var g in grayFollowers) if (g != null) g.EndPlayerReverseFollow();

        int n = whiteFollowers.Count + grayFollowers.Count;
        whiteFollowers.Clear();
        grayFollowers.Clear();
        following = false;

        if (logBroadcast) UnityEngine.Debug.Log($"<color=orange>🔙 [倒車跟隨] 結束（{reason}），釋放 {n} 台</color>", this);
    }

    private void ApplyDeltaToFollowers(float delta)
    {
        float releaseDist = broadcastRange + releaseMargin;

        for (int i = whiteFollowers.Count - 1; i >= 0; i--)
        {
            ReverseThreatAICar car = whiteFollowers[i];
            if (car == null || !car.isFollowingPlayerReverse)
            {
                whiteFollowers.RemoveAt(i);    // 車被回收，或狀態被別的系統接手了
                continue;
            }
            if (Vector3.Distance(motion.position, car.transform.position) > releaseDist)
            {
                if (logBroadcast) UnityEngine.Debug.Log($"<color=grey>🔙 白車「{car.name}」離開範圍，釋放</color>", car);
                car.EndPlayerReverseFollow();
                whiteFollowers.RemoveAt(i);
                continue;
            }
            car.ApplyPlayerReverseDelta(delta);
        }

        for (int i = grayFollowers.Count - 1; i >= 0; i--)
        {
            CarNavigation car = grayFollowers[i];
            if (car == null || !car.isFollowingPlayerReverse)
            {
                grayFollowers.RemoveAt(i);
                continue;
            }
            if (Vector3.Distance(motion.position, car.transform.position) > releaseDist)
            {
                if (logBroadcast) UnityEngine.Debug.Log($"<color=grey>🔙 灰車「{car.name}」離開範圍，釋放</color>", car);
                car.EndPlayerReverseFollow();
                grayFollowers.RemoveAt(i);
                continue;
            }
            car.ApplyPlayerReverseDelta(delta);
        }
    }

    private void ScanForFollowers()
    {
        Collider[] hits = Physics.OverlapSphere(motion.position, broadcastRange, ~0,
                                              QueryTriggerInteraction.Ignore);

        // 同一台車可能有多個碰撞體，用 HashSet 去重
        var seen = new HashSet<GameObject>();
        int aiCarsSeen = 0;

        foreach (var h in hits)
        {
            if (h == null) continue;

            // 排除玩家自己。
            //
            // ⚠️ 這裡絕對不能用 motion.root —— 場景階層是
            //      RRRR → SSS → playerCAR → vehicle ONLY LOD0
            //    整座城市和所有「場景裡手動擺放的車」都掛在 RRRR 底下，
            //    拿 root 比對等於把整個場景都當成玩家自己排除掉。
            //    執行時 Instantiate 出來的 Dynamic_Replenish_AI_* 沒有父物件、
            //    自己就是根物件，所以只有那些逃過一劫 —— 看起來像「有時候有作用」，
            //    非常難查。只比對玩家車本體這棵子樹就夠了。
            if (h.transform.IsChildOf(motion)) continue;

            ReverseThreatAICar white = h.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation gray = white == null ? h.GetComponentInParent<CarNavigation>() : null;
            if (white == null && gray == null) continue;

            // 🚑 救護車是 ReverseThreatAICar 的子類，絕對不能被拖著倒退
            if (white is AmbulanceAI) continue;

            GameObject go = white != null ? white.gameObject : gray.gameObject;
            if (!seen.Add(go)) continue;
            aiCarsSeen++;
            if (white != null ? whiteFollowers.Contains(white) : grayFollowers.Contains(gray)) continue;

            string kind = white != null ? "白車" : "灰車";
            float behind, lateral, headingDot;
            Measure(go.transform, out behind, out lateral, out headingDot);

            // 過濾條件沒過 → 印出它的實際數字，才知道是哪一關、差多少
            string filterReason = FilterReason(behind, lateral, headingDot);
            if (filterReason != null)
            {
                ReportReject(go, kind, filterReason, behind, lateral, headingDot);
                continue;
            }

            // 資格判斷（在演危險因子？在避讓救護車？）由車子自己回答，
            // 廣播器看不到那些 private 旗標，也不該重複實作一份
            string carReason;
            bool accepted = white != null ? white.BeginPlayerReverseFollow(out carReason)
                                          : gray.BeginPlayerReverseFollow(out carReason);
            if (!accepted)
            {
                ReportReject(go, kind, carReason ?? "車子端拒絕", behind, lateral, headingDot);
                continue;
            }

            if (white != null) whiteFollowers.Add(white); else grayFollowers.Add(gray);

            if (logBroadcast)
                UnityEngine.Debug.Log($"<color=lime>🔙 [倒車跟隨] 納入{kind}「{go.name}」" +
                          $"（後方 {behind:F1}m、橫向 {Mathf.Abs(lateral):F1}m、同向 {headingDot:F2}）</color>", go);
        }

        // 每段 R 檔只總結一次。掃不到車時要能分辨「後方真的沒車」還是「查詢抓不到車」
        if (logBroadcast && !scanSummaryDone)
        {
            scanSummaryDone = true;
            UnityEngine.Debug.Log($"<color=cyan>🔙 [倒車跟隨] 掃描結果｜半徑 {broadcastRange}m 命中 {hits.Length} 個碰撞體" +
                      $"｜其中 AI 車 {aiCarsSeen} 台｜已納入 {whiteFollowers.Count + grayFollowers.Count} 台</color>", this);

            if (aiCarsSeen == 0)
            {
                DumpHits(hits);
                ReportNearestCarsFallback();
            }
        }
    }

    /// <summary>
    /// 量玩家與目標車的相對關係。
    ///
    /// 刻意不用 InverseTransformPoint —— 它會把父物件的縮放算進去。
    /// 玩家的 vehicle ONLY LOD0 縮放是 1.5，用那個量出來的距離只有真值的 1/1.5，
    /// 所有門檻都會被悄悄放寬 1.5 倍。改用單位向量點積，與縮放無關。
    /// </summary>
    private void Measure(Transform target, out float behind, out float lateral, out float headingDot)
    {
        Vector3 toCar = target.position - motion.position;
        behind = Vector3.Dot(toCar, -motion.forward);    // 正 = 在玩家後方
        headingDot = Vector3.Dot(motion.forward, target.forward);

        // 🔧 橫向距離用兩個座標框各量一次，取比較寬鬆的那個。
        //
        //    只用玩家車身框的話，走廊是「綁在玩家車身軸上的長方形」——
        //    玩家車一斜，走廊就跟著歪出車道。後方 12m 的車，玩家只要歪 15 度，
        //    走廊邊緣就跑掉 12 x sin15° = 3.1m，遠超過 lateralTolerance(1.8m)，
        //    那台車就落在走廊外 → 斜著倒車完全不觸發連鎖，正正倒車卻正常。
        //
        //    AI 車是端正對齊車道的，所以「從它的車身框量」問的就是
        //    「玩家在不在我這條車道的正前方」—— 跟玩家車身歪不歪完全無關。
        //    兩者取絕對值較小的：任一個框認定同車道就算數。
        float latPlayer = Vector3.Dot(toCar, motion.right);
        if (!measureInBothFrames) { lateral = latPlayer; return; }

        float latTarget = Vector3.Dot(-toCar, target.right);
        lateral = Mathf.Abs(latTarget) < Mathf.Abs(latPlayer) ? latTarget : latPlayer;
    }

    /// <summary>AI 車 0 台時，把實際抓到的碰撞體列出來，確認球形查詢到底掃到了什麼</summary>
    private void DumpHits(Collider[] hits)
    {
        if (hits.Length == 0)
        {
            UnityEngine.Debug.LogWarning("🔙 [倒車跟隨] OverlapSphere 一個碰撞體都沒抓到 —— 範圍或位置有問題。", this);
            return;
        }

        // 逐一列出，不按根物件去重 —— 上一版就是因為去重，14 個碰撞體被壓成 1 行，
        // 差點看不出「它們全都在 RRRR 底下」這件事。
        var lines = new List<string>();
        foreach (var h in hits)
        {
            if (h == null) continue;

            string owner = "（非車輛）";
            ReverseThreatAICar w = h.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation g = w == null ? h.GetComponentInParent<CarNavigation>() : null;
            if (w != null) owner = $"白車 {w.name}";
            else if (g != null) owner = $"灰車 {g.name}";

            lines.Add($"{h.name}｜root={h.transform.root.name}｜{owner}｜" +
                      $"距離 {Vector3.Distance(motion.position, h.transform.position):F1}m" +
                      $"｜是玩家自己={h.transform.IsChildOf(motion)}");
        }

        UnityEngine.Debug.LogWarning($"🔙 [倒車跟隨] 掃到的 {hits.Length} 個碰撞體明細：\n  " +
                      string.Join("\n  ", lines), this);
    }

    /// <summary>
    /// OverlapSphere 一台 AI 車都沒抓到時的保底診斷：
    /// 繞過 physics 查詢，直接列出場上離玩家最近的 3 台車。
    /// 如果這裡列得出來、而球形查詢抓不到，問題就在碰撞體／圖層／範圍，不在過濾條件。
    /// </summary>
    private void ReportNearestCarsFallback()
    {
        var all = new List<Transform>();
        foreach (var w in FindObjectsOfType<ReverseThreatAICar>()) if (!(w is AmbulanceAI)) all.Add(w.transform);
        foreach (var g in FindObjectsOfType<CarNavigation>()) all.Add(g.transform);

        if (all.Count == 0)
        {
            UnityEngine.Debug.LogWarning("🔙 [倒車跟隨] 場上完全沒有任何 ReverseThreatAICar / CarNavigation。", this);
            return;
        }

        all.Sort((a, b) => Vector3.Distance(motion.position, a.position)
                             .CompareTo(Vector3.Distance(motion.position, b.position)));

        for (int i = 0; i < Mathf.Min(3, all.Count); i++)
        {
            Transform t = all[i];
            float behind, lateral, headingDot;
            Measure(t, out behind, out lateral, out headingDot);
            UnityEngine.Debug.LogWarning($"🔙 [倒車跟隨] 場上第 {i + 1} 近的車「{t.name}」｜直線距離 " +
                           $"{Vector3.Distance(motion.position, t.position):F1}m" +
                           $"｜後方 {behind:F1}m（負=在前方）、橫向 {lateral:F1}m" +
                           $"｜同向 {headingDot:F2}", t);
        }
    }

    /// <summary>正後方 + 同車道 + 同向，回傳沒過的那一關，全過則回傳 null</summary>
    private string FilterReason(float behind, float lateral, float headingDot)
    {
        if (behind < minBehindDistance)
            return $"不在後方（後方距離 {behind:F1}m < 門檻 {minBehindDistance}m）";

        if (Mathf.Abs(lateral) > lateralTolerance)
            return $"橫向超標（{Mathf.Abs(lateral):F1}m > 門檻 {lateralTolerance}m，判定為隔壁車道）";

        // 對向車（車頭朝著玩家）如果被納入，它會往 -forward 退 = 朝玩家開過來
        if (headingDot < sameHeadingDot)
            return $"車頭不同向（同向值 {headingDot:F2} < 門檻 {sameHeadingDot}，判定為對向或轉彎中）";

        return null;
    }

    /// <summary>同一台車在同一段 R 檔期間只報一次，避免每 0.5 秒洗版</summary>
    private void ReportReject(GameObject go, string kind, string reason,
                            float behind, float lateral, float headingDot)
    {
        if (!logBroadcast) return;
        if (!rejectReported.Add(go)) return;

        UnityEngine.Debug.Log($"<color=grey>🔙 [倒車跟隨] 略過{kind}「{go.name}」：{reason}" +
                  $"｜實測 後方 {behind:F1}m、橫向 {Mathf.Abs(lateral):F1}m、同向 {headingDot:F2}</color>", go);
    }

    private void OnDisable()
    {
        if (following) StopFollowing("廣播器被關閉");
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // 編輯模式下 motion 還沒解析，用同一套規則現算一次，Gizmo 才會畫在真正會動的物件上
        Transform t = motion;
        if (t == null)
        {
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb == null) rb = GetComponentInParent<Rigidbody>();
            if (rb == null) rb = GetComponentInChildren<Rigidbody>();
            t = rb != null ? rb.transform : transform;
        }

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.3f);
        Gizmos.DrawWireSphere(t.position, broadcastRange);

        // 車道走廊：只有落在這個帶狀範圍內的後車才會跟。
        // ⚠️ 開了 measureInBothFrames 之後，這個框畫的是「玩家車身框」那一半；
        //    實際判定還會再用對方車的車身框量一次，所以真正的涵蓋範圍比畫出來的寬。
        // 用 TRS 而不是 localToWorldMatrix —— 玩家車有 1.5 倍縮放，
        // 直接吃 localToWorldMatrix 會把走廊也畫大 1.5 倍，跟實際判定範圍對不起來
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.6f);
        Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
        Gizmos.DrawWireCube(new Vector3(0f, 0f, -broadcastRange * 0.5f),
                            new Vector3(lateralTolerance * 2f, 1.5f, broadcastRange));
    }
#endif
}