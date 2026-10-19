#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.MLAgents.Policies;

/// <summary>
/// ⚡ 打包鬼切強化學習訓練場（加速訓練用）
/// 選單：Tools > 危險駕駛AI > 打包訓練場（加速訓練用）
///
/// 為什麼打包會比較快：
///   在編輯器裡訓練，Unity 編輯器本身（Inspector、Scene 視窗、編輯器迴圈）就吃掉不少時間，而且一次只能跑一份。
///   打包成執行檔後，mlagents-learn 可以用 --no-graphics 不畫畫面、用 --num-envs=4 同時開 4 份，
///   4 × 32 = 128 台學習車一起收集經驗，大約快 2～3 倍。
///   訓練場是純數學模擬（固定每步 0.02 秒、不靠物理），所以平行份數與倍速都不會讓模擬變不準。
///
/// 這個選單會：
///   1. 只打包 Assets/Scenes/鬼切RL訓練.unity（不改動你遊戲本身的 Build Settings 場景清單）
///   2. 打包前把 32 台學習車切成 Default（訓練模式）並存檔
///   3. 打包時暫時關閉「啟動時初始化 VR」（訓練場不需要 VR，打包版沒有頭盔時會報錯），打包完自動還原
///   4. 輸出到 專案資料夾\TrainingBuild\GhostCutInTrain.exe
///      → 透過英文路徑 C:\AgentRL\TrainingBuild\ 執行（ML-Agents 的通訊元件遇到中文路徑會載入失敗）
///
/// ⚠ 改了訓練場的程式（CutInTrainingArena、GhostCutInAgent、GhostCutInRL…）之後，要重新打包一次才會生效。
/// </summary>
public static class GhostCutInTrainingBuild
{
    private const string ScenePath = "Assets/Scenes/鬼切RL訓練.unity";
    private const string BuildFolder = "TrainingBuild";
    private const string ExeName = "GhostCutInTrain.exe";

    [MenuItem("Tools/危險駕駛AI/打包訓練場（加速訓練用）")]
    private static void Build()
    {
        if (!File.Exists(ScenePath))
        {
            EditorUtility.DisplayDialog("打包訓練場", $"找不到 {ScenePath}。", "確定");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        string previousScene = EditorSceneManager.GetActiveScene().path;

        // ── 1. 開訓練場、切成訓練模式、存檔 ──
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int agents = 0, arenas = UnityEngine.Object.FindObjectsOfType<CutInTrainingArena>(true).Length;
        foreach (var bp in UnityEngine.Object.FindObjectsOfType<BehaviorParameters>(true))
        {
            if (bp.BehaviorName != "GhostCutIn") continue;
            bp.BehaviorType = BehaviorType.Default;
            bp.BrainParameters.VectorObservationSize = GhostCutInRL.ObservationSize;
            EditorUtility.SetDirty(bp);
            agents++;
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        if (agents == 0)
        {
            EditorUtility.DisplayDialog("打包訓練場", "訓練場場景裡沒有學習車（Behavior Name = GhostCutIn）。", "確定");
            return;
        }

        // ── 2. 暫時關閉「啟動時初始化 VR」──
        bool? xrWas = SetXRInitOnStart(false);

        // ── 3. 打包 ──
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string outDir = Path.Combine(projectRoot, BuildFolder);
        Directory.CreateDirectory(outDir);
        bool oldRunInBackground = PlayerSettings.runInBackground;
        PlayerSettings.runInBackground = true;

        BuildReport report = null;
        try
        {
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(outDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            report = BuildPipeline.BuildPlayer(options);
        }
        catch (Exception e)
        {
            Debug.LogError("[鬼切RL] 打包失敗：" + e.Message);
        }
        finally
        {
            PlayerSettings.runInBackground = oldRunInBackground;
            if (xrWas.HasValue) SetXRInitOnStart(xrWas.Value);
        }

        // ── 4. 回到原本的場景 ──
        if (!string.IsNullOrEmpty(previousScene) && previousScene != ScenePath && File.Exists(previousScene))
            EditorSceneManager.OpenScene(previousScene, OpenSceneMode.Single);

        if (report != null && report.summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"<color=cyan>⚡ [鬼切RL] 訓練場打包完成：{arenas} 組訓練場、{agents} 台學習車、觀測 {GhostCutInRL.ObservationSize} 個｜" +
                      $"{report.summary.totalSize / 1048576f:F0} MB、花了 {report.summary.totalTime.TotalSeconds:F0} 秒\n" +
                      $"→ C:\\AgentRL\\{BuildFolder}\\{ExeName}\n" +
                      "下一步：關掉 Unity（或不關也可以），雙擊 MLTraining\\4_打包版加速訓練.bat</color>");
            EditorUtility.RevealInFinder(Path.Combine(outDir, ExeName));
        }
        else if (report != null)
        {
            Debug.LogError($"[鬼切RL] 打包失敗（{report.summary.result}），錯誤 {report.summary.totalErrors} 個，請看 Console 上方的紅字");
        }
    }

    /// <summary>
    /// 設定 Standalone 平台的「Initialize XR on Startup」，回傳原本的值（找不到 XR 設定時回傳 null）。
    /// 用反射呼叫，專案沒有安裝 XR Plugin Management 時也能編譯。
    /// </summary>
    private static bool? SetXRInitOnStart(bool value)
    {
        try
        {
            Type perTarget = Type.GetType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget, Unity.XR.Management.Editor");
            if (perTarget == null) return null;
            MethodInfo get = perTarget.GetMethod("XRGeneralSettingsForBuildTarget", BindingFlags.Public | BindingFlags.Static);
            if (get == null) return null;
            object settings = get.Invoke(null, new object[] { BuildTargetGroup.Standalone });
            if (settings == null) return null;
            PropertyInfo prop = settings.GetType().GetProperty("InitManagerOnStart");
            if (prop == null) return null;
            bool old = (bool)prop.GetValue(settings);
            prop.SetValue(settings, value);
            EditorUtility.SetDirty((UnityEngine.Object)settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"<color=grey>[鬼切RL] Standalone 的「啟動時初始化 VR」{(value ? "已還原為開啟" : "暫時關閉（訓練場不需要 VR）")}</color>");
            return old;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[鬼切RL] 無法調整 VR 啟動設定（不影響打包，打包版啟動時可能出現 VR 錯誤訊息，可忽略）：" + e.Message);
            return null;
        }
    }
}
#endif
