using UnityEngine;

/// <summary>
/// 【線性鍵盤優化版】汽車驅動類 - 內建方向盤平滑模擬與物理數據追蹤
/// </summary>
public class DriveCar : MonoBehaviour, IPlayerVehicleState
{
    [Header("輪胎碰撞器 (Wheel Colliders)")]
    public WheelCollider flWheelCollider;
    public WheelCollider frWheelCollider;
    public WheelCollider rlWheelCollider;
    public WheelCollider rrWheelCollider;

    [Header("視覺模型 (Transforms)")]
    public Transform flWheelModel;
    public Transform frWheelModel;
    public Transform rlWheelModel;
    public Transform rrWheelModel;

    [Header("轉向構造 (Disc Brakes)")]
    public Transform flDiscBrake;
    public Transform frDiscBrake;

    [Header("驅動參數")]
    public float motorTorque = 1600f;  // 最大馬達扭力
    public float steerAngle = 38f;    // 最大轉向角度
    public float brakeTorque = 2000f;  // 煞車力量
    public Transform centerOfMass;    // 重心物體

    [Header("鍵盤線性模擬設定 (接方向盤前除錯用)")]
    [Tooltip("模擬方向盤轉動的速度 (每秒可以轉幾度)。數值越小，轉向越平滑，越像在轉真實方向盤")]
    public float keyboardSteerSpeed = 80f; 
    
    // 用來儲存目前平滑漸變後的即時轉向比例 (-1.0 到 1.0)
    private float currentSteerInput = 0f;

    // 🔧 目前的前後輸入 (W=1 / S=-1 / 放開=0),供下方 IPlayerVehicleState 讀取
    private float currentVerticalInput = 0f;

    /// <summary>
    /// 🔧 IPlayerVehicleState 介面實作:讓倒車情境的 AI (ReverseThreatAICar) 即時得知玩家正在倒車。
    /// 作法與舊的 KeyboardCarController 一致 —— 直接讀輸入,按下 S 的當下就成立;
    /// 若沒有這個介面,AI 會退回用 Rigidbody 速度方向判斷,要等車真的退後才成立,會慢半拍。
    /// </summary>
    public bool IsReversing => currentVerticalInput < -0.01f;

    private Rigidbody rb;

    private void Start()
    {
        rb = GetComponentInParent<Rigidbody>();
        if (rb == null)
        {
            Debug.LogError("<color=red>[汽車錯誤]</color> 在父階層找不到 Rigidbody！請確認頂層物件有掛剛體。");
            return;
        }

        // 徹底將剛體的初始速度與力道物理清空
        rb.velocity = Vector3.zero;        
        rb.angularVelocity = Vector3.zero; 

        if (centerOfMass != null)
        {
            rb.centerOfMass = centerOfMass.localPosition;
        }

        // 暴力初始化：強制清空所有輪胎可能殘留的馬達與煞車力矩
        ResetAllCollidersInit();
    }

    private void Update()
    {
        // 1. 前進後退訊號 (W/S) 保持原樣
        float v = 0f;
        if (Input.GetKey(KeyCode.W)) v = 1f;
        else if (Input.GetKey(KeyCode.S)) v = -1f;
        currentVerticalInput = v;   // 🔧 同步給 IPlayerVehicleState.IsReversing 使用

        // 2. 轉向系統：改為「目標點漸進式尋找」
        float targetSteerInput = 0f;
        if (Input.GetKey(KeyCode.D)) targetSteerInput = 1f;
        else if (Input.GetKey(KeyCode.A)) targetSteerInput = -1f;

        // 【線性緩衝核心】：使用 Mathf.MoveTowards 消除鍵盤非黑即白的跳變
        // 當你按下 D 時，數值會從 0 按照速率花費一點點時間平滑上升到 1；放開按鍵時則會慢慢平滑回彈到 0
        currentSteerInput = Mathf.MoveTowards(currentSteerInput, targetSteerInput, (keyboardSteerSpeed / steerAngle) * Time.deltaTime);

        // 計算出當前幀「正在平滑轉動中」的角度
        float calculatedSteerAngle = currentSteerInput * steerAngle;

        // 【Debug 日誌 1】實時監測純鍵盤 W/S 訊號與目前車速
        if (v != 0 || targetSteerInput != 0)
        {
            Debug.Log($"<color=cyan>[輸入監測]</color> 轉向目標值: {targetSteerInput} | 當前平滑角度: {calculatedSteerAngle:F1}° | 時速: {rb.velocity.magnitude:F2} m/s");
        }

        // 1. 動力系統 (後輪驅動)
        if (v != 0)
        {
            rlWheelCollider.motorTorque = v * motorTorque;
            rrWheelCollider.motorTorque = v * motorTorque;
            
            // 重要：踩油門時，必須確定解鎖「所有」輪胎的煞車！
            ClearAllBrakes();
        }
        else
        {
            // 沒踩油門時馬達歸零
            rlWheelCollider.motorTorque = 0;
            rrWheelCollider.motorTorque = 0;
        }

        // 2. 轉向系統應用 (套用平滑後的角度，這會讓輪胎跟預測線同步絲滑轉動)
        flWheelCollider.steerAngle = calculatedSteerAngle;
        frWheelCollider.steerAngle = calculatedSteerAngle;

        // 3. 煞車系統 (空格鍵)
        if (Input.GetKey(KeyCode.Space))
        {
            Debug.Log("<color=red>[動作監測]</color> 玩家正在按下空白鍵煞車！");
            ApplyBrakes();
        }
        else if (v == 0 && targetSteerInput == 0)
        {
            // 靜止時自動給予小量煞車防止滑行，其餘時間必須為 0
            rlWheelCollider.brakeTorque = 10f;
            rrWheelCollider.brakeTorque = 10f;
            flWheelCollider.brakeTorque = 0f;
            frWheelCollider.brakeTorque = 0f;
        }

        // 【Debug 日誌 2】每 30 幀檢查一次後輪真正的物理輸出，避免刷屏
        if (Time.frameCount % 30 == 0)
        {
            Debug.Log($"<color=yellow>[物理狀態回報]</color> 左後輪馬達扭力: {rlWheelCollider.motorTorque} | 煞車力: {rlWheelCollider.brakeTorque} | 輪胎RPM: {rlWheelCollider.rpm:F1}");
        }

        // 4. 同步視覺表現
        UpdateWheelVisuals();
    }

    private void ApplyBrakes()
    {
        flWheelCollider.brakeTorque = brakeTorque;
        frWheelCollider.brakeTorque = brakeTorque;
        rlWheelCollider.brakeTorque = brakeTorque;
        rrWheelCollider.brakeTorque = brakeTorque;
        rlWheelCollider.motorTorque = 0;
        rrWheelCollider.motorTorque = 0;
    }

    private void ClearAllBrakes()
    {
        flWheelCollider.brakeTorque = 0;
        frWheelCollider.brakeTorque = 0;
        rlWheelCollider.brakeTorque = 0;
        rrWheelCollider.brakeTorque = 0;
    }

    private void ResetAllCollidersInit()
    {
        flWheelCollider.motorTorque = 0; frWheelCollider.motorTorque = 0;
        rlWheelCollider.motorTorque = 0; rrWheelCollider.motorTorque = 0;
        flWheelCollider.brakeTorque = 0; frWheelCollider.brakeTorque = 0;
        rlWheelCollider.brakeTorque = 0; rrWheelCollider.brakeTorque = 0;
        Debug.Log("<color=green>[物理初始化]</color> 已成功對四個輪胎進行物理力矩歸零。");
    }

    private void UpdateWheelVisuals()
    {
        if (flDiscBrake == null || frDiscBrake == null || flWheelModel == null || frWheelModel == null || rlWheelModel == null || rrWheelModel == null)
        {
            return; // 防止欄位未填寫時報錯
        }

        // 1. 處理轉向 (左右擺動)：應該作用在「父物件」上
        Vector3 steerEuler = flDiscBrake.localEulerAngles;
        steerEuler.y = flWheelCollider.steerAngle;
        flDiscBrake.localEulerAngles = steerEuler;
        
        Vector3 steerEulerR = frDiscBrake.localEulerAngles;
        steerEulerR.y = frWheelCollider.steerAngle;
        frDiscBrake.localEulerAngles = steerEulerR;

        // 2. 處理滾動 (前後滾動)：應該作用在「子物件」上
        float rotationDegree = flWheelCollider.rpm * 6 * Time.deltaTime;
        
        flWheelModel.Rotate(Vector3.right * rotationDegree, Space.Self);
        frWheelModel.Rotate(Vector3.right * rotationDegree, Space.Self);
        
        rlWheelModel.Rotate(Vector3.right * rotationDegree, Space.Self);
        rrWheelModel.Rotate(Vector3.right * rotationDegree, Space.Self);
    }
}