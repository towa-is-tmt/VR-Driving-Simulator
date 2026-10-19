using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>載入畫面的兩種呈現方式</summary>
public enum 載入樣式
{
    圖片覆蓋,    // 蓋滿背景圖（圖上已含模式名稱）＋進度條
    僅進度條,    // 不蓋底圖，讓後面的行駛影片繼續播，只顯示進度條
}

public class TransitionController : MonoBehaviour
{
    [Header("轉場 / 載入 UI")]
    public CanvasGroup canvasGroup;
    public TextMeshProUGUI titleText;

    [Header("背景圖")]
    public Image backgroundImage;

    [Tooltip("訓練模式用的背景圖：有多張時每次隨機挑一張")]
    public Sprite[] 訓練模式背景池;

    [Tooltip("自由模式用的背景圖：固定顯示這一張")]
    public Sprite 自由模式背景;

    [Tooltip("備援用：上面兩項都沒設定時，才從這個舊有的池子隨機挑")]
    public Sprite[] backgroundPool;

    [Header("載入畫面樣式 — 訓練模式")]
    [Tooltip("圖片覆蓋＝蓋滿背景圖；僅進度條＝不蓋底圖，讓行駛影片繼續播")]
    public 載入樣式 訓練模式樣式 = 載入樣式.僅進度條;

    [Tooltip("訓練模式載入時是否顯示進度條")]
    public bool 訓練模式顯示進度條 = true;

    [Header("載入畫面樣式 — 自由模式")]
    public 載入樣式 自由模式樣式 = 載入樣式.圖片覆蓋;

    [Tooltip("自由模式載入時是否顯示進度條")]
    public bool 自由模式顯示進度條 = false;

    [Tooltip("進度條的外框容器（進度條底層）。指定後隱藏進度條時會連底槽一起隱藏；留空則只隱藏 Fill 本身")]
    public GameObject 進度條容器;

    [Tooltip("轉場面板自己那層底色 Image（TransitionPanel 上的 Image）。僅進度條模式會關掉它，否則會把影片壓暗")]
    public Image 面板底色;

    [Tooltip("僅進度條模式時，連小標語也一起隱藏")]
    public bool 僅進度條時隱藏標語 = true;

    [Header("標題文字")]
    [Tooltip("背景圖上已經印好「訓練模式／自由模式」字樣時勾選，會自動隱藏標題文字，避免文字重複")]
    public bool 隱藏標題文字 = true;

    [Header("載入進度條")]
    public Image progressBar;
    public TextMeshProUGUI progressText;

    [Header("小標語（多句隨機一句）")]
    public TextMeshProUGUI tipText;
    [TextArea] public string[] tips;

    [Header("背景音樂（進入模式時隨載入進度淡出）")]
    [Tooltip("選單的背景音樂 AudioSource。載入進度 0→100% 時音量同步降到 0，進度跑完音樂剛好結束")]
    public AudioSource 背景音樂;

    [Tooltip("取消勾選＝不淡出，音樂會在切場景瞬間直接中斷")]
    public bool 隨進度淡出音樂 = true;

    [Header("時間設定")]
    public float fadeTime  = 0.4f;
    public float loadTime  = 2f;    // 最短顯示時間（避免載太快閃一下）
    public float holdAtFull = 0.4f;

    void Awake()
    {
        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.blocksRaycasts = false;
            canvasGroup.interactable = false;
        }
        if (progressBar != null) progressBar.fillAmount = 0f;
    }

    // ── 原本的同場景轉場（保留給 car 場景重來用）──
    public void Play(string title, Action onMidpoint = null, Action onDone = null)
        => StartCoroutine(PlayRoutine(title, onMidpoint, onDone));

    private IEnumerator PlayRoutine(string title, Action onMidpoint, Action onDone)
    {
        PrepUI(title);
        if (canvasGroup != null) { canvasGroup.alpha = 1f; canvasGroup.blocksRaycasts = true; }
        onMidpoint?.Invoke();
        yield return RunProgress(loadTime);
        if (holdAtFull > 0f) yield return new WaitForSecondsRealtime(holdAtFull);
        yield return Fade(1f, 0f, fadeTime);
        if (canvasGroup != null) canvasGroup.blocksRaycasts = false;
        onDone?.Invoke();
    }

    // ── 車庫→駕駛：播載入畫面 + 非同步載入場景 ──
    public void PlayThenLoad(string title, string sceneName)
        => StartCoroutine(PlayThenLoadRoutine(title, sceneName));

    private IEnumerator PlayThenLoadRoutine(string title, string sceneName)
    {
        PrepUI(title);

        // 立刻蓋滿（不淡入，避免露出車庫畫面）
        if (canvasGroup != null) { canvasGroup.alpha = 1f; canvasGroup.blocksRaycasts = true; }

        // 非同步載入，先不啟用新場景
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName);
        op.allowSceneActivation = false;

        // 記住音樂原始音量，之後依進度等比例降下來
        float 原始音量 = (背景音樂 != null) ? 背景音樂.volume : 0f;

        float t = 0f;
        while (true)
        {
            t += Time.unscaledDeltaTime;
            float real  = Mathf.Clamp01(op.progress / 0.9f);   // 真實載入(卡在0.9等啟用)
            float timed = Mathf.Clamp01(t / loadTime);          // 最短顯示時間
            float shown = Mathf.Min(real, timed);
            if (progressBar != null)  progressBar.fillAmount = shown;
            if (progressText != null) progressText.text = Mathf.RoundToInt(shown * 100f) + "%";

            // 音量隨進度淡出：進度 0% 時原音量、100% 時為 0
            if (隨進度淡出音樂 && 背景音樂 != null)
                背景音樂.volume = 原始音量 * (1f - shown);

            if (real >= 1f && timed >= 1f) break;               // 載完且至少顯示 loadTime
            yield return null;
        }

        if (progressBar != null)  progressBar.fillAmount = 1f;
        if (progressText != null) progressText.text = "100%";

        // 進度 100% ＝ 音樂剛好結束
        if (隨進度淡出音樂 && 背景音樂 != null)
        {
            背景音樂.volume = 0f;
            背景音樂.Stop();
        }

        if (holdAtFull > 0f) yield return new WaitForSecondsRealtime(holdAtFull);

        op.allowSceneActivation = true;   // 啟用新場景（車庫連同載入畫面一起卸載＝切到駕駛）
    }

    // ── 共用 ──
    // ── 依目前模式決定背景圖 ──────────────────────────────
    // 訓練模式：從「訓練模式背景池」隨機挑一張
    // 自由模式：固定用「自由模式背景」
    // 兩者都沒設定時，退回舊的 backgroundPool（保持相容，不會突然沒背景）
    private Sprite 依模式挑背景()
    {
        switch (TrainingSession.gameMode)
        {
            case TrainingSession.Mode.Training:
                if (訓練模式背景池 != null && 訓練模式背景池.Length > 0)
                    return 訓練模式背景池[UnityEngine.Random.Range(0, 訓練模式背景池.Length)];
                break;

            case TrainingSession.Mode.Free:
                if (自由模式背景 != null)
                    return 自由模式背景;
                break;
        }

        // 備援：沿用舊的隨機池
        if (backgroundPool != null && backgroundPool.Length > 0)
            return backgroundPool[UnityEngine.Random.Range(0, backgroundPool.Length)];

        return null;
    }

    private void PrepUI(string title)
    {
        // 依目前模式決定要用哪一組設定
        bool 是自由模式 = (TrainingSession.gameMode == TrainingSession.Mode.Free);
        載入樣式 本次樣式 = 是自由模式 ? 自由模式樣式 : 訓練模式樣式;
        bool 要顯示進度條 = 是自由模式 ? 自由模式顯示進度條 : 訓練模式顯示進度條;

        bool 只顯示進度條 = (本次樣式 == 載入樣式.僅進度條);

        // ── 進度條顯示與否 ──
        if (進度條容器 != null)
        {
            進度條容器.SetActive(要顯示進度條);
        }
        else
        {
            if (progressBar != null) progressBar.gameObject.SetActive(要顯示進度條);
        }
        if (progressText != null) progressText.gameObject.SetActive(要顯示進度條);

        // ── 背景圖 ──
        if (backgroundImage != null)
        {
            if (只顯示進度條)
            {
                // 不蓋底圖，讓車庫場景的行駛影片繼續看得到
                backgroundImage.enabled = false;
            }
            else
            {
                backgroundImage.enabled = true;
                Sprite 選中的背景 = 依模式挑背景();
                if (選中的背景 != null) backgroundImage.sprite = 選中的背景;
            }
        }

        // ── 面板本身那層底色（不關掉的話影片會被壓暗）──
        if (面板底色 != null)
            面板底色.enabled = !只顯示進度條;

        // ── 標題文字 ──
        if (titleText != null)
        {
            bool 要顯示標題 = !隱藏標題文字 && !只顯示進度條;
            titleText.gameObject.SetActive(要顯示標題);
            if (要顯示標題) titleText.text = title;
        }

        if (tipText != null)
        {
            bool 要顯示標語 = !(只顯示進度條 && 僅進度條時隱藏標語);
            tipText.gameObject.SetActive(要顯示標語);
            if (要顯示標語 && tips != null && tips.Length > 0)
                tipText.text = tips[UnityEngine.Random.Range(0, tips.Length)];
        }
        if (progressBar != null)  progressBar.fillAmount = 0f;
        if (progressText != null) progressText.text = "0%";
    }

    private IEnumerator RunProgress(float dur)
    {
        if (dur <= 0f) dur = 0.01f;
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(t / dur);
            if (progressBar != null)  progressBar.fillAmount = p;
            if (progressText != null) progressText.text = Mathf.RoundToInt(p * 100f) + "%";
            yield return null;
        }
        if (progressBar != null)  progressBar.fillAmount = 1f;
        if (progressText != null) progressText.text = "100%";
    }

    private IEnumerator Fade(float from, float to, float dur)
    {
        if (canvasGroup == null) yield break;
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(from, to, t / dur);
            yield return null;
        }
        canvasGroup.alpha = to;
    }
}