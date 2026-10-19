using UnityEngine;

public class SpiralWallTrigger : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        var detector = collision.gameObject.GetComponentInParent<SpiralRoadDetector>();
        if (detector == null)
            detector = collision.gameObject.GetComponent<SpiralRoadDetector>();
        if (detector == null) return;

        Debug.Log($"[Spiral] 撞到牆壁：{collision.gameObject.name}");
        detector.OnHitWall();
    }
}