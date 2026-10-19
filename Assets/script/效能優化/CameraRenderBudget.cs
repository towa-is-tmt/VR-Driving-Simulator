using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// 🎥 攝影機算繪預算：讓「次要攝影機」少畫一點
///
/// 量測結果：遊戲每幀約 31ms，其中攝影機算繪就占了大部分（腳本＋物理＋動畫只有約 6ms）。
/// 場景裡除了主畫面，還有 3 面後照鏡、倒車鏡頭、2 台小地圖攝影機，每一台都把城市再畫一次，
/// 而且全部都是 1000m 可視距離、都算陰影。
///
/// 這個元件依「攝影機名稱關鍵字」套用規則：
///   ● 每幾幀才畫一次（側後照鏡 30Hz 肉眼幾乎看不出來；小地圖更不需要每幀）
///   ● 縮短可視距離（後照鏡看 200m 就夠）
///   ● 關掉陰影（鏡子裡的影子幾乎看不到，但陰影圖要重算一次）
///   ● 倒車鏡頭：倒車時每幀畫，平常降到很低的頻率
/// 主攝影機完全不動。關掉這個元件（或刪掉物件）就全部恢復原狀。
///
/// 用法：場景放一個空物件掛上它即可。Play 後 Console 會列出每台攝影機套用了哪條規則。
/// 搭配效能監測器（F11）比較前後的「每台攝影機」毫秒數。
/// </summary>
public class CameraRenderBudget : MonoBehaviour
{
    [Serializable]
    public class Rule
    {
        [Tooltip("攝影機物件名稱包含這個字就套用（第一條符合的規則優先）")]
        public string nameKeyword = "後照";
        [Tooltip("每幾幀畫一次。1 = 每幀（不變）、2 = 每 2 幀（30Hz@60fps）")]
        [Range(1, 10)] public int renderEveryNFrames = 1;
        [Tooltip("可視距離（m）。0 = 不改")]
        public float farClip = 0f;
        [Tooltip("這台攝影機不畫陰影")]
        public bool disableShadows = true;
        [Tooltip("倒車鏡頭專用：玩家倒車時改成每幀畫")]
        public bool fullRateWhenReversing = false;
    }

    [Header("🎥 規則（依攝影機名稱比對）")]
    public Rule[] rules =
    {
        new Rule { nameKeyword = "中後照", renderEveryNFrames = 1, farClip = 250f, disableShadows = true },
        new Rule { nameKeyword = "左後照", renderEveryNFrames = 2, farClip = 200f, disableShadows = true },
        new Rule { nameKeyword = "右後照", renderEveryNFrames = 2, farClip = 200f, disableShadows = true },
        new Rule { nameKeyword = "倒車",   renderEveryNFrames = 6, farClip = 60f,  disableShadows = true, fullRateWhenReversing = true },
        new Rule { nameKeyword = "Minimap", renderEveryNFrames = 3, farClip = 0f,  disableShadows = true },
    };

    [Header("🔧 診斷")]
    public bool logApplied = true;
    [Tooltip("每幾秒重新找一次攝影機（玩家車重生、切換場景時會出現新的攝影機）")]
    public float rescanInterval = 2f;

    private class Managed
    {
        public Camera cam;
        public Rule rule;
        public bool originallyEnabled;
        public float originalFar;
        public int phase;   // 錯開不同攝影機的算繪幀，避免同一幀全部擠在一起
        public bool intended;      // 遊戲（其他腳本）希望它開著嗎
        public bool lastSetByUs;   // 上一幀我們把 enabled 設成什麼（用來偵測別的腳本有沒有改它）
    }

    private readonly Dictionary<Camera, Managed> managed = new Dictionary<Camera, Managed>();
    private readonly HashSet<Camera> noShadowCams = new HashSet<Camera>();
    private float savedShadowDistance = -1f;
    private float nextScan;
    private IPlayerVehicleState playerState;
    private float nextPlayerSearch;

    private void OnEnable()
    {
        Camera.onPreCull += OnAnyPreCull;
        Camera.onPostRender += OnAnyPostRender;
        nextScan = 0f;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= OnAnyPreCull;
        Camera.onPostRender -= OnAnyPostRender;
        RestoreAll();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + rescanInterval;
            Scan();
        }
    }

    private void LateUpdate()
    {
        bool reversing = IsPlayerReversing();
        int frame = Time.frameCount;

        foreach (var m in managed.Values)
        {
            if (m.cam == null || !m.cam.gameObject.activeInHierarchy) continue;
            int every = (m.rule.fullRateWhenReversing && reversing) ? 1 : m.rule.renderEveryNFrames;
            if (every <= 1) continue;   // 不降頻的攝影機完全不碰 enabled

            // 別的腳本在這一幀改了 enabled → 以它的意思為準
            if (m.cam.enabled != m.lastSetByUs) m.intended = m.cam.enabled;
            if (!m.intended) { m.cam.enabled = false; m.lastSetByUs = false; continue; }

            // 降頻：輪到的那一幀讓 Unity 照常畫，其餘幀關掉（Render Texture 會保留上一張畫面）
            bool on = (frame + m.phase) % every == 0;
            m.cam.enabled = on;
            m.lastSetByUs = on;
        }
    }

    // ====================================================================
    //  找攝影機、套規則
    // ====================================================================

    private void Scan()
    {
        managed.Keys.Where(c => c == null).ToList().ForEach(c => managed.Remove(c));

        // 已經有腳本負責開關的攝影機（車內螢幕：倒車時開倒車鏡頭、關小地圖）→ 不降頻，避免互相打架
        var scriptControlled = new HashSet<Camera>();
        foreach (var sc in FindObjectsOfType<CarScreenController>())
        {
            if (sc.minimapCamera != null) scriptControlled.Add(sc.minimapCamera);
            if (sc.backupCamera != null) scriptControlled.Add(sc.backupCamera);
        }

        var sb = new StringBuilder();
        int phase = 0;
        foreach (var cam in FindObjectsOfType<Camera>(true))
        {
            if (managed.ContainsKey(cam)) continue;
            if (cam == Camera.main) continue;   // 主攝影機一律不動

            Rule rule = rules.FirstOrDefault(r => !string.IsNullOrEmpty(r.nameKeyword) &&
                                                  cam.name.IndexOf(r.nameKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
            if (rule == null) continue;

            // 輸出到畫面的攝影機不能降頻（畫面會閃）；有腳本負責開關的也不降頻。這兩種只套距離與陰影
            bool toScreen = cam.targetTexture == null;
            bool controlled = scriptControlled.Contains(cam);
            bool noRate = toScreen || controlled;

            var m = new Managed
            {
                cam = cam,
                rule = noRate ? new Rule { nameKeyword = rule.nameKeyword, renderEveryNFrames = 1, farClip = rule.farClip, disableShadows = rule.disableShadows } : rule,
                originallyEnabled = cam.enabled,
                originalFar = cam.farClipPlane,
                phase = phase++,
                intended = cam.enabled,
                lastSetByUs = cam.enabled
            };
            if (rule.farClip > 0f) cam.farClipPlane = rule.farClip;
            if (rule.disableShadows) noShadowCams.Add(cam);
            managed[cam] = m;

            sb.AppendLine($"  {cam.name}｜規則「{rule.nameKeyword}」｜每 {m.rule.renderEveryNFrames} 幀畫一次" +
                          $"{(rule.fullRateWhenReversing ? "（倒車時每幀）" : "")}｜可視距離 {m.originalFar:F0}→{cam.farClipPlane:F0}m｜" +
                          $"陰影 {(rule.disableShadows ? "關" : "照舊")}{(toScreen ? "｜⚠ 輸出到畫面，不降頻" : "")}" +
                          $"{(controlled ? "｜由 CarScreenController 負責開關（倒車才開倒車鏡頭），不降頻" : "")}");
        }

        if (logApplied && sb.Length > 0)
            Debug.Log("<color=#4fc3f7>🎥 [攝影機預算] 套用規則：\n" + sb + "</color>", this);
    }

    private void RestoreAll()
    {
        foreach (var m in managed.Values)
        {
            if (m.cam == null) continue;
            if (m.rule.renderEveryNFrames > 1) m.cam.enabled = m.intended;   // 還給遊戲原本想要的狀態
            m.cam.farClipPlane = m.originalFar;
        }
        managed.Clear();
        noShadowCams.Clear();
        if (savedShadowDistance >= 0f) { QualitySettings.shadowDistance = savedShadowDistance; savedShadowDistance = -1f; }
    }

    // ====================================================================
    //  陰影：只在這幾台攝影機算繪期間把陰影距離設成 0，畫完立刻還原
    // ====================================================================

    private void OnAnyPreCull(Camera c)
    {
        if (c == null || !noShadowCams.Contains(c)) return;
        if (savedShadowDistance < 0f) savedShadowDistance = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = 0f;
    }

    private void OnAnyPostRender(Camera c)
    {
        if (c == null || !noShadowCams.Contains(c) || savedShadowDistance < 0f) return;
        QualitySettings.shadowDistance = savedShadowDistance;
        savedShadowDistance = -1f;
    }

    // ====================================================================
    //  倒車偵測
    // ====================================================================

    private bool IsPlayerReversing()
    {
        if (playerState == null && Time.unscaledTime >= nextPlayerSearch)
        {
            nextPlayerSearch = Time.unscaledTime + 2f;
            GameObject p = GameObject.FindWithTag("Player");
            if (p != null)
            {
                Transform root = p.transform.root;
                playerState = root.GetComponentsInChildren<MonoBehaviour>(true).OfType<IPlayerVehicleState>().FirstOrDefault();
                if (playerState == null && logApplied)
                    Debug.Log("<color=#4fc3f7>🎥 [攝影機預算] 玩家車上找不到 IPlayerVehicleState，倒車鏡頭一律用低頻率</color>", this);
            }
        }
        return playerState != null && playerState.IsReversing;
    }
}
