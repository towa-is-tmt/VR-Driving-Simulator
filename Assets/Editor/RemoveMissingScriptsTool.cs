#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// 批次清除 prefab / 場景物件上的「Missing Script」
/// 用法:在 Project 或 Hierarchy 選取物件 → 選單 Tools → 清除 Missing Scripts
/// </summary>
public static class RemoveMissingScriptsTool
{
    [MenuItem("Tools/清除 Missing Scripts (選取的 Prefab)")]
    private static void CleanSelectedPrefabs()
    {
        int totalRemoved = 0, prefabCount = 0;

        foreach (Object obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) continue;

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            int removed = 0;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            if (removed > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"<color=lime>✅ {System.IO.Path.GetFileName(path)} 清除了 {removed} 個 Missing Script</color>");
                totalRemoved += removed;
            }
            PrefabUtility.UnloadPrefabContents(root);
            prefabCount++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"<color=cyan>🧹 完成:掃描 {prefabCount} 個 prefab,共清除 {totalRemoved} 個 Missing Script</color>");
    }

    [MenuItem("Tools/清除 Missing Scripts (場景中選取的物件)")]
    private static void CleanSelectedSceneObjects()
    {
        int removed = 0;
        foreach (GameObject go in Selection.gameObjects)
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

        Debug.Log($"<color=cyan>🧹 場景物件共清除 {removed} 個 Missing Script</color>");
    }
}
#endif
