using UnityEngine;

// 掛在停車格三條線（LineTop / LineLeft / LineRight）的 Collider 物件上，
// 把 sensor 壓線事件轉報給車上的 ParkingDetector。
// 不需要 spaceId，也不影響原本的 ParkingLineTrigger 與 SpaceManager。
[RequireComponent(typeof(Collider))]
public class ParkingLineRelay : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Line] {other.gameObject.name} 壓到格線！（{gameObject.name}）");
        detector.OnSensorTouchLine(other.gameObject.name);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<ParkingDetector>();
        if (detector == null) return;

        Debug.Log($"[Line] {other.gameObject.name} 離開格線！（{gameObject.name}）");
        detector.OnSensorLeaveLine(other.gameObject.name);
    }
}