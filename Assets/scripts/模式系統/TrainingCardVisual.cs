using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ── 訓練卡片視覺:滑過／選中顯示藍色外框 ──────────
// 掛在每張卡片(有 Toggle 的物件)上
// 注意:Toggle 的 Graphic 欄位請留空(None),藍框由本腳本控制
[RequireComponent(typeof(Toggle))]
public class TrainingCardVisual : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
    ISelectHandler, IDeselectHandler
{
    [Header("藍色外框(拖 SelectFrame 進來)")]
    [Tooltip("建議:Image + Sliced 空心框,或用 Outline 圖")]
    public Graphic selectFrame;

    [Header("外框顏色")]
    [Tooltip("滑鼠移過時的外框顏色(通常淡一點)")]
    public Color hoverColor = new Color(0.20f, 0.70f, 1f, 0.65f);
    [Tooltip("被選中時的外框顏色(最亮)")]
    public Color selectedColor = new Color(0.20f, 0.70f, 1f, 1f);

    [Header("淡入淡出速度")]
    public float fadeSpeed = 12f;

    private Toggle toggle;
    private bool isHover;      // 滑鼠滑過
    private bool isFocused;    // 方向盤／鍵盤選取（EventSystem 目前選中）
    private Color targetColor;

    void Awake()
    {
        toggle = GetComponent<Toggle>();
        if (toggle != null) toggle.onValueChanged.AddListener(OnToggleChanged);

        // 自動找名為 SelectFrame 的子物件(沒拖也能運作)
        if (selectFrame == null)
        {
            Transform t = transform.Find("SelectFrame");
            if (t != null) selectFrame = t.GetComponent<Graphic>();
        }

        Refresh();
        ApplyInstant();
    }

    void OnEnable()
    {
        isHover = false;      // 面板重開時清掉殘留的狀態
        isFocused = false;
        Refresh();
        ApplyInstant();
    }

    void OnDestroy()
    {
        if (toggle != null) toggle.onValueChanged.RemoveListener(OnToggleChanged);
    }

    void Update()
    {
        if (selectFrame == null) return;
        Color now = selectFrame.color;
        if (Approximately(now, targetColor)) return;   // 到位就停,不浪費效能
        selectFrame.color = Color.Lerp(now, targetColor, Time.unscaledDeltaTime * fadeSpeed);
    }

    // ── 狀態計算 ──────────
    private void Refresh()
    {
        bool on = (toggle != null && toggle.isOn);

        if (on) targetColor = selectedColor;              // 已勾選 → 常駐最亮
        else if (isHover || isFocused) targetColor = hoverColor;                 // 滑過或方向盤選到 → 淡藍框
        else targetColor = MakeTransparent(hoverColor); // 都沒有 → 消失

        // 外框只在需要時參與射線,避免擋住點擊
        if (selectFrame != null) selectFrame.raycastTarget = false;
    }

    private void ApplyInstant()
    {
        if (selectFrame != null) selectFrame.color = targetColor;
    }

    private void OnToggleChanged(bool value) => Refresh();

    public void OnPointerEnter(PointerEventData e) { isHover = true; Refresh(); }
    public void OnPointerExit(PointerEventData e) { isHover = false; Refresh(); }

    // ── 方向盤／鍵盤透過 EventSystem 選取時，也顯示淡藍框 ──
    public void OnSelect(BaseEventData e) { isFocused = true; Refresh(); }
    public void OnDeselect(BaseEventData e) { isFocused = false; Refresh(); }

    // ── 小工具 ──────────
    private static Color MakeTransparent(Color c) => new Color(c.r, c.g, c.b, 0f);

    private static bool Approximately(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.003f && Mathf.Abs(a.g - b.g) < 0.003f
            && Mathf.Abs(a.b - b.b) < 0.003f && Mathf.Abs(a.a - b.a) < 0.003f;
    }
}