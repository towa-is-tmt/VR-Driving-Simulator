using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class AutoSetReverseSpaceId : EditorWindow
{
    [MenuItem("Tools/自動設定倒車入庫 SpaceId")]
    public static void Setup()
    {
        GameObject[] selected = Selection.gameObjects;

        if (selected.Length == 0)
        {
            Debug.LogWarning("[倒車入庫SpaceId] 請先在 Hierarchy 選取倒車入庫停車格物件！");
            return;
        }

        List<GameObject> sortedList = new List<GameObject>(selected);
        sortedList.Sort((a, b) => GetHierarchyOrder(a).CompareTo(GetHierarchyOrder(b)));

        int count = 0;
        foreach (var obj in sortedList)
        {
            count++;
            string newId = $"Space_倒車入庫{count:D2}";

            // 設定該格底下所有 ParkingLineTrigger 的 SpaceId
            foreach (var lineTrigger in obj.GetComponentsInChildren<ParkingLineTrigger>(true))
            {
                lineTrigger.spaceId = newId;
                EditorUtility.SetDirty(lineTrigger);
            }

            // 設定所有 ParkingZoneTrigger 的 SpaceId（這個才是判定側停/倒車的依據）
            foreach (var zoneTrigger in obj.GetComponentsInChildren<ParkingZoneTrigger>(true))
            {
                zoneTrigger.spaceId = newId;
                EditorUtility.SetDirty(zoneTrigger);
            }

            Debug.Log($"[倒車入庫SpaceId] {obj.name} → SpaceId: {newId}");
        }

        Debug.Log($"✅ 完成！共設定 {count} 個倒車入庫停車格。");
    }

    static int GetHierarchyOrder(GameObject obj)
    {
        int order = 0;
        GameObject[] allRoots = obj.scene.GetRootGameObjects();
        foreach (var root in allRoots)
        {
            int result = TraverseCount(root.transform, obj.transform, ref order);
            if (result >= 0) return result;
        }
        return int.MaxValue;
    }

    static int TraverseCount(Transform current, Transform target, ref int order)
    {
        if (current == target) return order;
        order++;
        for (int i = 0; i < current.childCount; i++)
        {
            int result = TraverseCount(current.GetChild(i), target, ref order);
            if (result >= 0) return result;
        }
        return -1;
    }
}