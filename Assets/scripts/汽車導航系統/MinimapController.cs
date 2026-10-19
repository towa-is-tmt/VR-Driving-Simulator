using UnityEngine;
using UnityEngine.UI;

public class MinimapController : MonoBehaviour
{
    [Header("攝影機設定")]
    public Camera minimapCamera;
    public Transform vehicle;

    [Header("小地圖 UI")]
    public RectTransform playerIcon;
    public RectTransform destIcon;

    [Header("導航")]
    public NavigationManager navigationManager;

    [Header("小地圖設定")]
    [Tooltip("小地圖攝影機覆蓋範圍")]
    public float minimapWorldSize = 100f;
    [Tooltip("小地圖 UI 大小（像素）")]
    public float minimapUISize = 200f;

    [Header("車頭朝上（地圖旋轉，像真實導航）")]
    public bool 車頭朝上 = true;

    private void LateUpdate()
    {
        if (vehicle == null) return;

        // ── 攝影機跟著車子（位置）──────────────────────────
        minimapCamera.orthographicSize = minimapWorldSize * 0.5f;

        Vector3 camPos = minimapCamera.transform.position;
        camPos.x = vehicle.position.x;
        camPos.z = vehicle.position.z;
        minimapCamera.transform.position = camPos;

        // ── 相機朝向 ──────────────────────────────────────
        if (車頭朝上)
        {
            // 相機繞垂直軸跟著車轉 → 地圖旋轉、車頭永遠朝上
            minimapCamera.transform.rotation = Quaternion.Euler(90f, vehicle.eulerAngles.y, 0f);
            playerIcon.localRotation = Quaternion.identity;   // 車圖示固定朝上
        }
        else
        {
            // 北方朝上（原本的）：地圖不轉、車圖示轉
            minimapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            playerIcon.localRotation = Quaternion.Euler(0, 0, -vehicle.eulerAngles.y);
        }

        playerIcon.anchoredPosition = Vector2.zero;

        UpdateDestIcon();
    }

    private void UpdateDestIcon()
    {
        if (destIcon == null) return;

        if (navigationManager != null &&
            navigationManager.currentPath != null &&
            navigationManager.currentPath.Count > 0)
        {
            NavWaypointNode dest = navigationManager.currentPath[navigationManager.currentPath.Count - 1];
            destIcon.gameObject.SetActive(true);

            Vector2 pos = WorldToMinimap(dest.transform.position);

            float limit = minimapUISize * 0.5f - 10f;
            pos.x = Mathf.Clamp(pos.x, -limit, limit);
            pos.y = Mathf.Clamp(pos.y, -limit, limit);

            destIcon.anchoredPosition = pos;
        }
        else
        {
            destIcon.gameObject.SetActive(false);
        }
    }

    private Vector2 WorldToMinimap(Vector3 worldPos)
    {
        Vector3 offset = worldPos - vehicle.position;

        // 車頭朝上時，要把世界位移轉成「車體座標」，圖示才會跟著轉的地圖對齊
        if (車頭朝上)
            offset = Quaternion.Euler(0, -vehicle.eulerAngles.y, 0) * offset;

        float x = (offset.x / minimapWorldSize) * minimapUISize;
        float y = (offset.z / minimapWorldSize) * minimapUISize;
        return new Vector2(x, y);
    }
}