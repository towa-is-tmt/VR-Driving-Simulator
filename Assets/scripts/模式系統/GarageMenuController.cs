using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GarageMenuController : MonoBehaviour
{
    [Header("駕駛場景名稱")]
    public string drivingSceneName = "car";

    [Header("UI 面板")]
    public GameObject modeMenuPanel;
    public GameObject itemSelectPanel;

    // ─── 新增：攝影機瞬移設定 ───
    [Header("攝影機瞬移設定")]
    [Tooltip("主要攝影機（若留空會自動抓取 Tag 為 MainCamera 的攝影機）")]
    public Camera targetCamera;

    [Tooltip("隱藏 UI 後，攝影機要瞬移到的目標位置物件（請在 Scene 建立一個空物件作為 Target）")]
    public Transform cameraTargetTransform;
    // ──────────────────────────

    [Header("項目選擇器")]
    public TrainingItemSelector itemSelector;

    [Header("載入畫面（車庫場景裡的轉場）")]
    public TransitionController transition;

    [Header("鐵捲門")]
    [Tooltip("訓練模式進場是否播鐵捲門開門動畫")]
    public bool 訓練模式播鐵捲門 = false;

    [Tooltip("自由模式進場是否播鐵捲門開門動畫")]
    public bool 自由模式播鐵捲門 = true;

    [Tooltip("播鐵捲門動畫前要先隱藏的 UI（例如全螢幕的背景影片）。\n" +
             "選單 Canvas 是 Screen Space - Overlay，會蓋住 3D 車庫，不收起來就看不到開門動畫。\n" +
             "版本二（不播動畫）時不會隱藏，影片會繼續播。")]
    public GameObject[] 開門時隱藏的UI;

    [Header("鐵捲門音效")]
    [Tooltip("播放開門聲的 AudioSource。留空則不播")]
    public AudioSource 開門音源;

    [Tooltip("開門音效檔。留空則直接播 AudioSource 上原本設定的 Clip")]
    public AudioClip 開門音效;

    [Range(0f, 1f)] public float 開門音量 = 0.8f;

    public Animator shutterAnimator;         // 拖鐵捲門物件的 Animator
    public string shutterTrigger = "opens"; // 隊友 controller 的觸發參數名
    [Tooltip("開門動畫長度（秒）；開完才進載入畫面")]
    public float shutterOpenTime = 4.5f;

    private bool leaving = false;             // 防止重複觸發

    void Start()
    {
        Time.timeScale = 1f;                  // 保險：從 car 回來時解除暫停
        if (itemSelectPanel != null) itemSelectPanel.SetActive(false);
        if (modeMenuPanel != null) modeMenuPanel.SetActive(true);
    }

    public void SelectTrainingMode()
    {
        if (modeMenuPanel != null) modeMenuPanel.SetActive(false);
        if (itemSelectPanel != null) itemSelectPanel.SetActive(true);
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
    }

    public void SelectFreeMode()
    {
        TrainingSession.Clear();
        TrainingSession.gameMode = TrainingSession.Mode.Free;
        GoToDriving("自由模式");
    }

    public void ConfirmTrainingItems()
    {
        var sel = (itemSelector != null) ? itemSelector.GetSelected() : new List<string>();
        if (sel.Count == 0)
        {
            Debug.LogWarning("[車庫] 沒選任何訓練項目！");
            return;
        }
        TrainingSession.Clear();
        TrainingSession.gameMode = TrainingSession.Mode.Training;
        TrainingSession.selectedItems = sel;
        GoToDriving("訓練模式");
    }

    public void BackToModeMenu()
    {
        if (itemSelectPanel != null) itemSelectPanel.SetActive(false);
        if (modeMenuPanel != null) modeMenuPanel.SetActive(true);
    }

    private void GoToDriving(string title)
    {
        if (leaving) return;
        leaving = true;
        Debug.Log($"[車庫] 開門 → 進駕駛，模式={TrainingSession.gameMode}");
        StartCoroutine(OpenThenLoad(title));
    }

    private IEnumerator OpenThenLoad(string title)
    {
        // 收掉選單，讓玩家看門開
        if (modeMenuPanel != null) modeMenuPanel.SetActive(false);
        if (itemSelectPanel != null) itemSelectPanel.SetActive(false);

        // ─── 瞬移攝影機到指定位置與角度 ───
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera != null && cameraTargetTransform != null
            && targetCamera.transform.parent != null)
        {
            targetCamera.transform.parent.position = cameraTargetTransform.position;
            targetCamera.transform.parent.rotation = cameraTargetTransform.rotation;
        }

        // 依這次選的模式決定要不要播鐵捲門
        bool 是自由模式 = (TrainingSession.gameMode == TrainingSession.Mode.Free);
        bool 播放鐵捲門動畫 = 是自由模式 ? 自由模式播鐵捲門 : 訓練模式播鐵捲門;

        if (播放鐵捲門動畫)
        {
            // ── 版本一：先播鐵捲門開門，再蓋上載入畫面 ──

            // 先收掉會擋住 3D 車庫的 UI（Overlay Canvas 永遠畫在 3D 之上，
            // 不隱藏的話背景影片會把整個開門動畫蓋住）
            if (開門時隱藏的UI != null)
            {
                foreach (var go in 開門時隱藏的UI)
                    if (go != null) go.SetActive(false);
            }

            if (shutterAnimator != null)
                shutterAnimator.SetTrigger(shutterTrigger);

            // 動畫沒有 Animation Event，所以在這裡同步播開門音效
            播放開門音效();

            // 等門開完
            yield return new WaitForSecondsRealtime(shutterOpenTime);
        }
        else
        {
            // ── 版本二：不播鐵捲門，直接進載入畫面 ──
            //   背景的行駛影片會繼續播放，載入畫面只顯示進度條。
            //   （TransitionController 的「樣式」請一併設成 僅進度條）
            yield return null;
        }

        // 進載入畫面 + 非同步載入
        if (transition != null)
            transition.PlayThenLoad(title, drivingSceneName);
        else
            SceneManager.LoadScene(drivingSceneName);
    }

    /// <summary>播放鐵捲門開啟音效（與動畫同時觸發）</summary>
    private void 播放開門音效()
    {
        if (開門音源 == null) return;

        if (開門音效 != null)
            開門音源.PlayOneShot(開門音效, 開門音量);
        else
        {
            開門音源.volume = 開門音量;
            開門音源.Play();
        }
    }
}