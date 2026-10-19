using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;

using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;

/// <summary>
/// 🧱 ProBuilder 轉一般網格（消除生成車輛時的卡頓）
///
/// 【問題】
///   效能監測器抓到的 38 次卡頓，100% 都發生在「機車生成」那一幀，每生一台機車就卡約 125ms。
///   原因：機車 prefab 是用 ProBuilder 做的，而 prefab 裡「沒有存網格」，只存 ProBuilder 的原始面資料
///   （Moto(black).prefab 有 17MB，其中車身一個元件就占 12.6MB）。
///   每次 Instantiate，Unity 要先複製這 17MB 的資料，接著 ProBuilderMesh.Awake() 發現網格是空的，
///   就當場從面資料重建整台車的網格 —— 這兩件事加起來就是那 125ms。
///
/// 【這個工具做的事】
///   把 prefab 裡每個 ProBuilderMesh 的網格「烤成一般的 Mesh 檔案（.asset）」，
///   MeshFilter 改指向那個檔案，然後移除 ProBuilderMesh 元件。
///   畫面上完全一樣，但生成時不用再重建網格，prefab 也會從 17MB 變成幾十 KB。
///
/// 【代價】
///   轉換後這些 prefab 不能再用 ProBuilder 編輯（要改造型的話用備份還原）。
///   執行前會自動把原始 prefab 備份到專案根目錄的「_備份_ProBuilder轉換_時間」資料夾（不在 Assets 裡，不會被匯入），
///   視窗下方有「從最近一次備份還原」按鈕。
/// </summary>
public class ProBuilderPrefabConverter : EditorWindow
{
    private const string DefaultMeshFolder = "Assets/script/效能優化/轉換網格";

    private class Entry
    {
        public string path;
        public int pbCount;
        public long bytes;
        public bool selected;
        public readonly List<string> referencedBy = new List<string>();
        public int sceneInstances;
    }

    private List<Entry> entries = new List<Entry>();
    private Vector2 scroll;
    private string meshFolder = DefaultMeshFolder;
    private bool optimizeMesh = true;
    private bool onlyReferenced = false;
    private string lastReport = "";

    [MenuItem("Tools/效能優化/ProBuilder 轉一般網格（消除生成卡頓）")]
    private static void Open()
    {
        var w = GetWindow<ProBuilderPrefabConverter>("ProBuilder 轉網格");
        w.minSize = new Vector2(620, 420);
        w.Scan();
    }

    // ====================================================================
    //  介面
    // ====================================================================

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "把 prefab 裡的 ProBuilder 物件烤成一般網格。生成車輛時就不用再重建網格，卡頓會消失。\n" +
            "預設只勾選「場景裡有腳本引用」的 prefab（也就是會被生成器 Instantiate 的車輛）。\n" +
            "執行前會自動備份原始 prefab 到專案根目錄，不放心可以隨時還原。", MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("🔍 重新掃描", GUILayout.Height(24))) Scan();
            onlyReferenced = GUILayout.Toggle(onlyReferenced, "只顯示被腳本引用的", GUILayout.Width(150));
        }

        meshFolder = EditorGUILayout.TextField(new GUIContent("網格存放資料夾", "轉換出來的 .asset 網格存在這裡"), meshFolder);
        optimizeMesh = EditorGUILayout.Toggle(new GUIContent("合併重複頂點（建議）", "用 ProBuilder 的 Optimize 合併共用頂點，網格更小"), optimizeMesh);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("轉換", EditorStyles.boldLabel, GUILayout.Width(36));
            GUILayout.Label("大小", EditorStyles.boldLabel, GUILayout.Width(70));
            GUILayout.Label("PB 數", EditorStyles.boldLabel, GUILayout.Width(44));
            GUILayout.Label("場景擺放", EditorStyles.boldLabel, GUILayout.Width(60));
            GUILayout.Label("Prefab ／ 被誰引用", EditorStyles.boldLabel);
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (var e in entries)
        {
            if (onlyReferenced && e.referencedBy.Count == 0) continue;
            using (new EditorGUILayout.HorizontalScope())
            {
                e.selected = GUILayout.Toggle(e.selected, "", GUILayout.Width(36));
                GUILayout.Label(FormatBytes(e.bytes), GUILayout.Width(70));
                GUILayout.Label(e.pbCount.ToString(), GUILayout.Width(44));
                GUILayout.Label(e.sceneInstances.ToString(), GUILayout.Width(60));
                string refs = e.referencedBy.Count > 0
                    ? "  ← " + string.Join("、", e.referencedBy.Distinct().Take(3)) + (e.referencedBy.Distinct().Count() > 3 ? "…" : "")
                    : "";
                if (GUILayout.Button(e.path.Replace("Assets/", "") + refs, EditorStyles.label))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(e.path));
            }
        }
        EditorGUILayout.EndScrollView();

        int selCount = entries.Count(x => x.selected);
        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(selCount == 0))
        {
            GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button($"🧱 轉換勾選的 {selCount} 個 prefab（會先自動備份）", GUILayout.Height(32)))
            {
                if (EditorUtility.DisplayDialog("ProBuilder 轉一般網格",
                        $"要轉換 {selCount} 個 prefab 嗎？\n\n原始檔會先備份到專案根目錄。\n轉換後這些 prefab 不能再用 ProBuilder 編輯造型。",
                        "轉換", "取消"))
                    ConvertSelected();
            }
            GUI.backgroundColor = Color.white;
        }

        if (GUILayout.Button("↩ 從最近一次備份還原", GUILayout.Height(22))) RestoreLatestBackup();

        if (!string.IsNullOrEmpty(lastReport))
            EditorGUILayout.HelpBox(lastReport, MessageType.None);
    }

    // ====================================================================
    //  掃描
    // ====================================================================

    private void Scan()
    {
        entries.Clear();

        // 1. 場景裡腳本欄位引用到的 prefab（= 會被生成的車輛）
        var refMap = new Dictionary<string, List<string>>();
        var instCount = new Dictionary<string, int>();
        for (int s = 0; s < EditorSceneManager.sceneCount; s++)
        {
            var scene = EditorSceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string ns = mb.GetType().Namespace ?? "";
                    if (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro") || ns.StartsWith("Unity.")) continue;
                    CollectPrefabRefs(mb, refMap);
                }
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) continue;
                    string p = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
                    if (string.IsNullOrEmpty(p)) continue;
                    instCount.TryGetValue(p, out int c);
                    instCount[p] = c + 1;
                }
            }
        }

        // 2. 所有含 ProBuilderMesh 的 prefab
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (i % 20 == 0 &&
                    EditorUtility.DisplayCancelableProgressBar("掃描 prefab", path, (float)i / guids.Length)) break;

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                int n = go.GetComponentsInChildren<ProBuilderMesh>(true).Length;
                if (n == 0) continue;

                var e = new Entry { path = path, pbCount = n, bytes = SafeFileSize(path) };
                if (refMap.TryGetValue(path, out var refs)) e.referencedBy.AddRange(refs);
                instCount.TryGetValue(path, out e.sceneInstances);
                e.selected = e.referencedBy.Count > 0;
                entries.Add(e);
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        entries = entries.OrderByDescending(e => e.referencedBy.Count > 0).ThenByDescending(e => e.bytes).ToList();
        Debug.Log($"<color=#ffb74d>🧱 [ProBuilder 轉網格] 找到 {entries.Count} 個含 ProBuilder 的 prefab，" +
                  $"其中 {entries.Count(e => e.referencedBy.Count > 0)} 個被場景腳本引用（會被生成）</color>");
        Repaint();
    }

    private static void CollectPrefabRefs(MonoBehaviour mb, Dictionary<string, List<string>> refMap)
    {
        var so = new SerializedObject(mb);
        var it = so.GetIterator();
        bool enter = true;
        int guard = 0;
        while (it.Next(enter) && guard++ < 20000)
        {
            // 大型數值陣列（例如路徑點座標）不用往下走
            enter = it.propertyType == SerializedPropertyType.Generic && !(it.isArray && it.arraySize > 500);
            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
            var obj = it.objectReferenceValue as GameObject;
            if (obj == null || !PrefabUtility.IsPartOfPrefabAsset(obj)) continue;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) continue;
            if (!refMap.TryGetValue(path, out var list)) refMap[path] = list = new List<string>();
            string label = $"{mb.GetType().Name}.{it.propertyPath.Split('.')[0]}";
            if (!list.Contains(label)) list.Add(label);
        }
    }

    // ====================================================================
    //  轉換
    // ====================================================================

    private void ConvertSelected()
    {
        var selected = entries.Where(e => e.selected).Select(e => e.path).ToList();

        // 巢狀 prefab：ProBuilder 元件可能其實屬於「來源 prefab」，要先轉換來源，子 prefab 才會跟著乾淨
        var order = BuildProcessingOrder(selected, out var addedSources);

        string backupDir = BackupPrefabs(order);
        if (backupDir == null) return;

        EnsureFolder(meshFolder);
        var hashToMesh = new Dictionary<string, Mesh>();
        var sb = new StringBuilder();
        int totalPb = 0, totalMeshes = 0, reused = 0;
        long before = order.Sum(SafeFileSize);

        try
        {
            // 不用 StartAssetEditing 批次匯入：巢狀 prefab 要等來源 prefab 存檔、重新匯入後才能正確處理
            for (int i = 0; i < order.Count; i++)
            {
                string path = order[i];
                EditorUtility.DisplayProgressBar("ProBuilder 轉一般網格", path, (float)i / order.Count);
                ConvertOne(path, hashToMesh, sb, ref totalPb, ref totalMeshes, ref reused);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"🧱 [ProBuilder 轉網格] 轉換中斷：{ex}\n原始 prefab 已備份在：{backupDir}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        long after = order.Sum(SafeFileSize);
        lastReport =
            $"✅ 轉換完成：{order.Count} 個 prefab、{totalPb} 個 ProBuilder 元件 → {totalMeshes} 個網格檔（重複使用 {reused} 個）\n" +
            $"prefab 總大小 {FormatBytes(before)} → {FormatBytes(after)}\n" +
            (addedSources.Count > 0 ? $"另外自動加入了來源 prefab：{string.Join("、", addedSources.Select(Path.GetFileName))}\n" : "") +
            $"備份：{backupDir}";
        Debug.Log("<color=#81c784>🧱 [ProBuilder 轉網格] " + lastReport + "\n" + sb + "</color>");
        Scan();
    }

    private void ConvertOne(string path, Dictionary<string, Mesh> hashToMesh, StringBuilder sb,
                            ref int totalPb, ref int totalMeshes, ref int reused)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        int done = 0, skippedNested = 0, failed = 0;
        string prefabName = Path.GetFileNameWithoutExtension(path);
        try
        {
            foreach (var pb in root.GetComponentsInChildren<ProBuilderMesh>(true))
            {
                if (pb == null) continue;
                // 屬於巢狀來源 prefab 的元件，由來源 prefab 負責轉換
                if (PrefabUtility.IsPartOfPrefabInstance(pb)) { skippedNested++; continue; }

                GameObject go = pb.gameObject;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null) { failed++; Debug.LogWarning($"🧱 {prefabName}/{GetPath(go.transform, root.transform)} 沒有 MeshFilter，略過", go); continue; }

                // 1. 確保網格是最新的
                try
                {
                    pb.ToMesh();
                    pb.Refresh();
                    if (optimizeMesh) UnityEditor.ProBuilder.EditorMeshUtility.Optimize(pb);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"🧱 {prefabName}/{go.name} 重建網格時出錯，改用目前的網格：{ex.Message}", go);
                }

                Mesh live = mf.sharedMesh;
                if (live == null || live.vertexCount == 0)
                {
                    failed++;
                    Debug.LogWarning($"🧱 {prefabName}/{GetPath(go.transform, root.transform)} 產生不出網格，保留 ProBuilder 元件不動", go);
                    continue;
                }

                // 2. 存成 .asset（內容完全相同的網格只存一份，例如四種顏色的機車共用同一組輪子）
                string hash = MeshHash(live);
                if (!hashToMesh.TryGetValue(hash, out Mesh asset))
                {
                    asset = Instantiate(live);
                    asset.name = $"{prefabName}_{go.name}";
                    string file = AssetDatabase.GenerateUniqueAssetPath($"{meshFolder}/{SafeFileName(prefabName)}_{SafeFileName(go.name)}.asset");
                    AssetDatabase.CreateAsset(asset, file);
                    hashToMesh[hash] = asset;
                    totalMeshes++;
                }
                else reused++;

                var mc = go.GetComponent<MeshCollider>();
                bool colliderUsedPb = mc != null && (mc.sharedMesh == null || mc.sharedMesh == live);

                // 3. 移除依賴 ProBuilderMesh 的元件（例如 BezierShape），再移除 ProBuilderMesh 本身
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null || comp == pb) continue;
                    bool requiresPb = comp.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                        .Cast<RequireComponent>()
                        .Any(r => r.m_Type0 == typeof(ProBuilderMesh) || r.m_Type1 == typeof(ProBuilderMesh) || r.m_Type2 == typeof(ProBuilderMesh));
                    if (requiresPb) DestroyImmediate(comp);
                }
                // ⚠ 一定要先設這個：否則 ProBuilderMesh 被刪除時會連 MeshFilter 上的網格一起刪掉
                pb.preserveMeshAssetOnDestroy = true;
                DestroyImmediate(pb);

                // 4. 指向剛存好的網格
                mf.sharedMesh = asset;
                mf.hideFlags = HideFlags.None;
                if (colliderUsedPb) mc.sharedMesh = asset;
                if (!AssetDatabase.Contains(live)) DestroyImmediate(live);

                done++;
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        totalPb += done;
        sb.AppendLine($"  {prefabName}：轉換 {done} 個" +
                      (skippedNested > 0 ? $"、巢狀 {skippedNested} 個由來源 prefab 處理" : "") +
                      (failed > 0 ? $"、⚠ 失敗 {failed} 個（保留原樣）" : ""));
    }

    /// <summary>來源 prefab 先處理，子 prefab 後處理</summary>
    private static List<string> BuildProcessingOrder(List<string> selected, out List<string> addedSources)
    {
        var deps = new Dictionary<string, HashSet<string>>();
        var queue = new Queue<string>(selected);
        addedSources = new List<string>();
        while (queue.Count > 0)
        {
            string p = queue.Dequeue();
            if (deps.ContainsKey(p)) continue;
            var set = new HashSet<string>();
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            if (go != null)
            {
                foreach (var pb in go.GetComponentsInChildren<ProBuilderMesh>(true))
                {
                    var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(pb);
                    string sp = src != null ? AssetDatabase.GetAssetPath(src) : null;
                    if (!string.IsNullOrEmpty(sp) && sp != p)
                    {
                        set.Add(sp);
                        if (!selected.Contains(sp) && !addedSources.Contains(sp)) addedSources.Add(sp);
                        queue.Enqueue(sp);
                    }
                }
            }
            deps[p] = set;
        }

        var order = new List<string>();
        var done = new HashSet<string>();
        for (int guard = 0; guard < 50 && order.Count < deps.Count; guard++)
            foreach (var kv in deps)
                if (!done.Contains(kv.Key) && kv.Value.All(done.Contains)) { order.Add(kv.Key); done.Add(kv.Key); }
        order.AddRange(deps.Keys.Where(k => !done.Contains(k)));   // 理論上不會發生（循環引用）
        return order;
    }

    // ====================================================================
    //  備份／還原
    // ====================================================================

    private static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

    private static string BackupPrefabs(List<string> paths)
    {
        string dir = Path.Combine(ProjectRoot, $"_備份_ProBuilder轉換_{DateTime.Now:MMddHHmmss}");
        try
        {
            foreach (string p in paths)
            {
                foreach (string f in new[] { p, p + ".meta" })
                {
                    string src = Path.Combine(ProjectRoot, f);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(dir, f);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
            }
            Debug.Log($"<color=#ffb74d>🧱 [ProBuilder 轉網格] 已備份 {paths.Count} 個 prefab → {dir}</color>");
            return dir;
        }
        catch (Exception ex)
        {
            EditorUtility.DisplayDialog("備份失敗", $"備份失敗，已取消轉換：\n{ex.Message}", "確定");
            return null;
        }
    }

    private void RestoreLatestBackup()
    {
        var dirs = Directory.GetDirectories(ProjectRoot, "_備份_ProBuilder轉換_*").OrderByDescending(d => d).ToList();
        if (dirs.Count == 0) { EditorUtility.DisplayDialog("還原", "找不到任何備份。", "確定"); return; }
        string dir = dirs[0];
        var files = Directory.GetFiles(dir, "*.prefab", SearchOption.AllDirectories);
        if (!EditorUtility.DisplayDialog("從備份還原",
                $"要把 {files.Length} 個 prefab 還原成轉換前的 ProBuilder 版本嗎？\n\n備份：{Path.GetFileName(dir)}\n" +
                "（轉換出來的網格檔會留在網格資料夾，不影響使用，可自行刪除）", "還原", "取消")) return;

        foreach (string f in files)
        {
            string rel = f.Substring(dir.Length + 1);
            File.Copy(f, Path.Combine(ProjectRoot, rel), true);
            if (File.Exists(f + ".meta")) File.Copy(f + ".meta", Path.Combine(ProjectRoot, rel + ".meta"), true);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        lastReport = $"↩ 已從 {Path.GetFileName(dir)} 還原 {files.Length} 個 prefab";
        Debug.Log("<color=#ffb74d>🧱 [ProBuilder 轉網格] " + lastReport + "</color>");
        Scan();
    }

    // ====================================================================
    //  小工具
    // ====================================================================

    private static string MeshHash(Mesh m)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(m.subMeshCount);
            foreach (var v in m.vertices) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
            foreach (var v in m.normals) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
            foreach (var v in m.uv) { w.Write(v.x); w.Write(v.y); }
            foreach (var c in m.colors) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
            for (int s = 0; s < m.subMeshCount; s++)
            {
                w.Write(-1);
                foreach (int idx in m.GetIndices(s)) w.Write(idx);
            }
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(ms.ToArray()));
        }
    }

    private static void EnsureFolder(string folder)
    {
        folder = folder.TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static string GetPath(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (; t != null && t != root; t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string SafeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace('/', '_').Replace(' ', '_');
    }

    private static long SafeFileSize(string assetPath)
    {
        try { return new FileInfo(Path.Combine(ProjectRoot, assetPath)).Length; }
        catch { return 0; }
    }

    private static string FormatBytes(long b) =>
        b >= 1 << 20 ? $"{b / 1048576f:F1} MB" : $"{b / 1024f:F0} KB";
}
