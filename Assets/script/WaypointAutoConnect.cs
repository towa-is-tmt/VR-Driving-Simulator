using System.Collections.Generic;
using UnityEngine;

public class WaypointAutoConnect : MonoBehaviour
{
    [Header("自動連線工具：只認 TrafficPathNode")]
    public List<TrafficPathNode> allNodes = new List<TrafficPathNode>();

    [ContextMenu("1. 自動抓取子物件下的所有路點")]
    public void GetAllChildNodes()
    {
        allNodes.Clear();
        foreach (Transform child in transform)
        {
            TrafficPathNode node = child.GetComponent<TrafficPathNode>();
            if (node != null) allNodes.Add(node);
        }
        UnityEngine.Debug.Log($"<color=green>成功抓取到 {allNodes.Count} 個新版路點！</color>");
    }

    [ContextMenu("2. 開始一鍵串聯線路 (閉環單向道)")]
    public void ConnectNodesInOrder()
    {
        if (allNodes == null || allNodes.Count < 2) return;

        for (int i = 0; i < allNodes.Count; i++)
        {
            allNodes[i].nextChoices.Clear();
            if (i < allNodes.Count - 1) allNodes[i].nextChoices.Add(allNodes[i + 1]);
            else allNodes[i].nextChoices.Add(allNodes[0]);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(allNodes[i]);
#endif
        }
        UnityEngine.Debug.Log("<color=cyan>✨ 60個新版路點已完美串聯！</color>");
    }
}