using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public Transform target;        // 拖入你的玩家車
    public Vector3 offset = new Vector3(0, 5, -10); // 與車子的距離偏移
    public float smoothSpeed = 0.125f; // 跟隨的平滑速度

    void LateUpdate() // 使用 LateUpdate 確保在車子移動後才更新相機位置
    {
        if (target == null) return;

        // 計算目標位置
        Vector3 desiredPosition = target.position + target.TransformDirection(offset);

        // 平滑移動
        Vector3 smoothedPosition = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed);
        transform.position = smoothedPosition;

        // 讓相機始終盯著車子看
        transform.LookAt(target);
    }
}