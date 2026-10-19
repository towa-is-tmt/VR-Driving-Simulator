using UnityEngine;

/// <summary>
/// 錄影模式 — 錄製乾淨的開車畫面用。
/// 勾選「啟用錄影模式」後，進入 Play 會持續隱藏指定的 UI（小地圖、訓練清單、導航選單等），
/// 並可一併隱藏滑鼠游標。錄完取消勾選即完全恢復，不影響任何既有邏輯。
/// </summary>
public class RecordingMode : MonoBehaviour
{
    [Header("錄影模式開關")]
    [Tooltip("勾選＝進入 Play 時持續隱藏下列 UI（錄影用）；取消＝一切正常顯示")]
    public bool 啟用錄影模式 = true;

    [Header("要隱藏的 UI（把小地圖／訓練清單／導航選單等物件拖進來）")]
    [Tooltip("清單內的物件在錄影模式下會被持續關閉，不會被其他腳本重新打開")]
    public GameObject[] 要隱藏的UI;

    [Header("其他選項")]
    [Tooltip("錄影時隱藏並鎖定滑鼠游標，避免游標入鏡")]
    public bool 隱藏滑鼠游標 = true;

    [Tooltip("錄影時關閉所有 Debug.Log（保留警告與錯誤）")]
    public bool 關閉除錯訊息 = true;

    private bool 上一次狀態 = false;

    private void Start()
    {
        套用狀態();
        上一次狀態 = 啟用錄影模式;
    }

    private void LateUpdate()
    {
        // 若在 Inspector 中途切換開關，即時反應
        if (啟用錄影模式 != 上一次狀態)
        {
            套用狀態();
            上一次狀態 = 啟用錄影模式;
        }

        if (!啟用錄影模式) return;

        // 持續壓制：即使 ExamChecklist／NavigationMenuToggle 的「固定顯示」
        // 想把面板打開，這裡每幀都會再關掉，確保畫面乾淨。
        if (要隱藏的UI != null)
        {
            foreach (var go in 要隱藏的UI)
            {
                if (go != null && go.activeSelf)
                    go.SetActive(false);
            }
        }

        if (隱藏滑鼠游標 && Cursor.visible)
        {
            Cursor.visible   = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    private void 套用狀態()
    {
        if (啟用錄影模式)
        {
            if (關閉除錯訊息)
                Debug.unityLogger.filterLogType = LogType.Warning; // 只留警告與錯誤

            if (隱藏滑鼠游標)
            {
                Cursor.visible   = false;
                Cursor.lockState = CursorLockMode.Locked;
            }

            Debug.LogWarning("[錄影模式] 已啟用：指定 UI 將持續隱藏。錄完請取消勾選「啟用錄影模式」。");
        }
        else
        {
            // 恢復原狀
            Debug.unityLogger.filterLogType = LogType.Log;

            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;

            if (要隱藏的UI != null)
            {
                foreach (var go in 要隱藏的UI)
                {
                    if (go != null && !go.activeSelf)
                        go.SetActive(true);
                }
            }
        }
    }
}
