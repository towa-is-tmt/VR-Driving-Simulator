using UnityEngine;

public class TrafficLightTrigger : MonoBehaviour
{
    public enum LaneType
    {
        A_C,          // 南北向車道 (A -> C)
        B_D_Straight, // 東西向直行車道 (B -> D)
        B_D_LeftTurn  // 東西向左轉專用道 (僅特殊路口有效)
    }

    [Header("路口類型")]
    public bool isSpecialIntersection = false; // 勾選代表這是特殊路口(有左轉專用時相)

    [Header("車道方向篩選")]
    [Range(0f, 1f)]
    [Tooltip("車頭方向與這個停等盒車道長軸的對齊度門檻。沿車道行進的車接近 1，橫向穿越的接近 0。設 0 = 不篩選，回到舊行為")]
    public float laneAlignDot = 0.6f;

    [Header("車道類型")]
    public LaneType laneType = LaneType.A_C;

    [Header("左轉判定前瞻")]
    // 車要不要左轉是「用路網幾何算出來的」:沿著未來幾個節點找第一個超過 15 度的轉彎。
    // 但左轉專用道常常有很長的引道 —— 場景裡就有一條是
    //     D1 (1)[停止線] → D_L → D_L (1) → D_L (2) → D_L (3) → -A_L0[54 度左轉]
    // 真正的彎在第 5 個節點,而預設只看 2 個 → 車回答「我不左轉」→ 左轉專用盒把它
    // 當成直行車處理 → 箭頭燈亮不動,等直行綠燈才跟著走。這個欄位就是給那種車道調的。
    [Tooltip("往前看幾個節點來判斷這台車要不要左轉。左轉專用道若有很長的引道(真正的彎在好幾個節點之後)就要調大;" +
             "一般車道維持 2。看太遠會把「下一個路口才左轉」的直行車誤判成左轉車。" +
             "判斷方法:選那條車道的停止線節點,順著 Next Choices 點下去,數幾顆之後路才真的彎,設成那個顆數加 1")]
    [Range(1, 8)]
    public int leftTurnLookaheadSteps = 2;

    [Header("診斷")]
    [Tooltip("勾選後,每台進到這個盒子的車會每秒印一次完整判定過程:車道對齊度、前瞻在看哪幾顆節點、" +
             "各段轉角、目前時相、最終決定,以及此刻有哪幾個盒子正壓著這台車(停等是 OR,任一個說停就停)。" +
             "不勾完全不執行,對行為與效能都沒有影響")]
    public bool debugLogDecision = false;

    // 每台車每秒最多印一行,否則 OnTriggerStay 每幀都會刷版
    private readonly System.Collections.Generic.Dictionary<int, float> nextLogTime
        = new System.Collections.Generic.Dictionary<int, float>();

    private TrafficSystem_Brain brain;
    private Collider selfCollider;

    void Start()
    {
        brain = Object.FindFirstObjectByType<TrafficSystem_Brain>();
        selfCollider = GetComponent<Collider>();
    }

    private void OnTriggerStay(Collider other)
    {
        // 用 GetComponentInParent 而不是 GetComponent：
        // 有些車（例如機車）的碰撞體放在子物件上，只找碰撞體自己那一層會抓不到，
        // 結果那台車永遠收不到停等指令，看起來就像完全不理紅綠燈。
        CarNavigation car = other.GetComponentInParent<CarNavigation>();
        ReverseThreatAICar threatCar = other.GetComponentInParent<ReverseThreatAICar>();

        // 停等區只該管「沿著這個車道行進」的車。橫向穿越、或只是壓到盒子邊角的車不該被它扣住 ——
        // 新路網 77 個停等盒密度高，又跟白車路網的車道不對齊，不篩方向的話會出現
        // 「綠燈了還有車釘在原地」：它自己的方向轉綠了，卻被另一個方向還紅著的盒子留在停等清單裡。
        Transform vehicleTf = car != null ? car.transform : (threatCar != null ? threatCar.transform : null);
        float alignDotNow = vehicleTf != null ? Mathf.Abs(Vector3.Dot(vehicleTf.forward, LaneAxisWorld())) : -1f;
        bool alignedWithLane = vehicleTf == null || laneAlignDot <= 0f
                               || alignDotNow >= laneAlignDot;

        if ((car != null || threatCar != null) && brain != null && alignedWithLane)
        {
            // 左轉專用道的號誌只對「真的要左轉」的車有效，所以要逐車問意圖，不能算一次共用

            // 灰車：新版介面。一台車可能同時壓在好幾個停等區上，
            // 所以要連 selfCollider 一起傳，讓車自己記住是「哪一個」區要它停，
            // 離開其中一個時才不會把其他區的停等指令一起清掉。
            if (car != null)
            {
                bool intendsLeft = car.WillTurnLeftWithin(leftTurnLookaheadSteps);
                bool stopNow = ShouldStopNow(intendsLeft);
                car.UpdateTrafficLightStatus(selfCollider, stopNow);
                LogDecision(car.gameObject, car.isScooter ? "機車" : "灰車", alignDotNow, true,
                            intendsLeft, stopNow, car.DescribeLookahead(leftTurnLookaheadSteps),
                            car.DescribeActiveRedTriggers());
            }

            // 白車 / 救護車（AmbulanceAI 繼承自 ReverseThreatAICar）現在也用同一套多停等區介面。
            // 救護車的 SetTrafficLightStatus override 仍會接手強制忽略紅燈，行為不變。
            if (threatCar != null)
            {
                bool intendsLeft = threatCar.WillTurnLeftWithin(leftTurnLookaheadSteps);
                bool stopNow = ShouldStopNow(intendsLeft);
                threatCar.UpdateTrafficLightStatus(selfCollider, stopNow);
                LogDecision(threatCar.gameObject, "白車", alignDotNow, true,
                            intendsLeft, stopNow, "(白車沒有節點鏈診斷)", threatCar.DescribeActiveRedTriggers());
            }
        }
        else if (!alignedWithLane)
        {
            // 車在盒子裡轉向(例如右轉切出去)後方向就不符了。這時要主動把自己
            // 從對方的停等清單移除，否則會留下永遠清不掉的殘留把車釘死。
            if (car != null)
            {
                car.RemoveTrafficLightTrigger(selfCollider);
                LogDecision(car.gameObject, car.isScooter ? "機車" : "灰車", alignDotNow, false,
                            false, false, "(未對齊,不做左轉判定)", car.DescribeActiveRedTriggers());
            }
            if (threatCar != null)
            {
                threatCar.RemoveTrafficLightTrigger(selfCollider);
                LogDecision(threatCar.gameObject, "白車", alignDotNow, false, false, false,
                            "(未對齊,不做左轉判定)", threatCar.DescribeActiveRedTriggers());
            }
        }

        // 🚦 玩家車：回報燈號給闖紅燈偵測器。純新增，不影響上面任何 AI 的判斷。
        NotifyPlayerDetector(other);
    }

    private void OnTriggerExit(Collider other)
    {
        CarNavigation car = other.GetComponentInParent<CarNavigation>();
        if (car != null) car.RemoveTrafficLightTrigger(selfCollider);

        ReverseThreatAICar threatCar = other.GetComponentInParent<ReverseThreatAICar>();
        if (threatCar != null) threatCar.RemoveTrafficLightTrigger(selfCollider);

        // 🚦 玩家離開停等區：如果離開的當下仍是紅燈且車子往前開，就是闖越。
        //    多傳一個「車道方向」給偵測器 —— 這個盒子本來是給 AI 判斷停等用的，
        //    玩家只要擦到邊角或橫越過去也會觸發 OnTriggerExit，
        //    沒有方向資訊就會把「路過」判成闖紅燈。
        RedLightViolationDetector detector = other.GetComponentInParent<RedLightViolationDetector>();
        if (detector != null)
            detector.ReportZoneExit(brain, laneType == LaneType.A_C, LaneAxisWorld(), isSpecialIntersection);
    }

    /// <summary>
    /// 這個停等區現在該不該叫 AI 停。黃燈一律算要停（AI 保守處理）。
    /// intendsLeftTurn 只有左轉專用道會用到：不轉的車改看直行的主綠燈。
    /// </summary>
    private bool ShouldStopNow(bool intendsLeftTurn)
    {
        if (brain == null) return false;

        if (!isSpecialIntersection)
        {
            // ==============================
            // 一般路口判定邏輯
            // ==============================
            return laneType == LaneType.A_C
                ? (brain.normalPhase != TrafficSystem_Brain.NormalPhase.A_to_C_Green)
                : (brain.normalPhase != TrafficSystem_Brain.NormalPhase.B_to_D_Green);
        }

        // ==============================
        // 特殊路口判定邏輯
        // ==============================
        switch (laneType)
        {
            case LaneType.A_C:
                return brain.specialPhase != TrafficSystem_Brain.SpecialPhase.A_to_C_Green;
            case LaneType.B_D_Straight:
                // 這種路口有左轉專用時相 → 要左轉的車一律等箭頭燈，
                // 就算它站在「直行」的停等區裡也一樣。白車走的是另一套路網，
                // 車道不見得跟灰車的停等區對齊，只靠盒子分車道會漏掉。
                if (intendsLeftTurn)
                    return brain.specialPhase != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftGreen;

                return brain.specialPhase != TrafficSystem_Brain.SpecialPhase.B_to_D_Green;
            case LaneType.B_D_LeftTurn:
                // 左轉專用綠燈只放行「真的要左轉」的車。
                // 直行車也可能壓在這個盒子上(白車走的是另一套路網，車道不見得對齊)，
                // 這種車要等的是直行的主綠燈 —— 否則箭頭燈一亮它就直接穿過路口，
                // 跟對向直行車正面對上。
                if (!intendsLeftTurn)
                    return brain.specialPhase != TrafficSystem_Brain.SpecialPhase.B_to_D_Green;

                // 真的要左轉：只能在左轉專用綠燈起步，才不會撞對向直行
                return brain.specialPhase != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftGreen;
        }
        return false;
    }

    /// <summary>
    /// 回報「對玩家而言現在是不是紅燈」。
    /// 上面給 AI 用的 ShouldStopNow 把黃燈也算成要停，但違規判定必須排除黃燈，所以另外算一次。
    /// </summary>
    private void NotifyPlayerDetector(Collider other)
    {
        if (brain == null) return;

        RedLightViolationDetector detector = other.GetComponentInParent<RedLightViolationDetector>();
        if (detector == null) return;

        // 🚦 只有玩家「真的沿著這個盒子的車道行進」時才回報它的燈號。
        //    路口停等盒很密，玩家的大車會同時壓到橫向那條街的停等盒 —— 那個盒子在你這邊
        //    綠燈時剛好是紅的。偵測器只有一個共用的 zoneIsRed，橫向街的紅燈滲進去之後，
        //    你合法通過自己這條、離開盒子時就會被讀成「離開當下是紅燈」→ 誤判闖紅燈。
        //    AI 車的判定（上面）本來就有這道方向篩選，玩家這條當初漏掉了，這裡補齊。
        //
        //    方向一律用 Rigidbody 的實際速度方向判斷：玩家車階層
        //    (RRRR→SSS→playerCAR→vehicle ONLY LOD0) 的 collider Z 軸不保證是車頭方向，
        //    用 other.transform.forward 會抓錯（與 ReverseTrigger 踩過的是同一個坑）。
        //    速度太慢時方向不可靠 → 照舊回報，交給移動中的那幾幀把值更新對；
        //    真正決定違規的「離開盒子」永遠發生在移動中，那時篩選一定生效。
        Rigidbody prb = other.attachedRigidbody;
        if (prb != null && laneAlignDot > 0f)
        {
            Vector3 v = prb.velocity; v.y = 0f;
            if (v.sqrMagnitude >= 0.25f)   // ≥ 0.5 m/s 才有可靠的行進方向
            {
                float align = Mathf.Abs(Vector3.Dot(v.normalized, LaneAxisWorld()));
                if (align < laneAlignDot) return;   // 橫向街／擦邊 → 不讓它污染玩家的燈號狀態
            }
        }

        detector.ReportZoneState(IsRedForPlayerNow());
    }

    /// <summary>
    /// 對玩家而言是不是紅燈（黃燈不算，黃燈通過是合法的）。
    /// 特殊路口的東西向刻意把「直行綠、左轉綠」都當成不是紅燈 ——
    /// 觸發區分不出玩家實際待在哪一個車道，寧可漏判也不要誤判，
    /// 誤告玩家闖紅燈比漏抓一次嚴重得多。
    /// </summary>
    private bool IsRedForPlayerNow()
    {
        if (brain == null) return false;

        if (!isSpecialIntersection)
        {
            var p = brain.normalPhase;
            return laneType == LaneType.A_C
                ? (p != TrafficSystem_Brain.NormalPhase.A_to_C_Green
                   && p != TrafficSystem_Brain.NormalPhase.A_to_C_Yellow)
                : (p != TrafficSystem_Brain.NormalPhase.B_to_D_Green
                   && p != TrafficSystem_Brain.NormalPhase.B_to_D_Yellow);
        }

        var s = brain.specialPhase;
        if (laneType == LaneType.A_C)
        {
            return s != TrafficSystem_Brain.SpecialPhase.A_to_C_Green
                && s != TrafficSystem_Brain.SpecialPhase.A_to_C_Yellow;
        }

        return s != TrafficSystem_Brain.SpecialPhase.B_to_D_Green
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_Yellow
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftGreen
            && s != TrafficSystem_Brain.SpecialPhase.B_to_D_LeftYellow;
    }

    /// <summary>
    /// 這個停等區的「車道方向」（世界座標、已正規化）。
    ///
    /// 停等盒是沿著車道拉長的：實測 32 個盒子都是長軸 22~25m、短軸 2~7m。
    /// 取比較長的那個水平軸就是車道方向。
    /// 不寫死 transform.right 是因為有些盒子被轉了 90 度擺放，
    /// 用 lossyScale 判斷才不會因為擺法不同而抓錯軸。
    /// </summary>
    /// <summary>🔍 每台車每秒印一行完整判定過程。只有勾了 debugLogDecision 才會執行。</summary>
    private void LogDecision(GameObject go, string kind, float alignDot, bool aligned,
                             bool intendsLeft, bool stopNow, string lookahead, string redTriggers)
    {
        if (!debugLogDecision || go == null) return;

        int id = go.GetInstanceID();
        if (nextLogTime.TryGetValue(id, out float until) && Time.time < until) return;
        nextLogTime[id] = Time.time + 1f;

        string nl = System.Environment.NewLine;
        string group = transform.parent != null ? transform.parent.name : "(無父物件)";
        string phase = brain == null ? "★找不到 Brain"
                     : (isSpecialIntersection ? $"特殊時相 = {brain.specialPhase}" : $"一般時相 = {brain.normalPhase}");

        UnityEngine.Debug.Log(
            $"<color=yellow>🚦 [停等盒]</color> {group} / {name}" + nl +
            $"   車道類型={laneType}   特殊路口={isSpecialIntersection}   前瞻步數={leftTurnLookaheadSteps}" + nl +
            $"   對象: {kind}「{go.name}」" + nl +
            $"   車道對齊: 點積={alignDot:F2} 門檻={laneAlignDot:F2} → {(aligned ? "通過" : "★未通過,這個盒子不管這台車")}" + nl +
            $"   前瞻: {lookahead}" + nl +
            $"   WillTurnLeftWithin → {(intendsLeft ? "true (要左轉)" : "★false (判定為不左轉)")}" + nl +
            $"   {phase}" + nl +
            $"   本盒判定 → {(stopNow ? "停" : "放行")}" + nl +
            $"   此刻壓著這台車的盒子: {redTriggers}", go);
    }

    private Vector3 LaneAxisWorld()
    {
        Vector3 s = transform.lossyScale;
        Vector3 axis = (Mathf.Abs(s.x) >= Mathf.Abs(s.z)) ? transform.right : transform.forward;
        axis.y = 0f;
        return axis.sqrMagnitude > 0.0001f ? axis.normalized : transform.forward;
    }
}
