using UnityEngine;
using UnityEngine.InputSystem;

public class G923SteeringWheel : MonoBehaviour
{
    [Header("模型設定")]
    // 在 Inspector 中，把方向盤模型 (Steering Wheel_LOD0) 拖進這個欄位
    public Transform virtualWheelModel;

    [Header("旋轉參數")]
    // G923 總轉角 900 度，所以單邊最大 450 度
    public float maxRotationAngle = 450f;

    [Header("💥 撞擊抖動")]
    // ⚠️ 抖動必須做在這個腳本裡面。
    // 底下 Update 每一幀都會覆寫 virtualWheelModel.localRotation，
    // 從外部去改那個 Transform 會在下一幀被蓋掉，接上 G923 後完全看不到效果。
    // 所以這裡把抖動角度「疊加」在轉向角上，兩者可以同時存在。
    //
    // 注意這動的是場景中的物件、不是相機，所以在 VR 裡不會造成暈眩。
    [Tooltip("最重的撞擊要抖動幾度")]
    public float shakeMaxAngle = 16f;
    [Tooltip("抖動的來回頻率，數字越大抖得越急")]
    public float shakeFrequency = 34f;
    [Tooltip("抖動衰減到停止要幾秒")]
    public float shakeDuration = 0.25f;

    // 儲存從 G923 讀取到的方向盤數值 (-1 ~ 1)
    private float steerInput;

    // 目前的抖動振幅，會隨時間衰減回 0
    private float shakeAmplitude;

    /// <summary>
    /// 這個函式會被 Player Input 元件呼叫。
    /// 請在 Player Input 的 Events > Player > steer 事件中連結此函式。
    /// </summary>
    public void OnSteer(InputAction.CallbackContext context)
    {
        // 💡 關鍵修復：只有在方向盤有實體訊號觸發（Started 或 Performed）時才讀取數值
        // 如果是 VR 環境因為沒動而誤觸發的 Canceled（放開）事件，直接忽略，不讓它歸零！
        if (context.started || context.performed)
        {
            steerInput = context.ReadValue<float>();
        }
    }

    /// <summary>
    /// 撞擊時由 PlayerCollisionAudio 呼叫，讓方向盤抖一下。
    /// </summary>
    /// <param name="severity01">撞擊力道 0~1，跟碰撞音效與動力懲罰用同一套分級</param>
    public void ApplyImpactShake(float severity01)
    {
        float target = shakeMaxAngle * Mathf.Clamp01(severity01);
        // 取較大值：連續撞擊時不會把正在進行的抖動洗掉
        shakeAmplitude = Mathf.Max(shakeAmplitude, target);
    }

    void Update()
    {
        if (virtualWheelModel == null) return;

        // 計算虛擬方向盤該轉到的角度
        float targetAngle = steerInput * maxRotationAngle;

        // 撞擊抖動：疊加在轉向角上，並隨時間衰減
        float shake = 0f;
        if (shakeAmplitude > 0.01f)
        {
            shake = Mathf.Sin(Time.time * shakeFrequency * Mathf.PI * 2f) * shakeAmplitude;
            shakeAmplitude = Mathf.MoveTowards(
                shakeAmplitude, 0f,
                shakeMaxAngle / Mathf.Max(0.01f, shakeDuration) * Time.deltaTime);
        }

        // 方向盤模型通常是繞著自身 Z 軸旋轉
        // 如果發現轉錯軸或方向相反，可改成 (targetAngle, 0, 0) 或 (0, targetAngle, 0)
        virtualWheelModel.localRotation = Quaternion.Euler(0, 0, -targetAngle + shake);
    }
}
