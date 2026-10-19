using UnityEngine;
using UnityEditor;

public static class RemoveMissingScripts
{
    // 用法 A：在 Project 選中 .prefab 檔 → 直接清 prefab 本體
    [MenuItem("Tools/移除選取 Prefab 的 Missing 腳本")]
    static void RemoveOnPrefabAsset()
    {
        int prefabCount = 0;
        foreach (var obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            int c = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                c += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            Debug.Log($"[清理] Prefab「{path}」移除了 {c} 個 Missing 腳本");
            prefabCount++;
        }
        if (prefabCount == 0)
            Debug.LogWarning("[清理] 沒有選到 .prefab！請在 Project 視窗點選 prefab 檔再執行。");
    }

    // 用法 B：清目前選取物件(含子物件)的 Missing —— 場景或 Prefab 編輯模式都行
    [MenuItem("Tools/移除選取物件(含子物件)的 Missing 腳本")]
    static void RemoveOnSelection()
    {
        int total = 0;
        foreach (var go in Selection.gameObjects)
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                total += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        Debug.Log($"[清理] 選取物件移除了 {total} 個 Missing 腳本");
    }
}