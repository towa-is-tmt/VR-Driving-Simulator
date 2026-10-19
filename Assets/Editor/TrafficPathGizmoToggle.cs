#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// 路網 Gizmo 顯示開關
///
/// 選單: Tools > 路網 Gizmo > 顯示節點距離數字
///
/// 只切換 TrafficPathNode 連線中間的「XX.Xm」文字,
/// 節點球 (DrawSphere)、連線 (DrawLine)、停等區方塊都不受影響,
/// 所以場景裡照樣點得到節點。
///
/// 設定記在 EditorPrefs,重開 Unity 或重新編譯都會記住。
/// </summary>
public static class TrafficPathGizmoToggle
{
    private const string MenuPath = "Tools/路網 Gizmo/顯示節點距離數字";
    private const string PrefKey  = "Agent0314.TrafficPathNode.ShowDistanceLabels";

    // 開啟 Unity / 每次重新編譯後,把記住的設定套回去
    [InitializeOnLoadMethod]
    private static void LoadSetting()
    {
        TrafficPathNode.ShowDistanceLabels = EditorPrefs.GetBool(PrefKey, false);
    }

    [MenuItem(MenuPath)]
    private static void Toggle()
    {
        bool next = !TrafficPathNode.ShowDistanceLabels;
        TrafficPathNode.ShowDistanceLabels = next;
        EditorPrefs.SetBool(PrefKey, next);
        SceneView.RepaintAll();

        Debug.Log($"<color=cyan>路網距離數字:{(next ? "顯示" : "隱藏")}</color>");
    }

    // 讓選單項目左邊顯示打勾狀態
    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, TrafficPathNode.ShowDistanceLabels);
        return true;
    }

    // ==================== ⚡ 灰車 WaypointNode 的文字標籤（效能） ====================
    // 灰車路網 1278 個節點，舊版每個節點每幀都畫距離文字 + 狀態文字，Scene 視窗會很卡。
    // 預設:距離數字關、狀態文字開(只畫 Scene 攝影機 80m 內的)。

    private const string GrayDistPath   = "Tools/路網 Gizmo/灰車節點:顯示距離數字";
    private const string GrayStatusPath = "Tools/路網 Gizmo/灰車節點:顯示狀態文字(分岔、死路、STOP)";
    private const string GrayDistKey    = "Agent0314.WaypointNode.ShowDistanceLabels";
    private const string GrayStatusKey  = "Agent0314.WaypointNode.ShowStatusLabels";

    [InitializeOnLoadMethod]
    private static void LoadGraySetting()
    {
        WaypointNode.ShowDistanceLabels = EditorPrefs.GetBool(GrayDistKey, false);
        WaypointNode.ShowStatusLabels = EditorPrefs.GetBool(GrayStatusKey, true);
    }

    [MenuItem(GrayDistPath)]
    private static void ToggleGrayDist()
    {
        WaypointNode.ShowDistanceLabels = !WaypointNode.ShowDistanceLabels;
        EditorPrefs.SetBool(GrayDistKey, WaypointNode.ShowDistanceLabels);
        SceneView.RepaintAll();
        Debug.Log($"<color=cyan>灰車節點距離數字:{(WaypointNode.ShowDistanceLabels ? "顯示" : "隱藏")}</color>");
    }

    [MenuItem(GrayDistPath, true)]
    private static bool ToggleGrayDistValidate()
    {
        Menu.SetChecked(GrayDistPath, WaypointNode.ShowDistanceLabels);
        return true;
    }

    [MenuItem(GrayStatusPath)]
    private static void ToggleGrayStatus()
    {
        WaypointNode.ShowStatusLabels = !WaypointNode.ShowStatusLabels;
        EditorPrefs.SetBool(GrayStatusKey, WaypointNode.ShowStatusLabels);
        SceneView.RepaintAll();
        Debug.Log($"<color=cyan>灰車節點狀態文字:{(WaypointNode.ShowStatusLabels ? "顯示" : "隱藏")}</color>");
    }

    [MenuItem(GrayStatusPath, true)]
    private static bool ToggleGrayStatusValidate()
    {
        Menu.SetChecked(GrayStatusPath, WaypointNode.ShowStatusLabels);
        return true;
    }
}
#endif
