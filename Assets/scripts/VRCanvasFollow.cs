using UnityEngine;

public class VRCanvasFollow : MonoBehaviour
{
    [Header("目標攝影機")]
    public Transform targetCamera;

    [Header("跟隨設定")]
    public float distance = 1.5f;     // UI 與眼睛的距離
    public float moveSpeed = 5.0f;    // 位置移動平滑速度
    public float rotateSpeed = 5.0f;  // 旋轉平滑速度

    void Start()
    {
        if (targetCamera == null && Camera.main != null)
        {
            targetCamera = Camera.main.transform;
        }
    }

    void LateUpdate()
    {
        if (targetCamera == null) return;

        // 計算 Canvas 的目標位置（攝影機前方 distance 公尺）
        Vector3 targetPosition = targetCamera.position + targetCamera.forward * distance;
        
        // 平滑移動位置
        transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * moveSpeed);

        // 讓 Canvas 始終面向玩家攝影機
        Quaternion targetRotation = Quaternion.LookRotation(transform.position - targetCamera.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * rotateSpeed);
    }
}