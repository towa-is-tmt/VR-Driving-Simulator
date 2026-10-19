using UnityEngine;
using TMPro;

/// <summary>
/// 🚑 救護車禮讓教練
///
/// 負責把「察覺」變成「正確動作」的教學閉環：
///   1. 救護車進入後方 promptDistance → 顯示文字 + 播語音「救護車接近，請減速靠右禮讓」
///   2. 在 graceSeconds 內判定玩家有沒有真的往右靠並減速
///   3. 給出「禮讓正確」或「未禮讓救護車」的回饋，並記錄成績
///
/// 掛在場景中任一物件上（跟 HazardAwarenessTracker 同一個物件最方便）。
///
/// ⚠️ 純提示與評分，不會干預救護車或任何車輛的行為。
/// </summary>
public class AmbulanceYieldCoach : MonoBehaviour
{
    [Header("提示文字（可選，支援 3D 或 UI 版 TextMeshPro）")]
    public TMP_Text instructionText;

    [Header("語音提示（可選）")]
    public AudioSource voiceSource;
    [Tooltip("「救護車接近，請減速靠右禮讓」")]
    public AudioClip clipYieldRight;
    [Tooltip("「禮讓正確」")]
    public AudioClip clipGood;
    [Tooltip("「未禮讓救護車」")]
    public AudioClip clipFail;

    [Header("觸發與判定")]
    // ⚠️ 這個值必須明顯小於 AmbulanceSpawner 的 spawnBehindDistance（預設 60m），
    //    否則救護車一生成就落在提示範圍內，會變成「還很遠就開始喊靠右」。
    //    目前的提示階梯：60m 生成 → 45m 警笛加急 → 40m 後照鏡閃紅 → 30m 語音+文字指示
    [Tooltip("救護車進入後方多少公尺才開始提示。要比生成距離 60m 小很多")]
    public float promptDistance = 30f;
    [Tooltip("往右橫移多少公尺算有靠邊")]
    public float yieldRightMeters = 1.0f;
    [Tooltip("時速掉多少 km/h 算有減速")]
    public float yieldSlowKmH = 5f;
    [Tooltip("是否一定要「同時」減速才算通過。關掉的話只要靠右就算")]
    public bool requireSlowDown = true;
    [Tooltip("提示後給玩家幾秒完成禮讓")]
    public float graceSeconds = 8f;

    [Header("顯示")]
    [Tooltip("結果訊息顯示幾秒後清空")]
    public float resultDisplaySeconds = 4f;

    [Header("文字顏色（依狀態自動切換）")]
    [Tooltip("提示剛出現時的顏色。隨著禮讓時間快用完，會漸變成失敗色")]
    public Color promptColor = new Color(1f, 0.62f, 0.05f);    // 橘
    public Color successColor = new Color(0.25f, 1f, 0.35f);   // 綠
    public Color failColor = new Color(1f, 0.22f, 0.18f);      // 紅

    [Header("診斷")]
    public bool logResult = true;

    /// <summary>累計成績，供結算畫面讀取</summary>
    public int YieldSuccessCount { get; private set; }
    public int YieldFailCount { get; private set; }

    private enum Stage { Idle, Prompted, Judged }
    private Stage stage = Stage.Idle;

    private Vector3 promptPos;
    private float promptSpeedKmH;
    private float promptTime;
    private float clearTextAt = -1f;

    private const float MsToKmH = 3.6f;

    private void Start()
    {
        // 清掉在 Inspector 裡殘留的預設文字（例如從儀表板複製過來的舊內容）
        SetText("");
    }

    private void Update()
    {
        var tracker = HazardAwarenessTracker.Instance;
        if (tracker == null || tracker.playerTransform == null) return;

        // 結果訊息到時間就清掉
        if (clearTextAt > 0f && Time.time >= clearTextAt)
        {
            SetText("");
            clearTextAt = -1f;
        }

        bool ambulanceNear = AmbulanceAI.IsAmbulanceActive
                             && tracker.TryGetRearHazard(out float dist, out _)
                             && dist <= promptDistance;

        switch (stage)
        {
            case Stage.Idle:
                if (ambulanceNear) BeginPrompt(tracker);
                break;

            case Stage.Prompted:
                // 剩餘時間越少，文字越偏紅 —— 讓玩家直覺感受到「快來不及了」
                if (instructionText != null)
                {
                    float urgency = Mathf.Clamp01((Time.time - promptTime) / Mathf.Max(0.1f, graceSeconds));
                    instructionText.color = Color.Lerp(promptColor, failColor, urgency);
                }

                if (HasYielded(tracker))
                {
                    Judge(true);
                }
                else if (!tracker.TryGetRearHazard(out _, out _))
                {
                    // 救護車已經開過去了（不再位於玩家後方），沒讓到就是沒讓到，
                    // 不用等 graceSeconds 跑完 —— 真實世界的判準也是「它過去之前有沒有讓」。
                    Judge(false);
                }
                else if (Time.time - promptTime >= graceSeconds)
                {
                    Judge(false);
                }
                break;

            case Stage.Judged:
                // 這台救護車走了才重置，避免同一次事件重複評分
                if (!AmbulanceAI.IsAmbulanceActive) stage = Stage.Idle;
                break;
        }
    }

    private void BeginPrompt(HazardAwarenessTracker tracker)
    {
        stage = Stage.Prompted;
        promptTime = Time.time;
        promptPos = tracker.playerTransform.position;
        promptSpeedKmH = CurrentSpeedKmH(tracker);

        // 不使用 emoji：一般字體圖集不含 emoji，會顯示成空白方框。
        // 成功／失敗改用文字顏色區分（橘 / 綠 / 紅）。
        SetText("救護車接近\n請減速靠右禮讓", promptColor);
        PlayVoice(clipYieldRight);

        if (logResult)
            Debug.Log("<color=orange>🚑 [禮讓教練] 救護車接近，已提示玩家靠右禮讓</color>");
    }

    /// <summary>玩家是否已完成禮讓動作。</summary>
    private bool HasYielded(HazardAwarenessTracker tracker)
    {
        Transform player = tracker.playerTransform;

        // 玩家往右移動的話，當初的起點會落在他的左邊，所以取負號
        float movedRight = -player.InverseTransformPoint(promptPos).x;
        if (movedRight < yieldRightMeters) return false;

        if (requireSlowDown && (promptSpeedKmH - CurrentSpeedKmH(tracker)) < yieldSlowKmH)
            return false;

        return true;
    }

    private void Judge(bool success)
    {
        stage = Stage.Judged;

        if (success)
        {
            YieldSuccessCount++;
            SetText("禮讓正確", successColor);
            PlayVoice(clipGood);
            if (logResult)
                Debug.Log($"<color=lime>✅ [禮讓教練] 玩家正確禮讓救護車（累計成功 {YieldSuccessCount} 次）</color>");
        }
        else
        {
            YieldFailCount++;
            SetText("未禮讓救護車\n應減速並靠右讓行", failColor);
            PlayVoice(clipFail);
            if (logResult)
                Debug.LogWarning($"❌ [禮讓教練] 玩家未禮讓救護車（累計失誤 {YieldFailCount} 次）");
        }

        clearTextAt = Time.time + resultDisplaySeconds;
    }

    private float CurrentSpeedKmH(HazardAwarenessTracker tracker)
        => tracker.playerRigidbody != null ? tracker.playerRigidbody.velocity.magnitude * MsToKmH : 0f;

    private void SetText(string msg)
    {
        if (instructionText != null) instructionText.text = msg;
    }

    private void SetText(string msg, Color color)
    {
        if (instructionText == null) return;
        instructionText.text = msg;
        instructionText.color = color;
    }

    private void PlayVoice(AudioClip clip)
    {
        if (voiceSource != null && clip != null) voiceSource.PlayOneShot(clip);
    }
}
