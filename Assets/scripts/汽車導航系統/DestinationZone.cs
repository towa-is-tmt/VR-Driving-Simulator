using UnityEngine;

public class DestinationZone : MonoBehaviour
{
    public enum ArriveType
    {
        LeftRight,   // 路邊停車，播「在您的左/右手邊」
        Direct       // 直接開進，播「您已抵達目的地」
    }

    [Header("抵達語音類型")]
    public ArriveType arriveType = ArriveType.Direct;

    private BoxCollider box;

    private void Awake()
    {
        box = GetComponent<BoxCollider>();
    }

    /// <summary>
    /// 車子到這個 Box 的最近距離（在 Box 內回傳 0）
    /// </summary>
    public float DistanceToBox(Vector3 point)
    {
        if (box == null) box = GetComponent<BoxCollider>();
        if (box == null) return Vector3.Distance(transform.position, point);

        Vector3 closest = box.ClosestPoint(point);
        return Vector3.Distance(point, closest);
    }

    /// <summary>
    /// 車子是否在 Box 範圍內
    /// </summary>
    public bool IsInside(Vector3 point)
    {
        if (box == null) box = GetComponent<BoxCollider>();
        if (box == null) return false;

        Vector3 closest = box.ClosestPoint(point);
        return Vector3.Distance(point, closest) < 0.1f;
    }
}