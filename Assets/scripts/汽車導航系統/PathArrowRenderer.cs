using System.Collections.Generic;
using UnityEngine;

public class PathArrowRenderer : MonoBehaviour
{
    [Header("組件引用")]
    public NavigationManager navigationManager;

    [Header("箭頭設定")]
    [Tooltip("箭頭的 Sprite 圖片")]
    public Sprite arrowSprite;
    [Tooltip("箭頭間隔距離（公尺）")]
    public float arrowSpacing = 5f;
    [Tooltip("離「路面」的高度（相對於路點，非世界座標）")]
    public float arrowHeight = 0.2f;
    [Tooltip("箭頭大小")]
    public float arrowScale = 2f;
    [Tooltip("箭頭顏色")]
    public Color arrowColor = new Color(0f, 0.8f, 1f, 1f);

    private List<GameObject> arrowPool = new List<GameObject>();
    private Transform arrowParent;

    private void Start()
    {
        // 建立一個父物件收納所有箭頭
        arrowParent = new GameObject("ArrowContainer").transform;
        arrowParent.SetParent(transform);
    }

    private void LateUpdate()
    {
        if (navigationManager == null) return;

        List<NavWaypointNode> path = navigationManager.currentPath;

        if (path == null || path.Count < 2)
        {
            HideAllArrows();
            return;
        }

        // 沿路徑計算箭頭位置
        List<Vector3> arrowPositions = new List<Vector3>();
        List<Quaternion> arrowRotations = new List<Quaternion>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            if (path[i] == null || path[i + 1] == null) continue;

            Vector3 start = path[i].transform.position;
            Vector3 end   = path[i + 1].transform.position;
            float segmentLength = Vector3.Distance(start, end);

            Vector3 direction = (end - start).normalized;
            Quaternion rot = Quaternion.LookRotation(direction) * Quaternion.Euler(90f, 0f, 0f);

            // 沿這段路每隔 arrowSpacing 放一個箭頭
            for (float dist = 0; dist < segmentLength; dist += arrowSpacing)
            {
                Vector3 pos = start + direction * dist;
                pos.y += arrowHeight;
                arrowPositions.Add(pos);
                arrowRotations.Add(rot);
            }
        }

        // 更新箭頭池
        UpdateArrowPool(arrowPositions, arrowRotations);
    }

    private void UpdateArrowPool(List<Vector3> positions, List<Quaternion> rotations)
    {
        // 補足箭頭數量
        while (arrowPool.Count < positions.Count)
        {
            GameObject arrow = CreateArrow();
            arrowPool.Add(arrow);
        }

        // 設定每個箭頭位置
        for (int i = 0; i < arrowPool.Count; i++)
        {
            if (i < positions.Count)
            {
                arrowPool[i].SetActive(true);
                arrowPool[i].transform.position = positions[i];
                arrowPool[i].transform.rotation = rotations[i];
            }
            else
            {
                arrowPool[i].SetActive(false);
            }
        }
    }

    private GameObject CreateArrow()
    {
        GameObject arrow = new GameObject("Arrow");
        arrow.transform.SetParent(arrowParent);

        SpriteRenderer sr = arrow.AddComponent<SpriteRenderer>();
        sr.sprite = arrowSprite;
        sr.color  = arrowColor;

        arrow.transform.localScale = Vector3.one * arrowScale;

        return arrow;
    }

    private void HideAllArrows()
    {
        foreach (var arrow in arrowPool)
            arrow.SetActive(false);
    }
}