// ─────────────────────────────────────────────────────────────
// 此腳本已停用，功能由隊友的 CarIndicatorController 取代。
//
// CarIndicatorController（Assets/車庫 全部東西/程式碼/）已具備：
//   ‧ 左／右／雙閃（Onleft / Onright / Ondouble）
//   ‧ 喇叭按住持續響（Onspeaker / Onspeakers）
//   ‧ 閃爍、文字顯示、音效、10 秒自動關閉
//
// 兩支同時掛在車上會搶著控制同一組燈光與音效，因此停用本檔。
// 確認場景已移除本元件後，可直接刪除這個檔案。
// ─────────────────────────────────────────────────────────────
using UnityEngine;

[System.Obsolete("已由隊友的 CarIndicatorController 取代")]
public class TurnSignalController : MonoBehaviour
{
    private void Awake()
    {
        Debug.LogWarning($"[方向燈] 「{name}」上掛著已停用的 TurnSignalController，" +
                         $"請移除，改用 CarIndicatorController。");
        enabled = false;
    }
}
