using UnityEngine;

public class ParkingZoneTrigger : MonoBehaviour
{
    [Header("停車格識別")]
    public string spaceId = "Space_01";

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Zone] {other.gameObject.name} 進入停車格 {spaceId}");
        detector.OnSensorEnterZone(spaceId, other.gameObject.name);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Zone] {other.gameObject.name} 離開停車格 {spaceId}");
        detector.OnSensorExitZone(spaceId, other.gameObject.name);
    }
}