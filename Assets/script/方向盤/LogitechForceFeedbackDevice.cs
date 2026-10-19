using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 🎮 Logitech 方向盤力回饋 —— 硬體輸出層
///
/// 這一層只負責「把力道送進方向盤馬達」，效果怎麼算是 SteeringWheelFeedback 的事。
///
/// 【為什麼需要這個】
///   Unity 的 Input System 只能「讀」方向盤，不能「輸出」力回饋。
///   力回饋必須透過 Logitech 的原生 DLL：
///       Assets/Plugins/x86_64/LogitechSteeringWheelEnginesWrapper.dll
///
/// 【沒有 DLL 也不會壞】
///   P/Invoke 是「呼叫時」才去找 DLL，不是編譯時。所以這支在沒有 DLL 的情況下
///   照樣能編譯；第一次呼叫會丟 DllNotFoundException，被 try/catch 接住後
///   available 設為 false，之後所有呼叫直接跳過。
///   → 沒 DLL、沒插方向盤都能正常跑，不影響鍵盤測試。
///
/// 【G923 相容性提醒】
///   這套 SDK 是為舊的 Logitech Gaming Software 寫的，而 G923 走 G HUB。
///   若 LogiIsConnected 一直回 false，需要在 G HUB 把裝置切成相容模式，
///   或改用其他力回饋方案（那時只要換掉這個類別，效果邏輯完全不用動）。
/// </summary>
public class LogitechForceFeedbackDevice : IForceFeedbackDevice
{
    private const string DLL = "LogitechSteeringWheelEnginesWrapper";

    // ── Logitech Steering Wheel SDK ────────────────────────────────
    [DllImport(DLL)] private static extern bool LogiSteeringInitialize(bool ignoreXInputControllers);
    [DllImport(DLL)] private static extern bool LogiUpdate();
    [DllImport(DLL)] private static extern bool LogiIsConnected(int index);
    [DllImport(DLL)] private static extern bool LogiSteeringShutdown();

    // 自動回正：offset=中心點, saturation=力道上限, coefficient=彈簧硬度
    [DllImport(DLL)] private static extern bool LogiPlaySpringForce(int index, int offsetPercentage,
                                                                   int saturationPercentage, int coefficientPercentage);
    [DllImport(DLL)] private static extern bool LogiStopSpringForce(int index);

    // 阻尼：方向盤變重
    [DllImport(DLL)] private static extern bool LogiPlayDamperForce(int index, int coefficientPercentage);
    [DllImport(DLL)] private static extern bool LogiStopDamperForce(int index);

    // 週期性震動：拿來做路面反饋與撞擊
    [DllImport(DLL)] private static extern bool LogiPlaySurfaceEffect(int index, int type,
                                                                     int magnitudePercentage, int period);
    [DllImport(DLL)] private static extern bool LogiStopSurfaceEffect(int index);

    // 正面撞擊：一次性的猛烈衝擊
    [DllImport(DLL)] private static extern bool LogiPlayFrontalCollisionForce(int index, int magnitudePercentage);

    private const int LOGI_PERIODICTYPE_SINE = 0;

    private readonly int index;
    private bool available;      // DLL 找得到且初始化成功
    private bool initialised;

    // 只在數值真的改變時才送指令 —— 每幀重送同樣的值會讓某些型號的馬達抖動
    private int lastSpringSat = -1, lastSpringCoef = -1, lastDamper = -1, lastVibration = -1;

    public LogitechForceFeedbackDevice(int deviceIndex = 0) { index = deviceIndex; }

    public string Name => available
        ? $"Logitech SDK (裝置 {index}{(LogiIsConnectedSafe() ? "，已連線" : "，未偵測到方向盤")})"
        : "Logitech SDK（DLL 不可用）";

    public bool IsConnected => available && LogiIsConnectedSafe();

    public void Initialise()
    {
        if (initialised) return;
        initialised = true;
        try
        {
            available = LogiSteeringInitialize(false);
            if (!available)
                Debug.LogWarning("[力回饋] LogiSteeringInitialize 回傳 false —— DLL 有載入但初始化失敗。", null);
        }
        catch (DllNotFoundException)
        {
            available = false;
            Debug.LogWarning("[力回饋] 找不到 LogitechSteeringWheelEnginesWrapper.dll。" +
                             "請放到 Assets/Plugins/x86_64/ 並在 Inspector 勾選 Editor 與 Standalone。", null);
        }
        catch (EntryPointNotFoundException e)
        {
            // SDK 版本不同,函式名稱可能有出入
            available = false;
            Debug.LogWarning($"[力回饋] DLL 內找不到對應函式（SDK 版本可能不同）：{e.Message}", null);
        }
        catch (Exception e)
        {
            available = false;
            Debug.LogWarning($"[力回饋] 初始化失敗：{e.Message}", null);
        }
    }

    /// <summary>每幀必須呼叫一次，SDK 靠它推進內部狀態</summary>
    public void Update()
    {
        if (!available) return;
        try { LogiUpdate(); } catch { available = false; }
    }

    /// <summary>
    /// 自動回正。saturation 與 coefficient 是兩件不同的事，必須分開給：
    ///   saturation  = 力道上限。方向盤打到底時最重能到多重。
    ///   coefficient = 彈簧硬度，也就是「力隨轉角爬升的斜率」。
    ///
    /// 舊版把兩個都餵同一個值，於是低速時 saturation 一低、coefficient 跟著低，
    /// 中心附近一大段角度幾乎沒有反饋 —— 那就是「方向盤鬆鬆的、空空的」的來源。
    /// 分開之後可以做出真車的手感：力道上限低（輕），但一離開中心就有明確的支撐（不鬆）。
    /// </summary>
    public void SetSpring(float saturation, float coefficient)
    {
        if (!IsConnected) return;
        int sat = Pct(saturation);
        int coef = Pct(coefficient);
        if (sat == lastSpringSat && coef == lastSpringCoef) return;
        lastSpringSat = sat; lastSpringCoef = coef;
        try
        {
            if (sat <= 0) LogiStopSpringForce(index);
            // offset 0 = 回正中心就是方向盤的機械中點
            else LogiPlaySpringForce(index, 0, sat, coef);
        }
        catch { available = false; }
    }

    public void SetDamper(float strength)
    {
        if (!IsConnected) return;
        int v = Pct(strength);
        if (v == lastDamper) return;
        lastDamper = v;
        try
        {
            if (v <= 0) LogiStopDamperForce(index);
            else LogiPlayDamperForce(index, v);
        }
        catch { available = false; }
    }

    public void SetVibration(float strength, float hz)
    {
        if (!IsConnected) return;
        int v = Pct(strength);
        if (v == lastVibration) return;
        lastVibration = v;
        try
        {
            if (v <= 0) { LogiStopSurfaceEffect(index); return; }
            // period 是毫秒;頻率越高週期越短
            int period = Mathf.Clamp(Mathf.RoundToInt(1000f / Mathf.Max(1f, hz)), 5, 500);
            LogiPlaySurfaceEffect(index, LOGI_PERIODICTYPE_SINE, v, period);
        }
        catch { available = false; }
    }

    /// <summary>撞擊：用 SDK 專門的正面撞擊效果，比震動更有衝擊感</summary>
    public void PlayCollision(float strength)
    {
        if (!IsConnected) return;
        try { LogiPlayFrontalCollisionForce(index, Pct(strength)); }
        catch { available = false; }
    }

    public void Stop()
    {
        if (!available) return;
        try
        {
            LogiStopSpringForce(index);
            LogiStopDamperForce(index);
            LogiStopSurfaceEffect(index);
            LogiSteeringShutdown();
        }
        catch { }
        available = false;
    }

    private bool LogiIsConnectedSafe()
    {
        try { return LogiIsConnected(index); }
        catch { return false; }
    }

    private static int Pct(float v) => Mathf.Clamp(Mathf.RoundToInt(v), 0, 100);
}
