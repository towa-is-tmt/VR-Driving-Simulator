using UnityEngine;
using UnityEngine.Events;

public class StraightLineDetector : MonoBehaviour
{
    [Header("速度設定")]
    [Tooltip("目標速度（km/h），達到此速度才開始累加計時")]
    public float targetSpeed      = 40f;
    [Tooltip("低於此速度直接重置所有計時（km/h）")]
    public float resetThreshold   = 36f;
    [Tooltip("速度在 36~39 之間的緩衝時間（秒），超過緩衝時間才重置")]
    public float bufferTime       = 2f;
    [Tooltip("需要維持目標速度的總時間（秒）")]
    public float requiredDuration = 5f;

    [Header("事件")]
    public UnityEvent onSuccess;

    [Header("即時狀態（Debug 用）")]
    [Tooltip("目前已累加的達標時間（秒）")]
    public float holdTimer   = 0f;
    [Tooltip("目前緩衝倒數計時（秒）")]
    public float bufferTimer = 0f;
    [Tooltip("是否已判定成功")]
    public bool  isSuccess   = false;

    private Rigidbody rb;

    private void Start()
    {
        // 玩家車 CAR 用的是 DriveCar/CarPhysicsController+Rigidbody，不是 DrivingKernel。
        // 直接讀車身 Rigidbody 速度（與 RampDetector 同公式），與車控系統無關。
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
        if (rb == null) rb = GetComponentInChildren<Rigidbody>();
        if (rb == null)
            Debug.LogError("[直線] 找不到 Rigidbody！請把此腳本掛在車輛上（CAR 子樹裡有車身剛體）。");
    }

    private void Update()
    {
        if (isSuccess) return;
        if (rb == null) return;

        float speed = rb.velocity.magnitude * 3.6f;   // m/s → km/h

        if (speed >= targetSpeed)
        {
            // ── 速度達標，累加計時，重置緩衝 ────────────────
            bufferTimer = 0f;
            holdTimer  += Time.deltaTime;
            Debug.Log($"[直線] 速度達標 {speed:F1} km/h，已累計：{holdTimer:F1}s / {requiredDuration}s");

            if (holdTimer >= requiredDuration)
            {
                isSuccess = true;
                Debug.Log("[直線] 成功！維持 40km/h 超過 3 秒！");
                onSuccess?.Invoke();
            }
        }
        else if (speed >= resetThreshold)
        {
            // ── 速度 36~39，暫停計時，開始緩衝倒數 ──────────
            bufferTimer += Time.deltaTime;
            Debug.Log($"[直線] 速度偏低 {speed:F1} km/h，緩衝倒數：{bufferTimer:F1}s / {bufferTime}s");

            if (bufferTimer >= bufferTime)
            {
                holdTimer   = 0f;
                bufferTimer = 0f;
                Debug.Log("[直線] 緩衝超時，重置計時！");
            }
        }
        else
        {
            // ── 速度低於 36，直接重置所有計時 ────────────────
            if (holdTimer > 0f || bufferTimer > 0f)
                Debug.Log($"[直線] 速度過低 {speed:F1} km/h，直接重置！");

            holdTimer   = 0f;
            bufferTimer = 0f;
        }
    }
}