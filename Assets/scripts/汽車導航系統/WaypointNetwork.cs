using System.Collections.Generic;
using UnityEngine;

public class WaypointNetwork : MonoBehaviour
{
    [Header("所有路點（自動收集子物件）")]
    public List<NavWaypointNode> allNodes = new List<NavWaypointNode>();

    [ContextMenu("自動收集所有子物件路點")]
    public void CollectNodes()
    {
        allNodes.Clear();
        allNodes.AddRange(GetComponentsInChildren<NavWaypointNode>());
        Debug.Log($"[路網] 收集到 {allNodes.Count} 個路點");
    }

    /// <summary>
    /// 找離指定位置最近的路點（目的地用）
    /// </summary>
    public NavWaypointNode FindNearestNode(Vector3 worldPosition)
    {
        NavWaypointNode nearest = null;
        float minDist = float.MaxValue;

        foreach (var node in allNodes)
        {
            if (node == null) continue;
            float dist = Vector3.Distance(node.transform.position, worldPosition);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = node;
            }
        }

        return nearest;
    }

    /// <summary>
    /// 找最適合當導航起點的路點：完全優先車頭前方的路點
    /// </summary>
    public NavWaypointNode FindStartNode(Vector3 vehiclePos, Vector3 vehicleForward)
    {
        NavWaypointNode best = null;
        float bestScore = float.MinValue;

        foreach (var node in allNodes)
        {
            if (node == null) continue;

            Vector3 toNode = node.transform.position - vehiclePos;
            float dist = toNode.magnitude;
            if (dist < 0.1f) return node; // 幾乎重疊，直接用

            Vector3 dir = toNode.normalized;

            // 車頭方向與路點方向的吻合度（1=正前方, -1=正後方）
            float forwardDot = Vector3.Dot(vehicleForward, dir);

            // 平衡順向：forwardDot 權重放很大
            float score = forwardDot * 500f - dist;

            if (score > bestScore)
            {
                bestScore = score;
                best = node;
            }
        }

        return best;
    }
}