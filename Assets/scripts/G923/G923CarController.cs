using UnityEngine;

/// <summary>
/// 鍵盤車輛控制 — 供「操作方式＝鍵盤(PC)」時使用，取代 CarPhysicsController。
///
/// 物理行為刻意與 CarPhysicsController 一致，兩種模式手感才不會差太多：
///   ‧ 同樣讀 CarGearController 的檔位（P 切斷動力並鎖煞車、D 前進、R 後退）
///   ‧ 同樣有換檔安全保護（檔位與行進方向不符時先減速）
///   ‧ 同樣有極速限制、引擎煞車、完全停止
///
/// 操作：
///   W / ↑    油門        S / ↓    煞車
///   A / ←    左轉        D / →    右轉
///   空白鍵   手煞車
///   檔位沿用 CarGearController：1 = P、2 = D、3 = R
///
/// 掛在 vehicle（有 Rigidbody 的物件）上，並把元件**預設取消勾選**，
/// 再拖進 InputManager 的「鍵盤模式啟用」，由設定選單自動切換。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class G923CarController : MonoBehaviour, IPlayerVehicleState
{
    [Header("檔位系統（與 G923 共用）")]
    public CarGearController gearController;

    // 實作 IPlayerVehicleState：危險因子系統（ReverseThreatAICar、PlayerReverseBroadcaster 等）
    // 靠這個介面查詢玩家是否在倒車。判定方式刻意與 CarPhysicsController 一致，改用檔位而非
    // 油門輸入，G923 與鍵盤兩種操作模式給車流系統的答案才會一樣。
    public bool IsReversing => gearController != null
        && gearController.currentGear == CarGearController.AutomaticGear.R;

    [Header("🚗 4 個車輪碰撞器")]
    public WheelCollider frontLeftWheel;
    public WheelCollider frontRightWheel;
    public WheelCollider rearLeftWheel;
    public WheelCollider rearRightWheel;

    [Header("⚙️ 引擎與動力參數")]
    public float motorTorque = 1500f;
    public float brakeTorque = 3000f;
    public float maxForwardSpeedKmH = 80f;
    public float maxReverseSpeedKmH = 50f;

    [Header("📐 轉向參數")]
    public float maxSteerAngle = 35f;

    [Tooltip("鍵盤是數位輸入，靠這個做出漸進轉向的手感。數字越大轉得越快")]
    public float 轉向速度 = 4f;

    [Tooltip("放開左右鍵後方向盤自動回正的速度")]
    public float 回正速度 = 6f;

    [Header("🛞 滑行阻力")]
    [Tooltip("放開油門時的引擎煞車與滾動阻力")]
    public float 引擎煞車力 = 250f;

    [Tooltip("低於此速度且沒踩油門時直接鎖住車輪（km/h）")]
    public float 完全停止速度 = 2f;

    [Header("🔊 引擎音效")]
    public AudioSource engineAudioSource;
    public float minPitch = 0.7f;
    public float maxPitch = 2.5f;
    public float minVolume = 0.3f;
    public float maxVolume = 0.9f;

    [Header("除錯")]
    public bool debugInput = false;

    private Rigidbody rb;
    private const float msToKmH = 3.6f;

    private float steerInput;      // -1 ~ 1，漸進變化
    private float gasInput;        // 0 ~ 1
    private float brakeInput;      // 0 ~ 1

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = new Vector3(0, -0.5f, 0);

        if (gearController == null) gearController = GetComponent<CarGearController>();
        if (gearController == null) gearController = GetComponentInChildren<CarGearController>();
        if (gearController == null) gearController = GetComponentInParent<CarGearController>();
    }

    private void OnDisable()
    {
        // 被切換掉時清空輸入，避免下次啟用時沿用舊值
        steerInput = gasInput = brakeInput = 0f;
    }

    private void Update()
    {
        讀取鍵盤();

        if (debugInput && gearController != null)
            Debug.Log($"[鍵盤] 檔位 {gearController.currentGear} | 油門 {gasInput:F2} | " +
                      $"煞車 {brakeInput:F2} | 轉向 {steerInput:F2}");

        if (engineAudioSource != null) 更新引擎音效();
    }

    private void 讀取鍵盤()
    {
        // ── 轉向：漸進靠向目標值，放開則回正 ──
        float 目標轉向 = 0f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  目標轉向 -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) 目標轉向 += 1f;

        float 速度 = Mathf.Approximately(目標轉向, 0f) ? 回正速度 : 轉向速度;
        steerInput = Mathf.MoveTowards(steerInput, 目標轉向, 速度 * Time.deltaTime);

        // ── 油門與煞車 ──
        bool 油門鍵 = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
        bool 煞車鍵 = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)
                    || Input.GetKey(KeyCode.Space);

        gasInput   = 油門鍵 ? 1f : 0f;
        brakeInput = 煞車鍵 ? 1f : 0f;
    }

    private void FixedUpdate()
    {
        if (gearController == null) return;

        var gear = gearController.currentGear;
        float currentSpeedKmH = rb.velocity.magnitude * msToKmH;
        float movingDirection = Vector3.Dot(rb.velocity, transform.forward);

        frontLeftWheel.steerAngle  = steerInput * maxSteerAngle;
        frontRightWheel.steerAngle = steerInput * maxSteerAngle;

        // P 檔：切斷動力並鎖死煞車
        if (gear == CarGearController.AutomaticGear.P)
        {
            套用動力(0f);
            套用煞車(brakeTorque);
            return;
        }

        float cleanGas = (gasInput < 0.05f) ? 0f : gasInput;

        // 換檔安全保護：檔位和行進方向相反時先減速，不要硬倒推
        bool 檔位不符 = (gear == CarGearController.AutomaticGear.D && movingDirection < -0.5f) ||
                        (gear == CarGearController.AutomaticGear.R && movingDirection >  0.5f);

        if (檔位不符 && currentSpeedKmH > 1f)
        {
            套用動力(0f);
            套用煞車(brakeTorque * 0.5f);
            if (brakeInput > 0.05f) 套用煞車(brakeInput * brakeTorque);
            return;
        }

        // 極速限制（接近上限時扭力遞減）
        float 上限 = (gear == CarGearController.AutomaticGear.R) ? maxReverseSpeedKmH : maxForwardSpeedKmH;
        float 緩衝 = 5f;
        float 扭力係數 = 1f;
        if (currentSpeedKmH > 上限 - 緩衝)
            扭力係數 = Mathf.Clamp01(1f - (currentSpeedKmH - (上限 - 緩衝)) / 緩衝);

        float 方向 = (gear == CarGearController.AutomaticGear.D) ? 1f : -1f;
        套用動力(cleanGas * motorTorque * 方向 * 扭力係數);

        // 煞車與滑行阻力
        if (brakeInput > 0.05f)
        {
            套用煞車(brakeInput * brakeTorque);
        }
        else if (cleanGas < 0.05f)
        {
            套用煞車(currentSpeedKmH <= 完全停止速度 ? brakeTorque : 引擎煞車力);
        }
        else
        {
            套用煞車(0f);
        }
    }

    private void 套用動力(float torque)
    {
        rearLeftWheel.motorTorque  = torque;
        rearRightWheel.motorTorque = torque;
    }

    private void 套用煞車(float torque)
    {
        frontLeftWheel.brakeTorque  = torque;
        frontRightWheel.brakeTorque = torque;
        rearLeftWheel.brakeTorque   = torque;
        rearRightWheel.brakeTorque  = torque;
    }

    private void 更新引擎音效()
    {
        float speedKmH = rb.velocity.magnitude * msToKmH;

        if (gearController != null &&
            gearController.currentGear == CarGearController.AutomaticGear.P)
        {
            engineAudioSource.pitch  = Mathf.Lerp(engineAudioSource.pitch, minPitch, Time.deltaTime * 5f);
            engineAudioSource.volume = Mathf.Lerp(engineAudioSource.volume,
                                                  minVolume * GameSettings.引擎音效比例,
                                                  Time.deltaTime * 5f);
            return;
        }

        float 上限 = (gearController != null &&
                      gearController.currentGear == CarGearController.AutomaticGear.R)
                   ? maxReverseSpeedKmH : maxForwardSpeedKmH;

        float 速度比 = Mathf.Clamp01(speedKmH / 上限);

        float 目標音調 = Mathf.Lerp(minPitch, maxPitch, 速度比) + (gasInput * 0.2f);
        engineAudioSource.pitch = Mathf.Lerp(engineAudioSource.pitch, 目標音調, Time.deltaTime * 10f);

        // 音量隨油門變化，再乘上設定選單的引擎音效倍率（與 G923 模式一致）
        float 目標音量 = Mathf.Lerp(minVolume, maxVolume, gasInput) * GameSettings.引擎音效比例;
        engineAudioSource.volume = Mathf.Lerp(engineAudioSource.volume, 目標音量, Time.deltaTime * 10f);
    }
}
