using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// 按鈕滑過放大＋選取遮罩 — 讓「目前選到哪一顆」一眼就看得出來。
///
/// 設計重點：
/// ① 用 unscaledDeltaTime → 選單在 Time.timeScale = 0（凍結）時動畫照樣跑。
/// ② 同時實作 Pointer 與 Select 兩組介面：
///    - 滑鼠 / VR 手把射線 → Pointer Enter / Exit
///    - 鍵盤 / G923 方向鍵導航 → Select / Deselect
/// ③ 選取遮罩由腳本自動建立，不必在每顆按鈕手動加 Image。
/// ④ 音效交給場景上的 UISoundManager，音效檔只需設定一次。
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class ButtonHoverScale : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IPointerDownHandler, IPointerUpHandler,
    ISelectHandler, IDeselectHandler, ISubmitHandler
{
    [Header("縮放設定")]
    [Tooltip("滑過／被選取時的放大倍率（1 = 不變）")]
    [Range(1f, 1.5f)] public float 放大倍率 = 1.08f;

    [Tooltip("按下瞬間的縮小倍率，給一點回饋感（設 1 則不縮）")]
    [Range(0.8f, 1f)] public float 按下縮小倍率 = 0.96f;

    [Header("選取遮罩（讓選中的按鈕更明顯）")]
    [Tooltip("開啟後，選中的按鈕會蓋上一層半透明色塊")]
    public bool 使用選取遮罩 = true;

    [Tooltip("遮罩顏色與最大不透明度。淺色＝提亮、深色＝壓暗，可依你的配色調整")]
    public Color 遮罩顏色 = new Color(1f, 1f, 1f, 0.28f);

    [Tooltip("留空＝執行時自動建立一個遮罩子物件；也可自己指定既有的 Image")]
    public Image 遮罩圖;

    [Header("游標箭頭（標示目前游標停在哪一顆）")]
    [Tooltip("滑過或被方向盤選中時顯示的箭頭。\n" +
             "注意：這是「游標位置」，和 MenuSelectionArrow 的「已選定項目」不同，兩者可並存")]
    public TMP_Text 選中箭頭;

    [Tooltip("箭頭要顯示的內容")]
    public string 箭頭內容 = "▶";

    [Tooltip("箭頭顏色")]
    public Color 箭頭顏色 = new Color(0.16f, 0.82f, 1f, 1f);   // #29D0FF 青藍

    [Header("動畫")]
    [Tooltip("數字越大回彈越快。建議 10 ~ 18")]
    [Range(1f, 30f)] public float 動畫速度 = 12f;

    [Header("音效（音效檔請設定在場景的 UISoundManager 上）")]
    public bool 播放滑過音效 = true;
    public bool 播放點擊音效 = true;

    [Header("其他")]
    [Tooltip("按鈕不可互動（灰掉）時不放大、不顯示遮罩")]
    public bool 停用時不放大 = true;

    private RectTransform rt;
    private Selectable 可選元件;
    private Vector3 原始縮放;

    private bool 滑過中;
    private bool 選取中;
    private bool 按下中;

    private void Awake()
    {
        rt = GetComponent<RectTransform>();
        可選元件 = GetComponent<Selectable>();
        原始縮放 = rt.localScale;

        if (使用選取遮罩 && 遮罩圖 == null) 建立遮罩();
        if (遮罩圖 != null) 設定遮罩透明度(0f);

        if (選中箭頭 != null)
        {
            選中箭頭.text  = 箭頭內容;
            選中箭頭.color = 箭頭顏色;
            選中箭頭.enabled = false;      // 平常隱藏
        }
    }

    /// <summary>自動建立一個鋪滿按鈕的遮罩子物件，疊在最上層</summary>
    private void 建立遮罩()
    {
        var go = new GameObject("選取遮罩", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);

        var r = go.GetComponent<RectTransform>();
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.offsetMin = Vector2.zero;
        r.offsetMax = Vector2.zero;

        遮罩圖 = go.GetComponent<Image>();
        遮罩圖.raycastTarget = false;      // 不可擋住點擊
        遮罩圖.color = new Color(遮罩顏色.r, 遮罩顏色.g, 遮罩顏色.b, 0f);

        go.transform.SetAsLastSibling();   // 蓋在文字與圖之上
    }

    private void 設定遮罩透明度(float a)
    {
        if (遮罩圖 == null) return;
        var c = 遮罩圖.color;
        遮罩圖.color = new Color(遮罩顏色.r, 遮罩顏色.g, 遮罩顏色.b, a);
    }

    private void OnDisable()
    {
        // 物件被關掉時立刻回原狀，避免下次開啟時卡在放大／有遮罩的狀態
        滑過中 = 選取中 = 按下中 = false;
        if (rt != null) rt.localScale = 原始縮放;
        設定遮罩透明度(0f);
        設定箭頭(false);
    }

    private void Update()
    {
        bool 可以互動 = !停用時不放大 || 可互動();
        bool 要突顯 = 可以互動 && (滑過中 || 選取中);

        // ── 縮放 ──
        float 目標倍率 = 1f;
        if (可以互動)
        {
            if (按下中)      目標倍率 = 按下縮小倍率;
            else if (要突顯) 目標倍率 = 放大倍率;
        }

        // unscaledDeltaTime：選單凍結（timeScale = 0）時動畫仍會跑
        float t = 1f - Mathf.Exp(-動畫速度 * Time.unscaledDeltaTime);
        rt.localScale = Vector3.Lerp(rt.localScale, 原始縮放 * 目標倍率, t);

        // ── 游標箭頭 ──
        設定箭頭(要突顯);

        // ── 遮罩 ──
        if (使用選取遮罩 && 遮罩圖 != null)
        {
            float 目標透明度 = 要突顯 ? 遮罩顏色.a : 0f;
            float 現在 = 遮罩圖.color.a;
            設定遮罩透明度(Mathf.Lerp(現在, 目標透明度, t));
        }
    }

    private void 設定箭頭(bool 顯示)
    {
        if (選中箭頭 != null && 選中箭頭.enabled != 顯示) 選中箭頭.enabled = 顯示;
    }

    private bool 可互動() => 可選元件 == null || 可選元件.IsInteractable();

    // ── 滑鼠 / VR 手把射線 ──────────────────────────────
    public void OnPointerEnter(PointerEventData e)
    {
        滑過中 = true;
        if (播放滑過音效 && 可互動() && UISoundManager.實例 != null)
            UISoundManager.實例.播放滑過();
    }

    public void OnPointerExit(PointerEventData e) { 滑過中 = false; 按下中 = false; }

    public void OnPointerDown(PointerEventData e)
    {
        按下中 = true;
        if (播放點擊音效 && 可互動() && UISoundManager.實例 != null)
            UISoundManager.實例.播放點擊();
    }

    public void OnPointerUp(PointerEventData e) { 按下中 = false; }

    // ── 鍵盤 / 手把 / G923 方向鍵導航 ───────────────────
    public void OnSelect(BaseEventData e)
    {
        選取中 = true;
        if (播放滑過音效 && 可互動() && UISoundManager.實例 != null)
            UISoundManager.實例.播放滑過();
    }

    public void OnDeselect(BaseEventData e) { 選取中 = false; }

    public void OnSubmit(BaseEventData e)
    {
        if (播放點擊音效 && 可互動() && UISoundManager.實例 != null)
            UISoundManager.實例.播放點擊();
    }
}
