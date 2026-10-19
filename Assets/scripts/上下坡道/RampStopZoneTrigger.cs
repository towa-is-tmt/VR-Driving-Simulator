using UnityEngine;

public class RampStopZoneTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var detector = other.GetComponentInParent<RampDetector>();
        if (detector == null) return;

        Debug.Log("[坡道] 車子進入停車區！");
        detector.OnEnterStopZone();
    }

    private void OnTriggerExit(Collider other)
    {
        var detector = other.GetComponentInParent<RampDetector>();
        if (detector == null) return;

        Debug.Log("[坡道] 車子離開停車區！");
        detector.OnExitStopZone();
    }
}