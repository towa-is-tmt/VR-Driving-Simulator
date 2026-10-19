using System.Collections.Generic;
using UnityEngine;

public class AStarPathfinder : MonoBehaviour
{
    public WaypointNetwork network;

    /// <summary>
    /// 從起點到終點找最短路徑，回傳路點清單
    /// </summary>
    public List<NavWaypointNode> FindPath(NavWaypointNode start, NavWaypointNode goal)
    {
        if (start == null || goal == null) return null;

        // 開放清單（待探索）
        List<NavWaypointNode> openList   = new List<NavWaypointNode>();
        // 關閉清單（已探索）
        HashSet<NavWaypointNode> closedSet = new HashSet<NavWaypointNode>();

        // G值：從起點到該節點的實際距離
        Dictionary<NavWaypointNode, float> gCost = new Dictionary<NavWaypointNode, float>();
        // F值：G + H（H = 到終點的預估距離）
        Dictionary<NavWaypointNode, float> fCost = new Dictionary<NavWaypointNode, float>();
        // 記錄每個節點從哪裡來
        Dictionary<NavWaypointNode, NavWaypointNode> cameFrom = new Dictionary<NavWaypointNode, NavWaypointNode>();

        gCost[start] = 0f;
        fCost[start] = Heuristic(start, goal);
        openList.Add(start);

        while (openList.Count > 0)
        {
            // 找 F 值最小的節點
            NavWaypointNode current = GetLowestFCost(openList, fCost);

            // 抵達終點
            if (current == goal)
                return ReconstructPath(cameFrom, current);

            openList.Remove(current);
            closedSet.Add(current);

            foreach (var neighbor in current.connections)
            {
                if (neighbor == null || closedSet.Contains(neighbor)) continue;

                float tentativeG = gCost[current] + current.DistanceTo(neighbor);

                if (!gCost.ContainsKey(neighbor) || tentativeG < gCost[neighbor])
                {
                    gCost[neighbor]    = tentativeG;
                    fCost[neighbor]    = tentativeG + Heuristic(neighbor, goal);
                    cameFrom[neighbor] = current;

                    if (!openList.Contains(neighbor))
                        openList.Add(neighbor);
                }
            }
        }

        // 找不到路徑
        Debug.LogWarning("[A*] 找不到路徑！");
        return null;
    }

    private float Heuristic(NavWaypointNode a, NavWaypointNode b)
    {
        return Vector3.Distance(a.transform.position, b.transform.position);
    }

    private NavWaypointNode GetLowestFCost(List<NavWaypointNode> list, Dictionary<NavWaypointNode, float> fCost)
    {
        NavWaypointNode lowest = list[0];
        foreach (var node in list)
        {
            if (fCost.ContainsKey(node) && fCost[node] < fCost[lowest])
                lowest = node;
        }
        return lowest;
    }

    private List<NavWaypointNode> ReconstructPath(Dictionary<NavWaypointNode, NavWaypointNode> cameFrom, NavWaypointNode current)
    {
        List<NavWaypointNode> path = new List<NavWaypointNode>();
        while (cameFrom.ContainsKey(current))
        {
            path.Insert(0, current);
            current = cameFrom[current];
        }
        path.Insert(0, current);
        return path;
    }
}