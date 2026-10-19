using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class TrafficDiverter : MonoBehaviour
{
    [Header("🔗 繞道路線的起點清單 (可放入多個不同的路徑點)")]
    public List<TrafficPathNode> detourNodes = new List<TrafficPathNode>();

    private void OnTriggerEnter(Collider other)
    {
        if (!AccidentDirector.IsAccidentActive || detourNodes.Count == 0) return;

        // 🚗 白車:覆寫 currentWaypointNode 到改道點
        ReverseThreatAICar aiCar = other.GetComponentInParent<ReverseThreatAICar>();
        if (aiCar != null)
        {
            TrafficPathNode detour = detourNodes[UnityEngine.Random.Range(0, detourNodes.Count)];
            if (detour != null) aiCar.currentWaypointNode = detour;
            return;
        }

        // 🚗 灰車:走自己的 WaypointNode 路網,呼叫它的改道方法
        CarNavigation navCar = other.GetComponentInParent<CarNavigation>();
        if (navCar != null) navCar.ForceDetour();
    }
}