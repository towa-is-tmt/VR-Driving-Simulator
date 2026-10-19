using UnityEngine;

public class SpiralStartTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var detector = other.GetComponentInParent<SpiralRoadDetector>();
        if (detector == null) return;

        Debug.Log("[Spiral] 考試開始！");
        detector.OnStartExam();
    }
}