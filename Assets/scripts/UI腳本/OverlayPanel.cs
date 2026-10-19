using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 疊層面板 — 用於「規則介紹」這類蓋在主選單上方的說明視窗。
///
/// 特性：
/// ① 開場自動隱藏，不會一進遊戲就擋住畫面。
/// ② 只是疊在主選單上層，關閉後主選單原封不動（不換場景、不重載）。
/// ③ 可按 ESC 關閉（可在 Inspector 取消，避免和其他 ESC 功能衝突）。
/// ④ 開啟／關閉都是公開方法，Button 的 OnClick 直接拖來用即可。
/// </summary>
public class OverlayPanel : MonoBehaviour
{
    [Header("要控制的面板")]
    [Tooltip("規則介紹的整個面板物件（含底色遮罩、說明圖、關閉鈕）")]
    public GameObject 面板;

    [Header("方向盤按鍵（選填）")]
    [Tooltip("G923 用的 Input Actions 資產。不需要方向盤操作可留空")]
    public InputActionAsset 輸入設定;

    public string 動作對應表名稱 = "car";

    [Tooltip("開關此面板的動作名稱，例如 setting。留空則不接方向盤")]
    public string 開關動作名稱 = "";

    [Tooltip("勾選＝按一次開、再按一次關；取消＝只負責開啟")]
    public bool 按鍵為切換 = true;

    [Header("行為設定")]
    [Tooltip("進入場景時自動隱藏面板")]
    public bool 開場隱藏 = true;

    [Tooltip("面板開啟時，按 ESC 可關閉。若和其他 ESC 功能衝突請取消勾選")]
    public bool 按ESC關閉 = true;

    private InputAction 開關動作;
    private System.Action<InputAction.CallbackContext> 開關回呼;

    private void Start()
    {
        if (開場隱藏 && 面板 != null)
            面板.SetActive(false);
    }

    private void OnEnable()
    {
        綁定按鍵();
    }

    private void OnDisable()
    {
        // ⚠ 不呼叫 Disable()：InputActionAsset 的動作為全場景共用實例
        if (開關動作 != null && 開關回呼 != null)
            開關動作.performed -= 開關回呼.Invoke;
        開關回呼 = null;
    }

    private void 綁定按鍵()
    {
        if (輸入設定 == null || string.IsNullOrEmpty(開關動作名稱)) return;

        var map = 輸入設定.FindActionMap(動作對應表名稱, false);
        開關動作 = map?.FindAction(開關動作名稱, false);

        if (開關動作 == null)
        {
            Debug.LogWarning($"[疊層面板] 在 {動作對應表名稱} 找不到動作「{開關動作名稱}」。");
            return;
        }

        開關回呼 = _ => { if (按鍵為切換) 切換(); else 開啟(); };
        開關動作.performed += 開關回呼.Invoke;
        if (!開關動作.enabled) 開關動作.Enable();
    }

    private void Update()
    {
        if (!按ESC關閉 || 面板 == null) return;

        if (面板.activeSelf && Input.GetKeyDown(KeyCode.Escape))
            關閉();
    }

    // ── 給 Button 的 OnClick 直接呼叫 ──────────────────

    /// <summary>開啟說明面板（接到「規則介紹」按鈕）</summary>
    public void 開啟()
    {
        if (面板 != null) 面板.SetActive(true);
    }

    /// <summary>關閉說明面板（接到右下角的「退出」按鈕）</summary>
    public void 關閉()
    {
        if (面板 != null) 面板.SetActive(false);
    }

    /// <summary>開關切換（想用同一顆按鈕開關時用）</summary>
    public void 切換()
    {
        if (面板 != null) 面板.SetActive(!面板.activeSelf);
    }
}
