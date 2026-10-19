using System.Collections.Generic;
using UnityEngine;

public class NavWaypointNode : MonoBehaviour
{
    [Header("連接的相鄰路點")]
    public List<NavWaypointNode> connections = new List<NavWaypointNode>();

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(transform.position, 0.5f);

        Gizmos.color = Color.cyan;
        foreach (var node in connections)
        {
            if (node == null) continue;
            Gizmos.DrawLine(transform.position, node.transform.position);
        }
    }

    public float DistanceTo(NavWaypointNode other)
    {
        return Vector3.Distance(transform.position, other.transform.position);
    }
}