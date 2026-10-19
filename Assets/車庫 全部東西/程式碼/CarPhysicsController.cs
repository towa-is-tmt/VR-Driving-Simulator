using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

// 實作 IPlayerVehicleState：危險因子系統（ReverseThreatAICar 等）會用這個介面
// 查詢玩家是否在倒車。原本只有 KeyboardCarController 有實作，換成 G923 後就斷了。
public class CarPhysicsController : MonoBehaviour, IPlayerVehicleState
{
    public bool IsReversing => gearController != null
        && gearController.currentGear == CarGearController.AutomaticGear.R;

    /// <summary>目前實際送進物理的油門值（已合併 G923 與鍵盤、已平滑）。供卡死偵測判斷玩家是否真的想動。</summary>
    public float ThrottleInput => effectiveGas;
    /// <summary>目前實際的煞車值。踩著煞車不動不算卡死。</summary>
    public float BrakeInput => effectiveBrake;

    /// <summary>目前方向盤位置 -1~1（已合併 G923 與鍵盤）。力回饋要靠它分辨
    /// 「駕駛正在轉」還是「方向盤正在自己回中」—— 兩者該有的阻尼完全不同。</summary>
    public float SteerInput => effectiveSteer;
    /// <summary>目前檔位是否為 P。P 檔動不了是正常的，不算卡死。</summary>
    public bool IsParked => gearController != null
        && gearController.currentGear == CarGearController.AutomaticGear.P;

    private Rigidbody rb;
    private float msToKmH = 3.6f;

    [Header("3D 儀表板顯示")]
    public TextMeshPro speedText;
    public TextMeshPro gearText;

    [Header("檔位系統（改用 CarGearController，考試偵測也讀它）")]
    public CarGearController gearController;   // 留空會自動在同物件 / 子物件 / 父物件尋找

    [Header("🚗 4 個車輪碰撞器參照")]
    public WheelCollider frontLeftWheel;
    public WheelCollider frontRightWheel;
    public WheelCollider rearLeftWheel;
    public WheelCollider rearRightWheel;

    [Header("🛞 車輪 3D 模型參照 (用於視覺同步)")]
    public Transform flMesh;
    public Transform frMesh;
    public Transform rlMesh;
    public Transform rrMesh;

    [Header("⚙️ 引擎與動力參數")]
    public float motorTorque = 1500f;
    [Tooltip("開啟後動力會隨車速遞減，模擬真實引擎：起步推力足、高速逐漸推不動。\n關掉就是原本的等加速（從 0 到極速動力一樣大，很不真實）")]
    public bool enableSpeedTorqueFalloff = true;
    [Range(0f, 1f)]
    [Tooltip("衰減強度。1 = 到極速時動力歸零（最真實）；0.6 = 高速仍保有四成動力，比較好開")]
    public float torqueFalloffStrength = 1f;
    public float brakeTorque = 3000f;
    public float maxForwardSpeedKmH = 180f;
    public float maxReverseSpeedKmH = 50f;

    [Header("🛞 物理擬真（子步數 / 轉向 / 煞車配比）")]
    // Unity 的 WheelCollider 預設每個物理幀只算少數幾次子步，車速一快輪胎力就算不準
    // ——「飄」「抓不住」「煞車軟」大多來自這裡。真實感的 CP 值最高的一項。
    [Tooltip("低於這個速度(m/s)用高子步數")]
    public float substepSpeedThreshold = 5f;
    [Tooltip("低速時的子步數。越高越準也越吃效能")]
    public int substepsBelowThreshold = 12;
    [Tooltip("高速時的子步數")]
    public int substepsAboveThreshold = 6;

    // 真車方向盤轉一樣的角度,時速 100 的轉向幅度遠小於時速 20。
    // 固定角度會讓高速輕輕一轉就甩尾、低速又覺得轉不夠。
    [Tooltip("開啟速度感應轉向：車速越快,最大轉向角越小")]
    public bool enableSpeedSensitiveSteering = true;
    [Tooltip("到達這個時速時,轉向角降到下面的最小值")]
    public float steerReduceAtKmH = 120f;
    [Tooltip("高速時的最小轉向角(度)")]
    public float minSteerAngleAtSpeed = 12f;

    // 真車煞車力前後不同(前輪負責大部分制動,因為煞車時重心前移)。
    // 四輪均等會讓後輪太早鎖死,車尾容易滑出去。
    [Tooltip("前輪分配到的煞車比例。0.7 = 前七後三,接近真車")]
    [Range(0.5f, 1f)]
    public float frontBrakeBias = 0.7f;

    // 真車放開油門的減速來自引擎煞車 + 空氣阻力,速度越快掉得越快。
    // 固定值會讓高速滑行太久、低速又掉太急。
    [Tooltip("開啟隨速引擎煞車：車速越快,放開油門的減速越明顯")]
    public bool enableSpeedBasedEngineBrake = true;
    [Tooltip("引擎煞車在最高速時的倍率。2 = 極速時是基礎值的兩倍")]
    public float engineBrakeSpeedMultiplier = 2f;

    [Header("⛰️ 坡度補償")]
    // 真車的自排上坡會降檔,把被重力吃掉的推力補回來,所以同樣的油門深度
    // 在坡上跟平地的加速感差不多。這裡沒有變速箱,扭力從頭到尾是同一個數字,
    // 於是只能二選一:調到爬得上坡 → 平地變火箭;調到平地正常 → 爬不動。
    //
    // 補償量 = 車重 x 重力 x 坡度正弦 x 輪半徑 / 2(兩顆驅動輪),也就是
    // 「重力正好偷走多少推力就補多少」。平地時坡度正弦為 0,一點都不加,
    // 所以「平地加速太快」不會因為這個回來。
    [Tooltip("上坡自動補上被重力吃掉的那份扭力。平地完全不加,不會讓平地變快")]
    public bool enableSlopeCompensation = true;
    [Range(0f, 1.2f)]
    [Tooltip("補償比例。1 = 完全抵銷重力(上坡跟平地一樣快);0.85 = 上坡仍稍微吃力,比較真實")]
    public float slopeCompensationFactor = 0.85f;
    [Tooltip("下坡是否反向扣扭力(避免下坡衝太快)。預設不開,免得下坡踩油門沒反應")]
    public bool compensateDownhillToo = false;
    [Range(0.05f, 0.6f)]
    [Tooltip("坡度上限(正弦值)。0.4 約 24 度,防止極端地形算出誇張的扭力")]
    public float slopeCompensationMaxSin = 0.4f;

    [Header("⚖️ 重心 (爬坡與加速的關鍵,可在 Play 模式即時調)")]
    // 重心必須在「輪胎接地面上方」,否則縱向荷重轉移會整個反過來:
    // 踩油門時荷重應該往後壓、增加後驅輪的抓地,重心在接地面下方的話反而往前跑;
    // 上坡同理 —— 越爬後輪越輕,於是要灌很大的扭力才推得動,
    // 而那個扭力到了平地就變成火箭。
    //
    // 這台車的接地面大約在本地座標 y = -0.05(輪心 0.328 減掉輪世界半徑換算),
    // 所以舊值 -0.5 是在接地面「下方」約 0.45 個本地單位(世界約 0.67m)。
    // 真車的重心是在地面「上方」約 0.5m。
    //
    // 取捨:越高 → 爬坡與加速的抓地越好,但過彎側傾越明顯。
    // 舊值保留為預設,不動它的話行為跟以前完全一樣。
    [Tooltip("重心位置(本地座標)。選取這台車,用 Scene 視窗的黃色球對照青色的輪胎接地點 —— " +
             "黃球在青球「下面」就是錯的。 " +
             "-0.5 = 舊值(在接地面下方,荷重轉移是反的); " +
             "0.0 = 與接地面齊平,建議第一個試; " +
             "0.25 = 世界約離地 0.45m,接近真車")]
    public Vector3 centerOfMassOffset = new Vector3(0f, -0.5f, 0f);
    [Tooltip("取消勾選 = 完全不覆寫重心,交給 Unity 依碰撞體自動計算")]
    public bool overrideCenterOfMass = true;

    [Header("📐 轉向參數")]
    public float maxSteerAngle = 35f;

    [Header("💥 撞擊後果")]
    [Tooltip("撞到 AI 車後短暫切斷動力並損失車速。這是「物理後果」而不是視覺特效，\n在 VR 裡完全安全 —— 玩家是靠速度變化感受到撞擊，不是靠畫面晃動")]
    public bool enableImpactPenalty = true;
    [Tooltip("最重的撞擊要切斷動力幾秒。輕撞會按比例縮短")]
    public float impactPowerCutSeconds = 0.8f;
    [Range(0f, 1f)]
    [Tooltip("最重的撞擊要直接扣掉多少比例的車速。0.35 = 掉三成半")]
    public float impactSpeedLoss = 0.35f;
    [Tooltip("撞擊速度達到這個值就算最重（m/s）。跟碰撞音效用同一套分級")]
    public float impactSevereSpeed = 12f;

    private float powerCutUntilTime = -1f;

    // 已經寫進 Rigidbody 的重心值。跟 Inspector 上的比對,只有真的改了才重設 ——
    // 這樣就能在 Play 模式下邊拖邊感覺,不必停掉重進。
    private Vector3 appliedCenterOfMass = new Vector3(-9999f, -9999f, -9999f);

    [Header("🛑 引擎煞車（放開油門時的自然減速）")]
    // Rigidbody 的 Drag 是 0、WheelCollider 滾動阻力也極小，
    // 放開油門若不補一點煞車力，車子會一直維持時速滑行，跟真車完全不同。
    // 想要更明顯的減速就調大；設 0 則回到原本的純滑行。
    public float engineBrakeTorque = 300f;

    [Header("🔊 引擎音效設定")]
    public AudioSource engineAudioSource;
    public float minPitch = 0.7f;
    public float maxPitch = 2.5f;
    public float minVolume = 0.3f;
    public float maxVolume = 0.9f;

    [Header("🦶 踏板方向校正")]
    // 兩顆踏板的反向都已在 InputSystem_Actions 的 binding 層用 Invert processor 處理
    // （moveon → /z、stop → /rz），所以這兩個開關預設都是關的，程式只負責 -1~1 → 0~1。
    // 換了別的方向盤才需要動：打開 debugInput 看 Console，
    // 「放開 = 0.00、踩到底 ≈ 1.00」才是對的；若放開時是 1.00 就把對應開關打勾。
    public bool invertGasPedal = false;
    public bool invertBrakePedal = false;

    [Header("⌨️ 鍵盤備用操控")]
    // 刻意走舊版 Input Manager (Input.GetKey)，完全不經過 InputSystem_Actions，
    // 所以 G923 的 binding / processors 一個字都不用改，兩種裝置可以同時插著用。
    [Tooltip("W = 油門、S = 煞車、A/D = 轉向。G923 有輸入時以方向盤為準")]
    public bool enableKeyboardControl = true;
    [Tooltip("按住 A/D 時轉向角每秒變化多少")]
    public float keyboardSteerSpeed = 2.5f;
    [Tooltip("放開 A/D 後方向盤每秒回正多少")]
    public float keyboardSteerReturnSpeed = 4f;

    [Header("🦶 油門反應曲線（模擬引擎不會瞬間歸零）")]
    [Tooltip("踩下油門時每秒上升多少，越大越靈敏")]
    public float gasRiseSpeed = 5f;
    [Tooltip("放開油門時每秒下降多少，越小回落越慢、越像真車")]
    public float gasFallSpeed = 1.5f;

    // 煞車原本完全沒有平滑 —— 踏板值直接送進物理,所以踩下去是「瞬間到位」,
    // 跟油門那種漸進的手感對不起來。這裡補上跟油門同一套的上升/下降速率。
    [Tooltip("踩下煞車時每秒上升多少。越大越像一腳踩死,越小越漸進")]
    public float brakeRiseSpeed = 4f;
    [Tooltip("放開煞車時每秒下降多少。通常比上升快一點,放腳要乾脆")]
    public float brakeFallSpeed = 6f;

    // 真實踏板的行程不是線性的:前段細膩、後段才給大力。線性映射會讓
    // 「輕輕踩一點」就衝出去,完全沒有微調空間 —— 路邊停車與跟車最需要那段。
    [Header("🦶 踏板行程曲線")]
    [Tooltip("油門的行程曲線指數。1 = 線性;1.5~2 = 前段更細膩、要踩深才給大力")]
    [Range(1f, 3f)]
    public float pedalCurveGamma = 1.6f;
    // 煞車跟油門必須分開:G923 的煞車踏板是漸進式橡膠,越踩越硬,
    // 用跟油門一樣的 1.6 會讓「已經很費力了卻還沒煞多少」雪上加霜。
    [Tooltip("煞車的行程曲線指數,跟油門分開調。1 = 線性;大於 1 = 初段更軟;" +
             "小於 1 = 初段更咬,用來補償踏板太硬")]
    [Range(0.5f, 3f)]
    public float brakeCurveGamma = 1f;

    [Header("🦶 踏板行程校正 (硬體踏板太硬時用)")]
    // G923 的煞車踏板內部有漸進式橡膠,越踩越硬 —— 實際開車根本踩不到底,
    // 於是行程的後半段永遠用不到:腳已經很痠,遊戲裡卻還沒全力煞車。
    //
    // 這裡把「原始行程的哪一段」對應到「0~100% 的煞車」。例如 Full At 設 0.6,
    // 就是踩到六成行程即視為全力煞車,不必把踏板壓死。
    // 這是模擬賽車圈處理硬踏板的標準做法,不用改硬體。
    [Range(0f, 0.4f)]
    [Tooltip("煞車死區:低於這個原始行程一律當成沒踩。踏板歸零不準而導致一直輕微拖煞時調大")]
    public float brakeInputDeadzone = 0f;
    [Range(0.3f, 1f)]
    [Tooltip("踩到原始行程的多少比例就算 100% 煞車。踏板太硬踩不到底就調小;0.6 = 踩六成就是全力煞車")]
    public float brakeInputFullAt = 1f;
    [Range(0f, 0.4f)]
    [Tooltip("油門死區。同上,通常不用動")]
    public float gasInputDeadzone = 0f;
    [Range(0.3f, 1f)]
    [Tooltip("踩到原始行程的多少比例就算 100% 油門")]
    public float gasInputFullAt = 1f;

    [Header("除錯：即時監看輸入")]
    public bool debugInput = false;

    // 來自 G923 的原始輸入
    private float gasInput;
    private float brakeInput;
    private float steerInput;

    // 來自鍵盤的輸入
    private float keyboardGas;
    private float keyboardBrake;
    private float keyboardSteer;

    // 合併並平滑後、真正送進物理運算的值
    private float effectiveGas;
    private float effectiveBrake;
    private float effectiveSteer;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            Debug.LogError("[車輛] 找不到 Rigidbody！車子不會動。", this);
            enabled = false;
            return;
        }
        SyncCenterOfMass();   // 重心改由 Inspector 的 centerOfMassOffset 決定(預設值就是原本的 0,-0.5,0)

        // 🛞 子步數只要在任一個 WheelCollider 上呼叫就會套用到整台車。
        //    不設的話高速時輪胎力算不準,煞車與抓地都會軟掉。
        if (frontLeftWheel != null)
            frontLeftWheel.ConfigureVehicleSubsteps(
                substepSpeedThreshold, substepsBelowThreshold, substepsAboveThreshold);

        // 自動抓 CarGearController（同物件 / 子物件 / 父物件）
        if (gearController == null) gearController = GetComponent<CarGearController>();
        if (gearController == null) gearController = GetComponentInChildren<CarGearController>();
        if (gearController == null) gearController = GetComponentInParent<CarGearController>();
        if (gearController == null)
            Debug.LogError("[車輛] 找不到 CarGearController！檔位無法讀取。", this);
        else if (!gearController.enabled)
            // GetComponent 連停用的元件也找得到，所以上面不會是 null，但它的 Update 不會跑：
            // 鍵盤 1/2/3 換檔會失效，車子卡在目前檔位（P 檔的話等於完全開不動）。
            Debug.LogWarning($"[車輛] CarGearController 元件是「停用」狀態！鍵盤 1/2/3 換檔不會作用，" +
                             $"車子會卡在 {gearController.currentGear} 檔。請在 Inspector 把該元件勾選啟用。", gearController);
    }

    #region 接收 G923 輸入訊號（油門 / 煞車 / 方向盤）
    // 踏板一律比照 OnSteer：只在真的有訊號(started/performed)時更新，忽略 canceled。
    // 戴上 VR 後 OpenXR 會搶焦點/重置裝置，不斷送出假的 canceled；類比軸只在數值變化時
    // 才觸發 performed，所以腳定住不動時若被 canceled 歸零，就會一直卡在 0 —— 這就是
    // 「戴 VR 踩油門要一直狂壓才會慢慢走、摘掉 VR 卻正常」的原因。真的放開踏板時，
    // 行程 0.5→0.2→0 的連續變化會一路觸發 performed，最後自然歸零，所以忽略 canceled 是安全的。
    public void OnMoveon(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
            gasInput = ProcessPedalInput(context.ReadValue<float>(), invertGasPedal);
    }

    public void OnStop(InputAction.CallbackContext context)
    {
        if (context.started || context.performed)
            brakeInput = ProcessPedalInput(context.ReadValue<float>(), invertBrakePedal);
    }

    public void OnSteer(InputAction.CallbackContext context)
    {
        // 只有方向盤真的有轉動訊號才讀，避免 VR 手定格誤觸發 Canceled 把轉向歸零
        if (context.started || context.performed)
        {
            steerInput = context.ReadValue<float>();
        }
    }

    // G923 踏板原始值是 -1(放開) ~ 1(踩到底)，統一線性映射到 0 ~ 1。
    // 注意不要拆成正負兩段處理，否則踏板經過中點時數值會斷掉（0.5 → 0）。
    private float ProcessPedalInput(float rawValue, bool invert)
    {
        if (invert) rawValue = -rawValue;
        return Mathf.Clamp01((rawValue + 1f) / 2f);
    }
    #endregion

    #region 舊版 Player Input 綁定相容層（原本 dd/rr/pp 綁在本腳本上，轉呼叫 CarGearController）
    public void Ondd(InputAction.CallbackContext context) { if (gearController != null) gearController.Ondd(context); }
    public void Onrr(InputAction.CallbackContext context) { if (gearController != null) gearController.Onrr(context); }
    public void Onpp(InputAction.CallbackContext context) { if (gearController != null) gearController.Onpp(context); }
    #endregion

    /// <summary>
    /// 讀鍵盤並把 G923 與鍵盤合併成一組實際輸入。
    /// 油門另外做「上升快、下降慢」的平滑，避免放開油門時動力瞬間歸零。
    /// </summary>
    private void UpdateInputSources()
    {
        // ── 鍵盤（舊版 Input Manager，不影響 Input Actions）──
        if (enableKeyboardControl)
        {
            keyboardGas = Input.GetKey(KeyCode.W) ? 1f : 0f;
            keyboardBrake = Input.GetKey(KeyCode.S) ? 1f : 0f;

            float dir = 0f;
            if (Input.GetKey(KeyCode.A)) dir -= 1f;
            if (Input.GetKey(KeyCode.D)) dir += 1f;

            keyboardSteer = (Mathf.Abs(dir) > 0.01f)
                ? Mathf.MoveTowards(keyboardSteer, dir, keyboardSteerSpeed * Time.deltaTime)
                : Mathf.MoveTowards(keyboardSteer, 0f, keyboardSteerReturnSpeed * Time.deltaTime);
        }
        else
        {
            keyboardGas = 0f;
            keyboardBrake = 0f;
            keyboardSteer = Mathf.MoveTowards(keyboardSteer, 0f, keyboardSteerReturnSpeed * Time.deltaTime);
        }

        // ── 踏板行程校正：先把「實際用得到的那段行程」拉伸成 0~1 ──
        //    只作用在方向盤踏板上;鍵盤本來就是 0/1,不需要校正。
        float deviceGas = RemapPedalTravel(gasInput, gasInputDeadzone, gasInputFullAt);
        float deviceBrake = RemapPedalTravel(brakeInput, brakeInputDeadzone, brakeInputFullAt);

        // ── 合併：兩種來源取較大者，誰有動作就聽誰 ──
        float targetGas = Mathf.Max(deviceGas, keyboardGas);
        float targetBrake = Mathf.Max(deviceBrake, keyboardBrake);
        effectiveSteer = (Mathf.Abs(steerInput) > 0.01f) ? steerInput : keyboardSteer;

        // ── 踏板行程曲線：油門與煞車各用各的指數 ──
        if (pedalCurveGamma > 1.001f)
            targetGas = Mathf.Pow(Mathf.Clamp01(targetGas), pedalCurveGamma);
        if (Mathf.Abs(brakeCurveGamma - 1f) > 0.001f)
            targetBrake = Mathf.Pow(Mathf.Clamp01(targetBrake), brakeCurveGamma);

        // ── 油門平滑：踩下用 gasRiseSpeed、放開用 gasFallSpeed ──
        float rate = (targetGas > effectiveGas) ? gasRiseSpeed : gasFallSpeed;
        effectiveGas = Mathf.MoveTowards(effectiveGas, targetGas, rate * Time.deltaTime);

        // ── 煞車平滑：同一套做法,讓踩煞車也是漸進的 ──
        float brakeRate = (targetBrake > effectiveBrake) ? brakeRiseSpeed : brakeFallSpeed;
        effectiveBrake = Mathf.MoveTowards(effectiveBrake, targetBrake, brakeRate * Time.deltaTime);
    }

    /// <summary>
    /// 把踏板「實際用得到的那段行程」拉伸成完整的 0~1。
    /// deadzone 以下當成沒踩,fullAt 以上一律當成踩到底。
    /// 兩個參數維持預設 (0 和 1) 時完全不改變任何數值。
    /// </summary>
    private static float RemapPedalTravel(float raw01, float deadzone, float fullAt)
    {
        float lo = Mathf.Clamp(deadzone, 0f, 0.9f);
        float hi = Mathf.Max(fullAt, lo + 0.05f);   // 防止兩者重疊造成除以零
        if (lo <= 0.0001f && hi >= 0.9999f) return Mathf.Clamp01(raw01);
        return Mathf.Clamp01((raw01 - lo) / (hi - lo));
    }

    void Update()
    {
        // 放在 gearController 檢查之前 —— 就算檔位元件掉了,重心還是要能調
        SyncCenterOfMass();

        if (gearController == null) return;

        UpdateInputSources();

        if (debugInput)
            Debug.Log($"[車輛] 檔位 {gearController.currentGear}" +
                      $" | 油門 原始 {gasInput:F2} → 實際 {effectiveGas:F2}" +
                      $" | 煞車 原始 {brakeInput:F2} → 實際 {effectiveBrake:F2}" +
                      $" | 轉向 {effectiveSteer:F2}");

        float speedKmH = rb.velocity.magnitude * msToKmH;

        // 1. 更新儀表板 UI
        if (speedText != null) speedText.text = $"{Mathf.RoundToInt(speedKmH)} km/h";
        if (gearText != null) updateGearUI();

        // 2. 同步輪胎 3D 模型
        UpdateWheelMeshPose(frontLeftWheel, flMesh);
        UpdateWheelMeshPose(frontRightWheel, frMesh);
        UpdateWheelMeshPose(rearLeftWheel, rlMesh);
        UpdateWheelMeshPose(rearRightWheel, rrMesh);

        // 3. 動態引擎音效
        UpdateEngineAudio(speedKmH);
    }

    void FixedUpdate()
    {
        if (gearController == null) return;

        var gear = gearController.currentGear;

        float currentSpeedKmH = rb.velocity.magnitude * msToKmH;
        float movingDirection = Vector3.Dot(rb.velocity, transform.forward);

        // 🛞 速度感應轉向:車速越快,方向盤同樣角度對應的輪胎轉向越小。
        //    真車靠轉向齒比與車身動態自然產生這個效果;固定角度的話,
        //    高速輕輕一轉就會甩尾,低速又會覺得轉不夠。
        float steerLimit = maxSteerAngle;
        if (enableSpeedSensitiveSteering && steerReduceAtKmH > 1f)
        {
            float t = Mathf.Clamp01(currentSpeedKmH / steerReduceAtKmH);
            steerLimit = Mathf.Lerp(maxSteerAngle, minSteerAngleAtSpeed, t);
        }

        frontLeftWheel.steerAngle = effectiveSteer * steerLimit;
        frontRightWheel.steerAngle = effectiveSteer * steerLimit;

        // P 檔：強制切斷動力並鎖死煞車
        if (gear == CarGearController.AutomaticGear.P)
        {
            ApplyMotor(0f);
            ApplyBrake(brakeTorque);
            return;
        }

        // 撞擊後的動力中斷：這段期間就算油門踩到底也不會有動力
        bool powerCut = Time.time < powerCutUntilTime;
        float cleanGasInput = (powerCut || effectiveGas < 0.05f) ? 0f : effectiveGas;

        // 換檔安全保護與自動減速機制
        bool isGearMismatched = (gear == CarGearController.AutomaticGear.D && movingDirection < -0.5f) ||
                                (gear == CarGearController.AutomaticGear.R && movingDirection > 0.5f);

        if (isGearMismatched && currentSpeedKmH > 1f)
        {
            ApplyMotor(0f);
            ApplyBrake(brakeTorque * 0.5f);
            if (effectiveBrake > 0.05f) ApplyBrake(effectiveBrake * brakeTorque);
            return;
        }

        // 正常動力輸出
        float allowedMaxSpeed = (gear == CarGearController.AutomaticGear.R) ? maxReverseSpeedKmH : maxForwardSpeedKmH;
        float bufferRange = 5f;
        float torqueFactor = 1f;

        if (currentSpeedKmH > (allowedMaxSpeed - bufferRange))
        {
            float overSpeedRatio = (currentSpeedKmH - (allowedMaxSpeed - bufferRange)) / bufferRange;
            torqueFactor = Mathf.Clamp01(1f - overSpeedRatio);
        }

        // 真實引擎在固定功率下推力隨車速下降（F = P / v）。
        // 少了這一段，動力從起步到極速都一樣大，加速曲線會是直線，開起來像電動玩具車。
        float powerFalloff = 1f;
        if (enableSpeedTorqueFalloff)
        {
            float speedRatio = Mathf.Clamp01(currentSpeedKmH / Mathf.Max(1f, allowedMaxSpeed));
            powerFalloff = Mathf.Clamp01(1f - speedRatio * torqueFalloffStrength);
        }

        float torqueDirection = (gear == CarGearController.AutomaticGear.D) ? 1f : -1f;
        float currentTorque = cleanGasInput * motorTorque * torqueDirection * torqueFactor * powerFalloff;

        // ⛰️ 坡度補償:把重力沿行進方向偷走的那份推力補回來(見上面欄位的說明)
        if (enableSlopeCompensation && cleanGasInput > 0f && rb != null)
        {
            // 行進方向 = 車頭乘上檔位方向,倒車時「上坡」是往車尾那一側爬
            Vector3 travelDir = transform.forward * torqueDirection;
            float slopeSin = Mathf.Clamp(Vector3.Dot(travelDir, Vector3.up),
                                         -slopeCompensationMaxSin, slopeCompensationMaxSin);

            if (compensateDownhillToo || slopeSin > 0f)
            {
                float wheelR = DriveWheelRadiusWorld();
                float extraPerWheel = rb.mass * 9.81f * slopeSin * wheelR * slopeCompensationFactor * 0.5f;
                currentTorque += cleanGasInput * extraPerWheel * torqueDirection;
            }
        }

        ApplyMotor(currentTorque);

        if (effectiveBrake > 0.05f)
            ApplyBrake(effectiveBrake * brakeTorque);
        else if (cleanGasInput <= 0f)
        {
            // 🛞 隨速引擎煞車:真車放開油門的減速來自引擎阻力 + 空氣阻力,
            //    速度越快掉得越快。固定值會讓高速滑行太久、低速又掉太急。
            float eb = engineBrakeTorque;
            if (enableSpeedBasedEngineBrake)
            {
                float t = Mathf.Clamp01(currentSpeedKmH / Mathf.Max(1f, maxForwardSpeedKmH));
                eb *= Mathf.Lerp(1f, engineBrakeSpeedMultiplier, t);
            }
            ApplyBrake(eb);
        }
        else
            ApplyBrake(0f);
    }

    /// <summary>
    /// 撞到 AI 車時由 PlayerCollisionAudio 呼叫，施加「頓一下 + 掉速」的物理後果。
    /// 力道依撞擊速度縮放，跟碰撞音效的輕撞／重撞用同一套分級。
    /// </summary>
    /// <param name="impactSpeed">碰撞的相對速度（m/s）</param>
    public void ApplyImpactPenalty(float impactSpeed)
    {
        if (!enableImpactPenalty || rb == null) return;

        float severity = Mathf.Clamp01(impactSpeed / Mathf.Max(0.1f, impactSevereSpeed));

        // 動力中斷：輕撞也至少頓 0.4 倍的時間，不然小擦撞完全沒感覺
        float cutSeconds = impactPowerCutSeconds * Mathf.Lerp(0.4f, 1f, severity);
        powerCutUntilTime = Mathf.Max(powerCutUntilTime, Time.time + cutSeconds);

        // 直接扣速度：真實撞擊會瞬間損失動能，光靠切斷動力太溫和
        float loss = impactSpeedLoss * severity;
        rb.velocity *= (1f - loss);

        // 油門平滑值也一起歸零，避免鬆開懲罰後動力瞬間回滿
        effectiveGas = 0f;

        if (debugInput)
            Debug.Log($"[車輛] 撞擊懲罰：力道 {severity:F2}｜切斷動力 {cutSeconds:F2}s｜損失車速 {loss * 100f:F0}%");
    }

    /// <summary>
    /// 驅動輪的世界半徑。WheelCollider 的 radius 是本地單位,要乘上整條父物件的縮放。
    /// 這台車的縮放鏈是 2.54 x 1.5 = 3.81,所以 0.15 的設定值實際是 0.57m —— 差很多,
    /// 直接拿 radius 來算補償會少算將近四倍。
    /// </summary>
    private float DriveWheelRadiusWorld()
    {
        WheelCollider w = rearLeftWheel != null ? rearLeftWheel : frontLeftWheel;
        if (w == null) return 0.3f;
        return Mathf.Max(0.05f, w.radius * Mathf.Abs(w.transform.lossyScale.y));
    }

    void ApplyMotor(float torque)
    {
        rearLeftWheel.motorTorque = torque;
        rearRightWheel.motorTorque = torque;
    }

    void ApplyBrake(float torque)
    {
        // 前後配比:煞車時重心前移,前輪抓地力較大,本來就該分到比較多制動力。
        // 四輪均等會讓後輪太早鎖死,車尾容易滑出去。
        float front = torque * 2f * frontBrakeBias;
        float rear  = torque * 2f * (1f - frontBrakeBias);

        frontLeftWheel.brakeTorque = front;
        frontRightWheel.brakeTorque = front;
        rearLeftWheel.brakeTorque = rear;
        rearRightWheel.brakeTorque = rear;
    }

    void UpdateWheelMeshPose(WheelCollider collider, Transform meshTransform)
    {
        if (collider == null || meshTransform == null) return;
        Vector3 pos;
        Quaternion rot;
        collider.GetWorldPose(out pos, out rot);
        meshTransform.position = pos;
        meshTransform.rotation = rot;
    }

    void UpdateEngineAudio(float speedKmH)
    {
        if (engineAudioSource == null) return;

        if (gearController.currentGear == CarGearController.AutomaticGear.P)
        {
            engineAudioSource.pitch = Mathf.Lerp(engineAudioSource.pitch, minPitch, Time.deltaTime * 5f);
            engineAudioSource.volume = Mathf.Lerp(engineAudioSource.volume, minVolume, Time.deltaTime * 5f);
        }
        else
        {
            float allowedMaxSpeed = (gearController.currentGear == CarGearController.AutomaticGear.R) ? maxReverseSpeedKmH : maxForwardSpeedKmH;
            float speedFactor = Mathf.Clamp01(speedKmH / allowedMaxSpeed);

            float targetPitch = Mathf.Lerp(minPitch, maxPitch, speedFactor) + (effectiveGas * 0.2f);
            engineAudioSource.pitch = Mathf.Lerp(engineAudioSource.pitch, targetPitch, Time.deltaTime * 10f);

            float targetVolume = Mathf.Lerp(minVolume, maxVolume, effectiveGas);
            engineAudioSource.volume = Mathf.Lerp(engineAudioSource.volume, targetVolume, Time.deltaTime * 10f);
        }
    }

    void updateGearUI()
    {
        string gearString = gearController.currentGear.ToString();
        if (gearController.currentGear == CarGearController.AutomaticGear.P) gearText.text = $"<color=yellow>{gearString}</color>";
        else if (gearController.currentGear == CarGearController.AutomaticGear.R) gearText.text = $"<color=red>{gearString}</color>";
        else gearText.text = $"<color=green>{gearString}</color>";
    }

    /// <summary>
    /// 把 Inspector 上的重心同步進 Rigidbody。每幀呼叫,但只在數值真的改變時才寫入,
    /// 所以可以在 Play 模式下即時拖動、即時感覺,不用停掉重進。
    /// </summary>
    private void SyncCenterOfMass()
    {
        if (rb == null || !overrideCenterOfMass) return;
        if (appliedCenterOfMass == centerOfMassOffset) return;

        appliedCenterOfMass = centerOfMassOffset;
        rb.centerOfMass = centerOfMassOffset;
    }

#if UNITY_EDITOR
    // 重心跟輪胎接地面的相對高低,用眼睛看最快 ——
    // 黃球(重心)必須在青球(接地點)「上面」。在下面就是荷重轉移反了。
    private void OnDrawGizmosSelected()
    {
        if (!overrideCenterOfMass) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(transform.TransformPoint(centerOfMassOffset), 0.12f);

        Gizmos.color = Color.cyan;
        DrawContactGizmo(frontLeftWheel);  DrawContactGizmo(frontRightWheel);
        DrawContactGizmo(rearLeftWheel);   DrawContactGizmo(rearRightWheel);
    }

    private void DrawContactGizmo(WheelCollider w)
    {
        if (w == null) return;
        // WheelCollider 的 radius 是本地單位,要乘上整條父物件的縮放才是世界半徑
        float worldRadius = w.radius * w.transform.lossyScale.y;
        Vector3 wheelCentre = w.transform.TransformPoint(w.center);
        Gizmos.DrawWireSphere(wheelCentre - Vector3.up * worldRadius, 0.1f);
    }
#endif
}
