using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// 面板影片背景 — 讓背景影片只在「這個面板顯示時」播放。
///
/// 掛在你要有影片背景的面板上（例如 模式選單面板）：
///   面板被開啟 → 影片開始播放
///   面板被關閉 → 影片停止，不再佔用效能
///
/// 這樣選完模式進到下一頁（項目選擇）時，影片就會自動停掉。
/// </summary>
public class PanelVideoBackground : MonoBehaviour
{
    [Header("影片播放器")]
    [Tooltip("場景上那個掛著 Video Player 的物件（背景影片播放器）")]
    public VideoPlayer 影片播放器;

    [Header("關閉時的行為")]
    [Tooltip("勾選＝停止並回到影片開頭；取消＝只暫停，下次從中斷處繼續")]
    public bool 停用時停止播放 = true;

    private void OnEnable()
    {
        if (影片播放器 == null) return;
        影片播放器.Play();
    }

    private void OnDisable()
    {
        if (影片播放器 == null) return;

        if (停用時停止播放) 影片播放器.Stop();
        else                影片播放器.Pause();
    }
}
