// ─────────────────────────────────────────────────────────────
// 此腳本已停用，功能由 InputManager 取代。
//
// 原因：專案中已有 InputManager（DontDestroyOnLoad 單例）在做同一件事，
//       兩者同時存在會互相覆蓋元件的啟用狀態。
//
// 請改用：InputManager
// ─────────────────────────────────────────────────────────────
using UnityEngine;

[System.Obsolete("已由 InputManager 取代")]
public class ControlModeApplier : MonoBehaviour
{
    private void Awake()
    {
        Debug.LogWarning($"[操作方式] 「{name}」上掛著已停用的 ControlModeApplier，請移除，改用 InputManager。");
        enabled = false;
    }
}
