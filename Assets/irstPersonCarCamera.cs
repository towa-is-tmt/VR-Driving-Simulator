using UnityEngine;

// ── 第一人稱相機：完全鎖在駕駛座視角，跟著車一起動 ──────────
// 掛在相機物件上，seat 拖車內「駕駛座視角」空物件（眼睛位置、Z 朝車頭）
public class FirstPersonCarCamera : MonoBehaviour
{
    [Header("駕駛座視角（車內的空物件，眼睛位置）")]
    public Transform seat;

    [Header("微調偏移（相對駕駛座，通常留 0）")]
    public Vector3 positionOffset = Vector3.zero;
    public Vector3 rotationOffset = Vector3.zero;

    void Start()
    {
        if (seat == null)
            Debug.LogError("[第一人稱相機] 未指定 seat（駕駛座視角）！");
    }

    void LateUpdate()   // 車移動後再對齊相機，避免抖動
    {
        if (seat == null) return;

        transform.position = seat.TransformPoint(positionOffset);
        transform.rotation = seat.rotation * Quaternion.Euler(rotationOffset);
    }
}