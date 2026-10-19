using UnityEngine;

public class AmbulanceSpawnPoint : MonoBehaviour
{
    [Tooltip("這輛救護車出生後，第一個要開往的 TrafficPathNode")]
    public TrafficPathNode startWaypoint;

    // 在編輯器裡畫個小圖示，方便你在 Scene 視窗一眼看出出生點在哪
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawSphere(transform.position, 1.0f);
    }
}