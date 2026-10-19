using UnityEngine;

/// <summary>
/// 設定套用器 — 把 GameSettings 的數值實際作用到遊戲上。
///
/// 套用方式：
///   主音量   → AudioListener.volume（全域總音量，所有聲音都受影響）
///   語音導航 → VoiceNavigation 的 AudioSource 音量（在總音量之下再乘一層）
///   引擎音效 → 由 CarPhysicsController 每幀讀 GameSettings 自行套用
///
/// 每個場景放一個即可（車庫、car 各一）。開場會自動套用一次，
/// 之後只要設定有變動就會即時反應，不需要按「套用」。
/// </summary>
public class SettingsApplier : MonoBehaviour
{
    [Header("語音導航")]
    [Tooltip("場景上的 VoiceNavigation，留空會自動尋找。車庫場景沒有可留空")]
    public VoiceNavigation 語音導航;

    [Header("除錯")]
    public bool 除錯訊息 = false;

    private void OnEnable()
    {
        if (語音導航 == null) 語音導航 = FindObjectOfType<VoiceNavigation>();

        GameSettings.設定變更 += 套用;
        套用();                       // 進場先套用一次
    }

    private void OnDisable()
    {
        GameSettings.設定變更 -= 套用;
    }

    /// <summary>把目前設定套到場景上，可手動呼叫</summary>
    public void 套用()
    {
        // 總音量：影響所有 AudioSource（BGM、UI 音效、引擎、語音）
        AudioListener.volume = GameSettings.主音量比例;

        // 語音導航：在總音量之下再乘一層，可單獨調小
        if (語音導航 != null && 語音導航.audioSource != null)
            語音導航.audioSource.volume = GameSettings.語音導航比例;

        if (除錯訊息)
            Debug.Log($"[設定] 已套用　主音量 {GameSettings.主音量:F0}　" +
                      $"語音 {GameSettings.語音導航:F0}　引擎 {GameSettings.引擎音效:F0}");
    }
}
