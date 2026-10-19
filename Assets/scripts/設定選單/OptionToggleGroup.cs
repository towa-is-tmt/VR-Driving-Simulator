using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 二選一選項組 — 用於「操作方式（G923／鍵盤）」與「遊戲螢幕（PC版／VR版）」。
///
/// 被選中的按鈕會填滿強調色、文字轉深色；未選中的只有外框、文字為灰。
/// 目前只處理視覺與記錄選擇結果，實際套用留待之後接。
///
/// 掛在包住這幾顆按鈕的父物件上。
/// </summary>
public class OptionToggleGroup : MonoBehaviour
{
    [Header("選項按鈕（依順序）")]
    public Button[] 選項按鈕;

    [Header("對應的設定")]
    [Tooltip("這組按鈕要控制哪一項設定。\n" +
             "⚠ 選項按鈕的順序必須是：操作方式 = [0]G923 [1]鍵盤；遊戲螢幕 = [0]PC版 [1]VR版")]
    public GameSettings.選項群組 對應設定 = GameSettings.選項群組.無;

    [Header("目前選擇")]
    [Tooltip("預設選中第幾個（從 0 開始）")]
    public int 目前索引 = 0;

    [Header("配色")]
    public Color 選中底色   = new Color(0.16f, 0.82f, 1f, 1f);       // #29D0FF
    public Color 選中文字色 = new Color(0.055f, 0.102f, 0.169f, 1f); // #0E1A2B
    public Color 未選底色   = new Color(1f, 1f, 1f, 0f);             // 透明，只留外框
    public Color 未選文字色 = new Color(0.49f, 0.58f, 0.68f, 1f);    // #7E93AD

    [Header("外框（選填）")]
    [Tooltip("每顆按鈕的外框 Image，順序要和按鈕一致。留空則不處理外框")]
    public Image[] 外框;
    public Color 選中外框色 = new Color(0.16f, 0.82f, 1f, 1f);
    public Color 未選外框色 = new Color(0.49f, 0.58f, 0.68f, 0.6f);

    /// <summary>選擇改變時通知外部（之後要套用設定時可訂閱）</summary>
    public event System.Action<int> 選擇改變;

    private void Start()
    {
        接線();

        // 有對應設定時，以已儲存的選擇為準（跨場景、重開遊戲都會記得）
        if (對應設定 != GameSettings.選項群組.無)
            目前索引 = GameSettings.取群組(對應設定);

        套用外觀();
    }

    private void 接線()
    {
        if (選項按鈕 == null) return;

        for (int i = 0; i < 選項按鈕.Length; i++)
        {
            int index = i;                       // 閉包要用區域變數
            if (選項按鈕[i] == null) continue;
            選項按鈕[i].onClick.AddListener(() => 選擇(index));
        }
    }

    /// <summary>選擇第 index 個選項（也可直接接到按鈕的 OnClick）</summary>
    public void 選擇(int index)
    {
        if (選項按鈕 == null || index < 0 || index >= 選項按鈕.Length) return;

        目前索引 = index;
        套用外觀();

        if (對應設定 != GameSettings.選項群組.無)
            GameSettings.設群組(對應設定, 目前索引);   // 即時寫入設定並存檔

        選擇改變?.Invoke(目前索引);
    }

    private void 套用外觀()
    {
        if (選項按鈕 == null) return;

        for (int i = 0; i < 選項按鈕.Length; i++)
        {
            var b = 選項按鈕[i];
            if (b == null) continue;

            bool 選中 = (i == 目前索引);

            var img = b.GetComponent<Image>();
            if (img != null) img.color = 選中 ? 選中底色 : 未選底色;

            var txt = b.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null) txt.color = 選中 ? 選中文字色 : 未選文字色;

            if (外框 != null && i < 外框.Length && 外框[i] != null)
                外框[i].color = 選中 ? 選中外框色 : 未選外框色;
        }
    }
}
