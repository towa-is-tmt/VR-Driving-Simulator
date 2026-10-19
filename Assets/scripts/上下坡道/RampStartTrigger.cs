using UnityEngine;

public class RampStartTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var detector = other.GetComponentInParent<RampDetector>();
        if (detector == null) return;

        Debug.Log("[坡道] 進入坡道！");
        detector.OnStartExam();
    }
}