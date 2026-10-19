using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 方向盤中離橋接 — 讓 G923 的「跳出畫面」鍵等同鍵盤 ESC。
///
/// 行為與 TrainingModeManager 內的 ESC 完全一致：
///   訓練/自由進行中按下 → 開啟中離確認面板並凍結
///   面板已開啟時再按   → 取消中離、繼續遊戲
///
/// 不修改 TrainingModeManager，只呼叫它既有的公開方法，
/// 所以鍵盤 ESC 仍然可用，兩種操作並存。
///
/// 掛法：掛在 car 場景任一常駐物件上（例如 TrainingModeManager 同一個物件）。
/// </summary>
public class WheelExitBridge : MonoBehaviour
{
    [Header("輸入設定")]
    [Tooltip("拖入 G923Controls.inputactions 資產")]
    public InputActionAsset 輸入設定;

    [Tooltip("動作所在的 Action Map 名稱")]
    public string 動作對應表名稱 = "car";

    [Tooltip("要當作中離鍵的動作名稱（方向盤的跳出畫面鍵）")]
    public string 動作名稱 = "esc";

    [Header("目標")]
    [Tooltip("場景上的 TrainingModeManager，留空會自動尋找")]
    public TrainingModeManager 模式管理器;

    [Header("除錯")]
    public bool 除錯訊息 = false;

    private InputAction 中離動作;
    private System.Action<InputAction.CallbackContext> 回呼;

    private void OnEnable()
    {
        if (模式管理器 == null)
            模式管理器 = FindObjectOfType<TrainingModeManager>();

        if (模式管理器 == null)
        {
            Debug.LogWarning("[方向盤中離] 找不到 TrainingModeManager，方向盤中離鍵無作用。");
            return;
        }

        if (輸入設定 == null)
        {
            Debug.LogWarning("[方向盤中離] 沒有指定 輸入設定（G923Controls），方向盤中離鍵無作用。");
            return;
        }

        var map = 輸入設定.FindActionMap(動作對應表名稱, false);
        中離動作 = map?.FindAction(動作名稱, false);

        if (中離動作 == null)
        {
            Debug.LogWarning($"[方向盤中離] 在 {動作對應表名稱} 裡找不到動作「{動作名稱}」。");
            return;
        }

        if (除錯訊息)
        {
            if (中離動作.controls.Count == 0)
                Debug.LogWarning($"[方向盤中離] 動作「{動作名稱}」沒有對應到任何實體控制項（綁定路徑不符）。");
            else
                foreach (var c in 中離動作.controls)
                    Debug.Log($"[方向盤中離] 動作「{動作名稱}」綁到 → {c.path}");
        }

        回呼 = _ => 觸發中離();
        中離動作.performed += 回呼.Invoke;
        if (!中離動作.enabled) 中離動作.Enable();
    }

    private void OnDisable()
    {
        // ⚠ 不呼叫 Disable()：InputActionAsset 的動作為全場景共用實例，
        //   停用會影響其他也在使用同一個動作的腳本。
        if (中離動作 != null && 回呼 != null)
            中離動作.performed -= 回呼.Invoke;

        回呼 = null;
    }

    /// <summary>與 TrainingModeManager 的 ESC 分支相同的切換邏輯</summary>
    private void 觸發中離()
    {
        if (模式管理器 == null) return;

        // 不在遊戲進行中就不處理（和鍵盤 ESC 的條件一致）
        if (!模式管理器.inSession)
        {
            if (除錯訊息) Debug.Log("[方向盤中離] 目前不在遊戲進行中，忽略。");
            return;
        }

        var 面板 = 模式管理器.exitConfirmPanel;

        if (面板 != null && 面板.activeSelf)
        {
            if (除錯訊息) Debug.Log("[方向盤中離] 面板已開啟 → 取消中離");
            模式管理器.CancelExit();
        }
        else
        {
            if (除錯訊息) Debug.Log("[方向盤中離] → 開啟中離確認面板");
            模式管理器.RequestExit();
        }
    }
}
