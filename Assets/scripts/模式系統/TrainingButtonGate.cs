using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TrainingButtonGate : MonoBehaviour
{
    [System.Serializable]
    public class Entry
    {
        public string itemName;   // 要跟訓練的 itemName 一致（例：倒車入庫）
        public Button button;      // 導航選單上對應那顆按鈕
    }

    [Header("項目按鈕（清除選項等不列入，永遠可按）")]
    public List<Entry> entries = new List<Entry>();

    // 只讓 allowed 內的按鈕可按，其餘變暗
    public void SetAllowed(List<string> allowed)
    {
        foreach (var e in entries)
            if (e.button != null)
                e.button.interactable = (allowed == null) || allowed.Contains(e.itemName);
    }

    // 全部可按（自由模式 / 回選單）
    public void AllowAll()
    {
        foreach (var e in entries)
            if (e.button != null) e.button.interactable = true;
    }
}