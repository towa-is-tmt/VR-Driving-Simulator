using UnityEngine;
using UnityEngine.Events;

public class SpiralRoadDetector : MonoBehaviour
{
    [Header("事件")]
    public UnityEvent onExamStart;    // 考試開始
    public UnityEvent onHitWall;      // 撞到牆
    public UnityEvent onSuccess;      // 抵達四樓且無撞線 → 成功
    public UnityEvent onFail;         // 抵達四樓但有撞線 → 失敗

    private bool examStarted = false;
    private bool hasHitWall  = false;
    private bool isFinished  = false;

    public void OnStartExam()
    {
        if (!enabled) return;          // ← 訓練沒選這項（元件被關）→ 不偵測
        if (examStarted) return;
        examStarted = true;
        hasHitWall  = false;
        isFinished  = false;
        Debug.Log("[Spiral] 考試開始！");
        onExamStart?.Invoke();
    }

    public void OnHitWall()
    {
        if (!enabled) return;          // ←
        if (!examStarted || isFinished) return;
        hasHitWall = true;
        Debug.Log("[Spiral] 撞到邊線！登記失敗");
        onHitWall?.Invoke();
    }

    public void OnReachGoal()
    {
        if (!enabled) return;          // ←
        if (!examStarted || isFinished) return;
        isFinished = true;

        if (hasHitWall)
        {
            Debug.Log("[Spiral] 抵達四樓，但中途有撞線 → 失敗");
            onFail?.Invoke();
        }
        else
        {
            Debug.Log("[Spiral] 抵達四樓，無撞線 → 成功");
            onSuccess?.Invoke();
        }
    }

    // 沒被啟用時，順便清掉開始旗標，避免下次啟用還記得舊狀態
    void OnDisable()
    {
        examStarted = false;
        hasHitWall  = false;
        isFinished  = false;
    }

    public bool HasHitWall  => hasHitWall;
    public bool IsSuccess   => !hasHitWall && isFinished;
    public bool ExamStarted => examStarted;
}