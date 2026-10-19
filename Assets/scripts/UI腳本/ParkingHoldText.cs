using UnityEngine;
using TMPro;

/// <summary>
/// 停車評分倒數提示 — 打 P 檔靜止等待評分時，在畫面上顯示「等待評分中」與進度。
///
/// 同時支援兩種考試：
///   ‧ ParkingDetector（倒車入庫、路邊側停）
///   ‧ RampDetector  （坡道定點停車）
///
/// 兩者都有公開的 stopTimer 與 requiredStopDuration，直接讀取即可，
/// 不需要改動偵測邏輯。
///
/// 掛在提示文字的父物件上（建議底下放一層底板 ＋ 一個 TMP 文字）。
/// </summary>
public class ParkingHoldText : MonoBehaviour
{
    [Header("偵測器（留空會自動尋找）")]
    public ParkingDetector 停車偵測;
    public RampDetector     坡道偵測;

    [Header("顯示元件")]
    [Tooltip("整組提示的 CanvasGroup。留空則自動抓自己身上的")]
    public CanvasGroup 群組;

    [Tooltip("提示文字。留空則自動抓子物件的")]
    public TextMeshProUGUI 文字;

    [Header("文字內容")]
    public string 提示文字 = "等待評分中";

    [Tooltip("顯示剩餘秒數，例如「等待評分中　3」")]
    public bool 顯示秒數 = true;

    [Tooltip("勾選＝評分完成後不顯示任何文字，面板立刻關閉；\n" +
             "取消＝顯示下方的完成文字停留幾秒後才關")]
    public bool 完成後立即關閉 = true;

    [Tooltip("完成時顯示的文字（完成後立即關閉時不會用到）")]
    public string 完成文字 = "評分完成";

    [Tooltip("完成文字停留幾秒")]
    public float 完成停留秒數 = 1.5f;

    [Header("顏色")]
    public Color 等待色 = new Color(1f, 0.851f, 0.239f);   // #FFD93D 黃
    public Color 完成色 = new Color(0.216f, 0.878f, 0.627f); // #37E0A0 綠

    [Header("動畫")]
    [Range(1f, 30f)] public float 淡入淡出速度 = 12f;

    private float 完成倒數 = 0f;
    private bool  上次已完成 = false;

    private void Awake()
    {
        if (群組 == null) 群組 = GetComponent<CanvasGroup>();
        if (文字 == null) 文字 = GetComponentInChildren<TextMeshProUGUI>(true);

        if (群組 != null) 群組.blocksRaycasts = false;   // 純提示，不擋點擊
        設定透明度(0f);
    }

    private void Start()
    {
        if (停車偵測 == null) 停車偵測 = FindObjectOfType<ParkingDetector>(true);
        if (坡道偵測 == null) 坡道偵測 = FindObjectOfType<RampDetector>(true);
    }

    private void Update()
    {
        float 計時 = 0f, 需要 = 0f;
        bool 已完成 = false;
        bool 計時中 = 取得狀態(ref 計時, ref 需要, ref 已完成);

        // 剛完成
        if (已完成 && !上次已完成)
        {
            if (完成後立即關閉)
            {
                // 不做任何過場，直接收掉整個面板
                完成倒數 = 0f;
                設定透明度(0f);
                上次已完成 = true;
                return;
            }

            完成倒數 = 完成停留秒數;
            if (文字 != null) { 文字.text = 完成文字; 文字.color = 完成色; }
        }
        上次已完成 = 已完成;

        // 完成狀態持續期間不再顯示任何東西
        if (完成後立即關閉 && 已完成)
        {
            設定透明度(0f);
            return;
        }

        if (完成倒數 > 0f)
        {
            完成倒數 -= Time.deltaTime;
            設定透明度(Mathf.MoveTowards(取透明度(), 1f, 淡入淡出速度 * Time.deltaTime));
            return;
        }

        if (計時中)
        {
            if (文字 != null)
            {
                文字.color = 等待色;
                // 用無條件進位取整數：剩 2.4 秒顯示「3」、剩 0.3 秒顯示「1」，
                // 這樣倒數會是 3 → 2 → 1，不會一開始就跳 2、也不會出現 0
                int 剩餘 = Mathf.Clamp(Mathf.CeilToInt(需要 - 計時), 1, Mathf.CeilToInt(需要));

                文字.text = 顯示秒數 ? $"{提示文字}　{剩餘}" : 提示文字;
            }
            設定透明度(Mathf.MoveTowards(取透明度(), 1f, 淡入淡出速度 * Time.deltaTime));
        }
        else
        {
            設定透明度(Mathf.MoveTowards(取透明度(), 0f, 淡入淡出速度 * Time.deltaTime));
        }
    }

    /// <summary>回傳是否正在計時，並帶出目前秒數、需要秒數、是否已完成</summary>
    private bool 取得狀態(ref float 計時, ref float 需要, ref bool 已完成)
    {
        // 坡道優先（在坡道區內時通常就是在考坡道）
        if (坡道偵測 != null && 坡道偵測.isActiveAndEnabled && 坡道偵測.stopTimer > 0f)
        {
            計時 = 坡道偵測.stopTimer;
            需要 = 坡道偵測.requiredStopDuration;
            已完成 = 坡道偵測.pGearConfirmed;
            return !已完成;
        }

        if (停車偵測 != null && 停車偵測.isActiveAndEnabled && 停車偵測.stopTimer > 0f)
        {
            計時 = 停車偵測.stopTimer;
            需要 = 停車偵測.requiredStopDuration;
            已完成 = 停車偵測.pGearConfirmed;
            return !已完成;
        }

        // 沒在計時，但可能剛完成（stopTimer 已歸零）
        已完成 = (坡道偵測 != null && 坡道偵測.pGearConfirmed)
              || (停車偵測 != null && 停車偵測.pGearConfirmed);
        return false;
    }

    private float 取透明度() => 群組 != null ? 群組.alpha : (文字 != null ? 文字.color.a : 0f);

    private void 設定透明度(float a)
    {
        a = Mathf.Clamp01(a);
        if (群組 != null) { 群組.alpha = a; return; }
        if (文字 != null)
        {
            var c = 文字.color;
            文字.color = new Color(c.r, c.g, c.b, a);
        }
    }
}
