using UnityEngine;

public class testE : MonoBehaviour
{
    [Header("連結物件")]
    public Transform playerCamera;  // 玩家頭部 (Main Camera)
    public Transform quadCenter;    // 鏡子中心點 (後照鏡 Quad)

    [Header("位置死鎖設定")]
    public float depthDistance = 0.05f; // 相機固定在鏡子後方多遠

    [Header("動態 FOV 安全邊界設定")]
    public float minFOV = 35f;          // 玩家離得遠時的基礎視角 (通常設 35 ~ 45)
    public float maxFOV = 75f;          // 玩家貼到最近時的最大極限視角 (防止畫面拉滿爆掉)
    
    [Tooltip("玩家在距離鏡子多遠時，FOV 開始放大 (米)")]
    public float maxDistanceThreshold = 1.5f; 
    [Tooltip("玩家貼近到鏡子多遠時，FOV 達到最大值 (米)")]
    public float minDistanceThreshold = 0.2f; 

    private Camera cam;
    
    // 儲存你在 Scene 一開始調好的 XY 偏移量
    private float initialLocalX;
    private float initialLocalY;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (cam != null) cam.nearClipPlane = 0.01f;

        // 啟動時，先記錄你在 Scene 視窗擺放的 Camera 局部 XY 座標
        if (quadCenter != null)
        {
            Vector3 initialLocalPos = quadCenter.InverseTransformPoint(transform.position);
            initialLocalX = initialLocalPos.x;
            initialLocalY = initialLocalPos.y;
        }
    }

    void LateUpdate()
    {
        if (playerCamera == null || quadCenter == null || cam == null) return;

        // --- 1. 位置死鎖 (保留你在 Scene 調好的 XY) ---
        Vector3 lockedLocalPos = new Vector3(initialLocalX, initialLocalY, -depthDistance);
        transform.position = quadCenter.TransformPoint(lockedLocalPos);

        // --- 2. 計算真正的物理反射向量 (處理 Z 軸改變夾角問題) ---
        Vector3 incomingRay = (quadCenter.position - playerCamera.position).normalized;
        Vector3 reflectedRay = Vector3.Reflect(incomingRay, quadCenter.forward).normalized;

        // 讓相機看向反射光線的方向 (如果面向前面，請把 -reflectedRay 改為 reflectedRay)
        transform.rotation = Quaternion.LookRotation(reflectedRay, quadCenter.up);

        // --- 3. 動態 FOV 補償 (安全保護線性映射版) ---
        // 為了防止局部座標縮放出錯，我們這裡直接用「世界座標的實際物理距離」來計算遠近
        float realDistance = Vector3.Distance(playerCamera.position, quadCenter.position);

        // 使用 InverseLerp 計算出目前距離在 [min, max] 區間的比例 (回傳 0 ~ 1)
        // 當距離越靠近 minDistanceThreshold (越近)，t 就會越接近 1
        float t = Mathf.InverseLerp(maxDistanceThreshold, minDistanceThreshold, realDistance);

        // 限制 t 的範圍在 0 ~ 1 之間，確保絕對不會超出安全邊界
        t = Mathf.Clamp01(t);

        // 根據比例在 minFOV 與 maxFOV 之間滑順切換
        cam.fieldOfView = Mathf.Lerp(minFOV, maxFOV, t);
    }
}