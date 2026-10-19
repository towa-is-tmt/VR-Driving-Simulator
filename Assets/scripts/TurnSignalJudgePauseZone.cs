using UnityEngine;

/// <summary>
/// 方向燈判定暫停區 — 車子開進這個區域時暫停方向燈判定，離開時恢復。
///
/// 用途：S 型螺旋坡道這類場地內項目，轉彎頻繁但不該判方向燈，
///       用一個 Trigger 把整個區域框起來，進去就不偵測。
///
/// 用法：
///   1. 建一個空物件，加 BoxCollider（勾 Is Trigger），調大小把 S 彎範圍框住
///   2. 掛這支腳本
///   3. 車子開進去 → 暫停；開出來 → 恢復
///
/// 判定用車輛感測器所在的 Layer（和其他考試偵測一致），
/// 靠 GetComponentInParent 找到車上的 TurnSignalJudge。
/// </summary>
public class TurnSignalJudgePauseZone : MonoBehaviour
{
    [Header("觸發設定")]
    [Tooltip("只有這些 Layer 的 Collider 進入才算（通常是車輛感測器 LineSensor，或車身 VehicleBody）")]
    public LayerMask 觸發圖層 = ~0;   // 預設全部，實務上設成感測器那層

    [Header("除錯")]
    public bool 除錯訊息 = false;

    // 記錄已進入的判定器，避免同一台車多個感測器造成重複計數不平衡
    private readonly System.Collections.Generic.HashSet<TurnSignalJudge> 已進入
        = new System.Collections.Generic.HashSet<TurnSignalJudge>();

    private void OnTriggerEnter(Collider other)
    {
        if (!符合圖層(other)) return;

        var judge = other.GetComponentInParent<TurnSignalJudge>();
        if (judge == null) return;

        // 同一個判定器只在「第一個感測器進入」時暫停一次
        if (已進入.Add(judge))
        {
            judge.暫停偵測();
            if (除錯訊息) Debug.Log($"[方向燈暫停區] {other.name} 進入 → 暫停");
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!符合圖層(other)) return;

        var judge = other.GetComponentInParent<TurnSignalJudge>();
        if (judge == null) return;

        // 只在「最後一個感測器離開」時恢復
        // 這裡簡化為：任一感測器離開就恢復，並移出集合
        if (已進入.Remove(judge))
        {
            judge.恢復偵測();
            if (除錯訊息) Debug.Log($"[方向燈暫停區] {other.name} 離開 → 恢復");
        }
    }

    private bool 符合圖層(Collider c)
    {
        return (觸發圖層.value & (1 << c.gameObject.layer)) != 0;
    }
}