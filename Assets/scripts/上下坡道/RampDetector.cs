    using UnityEngine;
using UnityEngine.Events;

public class RampDetector : MonoBehaviour
{
    [Header("組件引用")]
    [Tooltip("組員的檔位腳本")]
    public CarGearController gearController;
    [Tooltip("車輛 Rigidbody（手動拖入）")]
    public Rigidbody vehicleRigidbody;

    [Header("偵測設定")]
    [Tooltip("低於此速度才開始計時（km/h）")]
    public float stopSpeedThreshold   = 3f;
    [Tooltip("P檔需靜止幾秒")]
    public float requiredStopDuration = 3f;

    [Header("事件")]
    public UnityEvent onSuccess;
    public UnityEvent onFail;

    [Header("即時狀態（Debug用）")]
    [Tooltip("考試是否開始")]
    public bool examStarted    = false;
    [Tooltip("是否已完成")]
    public bool isFinished     = false;
    [Tooltip("是否壓線")]
    public bool hasHitLine     = false;
    [Tooltip("P檔靜止計時")]
    public float stopTimer     = 0f;
    [Tooltip("P檔靜止是否完成")]
    public bool pGearConfirmed = false;
    [Tooltip("車子是否在停車區內")]
    public bool isInStopZone   = false;
    [Tooltip("目前車速（km/h）")]
    public float currentSpeedKmH = 0f;

    private Rigidbody rb;

private void Start()
{
    if (vehicleRigidbody == null)
        rb = GetComponent<Rigidbody>();
    if (vehicleRigidbody == null)
        rb = GetComponentInParent<Rigidbody>();
    if (vehicleRigidbody != null)
        rb = vehicleRigidbody;

    if (gearController == null)
        gearController = GetComponentInParent<CarGearController>();
    if (gearController == null)
        Debug.LogError("[坡道] 找不到 CarGearController！");

    if (rb == null)
        Debug.LogError("[坡道] 找不到 Rigidbody！請把車輛 Rigidbody 拖到 vehicleRigidbody。");
}
    private void FixedUpdate()
    {
        if (!examStarted || isFinished) return;
        if (gearController == null || rb == null) return;

        // ── 計算目前車速 ───────────────────────────────────
        currentSpeedKmH = rb.velocity.magnitude * 3.6f;

        // ── P檔低速計時（只有在 StopZone 內才計時）──────────
        if (!pGearConfirmed && isInStopZone)
        {
            bool isP       = gearController.currentGear == CarGearController.AutomaticGear.P;
            bool isStopped = currentSpeedKmH < stopSpeedThreshold;

            if (isP && isStopped)
            {
                stopTimer += Time.fixedDeltaTime;
                Debug.Log($"[坡道] P檔靜止中（速度：{currentSpeedKmH:F1} km/h）：{stopTimer:F1}s / {requiredStopDuration}s");

                if (stopTimer >= requiredStopDuration)
                {
                    pGearConfirmed = true;
                    Debug.Log("[坡道] ✅ P檔靜止確認！請打D檔起步。");
                }
            }
            else
            {
                if (stopTimer > 0f)
                    Debug.Log($"[坡道] P檔靜止中斷（速度：{currentSpeedKmH:F1} km/h），重置計時！");
                stopTimer = 0f;
            }
        }
    }

    // ── 由 RampStopZoneTrigger 呼叫 ──────────────────────────────
    public void OnEnterStopZone()
    {
        if (!examStarted || isFinished) return;
        isInStopZone = true;
        Debug.Log("[坡道] 進入停車區，開始偵測P檔靜止！");
    }

    public void OnExitStopZone()
    {
        if (!examStarted || isFinished) return;
        isInStopZone = false;
        stopTimer    = 0f;
        Debug.Log("[坡道] 離開停車區，重置計時！");
    }

    // ── 由 RampBottomLineTrigger 呼叫（左白線）──────────────────
    public void OnHitBottomLine()
    {
        if (!examStarted || isFinished) return;
        if (!pGearConfirmed) return; // P檔還沒確認 → 正常開進來，不算失敗

        hasHitLine = true;
        isFinished = true;
        Debug.Log("[坡道] 失敗：退出下方白線！");
        onFail?.Invoke();
    }   

    // ── 由 RampTopLineTrigger 呼叫（右白線壓線）─────────────────
    public void OnHitTopLine()
    {
        if (!examStarted || isFinished) return;
        if (!isInStopZone) return; // 還沒進停車區 → 不算壓線

        if (!pGearConfirmed)
        {
            hasHitLine = true;
            isFinished = true;
            Debug.Log("[坡道] 失敗：壓到上方白線！");
            onFail?.Invoke();
        }
        // pGearConfirmed 後前輪越過是正常起步，不算壓線
    }

    // ── 由 RampTopLineTrigger 呼叫（後輪越過右白線）─────────────
    public void OnRearWheelPassTopLine()
    {
        if (!examStarted || isFinished) return;

        if (!pGearConfirmed)
        {
            isFinished = true;
            Debug.Log("[坡道] 失敗：還沒完成P檔靜止3秒就起步！");
            onFail?.Invoke();
            return;
        }

        isFinished = true;

        if (hasHitLine)
        {
            Debug.Log("[坡道] 失敗：有壓線");
            onFail?.Invoke();
        }
        else
        {
            Debug.Log("[坡道] ✅ 成功！");
            onSuccess?.Invoke();
        }
    }

    // ── 由 RampStartTrigger 呼叫 ────────────────────────────────
    public void OnStartExam()
    {
        if (examStarted) return;
        examStarted      = true;
        hasHitLine       = false;
        isFinished       = false;
        pGearConfirmed   = false;
        isInStopZone     = false;
        stopTimer        = 0f;
        currentSpeedKmH  = 0f;
        Debug.Log("[坡道] 考試開始！");
    }
}