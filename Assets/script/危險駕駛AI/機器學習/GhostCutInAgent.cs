using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

/// <summary>
/// 🤖 鬼切強化學習車 v2（Unity ML-Agents，PPO 演算法）
///
/// 觀測（21 個，由 GhostCutInRL.FillObservations 計算 —— 城市演員車呼叫的是同一個函式）：
///   速度、前後距離、側向位置（車道單位）、前車、「現在切進去玩家要煞多重」、目標減速度、剩餘時間、
///   車道寬、安全側距、玩家側向速度（閃避）、玩家加速度（提早煞車）、隔壁車道剩多長、車尾間距、自己正前方的車
/// 動作（2 個連續值 −1～1）：油門／煞車、橫移（+ = 往玩家車道，跟左右側無關）
///   動作一律先經過 GhostCutInRL.ApplyAction 的安全防護，城市裡也是 —— 模型就是在「有防護」的情況下學會開車
/// 獎勵：
///   ＋0.3 ＋ 1.2 × 精準度   成功切入並觀察完（精準度 = exp(−|玩家實際最大減速度 − 目標| / 1.5)）
///                          v3 加大精準度權重：v2 模型成功率 72% 但不管目標多少都讓玩家煞到約 4.5 m/s²（平均多 2.3）
///   −1                     碰撞（玩家、前車、隔壁車道的車）
///   −0.5                   放棄（25 秒還沒切、超前太遠）、隔壁車道到底還沒切
///   −0.3                   切進去又跑出來
///   −0.1                   玩家自己慢下來（城市會取消演出）
///   −0.001／步             安全防護介入（鼓勵模型自己就開得安全，不要靠防護擋）
///   −0.0005／步            時間成本
///   ＋位能塑形              切入前往「玩家前方、同車道」靠近時給一點點引導；切入後不再引導
///                          （v4 修正：v2、v3 切入後仍獎勵「貼近玩家車頭 4m」，等於鼓勵切入後猛踩煞車讓玩家追上來，
///                            實測城市裡切入後一路煞到 0 km/h，玩家被迫煞到 12 m/s²，完全不管目標強度）
///
/// Heuristic（沒有模型時）＝ 規則式基準：IDM ＋「需要的減速度 ≤ 目標才切」，跟城市演員車同一套邏輯。
/// 把 Behavior Type 切成 Heuristic Only 就能在同一批隨機情境下跑「規則式」做對照實驗。
/// </summary>
public class GhostCutInAgent : Agent
{
    [Tooltip("所屬訓練場（留空會找父物件）")]
    public CutInTrainingArena arena;

    [Header("規則式基準（Heuristic）參數")]
    public float heuristicSpeedAdvantageKmH = 15f;
    public float heuristicMinCutGap = 3f;

    private float lastPotential;
    private float episodeReward;
    private BehaviorParameters behavior;
    private readonly float[] obs = new float[GhostCutInRL.ObservationSize];

    public override void Initialize()
    {
        if (arena == null) arena = GetComponentInParent<CutInTrainingArena>();
        behavior = GetComponent<BehaviorParameters>();
        if (MaxStep <= 0) MaxStep = 1500;   // 50Hz × 30 秒
        if (behavior != null && behavior.BrainParameters.VectorObservationSize != GhostCutInRL.ObservationSize)
            Debug.LogError($"[鬼切RL] Behavior Parameters 的 Vector Observation Size 是 {behavior.BrainParameters.VectorObservationSize}，" +
                           $"v2 需要 {GhostCutInRL.ObservationSize}。請執行 Tools > 危險駕駛AI > 訓練場：升級到 v2", this);
    }

    public override void OnEpisodeBegin()
    {
        arena.ResetEpisode();
        lastPotential = Potential();
        episodeReward = 0f;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        GhostCutInRL.FillObservations(arena.BuildState(), arena.settings, obs);
        for (int i = 0; i < obs.Length; i++) sensor.AddObservation(obs[i]);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        var ca = actions.ContinuousActions;
        CutInTrainingArena.StepResult r = arena.Step(ca[0], ca[1], Time.fixedDeltaTime);

        float reward = -0.0005f;
        if (arena.lastShield != GhostShieldAction.無) reward -= 0.001f;
        float p = Potential();
        reward += p - lastPotential;
        lastPotential = p;

        switch (r)
        {
            case CutInTrainingArena.StepResult.成功: reward += 0.3f + 1.2f * arena.PrecisionScore(); break;   // v4：成功底分再降，精準度佔大部分
            case CutInTrainingArena.StepResult.碰撞: reward += -1f; break;
            case CutInTrainingArena.StepResult.放棄: reward += -0.5f; break;
            case CutInTrainingArena.StepResult.車道到底: reward += -0.5f; break;
            case CutInTrainingArena.StepResult.切入後離開: reward += -0.3f; break;
            case CutInTrainingArena.StepResult.取消: reward += -0.1f; break;
        }
        AddReward(reward);
        episodeReward += reward;

        if (r != CutInTrainingArena.StepResult.進行中)
        {
            arena.LogEpisode(ModeName(), r, episodeReward);
            EndEpisode();
        }
    }

    /// <summary>規則式基準：跟城市演員車（AggressiveDriverActor）的鬼切邏輯相同</summary>
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        GhostRLState x = arena.BuildState();
        AggressiveDriverSettings cfg = arena.settings;

        // 縱向：IDM，期望速度 = 玩家 + 優勢（前車互動由 ApplyAction 裡的 IDM 防護負責）
        float a;
        if (arena.entered)
        {
            // 切入後：依目標強度踩煞車（目標越大煞越重），模仿演員車的「切入後煞車」
            a = -Mathf.Lerp(2f, 7f, Mathf.InverseLerp(1.5f, 4f, arena.targetDecel));
            if (x.v < x.vp * 0.4f) a = 0f;
        }
        else a = DrivingModel.IdmAccel(x.v, x.vp + heuristicSpeedAdvantageKmH / 3.6f, float.PositiveInfinity, 0f, cfg.aggressiveModel);
        ca[0] = a >= 0f ? a / cfg.rlMaxAccel : a / cfg.rlMaxBrake;

        // 橫向（車道單位：1 = 隔壁車道中心、0 = 玩家原本車道中心）：時機成立就切向玩家，否則待在自己車道
        float adj = Mathf.Abs(x.adjOffset) < 1f ? 1f : x.adjOffset;
        float u = x.d / adj;
        bool cut = arena.entered || (GhostCutInRL.RearGap(x) >= heuristicMinCutGap && GhostCutInRL.RequiredNow(x, cfg) <= arena.targetDecel);
        float uTarget = cut ? x.dp / adj : 1f;
        ca[1] = Mathf.Clamp((u - uTarget) * 1.5f, -1f, 1f);   // + = 往玩家車道（u 變小）
    }

    /// <summary>
    /// 位能（只引導方向，差分當獎勵）：
    ///   還沒超過玩家 → 越往前越高
    ///   已經超過玩家 → 「現在切進去玩家要煞多重」越接近目標越高，接近目標時再加一點「往玩家車道靠」
    ///   切入後 → 常數（之後只看實際精準度）
    /// v5 修正：v2～v4 引導的是「玩家前方 4m」這個固定位置，模型就照著在 4m 切進去
    /// （實測切入間距中位數 4.6m），玩家不管目標多少都被迫重煞 —— 引導本身就是錯的
    /// </summary>
    private float Potential()
    {
        if (arena.entered) return 0.1f;
        GhostRLState x = arena.BuildState();
        float rear = GhostCutInRL.RearGap(x);
        if (rear < 0f) return -0.2f * Mathf.Clamp01(-rear / 40f) - 0.1f;
        float miss = Mathf.Abs(GhostCutInRL.PredictedPlayerDecel(x, arena.settings) - arena.targetDecel);   // v6：用預測的實際煞車強度
        float p = -0.1f * Mathf.Clamp01(miss / 4f);
        if (miss < 1f)
            p += 0.05f * (1f - Mathf.Clamp01(Mathf.Abs(x.d - x.dp) / Mathf.Max(Mathf.Abs(x.adjOffset), 1f)));
        return p;
    }

    private string ModeName()
    {
        if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn) return "訓練中";
        if (behavior != null && behavior.IsInHeuristicMode()) return "規則式基準";
        // Behavior Type = Default 但沒連上訓練器、也沒放模型時，ML-Agents 會自動退回 Heuristic，實際跑的是規則式
        if (behavior != null && behavior.Model == null) return "規則式基準（未連上訓練器）";
        return "強化學習模型";
    }
}
