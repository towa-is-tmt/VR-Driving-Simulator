using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// 設定拉條 — 音量之類的數值調整，同時支援滑鼠與方向盤，兩種方式並存。
///
/// 調整精度：
///   滑鼠拖曳／點擊  → 每格 1（Whole Numbers，可細調）
///   方向盤左右鍵    → 每格 5（快速調整）
///
/// 視覺回饋：
///   滑鼠移上去或被方向盤選中時，把手（白色圓球）會放大，
///   一眼看得出目前正在調整哪一項。
///
/// 掛在 Slider 物件上。
/// </summary>
[RequireComponent(typeof(Slider))]
public class SettingsSlider : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    ISelectHandler, IDeselectHandler
{
    [Header("調整幅度")]
    [Tooltip("方向盤左右鍵每按一次的增減量")]
    public float 方向盤步進 = 5f;

    [Tooltip("滑鼠與鍵盤的最小調整單位")]
    public float 滑鼠步進 = 1f;

    [Header("對應的設定項目")]
    [Tooltip("這條拉條要控制哪一項設定。選「無」則不連動，只是單純的拉條")]
    public GameSettings.設定項目 設定項目 = GameSettings.設定項目.無;

    [Header("數值範圍")]
    public float 最小值 = 0f;
    public float 最大值 = 100f;
    public float 預設值 = 70f;

    [Header("把手放大")]
    [Tooltip("拉條的把手（白色圓球）。留空會自動抓 Slider 的 Handle")]
    public RectTransform 把手;

    [Tooltip("滑過或選中時把手的放大倍率")]
    [Range(1f, 2.5f)] public float 突顯放大 = 1.5f;

    [Tooltip("放大／縮小的動畫速度")]
    [Range(1f, 30f)] public float 動畫速度 = 14f;

    [Header("項目標題（提示目前調整位置）")]
    [Tooltip("這條拉條左邊的標題文字，例如「主音量」。被選中時會變色，讓玩家知道正在調哪一項")]
    public TextMeshProUGUI 標題文字;

    [Tooltip("平常的標題顏色")]
    public Color 標題原色 = new Color(0.949f, 0.973f, 1f, 1f);      // #F2F8FF

    [Tooltip("被選中／滑過時的標題顏色")]
    public Color 標題突顯色 = new Color(1f, 0.851f, 0.239f, 1f);    // #FFD93D 黃

    [Header("數值顯示")]
    [Tooltip("右側顯示目前數值的文字，可留空")]
    public TextMeshProUGUI 數值文字;

    private Slider 拉條;
    private Vector3 把手原始縮放 = Vector3.one;
    private bool 突顯中;

    /// <summary>目前數值（0~100）</summary>
    public float 數值 => 拉條 != null ? 拉條.value : 0f;

    private void Awake()
    {
        拉條 = GetComponent<Slider>();

        // 滑鼠拖曳時以 1 為單位，達成「可更精緻調整」
        拉條.wholeNumbers = true;
        拉條.minValue = 最小值;
        拉條.maxValue = 最大值;

        if (把手 == null && 拉條.handleRect != null) 把手 = 拉條.handleRect;
        if (把手 != null) 把手原始縮放 = 把手.localScale;

        if (標題文字 != null) 套用標題顏色(false);

        拉條.onValueChanged.AddListener(v =>
        {
            更新數值文字();
            if (設定項目 != GameSettings.設定項目.無)
                GameSettings.設值(設定項目, v);      // 即時套用，不需要按確認
        });
    }

    private void Start()
    {
        // 有對應設定項目時，以「已儲存的設定值」為準，而不是 Inspector 的預設值。
        // 這樣換場景或重開遊戲後，拉條會顯示玩家上次調的位置。
        float 初值 = (設定項目 != GameSettings.設定項目.無)
                   ? GameSettings.取值(設定項目)
                   : 預設值;

        拉條.SetValueWithoutNotify(Mathf.Clamp(Mathf.Round(初值), 最小值, 最大值));
        更新數值文字();
    }

    private void OnDisable()
    {
        突顯中 = false;
        if (把手 != null) 把手.localScale = 把手原始縮放;
        套用標題顏色(false);
    }

    private void Update()
    {
        套用標題顏色(突顯中);

        if (把手 == null) return;

        Vector3 目標 = 突顯中 ? 把手原始縮放 * 突顯放大 : 把手原始縮放;

        // unscaledDeltaTime：設定選單通常在凍結狀態下開啟
        float t = 1f - Mathf.Exp(-動畫速度 * Time.unscaledDeltaTime);
        把手.localScale = Vector3.Lerp(把手.localScale, 目標, t);
    }

    // ── 方向盤呼叫（每次 5）──────────────────────────────
    public void 增加() => 設定數值(數值 + 方向盤步進);
    public void 減少() => 設定數值(數值 - 方向盤步進);

    // ── 細調（每次 1，給鍵盤或其他來源用）───────────────
    public void 增加細調() => 設定數值(數值 + 滑鼠步進);
    public void 減少細調() => 設定數值(數值 - 滑鼠步進);

    public void 設定數值(float v)
    {
        if (拉條 == null) return;
        拉條.value = Mathf.Clamp(Mathf.Round(v), 最小值, 最大值);
    }

    /// <summary>標題文字依是否被選中切換顏色，讓玩家一眼看出正在調哪一項</summary>
    private void 套用標題顏色(bool 突顯)
    {
        if (標題文字 == null) return;

        Color 目標 = 突顯 ? 標題突顯色 : 標題原色;
        if (標題文字.color == 目標) return;      // 沒變就不寫，省效能

        // 用 unscaledDeltaTime：設定選單通常在凍結狀態下開啟
        float t = 1f - Mathf.Exp(-動畫速度 * Time.unscaledDeltaTime);
        標題文字.color = Color.Lerp(標題文字.color, 目標, t);
    }

    private void 更新數值文字()
    {
        if (數值文字 != null) 數值文字.text = Mathf.RoundToInt(數值).ToString();
    }

    // ── 突顯狀態 ─────────────────────────────────────────
    public void OnPointerEnter(PointerEventData e) => 突顯中 = true;
    public void OnPointerExit (PointerEventData e) => 突顯中 = false;
    public void OnSelect      (BaseEventData e)    => 突顯中 = true;
    public void OnDeselect    (BaseEventData e)    => 突顯中 = false;
}
