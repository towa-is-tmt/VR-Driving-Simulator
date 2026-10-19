using UnityEngine;

public class SideMirror : MonoBehaviour
{
    [Header("旋轉速度")]
    public float adjustSpeed = 15f; // 每秒旋轉的角度

    [Header("角度限制")]
    public float minVerticalAngle = -15f;
    public float maxVerticalAngle = 15f;
    public float minHorizontalAngle = -20f;
    public float maxHorizontalAngle = 20f;

    private float rotationX = 0f;
    private float rotationY = 0f;

    void Start()
    {
        // 初始化角度
        Vector3 currentRotation = transform.localEulerAngles;
        
        // 處理 Unity 角度可能超過 180 度的情況 (例如 350 度轉回 -10 度)
        rotationX = (currentRotation.x > 180) ? currentRotation.x - 360 : currentRotation.x;
        rotationY = (currentRotation.y > 180) ? currentRotation.y - 360 : currentRotation.y;
    }

    void Update()
    {
        HandleKeyInput();
    }

    void HandleKeyInput()
    {
        float horizontalInput = 0f;
        float verticalInput = 0f;

        // 數字鍵 1 & 3 控制左右 (Horizontal)
        if (Input.GetKey(KeyCode.Keypad1)) horizontalInput = -1f;
        else if (Input.GetKey(KeyCode.Keypad3)) horizontalInput = 1f;

        // 數字鍵 2 & 5 控制上下 (Vertical)
        if (Input.GetKey(KeyCode.Keypad2)) verticalInput = -1f;
        else if (Input.GetKey(KeyCode.Keypad5)) verticalInput = 1f;

        if (horizontalInput != 0 || verticalInput != 0)
        {
            // 根據時間與速度計算旋轉量
            rotationY += horizontalInput * adjustSpeed * Time.deltaTime;
            rotationX -= verticalInput * adjustSpeed * Time.deltaTime;

            // 限制角度
            rotationX = Mathf.Clamp(rotationX, minVerticalAngle, maxVerticalAngle);
            rotationY = Mathf.Clamp(rotationY, minHorizontalAngle, maxHorizontalAngle);

            // 套用旋轉
            transform.localRotation = Quaternion.Euler(rotationX, rotationY, 0);
        }
    }
}