using UnityEngine;

public class ParkingLineTrigger : MonoBehaviour
{
    [Header("停車格識別")]
    public string spaceId = "Space_01";

    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Line] {other.gameObject.name} 壓到格線！SpaceId: {spaceId}");
        detector.OnSensorTouchLine(other.gameObject.name);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Line] {other.gameObject.name} 離開格線！SpaceId: {spaceId}");
        detector.OnSensorLeaveLine(other.gameObject.name);
    }
}