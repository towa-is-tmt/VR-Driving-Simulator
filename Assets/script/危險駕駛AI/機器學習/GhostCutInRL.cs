using UnityEngine;

/// <summary>
/// 🤖 鬼切強化學習在某一瞬間的狀態，城市和訓練場共用。
///
/// 座標跟演員車一樣用 Frenet：s = 沿路距離，d = 側向偏移（+ = 右）。
/// d = 0 是玩家「原本」車道的中心，d = adjOffset 是鬼切車出發的隔壁車道中心。
/// </summary>
public struct GhostRLState
{
    // ── 鬼切車 ──
    public float s, d, v;

    // ── 玩家車 ──
    public float sp, dp, vp;
    /// <summary>玩家側向速度（m/s，+ = 往右）。用來察覺玩家在換道閃避</summary>
    public float dpRate;
    /// <summary>玩家縱向加速度（m/s²，負 = 煞車）。用來察覺玩家提早煞車</summary>
    public float playerAccel;

    // ── 道路 ──
    /// <summary>隔壁車道中心的 d（帶號：負 = 左側、正 = 右側）</summary>
    public float adjOffset;
    /// <summary>隔壁車道在 s = adjEnd 處結束（沒有結束就給很大的數字）</summary>
    public float adjEnd;

    // ── 玩家車道的前車 ──
    public bool hasLead;
    /// <summary>前車中心的 s 與速度</summary>
    public float leadS, leadV;

    // ── 鬼切車正前方的其他車（不含玩家；城市用前方偵測射線，訓練場用模擬車）──
    public bool hasFront;
    /// <summary>車頭到那台車車尾的距離（m）與那台車的速度</summary>
    public float frontGap, frontV;

    // ── 車身尺寸（實測）──
    public float halfLen, halfWidth, playerHalfLen, playerHalfWidth;

    // ── 任務 ──
    /// <summary>這次要讓玩家踩到多重的煞車（m/s²）</summary>
    public float target;
    /// <summary>演出開始到現在幾秒</summary>
    public float elapsed;
}

/// <summary>模型的動作被安全防護改掉的方式</summary>
public enum GhostShieldAction { 無, 暫停靠近, 往外讓 }

/// <summary>
/// 🤖 鬼切強化學習的核心，城市和訓練場用的是「同一份」程式。
///
/// 第一版模型的教訓：訓練場跟城市各寫一套，觀測、橫移、防護有細微差異，
/// 模型在城市裡一碰到沒練過的情況就亂做。所以這一版把下面這些全部集中在這裡，兩邊都呼叫它：
///   ● 21 個觀測值的計算與正規化
///   ● 模型輸出 → 實際加速度、橫移速度
///   ● 安全防護（IDM 跟車 ＋ 橫向防護）
///   ● 「已切入」「切入後又離開」的判定
/// 訓練時模型就是在「有防護」的情況下學的，到了城市行為才會一致。
/// </summary>
public static class GhostCutInRL
{
    /// <summary>觀測值數量（訓練場 Behavior Parameters 的 Vector Observation Size 必須一樣）</summary>
    public const int ObservationSize = 21;
    public const int ActionSize = 2;

    /// <summary>切入前最多接近幾秒，超過就放棄（城市與訓練場相同）</summary>
    public const float ApproachTimeLimit = 25f;
    /// <summary>鬼切車中心離玩家中心的側向距離小於這個值 → 算切進玩家車道</summary>
    public const float EnterLateral = 0.6f;
    /// <summary>切入後側向距離又大於這個值 → 算「切進去又離開」</summary>
    public const float LeaveLateral = 1.8f;

    // ====================================================================
    //  幾何
    // ====================================================================

    /// <summary>鬼切車車尾到玩家車頭（正 = 鬼切車已經在玩家前方）</summary>
    public static float RearGap(in GhostRLState x) { return (x.s - x.sp) - x.halfLen - x.playerHalfLen; }

    /// <summary>鬼切車車頭到玩家車尾（正 = 鬼切車還在玩家後方）</summary>
    public static float FrontGap(in GhostRLState x) { return (x.sp - x.s) - x.halfLen - x.playerHalfLen; }

    /// <summary>還沒超過玩家時，兩車中心至少要保持的橫向距離</summary>
    public static float SideClearance(in GhostRLState x, AggressiveDriverSettings cfg)
    {
        return Mathf.Max(cfg.rlSideClearance, x.halfWidth + x.playerHalfWidth + cfg.rlSideMargin);
    }

    /// <summary>如果現在切進去，玩家反應過來後要煞多重才不會撞（m/s²）。還沒超過玩家 → 8</summary>
    public static float RequiredNow(in GhostRLState x, AggressiveDriverSettings cfg)
    {
        float rear = RearGap(x);
        if (rear <= 0f) return 8f;
        return DrivingModel.RequiredDecelToAvoid(rear, x.vp, x.v, 0f, 0f, 0f, 0f, cfg.playerReactionTime, 1f);
    }

    /// <summary>
    /// 預測「現在切進去，玩家實際會煞多重」（m/s²）＝ 運動學需要的減速度 與 一般駕駛（IDM）跟車反應 取大者。
    /// v6 新增：v2～v5 只用運動學公式，鬼切車比玩家快時，切在玩家車頭前 4m 也算出「不用煞」，
    /// 但真實駕駛（與訓練場的虛擬玩家）看到前面 4m 突然多一台車會重煞 7～11 m/s²。
    /// 模型照著錯的預測一直切在 4.5m，切入強度永遠比目標多 4.5 m/s²
    /// </summary>
    public static float PredictedPlayerDecel(in GhostRLState x, AggressiveDriverSettings cfg)
    {
        float rear = RearGap(x);
        if (rear <= 0f) return 8f;
        float idm = DrivingModel.ImposedDecel(x.vp, x.v, rear, cfg.playerReferenceModel);
        return Mathf.Clamp(Mathf.Max(RequiredNow(x, cfg), idm), 0f, 15f);
    }

    public static bool Entered(in GhostRLState x) { return Mathf.Abs(x.d - x.dp) < EnterLateral && RearGap(x) > 0f; }
    public static bool LeftAfterEntry(in GhostRLState x) { return Mathf.Abs(x.d - x.dp) > LeaveLateral; }

    /// <summary>還在自己的隔壁車道上（離隔壁車道中心不到半個車道）</summary>
    public static bool InAdjacentLane(in GhostRLState x)
    {
        return Mathf.Abs(x.d - x.adjOffset) < Mathf.Abs(x.adjOffset) * 0.5f;
    }

    // ====================================================================
    //  觀測值（21 個，全部正規化到大約 −2～2）
    // ====================================================================

    /// <summary>
    /// 側向一律換算成「車道單位」：1 = 隔壁車道中心、0 = 玩家原本車道中心，
    /// 所以從左側或右側出發，模型看到的數字一樣，不用分開學。
    /// </summary>
    public static void FillObservations(in GhostRLState x, AggressiveDriverSettings cfg, float[] o)
    {
        float adj = Mathf.Abs(x.adjOffset) < 1f ? (x.adjOffset < 0f ? -1f : 1f) : x.adjOffset;
        float W = Mathf.Abs(adj);

        o[0] = x.v / 30f;                                                     // 自己速度
        o[1] = x.vp / 30f;                                                    // 玩家速度
        o[2] = Mathf.Clamp((x.s - x.sp) / 50f, -1.5f, 1.5f);                  // 前後距離（中心對中心）
        o[3] = Mathf.Clamp(x.d / adj, -1.5f, 2f);                             // 自己側向位置（車道單位）
        o[4] = Mathf.Clamp(x.dp / adj, -1.5f, 2f);                            // 玩家側向位置（車道單位）
        o[5] = Mathf.Clamp((x.v - x.vp) / 10f, -2f, 2f);                      // 相對速度
        o[6] = x.hasLead ? 1f : 0f;                                           // 玩家車道有沒有前車
        o[7] = x.hasLead ? Mathf.Clamp((x.leadS - x.s) / 80f, -1.5f, 1.5f) : 1f;
        o[8] = x.hasLead ? Mathf.Clamp((x.leadV - x.v) / 10f, -3f, 3f) : 0f;
        o[9] = Mathf.Clamp(PredictedPlayerDecel(x, cfg) / 8f, 0f, 2f);        // 現在切進去，預測玩家會煞多重
        o[10] = x.target / 8f;                                                // 這次的目標減速度
        o[11] = Mathf.Clamp01(1f - x.elapsed / ApproachTimeLimit);            // 剩餘時間
        o[12] = W / 5f;                                                       // 車道寬（城市實測 3.8～5.8m）
        o[13] = Mathf.Clamp(SideClearance(x, cfg) / W, 0f, 2f);              // 安全側距佔車道寬的比例（車越寬越大）
        o[14] = Mathf.Clamp(x.dpRate / adj / 0.5f, -2f, 2f);                 // 玩家側向速度（+ = 往鬼切車這邊閃）
        o[15] = Mathf.Clamp(x.playerAccel / 8f, -2f, 1f);                     // 玩家加速度（負 = 煞車）
        o[16] = Mathf.Clamp01((x.adjEnd - x.s) / 150f);                       // 隔壁車道還剩多長
        o[17] = Mathf.Clamp(RearGap(x) / 20f, -2f, 2f);                       // 車尾到玩家車頭
        o[18] = x.hasFront ? 1f : 0f;                                         // 自己正前方有沒有別台車
        o[19] = x.hasFront ? Mathf.Clamp(x.frontGap / 60f, 0f, 1.5f) : 1f;
        o[20] = x.hasFront ? Mathf.Clamp((x.frontV - x.v) / 10f, -3f, 3f) : 0f;
    }

    // ====================================================================
    //  動作 ＋ 安全防護
    // ====================================================================

    /// <summary>
    /// 把模型輸出換成實際的加速度與橫移速度，並套用安全防護。
    ///   cmdA：+1 = 全油門、−1 = 全力煞車
    ///   cmdL：+1 = 全速往玩家車道橫移、−1 = 往外（跟左右側無關）
    ///   idmShieldAccel：IDM 跟車算出的加速度上限（前方有車就會煞）
    /// 回傳安全防護有沒有介入、怎麼介入（message 給 Log 用）。
    /// </summary>
    public static GhostShieldAction ApplyAction(in GhostRLState x, AggressiveDriverSettings cfg,
                                                float cmdA, float cmdL, float idmShieldAccel, bool afterEntry, float dt,
                                                out float accel, out float latRate, out string message)
    {
        cmdA = Mathf.Clamp(cmdA, -1f, 1f);
        cmdL = Mathf.Clamp(cmdL, -1f, 1f);

        // ── 縱向：模型 vs IDM 跟車，取較小者（模型看不到的車，IDM 會煞）──
        float a = cmdA >= 0f ? cmdA * cfg.rlMaxAccel : cmdA * cfg.rlMaxBrake;
        accel = Mathf.Min(a, idmShieldAccel);

        // ── 橫向：+ 一律代表「往玩家車道」，再換回實際的左右方向 ──
        float towardSign = x.adjOffset >= 0f ? -1f : 1f;              // 從右側出發 → 往左（d 變小）
        float laneScale = Mathf.Abs(x.adjOffset) / 5f;                 // 訓練基準 5m 車道，依實際寬度等比例
        float maxLat = Mathf.Min(cfg.rlMaxLateralSpeed, 0.3f * x.v);  // 低速時不能橫著走
        latRate = towardSign * cmdL * maxLat * laneScale;

        message = null;
        if (afterEntry) return GhostShieldAction.無;

        // ── 🛡 橫向防護：還沒超過玩家、而且前後距離很近時，不准靠到玩家旁邊 ──
        //    實測：城市的車寬 2.8m，模型在玩家斜後方就開始橫移，玩家一急煞或一偏，側距 2.6m 就擦撞
        bool notPassed = RearGap(x) < cfg.rlLateralClearGap;
        float closing = Mathf.Max(0f, x.v - x.vp);
        bool longitudinallyClose = FrontGap(x) < cfg.rlShieldLongitudinalRange + closing * 1.5f;
        if (!notPassed || !longitudinallyClose) return GhostShieldAction.無;

        float side = Mathf.Abs(x.d - x.dp);
        float clearance = SideClearance(x, cfg);
        bool towardPlayer = latRate * (x.dp - x.d) > 0f;

        if (side < clearance)
        {
            // 已經太近（模型靠過來，或玩家自己靠過來）→ 往外讓，而且跟玩家同速，不從旁邊硬超
            float away = x.d - x.dp >= 0f ? 1f : -1f;
            latRate = away * Mathf.Min(cfg.rlMaxLateralSpeed, 0.3f * x.v + 0.5f) * 0.6f;
            accel = Mathf.Min(accel, (x.vp - x.v) * 1.5f);
            message = $"🛡 橫向防護：側距 {side:F1}m 小於安全距離 {clearance:F1}m → 往外讓、跟玩家同速，不從旁邊硬超";
            return GhostShieldAction.往外讓;
        }
        if (towardPlayer && side - Mathf.Abs(latRate) * dt < clearance)
        {
            latRate = 0f;
            message = $"🛡 橫向防護：車尾還沒超過玩家（差 {RearGap(x):F1}m），暫停靠向玩家車道（側距 {side:F1}m）";
            return GhostShieldAction.暫停靠近;
        }

        // 車身還不准離開自己的車道（v2.1：實測側距 4.2m 雖然夠，但鬼切車已經壓進玩家車道 0.6m，
        // 玩家一急煞加閃避，車頭一偏就撞上）
        float ownLaneLimit = Mathf.Max(0f, Mathf.Abs(x.adjOffset) * 0.5f - x.halfWidth - 0.2f);
        float nextDev = Mathf.Abs(x.d + latRate * dt - x.adjOffset);
        if (towardPlayer && nextDev > ownLaneLimit && nextDev > Mathf.Abs(x.d - x.adjOffset))
        {
            latRate = 0f;
            message = $"🛡 橫向防護：還沒超過玩家（差 {RearGap(x):F1}m），車身不准離開自己的車道";
            return GhostShieldAction.暫停靠近;
        }
        return GhostShieldAction.無;
    }

    /// <summary>
    /// 鬼切車的 IDM 跟車加速度（安全防護用），城市版 AggressiveDriverActor.IdmWithLeaders 的純數學版本。
    /// 玩家在正前方、側向距離小於 playerOverlapLateral 就當前車；正前方的其他車由 hasFront 提供。
    /// </summary>
    public static float IdmShield(in GhostRLState x, AggressiveDriverSettings cfg)
    {
        float desired = x.vp + 30f / 3.6f;   // 只剩「前車互動項」在作用
        float gap = float.PositiveInfinity, leaderV = 0f;
        float overlap = Mathf.Max(cfg.playerOverlapLateral, x.halfWidth + x.playerHalfWidth + 0.3f);   // 實際車寬比 2.6m 還寬時以車寬為準
        if (Mathf.Abs(x.dp - x.d) < overlap && x.sp > x.s)
        {
            gap = FrontGap(x);
            leaderV = x.vp;
        }
        if (x.hasFront && x.frontGap < gap) { gap = x.frontGap; leaderV = x.frontV; }
        return DrivingModel.IdmAccel(x.v, desired, gap, x.v - leaderV, cfg.aggressiveModel);
    }
}
