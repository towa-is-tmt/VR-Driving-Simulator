using UnityEngine;

/// <summary>
/// UI 音效管理器 — 集中管理按鈕的「滑過」與「點擊」音效。
///
/// 為什麼要有這支：
/// 音效檔只需要在這裡拖一次，所有按鈕的 ButtonHoverScale 會自動呼叫，
/// 不必在每一顆按鈕上重複拖 AudioClip，之後要換音效也只改一個地方。
///
/// 放法：每個場景各放一個（車庫+選單、car 各一），掛在獨立的空物件上。
/// </summary>
public class UISoundManager : MonoBehaviour
{
    public static UISoundManager 實例 { get; private set; }

    [Header("音效檔")]
    [Tooltip("滑鼠移到按鈕上時播放")]
    public AudioClip 滑過音效;

    [Tooltip("按下按鈕時播放")]
    public AudioClip 點擊音效;

    [Header("音量")]
    [Range(0f, 1f)] public float 滑過音量 = 0.5f;
    [Range(0f, 1f)] public float 點擊音量 = 0.8f;

    [Header("防連發")]
    [Tooltip("滑過音效的最短間隔（秒），避免游標快速掃過時聲音連發")]
    [Range(0f, 0.5f)] public float 滑過最短間隔 = 0.06f;

    private AudioSource 音源;
    private float 上次滑過時間 = -99f;

    private void Awake()
    {
        實例 = this;

        音源 = GetComponent<AudioSource>();
        if (音源 == null) 音源 = gameObject.AddComponent<AudioSource>();

        // UI 音效固定用 2D，不受相機距離影響
        音源.playOnAwake  = false;
        音源.spatialBlend = 0f;
        音源.loop         = false;
    }

    private void OnDestroy()
    {
        if (實例 == this) 實例 = null;
    }

    public void 播放滑過()
    {
        if (滑過音效 == null || 音源 == null) return;

        // 用 unscaledTime：選單凍結（timeScale = 0）時也能正確計時
        if (Time.unscaledTime - 上次滑過時間 < 滑過最短間隔) return;
        上次滑過時間 = Time.unscaledTime;

        音源.PlayOneShot(滑過音效, 滑過音量);
    }

    public void 播放點擊()
    {
        if (點擊音效 == null || 音源 == null) return;
        音源.PlayOneShot(點擊音效, 點擊音量);
    }
}
