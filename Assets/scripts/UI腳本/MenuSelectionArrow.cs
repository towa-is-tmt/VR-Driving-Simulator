using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 選單選取標記 — 用箭頭標示「目前已選定的項目」。
///
/// 與 ButtonHoverScale 的箭頭不同：
///   ButtonHoverScale  → 標示「游標現在在哪」，移動就跟著跑
///   本腳本            → 標示「已經選了哪一個」，按下去後停在那裡
///
/// 兩者可以並存：游標用放大＋遮罩表示，選取用箭頭表示。
///
/// 掛在包住這些按鈕的父物件上（例如導航選單的面板）。
/// </summary>
public class MenuSelectionArrow : MonoBehaviour
{
    [Header("按鈕與箭頭（順序要一一對應）")]
    [Tooltip("選單裡的按鈕，依畫面上的順序拖入")]
    public Button[] 選項按鈕;

    [Tooltip("每顆按鈕左側的箭頭文字，順序要和上面一致")]
    public TMP_Text[] 箭頭;

    [Header("外觀")]
    public string 箭頭內容 = "▶";
    public Color 箭頭顏色 = new Color(0.16f, 0.82f, 1f, 1f);   // #29D0FF

    [Header("開場設定")]
    [Tooltip("開場預設選中第幾個（從 0 開始）。填 -1 表示一開始不標記任何項目")]
    public int 開場索引 = -1;

    [Tooltip("按到「清除」之類的按鈕時要取消標記，把那顆按鈕的索引填在這裡（-1 = 沒有）")]
    public int 清除按鈕索引 = -1;

    /// <summary>目前選定的索引，-1 表示沒有</summary>
    public int 目前索引 { get; private set; } = -1;

    private void Awake()
    {
        // 先把所有箭頭設好內容與顏色，並全部隱藏
        if (箭頭 != null)
        {
            foreach (var a in 箭頭)
            {
                if (a == null) continue;
                a.text  = 箭頭內容;
                a.color = 箭頭顏色;
                a.enabled = false;
            }
        }
    }

    private void Start()
    {
        接線();
        設定選取(開場索引);
    }

    private void 接線()
    {
        if (選項按鈕 == null) return;

        for (int i = 0; i < 選項按鈕.Length; i++)
        {
            int index = i;                       // 閉包要用區域變數
            if (選項按鈕[i] == null) continue;
            選項按鈕[i].onClick.AddListener(() => 被按下(index));
        }
    }

    private void 被按下(int index)
    {
        // 按到「清除選項」時取消所有標記
        if (index == 清除按鈕索引) 設定選取(-1);
        else                       設定選取(index);
    }

    /// <summary>設定目前標記的項目，-1 = 全部取消。也可從外部呼叫</summary>
    public void 設定選取(int index)
    {
        目前索引 = index;

        if (箭頭 == null) return;
        for (int i = 0; i < 箭頭.Length; i++)
        {
            if (箭頭[i] == null) continue;
            箭頭[i].enabled = (i == 目前索引);
        }
    }

    /// <summary>清除標記（可接到「清除選項」按鈕的 OnClick）</summary>
    public void 清除選取() => 設定選取(-1);
}
