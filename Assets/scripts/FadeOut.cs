using System.Collections;
using UnityEngine;

[RequireComponent(typeof(CanvasGroup))]
public class FadeOut : MonoBehaviour
{
    private CanvasGroup canvasGroup;

    [Header("實體燈光設定")]
    [Tooltip("請把場景中負責模擬強光的那個 Light 物件（例如 Directional Light 或 Spot Light）拖進來")]
    public Light strongLight;

    [Header("淡出時間與延遲")]
    [Tooltip("進入場景後，延遲幾秒才開始變透明與減弱燈光")]
    public float delayBeforeFade = 0.2f;
    [Tooltip("徹底消失需要花幾秒")]
    public float fadeDuration = 2.0f;

    private void Start()
    {
        canvasGroup = GetComponent<CanvasGroup>();

        // 檢查有沒有拖入燈光，如果沒有，會自動在後台提醒你
        if (strongLight == null)
        {
            Debug.LogWarning($"<color=orange>[特效警告]</color> {gameObject.name} 腳本上的 Strong Light 欄位是空的！請記得拖入燈光物件。", this);
        }

        // 啟動雙重淡出計時器
        StartCoroutine(DoDoubleFadeOut());
    }

    private IEnumerator DoDoubleFadeOut()
    {
        // 1. 等待初始延遲
        if (delayBeforeFade > 0)
        {
            yield return new WaitForSeconds(delayBeforeFade);
        }

        float elapsedTime = 0f;
        float startAlpha = canvasGroup.alpha;
        
        // 紀錄燈光原本的初始強度（例如 5.0 或 10.0）
        float startLightIntensity = strongLight != null ? strongLight.intensity : 0f;

        // 2. 開始在時間內同步進行雙重 Lerp 漸變
        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            float normalizedTime = elapsedTime / fadeDuration; // 算出目前精確的進度百分比 (0 到 1)

            // 【畫布淡出】：Alpha 從原本的數值漸變到 0
            canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, normalizedTime);

            // 【燈光減弱】：強度從原本的亮度漸變到 0
            if (strongLight != null)
            {
                strongLight.intensity = Mathf.Lerp(startLightIntensity, 0f, normalizedTime);
            }

            yield return null; // 等待下一幀
        }

        // 3. 確保數值徹底歸零，避免浮點數殘留
        canvasGroup.alpha = 0f;
        if (strongLight != null)
        {
            strongLight.intensity = 0f;
            strongLight.gameObject.SetActive(false); // 徹底關閉燈光物件以節省效能
        }

        // 4. 徹底關閉畫布
        gameObject.SetActive(false);

        Debug.Log("<color=lime>[環境光效]</color> 畫布與實體強光已完美同步淡出並關閉！");
    }
}