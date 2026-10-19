using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 車損俯視圖 — 用一張車輛俯視圖 ＋ 八個偵測點，直接標示車子哪裡被撞。
///
/// 八個點的陣列順序必須和 DamageLog.部位 一致（順時針）：
///   [0] 左前角  [1] 車頭中  [2] 右前角  [3] 右側中
///   [4] 右後角  [5] 車尾中  [6] 左後角  [7] 左側中
///
/// 掛在常駐物件上（不要掛在面板本身，面板隱藏時腳本會停止運作）。
/// </summary>
public class DamageDiagramPanel : MonoBehaviour
{
    [Header("面板")]
    public GameObject 面板;

    [Header("八個偵測點（順序要對，見上方說明）")]
    [Tooltip("依序拖入：左前角、車頭中、右前角、右側中、右後角、車尾中、左後角、左側中")]
    public Image[] 八個點 = new Image[DamageLog.部位數量];

    [Header("文字")]
    [Tooltip("上方摘要：總次數與各程度統計")]
    public TextMeshProUGUI 摘要文字;

    [Tooltip("下方清單：列出被撞到的點")]
    public TextMeshProUGUI 明細文字;

    [Header("顏色")]
    public Color 未受損 = new Color(0.49f, 0.58f, 0.68f, 0.35f);   // #7E93AD 淡
    public Color 輕微色 = new Color(1f, 0.851f, 0.239f, 1f);       // #FFD93D 黃
    public Color 中度色 = new Color(1f, 0.541f, 0.122f, 1f);       // #FF8A1F 深橘
    public Color 嚴重色 = new Color(1f, 0.141f, 0.212f, 1f);       // #FF2436 正紅

    [Header("被撞的點放大倍率")]
    [Range(1f, 2.5f)] public float 受損放大 = 1.35f;

    [Header("明細顯示方式")]
    [Tooltip("勾選＝列出「部位 程度 次數」；取消＝只列「程度 次數」（部位已經在俯視圖上標出來了，不必重複）")]
    public bool 顯示部位名稱 = false;

    [Header("其他")]
    public bool 開場隱藏 = true;

    [Tooltip("按這個鍵切換車損報告的開/關（沒有 UI 按鈕時方便隨時叫出，錄影用）。設 None 停用")]
    public KeyCode 切換鍵 = KeyCode.F8;

    private Vector3[] 原始縮放;

    private const string 灰 = "#7E93AD";
    private const string 綠 = "#37E0A0";
    private const string 白 = "#F2F8FF";
    // 車損三級：黃 → 深橘 → 正紅
    private const string 黃 = "#FFD93D";
    private const string 橘 = "#FF8A1F";
    private const string 紅 = "#FF2436";

    private void Awake()
    {
        // 記住每個點原本的大小，之後放大縮小才有基準
        原始縮放 = new Vector3[八個點 != null ? 八個點.Length : 0];
        for (int i = 0; i < 原始縮放.Length; i++)
            原始縮放[i] = 八個點[i] != null ? 八個點[i].transform.localScale : Vector3.one;
    }

    private void Start()
    {
        if (開場隱藏 && 面板 != null) 面板.SetActive(false);
    }

    private void Update()
    {
        // 切換鍵：沒有 UI 按鈕時，隨時按一下開/關車損報告
        if (切換鍵 != KeyCode.None && Input.GetKeyDown(切換鍵))
        {
            bool 已開 = 面板 != null && 面板.activeSelf;
            if (已開) 關閉報告();
            else      開啟報告();   // 開啟報告() 內會先呼叫 重新整理()
        }
    }

    /// <summary>接到「查看車損報告」按鈕</summary>
    public void 開啟報告()
    {
        重新整理();
        if (面板 != null) 面板.SetActive(true);
    }

    /// <summary>接到報告頁的「返回」按鈕</summary>
    public void 關閉報告()
    {
        if (面板 != null) 面板.SetActive(false);
    }

    /// <summary>重新產生俯視圖與文字</summary>
    public void 重新整理()
    {
        更新八個點();
        寫入摘要();
        寫入明細();
    }

    private void 更新八個點()
    {
        if (八個點 == null) return;

        for (int i = 0; i < 八個點.Length && i < DamageLog.部位數量; i++)
        {
            var 圖 = 八個點[i];
            if (圖 == null) continue;

            var 部位 = (DamageLog.部位)i;
            bool 有撞到 = DamageLog.部位最嚴重(部位, out var 程度);

            圖.color = 有撞到 ? 取顏色(程度) : 未受損;

            Vector3 基準 = (原始縮放 != null && i < 原始縮放.Length) ? 原始縮放[i] : Vector3.one;
            圖.transform.localScale = 有撞到 ? 基準 * 受損放大 : 基準;
        }
    }

    /// <summary>取得富文字用的色碼</summary>
    private string 取色碼(DamageLog.程度 p) => p switch
    {
        DamageLog.程度.嚴重 => 紅,
        DamageLog.程度.中度 => 橘,
        _                   => 黃,
    };

    private Color 取顏色(DamageLog.程度 p) => p switch
    {
        DamageLog.程度.嚴重 => 嚴重色,
        DamageLog.程度.中度 => 中度色,
        _                   => 輕微色,
    };

    private void 寫入摘要()
    {
        if (摘要文字 == null) return;

        if (!DamageLog.有損傷)
        {
            摘要文字.text = $"<color={綠}>全程無碰撞，車輛完好</color>";
            return;
        }

        int 輕 = DamageLog.計數(DamageLog.程度.輕微);
        int 中 = DamageLog.計數(DamageLog.程度.中度);
        int 重 = DamageLog.計數(DamageLog.程度.嚴重);

        摘要文字.text =
            $"<color={白}>總碰撞　<b>{DamageLog.總次數}</b> 次</color>\n" +
            $"<color={黃}>輕微 {輕}</color>　<color={橘}>中度 {中}</color>　<color={紅}>嚴重 {重}</color>";
    }

    private void 寫入明細()
    {
        if (明細文字 == null) return;

        if (!DamageLog.有損傷)
        {
            明細文字.text = $"<color={灰}>沒有任何損傷紀錄。</color>";
            return;
        }

        var sb = new StringBuilder();

        if (顯示部位名稱)
        {
            // 依八個點分組：部位 程度 次數
            for (int i = 0; i < DamageLog.部位數量; i++)
            {
                var 部位 = (DamageLog.部位)i;
                int 次數 = DamageLog.部位次數(部位);
                if (次數 == 0) continue;

                DamageLog.部位最嚴重(部位, out var 程度);
                sb.Append($"<color={白}>{部位}</color>　");
                sb.Append($"<color={取色碼(程度)}>{程度}</color>　");
                sb.Append($"<color={灰}>{次數} 次</color>\n");
            }
        }
        else
        {
            // 部位已標在俯視圖上，這裡只依「嚴重程度」分組，避免重複資訊
            foreach (DamageLog.程度 度 in System.Enum.GetValues(typeof(DamageLog.程度)))
            {
                int 次數 = DamageLog.計數(度);
                if (次數 == 0) continue;

                sb.Append($"<color={取色碼(度)}>{度}</color>　");
                sb.Append($"<color={灰}>{次數} 次</color>\n");
            }
        }

        明細文字.text = sb.ToString();
    }
}
