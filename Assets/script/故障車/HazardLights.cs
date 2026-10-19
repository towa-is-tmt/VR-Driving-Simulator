using UnityEngine;

public class HazardLights : MonoBehaviour
{
    [Header("請放入左右方向燈的「模型」物件")]
    public Renderer[] blinkerRenderers;

    [Header("閃爍速度 (數值越高閃越快)")]
    public float flashSpeed = 4f;

    [Header("發光顏色與最大亮度 (點擊可調 Intensity)")]
    [ColorUsage(true, true)]
    public Color baseGlowColor = new Color(1f, 0.5f, 0f); // 基礎橘黃色

    void Update()
    {
        // 使用 Mathf.PingPong 產生一個在 0 到 1 之間來回平滑變化的數值
        // 這會自動形成漸亮、漸暗的緩衝效果
        float glowLerp = Mathf.PingPong(Time.time * flashSpeed, 1f);

        // 讓波形更陡峭一點，模擬燈泡亮起快、熄滅稍微有餘溫的真實感
        glowLerp = Mathf.SmoothStep(0f, 1f, glowLerp);

        // 根據平滑數值計算出當前的實際 HDR 顏色
        Color currentGlowColor = baseGlowColor * glowLerp;

        // 套用至所有方向燈材質
        foreach (Renderer rend in blinkerRenderers)
        {
            if (rend != null)
            {
                // 確保材質的自發光功能是開啟的
                rend.material.EnableKeyword("_EMISSION");
                // 動態修改自發光顏色與亮度
                rend.material.SetColor("_EmissionColor", currentGlowColor);
            }
        }
    }

    // 當這個物件被銷毀或關閉時，順手把動態產生的實例材質清理掉，防止記憶體洩漏
    private void OnDestroy()
    {
        foreach (Renderer rend in blinkerRenderers)
        {
            if (rend != null && rend.material != null)
            {
                Destroy(rend.material);
            }
        }
    }
}