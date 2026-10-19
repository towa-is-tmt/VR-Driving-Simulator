using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🚸 玩家逆向偵測
///
/// 掛在玩家車上（有 Rigidbody 的那個物件，例如 CAR）。
///
/// 【做什麼】
///   持續比對玩家車頭方向與「玩家所在那條車道」的行進方向，判定為逆向且持續
///   超過 confirmSeconds 就跳提示，並在持續逆向期間每隔 repeatInterval 再提醒一次。
///
/// 【車道方向哪裡來】
///   直接用場景既有的路網：白車的 TrafficPathNode 與灰車的 WaypointNode，
///   每個節點的 nextChoices 就定義了該車道往哪個方向走。不需要另外拉觸發區。
///
/// 【為什麼是「點到線段」而不是「最近的節點」】
///   雙向道的兩條車道只差 3~4 公尺，而節點沿路方向的間隔可能有 5~10 公尺。
///   如果找「距離最近的節點」，你明明開在正確車道上，卻很容易抓到對向車道的節點
///   （因為它在側前方，直線距離反而更短）→ 直接誤判成逆向。
///   改成量「玩家到車道線段的垂直距離」就準了 —— 你在哪條車道上，那條的垂直距離最小。
///   這是整支腳本唯一有技術難度的地方。
/// </summary>
public class PlayerWrongWayDetector : MonoBehaviour
{
    [Header("判定條件")]
    [Tooltip("幾秒檢查一次。不需要每幀，0.2~0.3 就很足夠")]
    public float checkInterval = 0.25f;
    [Tooltip("玩家離最近車道超過這個距離就不判定（停車場、路肩、空地）")]
    public float maxLaneDistance = 8f;
    // -0.7 ≈ 夾角大於 134 度，必須是明顯「對著開」。
    // 不要放寬到 -0.4 —— 從小路轉進大路要橫越對向車道，過程中角度會掃過那個範圍。
    [Tooltip("車頭與車道方向的點積低於此值算逆向。-1=完全相反，-0.7≈夾角大於 134 度")]
    [Range(-1f, 0f)]
    public float wrongWayDot = -0.7f;
    [Tooltip("連續多久才確認是逆向（秒）")]
    public float confirmSeconds = 1.5f;
    // 橫越一條車道只會佔用 3~5 公尺，沿著對向車道開則是幾十公尺。
    // 這一條專門用來排除「轉彎時橫越對向車道」的誤判 —— 時間門檻擋不掉那種情況，
    // 因為慢慢轉彎也可能撐過 confirmSeconds，但橫越的距離本質上就是有上限的。
    [Tooltip("★ 逆向狀態下必須實際走完這麼多公尺才確認。專門排除轉彎橫越對向車道的誤判")]
    public float confirmDistance = 20f;
    [Tooltip("時速低於此值不判定。停著調頭、慢速倒車是正常操作")]
    public float minSpeedKmH = 8f;
    // 逆向行駛的人是「直線開下去」的，車頭方向穩定；轉彎時車頭每秒要轉好幾十度。
    // 這個訊號跟路網幾何完全無關，所以不會被窄路、大路口那些奇怪的節點排列影響，
    // 比單純加大 confirmDistance 可靠得多。
    [Tooltip("車頭每秒轉超過這個角度就視為「正在轉彎」，暫停判定並歸零。設 0 = 關閉這道保險")]
    public float maxYawRateForJudge = 25f;
    // 「同向車道離我比較近 → 我走在正確那條上」這個判斷,在剛跨過雙黃線時
    // 兩邊距離差不多(各 3~4m),於是要一路開到對向車道中線、拉開到 2.5 vs 9.7
    // 才會成立 —— 症狀就是「開到最邊邊才提示」。
    // 但現實中壓過雙黃線就已經是違規,不必開到對面正中央。
    // 這個偏置讓「還沒完全過去」也算數:同向車道要近上這麼多公尺,才判定你走對邊。
    [Tooltip("跨中線的靈敏度(公尺)。0 = 舊行為(要比較靠近對向車道才算);" +
             "1.5 = 剛壓過中線就算。太大會讓貼著中線正常行駛也被判逆向")]
    [Range(0f, 4f)]
    public float centreLineBias = 1.5f;

    // 進入判定看「車頭角度」是對的 —— 那能擋掉路口轉彎橫越對向車道的誤判。
    // 但解除判定不該看角度:你人還壓在對向車道上,只是把方向盤轉了一點,
    // 舊版就會宣告「已回到正確方向」—— 而現實中你根本還在違規。
    // 開啟後,已經判定逆向的狀態只有「位置真的回到正確那一側」才會解除。
    [Tooltip("已判定逆向後,必須位置真的回到正確車道才解除;光把車頭轉正不算。" +
             "取消勾選 = 舊行為(車頭一轉就解除)")]
    public bool exitRequiresCorrectLane = true;

    [Tooltip("路口節點的方向沒有意義（可轉彎），判定時跳過")]
    public bool ignoreIntersectionNodes = true;

    [Header("★ 窄路一律不判定")]
    // 窄路通常只有一條節點鏈（AI 單向跑）但實際上雙向可通行，怎麼調參數都會誤報。
    // 這個地圖 846 個白車節點裡有 546 個是窄路，直接整段關掉最乾淨。
    [Tooltip("窄路完全不做逆向判定。開啟後那些路段永遠不會提示，換取零誤報")]
    public bool ignoreNarrowRoadNodes = true;
    // 灰車的 WaypointNode 沒有 isNarrowRoad 欄位，所以灰車線段改用「離窄路節點多近」來排除。
    // 兩套路網疊在同樣的路面上，用距離當遮罩是有效的。
    [Tooltip("灰車線段的中點離任一窄路節點小於這個距離，就一併排除。約一個路寬即可")]
    public float narrowRoadMaskRadius = 6f;
    // 只有白車的 TrafficPathNode 有 isNarrowRoad 且維護得完整，灰車的 WaypointNode 沒這個欄位。
    // 關掉灰車路網 = 窄路排除變成完全可靠，代價是只靠白車路網覆蓋的路段才會偵測。
    [Tooltip("是否把灰車 WaypointNode 路網也納入判定。關掉的話只用白車 TrafficPathNode，窄路排除最準")]
    public bool useGrayNetwork = true;

    [Header("★ 雙黃線白名單（最可靠的窄路排除）")]
    // 下面那些排除全都是啟發式的:窄路旗標只有白車路網有、遮罩半徑是猜的、
    // requirePairedLane 靠幾何推測「旁邊有沒有平行的同向車道」。
    // 啟發式一定會有漏網之魚,而每一次誤報都在教玩家錯的東西。
    //
    // 白名單反過來做:在節點上標 isDividedRoad,只有標過的路段才做逆向判定。
    // 沒標的(窄路、巷弄、停車場)自動豁免 —— 零誤報,代價是要先去標路。
    [Tooltip("只在標記為 isDividedRoad 的路段做逆向判定。" +
             "★ 開啟前必須先去節點上標記,否則等於整個關閉逆向偵測 —— " +
             "開場的 Console 會印出實際留下幾條線段,務必確認那個數字不是 0")]
    public bool requireDividedRoadFlag = false;

    [Header("★ 雙向道確認（避免窄路誤判）")]
    [Tooltip("必須在附近找得到「正確方向的車道」才判定逆向。\n\n" +
             "窄路通常只有一條節點鏈（AI 單向跑）但實際雙向可通行，\n" +
             "你往另一邊開就會被誤判。有分隔的雙向道則不同 —— 你開在對向車道時，\n" +
             "你該走的那條就在旁邊 3~4 公尺，找得到它才代表真的走錯邊。\n\n" +
             "★ 副作用：真正的單行道逆向開也不會被抓（因為找不到同向車道）")]
    public bool requirePairedLane = true;
    // 設太大(例如 14)會在密集市區撈到隔壁巷子，這一關就形同虛設 —— 那正是窄路誤報的原因。
    [Tooltip("往外找多遠算「附近有正確方向的車道」。一般車道間距 3~5m，設 8 就夠")]
    public float pairedLaneRadius = 8f;
    // 真正的雙向道，兩條車道必然平行(點積接近 -1，取絕對值接近 1)。
    // 窄路旁邊撈到的同向線段是岔路、巷子，角度是斜的，用這個濾掉。
    [Tooltip("★ 同向車道必須跟逆向車道平行到這個程度，才算是同一條路的兩個方向")]
    [Range(0f, 1f)]
    public float pairedLaneParallelDot = 0.8f;

    [Header("提示")]
    [TextArea(2, 3)]
    public string message = "您已闖入對向車道\n請返回您的車道";
    public Color messageColor = new Color(1f, 0.35f, 0.1f);
    public float displaySeconds = 3f;
    [Tooltip("持續逆向時每隔多久再提醒一次")]
    public float repeatInterval = 6f;

    [Header("診斷")]
    public bool logDetection = true;

    /// <summary>目前是否判定為逆向（供其他系統查詢，例如闖紅燈改判）</summary>
    public bool IsWrongWay { get; private set; }
    /// <summary>累計逆向次數</summary>
    public int WrongWayCount { get; private set; }

    // 預先攤平的車道線段。每 0.25 秒要掃 1300 多條，
    // 存成三個平行陣列比每次去問 Transform 快很多，也不會產生 GC。
    private Vector3[] segStart;
    private Vector3[] segEnd;
    private Vector3[] segDir;      // 已正規化
    private bool[] segIsWhite;     // true=白車 TrafficPathNode，false=灰車 WaypointNode
    private int segCount;

    private Transform motion;
    private Rigidbody rb;

    private float nextCheckTime;
    private float wrongWayTimer;
    private float wrongWayDistance;      // 逆向狀態下累計走了多遠
    private Vector3 lastCheckPos;
    private bool hasLastPos;
    private Vector3 lastFwd;             // 上一輪的車頭方向,用來算偏航率
    private bool hasLastFwd;

    // 窄路節點座標保留到執行時。判定成立時要回報「這裡離最近的窄路節點多遠」——
    // 這是分辨「這條路沒被標成窄路」與「標了但我沒排除掉」的唯一方法。
    private Vector3[] narrowNodeCache;
    private float nextBlockLogTime;   // 「為什麼沒判定」的診斷節流
    private float nextMessageTime;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponentInChildren<Rigidbody>();
        motion = rb != null ? rb.transform : transform;

        if (rb == null)
            Debug.LogError("[逆向偵測] 找不到 Rigidbody，無法取得車速與位置。", this);

        BuildLaneCache();

        if (logDetection)
        {
            int w = 0;
            for (int i = 0; i < segCount; i++) if (segIsWhite[i]) w++;
            Debug.Log($"<color=orange>🚸 [逆向偵測] 已啟動｜量位置的物件是「{motion.name}」" +
                      $"｜車道線段 {segCount} 條（白車 {w}、灰車 {segCount - w}）</color>", this);
        }
    }

    /// <summary>
    /// 把兩套路網的「節點 → 下一個節點」全部攤平成線段陣列。
    /// 只做一次，之後每次判定都只是純數學運算。
    /// </summary>
    private void BuildLaneCache()
    {
        var a = new List<Vector3>();
        var b = new List<Vector3>();
        var white = new List<bool>();

        // 窄路節點的座標清單。之所以要先收集起來，是因為灰車的 WaypointNode
        // 根本沒有 isNarrowRoad 這個欄位（只有白車的 TrafficPathNode 有）——
        // 只靠旗標會漏掉灰車那 487 個節點，窄路照樣被判逆向。
        // 所以改成用「離窄路節點多近」當遮罩，把兩套路網一起蓋掉。
        // ⚠️ isNarrowRoad 這個旗標身兼兩職:
        //      (1) 救護車選路 —— 有寬路就絕不進窄路
        //      (2) 逆向偵測排除 —— 窄路不判逆向
        //    它本來是為了 (1) 標的,標得寬鬆沒關係;但 (2) 借用同一個旗標之後,
        //    「救護車不想走的路」就被當成「不該判逆向的路」—— 兩者根本不是同一件事。
        //    這個地圖 752 顆白車節點裡有 570 顆(76%)標了窄路,
        //    兩個排除疊起來之後只剩 145 條線段能判定,逆向幾乎永遠不會觸發。
        //
        //    白名單模式下,「哪條路要判逆向」已經由 isDividedRoad 明確指定了,
        //    窄路排除不但多餘,還會把你標好的路一起擋掉 —— 所以自動停用。
        bool useNarrowExclusion = ignoreNarrowRoadNodes && !requireDividedRoadFlag;

        var narrowPositions = new List<Vector3>();
        if (ignoreNarrowRoadNodes || requireDividedRoadFlag)   // 診斷用的距離回報仍需要這份清單
        {
            foreach (var n in FindObjectsOfType<TrafficPathNode>())
                if (n != null && n.isNarrowRoad) narrowPositions.Add(n.transform.position);
        }

        int skippedNarrow = 0;
        int skippedNotDivided = 0;

        foreach (var n in FindObjectsOfType<TrafficPathNode>())
        {
            if (n == null || n.nextChoices == null) continue;
            if (ignoreIntersectionNodes && n.intersectionType != TrafficPathNode.IntersectionType.None) continue;
            if (useNarrowExclusion && n.isNarrowRoad) { skippedNarrow += n.nextChoices.Count; continue; }

            foreach (var nx in n.nextChoices)
            {
                if (nx == null) continue;
                if (useNarrowExclusion && nx.isNarrowRoad) { skippedNarrow++; continue; }
                // 白名單:兩端都要標記過才算。只看單端會在路口洩漏到沒標的岔路上。
                if (requireDividedRoadFlag && !(n.isDividedRoad && nx.isDividedRoad))
                { skippedNotDivided++; continue; }
                a.Add(n.transform.position);
                b.Add(nx.transform.position);
                white.Add(true);
            }
        }

        foreach (var n in useGrayNetwork ? FindObjectsOfType<WaypointNode>() : new WaypointNode[0])
        {
            if (n == null || n.nextChoices == null) continue;
            if (ignoreIntersectionNodes && n.intersectionType != WaypointNode.IntersectionType.None) continue;

            foreach (var nx in n.nextChoices)
            {
                if (nx == null) continue;

                // 灰車節點沒有窄路旗標 → 用線段中點離最近窄路節點的距離來判斷
                if (useNarrowExclusion &&
                    IsNearNarrowRoad((n.transform.position + nx.transform.position) * 0.5f, narrowPositions))
                {
                    skippedNarrow++;
                    continue;
                }

                if (requireDividedRoadFlag && !(n.isDividedRoad && nx.isDividedRoad))
                { skippedNotDivided++; continue; }

                a.Add(n.transform.position);
                b.Add(nx.transform.position);
                white.Add(false);
            }
        }

        narrowNodeCache = narrowPositions.ToArray();

        if (useNarrowExclusion && logDetection)
            Debug.Log($"<color=grey>🚸 [逆向偵測] 已排除窄路線段 {skippedNarrow} 條" +
                      $"（窄路節點 {narrowPositions.Count} 個，遮罩半徑 {narrowRoadMaskRadius}m）</color>", this);

        if (requireDividedRoadFlag)
        {
            if (a.Count == 0)
                Debug.LogWarning($"[逆向偵測] ★ 白名單模式開著,但沒有任何路段標記 isDividedRoad" +
                                 $"（已排除 {skippedNotDivided} 條未標記線段）→ 逆向偵測等於完全關閉。" +
                                 $"請先到節點上勾選,或把 Require Divided Road Flag 取消勾選。", this);
            else if (logDetection)
                Debug.Log($"<color=grey>🚸 [逆向偵測] 白名單模式:保留 {a.Count} 條雙黃線路段," +
                          $"排除 {skippedNotDivided} 條未標記線段。" +
                          $"已自動停用:窄路排除{(ignoreNarrowRoadNodes ? "(原本是開的)" : "(本來就關)")}、" +
                          $"同向車道確認{(requirePairedLane ? "(原本是開的)" : "(本來就關)")}" +
                          $" —— 白名單已經明確指定哪些路要判定,那兩道會把標好的路擋掉</color>", this);
        }

        segCount = a.Count;
        segStart = new Vector3[segCount];
        segEnd = new Vector3[segCount];
        segDir = new Vector3[segCount];
        segIsWhite = new bool[segCount];

        for (int i = 0; i < segCount; i++)
        {
            segStart[i] = a[i];
            segEnd[i] = b[i];
            segIsWhite[i] = white[i];
            Vector3 d = b[i] - a[i];
            d.y = 0f;
            segDir[i] = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
        }
    }

    private void Update()
    {
        if (rb == null || segCount == 0) return;
        if (Time.time < nextCheckTime) return;
        nextCheckTime = Time.time + checkInterval;

        // 太慢不判定 —— 停著調頭、慢速倒車都是正常操作，不該被罵
        if (rb.velocity.magnitude * 3.6f < minSpeedKmH)
        {
            ResetState();
            return;
        }

        Vector3 fwd = motion.forward; fwd.y = 0f; fwd.Normalize();

        // 🔄 正在轉彎就整個跳過。
        //    從小路轉進大路一定要橫越對向車道，過程中車頭會短暫對著那條車道的反方向；
        //    但轉彎的偏航率很高，逆向直行則幾乎是 0，用這個分辨最乾淨。
        if (maxYawRateForJudge > 0f && hasLastFwd)
        {
            float yawRate = Vector3.Angle(lastFwd, fwd) / checkInterval;
            lastFwd = fwd;
            if (yawRate > maxYawRateForJudge)
            {
                LogBlocked($"轉彎中（偏航率 {yawRate:F0} > {maxYawRateForJudge} 度/秒）" +
                           $"{(IsWrongWay && exitRequiresCorrectLane ? "｜已在逆向狀態 → 只暫停累計,不解除" : "")}");
                // 已經在逆向狀態時不歸零 —— 逆向中打方向盤本來就是常態,
                // 一轉就重置的話,違規者只要邊開邊修方向就永遠不會被記錄。
                if (!(IsWrongWay && exitRequiresCorrectLane)) ResetState();
                return;
            }
        }
        else { lastFwd = fwd; hasLastFwd = true; }

        // 這一輪相對上一輪走了多遠(逆向距離累計用)
        Vector3 curPos = motion.position;
        float stepDist = hasLastPos ? Vector3.Distance(curPos, lastCheckPos) : 0f;
        lastCheckPos = curPos;
        hasLastPos = true;

        float oppDist, sameDist;
        Vector3 oppDir, sameDir; bool oppIsWhite;
        EvaluateLanes(curPos, fwd, out oppDist, out sameDist, out oppDir, out sameDir, out oppIsWhite);

        // 1️⃣ 附近完全沒有逆向車道 → 不在路上，或方向本來就對
        if (oppDist > maxLaneDistance)
        {
            LogBlocked($"附近沒有逆向車道（最近的在 {oppDist:F1}m > Max Lane Distance {maxLaneDistance}m）");
            ClearIfNeeded(); return;
        }

        // 2️⃣ 同向車道離我更近 → 我就是走在正確的那條上。
        //    加上 centreLineBias:同向車道要「明顯」比較近才算走對邊,
        //    否則剛跨過中線的那段會被當成正常行駛。
        if (sameDist + centreLineBias <= oppDist) { ClearIfNeeded(); return; }   // 走在正確車道上,不用回報

        // 3️⃣ 夾角要夠大才算逆向
        float dot = Vector3.Dot(fwd, oppDir);
        if (dot >= wrongWayDot)
        {
            bool holdByPosition = IsWrongWay && exitRequiresCorrectLane;
            LogBlocked($"夾角不夠大（同向值 {dot:F2} >= Wrong Way Dot {wrongWayDot}）" +
                       $"{(holdByPosition ? "｜但人還在對向車道 → 維持逆向狀態" : "")}");
            // 只把車頭轉正不算回到正確車道,要等位置真的過去(上面第 2 關)才解除
            if (!holdByPosition) ClearIfNeeded();
            return;
        }

        // 4️⃣ ★ 關鍵：附近必須存在「正確方向的車道」。
        //    窄路只有一條節點鏈但實際雙向可通行，找不到同向車道就不能判逆向 ——
        //    你的地圖有 546/846 個節點是窄路，少了這一關會整天誤報。
        // ⚠️ requirePairedLane 是「這是不是有分向的雙向道」的啟發式猜測,
        //    跟 isDividedRoad 白名單在回答同一個問題。兩個都開 = 要同時通過兩關,
        //    而那個「猜」的一關會把你已經明確標好的路也擋掉。白名單優先,自動停用。
        if (requirePairedLane && !requireDividedRoadFlag)
        {
            // 距離要夠近
            if (sameDist > pairedLaneRadius)
            {
                LogBlocked($"同向車道太遠（{sameDist:F1}m > Paired Lane Radius {pairedLaneRadius}m）" +
                           $"｜逆向車道就在 {oppDist:F1}m｜寬路漏報的話把這個值調大");
                ResetState();
                return;
            }

            // 而且要跟逆向車道平行 —— 才代表它們是同一條路的兩個方向,
            // 而不是剛好在旁邊的岔路或巷子
            float parallel = Mathf.Abs(Vector3.Dot(sameDir, oppDir));
            if (parallel < pairedLaneParallelDot)
            {
                LogBlocked($"同向車道不平行（平行度 {parallel:F2} < {pairedLaneParallelDot}）" +
                           $"｜判定為岔路而非對向車道");
                ResetState();
                return;
            }
        }

        // 方向不對,開始累計時間與距離
        wrongWayTimer += checkInterval;
        wrongWayDistance += stepDist;

        // ★ 兩個門檻都要過。距離那道專門擋「轉彎時橫越對向車道」——
        //   橫越只佔 3~5 公尺,沿著對向車道開才會累積到幾十公尺
        if (wrongWayTimer < confirmSeconds) return;
        if (wrongWayDistance < confirmDistance)
        {
            LogBlocked($"逆向中但距離還不夠（已走 {wrongWayDistance:F0}m / 需要 {confirmDistance}m）");
            return;
        }

        if (!IsWrongWay)
        {
            IsWrongWay = true;
            WrongWayCount++;
            nextMessageTime = 0f;   // 確認的當下立刻提示一次
            if (logDetection)
            {
                float nearNarrow = DistanceToNearestNarrowNode(curPos);
                // 白名單模式下窄路排除是「刻意停用」的,不是漏洞 —— 這裡的措辭要跟著改,
                // 否則每次判定都跳一行看起來像錯誤的訊息,會誤導排查方向。
                string narrowNote = requireDividedRoadFlag
                    ? $"(白名單模式:窄路排除已停用,這裡離最近窄路節點 {(nearNarrow < 0f ? -1f : nearNarrow):F1}m,不影響判定)"
                    : (nearNarrow < 0f
                        ? "場景中沒有任何窄路節點"
                        : (nearNarrow <= narrowRoadMaskRadius
                            ? $"★ 這裡離窄路節點只有 {nearNarrow:F1}m（在遮罩內卻仍判定 → 遮罩沒蓋到這條線段）"
                            : $"離最近窄路節點 {nearNarrow:F1}m（> 遮罩 {narrowRoadMaskRadius}m → 這條路沒被標成窄路）"));

                Debug.Log($"<color=red>🚸 [逆向偵測] 判定逆向！同向值 {dot:F2}｜" +
                          $"逆向車道 {oppDist:F1}m、同向車道 {sameDist:F1}m｜" +
                          $"★ 這條線段來自「{(oppIsWhite ? "白車 TrafficPathNode" : "灰車 WaypointNode")}」｜" +
                          $"已逆向行駛 {wrongWayDistance:F0}m（累計 {WrongWayCount} 次）｜" +
                          $"座標 {curPos.x:F0},{curPos.z:F0}｜{narrowNote}</color>", this);
            }
        }

        if (Time.time >= nextMessageTime)
        {
            nextMessageTime = Time.time + repeatInterval;
            if (HazardAwarenessTracker.Instance != null && !string.IsNullOrEmpty(message))
                HazardAwarenessTracker.Instance.ShowMessage(message, messageColor, displaySeconds);
        }
    }

    private void ResetState()
    {
        wrongWayTimer = 0f;
        wrongWayDistance = 0f;
        IsWrongWay = false;
    }

    /// <summary>
    /// 一次掃過所有車道線段，分別找出「與我同向」與「與我逆向」中各自最近的一條。
    ///
    /// 分成兩類是為了回答兩個不同的問題：
    ///   逆向那條多近 → 我是不是壓在對向車道上
    ///   同向那條多近 → 這條路到底有沒有「我該走的那一邊」
    /// 只找單一最近線段的話，窄路（只有一條鏈、雙向通行）永遠會被判成逆向。
    /// </summary>
    private void EvaluateLanes(Vector3 pos, Vector3 fwd,
                               out float oppDist, out float sameDist,
                               out Vector3 oppDir, out Vector3 sameDir, out bool oppIsWhite)
    {
        oppDist = float.MaxValue;
        sameDist = float.MaxValue;
        oppDir = Vector3.forward;
        sameDir = Vector3.forward;
        oppIsWhite = true;

        pos.y = 0f;
        float oppSqr = float.MaxValue;
        float sameSqr = float.MaxValue;

        for (int i = 0; i < segCount; i++)
        {
            Vector3 a = segStart[i]; a.y = 0f;
            Vector3 b = segEnd[i];   b.y = 0f;

            // 點到線段的最短距離（把點投影到線段上並夾在兩端之間）
            Vector3 ab = b - a;
            float abSqr = ab.sqrMagnitude;
            float t = abSqr > 0.0001f ? Mathf.Clamp01(Vector3.Dot(pos - a, ab) / abSqr) : 0f;
            Vector3 closest = a + ab * t;

            float sqr = (pos - closest).sqrMagnitude;

            if (Vector3.Dot(fwd, segDir[i]) < 0f)
            {
                if (sqr < oppSqr) { oppSqr = sqr; oppDir = segDir[i]; oppIsWhite = segIsWhite[i]; }
            }
            else
            {
                if (sqr < sameSqr) { sameSqr = sqr; sameDir = segDir[i]; }
            }
        }

        if (oppSqr < float.MaxValue) oppDist = Mathf.Sqrt(oppSqr);
        if (sameSqr < float.MaxValue) sameDist = Mathf.Sqrt(sameSqr);
    }

    /// <summary>離最近的窄路節點多遠。沒有任何窄路節點時回傳 -1。</summary>
    private float DistanceToNearestNarrowNode(Vector3 pos)
    {
        if (narrowNodeCache == null || narrowNodeCache.Length == 0) return -1f;

        pos.y = 0f;
        float best = float.MaxValue;
        for (int i = 0; i < narrowNodeCache.Length; i++)
        {
            Vector3 p = narrowNodeCache[i]; p.y = 0f;
            float sqr = (pos - p).sqrMagnitude;
            if (sqr < best) best = sqr;
        }
        return Mathf.Sqrt(best);
    }

    /// <summary>這個位置是不是落在窄路附近（灰車節點沒有窄路旗標，只能靠距離判斷）</summary>
    private bool IsNearNarrowRoad(Vector3 pos, List<Vector3> narrowPositions)
    {
        float r2 = narrowRoadMaskRadius * narrowRoadMaskRadius;
        pos.y = 0f;

        for (int i = 0; i < narrowPositions.Count; i++)
        {
            Vector3 p = narrowPositions[i]; p.y = 0f;
            if ((pos - p).sqrMagnitude <= r2) return true;
        }
        return false;
    }

    /// <summary>
    /// 回報「為什麼這一輪沒判定」。
    ///
    /// 原本這些訊息的條件是 wrongWayTimer > 0，但那個計時器要通過全部關卡才會開始累加 ——
    /// 一直被某一關擋住的話它永遠是 0，訊息一次都不會印，等於完全查不到原因。
    /// 改成純時間節流，被擋就會定期回報。
    /// </summary>
    private void LogBlocked(string reason)
    {
        if (!logDetection) return;
        if (Time.time < nextBlockLogTime) return;
        nextBlockLogTime = Time.time + 2f;
        Debug.Log($"<color=grey>🚸 [逆向偵測] 未判定：{reason}</color>", this);
    }

    /// <summary>方向恢復正常時的收尾，順便印一次讓使用者知道解除了</summary>
    private void ClearIfNeeded()
    {
        if (IsWrongWay && logDetection)
            Debug.Log("<color=lime>🚸 [逆向偵測] 已回到正確車道（位置已越回自己那一側）</color>", this);
        ResetState();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform t = motion;
        if (t == null)
        {
            Rigidbody r = GetComponent<Rigidbody>();
            if (r == null) r = GetComponentInParent<Rigidbody>();
            if (r == null) r = GetComponentInChildren<Rigidbody>();
            t = r != null ? r.transform : transform;
        }

        // 判定半徑：離所有車道都超出這個範圍就不判定
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.5f);
        Gizmos.DrawWireSphere(t.position, maxLaneDistance);

        // 同向車道的搜尋半徑（判定雙向道用）
        Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.35f);
        Gizmos.DrawWireSphere(t.position, pairedLaneRadius);

        // 執行中才畫得出「目前偵測到的逆向車道方向」
        if (Application.isPlaying && segCount > 0)
        {
            Vector3 fwd = t.forward; fwd.y = 0f; fwd.Normalize();
            float oppDist, sameDist; Vector3 oppDir, sameDir; bool oppIsWhite;
            EvaluateLanes(t.position, fwd, out oppDist, out sameDist, out oppDir, out sameDir, out oppIsWhite);

            if (oppDist <= maxLaneDistance)
            {
                Gizmos.color = IsWrongWay ? Color.red : Color.yellow;
                Gizmos.DrawLine(t.position, t.position + oppDir * 6f);
            }
        }
    }
#endif
}
