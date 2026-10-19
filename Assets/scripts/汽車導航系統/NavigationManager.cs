using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class NavigationManager : MonoBehaviour
{
    [Header("組件引用")]
    public WaypointNetwork network;
    public AStarPathfinder pathfinder;
    public Transform vehicle;

    [Header("考試目的地（可多個，自動找最近）")]
    [Tooltip("倒車入庫入口（可放多個）")]
    public Transform[] destReverseParkList;
    [Tooltip("路邊側停入口（可放多個）")]
    public Transform[] destSideParkList;
    [Tooltip("S型螺旋坡道入口")]
    public Transform[] destSpiralList;
    [Tooltip("直線行駛起點")]
    public Transform[] destStraightList;
    [Tooltip("坡道定點停車入口")]
    public Transform[] destRampList;

    [Header("自動重算設定")]
    [Tooltip("偏離路徑多遠就重算（公尺）")]
    public float recalculateDistance = 20f;
    [Tooltip("多久檢查一次偏離（秒）")]
    public float recalculateInterval = 1f;
    [Tooltip("距目的地多近就停止導航（公尺）")]
    public float arriveStopDistance = 8f;
    [Tooltip("導航開始後多久才開始偏離偵測（秒）")]
    public float navStartGracePeriod = 3f;

    [Header("目前導航狀態（Debug用）")]
    public string currentDestName = "";
    public int currentPathCount = 0;
    public List<NavWaypointNode> currentPath = new List<NavWaypointNode>();
    [Tooltip("目前導航的目的地物件")]
    public Transform currentDestinationTransform;

    [Header("事件")]
    public UnityEvent onPathFound;
    public UnityEvent onPathNotFound;
    public UnityEvent onNavigationCleared;
    public UnityEvent onStraightHint;
    public UnityEvent onRecalculate;

    private Transform currentDestination;
    private string    currentDestNameSaved;
    private float     recalcTimer  = 0f;
    private float     navStartTime = 0f;

    private void Update()
    {
        if (currentPath == null || currentPath.Count == 0) return;
        if (currentDestination == null) return;

        // ── 抵達目的地就停止導航（相容 Box 與單點）──────────
        NavWaypointNode destNode = currentPath[currentPath.Count - 1];

        float distToDest;
        DestinationZone zone = currentDestinationTransform != null
            ? currentDestinationTransform.GetComponent<DestinationZone>()
            : null;

        if (zone != null)
            distToDest = zone.DistanceToBox(vehicle.position);
        else
            distToDest = Vector3.Distance(vehicle.position, destNode.transform.position);

        if (distToDest < arriveStopDistance)
        {
            Debug.Log("[導航] 已抵達目的地，停止導航");
            currentPath.Clear();
            currentPathCount   = 0;
            currentDestination = null;
            return;
        }

        // ── 導航剛開始的緩衝時間內不檢查偏離 ─────────────────
        if (Time.time - navStartTime < navStartGracePeriod) return;

        // ── 偏離偵測（用線段距離）──────────────────────────
        recalcTimer += Time.deltaTime;
        if (recalcTimer < recalculateInterval) return;
        recalcTimer = 0f;

        float minDistToPath = GetDistanceToPath();

        if (minDistToPath > recalculateDistance)
        {
            Debug.Log($"[導航] 偏離路線 {minDistToPath:F1}m，重新計算路徑！");
            onRecalculate?.Invoke();
            NavigateTo(currentDestination, currentDestNameSaved);
        }
    }

    // 計算車子到「路徑線段」的最近距離
    private float GetDistanceToPath()
    {
        float minDist = float.MaxValue;

        for (int i = 0; i < currentPath.Count - 1; i++)
        {
            if (currentPath[i] == null || currentPath[i + 1] == null) continue;

            float d = DistancePointToSegment(
                vehicle.position,
                currentPath[i].transform.position,
                currentPath[i + 1].transform.position
            );
            if (d < minDist) minDist = d;
        }

        return minDist;
    }

    private float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float sqr = ab.sqrMagnitude;
        if (sqr < 0.0001f) return Vector3.Distance(point, a);

        float t = Vector3.Dot(point - a, ab) / sqr;
        t = Mathf.Clamp01(t);
        Vector3 closest = a + t * ab;
        return Vector3.Distance(point, closest);
    }

    // ── 公開給 UI 按鈕呼叫 ───────────────────────────────────

    public void NavigateToReversePark() { NavigateToNearest(destReverseParkList, "倒車入庫"); }
    public void NavigateToSidePark()    { NavigateToNearest(destSideParkList,    "路邊側停"); }
    public void NavigateToSpiral()      { NavigateToNearest(destSpiralList,      "S型螺旋坡道"); }
    public void NavigateToRamp()        { NavigateToNearest(destRampList,        "坡道定點停車"); }

    public void NavigateToStraight()
    {
        Debug.Log("[導航] 直線行駛：請在任意直線道路上加速至 40 km/h 維持 5 秒！");
        onStraightHint?.Invoke();
    }

    public void ClearNavigation()
    {
        currentPath.Clear();
        currentDestName             = "";
        currentPathCount            = 0;
        currentDestination          = null;
        currentDestinationTransform = null;
        Debug.Log("[導航] 已清除路徑");
        onNavigationCleared?.Invoke();
    }

    // ── 找最近目的地 ──────────────────────────────────────────

    private void NavigateToNearest(Transform[] destinations, string destName)
    {
        if (destinations == null || destinations.Length == 0)
        {
            Debug.LogWarning($"[導航] {destName} 沒有設定任何目的地！");
            onPathNotFound?.Invoke();
            return;
        }

        Transform nearest = null;
        float minDist = float.MaxValue;

        foreach (var dest in destinations)
        {
            if (dest == null) continue;
            float dist = Vector3.Distance(vehicle.position, dest.position);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = dest;
            }
        }

        if (nearest == null)
        {
            Debug.LogWarning($"[導航] {destName} 所有目的地都是空的！");
            onPathNotFound?.Invoke();
            return;
        }

        Debug.Log($"[導航] 選擇最近的 {destName} 目的地：{nearest.name}，距離：{minDist:F1}m");
        NavigateTo(nearest, destName);
    }

    // ── A* 尋路核心 ───────────────────────────────────────────

    private void NavigateTo(Transform destination, string destName)
    {
        currentDestination          = destination;
        currentDestNameSaved        = destName;
        currentDestinationTransform = destination;
        navStartTime                = Time.time;

        // 起點用「順向優先」，目的地用「最近」
        NavWaypointNode startNode = network.FindStartNode(vehicle.position, vehicle.forward);
        NavWaypointNode goalNode  = network.FindNearestNode(destination.position);

        if (startNode == null || goalNode == null)
        {
            Debug.LogWarning("[導航] 找不到起點或終點路點！");
            onPathNotFound?.Invoke();
            return;
        }

        if (startNode == goalNode)
        {
            Debug.Log("[導航] 已在目的地附近！");
            currentPath      = new List<NavWaypointNode> { startNode };
            currentDestName  = destName;
            currentPathCount = 1;
            onPathFound?.Invoke();
            return;
        }

        List<NavWaypointNode> path = pathfinder.FindPath(startNode, goalNode);

        if (path == null || path.Count == 0)
        {
            Debug.LogWarning($"[導航] 找不到前往 {destName} 的路徑！");
            onPathNotFound?.Invoke();
            return;
        }

        currentPath      = path;
        currentDestName  = destName;
        currentPathCount = path.Count;

        Debug.Log($"[導航] ✅ 找到路徑！前往 {destName}，共 {path.Count} 個路點");
        onPathFound?.Invoke();
    }
}