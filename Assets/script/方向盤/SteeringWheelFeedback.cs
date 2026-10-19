using UnityEngine;

/// <summary>
/// 🎮 方向盤力回饋 —— 效果計算層
///
/// 掛在玩家車上（有 Rigidbody、CarPhysicsController 的那個物件）。
///
/// 【為什麼分成兩層】
///   這支只負責算出「現在方向盤該有多重、震多大」，算完交給 ForceFeedbackDevice 輸出。
///   Unity 的 Input System 只能讀方向盤輸入、不能輸出力道，輸出必須靠 Logitech 的原生 DLL。
///   把「算力道」和「送力道」拆開之後：
///     - 沒有 DLL、沒插方向盤時照樣能跑（用 NullDevice 空轉），不影響鍵盤測試
///     - 之後接上 DLL 只要換掉輸出層，這裡一行都不用改
///     - 換成別的力回饋方案（別的 SDK、別的廠牌）也一樣只換輸出層
///
/// 【怎麼在沒有硬體的情況下驗證】
///   把 logForces 打開，Console 會定期印出算出來的四個力道值。
///   數值調到合理之後，等方向盤到手直接就是對的手感，不用重調。
/// </summary>
public class SteeringWheelFeedback : MonoBehaviour
{
    [Header("🔧 自動回正（Spring）")]
    // 真車的方向盤放手會自己回中，速度越快回正力越強（來自前輪定位角與輪胎自回正力矩）。
    [Tooltip("停止時的回正力道 (0~100)")]
    [Range(0f, 100f)] public float springAtStandstill = 15f;
    [Tooltip("到達下面那個時速時的回正力道 (0~100)")]
    [Range(0f, 100f)] public float springAtSpeed = 65f;
    [Tooltip("回正力道爬到最大值的時速")]
    public float springFullAtKmH = 90f;

    // 「力道上限」和「硬度」是兩件事,舊版把兩個綁成同一個值送進 SDK,
    // 於是低速時上限一低、硬度跟著低 —— 中心附近一大段角度幾乎沒有反饋,
    // 那就是「原地打方向鬆鬆的、空空的」的來源。分開之後可以做出真車的手感:
    // 力道上限低(輕),但一離開中心就有明確的支撐(不鬆)。
    [Header("🔩 回正硬度 (力隨轉角爬升多快,與上面的力道上限分開)")]
    [Tooltip("停止時的回正硬度 (0~100)。上限低但硬度高 = 輕,但不鬆")]
    [Range(0f, 100f)] public float springCoefficientAtStandstill = 45f;
    [Tooltip("高速時的回正硬度 (0~100)。真車越快越緊實、越線性")]
    [Range(0f, 100f)] public float springCoefficientAtSpeed = 80f;

    [Header("🌀 過彎阻力（Damper）")]
    // 轉向角越大、車速越快，方向盤越重 —— 那是輪胎側向力回饋到轉向機構的結果。
    [Tooltip("基礎阻尼 (0~100)")]
    [Range(0f, 100f)] public float damperBase = 10f;
    [Tooltip("滿舵且高速時額外增加的阻尼 (0~100)")]
    [Range(0f, 100f)] public float damperAtFullLock = 45f;

    [Header("🅿️ 原地打方向的阻力")]
    // 真車停著打方向是「最重」的情況 —— 輪胎整個貼在地上磨,就算有動力輔助
    // 也明顯比行進間吃力。少了這一項,路邊停車和倒車入庫會輕得不真實,
    // 而那兩項正好是駕訓最需要練的動作。
    [Tooltip("完全靜止時額外增加的阻尼 (0~100)。真車停車打方向很重")]
    [Range(0f, 100f)] public float parkingEffort = 55f;
    [Tooltip("到達這個時速時,停車阻力完全消失,之後交給過彎阻尼接手")]
    public float parkingEffortFadeAtKmH = 20f;

    [Header("↩️ 回正順暢度")]
    // 阻尼是為了讓「原地打方向很重」而存在的,但它同時也在拖住方向盤自己回中,
    // 於是放手之後就是在糖漿裡慢慢爬 —— 那就是「回正又慢又黏」的來源。
    //
    // 真車沒有這個問題:原地打方向的重來自輪胎磨地的「摩擦」,而放手回中時
    // 輪胎是在解除扭轉,阻力小得多。所以這裡分辨方向盤是「被駕駛往外轉」還是
    // 「自己往中心回」,只有前者要重。判斷方式是看轉動方向與目前偏角是否同號。
    [Tooltip("方向盤「往中心回」時,阻尼打幾折。1 = 不打折(舊行為,回正會被拖住);" +
             "0.3 = 回正時阻尼降到三成,主動轉仍然重、但放手回中很順")]
    [Range(0.05f, 1f)] public float returnDamperScale = 0.3f;
    [Tooltip("判定為「正在回正」的最小轉動速率。太小會被雜訊誤觸發,太大則慢慢回中時判不出來")]
    public float returnDetectRate = 0.05f;
    [Tooltip("阻尼變化的平滑速度(每秒單位)。在「主動轉」與「回正中」之間切換時," +
             "力道才不會突然跳一下。設 0 = 不平滑")]
    public float damperSmoothSpeed = 200f;

    [Header("🛣️ 路面反饋（持續震動）")]
    [Tooltip("輪胎打滑時的震動強度 (0~100)")]
    [Range(0f, 100f)] public float slipVibrationStrength = 40f;
    [Tooltip("滑移量到多少算「完全打滑」。WheelCollider 的 sideways slip，通常 0.3~0.6")]
    public float slipAtFullVibration = 0.45f;
    [Tooltip("震動頻率 (Hz)")]
    public float vibrationHz = 22f;
    [Tooltip("低於這個時速不做路面震動，避免停車時方向盤一直抖")]
    public float minSpeedForVibration = 8f;

    [Header("💥 撞擊衝擊")]
    [Tooltip("最重的撞擊力道 (0~100)")]
    [Range(0f, 100f)] public float impactMaxStrength = 90f;
    [Tooltip("撞擊力道衰減到 0 要幾秒")]
    public float impactDecaySeconds = 0.35f;
    // 真車撞完,方向盤是「被踹歪停在那裡」,要靠駕駛自己轉回來 ——
    // 它不會自己慢慢扶正。撞擊期間如果回正彈簧還開著,那一記衝擊會被彈簧
    // 一路抵銷掉,手感就變成「軟軟地被推回中間」,完全不像撞到東西。
    [Tooltip("撞擊後暫停自動回正幾秒。真車撞完方向盤停在被打歪的位置,不會自己回中。0 = 不暫停(舊行為)")]
    public float impactSpringSuppressSeconds = 1.2f;

    [Header("硬體輸出")]
    [Tooltip("嘗試使用 Logitech SDK 輸出力回饋。DLL 不存在或初始化失敗時會自動退回空裝置,不會報錯")]
    public bool useLogitechSDK = true;
    [Tooltip("方向盤裝置索引。只接一個方向盤就是 0")]
    public int deviceIndex = 0;

    [Header("診斷")]
    [Tooltip("沒有方向盤時用這個驗證力道算得對不對。每隔一段時間印一次")]
    public bool logForces = false;
    public float logInterval = 0.5f;

    /// <summary>目前是否真的有硬體在接收力回饋</summary>
    public bool DeviceConnected => device != null && device.IsConnected;

    private CarPhysicsController carPhysics;
    private Rigidbody rb;
    private IForceFeedbackDevice device;

    private float impactAmplitude;
    private float springSuppressUntil = -1f;   // 撞擊後暫停回正到這個時間點
    private float lastSteerPos = 0f;           // 上一幀的方向盤位置,用來算轉動速率
    private float smoothedDamper = 0f;         // 平滑後真正送出去的阻尼
    private bool returningToCentre = false;    // 這一幀方向盤是不是正在往中心回
    private float nextLogTime;

    private void Start()
    {
        carPhysics = GetComponent<CarPhysicsController>();
        if (carPhysics == null) carPhysics = GetComponentInParent<CarPhysicsController>();
        if (carPhysics == null) carPhysics = GetComponentInChildren<CarPhysicsController>();

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponentInChildren<Rigidbody>();

        // 先試 Logitech,失敗就退回空裝置 ——
        // DLL 不存在、SDK 版本不合、沒插方向盤,三種情況都會安全退回,不會報錯。
        device = null;
        if (useLogitechSDK)
        {
            var logi = new LogitechForceFeedbackDevice(deviceIndex);
            logi.Initialise();
            if (logi.IsConnected) device = logi;
            else
            {
                logi.Stop();
                Debug.LogWarning($"[力回饋] Logitech 裝置不可用（{logi.Name}），改用空裝置。" +
                                 $"效果照常計算,只是不會送到方向盤。", this);
            }
        }
        if (device == null)
        {
            device = new NullForceFeedbackDevice();
            device.Initialise();
        }

        if (carPhysics == null)
            Debug.LogWarning("[力回饋] 找不到 CarPhysicsController，路面震動與過彎阻力會失效。", this);

        Debug.Log($"<color=orange>🎮 [力回饋] 已啟動｜輸出裝置 = {device.Name}｜" +
                  $"硬體連線 = {(DeviceConnected ? "有" : "無（效果照算，只是沒送出去）")}</color>", this);
    }

    /// <summary>
    /// 撞擊時由外部呼叫（PlayerCollisionAudio 已經算好 severity01，直接沿用同一套力道分級）。
    /// </summary>
    public void ApplyImpact(float severity01)
    {
        float target = impactMaxStrength * Mathf.Clamp01(severity01);
        // 取較大值：連續撞擊時不會把正在進行的衝擊洗掉
        impactAmplitude = Mathf.Max(impactAmplitude, target);

        // 撞擊期間關掉回正,那一記衝擊才不會被彈簧抵銷 —— 真車撞完方向盤是被打歪停住,
        // 不是被慢慢扶回中間。力道越重,暫停越久。
        if (impactSpringSuppressSeconds > 0f)
            springSuppressUntil = Mathf.Max(springSuppressUntil,
                Time.time + impactSpringSuppressSeconds * Mathf.Clamp01(severity01));

        // Logitech 有專門的正面撞擊效果,比單純震動更有衝擊感,一次性觸發
        (device as LogitechForceFeedbackDevice)?.PlayCollision(target);
    }

    private void Update()
    {
        float speedKmH = rb != null ? rb.velocity.magnitude * 3.6f : 0f;

        // ↩️ 先判斷方向盤是「被往外轉」還是「自己往中心回」。
        //    位置與轉動速率「異號」= 正在朝 0 靠近 = 回正中。
        float steerPos = carPhysics != null ? carPhysics.SteerInput : 0f;
        float steerRate = (steerPos - lastSteerPos) / Mathf.Max(0.0001f, Time.deltaTime);
        lastSteerPos = steerPos;
        returningToCentre = Mathf.Abs(steerPos) > 0.02f
                            && steerPos * steerRate < 0f
                            && Mathf.Abs(steerRate) > returnDetectRate;

        float spring = CalcSpring(speedKmH);
        float springCoef = Mathf.Lerp(springCoefficientAtStandstill, springCoefficientAtSpeed,
                                      Mathf.Clamp01(speedKmH / Mathf.Max(1f, springFullAtKmH)));

        // 撞擊後的暫停期:回正整個關掉,方向盤停在被打歪的位置
        bool springSuppressed = Time.time < springSuppressUntil;
        if (springSuppressed) spring = 0f;
        float damper = CalcDamper(speedKmH);
        if (returningToCentre) damper *= returnDamperScale;   // 回正中 → 讓路,別拖住彈簧

        // 切換瞬間力道不要跳,平滑之後才送出去
        smoothedDamper = damperSmoothSpeed > 0.01f
            ? Mathf.MoveTowards(smoothedDamper, damper, damperSmoothSpeed * Time.deltaTime)
            : damper;
        float vibration = CalcVibration(speedKmH);

        // 撞擊隨時間衰減
        if (impactAmplitude > 0.01f)
            impactAmplitude = Mathf.MoveTowards(impactAmplitude, 0f,
                impactMaxStrength / Mathf.Max(0.01f, impactDecaySeconds) * Time.deltaTime);
        else impactAmplitude = 0f;

        // 撞擊期間蓋過路面震動 —— 兩個同時送會互相抵銷，撞擊感反而變弱
        float finalVibration = impactAmplitude > 1f ? impactAmplitude : vibration;

        // Logitech SDK 需要每幀呼叫 LogiUpdate() 推進內部狀態
        (device as LogitechForceFeedbackDevice)?.Update();

        device.SetSpring(spring, springCoef);
        device.SetDamper(smoothedDamper);
        device.SetVibration(finalVibration, vibrationHz);

        if (logForces && Time.time >= nextLogTime)
        {
            nextLogTime = Time.time + Mathf.Max(0.1f, logInterval);
            Debug.Log($"🎮 [力回饋] 時速 {speedKmH:F0}｜回正 {spring:F0}(硬度 {springCoef:F0})｜" +
                      $"阻尼 {smoothedDamper:F0}{(returningToCentre ? "(回正中,已打折)" : "")}｜" +
                      $"震動 {finalVibration:F0}{(impactAmplitude > 1f ? "（撞擊中）" : "")}" +
                      $"{(springSuppressed ? "｜★撞擊後回正暫停中" : "")}");
        }
    }

    /// <summary>回正力：速度越快越強</summary>
    private float CalcSpring(float speedKmH)
    {
        float t = Mathf.Clamp01(speedKmH / Mathf.Max(1f, springFullAtKmH));
        return Mathf.Lerp(springAtStandstill, springAtSpeed, t);
    }

    /// <summary>
    /// 方向盤有多重 = 基礎阻尼 + 過彎阻力 + 原地打方向阻力。
    ///
    /// 過彎阻力隨「轉角 × 車速」上升；停車阻力則相反,速度越低越重,
    /// 兩者剛好接在一起 —— 靜止時是停車阻力主導,動起來後換過彎阻力接手,
    /// 中間不會有「突然變輕」的空窗。
    /// </summary>
    private float CalcDamper(float speedKmH)
    {
        // 停車阻力不需要 carPhysics,即使抓不到車輛元件也要有,所以先算
        float parking = 0f;
        if (parkingEffortFadeAtKmH > 0.1f)
        {
            float fade = 1f - Mathf.Clamp01(speedKmH / parkingEffortFadeAtKmH);
            parking = parkingEffort * fade;
        }

        if (carPhysics == null || carPhysics.frontLeftWheel == null)
            return Mathf.Min(100f, damperBase + parking);

        float maxAngle = Mathf.Max(1f, carPhysics.maxSteerAngle);
        float steerRatio = Mathf.Clamp01(Mathf.Abs(carPhysics.frontLeftWheel.steerAngle) / maxAngle);
        float speedRatio = Mathf.Clamp01(speedKmH / Mathf.Max(1f, springFullAtKmH));

        float cornering = damperAtFullLock * steerRatio * speedRatio;

        // 上限 100 —— SDK 的參數範圍就是 0~100,超出去會被截斷
        return Mathf.Min(100f, damperBase + cornering + parking);
    }

    /// <summary>
    /// 路面反饋：用 WheelCollider 實際回報的側向滑移量。
    /// 直接反映輪胎有沒有抓住地面，過彎推頭、煞車鎖死、壓到路肩都會自然產生震動，
    /// 不需要為每種情況各寫一套判斷。
    /// </summary>
    private float CalcVibration(float speedKmH)
    {
        if (carPhysics == null || speedKmH < minSpeedForVibration) return 0f;

        float maxSlip = 0f;
        maxSlip = Mathf.Max(maxSlip, SlipOf(carPhysics.frontLeftWheel));
        maxSlip = Mathf.Max(maxSlip, SlipOf(carPhysics.frontRightWheel));
        maxSlip = Mathf.Max(maxSlip, SlipOf(carPhysics.rearLeftWheel));
        maxSlip = Mathf.Max(maxSlip, SlipOf(carPhysics.rearRightWheel));

        float t = Mathf.Clamp01(maxSlip / Mathf.Max(0.01f, slipAtFullVibration));
        return slipVibrationStrength * t;
    }

    private float SlipOf(WheelCollider w)
    {
        if (w == null) return 0f;
        WheelHit hit;
        if (!w.GetGroundHit(out hit)) return 0f;   // 輪子離地
        return Mathf.Abs(hit.sidewaysSlip);
    }

    private void OnDisable()
    {
        if (device != null) device.Stop();
    }
}

/// <summary>
/// 力回饋輸出層的介面。把「算力道」與「送力道」分開，
/// 之後接 Logitech DLL 只要新增一個實作類別，效果邏輯完全不用動。
/// </summary>
public interface IForceFeedbackDevice
{
    string Name { get; }
    bool IsConnected { get; }
    void Initialise();
    /// <summary>自動回正。saturation = 力道上限,coefficient = 硬度(力隨轉角爬升多快)。兩者必須分開。</summary>
    void SetSpring(float saturation, float coefficient);
    /// <summary>阻尼（方向盤變重）0~100</summary>
    void SetDamper(float strength);
    /// <summary>震動 0~100 與頻率 Hz</summary>
    void SetVibration(float strength, float hz);
    void Stop();
}

/// <summary>
/// 空裝置：沒有 DLL 或沒插方向盤時使用。
/// 所有效果照常計算（診斷 log 看得到數值），只是不送給硬體。
/// </summary>
public class NullForceFeedbackDevice : IForceFeedbackDevice
{
    public string Name => "無（尚未接上方向盤 SDK）";
    public bool IsConnected => false;
    public void Initialise() { }
    public void SetSpring(float saturation, float coefficient) { }
    public void SetDamper(float strength) { }
    public void SetVibration(float strength, float hz) { }
    public void Stop() { }
}
