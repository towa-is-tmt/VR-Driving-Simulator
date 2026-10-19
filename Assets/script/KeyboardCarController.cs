using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class KeyboardCarController : MonoBehaviour, IPlayerVehicleState
{
    public float speed = 15f;
    public float turnSpeed = 50f;

    [Header("交通系統連動")]
    public bool isWaitingAtTrafficLight = false;

    private Rigidbody rb;
    private float verticalInput = 0f;
    private float horizontalInput = 0f;

    // 🟢 介面實作：直接讀輸入，反應最即時
    public bool IsReversing => verticalInput < -0.01f;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ; // 避免車身亂翻滾
    }

    void Update()
    {
        verticalInput = Input.GetAxis("Vertical");
        horizontalInput = Input.GetAxis("Horizontal");
    }

    void FixedUpdate()
    {
        // 🟢 改用 Rigidbody 移動，讓 velocity 真實反映在物理系統裡
        Vector3 moveDelta = transform.forward * verticalInput * speed;
        rb.velocity = new Vector3(moveDelta.x, rb.velocity.y, moveDelta.z); // 保留重力影響的 Y 軸

        float turn = horizontalInput * turnSpeed * Time.fixedDeltaTime;
        Quaternion turnRotation = Quaternion.Euler(0f, turn, 0f);
        rb.MoveRotation(rb.rotation * turnRotation);
    }

    public void SetTrafficLightStatus(bool stop)
    {
        isWaitingAtTrafficLight = stop;
        if (stop)
        {
            Debug.Log("收到紅燈訊號，請停車！");
        }
    }
}