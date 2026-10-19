using UnityEngine;

/// <summary>
/// 🪞 後照鏡危險提示（參考真實車輛的盲點偵測 BSM 警示）
///
/// 掛在「左 / 中 / 右」後照鏡各一份。當有危險因子從後方接近時，
/// 對應那面鏡子的「邊緣」會閃紅光，中間影像維持清晰 ——
/// 玩家仍然看得見後方來車，而不是被紅色蓋住看不到。
///
/// 顯示方式會自動選擇（依材質支援度）：
///   1. 邊緣警示 — 材質有 _WarnStrength（Custom/MirrorMask_BuiltIn）→ 最真實，優先使用
///   2. 整面染色 — 只有一般顏色屬性時的退路
///   3. 警示框物件 — 有指定 Warning Frame 時額外開關（可與上面並用）
///
/// ⚠️ 純顯示用，不會影響任何車輛行為。
/// </summary>
public class MirrorHazardAlert : MonoBehaviour
{
    public enum MirrorSide { Left, Center, Right }

    [Header("這是哪一面鏡子")]
    public MirrorSide side = MirrorSide.Center;

    [Header("警示對象")]
    [Tooltip("鏡面的 Renderer。材質若是 Custom/MirrorMask_BuiltIn 會自動用邊緣警示")]
    public Renderer tintTarget;
    [Tooltip("可選：額外要閃爍的物件（例如鏡框旁的實體警示燈）")]
    public GameObject warningFrame;

    [Header("警示外觀")]
    public Color warningColor = new Color(1f, 0.12f, 0.08f, 1f);
    [Tooltip("邊緣發光強度。1 = 最亮")]
    [Range(0f, 1f)]
    public float warningIntensity = 1f;
    [Tooltip("退路用：材質沒有 _WarnStrength 時，要改色的屬性名稱")]
    public string tintPropertyName = "_TintColor";

    [Header("觸發條件")]
    [Tooltip("危險因子進入後方多少公尺內開始警示")]
    public float triggerDistance = 40f;
    [Tooltip("判定屬於左/右鏡的側向偏移門檻（公尺）。絕對值小於它算正後方，歸中央鏡")]
    public float sideThreshold = 1.5f;

    [Header("閃爍")]
    [Tooltip("閃爍間隔（秒）。危險越近閃得越快")]
    public float blinkInterval = 0.35f;
    [Tooltip("最近距離時的閃爍間隔，用來製造逼近感")]
    public float blinkIntervalWhenClose = 0.12f;

    private static readonly int WarnStrengthId = Shader.PropertyToID("_WarnStrength");
    private static readonly int WarnColorId = Shader.PropertyToID("_WarnColor");

    private Material mat;
    private bool useEdgeGlow;
    private int tintPropId = -1;
    private Color originalTint = Color.white;
    private bool tintReady;

    private float blinkTimer;
    private bool blinkOn;

    private void Start()
    {
        if (tintTarget != null)
        {
            // 取 material（實例）而非 sharedMaterial，避免改到磁碟上的共用材質
            mat = tintTarget.material;

            if (mat.HasProperty(WarnStrengthId))
            {
                useEdgeGlow = true;
                if (mat.HasProperty(WarnColorId)) mat.SetColor(WarnColorId, warningColor);
            }
            else
            {
                string prop = ResolveTintProperty(mat);
                if (prop != null)
                {
                    tintPropId = Shader.PropertyToID(prop);
                    originalTint = mat.GetColor(tintPropId);
                    tintReady = true;
                }
                else
                {
                    Debug.LogWarning(
                        $"[後照鏡警示] 材質「{mat.name}」的 shader（{mat.shader.name}）既沒有 _WarnStrength " +
                        $"也沒有可染色的顏色屬性，鏡面不會有警示。請改用 Warning Frame。", this);
                }
            }
        }
        SetAlert(false);
    }

    /// <summary>退路：找出這個材質能用的顏色屬性，都沒有就回 null。</summary>
    private string ResolveTintProperty(Material m)
    {
        if (!string.IsNullOrEmpty(tintPropertyName) && m.HasProperty(tintPropertyName))
            return tintPropertyName;
        if (m.HasProperty("_BaseColor")) return "_BaseColor";   // URP Lit
        if (m.HasProperty("_Color")) return "_Color";           // Built-in Standard
        return null;
    }

    private void Update()
    {
        var tracker = HazardAwarenessTracker.Instance;
        if (tracker == null) { SetAlert(false); return; }

        if (!tracker.TryGetRearHazard(out float distance, out float lateralOffset)
            || distance > triggerDistance
            || !BelongsToThisMirror(lateralOffset))
        {
            SetAlert(false);
            return;
        }

        // 越近閃越快
        float t = Mathf.InverseLerp(triggerDistance, 0f, distance);
        float interval = Mathf.Lerp(blinkInterval, blinkIntervalWhenClose, t);

        blinkTimer += Time.deltaTime;
        if (blinkTimer >= interval)
        {
            blinkTimer = 0f;
            blinkOn = !blinkOn;
            SetAlert(blinkOn);
        }
    }

    private bool BelongsToThisMirror(float lateralOffset)
    {
        switch (side)
        {
            case MirrorSide.Left:   return lateralOffset < -sideThreshold;
            case MirrorSide.Right:  return lateralOffset > sideThreshold;
            default:                return Mathf.Abs(lateralOffset) <= sideThreshold;
        }
    }

    private void SetAlert(bool on)
    {
        if (!on) { blinkOn = false; blinkTimer = 0f; }

        if (warningFrame != null && warningFrame.activeSelf != on)
            warningFrame.SetActive(on);

        if (mat == null) return;

        if (useEdgeGlow)
            mat.SetFloat(WarnStrengthId, on ? warningIntensity : 0f);
        else if (tintReady)
            mat.SetColor(tintPropId, on ? warningColor : originalTint);
    }

    private void OnDisable()
    {
        SetAlert(false);
    }
}
