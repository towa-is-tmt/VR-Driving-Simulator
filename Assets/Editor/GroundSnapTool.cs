using UnityEditor;
using UnityEngine;

/// <summary>
/// 🛬 編輯器工具：把選取的物件批次貼齊地面
///
/// 場景裡預先擺放的車輛（例如 kk (1) ~ kk (292)）不會經過生成器，
/// 所以 SpawnGroundSnap 在執行時碰不到它們。這個工具讓你在編輯器裡一次修完。
///
/// 用法：
///   1. Hierarchy 搜尋框輸入車輛名稱（例如 kk）
///   2. 在 Hierarchy 裡 Ctrl+A 全選
///   3. 選單 Tools → 車輛工具 → 把選取的物件貼齊地面
///
/// 支援 Ctrl+Z 復原。只改 Transform 的 Y，不動任何其他設定。
/// </summary>
public static class GroundSnapTool
{
    private const string MenuPath = "Tools/車輛工具/把選取的物件貼齊地面";

    [MenuItem(MenuPath)]
    private static void SnapSelectionToGround()
    {
        GameObject[] targets = Selection.gameObjects;

        if (targets == null || targets.Length == 0)
        {
            EditorUtility.DisplayDialog("貼齊地面",
                "沒有選取任何物件。\n\n請先在 Hierarchy 選取要處理的車輛，可以用搜尋框篩選後 Ctrl+A 全選。",
                "知道了");
            return;
        }

        if (!EditorUtility.DisplayDialog("貼齊地面",
                $"即將把選取的 {targets.Length} 個物件貼齊到正下方的地面。\n\n" +
                "找不到地面的物件會維持原樣不動。\n" +
                "執行後可以用 Ctrl+Z 復原。",
                "執行", "取消"))
        {
            return;
        }

        int moved = 0, skipped = 0;

        try
        {
            for (int i = 0; i < targets.Length; i++)
            {
                GameObject go = targets[i];
                if (go == null) { skipped++; continue; }

                EditorUtility.DisplayProgressBar("貼齊地面",
                    $"處理中 {i + 1} / {targets.Length}：{go.name}",
                    (float)i / targets.Length);

                Undo.RecordObject(go.transform, "貼齊地面");

                float beforeY = go.transform.position.y;
                SpawnGroundSnap.Apply(go);

                if (!Mathf.Approximately(beforeY, go.transform.position.y))
                {
                    moved++;
                    EditorUtility.SetDirty(go.transform);
                }
                else
                {
                    skipped++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"<color=lime>🛬 [貼齊地面] 完成 —— 已調整 {moved} 個，維持原樣 {skipped} 個" +
                  $"（找不到地面或本來就已經貼齊）。可用 Ctrl+Z 復原。</color>");
    }
}
