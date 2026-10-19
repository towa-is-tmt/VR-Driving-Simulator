using System;
using UnityEngine;

public class TrainingItemController : MonoBehaviour
{
    [Header("項目識別（要跟選單的 itemName 完全一致）")]
    public string itemName;

    [Header("即時狀態（Debug）")]
    public bool isActive   = false;
    public bool isSettled  = false;
    public bool isComplete = false;

    [Header("可選：啟用/停用時要一起開關的物件（如觸發區）")]
    public GameObject[] toggleObjects;

    [Header("此項目對應的偵測器（訓練未選時關閉，只偵測所選）")]
    public Behaviour[] detectors;   // 拖對應 Detector，如 StraightLineDetector、ParkingDetector

    public event Action<TrainingItemController> onCompleted;
    public event Action<TrainingItemController> onFailed;

    public void Activate()
    {
        isActive = true; isComplete = false; isSettled = false; SetObjects(true);
    }

    public void Deactivate()
    {
        isActive = false; SetObjects(false);
    }

    public void ResetItem()
    {
        isComplete = false;
        isSettled  = false;
    }

    // 開關對應偵測器（由 TrainingModeManager 統一呼叫）
    public void SetDetectorsEnabled(bool on)
    {
        if (detectors == null) return;
        foreach (var d in detectors) if (d != null) d.enabled = on;
    }

    public void Complete()
    {
        if (!isActive || isSettled) return;
        isSettled = true; isComplete = true;
        Debug.Log($"[訓練] 項目完成：{itemName}");
        onCompleted?.Invoke(this);
    }

    public void Fail()
    {
        if (!isActive || isSettled) return;
        isSettled = true; isComplete = false;
        Debug.Log($"[訓練] 項目失敗：{itemName}");
        onFailed?.Invoke(this);
    }

    public void Cancel()
    {
        isComplete = false;
        isSettled  = false;
        Debug.Log($"[訓練] 項目取消：{itemName}");
    }

    private void SetObjects(bool on)
    {
        if (toggleObjects == null) return;
        foreach (var go in toggleObjects) if (go != null) go.SetActive(on);
    }
}