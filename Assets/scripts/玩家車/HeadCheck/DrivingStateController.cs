using UnityEngine;
using UnityEngine.InputSystem;

public class DrivingStateController : MonoBehaviour
{
    [Header("引用 B 程式")]
    public HeadCheckDetector headCheckDetector;

    [Header("Input System 設定")]
    public InputActionReference leftAction;   // 拖入 left Action
    public InputActionReference rightAction;  // 拖入 right Action

    private bool isChecking = false;

    private void OnEnable()
    {
        // 啟用 Action 並綁定觸發事件
        if (leftAction != null && leftAction.action != null)
        {
            leftAction.action.Enable();
            leftAction.action.started += OnTurnSignalTriggered;
        }

        if (rightAction != null && rightAction.action != null)
        {
            rightAction.action.Enable();
            rightAction.action.started += OnTurnSignalTriggered;
        }
    }

    private void OnDisable()
    {
        // 解除綁定與停用 Action，避免記憶體洩漏
        if (leftAction != null && leftAction.action != null)
        {
            leftAction.action.started -= OnTurnSignalTriggered;
            leftAction.action.Disable();
        }

        if (rightAction != null && rightAction.action != null)
        {
            rightAction.action.started -= OnTurnSignalTriggered;
            rightAction.action.Disable();
        }
    }

    // 只要按下 left (Q / 方向盤按鈕6) 或 right (R / 方向盤按鈕5) 都會觸發此函式
    private void OnTurnSignalTriggered(InputAction.CallbackContext context)
    {
        // 避免在已經偵測中重複啟動
        if (isChecking) return;

        // 將 context 傳入 StartHeadCheckSequence
        StartHeadCheckSequence(context);
    }

    void StartHeadCheckSequence(InputAction.CallbackContext context)
    {
        isChecking = true;
        Debug.Log($"▶️ [程式 A] 觸發方向燈訊號 ({context.action.name})，啟動擺頭偵測！");
        
        // 呼叫 B 程式開啟偵測
        if (headCheckDetector != null)
        {
            headCheckDetector.StartDetection(OnHeadCheckCompleted);
        }
    }

    // 當 B 程式完成後會自動回呼 (Callback) 這個函式
    private void OnHeadCheckCompleted()
    {
        isChecking = false;
        Debug.Log("🎉 [程式 A] 收到 B 程式完成訊號！安全檢查合格，允許起步！");
    }
}