using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class AutoSetSpaceId : EditorWindow
{
    [MenuItem("Tools/自動設定停車格 SpaceId")]
    public static void Setup()
    {
        GameObject[] selected = Selection.gameObjects;

        if (selected.Length == 0)
        {
            Debug.LogWarning("[AutoSetSpaceId] 請先在 Hierarchy 選取停車格物件！");
            return;
        }

        List<GameObject> sortedList = new List<GameObject>(selected);
        sortedList.Sort((a, b) =>
            GetHierarchyOrder(a).CompareTo(GetHierarchyOrder(b)));

        int count = 0;
        foreach (var obj in sortedList)
        {
            count++;
            string newId = $"Space_路邊停車{count:D2}";

            // 設定 LineCollider 的 ParkingLineTrigger SpaceId
            ParkingLineTrigger lineTrigger = obj.GetComponentInChildren<ParkingLineTrigger>();
            if (lineTrigger != null)
            {
                lineTrigger.spaceId = newId;
                EditorUtility.SetDirty(lineTrigger);
            }

            // 找名稱以 "LineTrigger" 開頭的子物件並重新命名
            foreach (Transform child in obj.transform)
            {
                if (child.name.StartsWith("LineTrigger"))
                {
                    child.gameObject.name = $"LineTrigger側停{count:D2}";
                    EditorUtility.SetDirty(child.gameObject);
                    break;
                }
            }

            // 設定 ZoneTrigger 的 ParkingZoneTrigger SpaceId
            ParkingZoneTrigger zoneTrigger = obj.GetComponentInChildren<ParkingZoneTrigger>();
            if (zoneTrigger != null)
            {
                zoneTrigger.spaceId = newId;
                EditorUtility.SetDirty(zoneTrigger);
            }

            Debug.Log($"[AutoSetSpaceId] {obj.name} → SpaceId: {newId}");
        }

        Debug.Log($"✅ 完成！共設定 {count} 個停車格。");
    }

    static int GetHierarchyOrder(GameObject obj)
    {
        int order = 0;
        GameObject[] allRoots = obj.scene.GetRootGameObjects();

        foreach (var root in allRoots)
        {
            int result = TraverseCount(root.transform, obj.transform, ref order);
            if (result >= 0)
                return result;
        }

        return int.MaxValue;
    }

    static int TraverseCount(Transform current, Transform target, ref int order)
    {
        if (current == target)
            return order;

        order++;

        for (int i = 0; i < current.childCount; i++)
        {
            int result = TraverseCount(current.GetChild(i), target, ref order);
            if (result >= 0)
                return result;
        }

        return -1;
    }
}