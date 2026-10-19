using System;
using UnityEngine;

public class HeadCheckDetector : MonoBehaviour
{
    [Header("綁定物件 (圓柱體)")]
    public Transform vrCamera;          // Main Camera (VR 頭盔)
    public Transform carTransform;      // 車體 Transform (作為相對角度基準)
    public Renderer leftCylinderRenderer;   // 左側圓柱體的 Renderer (代替原本的 Box)
    public Renderer rightCylinderRenderer;  // 右側圓柱體的 Renderer (代替原本的 Box)

    [Header("發光材質設定")]
    public Material redGlowMaterial;    // 拖入你做好的紅色發光材質
    public Material greenGlowMaterial;  // 拖入你做好的綠色發光材質

    [Header("偵測參數")]
    public float targetAngle = 30f;        // 門檻角度
    public float requiredDwellTime = 0.3f; // 需維持注視的時間 (秒)

    private bool isLeftPassed = false;
    private bool isRightPassed = false;
    private float leftTimer = 0f;
    private float rightTimer = 0f;
    private bool isDetecting = false;

    private Action onCompleteCallback; // 用於回傳訊號給程式 A 的委派

    void Awake()
    {
        // 預設將圓柱體隱藏
        SetCylindersVisible(false);
    }

    // 由程式 A 呼叫啟動
    public void StartDetection(Action callbackOnComplete)
    {
        onCompleteCallback = callbackOnComplete;
        
        // 1. 重置狀態
        isLeftPassed = false;
        isRightPassed = false;
        leftTimer = 0f;
        rightTimer = 0f;

        // 2. 初始化套用紅色材質
        if (leftCylinderRenderer && redGlowMaterial) 
            leftCylinderRenderer.material = redGlowMaterial;

        if (rightCylinderRenderer && redGlowMaterial) 
            rightCylinderRenderer.material = redGlowMaterial;

        // 顯示兩個圓柱體
        SetCylindersVisible(true);
        isDetecting = true;
    }

    void Update()
    {
        if (!isDetecting || vrCamera == null || carTransform == null) return;

        // 計算頭部相對於車體正前方的 Y 軸夾角
        Vector3 carForward = Vector3.ProjectOnPlane(carTransform.forward, Vector3.up);
        Vector3 headForward = Vector3.ProjectOnPlane(vrCamera.forward, Vector3.up);
        float currentYaw = Vector3.SignedAngle(carForward, headForward, Vector3.up);

        // 1. 檢測向左擺頭 (角度為負)
        if (!isLeftPassed)
        {
            if (currentYaw < -targetAngle)
            {
                leftTimer += Time.deltaTime;
                if (leftTimer >= requiredDwellTime)
                {
                    isLeftPassed = true;
                    // 通過檢查：更換為綠色材質
                    if (leftCylinderRenderer && greenGlowMaterial) 
                        leftCylinderRenderer.material = greenGlowMaterial;

                    Debug.Log("✅ 左側擺頭達成！");
                }
            }
            else leftTimer = 0f;
        }

        // 2. 檢測向右擺頭 (角度為正)
        if (!isRightPassed)
        {
            if (currentYaw > targetAngle)
            {
                rightTimer += Time.deltaTime;
                if (rightTimer >= requiredDwellTime)
                {
                    isRightPassed = true;
                    // 通過檢查：更換為綠色材質
                    if (rightCylinderRenderer && greenGlowMaterial) 
                        rightCylinderRenderer.material = greenGlowMaterial;

                    Debug.Log("✅ 右側擺頭達成！");
                }
            }
            else rightTimer = 0f;
        }

        // 3. 當兩個方向都完成時
        if (isLeftPassed && isRightPassed)
        {
            isDetecting = false;
            SetCylindersVisible(false); // 隱藏圓柱體

            // 回傳訊號給程式 A
            onCompleteCallback?.Invoke();
        }
    }

    private void SetCylindersVisible(bool visible)
    {
        if (leftCylinderRenderer) leftCylinderRenderer.gameObject.SetActive(visible);
        if (rightCylinderRenderer) rightCylinderRenderer.gameObject.SetActive(visible);
    }
}