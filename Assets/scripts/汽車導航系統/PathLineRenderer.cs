using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class PathLineRenderer : MonoBehaviour
{
    [Header("組件引用")]
    public NavigationManager navigationManager;

    [Header("線條設定")]
    [Tooltip("離「路面」的高度（相對於路點，非世界座標）")]
    public float lineHeight = 3f;
    [Tooltip("線條寬度")]
    public float lineWidth = 2f;
    [Tooltip("線條顏色")]
    public Color lineColor = new Color(0f, 0.8f, 1f, 1f); // 青色

    private LineRenderer lineRenderer;

    private void Start()
    {
        lineRenderer = GetComponent<LineRenderer>();

        // 基本設定
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth   = lineWidth;
        lineRenderer.material   = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = lineColor;
        lineRenderer.endColor   = lineColor;
        lineRenderer.positionCount = 0;

        lineRenderer.alignment = LineAlignment.TransformZ;
    }

    private void LateUpdate()
    {
        if (navigationManager == null) return;

        List<NavWaypointNode> path = navigationManager.currentPath;

        if (path == null || path.Count < 2)
        {
            // 沒有路徑，清空線條
            lineRenderer.positionCount = 0;
            return;
        }

        // 沿路點畫線
        lineRenderer.positionCount = path.Count;
        for (int i = 0; i < path.Count; i++)
        {
            if (path[i] == null) continue;
            Vector3 pos = path[i].transform.position;
            pos.y += lineHeight; // 相對於路點抬高，坡道與立體停車場才不會埋進地板
            lineRenderer.SetPosition(i, pos);
        }
    }
}