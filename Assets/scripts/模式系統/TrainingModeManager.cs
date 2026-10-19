using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using TMPro;

public class TrainingModeManager : MonoBehaviour
{
    public enum GameMode { None, Training, Free }

    [Header("目前模式")]
    public GameMode currentMode = GameMode.None;

    [Header("回車庫用的場景名稱")]
    public string garageSceneName = "車庫";

    [Header("UI 面板")]
    public GameObject modeMenuPanel;
    public GameObject itemSelectPanel;
    public GameObject resultPanel;
    public TextMeshProUGUI resultText;
    public GameObject exitConfirmPanel;

    [Header("結算明細文字（各項目成功/失敗，可留空）")]
    public TextMeshProUGUI resultDetailText;

    [Header("項目選擇器")]
    public TrainingItemSelector itemSelector;

    [Header("轉場")]
    public TransitionController transition;

    [Header("導航選單按鈕限制")]
    public TrainingButtonGate navButtonGate;

    [Header("所有訓練項目")]
    public List<TrainingItemController> allItems = new List<TrainingItemController>();

    [Header("停車類型對應的項目名稱（要和 itemName 一致）")]
    public string sideParkItemName    = "路邊側停";
    public string reverseParkItemName = "倒車入庫";

    [Header("結算（訓練完自動跳分數；不接則用下方簡易面板）")]
    public ScoreManager scoreManager;

    [Header("訓練清單（重來時清燈號＋暗下未選）")]
    public ExamChecklist examChecklist;

    [Header("重新開始的出生點（多個 = 隨機選一個）")]
    public Transform[] spawnPoints;
    public Transform carRoot;
    public Rigidbody  carRigidbody;

    [Header("事件（之後可接結算）")]
    public UnityEvent onTrainingComplete;

    [Header("除錯：測試熱鍵（展示前取消勾選）")]
    public bool debugHotkeys = false;

    [Header("結算延遲")]
    [Tooltip("訓練完成後隔幾秒才跳出結算畫面。\n" +
             "留一點時間讓「等待評分中」等提示先收掉，畫面才不會擠在一起。設 0 則立刻結算")]
    public float 結算延遲秒數 = 2f;

    [Header("狀態（Debug）")]
    public List<string> selectedItems = new List<string>();
    public bool inSession = false;

    private readonly List<TrainingItemController> activeItems = new List<TrainingItemController>();

    private void Freeze() => Time.timeScale = 0f;
    private void Resume() => Time.timeScale = 1f;

    private void StopCar()
    {
        if (carRigidbody != null)
        {
            carRigidbody.velocity        = Vector3.zero;
            carRigidbody.angularVelocity = Vector3.zero;
        }
    }

    void Start()
    {
        if (itemSelectPanel != null)  itemSelectPanel.SetActive(false);
        if (resultPanel != null)      resultPanel.SetActive(false);
        if (exitConfirmPanel != null) exitConfirmPanel.SetActive(false);
        if (modeMenuPanel != null)    modeMenuPanel.SetActive(false);

        // 從車庫帶來的選擇 → 自動開始對應模式
        switch (TrainingSession.gameMode)
        {
            case TrainingSession.Mode.Training:
                currentMode   = GameMode.Training;
                selectedItems = new List<string>(TrainingSession.selectedItems);
                StartTraining(selectedItems);
                break;

            case TrainingSession.Mode.Free:
                currentMode = GameMode.Free;
                StartFree();
                break;

            default:
                // 沒有車庫資料（直接按 Play 測試）。
                //   有備援選單（車庫場景）→ 顯示選單並凍結，等玩家選。
                //   沒有備援選單（駕駛場景）→ 直接當自由模式跑，而且「不搬車」，
                //   讓開發時可以把車擺在想測的位置按 Play 就直接測危險因子。
                //   舊行為是無條件 Freeze()，在沒有選單的場景會變成永遠解不開的凍結。
                if (modeMenuPanel != null)
                {
                    modeMenuPanel.SetActive(true);
                    Freeze();
                }
                else
                {
                    currentMode = GameMode.Free;
                    StartFreeInternal(respawn: false);
                    Debug.Log("[自由] 直接播場景測試（未經車庫，車輛保持原位）");
                }
                break;
        }
    }

    // ── 車庫端才用得到的選單方法（保留備援，car 場景通常不呼叫）──
    public void SelectTrainingMode()
    {
        currentMode = GameMode.Training;
        if (modeMenuPanel != null)   modeMenuPanel.SetActive(false);
        if (itemSelectPanel != null) itemSelectPanel.SetActive(true);
        Freeze();
    }

    public void SelectFreeMode()
    {
        currentMode = GameMode.Free;
        if (modeMenuPanel != null) modeMenuPanel.SetActive(false);
        EnterMode("自由模式", StartFree);
    }

    public void ConfirmTrainingItems()
    {
        if (itemSelector != null) selectedItems = itemSelector.GetSelected();
        if (selectedItems.Count == 0)
        {
            Debug.LogWarning("[模式] 沒有選任何訓練項目！");
            return;
        }
        if (itemSelectPanel != null) itemSelectPanel.SetActive(false);
        EnterMode("訓練模式", () => StartTraining(selectedItems));
    }

    public void BackToModeMenu()
    {
        if (itemSelectPanel != null) itemSelectPanel.SetActive(false);
        if (modeMenuPanel != null)   modeMenuPanel.SetActive(true);
        Freeze();
    }

    private void EnterMode(string title, Action enterAction)
    {
        if (transition != null) transition.Play(title, onMidpoint: enterAction);
        else enterAction?.Invoke();
    }

    // ── 進入模式（每次都重生到隨機點）──
    private void StartTraining(List<string> names)
    {
        if (scoreManager != null) { scoreManager.acceptScoring = true; scoreManager.ResetForRetry(); }
        StopCar();
        RespawnCar();
        SetupItems(names);
        inSession = true;
        if (navButtonGate != null) navButtonGate.SetAllowed(names);
        Resume();
        Debug.Log($"[訓練] 開始，需完成：{string.Join("、", names)}");
    }

    // EnterMode 需要一個無參數的 Action，所以外層維持零參數，實作放到 Internal。
    private void StartFree() => StartFreeInternal(respawn: true);

    private void StartFreeInternal(bool respawn)
    {
        if (scoreManager != null) { scoreManager.acceptScoring = false; scoreManager.ResetForRetry(); }
        if (respawn)
        {
            StopCar();
            RespawnCar();
        }
        var all = new List<string>();
        foreach (var it in allItems) if (it != null) all.Add(it.itemName);
        SetupItems(all);
        inSession = true;
        if (navButtonGate != null) navButtonGate.AllowAll();
        Resume();
        Debug.Log("[自由] 開始");
    }

    private void SetupItems(List<string> names)
    {
        if (examChecklist != null) examChecklist.ResetChecklist();

        activeItems.Clear();
        foreach (var it in allItems)
        {
            if (it == null) continue;
            it.onCompleted -= OnItemCompleted;
            it.onFailed    -= OnItemFailed;
            it.ResetItem();

            if (names.Contains(it.itemName))
            {
                it.Activate();
                it.onCompleted += OnItemCompleted;
                it.onFailed    += OnItemFailed;
                activeItems.Add(it);
            }
            else it.Deactivate();
        }

        // 偵測器：先全關，再只開啟所選
        foreach (var it in allItems)    if (it != null) it.SetDetectorsEnabled(false);
        foreach (var it in activeItems)                 it.SetDetectorsEnabled(true);

        // 停車兩種共用一個 ParkingDetector → 用類型開關分開
        ParkingDetector pd = (carRoot != null) ? carRoot.GetComponentInChildren<ParkingDetector>() : null;
        if (pd == null) pd = FindObjectOfType<ParkingDetector>();
        if (pd != null)
        {
            pd.detectSide    = names.Contains(sideParkItemName);
            pd.detectReverse = names.Contains(reverseParkItemName);
        }

        // 訓練清單：只讓所選項目正常亮，其餘變暗
        if (examChecklist != null) examChecklist.SetActiveItems(names);
    }

    private void OnItemCompleted(TrainingItemController item)
    {
        if (!inSession || currentMode != GameMode.Training) return;
        CheckSessionEnd();
    }

    private void OnItemFailed(TrainingItemController item)
    {
        if (!inSession || currentMode != GameMode.Training) return;
        CheckSessionEnd();
    }

    private void CheckSessionEnd()
    {
        foreach (var it in activeItems) if (!it.isSettled) return;

        bool passed = true;
        foreach (var it in activeItems) if (!it.isComplete) { passed = false; break; }
        EndTraining(passed);
    }

    private void EndTraining(bool passed)
    {
        inSession = false;
        onTrainingComplete?.Invoke();

        if (resultDetailText != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var it in activeItems)
            {
                if (it == null) continue;
                if (it.isComplete)
                    sb.AppendLine($"<color=#37E0A0>完成　{it.itemName}</color>");
                else if (it.isSettled)
                    sb.AppendLine($"<color=#FF5C6C>失敗　{it.itemName}</color>");
                else
                    sb.AppendLine($"<color=#7E93AD>未完成　{it.itemName}</color>");
            }
            resultDetailText.text = sb.ToString();
        }

        Debug.Log(passed ? "[訓練] 全部完成且通過" : "[訓練] 全部結束但有失敗 → 未通過");

        // 延遲一下再結算，讓場上的提示（例如「等待評分中」）有時間收掉
        if (結算延遲秒數 > 0f) StartCoroutine(延遲結算(passed));
        else                   顯示結算(passed);
    }

    private IEnumerator 延遲結算(bool passed)
    {
        // 用 Realtime：此時尚未凍結，但保險起見不受 timeScale 影響
        yield return new WaitForSecondsRealtime(結算延遲秒數);
        顯示結算(passed);
    }

    /// <summary>實際跳出結算畫面並凍結遊戲</summary>
    private void 顯示結算(bool passed)
    {
        if (scoreManager != null)
        {
            int done = 0;
            foreach (var it in activeItems) if (it.isComplete) done++;
            scoreManager.FinishTraining(done, activeItems.Count);
        }
        else
        {
            if (resultText != null)  resultText.text = passed ? "訓練完成！" : "未通過";
            if (resultPanel != null) resultPanel.SetActive(true);
            Freeze();
        }
    }

    // ── 手動結束訓練（只在訓練模式作用）──
    public void EndSessionManually()
    {
        if (!inSession) return;
        if (currentMode != GameMode.Training)
        {
            Debug.Log("[模式] 自由模式不結算，忽略手動結束");
            return;
        }

        bool passed = true;
        foreach (var it in activeItems)
            if (!it.isComplete) { passed = false; break; }

        Debug.Log("[訓練] 手動結束 → 結算目前進度");
        EndTraining(passed);
    }

    // ── 把車丟到出生點（多個則隨機）──
    private void RespawnCar()
    {
        if (carRoot == null || spawnPoints == null || spawnPoints.Length == 0)
        {
            Debug.LogWarning("[出生] 未設定 carRoot 或 spawnPoints，車不會被搬動");
            return;
        }

        Transform p = spawnPoints[UnityEngine.Random.Range(0, spawnPoints.Length)];
        StopCar();
        carRoot.SetPositionAndRotation(p.position, p.rotation);

        var pd = carRoot.GetComponentInChildren<ParkingDetector>();
        if (pd != null) pd.DisarmUntilMoved();

        Debug.Log($"[出生] 車移到出生點：{p.name}");
    }

    // ── 暫離 / 確認結算 / 取消 ──
    public void RequestExit()
    {
        string panelInfo = (exitConfirmPanel == null) ? "null" : exitConfirmPanel.name;
        Debug.Log($"[中離] inSession={inSession}, exitConfirmPanel={panelInfo}");

        if (!inSession) { ReturnToModeMenu(); return; }
        if (exitConfirmPanel != null) exitConfirmPanel.SetActive(true);
        Freeze();
    }

    public void ConfirmExit()
    {
        if (exitConfirmPanel != null) exitConfirmPanel.SetActive(false);

        if (currentMode == GameMode.Training)
            EndSessionManually();
        else
            ReturnToModeMenu();
    }

    public void CancelExit()
    {
        if (exitConfirmPanel != null) exitConfirmPanel.SetActive(false);
        Resume();
    }

    public void RestartTraining()
    {
        Debug.Log($"[重來] mode={currentMode}, 選了{selectedItems.Count}項");

        if (exitConfirmPanel != null) exitConfirmPanel.SetActive(false);
        if (resultPanel != null)      resultPanel.SetActive(false);
        if (scoreManager != null)     scoreManager.ResetForRetry();

        if (currentMode == GameMode.Free)
            EnterMode("自由模式", StartFree);
        else if (currentMode == GameMode.Training && selectedItems.Count > 0)
            EnterMode("訓練模式", () => StartTraining(selectedItems));
        else
            ReturnToModeMenu();
    }

    // ── 返回車庫場景 ──
    public void ReturnToModeMenu()
    {
        foreach (var it in allItems)
        {
            if (it == null) continue;
            it.onCompleted -= OnItemCompleted;
            it.onFailed    -= OnItemFailed;
            it.ResetItem();
            it.Deactivate();
        }
        activeItems.Clear();
        inSession = false;

        TrainingSession.Clear();       // 清掉這次的選擇
        Time.timeScale = 1f;           // 解除暫停再換場景
        Debug.Log("[模式] 回車庫場景");
        SceneManager.LoadScene(garageSceneName);
    }

    public void RequestCancel(TrainingItemController item)
    {
        if (currentMode == GameMode.Free) item.Cancel();
        else Debug.Log("[訓練] 訓練進行中不可取消（只能暫離結算）");
    }

    // ── ESC 暫離 + 測試熱鍵 ──
    private void Update()
    {
        if (inSession && Input.GetKeyDown(KeyCode.Escape))
        {
            if (exitConfirmPanel != null && exitConfirmPanel.activeSelf)
                CancelExit();
            else
                RequestExit();
            return;
        }

        if (!debugHotkeys || !inSession) return;
        if (Input.GetKeyDown(KeyCode.F1) && allItems.Count > 0) allItems[0].Complete();
        if (Input.GetKeyDown(KeyCode.F2) && allItems.Count > 1) allItems[1].Complete();
        if (Input.GetKeyDown(KeyCode.F3) && allItems.Count > 2) allItems[2].Complete();
        if (Input.GetKeyDown(KeyCode.F4) && allItems.Count > 3) allItems[3].Complete();
        if (Input.GetKeyDown(KeyCode.F5) && allItems.Count > 4) allItems[4].Complete();
        if (Input.GetKeyDown(KeyCode.F6) && allItems.Count > 0) allItems[0].Fail();
        if (Input.GetKeyDown(KeyCode.F12)) RequestExit();
    }
}