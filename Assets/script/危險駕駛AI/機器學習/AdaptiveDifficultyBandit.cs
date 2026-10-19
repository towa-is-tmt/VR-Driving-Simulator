using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>鬼切的一個難度等級（數字會覆寫到 AggressiveDriverSettings）</summary>
[Serializable]
public class GhostDifficultyLevel
{
    public string name = "標準";
    [Tooltip("切入時機：玩家反應後需要的減速度上限（m/s²）")] public float targetImposedDecel = 2.5f;
    [Tooltip("切入後踩煞車的機率")] [Range(0f, 1f)] public float brakeAfterCutChance = 0.6f;
    [Tooltip("切入後煞車減速度（m/s²）")] public float brakeAfterCutDecel = 5.5f;
    [Tooltip("煞到玩家車速的幾成")] [Range(0f, 1f)] public float brakeAfterCutFloorRatio = 0.4f;
    [Tooltip("橫移時間（秒），越短越突然")] public float cutInDuration = 1.2f;
    [Tooltip("切入前打方向燈幾秒（0 = 不打燈）")] public float signalLeadSeconds = 0f;
}

/// <summary>路霸的一個難度等級</summary>
[Serializable]
public class HogDifficultyLevel
{
    public string name = "標準";
    [Tooltip("切回時機：玩家反應後需要的減速度上限（m/s²）")] public float hogTargetImposedDecel = 3f;
    [Tooltip("切回後故意煞車的機率")] [Range(0f, 1f)] public float brakeCheckChance = 0.7f;
    [Tooltip("故意煞車減速度（m/s²）")] public float brakeCheckDecel = 6f;
    [Tooltip("煞到玩家車速的幾成")] [Range(0f, 1f)] public float brakeCheckFloorRatio = 0.3f;
    [Tooltip("貼尾的車頭時距（秒），越小貼越近")] public float tailgateTimeHeadway = 0.45f;
    [Tooltip("貼尾幾秒後才開始超車")] public float tailgateSeconds = 7f;
}

/// <summary>危險事件結算資料（由演員車在事件結束時回報）</summary>
public struct DangerOutcome
{
    public bool reacted;          // 有沒有反應（減速／閃避／轉頭）
    public float reactionDelay;   // 反應延遲（秒），-1 = 沒反應
    public bool collided;         // 演出期間有沒有撞到
    public bool yielded;          // 路霸：有沒有主動讓道
    public bool raced;            // 路霸：有沒有加速跟它競速
    public float minTTC;          // 最小碰撞時間
    public float playerMaxDecel;  // 玩家最大減速度
    public string controller;     // 規則式／強化學習
}

/// <summary>
/// 🎓 自適應難度（機器學習：多臂吃角子老虎機 Multi-Armed Bandit ＋ 湯普森取樣 Thompson Sampling）
///
/// 問題：每位學員程度不同。難度太低學不到東西，太高只會一直撞車、挫折。
/// 做法：把每個難度等級當成一台「吃角子老虎機的拉桿」，每位學員、每種危險都有一組 Beta 分佈，
///       記錄「在這個等級成功應對的機率」。每次事件前：
///         1. 從每個等級的 Beta 分佈各抽一個成功率樣本（這就是 Thompson Sampling，天然兼顧探索與利用）
///         2. 挑「樣本最接近目標成功率（預設 70%）」的等級 —— 學習理論的「近側發展區」：有挑戰但做得到
///         3. 事件結束後，成功就 α+1、失敗就 β+1，分佈會越來越準
/// 學習結果存成 JSON（每位學員一份），下次開遊戲接著用；每次事件另外寫一行 CSV，可直接拿來畫學習曲線放報告。
///
/// ⚠️ 只調整「演出參數」，不碰任何車輛邏輯；關掉 enableAdaptive 就完全回到固定難度。
/// </summary>
public class AdaptiveDifficultyBandit : MonoBehaviour
{
    public static AdaptiveDifficultyBandit Instance;

    [Header("🎓 自適應難度")]
    [Tooltip("關掉 = 一律用觸發器上的固定參數")]
    public bool enableAdaptive = true;
    [Tooltip("學員名稱：每位學員的學習紀錄分開存。之後可以接到登入／選單")]
    public string learnerProfile = "預設學員";
    [Tooltip("目標成功率。0.7 = 希望學員大約七成能安全應對（有挑戰但做得到）")]
    [Range(0.4f, 0.95f)] public float targetSuccessRate = 0.7f;
    [Tooltip("每次最多只升降一級，避免難度忽高忽低")]
    public bool neighborOnly = true;
    [Tooltip("新學員從第幾級開始（0 = 最簡單）")]
    public int startLevel = 2;
    [Tooltip("反應延遲在幾秒內才算「成功應對」（有反應但太慢也算失敗）")]
    public float goodReactionMaxDelay = 2.0f;
    [Tooltip("先驗強度：一開始假設「越難越容易失敗」的信心有多強（虛擬次數）。越大學得越慢但越穩")]
    public float priorStrength = 2f;

    [Header("👻 鬼切難度等級（由易到難）")]
    public GhostDifficultyLevel[] ghostLevels = DefaultGhostLevels();

    [Header("😠 路霸難度等級（由易到難）")]
    public HogDifficultyLevel[] hogLevels = DefaultHogLevels();

    [Header("📊 紀錄")]
    [Tooltip("每次事件寫一行 CSV（時間、學員、劇本、等級、成功與否、反應時間…），可用 Excel 開來畫學習曲線")]
    public bool writeCsvLog = true;
    public bool logDecisions = true;

    [Header("目前狀態（唯讀）")]
    public int currentGhostLevel;
    public int currentHogLevel;
    [TextArea(3, 8)] public string posteriorSummary;

    [Serializable]
    private class BanditState
    {
        public string profile;
        public float[] ghostAlpha, ghostBeta, hogAlpha, hogBeta;
        public int ghostLevel = -1, hogLevel = -1;
        public int ghostTrials, hogTrials;
    }

    private BanditState state;

    private string SavePath
    {
        get
        {
            string safe = learnerProfile;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Application.persistentDataPath, "危險駕駛AI_自適應難度_" + safe + ".json");
        }
    }

    private string CsvPath { get { return Path.Combine(Application.persistentDataPath, "危險駕駛AI_事件紀錄.csv"); } }

    private void Awake()
    {
        Instance = this;
        Load();
    }

    // ====================================================================
    //  對外 API
    // ====================================================================

    /// <summary>
    /// 事件開始前呼叫：挑一個難度等級，回傳「套用該等級後的參數複本」（不會改到原本的參數）。
    /// </summary>
    public AggressiveDriverSettings PrepareSettings(DangerDriverScenario sc, AggressiveDriverSettings baseSettings, out int level)
    {
        AggressiveDriverSettings s = baseSettings.Clone();
        level = -1;
        if (!enableAdaptive) return s;
        EnsureState();

        bool ghost = sc == DangerDriverScenario.鬼切;
        float[] a = ghost ? state.ghostAlpha : state.hogAlpha;
        float[] b = ghost ? state.ghostBeta : state.hogBeta;
        int current = ghost ? state.ghostLevel : state.hogLevel;
        level = ChooseLevel(a, b, current, out string why);

        if (ghost)
        {
            var L = ghostLevels[level];
            s.targetImposedDecel = L.targetImposedDecel;
            s.brakeAfterCutChance = L.brakeAfterCutChance;
            s.brakeAfterCutDecel = L.brakeAfterCutDecel;
            s.brakeAfterCutFloorRatio = L.brakeAfterCutFloorRatio;
            s.cutInDuration = L.cutInDuration;
            s.signalLeadSeconds = L.signalLeadSeconds;
        }
        else
        {
            var L = hogLevels[level];
            s.hogTargetImposedDecel = L.hogTargetImposedDecel;
            s.brakeCheckChance = L.brakeCheckChance;
            s.brakeCheckDecel = L.brakeCheckDecel;
            s.brakeCheckFloorRatio = L.brakeCheckFloorRatio;
            s.aggressiveModel.timeHeadway = L.tailgateTimeHeadway;
            s.tailgateSeconds = L.tailgateSeconds;
        }

        if (logDecisions)
            Debug.Log($"<color=#7fdbff>🎓 [自適應難度] {learnerProfile}「{sc}」選第 {level + 1} 級（{(ghost ? ghostLevels[level].name : hogLevels[level].name)}）—— {why}</color>", this);
        return s;
    }

    /// <summary>事件結束後呼叫：依結果更新這一級的 Beta 分佈，並寫 CSV</summary>
    public void ReportOutcome(DangerDriverScenario sc, int level, DangerOutcome o)
    {
        bool success = IsSuccess(sc, o, out string verdict);

        if (enableAdaptive && level >= 0)
        {
            EnsureState();
            bool ghost = sc == DangerDriverScenario.鬼切;
            float[] a = ghost ? state.ghostAlpha : state.hogAlpha;
            float[] b = ghost ? state.ghostBeta : state.hogBeta;
            if (level < a.Length)
            {
                if (success) a[level] += 1f; else b[level] += 1f;
                if (ghost) { state.ghostLevel = level; state.ghostTrials++; }
                else { state.hogLevel = level; state.hogTrials++; }
                Save();
            }

            if (logDecisions)
                Debug.Log($"<color=#7fdbff>🎓 [自適應難度] 「{sc}」第 {level + 1} 級 → {(success ? "✔ 成功" : "✘ 失敗")}（{verdict}）" +
                          $"｜此級估計成功率 {Mean(a[level], b[level]) * 100f:F0}%（{a[level] + b[level] - priorStrength - 2f:F0} 次實測）</color>", this);
        }

        WriteCsv(sc, level, success, verdict, o);
        RefreshSummary();
    }

    /// <summary>成功應對的定義（寫進報告的「獎勵函數」）</summary>
    public bool IsSuccess(DangerDriverScenario sc, DangerOutcome o, out string verdict)
    {
        if (o.collided) { verdict = "發生碰撞"; return false; }
        if (sc == DangerDriverScenario.路霸)
        {
            if (o.raced) { verdict = "跟路霸競速"; return false; }
            if (o.yielded) { verdict = "主動讓道"; return true; }
        }
        if (!o.reacted) { verdict = "全程沒反應"; return false; }
        if (o.reactionDelay > goodReactionMaxDelay) { verdict = $"反應太慢 {o.reactionDelay:F1}s"; return false; }
        verdict = $"反應 {o.reactionDelay:F1}s";
        return true;
    }

    // ====================================================================
    //  Thompson Sampling
    // ====================================================================

    private int ChooseLevel(float[] a, float[] b, int current, out string why)
    {
        int n = a.Length;
        if (current < 0) current = Mathf.Clamp(startLevel, 0, n - 1);

        int lo = neighborOnly ? Mathf.Max(0, current - 1) : 0;
        int hi = neighborOnly ? Mathf.Min(n - 1, current + 1) : n - 1;

        int best = current;
        float bestScore = float.MaxValue;
        var sb = new StringBuilder("抽樣成功率 ");
        for (int i = lo; i <= hi; i++)
        {
            float theta = SampleBeta(a[i], b[i]);
            // 越接近目標成功率越好；同分時偏向較難的等級（i 越大越好，極小的加權）
            float score = Mathf.Abs(theta - targetSuccessRate) - i * 0.0001f;
            sb.Append($"L{i + 1}={theta * 100f:F0}% ");
            if (score < bestScore) { bestScore = score; best = i; }
        }
        sb.Append($"→ 最接近目標 {targetSuccessRate * 100f:F0}%");
        why = sb.ToString();
        return best;
    }

    private static float Mean(float a, float b) { return a / Mathf.Max(a + b, 0.0001f); }

    /// <summary>Beta(a, b) 取樣：兩個 Gamma 樣本相除</summary>
    public static float SampleBeta(float a, float b)
    {
        float x = SampleGamma(a), y = SampleGamma(b);
        return x / Mathf.Max(x + y, 1e-6f);
    }

    /// <summary>Gamma(k, 1) 取樣（Marsaglia–Tsang 法）</summary>
    public static float SampleGamma(float k)
    {
        if (k < 1f)
        {
            // Gamma(k) = Gamma(k+1) · U^(1/k)
            float u = Mathf.Max(UnityEngine.Random.value, 1e-6f);
            return SampleGamma(k + 1f) * Mathf.Pow(u, 1f / k);
        }
        float d = k - 1f / 3f, c = 1f / Mathf.Sqrt(9f * d);
        for (int guard = 0; guard < 100; guard++)
        {
            float x = SampleNormal();
            float v = 1f + c * x;
            if (v <= 0f) continue;
            v = v * v * v;
            float u = UnityEngine.Random.value;
            if (u < 1f - 0.0331f * x * x * x * x) return d * v;
            if (Mathf.Log(Mathf.Max(u, 1e-6f)) < 0.5f * x * x + d * (1f - v + Mathf.Log(v))) return d * v;
        }
        return k;   // 保險
    }

    private static float SampleNormal()
    {
        float u1 = Mathf.Max(UnityEngine.Random.value, 1e-6f), u2 = UnityEngine.Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    // ====================================================================
    //  狀態、存檔、CSV
    // ====================================================================

    private void EnsureState()
    {
        int ng = ghostLevels != null ? ghostLevels.Length : 0;
        int nh = hogLevels != null ? hogLevels.Length : 0;
        if (state == null || state.profile != learnerProfile) Load();
        if (state.ghostAlpha == null || state.ghostAlpha.Length != ng) InitPrior(out state.ghostAlpha, out state.ghostBeta, ng);
        if (state.hogAlpha == null || state.hogAlpha.Length != nh) InitPrior(out state.hogAlpha, out state.hogBeta, nh);
    }

    /// <summary>先驗：簡單的等級預期成功率高、難的預期低（90% → 40%），強度 priorStrength</summary>
    private void InitPrior(out float[] a, out float[] b, int n)
    {
        a = new float[n]; b = new float[n];
        for (int i = 0; i < n; i++)
        {
            float p = n > 1 ? Mathf.Lerp(0.9f, 0.4f, i / (float)(n - 1)) : 0.7f;
            a[i] = priorStrength * p + 1f;
            b[i] = priorStrength * (1f - p) + 1f;
        }
    }

    private void Load()
    {
        state = null;
        try
        {
            if (File.Exists(SavePath))
                state = JsonUtility.FromJson<BanditState>(File.ReadAllText(SavePath, Encoding.UTF8));
        }
        catch (Exception e) { Debug.LogWarning("[自適應難度] 讀檔失敗，改用新紀錄：" + e.Message, this); }

        if (state == null || state.profile != learnerProfile)
            state = new BanditState { profile = learnerProfile };
        EnsureState();
        RefreshSummary();
    }

    private void Save()
    {
        try { File.WriteAllText(SavePath, JsonUtility.ToJson(state, true), Encoding.UTF8); }
        catch (Exception e) { Debug.LogWarning("[自適應難度] 存檔失敗：" + e.Message, this); }
    }

    private void WriteCsv(DangerDriverScenario sc, int level, bool success, string verdict, DangerOutcome o)
    {
        if (!writeCsvLog) return;
        try
        {
            bool newFile = !File.Exists(CsvPath);
            var sb = new StringBuilder();
            if (newFile) sb.AppendLine("時間,學員,劇本,控制方式,難度等級,成功,判定,有反應,反應秒數,碰撞,讓道,競速,最小TTC,玩家最大減速度");
            sb.AppendLine(string.Join(",", new[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), learnerProfile, sc.ToString(), o.controller ?? "規則式",
                level >= 0 ? (level + 1).ToString() : "固定", success ? "1" : "0", verdict.Replace(",", " "),
                o.reacted ? "1" : "0", o.reactionDelay.ToString("F2"), o.collided ? "1" : "0",
                o.yielded ? "1" : "0", o.raced ? "1" : "0",
                float.IsInfinity(o.minTTC) ? "" : o.minTTC.ToString("F2"), o.playerMaxDecel.ToString("F2")
            }));
            // 加 BOM，Excel 開中文才不會亂碼
            File.AppendAllText(CsvPath, sb.ToString(), new UTF8Encoding(newFile));
        }
        catch (Exception e) { Debug.LogWarning("[自適應難度] CSV 寫入失敗：" + e.Message, this); }
    }

    private void RefreshSummary()
    {
        if (state == null) return;
        currentGhostLevel = Mathf.Max(0, state.ghostLevel >= 0 ? state.ghostLevel : startLevel) + 1;
        currentHogLevel = Mathf.Max(0, state.hogLevel >= 0 ? state.hogLevel : startLevel) + 1;
        var sb = new StringBuilder();
        sb.Append($"鬼切（{state.ghostTrials} 次）：");
        for (int i = 0; state.ghostAlpha != null && i < state.ghostAlpha.Length; i++)
            sb.Append($"L{i + 1} {Mean(state.ghostAlpha[i], state.ghostBeta[i]) * 100f:F0}%  ");
        sb.Append($"\n路霸（{state.hogTrials} 次）：");
        for (int i = 0; state.hogAlpha != null && i < state.hogAlpha.Length; i++)
            sb.Append($"L{i + 1} {Mean(state.hogAlpha[i], state.hogBeta[i]) * 100f:F0}%  ");
        posteriorSummary = sb.ToString();
    }

    [ContextMenu("🗑 重置目前學員的學習紀錄")]
    private void ResetProfile()
    {
        state = new BanditState { profile = learnerProfile };
        EnsureState();
        Save();
        RefreshSummary();
        Debug.Log($"[自適應難度] 已重置「{learnerProfile}」的紀錄", this);
    }

    [ContextMenu("📂 開啟紀錄資料夾（JSON / CSV）")]
    private void OpenFolder()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.RevealInFinder(CsvPath);
#endif
        Debug.Log("[自適應難度] 紀錄資料夾：" + Application.persistentDataPath, this);
    }

    // ====================================================================
    //  預設等級
    // ====================================================================

    private static GhostDifficultyLevel[] DefaultGhostLevels()
    {
        return new[]
        {
            new GhostDifficultyLevel { name = "入門（有打燈、輕煞）", targetImposedDecel = 1.5f, brakeAfterCutChance = 0.3f, brakeAfterCutDecel = 3.5f, brakeAfterCutFloorRatio = 0.6f, cutInDuration = 1.6f, signalLeadSeconds = 1.0f },
            new GhostDifficultyLevel { name = "初級",               targetImposedDecel = 2.0f, brakeAfterCutChance = 0.5f, brakeAfterCutDecel = 4.5f, brakeAfterCutFloorRatio = 0.5f, cutInDuration = 1.4f, signalLeadSeconds = 0.4f },
            new GhostDifficultyLevel { name = "標準",               targetImposedDecel = 2.5f, brakeAfterCutChance = 0.6f, brakeAfterCutDecel = 5.5f, brakeAfterCutFloorRatio = 0.4f, cutInDuration = 1.2f, signalLeadSeconds = 0f },
            new GhostDifficultyLevel { name = "進階",               targetImposedDecel = 3.0f, brakeAfterCutChance = 0.8f, brakeAfterCutDecel = 6.5f, brakeAfterCutFloorRatio = 0.3f, cutInDuration = 1.0f, signalLeadSeconds = 0f },
            new GhostDifficultyLevel { name = "極限",               targetImposedDecel = 3.5f, brakeAfterCutChance = 1.0f, brakeAfterCutDecel = 7.5f, brakeAfterCutFloorRatio = 0.2f, cutInDuration = 0.9f, signalLeadSeconds = 0f },
        };
    }

    private static HogDifficultyLevel[] DefaultHogLevels()
    {
        return new[]
        {
            new HogDifficultyLevel { name = "入門", hogTargetImposedDecel = 2.0f, brakeCheckChance = 0.3f,  brakeCheckDecel = 4f, brakeCheckFloorRatio = 0.5f,  tailgateTimeHeadway = 0.7f,  tailgateSeconds = 5f },
            new HogDifficultyLevel { name = "初級", hogTargetImposedDecel = 2.5f, brakeCheckChance = 0.5f,  brakeCheckDecel = 5f, brakeCheckFloorRatio = 0.4f,  tailgateTimeHeadway = 0.55f, tailgateSeconds = 6f },
            new HogDifficultyLevel { name = "標準", hogTargetImposedDecel = 3.0f, brakeCheckChance = 0.7f,  brakeCheckDecel = 6f, brakeCheckFloorRatio = 0.3f,  tailgateTimeHeadway = 0.45f, tailgateSeconds = 7f },
            new HogDifficultyLevel { name = "進階", hogTargetImposedDecel = 3.5f, brakeCheckChance = 0.85f, brakeCheckDecel = 7f, brakeCheckFloorRatio = 0.25f, tailgateTimeHeadway = 0.4f,  tailgateSeconds = 8f },
            new HogDifficultyLevel { name = "極限", hogTargetImposedDecel = 4.0f, brakeCheckChance = 1.0f,  brakeCheckDecel = 8f, brakeCheckFloorRatio = 0.2f,  tailgateTimeHeadway = 0.35f, tailgateSeconds = 9f },
        };
    }
}
