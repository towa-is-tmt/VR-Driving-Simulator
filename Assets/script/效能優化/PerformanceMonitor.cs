using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// 📈 效能監測器：找出「到底是誰在吃 CPU」
///
/// 為什麼需要：遮擋剔除讓三角形少了 31%、Batches 少了 19%，但 FPS 幾乎沒變 ——
/// Stats 顯示主執行緒 104ms、算繪執行緒只有 31ms，代表瓶頸在主執行緒（腳本、物理、動畫…），不在畫圖。
///
/// 做法：用 Unity 的 ProfilerRecorder 同時錄下
///   ● 每一支腳本的 Update／FixedUpdate／LateUpdate（Profiler 裡的「xxx.Update() [Invoke]」）
///   ● 物理、動畫、算繪、陰影、垃圾回收等主要階段
/// 錄 N 秒後，把「平均每幀花多少毫秒」排名印在 Console（同時會寫進 Editor.log，Claude 可以直接讀）。
///
/// 用法：場景放一個空物件掛上它 → Play → 正常開車，進場 15 秒後自動量一次；之後隨時按 F11 再量一次。
/// ⚠ 量測時請關掉 Scene 視窗或切到別的分頁（Scene 視窗也會算繪，會讓數字失真）。
/// </summary>
public class PerformanceMonitor : MonoBehaviour
{
    [Header("📈 量測設定")]
    [Tooltip("進場後幾秒自動量一次（0 = 不自動）")]
    public float autoRunAfterSeconds = 15f;
    [Tooltip("每次量測錄幾秒")]
    public float sampleSeconds = 10f;
    [Tooltip("手動量測按鍵")]
    public KeyCode measureKey = KeyCode.F11;
    [Tooltip("報告列出前幾名腳本")]
    public int topScripts = 15;
    [Tooltip("🔍 卡頓偵測：任何一幀超過這個毫秒數，就立刻印出那一幀各階段的耗時（不在量測期間也會偵測）。0 = 關閉")]
    public float spikeThresholdMs = 150f;
    [Tooltip("進場後幾秒內不偵測卡頓（場景載入、第一次生成車流本來就會卡）")]
    public float spikeIgnoreFirstSeconds = 8f;

    // 主要階段（名稱不存在的會自動略過）
    private static readonly string[] StageMarkers =
    {
        "PlayerLoop",
        "Update.ScriptRunBehaviourUpdate",
        "FixedUpdate.ScriptRunBehaviourFixedUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "FixedUpdate.PhysicsFixedUpdate",
        "Physics.Simulate",
        "Physics.Processing",
        "PreLateUpdate.DirectorUpdateAnimationBegin",
        "Animators.Update",
        "PostLateUpdate.FinishFrameRendering",
        "Camera.Render",
        "Culling",
        "Shadows.RenderShadowMap",
        "Render.OpaqueGeometry",
        "GC.Collect",
        "Gfx.WaitForPresentOnGfxThread",
        "Gfx.WaitForRenderThread",
    };

    private class Rec
    {
        public string name;
        public ProfilerRecorder recorder;
        public double totalNs;
    }

    // 每台攝影機的算繪時間（從剔除開始到算繪結束，主執行緒）
    private class CamRec { public Camera cam; public string name; public double totalMs; public int renders; }
    private readonly Dictionary<Camera, CamRec> camRecs = new Dictionary<Camera, CamRec>();
    private readonly Dictionary<Camera, long> camStart = new Dictionary<Camera, long>();
    private readonly Stopwatch watch = Stopwatch.StartNew();

    private readonly List<Rec> stages = new List<Rec>();
    private readonly List<Rec> scripts = new List<Rec>();
    private bool measuring;
    private float measureEnd;
    private int frames;
    private double frameTimeSum;
    private float worstFrame;
    private int run;

    private void Start()
    {
        if (autoRunAfterSeconds > 0f) Invoke(nameof(BeginMeasure), autoRunAfterSeconds);
        UnityEngine.Debug.Log($"<color=#8bc34a>📈 [效能監測] 已啟動：{(autoRunAfterSeconds > 0f ? $"{autoRunAfterSeconds:F0} 秒後自動量測，" : "")}隨時按 {measureKey} 量測 {sampleSeconds:F0} 秒</color>", this);
    }

    private void Update()
    {
        if (Input.GetKeyDown(measureKey) && !measuring) BeginMeasure();
        DetectSpike();
    }

    // ── 卡頓偵測：一直錄著主要階段，發現某一幀特別久就把那一幀拆開來看 ──
    private readonly List<Rec> spikeRecs = new List<Rec>();
    private bool spikeReady;
    private int spikeCount;
    private float lastSpikeLog = -99f;

    private void DetectSpike()
    {
        if (spikeThresholdMs <= 0f || Time.timeSinceLevelLoad < spikeIgnoreFirstSeconds) return;
        if (!spikeReady)
        {
            spikeReady = true;
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var wanted = new HashSet<string>(StageMarkers) { "GC.Alloc", "Instantiate", "Destroy", "Loading.ReadObject",
                "Shader.CreateGPUProgram", "Mesh.UploadMeshData", "Physics.ProcessingCallbacks", "Physics.SyncColliderTransform" };
            var seen = new HashSet<string>();
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (d.Name == null || !wanted.Contains(d.Name) || !seen.Add(d.Name)) continue;
                var r = ProfilerRecorder.StartNew(d.Category, d.Name);
                if (r.Valid) spikeRecs.Add(new Rec { name = d.Name, recorder = r }); else r.Dispose();
            }
        }

        float dt = Time.unscaledDeltaTime * 1000f;
        if (dt < spikeThresholdMs || Time.unscaledTime - lastSpikeLog < 1f) return;
        lastSpikeLog = Time.unscaledTime;
        spikeCount++;

        // LastValue = 上一幀（也就是剛剛那一幀卡頓）的數值
        var parts = spikeRecs.Select(r => new { r.name, ms = r.recorder.LastValue / 1e6 })
                             .Where(x => x.ms >= 1.0).OrderByDescending(x => x.ms)
                             .Select(x => $"{StageLabel(x.name).Trim().TrimStart('└', ' ')} {x.ms:F0}ms");
        UnityEngine.Debug.LogWarning($"⚠ [效能監測] 卡頓 #{spikeCount}：一幀花了 {dt:F0} ms（第 {Time.timeSinceLevelLoad:F1} 秒）｜" +
                                     $"{string.Join("、", parts.ToArray())}｜※ 這一行前後的 Log 通常就是當下發生的事（例如生成車輛）", this);
    }

    [ContextMenu("📈 立即量測")]
    public void BeginMeasure()
    {
        if (measuring) return;
        DisposeAll();

        // 列出目前所有可錄的標記（名稱 → 分類）；同一個名稱要用它真正的分類去錄，否則會找不到
        var handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        var available = new Dictionary<string, ProfilerCategory>();
        foreach (var h in handles)
        {
            var d = ProfilerRecorderHandle.GetDescription(h);
            if (d.Name != null && !available.ContainsKey(d.Name)) available[d.Name] = d.Category;
        }

        foreach (string m in StageMarkers)
        {
            ProfilerCategory cat;
            if (!available.TryGetValue(m, out cat)) continue;
            var r = ProfilerRecorder.StartNew(cat, m);
            if (r.Valid) stages.Add(new Rec { name = m, recorder = r });
            else r.Dispose();
        }

        // 所有目前已註冊的腳本函式（Profiler 裡的「xxx.Update() [Invoke]」）
        foreach (var h in handles)
        {
            var d = ProfilerRecorderHandle.GetDescription(h);
            if (d.Name == null || !d.Name.Contains("[Invoke]")) continue;
            var r = ProfilerRecorder.StartNew(d.Category, d.Name);
            if (r.Valid) scripts.Add(new Rec { name = d.Name, recorder = r });
            else r.Dispose();
        }

        camRecs.Clear(); camStart.Clear();
        Camera.onPreCull -= OnCamPreCull; Camera.onPostRender -= OnCamPostRender;
        Camera.onPreCull += OnCamPreCull; Camera.onPostRender += OnCamPostRender;

        frames = 0; frameTimeSum = 0; worstFrame = 0f;
        measuring = true;
        measureEnd = Time.unscaledTime + sampleSeconds;
        run++;
        UnityEngine.Debug.Log($"<color=#8bc34a>📈 [效能監測] 第 {run} 次量測開始（{sampleSeconds:F0} 秒）：追蹤 {stages.Count} 個階段、{scripts.Count} 個腳本函式。請照常開車</color>", this);
    }

    private void LateUpdate()
    {
        if (!measuring) return;

        // 第一幀的 LastValue 是開始前的資料，跳過
        if (frames > 0)
        {
            foreach (var r in stages) r.totalNs += r.recorder.LastValue;
            foreach (var r in scripts) r.totalNs += r.recorder.LastValue;
            float dt = Time.unscaledDeltaTime * 1000f;
            frameTimeSum += dt;
            if (dt > worstFrame) worstFrame = dt;
        }
        frames++;

        if (Time.unscaledTime >= measureEnd) Report();
    }

    private void Report()
    {
        measuring = false;
        Camera.onPreCull -= OnCamPreCull;
        Camera.onPostRender -= OnCamPostRender;
        int n = Mathf.Max(1, frames - 1);
        double avgFrame = frameTimeSum / n;

        var sb = new StringBuilder();
        sb.AppendLine($"<color=#8bc34a>📈 [效能監測] 第 {run} 次報告｜{n} 幀｜平均每幀 {avgFrame:F1} ms（{1000.0 / avgFrame:F1} FPS）｜最慢一幀 {worstFrame:F0} ms</color>");

        double playerLoop = 0;
        sb.AppendLine("【主要階段】平均每幀 ms");
        foreach (var r in stages)
        {
            double ms = r.totalNs / n / 1e6;
            if (r.name == "PlayerLoop") playerLoop = ms;
            if (ms < 0.05) continue;
            sb.AppendLine($"  {ms,7:F2} ms  {StageLabel(r.name)}");
        }
        if (playerLoop > 0)
            sb.AppendLine($"  ※ 遊戲本身（PlayerLoop）{playerLoop:F1} ms，其餘約 {avgFrame - playerLoop:F1} ms 是編輯器自己的開銷（Inspector、Scene 視窗等），打包後不會有");

        sb.AppendLine($"【最耗時的腳本函式】前 {topScripts} 名，平均每幀 ms（同一支腳本所有物件加總）");
        var counts = CountInstances();
        foreach (var r in scripts.OrderByDescending(x => x.totalNs).Take(topScripts))
        {
            double ms = r.totalNs / n / 1e6;
            if (ms < 0.01) break;
            string type = r.name.Split('.')[0];
            int c;
            counts.TryGetValue(type, out c);
            sb.AppendLine($"  {ms,7:F2} ms  {r.name}{(c > 0 ? $"（場上 {c} 個，平均每個 {ms / c * 1000:F0} μs）" : "")}");
        }
        double scriptSum = scripts.Sum(x => x.totalNs) / n / 1e6;
        if (scriptSum > 0.05) sb.AppendLine($"  腳本合計 {scriptSum:F1} ms");
        else sb.AppendLine("  （這個 Unity 版本錄不到個別腳本，請以上面的「所有腳本 Update」為準）");

        AppendCameraReport(sb, n);

        UnityEngine.Debug.Log(sb.ToString(), this);
        DisposeAll();
    }

    // ── 每台攝影機 ──
    private void OnCamPreCull(Camera c)
    {
        if (!measuring || c == null) return;
        camStart[c] = watch.ElapsedTicks;
    }

    private void OnCamPostRender(Camera c)
    {
        if (!measuring || c == null) return;
        long t0;
        if (!camStart.TryGetValue(c, out t0)) return;
        CamRec r;
        if (!camRecs.TryGetValue(c, out r)) { r = new CamRec { cam = c, name = CamName(c) }; camRecs[c] = r; }
        r.totalMs += (watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
        r.renders++;
    }

    private void AppendCameraReport(StringBuilder sb, int n)
    {
        sb.AppendLine("【每台攝影機】平均每幀 ms（主執行緒：剔除＋算繪指令）");
        if (camRecs.Count == 0) { sb.AppendLine("  （沒有錄到，這個算繪管線可能不支援 Camera.onPreCull）"); return; }

        // 找出「輸出到畫面、而且會把整個畫面重畫」的最上層攝影機；比它更早畫的全螢幕攝影機等於白畫
        var screenCams = camRecs.Values.Where(r => r.cam != null && r.cam.targetTexture == null).Select(r => r.cam).ToList();
        foreach (var r in camRecs.Values.OrderByDescending(x => x.totalMs))
        {
            Camera c = r.cam;
            string target = c == null ? "?" : (c.targetTexture != null
                ? $"→ {c.targetTexture.name}（{c.targetTexture.width}×{c.targetTexture.height}）" : $"→ 畫面（深度 {c.depth}）");
            string wasted = "";
            if (c != null && c.targetTexture == null)
            {
                bool covered = screenCams.Any(o => o != c && o.depth > c.depth && FullScreen(o) &&
                                                   (o.clearFlags == CameraClearFlags.Skybox || o.clearFlags == CameraClearFlags.SolidColor));
                if (covered) wasted = "  ⚠ 會被深度更高的全螢幕攝影機整個蓋掉 —— 畫了也看不到（白算）";
            }
            double perFrame = r.totalMs / n;
            sb.AppendLine($"  {perFrame,7:F2} ms  {r.name} {target}｜可視距離 {(c != null ? c.farClipPlane : 0):F0}m｜" +
                          $"陰影 {(c != null && QualitySettings.shadows != ShadowQuality.Disable ? "依品質設定" : "關")}{wasted}");
        }
        sb.AppendLine($"  攝影機合計 {camRecs.Values.Sum(x => x.totalMs) / n:F1} ms");
    }

    private static bool FullScreen(Camera c)
    {
        Rect r = c.rect;
        return r.x <= 0.001f && r.y <= 0.001f && r.width >= 0.999f && r.height >= 0.999f;
    }

    private static string CamName(Camera c)
    {
        Transform t = c.transform;
        string n = t.name;
        if (t.parent != null) n = t.parent.name + "/" + n;
        if (t.parent != null && t.parent.parent != null) n = t.parent.parent.name + "/" + n;
        return n;
    }

    private static Dictionary<string, int> CountInstances()
    {
        var d = new Dictionary<string, int>();
        foreach (var mb in FindObjectsOfType<MonoBehaviour>())
        {
            if (mb == null || !mb.isActiveAndEnabled) continue;
            string k = mb.GetType().Name;
            int c;
            d.TryGetValue(k, out c);
            d[k] = c + 1;
        }
        return d;
    }

    private static string StageLabel(string m)
    {
        switch (m)
        {
            case "PlayerLoop": return "遊戲主迴圈總計（PlayerLoop）";
            case "Update.ScriptRunBehaviourUpdate": return "所有腳本 Update";
            case "FixedUpdate.ScriptRunBehaviourFixedUpdate": return "所有腳本 FixedUpdate";
            case "PreLateUpdate.ScriptRunBehaviourLateUpdate": return "所有腳本 LateUpdate";
            case "FixedUpdate.PhysicsFixedUpdate": return "物理（含碰撞偵測）";
            case "Physics.Simulate": return "  └ 物理模擬";
            case "Physics.Processing": return "  └ 物理處理";
            case "PreLateUpdate.DirectorUpdateAnimationBegin": return "動畫（Animator）";
            case "Animators.Update": return "  └ Animator 更新";
            case "PostLateUpdate.FinishFrameRendering": return "算繪（主執行緒部分）";
            case "Camera.Render": return "  └ 所有攝影機算繪";
            case "Culling": return "  └ 剔除計算";
            case "Shadows.RenderShadowMap": return "  └ 陰影";
            case "Render.OpaqueGeometry": return "  └ 不透明物件";
            case "GC.Collect": return "垃圾回收（GC，數字高代表程式一直在配置記憶體）";
            case "GC.Alloc": return "配置記憶體";
            case "Instantiate": return "生成物件（Instantiate）";
            case "Destroy": return "刪除物件";
            case "Loading.ReadObject": return "載入資源";
            case "Shader.CreateGPUProgram": return "第一次編譯著色器";
            case "Mesh.UploadMeshData": return "上傳網格到顯示卡";
            case "Gfx.WaitForPresentOnGfxThread": return "等待顯示卡（GPU 太忙）";
            case "Gfx.WaitForRenderThread": return "等待算繪執行緒";
            default: return m;
        }
    }

    private void DisposeAll()
    {
        foreach (var r in stages) r.recorder.Dispose();
        foreach (var r in scripts) r.recorder.Dispose();
        stages.Clear();
        scripts.Clear();
    }

    private void OnDestroy()
    {
        foreach (var r in spikeRecs) r.recorder.Dispose();
        spikeRecs.Clear();
        Camera.onPreCull -= OnCamPreCull;
        Camera.onPostRender -= OnCamPostRender;
        DisposeAll();
    }
}
