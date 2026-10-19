using UnityEngine;

public class RampTopLineTrigger : MonoBehaviour
{
    [Header("前輪 Sensor（壓線失敗）")]
    public Collider sensorFL;
    public Collider sensorFR;

    [Header("後輪 Sensor（越過成功）")]
    public Collider sensorRL;
    public Collider sensorRR;

    private bool hasTriggered = false; // 防止重複觸發

    private void OnTriggerEnter(Collider other)
    {
        if (hasTriggered) return; // 已觸發過就忽略
        if (other.gameObject.layer != LayerMask.NameToLayer("LineSensor")) return;

        var detector = other.GetComponentInParent<RampDetector>();
        if (detector == null) return;

        if (other == sensorRL || other == sensorRR)
        {
            hasTriggered = true; // 標記已觸發
            Debug.Log($"[坡道] 後輪 {other.gameObject.name} 越過上方白線！");
            detector.OnRearWheelPassTopLine();
        }
        else if (other == sensorFL || other == sensorFR)
        {
            Debug.Log($"[坡道] 前輪 {other.gameObject.name} 壓到上方白線！");
            detector.OnHitTopLine();
        }
    }
}