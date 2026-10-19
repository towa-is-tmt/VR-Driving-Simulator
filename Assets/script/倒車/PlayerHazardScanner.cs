using System.Collections;
using UnityEngine;

// 🚨 玩家車上的「找碴雷達」:持續掃描前方正常行駛的白車,時機到了就叫它演急煞/倒車
//
// 【兩段式流程】
//   1. 掃描：在 Min~Max Event Distance 之間挑一台正前方、同向的白車
//   2. 待命：導演核准後把那台車的速度接管，帶到預定的演出位置
//   3. 發動：站定位之後才真的演
//
//   為什麼要分兩段：直接在掃到的距離發動，事件會發生在 15~20m 外，
//   玩家看到的是「遠處有台車突然煞車」，威脅感很弱。
//
// 【待命有兩種模式，由 Use Gap Holding 切換】
//   ● 間距控制(預設)：目標車跟著玩家的車速走，把間距穩定在 Hold Gap，
//     站穩 Hold Settle Seconds 就發動。距離不需要玩家去縮短。
//   ● 限速追趕(舊行為)：把目標壓到玩家車速的固定比例，等玩家追到
//     Fire Distance 才發動。
//
//   為什麼預設改成間距控制：限速追趕把事件品質綁在玩家怎麼開 ——
//   開快就追撞（體感變成碰撞不是危險）、開慢就逾時取消；而且為了讓玩家
//   追得到，目標必須慢很多，等於在「無聲的待命期」就把該在事件當下才
//   釋放的速度差提前花光，急煞下去根本沒有速度可掉。間距控制讓演出位置
//   與速度差都是設計出來的，跟玩家開法無關。
public class PlayerHazardScanner : MonoBehaviour
{
    [Header("📡 掃描設定")]
    [Tooltip("幾秒掃一次")]
    public float scanInterval = 1f;
    [Tooltip("事件最遠發動距離")]
    public float maxEventDistance = 40f;
    [Tooltip("挑目標的最近距離。要比下面的發動距離大，才有逼近的空間")]
    public float minEventDistance = 15f;
    public LayerMask aiCarLayer;

    [Header("🎯 觸發條件")]
    [Tooltip("玩家時速低於此值(m/s)不觸發,避免停紅燈時前車突然發瘋")]
    public float minPlayerSpeed = 5f;
    [Range(0f, 1f)]
    [Tooltip("目標必須在玩家前方多少角度內(1=正前方,0=側面也算)")]
    public float frontDotThreshold = 0.4f;
    [Range(0f, 1f)]
    [Tooltip("目標車頭必須跟玩家同向(排除對向來車)")]
    public float sameDirectionDot = 0.5f;
    // frontDotThreshold 是個大錐形(0.4 ≈ ±66 度),在 20m 處橫向可以偏到 18m ——
    // 隔壁車道才差 3.5m,不加橫向檢查一定會抓到。
    // 而隔壁車道的車急煞對玩家毫無威脅,你直接從旁邊開過去就好,
    // 事件白演一次還消耗掉 60 秒的節流。
    [Tooltip("★ 橫向容許距離(m)。超過視為隔壁車道,不挑它當目標。單一車道建議 2.0~2.5")]
    public float laneTolerance = 2.2f;

    [Header("🎲 事件權重")]
    public float reverseWeight = 1f;
    public float brakeWeight = 1f;

    [Header("🎯 待命與發動")]
    [Tooltip("急煞的發動距離(m)。上面的 Min/Max Event Distance 只負責「挑目標」,真正觸發看這個")]
    public float brakeFireDistance = 5f;
    [Tooltip("倒車的發動距離(m)。倒車要留多一點空間給玩家反應,通常設得比急煞遠")]
    public float reverseFireDistance = 7f;
    [Tooltip("待命期間目標車的速度「上限」(m/s)。實際限速還會再乘玩家車速,取兩者較小值")]
    public float armSpeedCap = 6f;
    // 固定限速只有在「玩家車速剛好比它快一點」的窄區間才有用:
    //   玩家比它慢 → 目標一路開走 → 距離拉遠取消
    //   玩家跟它差不多 → 永遠追不上 → 等待逾時取消
    // 改成跟著玩家車速走,就能保證距離只會縮短:目標永遠比玩家慢。
    // 玩家停下來時目標也跟著停(前車在車陣裡等紅燈本來就自然),距離不會被拉開。
    // 兩種事件要的東西相反,所以係數分開:
    //   急煞 → 要高係數。前車只比你慢一點,你不會不知不覺跟著收油,
    //          發動當下你還維持原本車速 → 剩下的反應時間才短、才有壓迫感。
    //   倒車 → 要低係數。那台車本來就要停下來再倒,不需要保留你的車速,
    //          係數低一點你追得快,事件比較容易演成。
    [Range(0.1f, 1f)]
    [Tooltip("★ 急煞的待命限速比例 = 玩家車速 × 這個值。高一點你才不會跟著慢下來,煞下去才夠力")]
    public float brakeArmSpeedFactor = 0.8f;
    [Range(0.1f, 1f)]
    [Tooltip("★ 倒車的待命限速比例。低一點追得快、比較容易觸發;倒車不需要保留玩家車速")]
    public float reverseArmSpeedFactor = 0.55f;
    [Tooltip("鎖定後等多久還沒逼近就放棄(秒)")]
    public float armMaxWaitSeconds = 12f;
    [Tooltip("待命中距離拉遠到多少就放棄(m)。\n" +
             "★ 一定要明顯大於 Max Event Distance ——\n" +
             "  兩者共用同一個值的話，車在接近上限處被挑中(例如 19m/上限 20m)，\n" +
             "  只要稍微拉開一點就立刻取消，事件永遠演不成")]
    public float armCancelDistance = 45f;

    // ─────────────────────────────────────────────────────────────
    [Header("📐 間距控制待命 (建議開啟)")]
    // 舊做法是「把目標車限速,讓玩家自己追上來」。它的結構性問題是
    // 事件品質完全綁在玩家怎麼開:
    //   開太快 → 追撞,體驗變成碰撞而不是危險
    //   開太慢 → 永遠追不到,armMaxWaitSeconds 逾時取消
    //   而且為了讓你追得到,目標必須比你慢很多 —— 等於在「無聲的待命期」
    //   就把該在事件當下才釋放的速度差提前花光,急煞下去根本沒有速度可掉。
    //
    // 間距控制反過來:待命期不要求玩家縮短距離,而是讓目標車「跟著玩家的
    // 車速走」,把間距穩定在 Hold Gap。你快它快、你慢它慢,距離不變。
    // 距離穩住之後才發動 —— 事件永遠在設計好的距離、設計好的速度差發生,
    // 跟玩家開法無關,也不會再出現追撞才觸發。
    [Tooltip("關掉會退回舊的「限速讓玩家追」行為(用 Arm Speed Cap 與兩個 Factor)。留著方便 A/B 比較")]
    public bool useGapHolding = true;
    [Tooltip("急煞的待命間距(m)。目標車會被穩定在你前方這個距離,然後才煞")]
    public float brakeHoldGap = 12f;
    [Tooltip("倒車的待命間距(m)。要比急煞遠 —— 它退過來的那段距離要看得見才有意義")]
    public float reverseHoldGap = 16f;
    [Tooltip("間距控制的比例增益。目標速度 = 玩家車速 - 增益 x (目前間距 - Hold Gap)。\n大 = 收斂快但容易前後晃;小 = 平順但慢。0.4~0.8 之間")]
    public float gapGain = 0.6f;
    [Tooltip("間距容許帶(m)。進入 Hold Gap ± 這個範圍就算「站好位置」")]
    public float gapTolerance = 2.5f;
    [Tooltip("在容許帶裡連續穩定幾秒才發動。太短會在剛進帶就爆掉,太長玩家會先變換車道")]
    public float holdSettleSeconds = 0.8f;
    [Tooltip("間距控制期間的速度硬上限(m/s)。設得比白車 Move Speed(16) 高就等於不限,交給間距控制決定")]
    public float holdSpeedCeiling = 20f;
    // ─────────────────────────────────────────────────────────────

    [Header("診斷")]
    public bool logArming = true;
    [Tooltip("間距控制期間每秒印一次目前間距與目標速度,用來調 Hold Gap 與 Gain。調完記得關掉")]
    public bool logGapHold = false;

    [Header("📍 量距離的來源")]
    // ⚠️ 這裡踩過一個安靜到極點的坑,值得寫下來。
    //
    // 原本是「GetComponent → GetComponentInParent → GetComponentInChildren」找 Rigidbody,
    // 前提是這支腳本掛的 CAR 上「沒有」Rigidbody,於是會往下找到 vehicle ONLY LOD0 那顆真車身。
    // 但後來 CAR 上被加了一顆 Rigidbody(Mass 1、開重力、**身上沒有任何 Collider**),
    // 第一行就抓到它,整套雷達從此量的是那顆的位置與速度:
    //   ① 它沒有 Collider 又開重力 → 執行時永遠自由落體,穿過地面一直往下掉
    //   ② 子物件 vehicle 自己有 Rigidbody = 獨立物理體,不會跟著父物件走 → 兩者各走各的
    //   ③ 於是 Gizmos 的圈圈停在遠處不跟車、rb.velocity 幾乎沒有水平分量,
    //      minPlayerSpeed 這種門檻永遠不成立 —— 而且完全不會報錯
    //
    // 那顆 Rigidbody 不能直接刪(刪了車子會亂動,代表有別的東西在用它),
    // 所以改成可以在 Inspector 直接指定,不必動階層。
    [Tooltip("量距離與車速要用哪顆 Rigidbody。\n" +
             "★ 這台車有兩顆 Rigidbody 時務必指定(指到真正有 Box Collider 的車身那顆,例如 vehicle ONLY LOD0)。\n" +
             "留空 = 自動尋找:優先挑「自己身上有 Collider」的那顆,藉此跳過沒有 Collider 的幽靈剛體")]
    public Rigidbody motionSource;

    // 真正在移動的那個 Transform。
    private Transform motion;
    private Rigidbody rb;

    private float nextScanTime;
    private bool armed;              // 待命中就暫停掃描,避免重複鎖定
    private ReverseThreatAICar armedCar;   // 供 OnDisable 還原限速用,不留下被卡住的車

    private void Start()
    {
        // ① Inspector 指定的優先 ② 自動尋找(會跳過沒有 Collider 的幽靈剛體)
        rb = motionSource != null ? motionSource : AutoPickBody();
        motion = rb != null ? rb.transform : transform;

        if (rb == null)
            Debug.LogError("[雷達] 這台車上找不到 Rigidbody，無法判斷玩家車速與位置。", this);
        else if (logArming)
            Debug.Log($"<color=orange>📡 [雷達] 已啟動｜元件掛在「{name}」" +
                      $"｜量距離的物件是「{motion.name}」" +
                      $"（{(motionSource != null ? "Inspector 指定" : "自動尋找")}，" +
                      $"質量 {rb.mass:F0}，自身 Collider {(rb.GetComponent<Collider>() != null ? "有" : "★無")}）</color>", this);
    }

    /// <summary>
    /// 自動挑一顆 Rigidbody 來當「玩家在哪、開多快」的來源。
    /// 順序跟舊版一樣(自己 → 往上 → 往下),但每一步都要求「那顆自己身上有 Collider」——
    /// 沒有 Collider 的 Rigidbody 不可能是車身,只會是殘留物或工具物件,
    /// 抓到它就是整套雷達靜默失效的原因。三步都挑不到才退回原本的順序。
    /// </summary>
    private Rigidbody AutoPickBody()
    {
        Rigidbody self = GetComponent<Rigidbody>();
        if (HasOwnCollider(self)) return self;

        // ⚠️ 不能用 GetComponentInParent / GetComponentInChildren 直接取一顆 ——
        //    這兩個都是「從自己開始」找,自己身上有 Rigidbody 時第一個就拿回自己,
        //    於是三個候選全是同一顆幽靈,篩選完全沒作用(這是第一版寫錯的地方)。
        //    要真的往上、往下走,得自己走階層並逐一篩。
        for (Transform p = transform.parent; p != null; p = p.parent)
        {
            Rigidbody up = p.GetComponent<Rigidbody>();
            if (HasOwnCollider(up)) return up;
        }

        foreach (Rigidbody down in GetComponentsInChildren<Rigidbody>(true))
        {
            if (down != self && HasOwnCollider(down)) return down;
        }

        // 全部都沒有 Collider → 退回原本的順序,至少維持舊行為而不是變成 null
        if (self != null) return self;
        Rigidbody anyUp = GetComponentInParent<Rigidbody>();
        if (anyUp != null) return anyUp;
        return GetComponentInChildren<Rigidbody>();
    }

    private static bool HasOwnCollider(Rigidbody body)
    {
        return body != null && body.GetComponent<Collider>() != null;
    }

    private void Update()
    {
        if (armed) return;                        // 已經鎖定一台了,等它演完再說
        if (Time.time < nextScanTime) return;
        nextScanTime = Time.time + scanInterval;

        // 玩家太慢就不找碴
        if (rb == null || rb.velocity.magnitude < minPlayerSpeed) return;

        // 掃描周圍 AI 車,挑出「正前方、同向、距離適中」的最近一台
        Collider[] hits = Physics.OverlapSphere(motion.position, maxEventDistance, aiCarLayer);
        ReverseThreatAICar best = null;
        float bestDist = float.MaxValue;

        foreach (var col in hits)
        {
            ReverseThreatAICar ai = col.GetComponentInParent<ReverseThreatAICar>();
            if (ai == null) continue;
            if (!ai.CanTakeAction() || ai.isStaticParked) continue;

            Vector3 toAI = ai.transform.position - motion.position;
            toAI.y = 0;
            float dist = toAI.magnitude;
            if (dist < minEventDistance || dist > maxEventDistance) continue;

            if (Vector3.Dot(motion.forward, toAI.normalized) < frontDotThreshold) continue;     // 要在前方
            if (Vector3.Dot(motion.forward, ai.transform.forward) < sameDirectionDot) continue; // 要同向
            // 用「AI 車的車道軸」量橫向，不是玩家的 ——
            // 玩家變換車道時車身是斜的，用 motion.right 量出來的值反而會變小，檢查就過了。
            // AI 車沿車道行駛，它的 right 才是穩定的車道橫向基準。
            if (Mathf.Abs(Vector3.Dot(-toAI, ai.transform.right)) > laneTolerance) continue;      // 要同車道

            if (dist < bestDist) { bestDist = dist; best = ai; }
        }
        if (best == null) return;

        // 決定演哪齣:優先挑「還沒經歷過」的那個,兩個都覆蓋才靠權重擲骰
        float totalWeight = reverseWeight + brakeWeight;
        if (totalWeight <= 0f) return;

        bool doReverse;
        var director = DangerEventDirector.Instance;
        bool reverseUncovered = director != null && director.IsUncovered("倒車");
        bool brakeUncovered = director != null && director.IsUncovered("急煞");

        if (reverseUncovered && !brakeUncovered) doReverse = true;        // 只剩倒車沒覆蓋 → 演倒車
        else if (brakeUncovered && !reverseUncovered) doReverse = false;  // 只剩急煞沒覆蓋 → 演急煞
        else doReverse = Random.Range(0f, totalWeight) < reverseWeight;   // 都覆蓋或都沒 → 照權重

        string eventName = doReverse ? "倒車" : "急煞";

        // 問導演,沒核准就下次再說
        if (DangerEventDirector.Instance != null && !DangerEventDirector.Instance.RequestEvent(eventName)) return;

        armed = true;
        StartCoroutine(ArmAndFireRoutine(best, doReverse, eventName));
    }

    /// <summary>
    /// 鎖定 → 接管目標車速 → 站定位 → 發動。
    ///
    /// 間距控制模式:把目標穩定在 holdGap，站穩 holdSettleSeconds 才演。
    /// 舊的限速追趕模式:把目標壓慢，等玩家追進 fireDist 才演。
    ///
    /// 不論成功發動還是中途取消,都一定要 ClearHazardArmSpeedCap(),
    /// 否則那台車會被永久限速卡在路上。所有離開路徑都收斂到同一處還原。
    /// </summary>
    private IEnumerator ArmAndFireRoutine(ReverseThreatAICar car, bool doReverse, string eventName)
    {
        float fireDist = doReverse ? reverseFireDistance : brakeFireDistance;
        float armFactor = doReverse ? reverseArmSpeedFactor : brakeArmSpeedFactor;
        float holdGap = doReverse ? reverseHoldGap : brakeHoldGap;

        armedCar = car;
        car.SetHazardArmSpeedCap(useGapHolding
            ? GapHoldCap(Vector3.Distance(motion.position, car.transform.position), holdGap)
            : CurrentArmCap(armFactor));

        if (logArming)
        {
            if (useGapHolding)
                Debug.Log($"<color=yellow>📡 [雷達] 鎖定「{car.name}」演{eventName}，" +
                          $"間距控制中：把它穩定在你前方 {holdGap}m（容許 ±{gapTolerance}m），" +
                          $"站穩 {holdSettleSeconds}s 就發動</color>", car);
            else
                Debug.Log($"<color=yellow>📡 [雷達] 鎖定「{car.name}」演{eventName}，" +
                          $"限速 {CurrentArmCap(armFactor):F1} m/s（上限 {armSpeedCap}、你的 {armFactor:P0}），" +
                          $"等你逼近到 {fireDist}m</color>", car);
        }

        float waited = 0f;
        float settled = 0f;          // 間距控制：已經在容許帶裡待了多久
        float nextGapLogTime = 0f;
        string cancelReason = null;

        while (true)
        {
            if (car == null) { cancelReason = "目標車已被回收"; break; }

            // 待命途中被別的系統徵用(觸發盒、連鎖倒車、救護車避讓…)就放棄。
            // 「被限速」本身不影響 CanTakeAction,所以這裡不會誤判成自己把自己擋掉。
            if (!car.CanTakeAction())
            {
                cancelReason = "目標車被其他事件接手";
                break;
            }

            Vector3 flat = car.transform.position - motion.position;
            flat.y = 0f;
            float dist = flat.magnitude;

            // 🔑 每幀重算限速。
            //    間距控制：目標速度跟著玩家走,把 dist 收斂到 holdGap —— 距離會「穩住」。
            //    舊模式：目標永遠比玩家慢,所以距離只會縮短不會拉遠。
            car.SetHazardArmSpeedCap(useGapHolding
                ? GapHoldCap(dist, holdGap)
                : CurrentArmCap(armFactor));

            // 待命期有 5~15 秒,中間你或它都可能變換車道。
            // 基準用 AI 車的軸:它沿車道行駛,姿態穩定;玩家變換車道時車身是斜的,
            // 用玩家的 right 去量,橫向值反而會變小而讓檢查通過。
            float behind, lateral;
            MeasureFromCar(car, out behind, out lateral);

            if (behind <= 0f)
            {
                cancelReason = $"玩家已不在它後方（{behind:F1}m）";
                break;
            }
            // 留 1.5 倍緩衝,避免車身輕微晃動就取消
            if (lateral > laneTolerance * 1.5f)
            {
                cancelReason = $"偏離同車道（相對它的橫向 {lateral:F1}m > {laneTolerance * 1.5f:F1}m）";
                break;
            }

            if (useGapHolding)
            {
                // 站穩了才演。玩家車速也要夠 —— 停在原地時演急煞毫無意義
                float pv = PlayerForwardSpeed();
                bool inBand = Mathf.Abs(dist - holdGap) <= gapTolerance;

                if (logGapHold && Time.time >= nextGapLogTime)
                {
                    nextGapLogTime = Time.time + 1f;
                    Debug.Log($"<color=cyan>📐 [間距] {car.name}｜目前 {dist:F1}m / 目標 {holdGap}m｜" +
                              $"給它的限速 {GapHoldCap(dist, holdGap):F1} m/s｜你 {pv:F1} m/s｜" +
                              $"站穩 {settled:F1}/{holdSettleSeconds}s</color>", car);
                }

                if (inBand && pv >= minPlayerSpeed) settled += Time.deltaTime;
                else settled = 0f;

                if (settled >= holdSettleSeconds) break;                    // ✅ 站穩了,發動

                // 保底：玩家衝太快，還沒站穩就已經殺進容許帶內側 ——
                // 這時候放掉事件才是最糟的（你正貼著它,卻什麼都沒發生）,直接演
                if (dist < holdGap - gapTolerance) break;
            }
            else
            {
                if (dist <= fireDist) break;                                // ✅ 到了,發動
            }

            // 用專屬的放棄門檻,不跟挑目標的 maxEventDistance 共用 —— 共用等於沒有緩衝
            if (dist > armCancelDistance)
            {
                cancelReason = $"距離拉遠到 {dist:F1}m（放棄門檻 {armCancelDistance}m）";
                break;
            }
            if (waited >= armMaxWaitSeconds)
            {
                cancelReason = $"等待逾時 {armMaxWaitSeconds:F0}s（目前還有 {dist:F1}m）";
                break;
            }

            waited += Time.deltaTime;
            yield return null;
        }

        // 🔑 唯一的還原點:成功與失敗都會經過這裡
        if (car != null) car.ClearHazardArmSpeedCap();
        armedCar = null;

        if (cancelReason != null)
        {
            if (logArming)
                Debug.Log($"<color=grey>📡 [雷達] 取消{eventName}：{cancelReason}（已還原限速）</color>", this);
            armed = false;
            yield break;
        }

        // 發動前最後確認一次,這一幀之內狀態可能又變了
        if (!car.CanTakeAction())
        {
            if (logArming)
                Debug.Log($"<color=grey>📡 [雷達] 到達發動距離但「{car.name}」已不能演，取消</color>", car);
            armed = false;
            yield break;
        }

        // ★ 最終位置確認:必須真的在它正後方、同車道。
        //   迴圈裡是用 1.5 倍緩衝在追蹤,發動這一刻要用嚴格值 ——
        //   你在最後一刻切換車道的話,演出來也沒有意義(你直接從旁邊過去)。
        float finalBehind, finalLateral;
        MeasureFromCar(car, out finalBehind, out finalLateral);
        if (finalBehind <= 0f || finalLateral > laneTolerance)
        {
            if (logArming)
                Debug.Log($"<color=grey>📡 [雷達] 到達發動距離但玩家不在正後方" +
                          $"（後方 {finalBehind:F1}m、橫向 {finalLateral:F1}m > {laneTolerance}m），取消</color>", car);
            armed = false;
            yield break;
        }

        float finalDist = Vector3.Distance(motion.position, car.transform.position);
        if (doReverse)
        {
            car.TriggerReverseFromZone();
            Debug.Log($"<color=orange>📡 [雷達] {car.name} 在 {finalDist:F1}m 對玩家演出無預警倒車!</color>", car);
        }
        else
        {
            car.TriggerBrakeFromZone();
            Debug.Log($"<color=red>📡 [雷達] {car.name} 在 {finalDist:F1}m 對玩家演出惡意急煞!</color>", car);
        }

        armed = false;
    }

    /// <summary>
    /// 待命限速 = min(固定上限, 玩家目前車速 × factor)。factor 由事件種類決定。
    /// 只取朝前的速度分量 —— 玩家在倒車或橫移時不該讓目標車跟著加速。
    /// </summary>
    /// <summary>
    /// 以「AI 車自己的座標軸」量玩家的相對位置。
    /// behind  &gt; 0 = 玩家在它正後方；lateral = 相對它車道中線的橫向偏移。
    /// 用它的軸而不是玩家的軸，量測才不會被玩家變換車道時的車身姿態影響。
    /// </summary>
    private void MeasureFromCar(ReverseThreatAICar car, out float behind, out float lateral)
    {
        Vector3 toPlayer = motion.position - car.transform.position;
        toPlayer.y = 0f;
        behind = Vector3.Dot(toPlayer, -car.transform.forward);
        lateral = Mathf.Abs(Vector3.Dot(toPlayer, car.transform.right));
    }

    private float CurrentArmCap(float factor)
    {
        if (rb == null) return armSpeedCap;

        float playerForward = Mathf.Max(0f, Vector3.Dot(rb.velocity, motion.forward));
        return Mathf.Min(armSpeedCap, playerForward * factor);
    }

    /// <summary>玩家朝正前方的速度分量(m/s)。倒車或橫移不算。</summary>
    private float PlayerForwardSpeed()
        => rb == null ? 0f : Mathf.Max(0f, Vector3.Dot(rb.velocity, motion.forward));

    /// <summary>
    /// 間距控制：算出這一幀該給目標車的速度上限，讓間距收斂到 holdGap。
    ///
    ///   目標速度 = 玩家車速 - 增益 x (目前間距 - 目標間距)
    ///
    ///   間距太遠 → 目標比玩家慢 → 你追上來 → 間距縮小
    ///   間距太近 → 目標比玩家快 → 拉開 → 間距變大
    ///   剛好      → 目標 = 玩家車速 → 距離凍住
    ///
    /// 這個值是丟給 SetHazardArmSpeedCap 當「上限」用的，而白車那端是用
    /// Mathf.Min 套用（ReverseThreatAICar 約 2994 行）—— 也就是說它只能讓車更慢，
    /// 永遠不會強迫加速。紅燈停等、路口停讓、跟車減速那些把速度壓到 0 的邏輯
    /// 全部不受影響，這是刻意的：待命中的演員車該停紅燈時還是要停。
    /// 代價是玩家如果開得比白車 Move Speed 還快，間距一定會縮短 ——
    /// 那種情況交給迴圈裡的「保底發動」處理。
    /// </summary>
    private float GapHoldCap(float gap, float holdGap)
    {
        float desired = PlayerForwardSpeed() - gapGain * (gap - holdGap);
        return Mathf.Clamp(desired, 0f, holdSpeedCeiling);
    }

    private void OnDisable()
    {
        // 腳本被關掉或物件被銷毀時,協程會直接中斷,還原那一行就跑不到了 ——
        // 目標車會被永久限速卡在路上。所以這裡一定要補一次還原。
        StopAllCoroutines();
        if (armedCar != null) armedCar.ClearHazardArmSpeedCap();
        armedCar = null;
        armed = false;
    }

    private void OnDrawGizmosSelected()
    {
        // ⚠️ 編輯模式不會跑 Start(),所以 motion 是 null,一定會走下面這條。
        //    這裡原本有一份「自己 → 往上 → 往下」的複製邏輯,沒有跟著 Start() 一起改,
        //    結果 Inspector 指定了 Motion Source、圈圈卻還是畫在舊的位置 ——
        //    看起來像「改了沒用」,其實是兩份邏輯不同步。改成共用同一套。
        Transform t = motion;
        if (t == null)
        {
            Rigidbody r = motionSource != null ? motionSource : AutoPickBody();
            t = r != null ? r.transform : transform;
        }

        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(t.position, maxEventDistance);      // 挑目標的外圈
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(t.position, minEventDistance);      // 挑目標的內圈

        // 實際發動的位置,這幾圈才是玩家真正感受到的距離
        if (useGapHolding)
        {
            // 間距控制:目標車會被穩定在這兩圈上,然後才演
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(t.position, reverseHoldGap);
            Gizmos.color = new Color(1f, 0f, 0.3f);
            Gizmos.DrawWireSphere(t.position, brakeHoldGap);
        }
        else
        {
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(t.position, reverseFireDistance);
            Gizmos.color = new Color(1f, 0f, 0.3f);
            Gizmos.DrawWireSphere(t.position, brakeFireDistance);
        }
    }
}
