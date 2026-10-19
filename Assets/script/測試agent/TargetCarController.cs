using UnityEngine;

public class TargetCarController : MonoBehaviour
{
    [Header("🏎️ 移動設定")]
    public float targetSpeed = 18f; // VR車的定速前進速度 (約時速 65 km/h)

    private Rigidbody rb;
    private Vector3 initialPosition;
    private Quaternion initialRotation;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        // 紀錄遊戲一開始的初始位置與角度，用來做回合重置
        initialPosition = transform.position;
        initialRotation = transform.rotation;
    }

    void FixedUpdate()
    {
        // 讓 VR 車不論如何都保持固定的前進速度
        Vector3 forwardVelocity = transform.forward * targetSpeed;
        // 保持原本的 Y 軸上下速度（避免重力出錯），強制設定前進速度
        rb.velocity = new Vector3(forwardVelocity.x, rb.velocity.y, forwardVelocity.z);
    }

    // 💡 關鍵：提供給白車 Agent 呼叫的重置巴掌
    public void ResetPosition()
    {
        transform.position = initialPosition;
        transform.rotation = initialRotation;
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }
}