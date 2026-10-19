#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;

/// <summary>
/// 一鍵建立／升級鬼切強化學習訓練場。
///   ● Tools > 危險駕駛AI > 建立鬼切強化學習訓練場（8 組）：在空場景建立全新的 v2 訓練場
///   ● Tools > 危險駕駛AI > 訓練場：升級到 v2（套用城市路網樣本）：把現有的訓練場（鬼切RL訓練.unity）升級
/// </summary>
public static class GhostCutInTrainingSetup
{
    private const int ArenaCount = 8;
    private const float Spacing = 30f;

    [MenuItem("Tools/危險駕駛AI/建立鬼切強化學習訓練場（8 組）")]
    private static void Create()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.name == "cc" || scene.path.EndsWith("cc.unity"))
        {
            if (!EditorUtility.DisplayDialog("危險駕駛AI", "目前開著的是 cc.unity。\n訓練場應該建在另一個空場景（File > New Scene），確定要建在這裡嗎？", "還是要建", "取消"))
                return;
        }

        CityRoadSamples samples = FindSamples();
        var root = new GameObject("鬼切強化學習訓練場");
        Undo.RegisterCreatedObjectUndo(root, "建立鬼切強化學習訓練場");

        for (int i = 0; i < ArenaCount; i++) CreateArena(root.transform, i, samples, true);

        // 俯視攝影機看第一組
        var camGo = new GameObject("訓練場攝影機");
        Undo.RegisterCreatedObjectUndo(camGo, "建立訓練場攝影機");
        var cam = camGo.AddComponent<Camera>();
        // 新場景（Basic）本來就有 Main Camera 附 Audio Listener，再加一個會跳「2 audio listeners」警告
        if (Object.FindObjectOfType<AudioListener>() == null) camGo.AddComponent<AudioListener>();
        camGo.transform.position = new Vector3(0f, 45f, -10f);
        camGo.transform.rotation = Quaternion.Euler(70f, 0f, 0f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.14f, 0.16f);

        if (Object.FindObjectOfType<Light>() == null)
        {
            var lightGo = new GameObject("方向光");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Undo.RegisterCreatedObjectUndo(lightGo, "建立方向光");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root;
        Debug.Log($"<color=cyan>🤖 [鬼切RL] 已建立 {ArenaCount} 組 v2 訓練場（觀測 {GhostCutInRL.ObservationSize} 個、" +
                  $"{(samples != null ? $"城市路網樣本 {samples.samples.Count} 筆" : "⚠ 沒有城市路網樣本")}）。請存檔場景再啟動訓練</color>");
    }

    /// <summary>建立一組訓練場（第 index 組，從 0 開始）。每排 8 組，間隔 30m；第 9 組起往後排</summary>
    private static CutInTrainingArena CreateArena(Transform root, int index, CityRoadSamples samples, bool visuals)
    {
        var arenaGo = new GameObject($"訓練場_{index + 1:00}");
        Undo.RegisterCreatedObjectUndo(arenaGo, "建立訓練場");
        arenaGo.transform.SetParent(root, false);
        arenaGo.transform.localPosition = new Vector3((index % 8) * Spacing, 0f, (index / 8) * 200f);
        var arena = arenaGo.AddComponent<CutInTrainingArena>();
        arena.roadSamples = samples;
        arena.buildVisuals = visuals;

        var agentGo = new GameObject("鬼切學習車（Agent）");
        agentGo.transform.SetParent(arenaGo.transform, false);

        var bp = agentGo.AddComponent<BehaviorParameters>();
        bp.BehaviorName = "GhostCutIn";
        bp.BrainParameters.VectorObservationSize = GhostCutInRL.ObservationSize;
        bp.BrainParameters.NumStackedVectorObservations = 1;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(GhostCutInRL.ActionSize);
        bp.BehaviorType = BehaviorType.Default;

        var agent = agentGo.AddComponent<GhostCutInAgent>();
        agent.arena = arena;
        agent.MaxStep = 1500;

        var dr = agentGo.AddComponent<DecisionRequester>();
        dr.DecisionPeriod = 5;
        dr.TakeActionsBetweenDecisions = true;
        return arena;
    }

    /// <summary>
    /// ⚡ 加速訓練：把訓練場擴充到 32 組。
    /// 訓練場是純數學模擬，一組只佔很少的 CPU，瓶頸在「每一步跟 Python 溝通一次」的固定開銷 ——
    /// 同一步裡有 32 台車一起回報，等於每次溝通收集 4 倍的經驗，訓練速度大約快 3～4 倍。
    /// 新增的訓練場不建立顯示用方塊（看前 8 組就夠了），省掉算繪。
    /// </summary>
    [MenuItem("Tools/危險駕駛AI/訓練場：擴充到 32 組（加速訓練）")]
    private static void ExpandTo32()
    {
        const int target = 32;
        var existing = Object.FindObjectsOfType<CutInTrainingArena>(true);
        if (existing.Length == 0)
        {
            EditorUtility.DisplayDialog("擴充訓練場", "這個場景沒有訓練場。請先開啟 Assets/Scenes/鬼切RL訓練.unity。", "確定");
            return;
        }
        if (existing.Length >= target)
        {
            Debug.Log($"<color=cyan>🤖 [鬼切RL] 已經有 {existing.Length} 組訓練場，不用擴充</color>");
            return;
        }

        Transform root = existing[0].transform.parent;
        if (root == null)
        {
            root = new GameObject("鬼切強化學習訓練場").transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, "建立訓練場根物件");
        }
        CityRoadSamples samples = existing[0].roadSamples != null ? existing[0].roadSamples : FindSamples();

        // 用名稱編號找出下一個空號，避免跟既有的「訓練場_01～08」重複
        var used = new System.Collections.Generic.HashSet<string>();
        foreach (var a in existing) used.Add(a.name);
        int added = 0, index = 0;
        while (existing.Length + added < target)
        {
            if (!used.Contains($"訓練場_{index + 1:00}")) { CreateArena(root, index, samples, false); added++; }
            index++;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"<color=cyan>🤖 [鬼切RL] 已新增 {added} 組訓練場，共 {existing.Length + added} 組（新增的不顯示方塊）。請存檔場景（Ctrl+S）</color>");
    }

    [MenuItem("Tools/危險駕駛AI/訓練場：升級到 v2（套用城市路網樣本）")]
    private static void UpgradeToV2()
    {
        CityRoadSamples samples = FindSamples();
        if (samples == null &&
            !EditorUtility.DisplayDialog("訓練場升級到 v2", "找不到「城市路網訓練樣本」。\n建議先開 cc.unity 執行 Tools > 危險駕駛AI > 匯出城市路網訓練樣本。\n\n還是要繼續升級（訓練場會改用隨機車道寬）？", "繼續", "取消"))
            return;

        int arenas = 0, agents = 0;
        foreach (var arena in Object.FindObjectsOfType<CutInTrainingArena>(true))
        {
            Undo.RecordObject(arena, "升級訓練場到 v2");
            arena.roadSamples = samples;
            EditorUtility.SetDirty(arena);
            arenas++;
        }
        foreach (var bp in Object.FindObjectsOfType<BehaviorParameters>(true))
        {
            if (bp.BehaviorName != "GhostCutIn") continue;
            Undo.RecordObject(bp, "升級訓練場到 v2");
            bp.BrainParameters.VectorObservationSize = GhostCutInRL.ObservationSize;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(GhostCutInRL.ActionSize);
            bp.Model = null;   // 第一版模型（12 個觀測值）不相容，拿掉避免推論錯誤
            bp.BehaviorType = BehaviorType.Default;
            EditorUtility.SetDirty(bp);
            agents++;
        }
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"<color=cyan>🤖 [鬼切RL] 已升級 {arenas} 組訓練場、{agents} 台學習車到 v2（觀測 {GhostCutInRL.ObservationSize} 個、" +
                  $"{(samples != null ? $"城市路網樣本 {samples.samples.Count} 筆" : "沒有城市路網樣本")}）。請存檔場景（Ctrl+S）</color>");
        if (arenas == 0) Debug.LogWarning("[鬼切RL] 這個場景沒有訓練場。請先開啟 Assets/Scenes/鬼切RL訓練.unity 再執行");
    }

    /// <summary>
    /// 📊 評估：所有學習車載入訓練好的模型、改成 Inference Only，按 Play 就會用模型跑，
    /// 每回合寫一行到「危險駕駛AI_強化學習評估_v2.csv」（模式 = 強化學習模型），跟規則式基準在同一批隨機情境下比較。
    /// </summary>
    [MenuItem("Tools/危險駕駛AI/訓練場：評估模式（載入最新的 GhostCutIn 模型）")]
    private static void SetEvaluation()
    {
        // 找名稱以 GhostCutIn 開頭、最新匯入的模型（拖進 Unity 時可能變成「GhostCutIn 1.onnx」）
        Unity.Barracuda.NNModel model = null;
        System.DateTime newest = System.DateTime.MinValue;
        foreach (string guid in AssetDatabase.FindAssets("t:NNModel"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!System.IO.Path.GetFileName(path).StartsWith("GhostCutIn")) continue;
            System.DateTime time = System.IO.File.GetLastWriteTime(path);
            if (time <= newest) continue;
            var m = AssetDatabase.LoadAssetAtPath<Unity.Barracuda.NNModel>(path);
            if (m == null) continue;
            model = m; newest = time;
        }
        if (model == null)
        {
            EditorUtility.DisplayDialog("評估模式", "找不到 GhostCutIn.onnx。請先把訓練好的模型放進 Assets/script/危險駕駛AI/機器學習/。", "確定");
            return;
        }

        int n = 0;
        foreach (var bp in Object.FindObjectsOfType<BehaviorParameters>(true))
        {
            if (bp.BehaviorName != "GhostCutIn") continue;
            Undo.RecordObject(bp, "評估模式");
            bp.Model = model;
            bp.BehaviorType = BehaviorType.InferenceOnly;
            EditorUtility.SetDirty(bp);
            n++;
        }
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"<color=cyan>📊 [鬼切RL] {n} 台學習車已載入「{AssetDatabase.GetAssetPath(model)}」並切成 Inference Only。按 Play 跑 2～3 分鐘即可比較</color>");
    }

    [MenuItem("Tools/危險駕駛AI/訓練場：全部切換成規則式基準（Heuristic）")]
    private static void SetHeuristic() { SetType(BehaviorType.HeuristicOnly); }

    [MenuItem("Tools/危險駕駛AI/訓練場：全部切換成訓練／模型模式（Default）")]
    private static void SetDefault() { SetType(BehaviorType.Default); }

    private static void SetType(BehaviorType t)
    {
        int n = 0;
        foreach (var bp in Object.FindObjectsOfType<BehaviorParameters>())
        {
            if (bp.BehaviorName != "GhostCutIn") continue;
            Undo.RecordObject(bp, "切換 Behavior Type");
            bp.BehaviorType = t;
            n++;
        }
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"<color=cyan>🤖 [鬼切RL] {n} 台學習車已切換成 {t}</color>");
    }

    private static CityRoadSamples FindSamples()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:CityRoadSamples"))
        {
            var s = AssetDatabase.LoadAssetAtPath<CityRoadSamples>(AssetDatabase.GUIDToAssetPath(guid));
            if (s != null) return s;
        }
        return null;
    }
}
#endif
