using UnityEngine;
using UnityEngine.Events;

public class ScoreManager : MonoBehaviour
{
    [Header("分數設定")]
    [Tooltip("每項分數")]
    public int scorePerItem = 20;
    [Tooltip("及格分數")]
    public int passScore = 70;

    [Header("結束畫面")]
    public GameObject resultPanel;
    public TMPro.TextMeshProUGUI scoreText;
    public TMPro.TextMeshProUGUI resultText;

    [Header("手動結束按鍵（設 None 停用，讓出 ESC）")]
    public KeyCode finishKey = KeyCode.None;

    [Header("是否接受結算（自由模式關掉）")]
    public bool acceptScoring = true;

    [Header("違規扣分")]
    [Tooltip("本次訓練累積的違規扣分（例如未打方向燈）。結算時會從總分扣除")]
    public int 違規扣分 = 0;

    [Tooltip("扣分後的分數下限，避免變成負分")]
    public int 分數下限 = 0;

    [Tooltip("本次累積的違規次數")]
    public int 違規次數 = 0;

    [Header("違規文字外觀")]
    [Tooltip("違規那一行相對於總分文字的大小（百分比）")]
    [Range(30, 120)] public int 違規文字大小 = 70;

    [Tooltip("違規文字顏色")]
    public Color 違規文字顏色 = new Color(1f, 0.141f, 0.212f);   // #FF2436

    [Tooltip("違規那一行的格式。{0}=次數　{1}=扣分")]
    public string 違規文字格式 = "違規 {0} 次　−{1} 分";

    [Header("事件")]
    public UnityEvent onExamFinished;

    // 0=未完成, 1=成功, -1=失敗
    private int reversePark = 0;
    private int sidePark    = 0;
    private int spiral      = 0;
    private int straight    = 0;
    private int ramp        = 0;

    private bool isFinished = false;

    // 密技：↑↑↓↓←→←→
    private KeyCode[] cheatSequence = new KeyCode[]
    {
        KeyCode.UpArrow, KeyCode.UpArrow,
        KeyCode.DownArrow, KeyCode.DownArrow,
        KeyCode.LeftArrow, KeyCode.RightArrow,
        KeyCode.LeftArrow, KeyCode.RightArrow
    };
    private int cheatIndex = 0;

    private void Start()
    {
        if (resultPanel != null) resultPanel.SetActive(false);
    }

    private void Update()
    {
        // 手動結束（finishKey 設 None 就停用）
        if (!isFinished && finishKey != KeyCode.None && Input.GetKeyDown(finishKey))
            FinishExam();

        if (!isFinished) CheckCheatCode();
    }

    // ── 各項目成功 ──
    public void SetReverseParkSuccess() { reversePark = 1; CheckAllComplete(); }
    public void SetSideParkSuccess()    { sidePark    = 1; CheckAllComplete(); }
    public void SetSpiralSuccess()      { spiral      = 1; CheckAllComplete(); }
    public void SetStraightSuccess()    { straight    = 1; CheckAllComplete(); }
    public void SetRampSuccess()        { ramp        = 1; CheckAllComplete(); }

    // ── 各項目失敗 ──
    public void SetReverseParkFail() { reversePark = -1; }
    public void SetSideParkFail()    { sidePark    = -1; }
    public void SetSpiralFail()      { spiral      = -1; }
    public void SetRampFail()        { ramp        = -1; }

    private void CheckAllComplete()
    {
        bool allSuccess = reversePark == 1 && sidePark == 1 && spiral == 1
                       && straight == 1 && ramp == 1;
        if (allSuccess) FinishExam();
    }

    // ── 全科結算（考試/密技用）──
    public void FinishExam()
    {
        if (!acceptScoring) { Debug.Log("[結算] 自由模式，忽略結算"); return; }
        if (isFinished) return;
        isFinished = true;

        int totalScore = 0;
        if (reversePark == 1) totalScore += scorePerItem;
        if (sidePark    == 1) totalScore += scorePerItem;
        if (spiral      == 1) totalScore += scorePerItem;
        if (straight    == 1) totalScore += scorePerItem;
        if (ramp        == 1) totalScore += scorePerItem;

        bool hasFail = reversePark == -1 || sidePark == -1 || spiral == -1 || ramp == -1;
        bool passed  = !hasFail && totalScore >= passScore;

        ShowResult(totalScore, passed, hasFail);
        Time.timeScale = 0f;
        onExamFinished?.Invoke();
    }

    // ── 訓練模式結算：只算所選項目（百分比）──
    /// <summary>累加違規扣分（訓練模式才會實際影響分數）</summary>
    public void 扣分(int 分數, string 原因 = "")
    {
        if (分數 <= 0) return;
        違規扣分 += 分數;
        違規次數++;
        Debug.Log($"[計分] 違規扣 {分數} 分{(string.IsNullOrEmpty(原因) ? "" : $"（{原因}）")}，累計扣 {違規扣分} 分");
    }

    /// <summary>清空違規扣分（重新開始時呼叫）</summary>
    public void 重設扣分() { 違規扣分 = 0; 違規次數 = 0; }

    public void FinishTraining(int completedCount, int selectedCount)
    {
        if (!acceptScoring) return;
        if (isFinished) return;
        isFinished = true;

        int 基本分 = (selectedCount > 0)
            ? Mathf.RoundToInt(100f * completedCount / selectedCount)
            : 0;

        // 扣除違規分數（未打方向燈等），並夾在下限之上
        int totalScore = Mathf.Max(分數下限, 基本分 - 違規扣分);
        if (違規扣分 > 0)
            Debug.Log($"[計分] 基本分 {基本分} − 違規 {違規扣分} = {totalScore}");

        bool hasFail = completedCount < selectedCount;
        bool passed  = !hasFail;

        ShowResult(totalScore, passed, hasFail);
        Time.timeScale = 0f;
        onExamFinished?.Invoke();
    }

    // ── 重來/回選單：清狀態、解除暫停（不重載場景）──
    public void ResetForRetry()
    {
        重設扣分();
        isFinished  = false;
        reversePark = sidePark = spiral = straight = ramp = 0;
        cheatIndex  = 0;
        Time.timeScale = 1f;
        if (resultPanel != null) resultPanel.SetActive(false);
    }

    private void ShowResult(int score, bool passed, bool hasFail)
    {
        if (resultPanel != null) resultPanel.SetActive(true);

        if (scoreText != null)
        {
            // 有違規扣分時，在總分下方多列一行說明扣了幾分
            if (違規扣分 > 0)
            {
                string 色碼 = ColorUtility.ToHtmlStringRGB(違規文字顏色);
                string 說明 = string.Format(違規文字格式, 違規次數, 違規扣分);
                scoreText.text = $"總分：{score} / 100\n" +
                                 $"<size={違規文字大小}%><color=#{色碼}>{說明}</color></size>";
            }
            else
            {
                scoreText.text = $"總分：{score} / 100";
            }
        }

        if (resultText != null)
        {
            if (hasFail)      { resultText.text = "不及格\n（有項目失敗）"; resultText.color = Color.red; }
            else if (passed)  { resultText.text = "及格！";                 resultText.color = Color.green; }
            else              { resultText.text = "不及格";                 resultText.color = Color.red; }
        }
        Debug.Log($"[結算] 總分：{score}（違規扣 {違規扣分} 分），{(passed ? "及格" : "不及格")}");
    }

    public void RestartExam()
    {
        Time.timeScale = 1f;
        UnityEngine.SceneManagement.SceneManager.LoadScene(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    private void CheckCheatCode()
    {
        if (!Input.anyKeyDown) return;
        if (Input.GetKeyDown(cheatSequence[cheatIndex]))
        {
            cheatIndex++;
            if (cheatIndex >= cheatSequence.Length) { ActivateCheat(); cheatIndex = 0; }
        }
        else cheatIndex = Input.GetKeyDown(cheatSequence[0]) ? 1 : 0;
    }

    private void ActivateCheat()
    {
        Debug.Log("[密技] ↑↑↓↓←→←→ 啟動！直接80分及格結算");
        reversePark = 1; sidePark = 1; spiral = 1; straight = 1; ramp = 0;
        FinishExam();
    }
}