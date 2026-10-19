using UnityEngine;

public class FreeFlyCamera : MonoBehaviour
{
    [Header("移動速度設定")]
    [Tooltip("基礎移動速度")]
    public float moveSpeed = 10f;
    [Tooltip("按住 Shift 時的加速倍率")]
    public float sprintMultiplier = 2.5f;

    [Header("視角旋轉設定")]
    [Tooltip("滑鼠靈敏度")]
    public float lookSensitivity = 2f;

    private float rotationX = 0f;
    private float rotationY = 0f;

    void Start()
    {
        // 隱藏並鎖定滑鼠游標於畫面中央
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // 初始化旋轉角度為攝影機當前角度
        Vector3 currentAngles = transform.eulerAngles;
        rotationX = currentAngles.x;
        rotationY = currentAngles.y;
    }

    void Update()
    {
        HandleRotation();
        HandleMovement();

        // 按下 Escape 鍵切換滑鼠顯示狀態（方便測試）
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    private void HandleRotation()
    {
        // 只有在滑鼠被鎖定時才旋轉視角
        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

        rotationY += mouseX;
        rotationX -= mouseY;
        // 限制上下仰角，避免翻轉
        rotationX = Mathf.Clamp(rotationX, -89f, 89f);

        transform.rotation = Quaternion.Euler(rotationX, rotationY, 0f);
    }

    private void HandleMovement()
    {
        // 計算當前速度（是否按住 Left Shift 加速）
        float currentSpeed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift))
        {
            currentSpeed *= sprintMultiplier;
        }

        // 取得 WASD 軸向輸入
        float horizontal = Input.GetAxisRaw("Horizontal"); // A (-1) / D (+1)
        float vertical = Input.GetAxisRaw("Vertical");     // S (-1) / W (+1)

        // 計算平行移動向量
        Vector3 moveDirection = (transform.forward * vertical + transform.right * horizontal).normalized;

        // Space 鍵上升，Left Control 鍵下降
        if (Input.GetKey(KeyCode.Space))
        {
            moveDirection += Vector3.up;
        }
        if (Input.GetKey(KeyCode.LeftControl))
        {
            moveDirection += Vector3.down;
        }

        // 更新攝影機位置
        transform.position += moveDirection * currentSpeed * Time.deltaTime;
    }
}