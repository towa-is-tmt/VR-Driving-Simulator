using UnityEngine;

public class SpiralGoalTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        var detector = other.GetComponentInParent<SpiralRoadDetector>();
        if (detector == null) return;

        detector.OnReachGoal();
    }
}