using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ExamChecklist : MonoBehaviour
{
    [Header("按鍵設定")]
    [Tooltip("顯示/隱藏清單的按鍵")]
    public KeyCode toggleKey = KeyCode.Tab;

    [Header("顯示設定")]
    [Tooltip("勾選＝清單固定顯示在畫面上、不受按鍵開關；取消＝維持原本按鍵切換")]
    public bool 固定顯示 = true;

    [Header("清單面板")]
    public GameObject checklistPanel;

    [Header("勾選圖示")]
    public Sprite checkedSprite;
    public Sprite uncheckedSprite;

    [Header("各項目勾選圖示")]
    [Tooltip("倒車入庫")]
    public Image checkReversePark;
    [Tooltip("路邊側停")]
    public Image checkSidePark;
    [Tooltip("S型螺旋坡道")]
    public Image checkSpiral;
    [Tooltip("直線行駛")]
    public Image checkStraight;
    [Tooltip("坡道定點停車")]
    public Image checkRamp;

    [System.Serializable]
    public class 清單項目
    {
        public string itemName;    // 要和訓練項目 itemName 一致
        public CanvasGroup row;     // 該項目「整列」的 CanvasGroup
    }

    [Header("清單各列（暗下未選項目用）")]
    public 清單項目[] rows;
    [Range(0f, 1f)] public float 未選透明度 = 0.35f;

    [Header("音效")]
    [Tooltip("成功音效")]
    public AudioClip successSound;
    [Tooltip("失敗音效")]
    public AudioClip failSound;

    private AudioSource audioSource;
    private bool isVisible = false;

    // 顏色設定
    private Color completedColor   = new Color(0.2f, 0.8f, 0.2f, 1f); // 綠色
    private Color failedColor      = new Color(0.8f, 0.2f, 0.2f, 1f); // 紅色
    private Color uncompletedColor = new Color(0.5f, 0.5f, 0.5f, 1f); // 灰色

    private void Start()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();

        // 固定顯示＝開場就顯示；否則沿用原本預設隱藏
        isVisible = 固定顯示;
        checklistPanel.SetActive(isVisible);

        SetUnchecked(checkReversePark);
        SetUnchecked(checkSidePark);
        SetUnchecked(checkSpiral);
        SetUnchecked(checkStraight);
        SetUnchecked(checkRamp);
    }

    private void Update()
    {
        // 固定顯示時忽略按鍵開關，清單不會被隱藏
        if (固定顯示) return;

        if (Input.GetKeyDown(toggleKey))
        {
            isVisible = !isVisible;
            checklistPanel.SetActive(isVisible);
        }
    }

    // ── 成功呼叫 ──
    public void CompleteReversePark() { SetChecked(checkReversePark); PlaySound(successSound); Debug.Log("[清單] 倒車入庫 ✅"); }
    public void CompleteSidePark()    { SetChecked(checkSidePark);    PlaySound(successSound); Debug.Log("[清單] 路邊側停 ✅"); }
    public void CompleteSpiral()      { SetChecked(checkSpiral);      PlaySound(successSound); Debug.Log("[清單] S型螺旋坡道 ✅"); }
    public void CompleteStraight()    { SetChecked(checkStraight);    PlaySound(successSound); Debug.Log("[清單] 直線行駛 ✅"); }
    public void CompleteRamp()        { SetChecked(checkRamp);        PlaySound(successSound); Debug.Log("[清單] 坡道定點停車 ✅"); }

    // ── 失敗呼叫 ──
    public void FailReversePark() { SetFailed(checkReversePark); PlaySound(failSound); Debug.Log("[清單] 倒車入庫 ❌"); }
    public void FailSidePark()    { SetFailed(checkSidePark);    PlaySound(failSound); Debug.Log("[清單] 路邊側停 ❌"); }
    public void FailSpiral()      { SetFailed(checkSpiral);      PlaySound(failSound); Debug.Log("[清單] S型螺旋坡道 ❌"); }
    public void FailRamp()        { SetFailed(checkRamp);        PlaySound(failSound); Debug.Log("[清單] 坡道定點停車 ❌"); }

    // ── 重置所有燈號為未完成（訓練重來時呼叫）──
    public void ResetChecklist()
    {
        SetUnchecked(checkReversePark);
        SetUnchecked(checkSidePark);
        SetUnchecked(checkSpiral);
        SetUnchecked(checkStraight);
        SetUnchecked(checkRamp);
        Debug.Log("[清單] 已重置為未完成");
    }

    // ── 只讓所選項目正常亮，其餘變暗（訓練用）──
    public void SetActiveItems(List<string> names)
    {
        if (rows == null) return;
        foreach (var r in rows)
        {
            if (r == null || r.row == null) continue;
            bool on = names != null && names.Contains(r.itemName);
            r.row.alpha = on ? 1f : 未選透明度;
        }
    }

    // ── 全部恢復正常亮（自由模式 / 回選單用）──
    public void ShowAllItems()
    {
        if (rows == null) return;
        foreach (var r in rows)
            if (r != null && r.row != null) r.row.alpha = 1f;
    }

    // ── 私有輔助 ──
    private void SetChecked(Image img)
    {
        if (img == null) return;
        img.sprite = checkedSprite;
        img.color  = completedColor;
    }

    private void SetFailed(Image img)
    {
        if (img == null) return;
        img.sprite = uncheckedSprite;
        img.color  = failedColor;
    }

    private void SetUnchecked(Image img)
    {
        if (img == null) return;
        img.sprite = uncheckedSprite;
        img.color  = uncompletedColor;
    }

    private void PlaySound(AudioClip clip)
    {
        if (audioSource != null && clip != null)
            audioSource.PlayOneShot(clip);
    }
}
