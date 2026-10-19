using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// 🏙 效能優化：靜態標記 ＋ 遮擋剔除（Occlusion Culling）
///
/// 選單：Tools > 效能優化 > 靜態標記與遮擋剔除
///
/// 為什麼需要：Unity 預設只會略過「畫面外」的物件（視錐剔除），
/// 被前面大樓擋住的建築照樣會畫。遮擋剔除可以把它們也略過，但前提是物件要標成 Static 並烘焙。
/// 目前 cc.unity 有 14833 個物件、0 個標成 Static、遮擋資料沒有烘焙。
///
/// 安全原則（寧可少標，不可標錯）：
///   會動的東西一律不標 —— 物件本身或任何上層物件只要有下列之一就排除：
///     Rigidbody、NavMeshAgent、Animator、Animation、CharacterController、粒子系統、攝影機、
///     「會移動自己」的腳本（自動掃描腳本原始碼判斷，例如 transform.position = …、MovePosition、Translate）、
///     車輛／玩家標籤、車輛圖層、你自己填的名稱關鍵字。
///   每次套用都會存一份還原紀錄，隨時可以一鍵改回。
///
/// 建議流程：① 預覽 → 在 Scene 視窗確認選到的物件 → ② 套用 → ③ 烘焙 → Play 比較 Stats
/// </summary>
public class StaticOcclusionSetupWindow : EditorWindow
{
    // ── 設定 ──
    private float minOccluderSize = 4f;
    private float minOccluderHeight = 2.5f;
    private bool markBatchingStatic = false;
    // ⚠ 不放「車」「UI」這類短字：「立體停車場」含「車」、「BUILDING」含「UI」，會把建築誤排除。
    //   車輛本來就會被 Rigidbody／圖層／標籤排除，不需要靠名稱。
    private string extraExcludeKeywords = "行人,NPC,後照,倒車鏡,觸發,Trigger";
    private Vector2 scroll;

    // ── 掃描結果 ──
    private List<GameObject> candidates = new List<GameObject>();
    private HashSet<GameObject> occluders = new HashSet<GameObject>();
    private Dictionary<string, int> excludedByReason = new Dictionary<string, int>();
    private Dictionary<string, List<string>> excludedSamples = new Dictionary<string, List<string>>();
    private int alreadyStatic;
    private List<string> hugeMeshes = new List<string>();
    private bool scanned;

    // 快取
    private readonly Dictionary<Transform, string> excludeCache = new Dictionary<Transform, string>();
    private static readonly Dictionary<Type, bool> moverScriptCache = new Dictionary<Type, bool>();

    private const string RecordPath = "Assets/script/效能優化/靜態標記還原紀錄.asset";

    private static readonly string[] VehicleTags = { "Player", "Car", "WhiteCar", "NPC" };
    private static readonly string[] VehicleLayers = { "TrafficLight", "AI_Car", "Car", "UI", "Minimap" };

    // 會移動自己的腳本：原始碼裡出現這些寫法就視為「會動」
    private static readonly Regex MoverPattern = new Regex(
        @"transform\.(position|rotation|localPosition|localRotation|eulerAngles|localEulerAngles)\s*[\+\-\*]?=(?!=)" +
        @"|\.Translate\s*\(|\.Rotate\s*\(|\.RotateAround\s*\(|\.LookAt\s*\(|SetPositionAndRotation\s*\(" +
        @"|MovePosition\s*\(|MoveRotation\s*\(|\.velocity\s*=|AddForce\s*\(|AddTorque\s*\(",
        RegexOptions.Compiled);

    [MenuItem("Tools/效能優化/靜態標記與遮擋剔除")]
    private static void Open()
    {
        var w = GetWindow<StaticOcclusionSetupWindow>("靜態標記與遮擋剔除");
        w.minSize = new Vector2(460, 520);
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("🏙 靜態標記 ＋ 遮擋剔除", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("只會標記「不會動」的建築、道路、街道物件。車輛、行人、路網、攝影機、會移動的腳本一律排除。\n" +
                                "建議：先按「① 預覽」，在 Scene 視窗確認選到的物件都是建築道路，再按「② 套用」。", MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("設定", EditorStyles.boldLabel);
        minOccluderSize = EditorGUILayout.FloatField(new GUIContent("遮擋者最小寬度 (m)", "夠大的物件才當「遮擋者」（會擋住別人），小東西只當「被遮擋者」。太小的遮擋者會讓烘焙變慢、效果也差"), minOccluderSize);
        minOccluderHeight = EditorGUILayout.FloatField(new GUIContent("遮擋者最小高度 (m)", "扁平的路面擋不住任何東西，不當遮擋者"), minOccluderHeight);
        markBatchingStatic = EditorGUILayout.Toggle(new GUIContent("同時標記靜態合批 (Batching Static)",
            "可以減少 Draw Call，但會額外占用記憶體（把網格合併）。你的筆電 16GB，建議先只做遮擋剔除，確認沒問題再開"), markBatchingStatic);
        EditorGUILayout.LabelField(new GUIContent("排除名稱關鍵字（逗號分隔）", "物件或上層物件名稱包含這些字就不標"));
        extraExcludeKeywords = EditorGUILayout.TextField(extraExcludeKeywords);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("步驟", EditorStyles.boldLabel);
        if (GUILayout.Button("① 預覽（不修改任何東西，只在 Console 列出結果）", GUILayout.Height(30))) Scan(false);
        using (new EditorGUI.DisabledScope(!scanned || candidates.Count == 0))
        {
            if (GUILayout.Button($"在 Hierarchy 選取預覽結果（{candidates.Count} 個，數量多時 Inspector 會卡幾秒）"))
                SelectCandidates();
        }

        using (new EditorGUI.DisabledScope(!scanned || candidates.Count == 0))
        {
            if (GUILayout.Button($"② 套用標記（{candidates.Count} 個物件，其中 {occluders.Count} 個當遮擋者）", GUILayout.Height(30))) Apply();
        }
        if (GUILayout.Button("↩ 還原上次標記")) Restore();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("③ 烘焙遮擋剔除", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox($"目前參數：最小遮擋者 {StaticOcclusionCulling.smallestOccluder:F1}m、最小孔洞 {StaticOcclusionCulling.smallestHole:F2}m、背面門檻 {StaticOcclusionCulling.backfaceThreshold:F0}\n" +
                                (StaticOcclusionCulling.isRunning ? "⏳ 烘焙中…（可以繼續做別的事）" :
                                 StaticOcclusionCulling.umbraDataSize > 0 ? $"✅ 已有烘焙資料（{StaticOcclusionCulling.umbraDataSize / 1024f:F0} KB）" : "尚未烘焙"), MessageType.None);
        if (GUILayout.Button("套用城市場景建議參數（最小遮擋者 5m、孔洞 0.25m、背面 100）"))
        {
            StaticOcclusionCulling.smallestOccluder = 5f;
            StaticOcclusionCulling.smallestHole = 0.25f;
            StaticOcclusionCulling.backfaceThreshold = 100f;
            Debug.Log("<color=cyan>🏙 [遮擋剔除] 已套用城市場景建議參數</color>");
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(StaticOcclusionCulling.isRunning))
                if (GUILayout.Button("開始烘焙（背景執行）", GUILayout.Height(26))) Bake();
            using (new EditorGUI.DisabledScope(!StaticOcclusionCulling.isRunning))
                if (GUILayout.Button("取消烘焙", GUILayout.Height(26))) StaticOcclusionCulling.Cancel();
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("開啟 Occlusion 視窗（看視覺化）")) EditorApplication.ExecuteMenuItem("Window/Rendering/Occlusion Culling");
            if (GUILayout.Button("清除烘焙資料")) { StaticOcclusionCulling.Clear(); Debug.Log("🏙 [遮擋剔除] 已清除烘焙資料"); }
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("🔍 檢查場景中的攝影機（遮擋剔除是否開啟、可視距離）")) ReportCameras();

        if (scanned) DrawScanSummary();
        EditorGUILayout.EndScrollView();

        if (StaticOcclusionCulling.isRunning) Repaint();
    }

    private void DrawScanSummary()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("預覽結果", EditorStyles.boldLabel);
        var sb = new StringBuilder();
        sb.AppendLine($"可標記：{candidates.Count} 個（遮擋者 {occluders.Count}、只被遮擋 {candidates.Count - occluders.Count}）");
        sb.AppendLine($"原本就已經是 Static：{alreadyStatic} 個");
        sb.AppendLine("排除：");
        foreach (var kv in excludedByReason.OrderByDescending(k => k.Value))
            sb.AppendLine($"  • {kv.Key}：{kv.Value} 個");
        if (hugeMeshes.Count > 0)
            sb.AppendLine($"⚠ 超大網格 {hugeMeshes.Count} 個（單一物件超過 200m，遮擋剔除對它幫助有限，建議在 Blender 切塊）");
        EditorGUILayout.HelpBox(sb.ToString(), MessageType.None);
    }

    // ====================================================================
    //  掃描
    // ====================================================================

    private void Scan(bool selectResult)
    {
        candidates.Clear(); occluders.Clear(); excludedByReason.Clear(); excludedSamples.Clear();
        excludeCache.Clear(); hugeMeshes.Clear(); alreadyStatic = 0;

        string[] keywords = extraExcludeKeywords.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();
        Scene scene = SceneManager.GetActiveScene();
        var seen = new HashSet<GameObject>();

        try
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                EditorUtility.DisplayProgressBar("靜態標記預覽", $"掃描 {roots[r].name}（{r + 1}/{roots.Length}）", r / (float)roots.Length);
                foreach (var mr in roots[r].GetComponentsInChildren<MeshRenderer>(true))
                {
                    GameObject go = mr.gameObject;
                    if (!seen.Add(go)) continue;
                    if (!go.activeInHierarchy || !mr.enabled) { Count("物件或渲染器是關閉的", go); continue; }
                    if (go.GetComponent<MeshFilter>() == null) continue;

                    string reason = ExcludeReason(go.transform, keywords);
                    if (reason != null) { Count(reason, go); continue; }

                    var flags = GameObjectUtility.GetStaticEditorFlags(go);
                    if ((flags & StaticEditorFlags.OccludeeStatic) != 0) alreadyStatic++;

                    candidates.Add(go);
                    Vector3 size = mr.bounds.size;
                    if (size.y >= minOccluderHeight && Mathf.Max(size.x, size.z) >= minOccluderSize) occluders.Add(go);
                    if (Mathf.Max(size.x, size.z) > 200f) hugeMeshes.Add($"{PathOf(go.transform)}（{size.x:F0}×{size.z:F0}m）");
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        scanned = true;

        // Log：讓你知道每一類排除的是哪些物件
        var sb = new StringBuilder();
        sb.AppendLine($"<color=cyan>🏙 [靜態標記預覽] 場景「{scene.name}」：可標記 {candidates.Count} 個（遮擋者 {occluders.Count}），已是 Static {alreadyStatic} 個</color>");
        foreach (var kv in excludedByReason.OrderByDescending(k => k.Value))
            sb.AppendLine($"  排除「{kv.Key}」{kv.Value} 個，例如：{string.Join("、", excludedSamples[kv.Key].ToArray())}");
        var sampleNames = candidates.Take(15).Select(g => PathOf(g.transform)).ToArray();
        sb.AppendLine("  可標記的例子：" + string.Join("、", sampleNames));
        foreach (var h in hugeMeshes.Take(10)) sb.AppendLine("  ⚠ 超大網格：" + h);
        Debug.Log(sb.ToString());

        if (selectResult) SelectCandidates();
        Repaint();
    }

    private void SelectCandidates()
    {
        Selection.objects = candidates.Where(g => g != null).Cast<UnityEngine.Object>().ToArray();
        Debug.Log("<color=cyan>🏙 已在 Hierarchy 選取所有可標記物件。到 Scene 視窗看看有沒有不該標的（例如會動的門、旋轉招牌）。" +
                  "有的話把它的名稱加進「排除名稱關鍵字」再預覽一次</color>");
    }

    private void Count(string reason, GameObject go)
    {
        int n;
        excludedByReason.TryGetValue(reason, out n);
        excludedByReason[reason] = n + 1;
        List<string> list;
        if (!excludedSamples.TryGetValue(reason, out list)) { list = new List<string>(); excludedSamples[reason] = list; }
        if (list.Count < 5) list.Add(PathOf(go.transform));
    }

    /// <summary>自己或任何上層物件符合「會動」條件就回傳原因；安全就回傳 null。結果會快取。</summary>
    private string ExcludeReason(Transform t, string[] keywords)
    {
        if (t == null) return null;
        string cached;
        if (excludeCache.TryGetValue(t, out cached)) return cached;

        string reason = SelfReason(t, keywords);
        if (reason == null && t.parent != null) reason = ExcludeReason(t.parent, keywords);
        excludeCache[t] = reason;
        return reason;
    }

    private string SelfReason(Transform t, string[] keywords)
    {
        GameObject go = t.gameObject;
        if (go.GetComponent<Rigidbody>() != null) return "有 Rigidbody（車輛、會被推動的物件）";
        if (go.GetComponent<NavMeshAgent>() != null) return "有 NavMeshAgent";
        if (go.GetComponent<Animator>() != null) return "有 Animator（動畫）";
        if (go.GetComponent<Animation>() != null) return "有 Animation";
        if (go.GetComponent<CharacterController>() != null) return "有 CharacterController";
        if (go.GetComponent<ParticleSystem>() != null) return "有粒子系統";
        if (go.GetComponent<Camera>() != null) return "是攝影機（後照鏡等會跟著車動）";
        if (go.GetComponent<SkinnedMeshRenderer>() != null) return "有 SkinnedMeshRenderer（角色）";

        foreach (string tag in VehicleTags)
            if (go.CompareTagSafe(tag)) return $"標籤是 {tag}";
        string layer = LayerMask.LayerToName(go.layer);
        foreach (string l in VehicleLayers)
            if (layer == l) return $"圖層是 {l}（這個專案的車輛放在 TrafficLight 圖層）";

        foreach (var mb in go.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            if (IsMoverScript(mb)) return $"有會移動自己的腳本（{mb.GetType().Name}）";
        }

        foreach (string k in keywords)
            if (go.name.IndexOf(k, StringComparison.Ordinal) >= 0) return $"名稱包含「{k}」";

        return null;
    }

    /// <summary>掃描腳本原始碼，看它會不會移動自己（快取，每種腳本只讀一次）</summary>
    private static bool IsMoverScript(MonoBehaviour mb)
    {
        Type type = mb.GetType();
        bool result;
        if (moverScriptCache.TryGetValue(type, out result)) return result;

        result = false;
        MonoScript script = MonoScript.FromMonoBehaviour(mb);
        string path = script != null ? AssetDatabase.GetAssetPath(script) : null;
        // 只看專案裡自己寫的腳本；Unity 內建與套件（UI、TextMeshPro…）不算
        if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/") && script.text != null)
            result = MoverPattern.IsMatch(script.text);
        moverScriptCache[type] = result;
        return result;
    }

    // ====================================================================
    //  套用 / 還原
    // ====================================================================

    private void Apply()
    {
        Scan(false);
        if (candidates.Count == 0) return;
        if (!EditorUtility.DisplayDialog("靜態標記",
                $"即將把 {candidates.Count} 個物件標成 Static：\n" +
                $"• 遮擋者＋被遮擋：{occluders.Count} 個\n• 只被遮擋：{candidates.Count - occluders.Count} 個\n" +
                (markBatchingStatic ? "• 同時標記靜態合批\n" : "") +
                "\n會存還原紀錄，可以隨時改回。要繼續嗎？", "套用", "取消"))
            return;

        var record = ScriptableObject.CreateInstance<StaticMarkRecord>();
        record.sceneName = SceneManager.GetActiveScene().name;
        record.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        Undo.SetCurrentGroupName("靜態標記");
        int group = Undo.GetCurrentGroup();
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i % 200 == 0) EditorUtility.DisplayProgressBar("靜態標記", $"{i}/{candidates.Count}", i / (float)candidates.Count);
                GameObject go = candidates[i];
                StaticEditorFlags before = GameObjectUtility.GetStaticEditorFlags(go);
                StaticEditorFlags add = StaticEditorFlags.OccludeeStatic;
                if (occluders.Contains(go)) add |= StaticEditorFlags.OccluderStatic;
                if (markBatchingStatic) add |= StaticEditorFlags.BatchingStatic;
                if ((before & add) == add) continue;

                record.entries.Add(new StaticMarkRecord.Entry
                {
                    id = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                    originalFlags = (int)before,
                    path = PathOf(go.transform)
                });
                Undo.RecordObject(go, "靜態標記");
                GameObjectUtility.SetStaticEditorFlags(go, before | add);
            }
        }
        finally { EditorUtility.ClearProgressBar(); }
        Undo.CollapseUndoOperations(group);

        SaveRecord(record);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"<color=lime>🏙 [靜態標記] 已標記 {record.entries.Count} 個物件，還原紀錄存在 {RecordPath}。請按 Ctrl+S 存檔場景，再進行 ③ 烘焙</color>");
    }

    private void Restore()
    {
        var record = AssetDatabase.LoadAssetAtPath<StaticMarkRecord>(RecordPath);
        if (record == null || record.entries.Count == 0) { EditorUtility.DisplayDialog("還原", "找不到還原紀錄（還沒套用過標記）", "好"); return; }
        if (!EditorUtility.DisplayDialog("還原", $"把 {record.time} 標記的 {record.entries.Count} 個物件改回原本的 Static 設定？", "還原", "取消")) return;

        int ok = 0, missing = 0;
        try
        {
            for (int i = 0; i < record.entries.Count; i++)
            {
                if (i % 200 == 0) EditorUtility.DisplayProgressBar("還原", $"{i}/{record.entries.Count}", i / (float)record.entries.Count);
                var e = record.entries[i];
                GlobalObjectId gid;
                GameObject go = GlobalObjectId.TryParse(e.id, out gid)
                    ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject : null;
                if (go == null) { missing++; continue; }
                Undo.RecordObject(go, "還原靜態標記");
                GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)e.originalFlags);
                ok++;
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"<color=orange>↩ [靜態標記] 已還原 {ok} 個物件{(missing > 0 ? $"，{missing} 個找不到（可能已刪除或場景沒開）" : "")}。請按 Ctrl+S 存檔；若已烘焙過遮擋資料，建議按「清除烘焙資料」</color>");
    }

    private static void SaveRecord(StaticMarkRecord record)
    {
        string folder = System.IO.Path.GetDirectoryName(RecordPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(folder))
        {
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }
        AssetDatabase.DeleteAsset(RecordPath);
        AssetDatabase.CreateAsset(record, RecordPath);
        AssetDatabase.SaveAssets();
    }

    // ====================================================================
    //  烘焙 / 攝影機
    // ====================================================================

    private void Bake()
    {
        if (EditorSceneManager.GetActiveScene().isDirty &&
            !EditorUtility.DisplayDialog("烘焙", "場景還沒存檔。烘焙前建議先按 Ctrl+S，要繼續嗎？", "繼續烘焙", "取消"))
            return;
        bool started = StaticOcclusionCulling.GenerateInBackground();
        Debug.Log(started
            ? "<color=cyan>⏳ [遮擋剔除] 開始在背景烘焙。場景很大，可能需要幾分鐘到幾十分鐘；完成後按 Ctrl+S 存檔</color>"
            : "<color=red>[遮擋剔除] 烘焙沒有啟動（可能已經在烘焙中）</color>");
    }

    private void ReportCameras()
    {
        var sb = new StringBuilder("<color=cyan>🎥 [攝影機檢查]</color>\n");
        foreach (var cam in FindObjectsOfType<Camera>(true))
        {
            string target = cam.targetTexture != null ? $"→ 輸出到 {cam.targetTexture.name}" : "→ 畫面";
            sb.AppendLine($"  {PathOf(cam.transform)}｜{(cam.gameObject.activeInHierarchy && cam.enabled ? "啟用" : "關閉")}｜" +
                          $"遮擋剔除 {(cam.useOcclusionCulling ? "開" : "關")}｜可視距離 {cam.farClipPlane:F0}m｜{target}");
        }
        Debug.Log(sb.ToString());
    }

    private static string PathOf(Transform t)
    {
        var parts = new List<string>();
        int guard = 0;
        while (t != null && guard++ < 6) { parts.Add(t.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }
}

/// <summary>CompareTag 碰到「專案沒定義的標籤」會丟例外；這裡安全地比對</summary>
internal static class GameObjectTagExtensions
{
    public static bool CompareTagSafe(this GameObject go, string tag)
    {
        try { return go.CompareTag(tag); }
        catch { return false; }
    }
}
