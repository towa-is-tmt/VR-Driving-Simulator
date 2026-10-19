using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class Route
{
    public string routeName = "New Route";
    public List<Transform> waypoints = new List<Transform>(); // 使用 List 方便在 Inspector 增減
}

public class test : MonoBehaviour
{
    [Header("路徑資料庫")]
    public List<Route> possibleRoutes = new List<Route>();

    [Header("貝茲曲線設定")]
    public Color pathColor = Color.yellow;
    public float bezierTriggerDistance = 8f; // 離點多遠開始切圓弧
    [Range(5, 50)] public int curveSegments = 20; // 曲線平滑度

    // 繪製 Scene 視窗預覽線
    void OnDrawGizmos()
    {
        if (possibleRoutes == null) return;

        foreach (var route in possibleRoutes)
        {
            DrawBezierPath(route);
        }
    }

    private void DrawBezierPath(Route route)
    {
        // 至少要有兩個點才能連線
        if (route.waypoints == null || route.waypoints.Count < 2) return;

        Gizmos.color = pathColor;
        Vector3 hoverOffset = Vector3.up * 0.2f; // 稍微離地避免閃爍

        // 起始繪製點
        if (route.waypoints[0] == null) return;
        Vector3 currentDrawPos = route.waypoints[0].position + hoverOffset;

        for (int i = 1; i < route.waypoints.Count; i++)
        {
            Transform targetNode = route.waypoints[i];
            if (targetNode == null) continue;

            Vector3 targetNodePos = targetNode.position + hoverOffset;

            // 判斷是否需要繪製貝茲轉彎 (需要有「下一個點」來決定出口方向)
            if (i < route.waypoints.Count - 1 && route.waypoints[i + 1] != null)
            {
                Transform nextNode = route.waypoints[i + 1];
                Vector3 nextNodePos = nextNode.position + hoverOffset;

                // 1. 計算進入點 P0
                Vector3 dirToTarget = (targetNodePos - currentDrawPos).normalized;
                float distToTarget = Vector3.Distance(currentDrawPos, targetNodePos);
                // 確保觸發距離不會超過路徑長度的一半
                float actualTriggerDist = Mathf.Min(bezierTriggerDistance, distToTarget * 0.5f);
                Vector3 p0 = targetNodePos - dirToTarget * actualTriggerDist;

                // --- 畫直線段 ---
                Gizmos.DrawLine(currentDrawPos, p0);

                // 2. 準備貝茲控制點
                Vector3 p1 = targetNodePos; // 轉角點
                Vector3 p2 = (targetNodePos + nextNodePos) / 2f; // 曲線出口點

                // --- 畫貝茲路段 ---
                Vector3 segmentStart = p0;
                for (int step = 1; step <= curveSegments; step++)
                {
                    float t = (float)step / curveSegments;
                    Vector3 segmentEnd = GetBezierPoint(p0, p1, p2, t);
                    Gizmos.DrawLine(segmentStart, segmentEnd);
                    segmentStart = segmentEnd;
                }

                // 更新下一段的畫筆起點為貝茲的終點
                currentDrawPos = segmentStart;
            }
            else
            {
                // 最後一段或無法形成轉彎時，直接畫直線
                Gizmos.DrawLine(currentDrawPos, targetNodePos);
            }

            // 在每個節點畫一個小球方便辨識
            Gizmos.DrawSphere(targetNodePos, 0.3f);
        }
    }

    // 二次貝茲公式
    private Vector3 GetBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        t = Mathf.Clamp01(t);
        float oneMinusT = 1f - t;
        return oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2;
    }
}