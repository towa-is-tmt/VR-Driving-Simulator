using System.Collections;
using UnityEngine;

/// <summary>
/// 開場規則說明 — 進入駕駛場景後先蓋一層規則說明，玩家確認後才開始開車。
///
/// 運作方式：
///   進 car 場景 → 等一影格（讓 TrainingModeManager 先跑完 Start 並開始模式）
///   → 顯示面板並凍結遊戲（Time.timeScale = 0，車是 Rigidbody 驅動所以會停住）
///   → 玩家按「開始」→ 關閉面板並恢復遊戲
///
/// 為什麼要等一影格：
///   TrainingModeManager.Start() 內的 StartTraining() 結尾會呼叫 Resume()（timeScale = 1）。
///   腳本執行順序不保證，若同一影格內凍結，可能被它覆蓋掉。
///
/// 掛法：掛在 car 場景的常駐物件上（例如 TrainingModeManager 同一個物件）。
/// </summary>
public class StartupRulesPanel : MonoBehaviour
{
    [Header("面板")]
    [Tooltip("規則說明面板（含遮罩、說明文字、開始按鈕）")]
    public GameObject 面板;

    [Header("顯示條件")]
    [Tooltip("只有訓練模式才顯示；取消勾選則自由模式也會顯示")]
    public bool 只在訓練模式顯示 = true;

    [Tooltip("每次進入場景都顯示。取消＝同一次遊戲執行只顯示第一次")]
    public bool 每次都顯示 = true;

    [Header("關閉方式")]
    [Tooltip("鍵盤關閉鍵，設 None 則只能用按鈕關閉")]
    public KeyCode 關閉按鍵 = KeyCode.None;

    [Header("除錯")]
    public bool 除錯訊息 = false;

    private static bool 這次執行已顯示過 = false;
    private bool 顯示中 = false;

    private void Start()
    {
        if (面板 != null) 面板.SetActive(false);

        if (!應該顯示())
        {
            if (除錯訊息) Debug.Log("[開場規則] 條件不符，不顯示。");
            return;
        }

        StartCoroutine(延後顯示());
    }

    private bool 應該顯示()
    {
        if (面板 == null) return false;

        if (只在訓練模式顯示 &&
            TrainingSession.gameMode != TrainingSession.Mode.Training) return false;

        if (!每次都顯示 && 這次執行已顯示過) return false;

        return true;
    }

    private IEnumerator 延後顯示()
    {
        // 等一影格，確保 TrainingModeManager.Start() 已跑完（它結尾會把 timeScale 設回 1）
        yield return null;

        面板.SetActive(true);
        Time.timeScale = 0f;
        顯示中 = true;
        這次執行已顯示過 = true;

        if (除錯訊息) Debug.Log("[開場規則] 已顯示，遊戲凍結。");
    }

    private void Update()
    {
        if (!顯示中) return;
        if (關閉按鍵 == KeyCode.None) return;

        if (Input.GetKeyDown(關閉按鍵)) 開始遊戲();
    }

    /// <summary>接到面板上的「開始」按鈕 OnClick</summary>
    public void 開始遊戲()
    {
        if (!顯示中) return;

        if (面板 != null) 面板.SetActive(false);
        Time.timeScale = 1f;
        顯示中 = false;

        if (除錯訊息) Debug.Log("[開場規則] 已關閉，遊戲開始。");
    }

    private void OnDisable()
    {
        // 防呆：物件被關掉時別把遊戲卡在凍結狀態
        if (顯示中)
        {
            Time.timeScale = 1f;
            顯示中 = false;
        }
    }
}
