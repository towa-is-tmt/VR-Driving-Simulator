using UnityEngine;

public class RampBottomLineTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<RampDetector>();
        if (detector == null) return;

        Debug.Log("[坡道] 碰到下方白線！");
        detector.OnHitBottomLine();
    }
}