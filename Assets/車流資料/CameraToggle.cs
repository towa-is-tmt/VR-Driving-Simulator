using UnityEngine;

public class CameraToggle : MonoBehaviour
{
    public Transform carView;     // 拖入跟車的位置 (子物件)
    public Transform godView;    // 拖入上帝視角的位置 (空物件)
    public float godSmoothSpeed = 5f; // 上帝視角切換可以用平滑 Lerp
    private bool isGodMode = true;

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.C)) isGodMode = !isGodMode;
    }

    void LateUpdate()
    {
        if (carView == null || godView == null) return;

        if (isGodMode)
        {
            // 上帝視角：平滑移動
            transform.position = Vector3.Lerp(transform.position, godView.position, Time.deltaTime * godSmoothSpeed);

            // 強制修正旋轉：獲取目標旋轉，但強行將 Z 軸設為 0，防止傾斜
            Quaternion targetRot = godView.rotation;
            Vector3 euler = targetRot.eulerAngles;
            targetRot = Quaternion.Euler(euler.x, euler.y, 0); // 鎖死 Z 為 0

            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * godSmoothSpeed);
        }
        else
        {
            // 車子視角：直接同步（這部分你已經成功了，維持現狀）
            transform.position = carView.position;
            transform.rotation = carView.rotation;
        }
    }
}