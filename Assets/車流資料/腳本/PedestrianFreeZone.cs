using UnityEngine;

// 掛在一個有 BoxCollider(Is Trigger 打勾) 的空物件上
// 用來定義「行人可以自由亂走」的範圍
[RequireComponent(typeof(BoxCollider))]
public class PedestrianFreeZone : MonoBehaviour
{
    [Header("=== 自由活動區設定 ===")]
    [Tooltip("行人在區域內閒晃時，離地面的高度（避免亂走的目標點卡進地板或飄空）")]
    public float groundY = 0f;
    [Tooltip("是否自動用自身 Transform 的 Y 座標當作地面高度")]
    public bool useTransformY = true;
    [Tooltip("邊界內縮距離，防止目標點產生在 Collider 邊緣導致卡牆")]
    private float edgePadding = 0.5f;

    private BoxCollider box;

    private void Awake()
    {
        box = GetComponent<BoxCollider>();
        box.isTrigger = true; // 保險起見強制打開，避免忘記勾選
    }

    // 取得世界座標下的 Bounds，給 PedestrianWalker 計算隨機亂走目標點用
    public Bounds GetBounds()
    {
        if (box == null) box = GetComponent<BoxCollider>();
        return box.bounds;
    }

    // 直接回傳一個範圍內的隨機世界座標點
    public Vector3 GetRandomPoint()
    {
        Bounds b = GetBounds();
        float y = useTransformY ? transform.position.y : groundY;

        return new Vector3(
            Random.Range(b.min.x + edgePadding, b.max.x - edgePadding),
            y,
            Random.Range(b.min.z + edgePadding, b.max.z - edgePadding)
        );
    }

    private void OnDrawGizmos()
    {
        BoxCollider bc = GetComponent<BoxCollider>();
        if (bc == null) return;

        Gizmos.color = new Color(0f, 1f, 0.5f, 0.15f);
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(bc.center, bc.size);
        Gizmos.color = new Color(0f, 1f, 0.5f, 0.8f);
        Gizmos.DrawWireCube(bc.center, bc.size);
        Gizmos.matrix = oldMatrix;
    }
}