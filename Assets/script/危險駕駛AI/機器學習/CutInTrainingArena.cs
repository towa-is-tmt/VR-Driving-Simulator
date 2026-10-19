using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 🧪 鬼切強化學習訓練場 v2（用城市實際路網樣本 ＋ 隨機化的虛擬玩家）
///
/// 第一版的問題：訓練場是理想化的（車道 5m、車寬 1.9m、玩家只會 IDM 跟車、前車跟玩家一樣快），
/// 模型到了城市一遇到沒看過的狀況就亂做，3 次測試 0 次成功、1 次擦撞。
///
/// v2 的做法（報告可寫成「以實際場景路網建構訓練環境 ＋ 領域隨機化 domain randomization」）：
///   ● 道路：每回合從「城市路網訓練樣本」抽一個真的能觸發鬼切的位置 →
///           實際的車道寬（3.8～5.8m）、左右側、隔壁車道在前方多遠斷掉
///   ● 車身：用城市實測的尺寸（切車 prefab 縮放 1.4 倍，車寬 2.8m）
///   ● 鬼切車的控制與安全防護：呼叫 GhostCutInRL，跟城市裡的演員車是同一份程式
///   ● 虛擬玩家：車速 20～85 km/h，每回合隨機一種駕駛風格
///           普通／提早煞車／閃避換道／搶快不讓／遲鈍，反應時間與煞車力也隨機
///   ● 其他車：玩家車道的前車（可能很慢、會突然煞車）、隔壁車道擋在鬼切車前面的車
///
/// 仍然是 Frenet 座標的純數學模擬（不載入城市模型、不靠物理），所以一個場景可以並排 8 組、開 20 倍速。
/// 座標：s = 沿路距離、d = 側向偏移（+ = 右）。d = 0 是玩家原本車道中心，d = adjOffset 是隔壁車道中心。
/// </summary>
public class CutInTrainingArena : MonoBehaviour
{
    public enum StepResult { 進行中, 成功, 碰撞, 放棄, 切入後離開, 車道到底, 取消 }
    public enum EgoStyle { 普通, 提早煞車, 閃避換道, 搶快不讓, 遲鈍 }

    [Header("🗺 城市路網樣本（Tools > 危險駕駛AI > 匯出城市路網訓練樣本）")]
    public CityRoadSamples roadSamples;
    [Tooltip("沒有樣本時的備用車道寬範圍（m）")]
    public Vector2 fallbackLaneWidth = new Vector2(3.8f, 5.8f);

    [Header("🤖 鬼切車（參數與城市共用）")]
    [Tooltip("跟城市觸發器上的 Settings 同一個類別；強化學習相關的欄位請保持一致")]
    public AggressiveDriverSettings settings = new AggressiveDriverSettings();
    [Tooltip("生成在玩家後方幾公尺（城市觸發器 Ghost Spawn Behind = 14）")]
    public Vector2 spawnBehindRange = new Vector2(10f, 18f);
    [Tooltip("生成時比玩家快多少（km/h，城市 = 鬼切 Speed Advantage 15）")]
    public Vector2 spawnSpeedAdvantageKmH = new Vector2(10f, 20f);
    [Tooltip("車身尺寸隨機縮放 ±")]
    [Range(0f, 0.2f)] public float sizeJitter = 0.05f;

    [Header("🚗 虛擬玩家")]
    public Vector2 egoSpeedRangeKmH = new Vector2(20f, 85f);
    [Tooltip("察覺鬼切車靠過來到開始反應的時間（秒）")]
    public Vector2 egoReactionRange = new Vector2(0.5f, 1.3f);
    [Tooltip("最大煞車能力（m/s²，實測真人急煞到 14.8）")]
    public Vector2 egoMaxBrakeRange = new Vector2(6f, 15f);
    [Tooltip("平常偏離車道中心多少（m，實測玩家 d = 0.2～0.8）")]
    public float egoLaneBiasMax = 0.6f;
    [Header("🎭 虛擬玩家風格機率（會自動正規化）")]
    [Min(0f)] public float chanceNormal = 0.35f;
    [Min(0f)] public float chanceEarlyBrake = 0.2f;
    [Min(0f)] public float chanceDodge = 0.15f;
    [Min(0f)] public float chanceNoYield = 0.1f;
    [Min(0f)] public float chanceSluggish = 0.2f;
    [Tooltip("提早煞車：減速度（m/s²）與持續秒數")]
    public Vector2 earlyBrakeDecel = new Vector2(3f, 12f);
    public Vector2 earlyBrakeSeconds = new Vector2(0.6f, 2f);
    [Tooltip("閃避換道：橫移速度（m/s）")]
    public Vector2 dodgeLateralSpeed = new Vector2(1.5f, 3f);
    [Tooltip("搶快不讓：多加多少速度（km/h）")]
    public Vector2 noYieldBoostKmH = new Vector2(8f, 20f);
    [Tooltip("切入前玩家低於這個時速超過 1 秒就取消（城市 Abort If Player Below Km H）")]
    public float cancelBelowKmH = 15f;

    [Header("🚙 其他車")]
    [Range(0f, 1f)] public float leadCarChance = 0.5f;
    [Tooltip("前車在玩家前方幾公尺")]
    public Vector2 leadGapRange = new Vector2(25f, 90f);
    [Tooltip("前車速度 = 玩家巡航速度 × 這個範圍（實測城市前車可能比鬼切車慢 50 km/h 以上）")]
    public Vector2 leadSpeedFactor = new Vector2(0.3f, 1.1f);
    [Range(0f, 1f)] public float leadBrakeChance = 0.2f;
    [Tooltip("隔壁車道擋在鬼切車前面的車")]
    [Range(0f, 1f)] public float adjLeadChance = 0.3f;
    public Vector2 adjLeadGapRange = new Vector2(15f, 80f);
    public Vector2 adjLeadSpeedFactor = new Vector2(0.5f, 1.0f);

    [Header("🎯 任務")]
    [Tooltip("每回合隨機抽的目標減速度範圍（m/s²）")]
    public Vector2 targetDecelRange = new Vector2(1.5f, 4f);
    [Tooltip("評估用：固定目標值。-1 = 每回合隨機")]
    public float fixedTargetDecel = -1f;

    [Header("📊 評估紀錄")]
    public bool writeEvaluationCsv = true;
    [Tooltip("訓練時不寫（訓練回合太多），只在推論／規則式模式寫")]
    public bool skipCsvWhileTraining = true;

    [Header("👀 顯示")]
    public bool buildVisuals = true;

    // ── 模擬狀態（公開給 Agent 讀）──
    [NonSerialized] public float sE, vE, dE;           // 虛擬玩家
    [NonSerialized] public float sA, vA, dA;           // 鬼切學習車
    [NonSerialized] public bool hasLead, hasAdjLead;
    [NonSerialized] public float sL, vL, sQ, vQ;       // 前車（玩家原本車道 d = 0）、隔壁車道的車（d = adjOffset）
    [NonSerialized] public float adjOffset, adjEnd;
    [NonSerialized] public float halfLenA, halfWA, halfLenE, halfWE, halfLenO, halfWO;   // O = 其他車
    [NonSerialized] public float targetDecel, episodeTime;
    [NonSerialized] public bool entered;
    [NonSerialized] public float entryTime, entryGap, maxEgoDecelAfterEntry;
    [NonSerialized] public EgoStyle egoStyle;
    [NonSerialized] public GhostShieldAction lastShield;
    [NonSerialized] public int shieldSteps;

    // ── 虛擬玩家內部 ──
    private IDMParams egoModel;
    private float egoCruise, egoReaction, egoMaxBrake, egoLaneBias, egoWobbleAmp, egoWobblePhase;
    private float egoLaneCenter, egoAccelSm, egoDRateSm, egoAccel;
    private float threatTime, styleT0, earlyDecel, earlyDur, dodgeTarget, dodgeSpeed, noYieldBoost;
    private bool dodgeStarted;
    private float actorLeaderTimer, slowTimer;
    private float leadCruise, leadBrakeAt;

    private Transform egoVis, agentVis, leadVis, adjLeadVis, laneEgoVis, laneAdjVis, laneEndVis;
    private static readonly object csvLock = new object();
    private static bool csvWarned, sampleWarned;

    public string RoadDescription { get; private set; }

    private void Awake()
    {
        if (buildVisuals) BuildVisuals();
        if (roadSamples == null || roadSamples.samples.Count == 0)
        {
            if (!sampleWarned)
            {
                sampleWarned = true;
                Debug.LogWarning("⚠ [鬼切RL] 訓練場沒有指定「城市路網訓練樣本」，改用隨機車道寬。" +
                                 "請先在 cc.unity 執行 Tools > 危險駕駛AI > 匯出城市路網訓練樣本，再執行 Tools > 危險駕駛AI > 訓練場：升級到 v2", this);
            }
        }
        else if (name.EndsWith("01"))
        {
            Debug.Log($"<color=cyan>🗺 [鬼切RL] 訓練場使用城市路網樣本 {roadSamples.samples.Count} 筆（{roadSamples.sourceScene}，{roadSamples.exportedAt} 匯出）、" +
                      $"鬼切車尺寸 {roadSamples.actorSizes.Count} 種、玩家車 {roadSamples.playerSize.halfLength * 2f:F1}×{roadSamples.playerSize.halfWidth * 2f:F1}m</color>", this);
        }
    }

    // ====================================================================
    //  回合
    // ====================================================================

    public void ResetEpisode()
    {
        episodeTime = 0f;
        entered = false;
        entryTime = -1f; entryGap = 0f; maxEgoDecelAfterEntry = 0f;
        shieldSteps = 0;
        lastShield = GhostShieldAction.無;

        // ── 道路（城市樣本）──
        CityRoadSamples.RoadSample rs = roadSamples != null ? roadSamples.RandomSample() : null;
        if (rs != null)
        {
            adjOffset = rs.adjOffset;
            adjEnd = rs.adjEndFromPlayer >= 900f ? 1e4f : rs.adjEndFromPlayer;
        }
        else
        {
            adjOffset = UnityEngine.Random.Range(fallbackLaneWidth.x, fallbackLaneWidth.y) * (UnityEngine.Random.value < 0.5f ? -1f : 1f);
            adjEnd = UnityEngine.Random.value < 0.8f ? 1e4f : UnityEngine.Random.Range(60f, 200f);
        }

        // ── 車身（城市實測）──
        float J() { return 1f + UnityEngine.Random.Range(-sizeJitter, sizeJitter); }
        CityRoadSamples.CarSize a = roadSamples != null ? roadSamples.RandomActorSize() : null;
        halfLenA = (a != null ? a.halfLength : 2.8f) * J();
        halfWA = (a != null ? a.halfWidth : 1.4f) * J();
        halfLenE = (roadSamples != null ? roadSamples.playerSize.halfLength : 2.3f) * J();
        halfWE = (roadSamples != null ? roadSamples.playerSize.halfWidth : 1.1f) * J();
        CityRoadSamples.CarSize o = roadSamples != null ? roadSamples.RandomActorSize() : null;
        halfLenO = (o != null ? o.halfLength : 2.4f) * J();
        halfWO = (o != null ? o.halfWidth : 1.1f) * J();

        // ── 虛擬玩家 ──
        egoModel = IDMParams.NormalDriver();
        egoModel.timeHeadway = UnityEngine.Random.Range(0.9f, 1.6f);
        egoModel.maxAccel = UnityEngine.Random.Range(1.2f, 2.5f);
        egoCruise = UnityEngine.Random.Range(egoSpeedRangeKmH.x, egoSpeedRangeKmH.y) / 3.6f;
        vE = egoCruise;
        sE = 0f;
        egoLaneCenter = 0f;
        egoLaneBias = UnityEngine.Random.Range(-egoLaneBiasMax, egoLaneBiasMax);
        egoWobbleAmp = UnityEngine.Random.Range(0f, 0.3f);
        egoWobblePhase = UnityEngine.Random.value * 10f;
        dE = egoLaneBias;
        egoAccel = 0f; egoAccelSm = 0f; egoDRateSm = 0f;
        egoStyle = PickStyle();
        egoReaction = UnityEngine.Random.Range(egoReactionRange.x, egoReactionRange.y);
        egoMaxBrake = UnityEngine.Random.Range(egoMaxBrakeRange.x, egoMaxBrakeRange.y);
        if (egoStyle == EgoStyle.遲鈍)
        {
            egoReaction = UnityEngine.Random.Range(1.8f, 3f);
            egoMaxBrake = UnityEngine.Random.Range(4f, 6f);
        }
        earlyDecel = UnityEngine.Random.Range(earlyBrakeDecel.x, earlyBrakeDecel.y);
        earlyDur = UnityEngine.Random.Range(earlyBrakeSeconds.x, earlyBrakeSeconds.y);
        dodgeSpeed = UnityEngine.Random.Range(dodgeLateralSpeed.x, dodgeLateralSpeed.y);
        // 閃避：一半閃進鬼切車原本的車道（實測真的有人這樣閃），一半往另一側閃
        dodgeTarget = UnityEngine.Random.value < 0.5f ? adjOffset : -Mathf.Sign(adjOffset) * Mathf.Min(Mathf.Abs(adjOffset), 4f);
        noYieldBoost = UnityEngine.Random.Range(noYieldBoostKmH.x, noYieldBoostKmH.y) / 3.6f;
        threatTime = -1f; styleT0 = -1f; dodgeStarted = false;
        actorLeaderTimer = 0f; slowTimer = 0f;

        // ── 鬼切車 ──
        sA = sE - UnityEngine.Random.Range(spawnBehindRange.x, spawnBehindRange.y);
        dA = adjOffset + UnityEngine.Random.Range(-0.3f, 0.3f);
        vA = vE + UnityEngine.Random.Range(spawnSpeedAdvantageKmH.x, spawnSpeedAdvantageKmH.y) / 3.6f;

        // ── 其他車 ──
        hasLead = UnityEngine.Random.value < leadCarChance;
        sL = sE + UnityEngine.Random.Range(leadGapRange.x, leadGapRange.y);
        leadCruise = egoCruise * UnityEngine.Random.Range(leadSpeedFactor.x, leadSpeedFactor.y);
        vL = leadCruise;
        leadBrakeAt = UnityEngine.Random.value < leadBrakeChance ? UnityEngine.Random.Range(2f, 15f) : -1f;
        hasAdjLead = UnityEngine.Random.value < adjLeadChance;
        sQ = sA + UnityEngine.Random.Range(adjLeadGapRange.x, adjLeadGapRange.y);
        vQ = egoCruise * UnityEngine.Random.Range(adjLeadSpeedFactor.x, adjLeadSpeedFactor.y);

        targetDecel = fixedTargetDecel > 0f ? fixedTargetDecel
                    : UnityEngine.Random.Range(targetDecelRange.x, targetDecelRange.y);

        RoadDescription = $"車道寬 {Mathf.Abs(adjOffset):F1}m（{(adjOffset > 0 ? "右" : "左")}側）" +
                          (adjEnd < 1000f ? $"、隔壁車道 {adjEnd:F0}m 後結束" : "") +
                          $"、玩家 {egoCruise * 3.6f:F0} km/h {egoStyle}";
        UpdateVisuals(true);
    }

    private EgoStyle PickStyle()
    {
        float total = chanceNormal + chanceEarlyBrake + chanceDodge + chanceNoYield + chanceSluggish;
        float r = UnityEngine.Random.value * Mathf.Max(total, 0.0001f);
        if ((r -= chanceNormal) < 0f) return EgoStyle.普通;
        if ((r -= chanceEarlyBrake) < 0f) return EgoStyle.提早煞車;
        if ((r -= chanceDodge) < 0f) return EgoStyle.閃避換道;
        if ((r -= chanceNoYield) < 0f) return EgoStyle.搶快不讓;
        return EgoStyle.遲鈍;
    }

    // ====================================================================
    //  狀態 → GhostCutInRL（跟城市演員車用同一個結構）
    // ====================================================================

    public GhostRLState BuildState()
    {
        var x = new GhostRLState
        {
            s = sA, d = dA, v = vA,
            sp = sE, dp = dE, vp = vE,
            dpRate = egoDRateSm, playerAccel = egoAccelSm,
            adjOffset = adjOffset, adjEnd = adjEnd,
            halfLen = halfLenA, halfWidth = halfWA,
            playerHalfLen = halfLenE, playerHalfWidth = halfWE,
            target = targetDecel,
            elapsed = episodeTime
        };

        // 玩家正前方的車（城市：從玩家車頭往前 80m 的球形射線，半徑 0.9m）
        float best = float.MaxValue;
        if (hasLead && sL > sE && Mathf.Abs(0f - dE) < 0.9f + halfWO && sL - sE < 80f + halfLenE + halfLenO) { best = sL; x.hasLead = true; x.leadS = sL; x.leadV = vL; }
        if (hasAdjLead && sQ > sE && sQ < best && Mathf.Abs(adjOffset - dE) < 0.9f + halfWO && sQ - sE < 80f + halfLenE + halfLenO) { x.hasLead = true; x.leadS = sQ; x.leadV = vQ; }

        // 鬼切車正前方的車，不含玩家（城市：ProbeForward，半徑 0.9m，Forward Probe Distance）
        float bestGap = float.MaxValue;
        float probe = settings.forwardProbeDistance;
        ProbeObject(hasLead, sL, 0f, vL, probe, ref bestGap, ref x);
        ProbeObject(hasAdjLead, sQ, adjOffset, vQ, probe, ref bestGap, ref x);
        return x;
    }

    private void ProbeObject(bool exists, float sObj, float dObj, float vObj, float probe, ref float bestGap, ref GhostRLState x)
    {
        if (!exists || sObj <= sA) return;
        if (Mathf.Abs(dObj - dA) > 0.9f + halfWO) return;
        float gap = sObj - halfLenO - (sA + halfLenA - 0.5f);
        if (gap > probe || gap >= bestGap) return;
        bestGap = gap;
        x.hasFront = true; x.frontGap = Mathf.Max(0f, gap); x.frontV = vObj;
    }

    // ====================================================================
    //  模擬一步
    // ====================================================================

    /// <summary>推進一步。cmdA、cmdL ∈ [−1, 1]（cmdL + = 往玩家車道）</summary>
    public StepResult Step(float cmdA, float cmdL, float dt)
    {
        episodeTime += dt;

        // ── 鬼切車：跟城市同一套「模型輸出 → 加速度／橫移 ＋ 安全防護」──
        GhostRLState x = BuildState();
        float idm = GhostCutInRL.IdmShield(x, settings);
        float accA, latA; string msg;
        lastShield = GhostCutInRL.ApplyAction(x, settings, cmdA, cmdL, idm, entered, dt, out accA, out latA, out msg);
        if (lastShield != GhostShieldAction.無) shieldSteps++;
        accA = Mathf.Clamp(accA, -settings.maxBrake, settings.aggressiveModel.maxAccel);   // 城市演員車同樣的上下限
        vA = Mathf.Clamp(vA + accA * dt, 0f, 45f);
        sA += vA * dt;
        dA += latA * dt;
        dA = Mathf.Clamp(dA, Mathf.Min(0f, adjOffset) - 1f, Mathf.Max(0f, adjOffset) + 1f);   // 城市一樣夾在兩條車道內

        // ── 其他車 ──
        if (hasLead)
        {
            if (leadBrakeAt > 0f && episodeTime >= leadBrakeAt && vL > leadCruise * 0.5f) vL = Mathf.Max(0f, vL - 3f * dt);
            sL += vL * dt;
        }
        if (hasAdjLead) sQ += vQ * dt;

        StepEgo(dt);

        // ── 判定 ──
        x = BuildState();
        StepResult r = StepResult.進行中;
        if (Overlap(sE, dE, halfLenE, halfWE) ||
            (hasLead && Overlap(sL, 0f, halfLenO, halfWO)) ||
            (hasAdjLead && Overlap(sQ, adjOffset, halfLenO, halfWO)))
            r = StepResult.碰撞;
        else if (!entered)
        {
            slowTimer = vE * 3.6f < cancelBelowKmH ? slowTimer + dt : 0f;
            if (slowTimer > 1f) r = StepResult.取消;
            else if (episodeTime > GhostCutInRL.ApproachTimeLimit) r = StepResult.放棄;
            else if (GhostCutInRL.RearGap(x) > settings.giveUpAheadGap) r = StepResult.放棄;
            else if (GhostCutInRL.InAdjacentLane(x) && sA + halfLenA > adjEnd) r = StepResult.車道到底;
            else if (GhostCutInRL.Entered(x))
            {
                entered = true;
                entryTime = episodeTime;
                entryGap = GhostCutInRL.RearGap(x);
            }
        }
        else
        {
            maxEgoDecelAfterEntry = Mathf.Max(maxEgoDecelAfterEntry, -egoAccel);
            if (GhostCutInRL.LeftAfterEntry(x)) r = StepResult.切入後離開;
            else if (episodeTime - entryTime >= settings.rlObserveSeconds) r = StepResult.成功;
        }

        UpdateVisuals(false);
        return r;
    }

    private bool Overlap(float sObj, float dObj, float hl, float hw)
    {
        return Mathf.Abs(sA - sObj) < halfLenA + hl && Mathf.Abs(dA - dObj) < halfWA + hw;
    }

    /// <summary>虛擬玩家：IDM 跟車 ＋ 依風格對鬼切車做出反應</summary>
    private void StepEgo(float dt)
    {
        // 察覺：鬼切車離開自己車道中心 1m 以上往玩家靠，而且前後距離在 −15～+25m
        float rel = sA - sE;
        bool encroach = Mathf.Abs(dA - dE) < Mathf.Abs(adjOffset) - 1f && rel > -15f && rel < 25f;
        if (threatTime < 0f && encroach) threatTime = episodeTime;
        bool reacting = threatTime >= 0f && episodeTime - threatTime >= egoReaction;
        if (reacting && styleT0 < 0f) styleT0 = episodeTime;
        float sinceReact = styleT0 >= 0f ? episodeTime - styleT0 : -1f;

        // 跟車對象：前方、側向重疊的車（鬼切車要「在前面持續一段反應時間」才當前車）
        float gap = float.PositiveInfinity, leaderV = 0f;
        Leader(hasLead, sL, 0f, vL, halfLenO, halfWO, ref gap, ref leaderV);
        Leader(hasAdjLead, sQ, adjOffset, vQ, halfLenO, halfWO, ref gap, ref leaderV);
        bool actorAhead = sA > sE && Mathf.Abs(dA - dE) < halfWA + halfWE + 0.3f;
        actorLeaderTimer = actorAhead ? actorLeaderTimer + dt : 0f;
        if (actorLeaderTimer >= egoReaction) Leader(true, sA, dA, vA, halfLenA, halfWA, ref gap, ref leaderV);

        float desired = egoCruise;
        float maxAcc = egoModel.maxAccel;
        if (egoStyle == EgoStyle.搶快不讓 && sinceReact >= 0f && sinceReact < 4f) { desired = egoCruise + noYieldBoost; maxAcc = 2.5f; }

        float a = DrivingModel.IdmAccel(vE, desired, gap, vE - leaderV, egoModel);
        if (egoStyle == EgoStyle.搶快不讓 && sinceReact >= 0f && sinceReact < 4f && gap > 10f) a = Mathf.Max(a, 1.5f);   // 前面有空間就硬加速不讓切
        if (egoStyle == EgoStyle.提早煞車 && sinceReact >= 0f && sinceReact < earlyDur) a = Mathf.Min(a, -earlyDecel);
        a = Mathf.Clamp(a, -egoMaxBrake, maxAcc);
        egoAccel = a;
        vE = Mathf.Max(0f, vE + a * dt);
        sE += vE * dt;

        // 側向：平常在車道中心附近小幅飄移；閃避風格反應後換到另一條車道
        if (egoStyle == EgoStyle.閃避換道 && reacting && !dodgeStarted)
        {
            dodgeStarted = true;
            // 鬼切車就在旁邊時，真人不會往它身上閃 → 改往另一側
            bool alongside = Mathf.Abs(sA - sE) < halfLenA + halfLenE + 3f;
            if (alongside && Mathf.Approximately(dodgeTarget, adjOffset)) dodgeTarget = -Mathf.Sign(adjOffset) * Mathf.Min(Mathf.Abs(adjOffset), 4f);
            egoLaneCenter = dodgeTarget;
        }
        float targetD = egoLaneCenter + (dodgeStarted ? 0f : egoLaneBias) + egoWobbleAmp * Mathf.Sin(episodeTime * 0.35f + egoWobblePhase);
        float latSpeed = dodgeStarted ? dodgeSpeed : 0.8f;
        float prevD = dE;
        dE = Mathf.MoveTowards(dE, targetD, latSpeed * dt);

        // 給模型看的量（跟城市一樣做指數平滑）
        egoDRateSm = Mathf.Lerp(egoDRateSm, (dE - prevD) / Mathf.Max(dt, 0.0001f), 0.2f);
        egoAccelSm = Mathf.Lerp(egoAccelSm, a, 0.2f);
    }

    private void Leader(bool exists, float sObj, float dObj, float vObj, float hl, float hw, ref float gap, ref float leaderV)
    {
        if (!exists || sObj <= sE) return;
        if (Mathf.Abs(dObj - dE) > hw + halfWE + 0.3f) return;
        float g = sObj - sE - halfLenE - hl;
        if (g < gap) { gap = g; leaderV = vObj; }
    }

    /// <summary>
    /// 成功回合的得分：玩家最大減速度越接近目標越高（0～1）。
    /// v5 改成指數衰減：誤差 0 → 1、1 → 0.51、2 → 0.26、4.5 → 0.05。
    /// 舊版線性公式在誤差超過目標值時直接變 0，v2～v4 模型的誤差都在 2.5～4.5，
    /// 落在「怎麼改都是 0 分」的平原上，完全學不到要往哪個方向修
    /// </summary>
    public float PrecisionScore()
    {
        return Mathf.Exp(-Mathf.Abs(maxEgoDecelAfterEntry - targetDecel) / 1.5f);
    }

    // ====================================================================
    //  評估 CSV
    // ====================================================================

    public void LogEpisode(string mode, StepResult result, float totalReward)
    {
        if (!writeEvaluationCsv) return;
        if (skipCsvWhileTraining && mode == "訓練中") return;
        try
        {
            string path = Path.Combine(Application.persistentDataPath, "危險駕駛AI_強化學習評估_v2.csv");
            lock (csvLock)
            {
                bool newFile = !File.Exists(path);
                var sb = new StringBuilder();
                if (newFile) sb.AppendLine("時間,模式,訓練場,結果,玩家風格,玩家時速,車道寬,隔壁車道結束,目標減速度,玩家實際最大減速度,誤差,切入間距,防護介入步數,回合秒數,總獎勵");
                sb.AppendLine(string.Join(",", new[]
                {
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), mode, name, result.ToString(), egoStyle.ToString(),
                    (egoCruise * 3.6f).ToString("F0"), Mathf.Abs(adjOffset).ToString("F1"), adjEnd < 1000f ? adjEnd.ToString("F0") : "",
                    targetDecel.ToString("F2"), maxEgoDecelAfterEntry.ToString("F2"),
                    result == StepResult.成功 ? Mathf.Abs(maxEgoDecelAfterEntry - targetDecel).ToString("F2") : "",
                    entered ? entryGap.ToString("F2") : "", shieldSteps.ToString(),
                    episodeTime.ToString("F1"), totalReward.ToString("F3")
                }));
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(newFile));
            }
        }
        catch (Exception e)
        {
            if (csvWarned) return;
            csvWarned = true;
            Debug.LogWarning("[鬼切RL] 評估 CSV 寫入失敗（之後不再重複提示）：" + e.Message +
                             "\n→ 最常見原因：這個 CSV 正被 Excel 開著。請先關閉 Excel 再按 Play", this);
        }
    }

    // ====================================================================
    //  顯示（純方塊，不影響模擬）
    // ====================================================================

    private void BuildVisuals()
    {
        laneEgoVis = MakeBox("玩家車道", new Color(0.25f, 0.25f, 0.28f), Vector3.one, -0.05f);
        laneAdjVis = MakeBox("隔壁車道", new Color(0.3f, 0.3f, 0.34f), Vector3.one, -0.05f);
        laneEndVis = MakeBox("隔壁車道終點", new Color(0.9f, 0.2f, 0.2f), Vector3.one, 0.05f);
        egoVis = MakeBox("虛擬玩家", new Color(0.2f, 0.5f, 1f), Vector3.one, 0.7f);
        agentVis = MakeBox("鬼切學習車", new Color(1f, 0.3f, 0.2f), Vector3.one, 0.7f);
        leadVis = MakeBox("前車", new Color(0.6f, 0.6f, 0.6f), Vector3.one, 0.7f);
        adjLeadVis = MakeBox("隔壁車道的車", new Color(0.5f, 0.5f, 0.45f), Vector3.one, 0.7f);
    }

    private Transform MakeBox(string n, Color c, Vector3 size, float y)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = n;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);   // 模擬不靠物理
        go.transform.SetParent(transform, false);
        go.transform.localScale = size;
        go.transform.localPosition = new Vector3(0f, y, 0f);
        var r = go.GetComponent<Renderer>();
        if (r != null) r.material.color = c;
        return go.transform;
    }

    /// <summary>以玩家車為中心畫。本地座標：x = 側向 d，z = 前後 s</summary>
    private void UpdateVisuals(bool resize)
    {
        if (egoVis == null) return;
        float W = Mathf.Abs(adjOffset);
        if (resize)
        {
            laneEgoVis.localScale = new Vector3(W - 0.15f, 0.1f, 160f);
            laneEgoVis.localPosition = new Vector3(0f, -0.05f, 0f);
            laneAdjVis.localScale = new Vector3(W - 0.15f, 0.1f, 160f);
            egoVis.localScale = new Vector3(halfWE * 2f, 1.4f, halfLenE * 2f);
            agentVis.localScale = new Vector3(halfWA * 2f, 1.4f, halfLenA * 2f);
            leadVis.localScale = new Vector3(halfWO * 2f, 1.4f, halfLenO * 2f);
            adjLeadVis.localScale = new Vector3(halfWO * 2f, 1.4f, halfLenO * 2f);
            laneEndVis.localScale = new Vector3(W, 0.2f, 0.6f);
        }
        laneAdjVis.localPosition = new Vector3(adjOffset, -0.05f, 0f);
        egoVis.localPosition = new Vector3(dE, 0.7f, 0f);
        agentVis.localPosition = new Vector3(dA, 0.7f, sA - sE);
        leadVis.gameObject.SetActive(hasLead);
        if (hasLead) leadVis.localPosition = new Vector3(0f, 0.7f, sL - sE);
        adjLeadVis.gameObject.SetActive(hasAdjLead);
        if (hasAdjLead) adjLeadVis.localPosition = new Vector3(adjOffset, 0.7f, sQ - sE);
        bool showEnd = adjEnd < 1000f && Mathf.Abs(adjEnd - sE) < 80f;
        laneEndVis.gameObject.SetActive(showEnd);
        if (showEnd) laneEndVis.localPosition = new Vector3(adjOffset, 0.05f, adjEnd - sE);
    }
}
