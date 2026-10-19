using UnityEngine;
using System.Collections;

public class testC : MonoBehaviour
{
    [Header("連結物件")]
    public Transform playerCamera;  // 玩家頭部
    public Transform quadCenter;    // 鏡子中心點 (空物件)

    [Header("設定")]
    public float depthDistance = 0.0f; // 固定在鏡後多遠
    [Range(0.1f, 2.0f)]
    public float sensitivity = 1.0f;   // 旋轉靈敏度 (物理正確通常為 1)

    private Camera cam;

    [Header("動態 FOV 設定 (純反比)")]
    public float minFOV = 35f;          // 玩家離得遠時的基礎視角
    public float maxFOV = 75f;          // 玩家貼得極近時的最大視角
    public float maxEffectDistance = 1.5f; // 開始觸發動態 FOV 的最遠距離
    public float minEffectDistance = 0.2f; // 達到最大 FOV 的最近距離

    [Header("🚀 VR 效能優化設定")]
    [Tooltip("後照鏡的更新頻率 (例如 30 代表每秒只渲染 30 幀，對開車來說非常流暢)")]
    public float targetFPS = 30f;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (quadCenter == null) quadCenter = transform.parent;

        // 💡 【核心優化第一步】：關閉自動渲染，改由我們用腳本手動控制它何時繪製
        if (cam != null)
        {
            cam.enabled = false; 
            // 啟動排程優化協程
            StartCoroutine(RenderCoroutine());
        }
    }

    // 💡 【核心優化第二步】：將原本的 LateUpdate 邏輯移入協程，控制執行頻率
    private IEnumerator RenderCoroutine()
    {
        // 隨機延遲一點點時間，錯開多個後照鏡的初始工作時間，避免它們卡在同一影格
        yield return new WaitForSeconds(Random.Range(0.0f, 0.05f));

        while (true)
        {
            if (playerCamera != null && quadCenter != null && cam != null)
            {
                // 執行原本的數學位置與角度計算
                UpdateMirrorLogic();

                // 💥 關鍵：計算完畢後，手動命令攝影機只「渲染這一影格」
                cam.Render();
            }

            // 根據設定的 targetFPS，決定下一次渲染要讓 CPU 休息多久
            // 如果設 30 FPS，每次畫完就會強迫休息約 0.033 秒
            yield return new WaitForSeconds(1.0f / targetFPS);
        }
    }

    /// <summary>
    /// 提取原本的 LateUpdate 計算邏輯
    /// </summary>
    private void UpdateMirrorLogic()
    {
        // 1. 固定位置
        transform.position = quadCenter.position - (quadCenter.forward * depthDistance);

        // 2. 計算旋轉邏輯
        Vector3 dirToPlayer = (playerCamera.position - quadCenter.position).normalized;
        Vector3 localDir = quadCenter.InverseTransformDirection(dirToPlayer);

        Vector3 targetLocalPos = new Vector3(-localDir.x, -localDir.y, -1.0f * sensitivity);
        Vector3 targetWorldPos = quadCenter.TransformPoint(targetLocalPos);

        // 3. 執行旋轉
        transform.LookAt(targetWorldPos, quadCenter.up);

        // 4. 動態 FOV 邏輯
        float distanceToMirror = Vector3.Distance(playerCamera.position, quadCenter.position);
        float t = Mathf.InverseLerp(maxEffectDistance, minEffectDistance, distanceToMirror);
        cam.fieldOfView = Mathf.Lerp(minFOV, maxFOV, t);

        // ApplyObliquePlane();
    }


    void ApplyObliquePlane()
    {
        Vector3 normal = quadCenter.forward;
        Vector3 point = quadCenter.position;
        Vector3 viewNormal = cam.worldToCameraMatrix.MultiplyVector(normal).normalized;
        Vector3 viewPoint = cam.worldToCameraMatrix.MultiplyPoint(point);
        float distance = -Vector3.Dot(viewNormal, viewPoint);
        Vector4 clipPlane = new Vector4(viewNormal.x, viewNormal.y, viewNormal.z, distance);
        cam.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);
    }
}