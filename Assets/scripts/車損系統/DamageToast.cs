using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 行駛中車損提示 — 撞到東西時，在畫面上跳出一行小字，數秒後自動淡出消失。
///
/// 例：「右前角　撞到 電線桿」
///
/// 掛法：掛在 HUD Canvas 底下、放在方向盤左上方的那個 TMP 文字物件上，
///       或掛在常駐物件並把文字欄位拖進來。
///
/// 特性：
///   ① 連續撞擊時以最新一次為準並重新計時，不會排隊卡住
///   ② 依損傷程度自動變色
///   ③ 用 unscaledDeltaTime，中離凍結時也能正常淡出（可切換）
/// </summary>
public class DamageToast : MonoBehaviour
{
    [Header("顯示元件")]
    [Tooltip("整組提示的 CanvasGroup（底板＋文字的共同父物件）。\n" +
             "指定後底板會和文字一起淡入淡出；留空則自動抓自己身上的。")]
    public CanvasGroup 群組;

    [Tooltip("要顯示提示的 TMP 文字。留空則自動抓自己或子物件身上的")]
    public TextMeshProUGUI 文字;

    [Tooltip("提示底板（選填）。指定後可讓底板顏色跟著損傷程度變化")]
    public Image 背景圖;

    [Header("時間")]
    [Tooltip("完整顯示幾秒後開始淡出")]
    public float 顯示秒數 = 3f;

    [Tooltip("淡出所需秒數")]
    public float 淡出秒數 = 0.6f;

    [Header("內容")]
    [Tooltip("除了部位，也顯示撞到的物件名稱")]
    public bool 顯示撞到的物件 = true;

    [Tooltip("物件名稱太長時截斷的字數上限")]
    public int 物件名稱字數上限 = 10;

    [Header("顏色（依損傷程度）")]
    public Color 輕微色 = new Color(1f, 0.851f, 0.239f);    // #FFD93D 黃
    public Color 中度色 = new Color(1f, 0.541f, 0.122f);    // #FF8A1F 深橘
    public Color 嚴重色 = new Color(1f, 0.141f, 0.212f);    // #FF2436 正紅

    [Header("底板")]
    [Tooltip("底板顏色跟著損傷程度變（否則維持你在 Inspector 設定的顏色）")]
    public bool 底板跟著變色 = false;

    [Tooltip("底板跟著變色時的不透明度")]
    [Range(0f, 1f)] public float 底板不透明度 = 0.35f;

    [Header("其他")]
    [Tooltip("凍結（timeScale = 0）時提示仍會繼續倒數淡出")]
    public bool 不受凍結影響 = false;

    [Tooltip("遊戲一凍結（開啟中離、結算、設定等面板）就立刻收掉提示，\n" +
             "避免提示卡在畫面上疊到面板前面")]
    public bool 凍結時立即隱藏 = true;


    private Coroutine 目前流程;
    private bool 上次是凍結 = false;

    private Color 底板原色 = Color.white;

    private void Awake()
    {
        if (群組 == null) 群組 = GetComponent<CanvasGroup>();
        if (文字 == null) 文字 = GetComponentInChildren<TextMeshProUGUI>(true);
        if (背景圖 == null) 背景圖 = GetComponentInChildren<Image>(true);

        if (背景圖 != null) 底板原色 = 背景圖.color;

        // 提示只是資訊顯示，不該擋住任何點擊
        if (群組 != null) 群組.blocksRaycasts = false;

        隱藏();
    }

    private void OnEnable()
    {
        DamageLog.發生損傷 += 收到損傷;
    }

    private void OnDisable()
    {
        // 必須取消訂閱，否則換場景後會留下失效參考
        DamageLog.發生損傷 -= 收到損傷;
    }

    private void Update()
    {
        if (!凍結時立即隱藏) return;

        bool 凍結中 = Mathf.Approximately(Time.timeScale, 0f);

        // 只在「剛進入凍結」的那一刻收掉，避免每幀重複處理
        if (凍結中 && !上次是凍結) 立即隱藏();

        上次是凍結 = 凍結中;
    }

    /// <summary>立刻收掉提示（中離面板開啟時呼叫，也可接到按鈕）</summary>
    public void 立即隱藏()
    {
        if (目前流程 != null) { StopCoroutine(目前流程); 目前流程 = null; }
        隱藏();
    }

    private void 收到損傷(DamageLog.紀錄 r)
    {
        if (文字 == null) return;

        // 凍結中（面板開著）不顯示提示，等回到遊戲再說
        if (凍結時立即隱藏 && Mathf.Approximately(Time.timeScale, 0f)) return;

        文字.text  = 組字串(r);
        文字.color = 取顏色(r.損傷程度);

        if (背景圖 != null)
        {
            if (底板跟著變色)
            {
                Color c = 取顏色(r.損傷程度);
                背景圖.color = new Color(c.r, c.g, c.b, 底板不透明度);
            }
            else
            {
                背景圖.color = 底板原色;
            }
        }

        if (目前流程 != null) StopCoroutine(目前流程);
        目前流程 = StartCoroutine(顯示流程());
    }

    private string 組字串(DamageLog.紀錄 r)
    {
        if (!顯示撞到的物件) return r.撞擊部位.ToString();

        string 物件 = r.撞到的物件 ?? "";
        if (物件名稱字數上限 > 0 && 物件.Length > 物件名稱字數上限)
            物件 = 物件.Substring(0, 物件名稱字數上限) + "…";

        return $"{r.撞擊部位}　撞到 {物件}";
    }

    private Color 取顏色(DamageLog.程度 p) => p switch
    {
        DamageLog.程度.嚴重 => 嚴重色,
        DamageLog.程度.中度 => 中度色,
        _                   => 輕微色,
    };

    private IEnumerator 顯示流程()
    {
        設定透明度(1f);

        // 完整顯示
        float t = 0f;
        while (t < 顯示秒數)
        {
            t += 不受凍結影響 ? Time.unscaledDeltaTime : Time.deltaTime;
            yield return null;
        }

        // 淡出
        t = 0f;
        float d = Mathf.Max(0.01f, 淡出秒數);
        while (t < d)
        {
            t += 不受凍結影響 ? Time.unscaledDeltaTime : Time.deltaTime;
            設定透明度(1f - t / d);
            yield return null;
        }

        隱藏();
        目前流程 = null;
    }

    /// <summary>有 CanvasGroup 就整組一起淡；否則退回只淡文字</summary>
    private void 設定透明度(float a)
    {
        a = Mathf.Clamp01(a);

        if (群組 != null)
        {
            群組.alpha = a;
            return;
        }

        if (文字 != null)
        {
            var c = 文字.color;
            文字.color = new Color(c.r, c.g, c.b, a);
        }
        if (背景圖 != null)
        {
            var c = 背景圖.color;
            背景圖.color = new Color(c.r, c.g, c.b, a * 底板原色.a);
        }
    }

    /// <summary>
    /// 只把透明度設為 0，不呼叫 SetActive(false)。
    /// 因為本腳本就掛在這個物件上，關掉自己會觸發 OnDisable 取消訂閱，
    /// 之後就再也收不到碰撞通知了。
    /// 透明度 0 ＋ blocksRaycasts = false 已足以達到「完全不顯示、不擋點擊」。
    /// </summary>
    private void 隱藏() => 設定透明度(0f);
}
