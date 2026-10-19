#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 🗺 從 cc.unity 匯出「城市路網訓練樣本」給鬼切強化學習訓練場 v2 用。
/// 選單：Tools > 危險駕駛AI > 匯出城市路網訓練樣本（在 cc.unity 執行）
///
/// 做的事：
///   1. 沿著城市裡每一段車道線，每 15m 取一個點、用車道方向當作玩家行進方向
///   2. 呼叫觸發器的 EvaluateGhostRoadSpot —— 跟按 F7 時 TryStart 用的是同一套道路條件
///      （前方夠直、沒有路口、隔壁有同向車道而且夠長…）
///   3. 通過的位置記下：隔壁車道偏移（左右、寬度）、隔壁車道在前方多遠結束、前方轉角
///   4. 實際量測鬼切車 prefab 與玩家車的車身尺寸
/// 結果存成 Assets/script/危險駕駛AI/機器學習/城市路網訓練樣本.asset，訓練場每回合從裡面隨機抽一筆。
/// </summary>
public static class CityRoadSampleExporter
{
    private const string AssetPath = "Assets/script/危險駕駛AI/機器學習/城市路網訓練樣本.asset";
    private const float SampleSpacing = 15f;

    [MenuItem("Tools/危險駕駛AI/匯出城市路網訓練樣本（在 cc.unity 執行）")]
    private static void Export()
    {
        var trigger = Object.FindObjectOfType<AggressiveDriverEventTrigger>();
        if (trigger == null)
        {
            EditorUtility.DisplayDialog("匯出城市路網訓練樣本", "場景裡找不到「危險駕駛事件觸發器」（AggressiveDriverEventTrigger）。\n請先開啟 cc.unity 再執行。", "確定");
            return;
        }

        LaneNetwork.Invalidate();
        LaneNetwork net = LaneNetwork.Get(trigger.grayNarrowMaskRadius, true);
        if (net.segCount == 0)
        {
            EditorUtility.DisplayDialog("匯出城市路網訓練樣本", "場景中沒有任何路網線段。", "確定");
            return;
        }

        // ── 1～3. 道路樣本 ──
        var samples = new List<CityRoadSamples.RoadSample>();
        var reasons = new Dictionary<string, int>();
        var offsets = new List<float>();
        var ends = new List<float>();
        int tried = 0;
        bool cancelled = false;
        try
        {
            for (int i = 0; i < net.segCount; i++)
            {
                if (i % 40 == 0 && EditorUtility.DisplayCancelableProgressBar("匯出城市路網訓練樣本",
                        $"檢查車道線段 {i}/{net.segCount}（已找到 {samples.Count} 個可觸發鬼切的位置）", (float)i / net.segCount))
                { cancelled = true; break; }

                int a = net.segA[i], b = net.segB[i];
                if (net.isScooter[a] || net.isScooter[b]) continue;
                int n = Mathf.Max(1, Mathf.FloorToInt(net.segLen[i] / SampleSpacing));
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = Vector3.Lerp(net.pos[a], net.pos[b], (k + 0.5f) / n);
                    tried++;
                    float heading; string reason;
                    if (trigger.EvaluateGhostRoadSpot(net, p, net.segDir[i], offsets, ends, out heading, out reason))
                    {
                        for (int j = 0; j < offsets.Count; j++)
                            samples.Add(new CityRoadSamples.RoadSample
                            {
                                adjOffset = offsets[j],
                                adjEndFromPlayer = ends[j] > 900f ? 999f : ends[j],
                                headingChange = heading,
                                position = p
                            });
                    }
                    else
                    {
                        reasons.TryGetValue(reason, out int c);
                        reasons[reason] = c + 1;
                    }
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        if (samples.Count == 0)
        {
            EditorUtility.DisplayDialog("匯出城市路網訓練樣本", "整個路網找不到任何可以觸發鬼切的位置，請檢查觸發器的道路條件設定。", "確定");
            return;
        }

        // ── 4. 車身尺寸 ──
        var actorSizes = new List<CityRoadSamples.CarSize>();
        if (trigger.actorPrefabs != null)
        {
            foreach (var prefab in trigger.actorPrefabs)
            {
                if (prefab == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetPositionAndRotation(new Vector3(0f, -5000f, 0f), Quaternion.identity);
                Physics.SyncTransforms();
                actorSizes.Add(new CityRoadSamples.CarSize
                {
                    name = prefab.name,
                    halfLength = AggressiveDriverActor.MeasureHalfLength(go, 2.3f),
                    halfWidth = Mathf.Clamp(AggressiveDriverActor.MeasureHalfWidth(go, 1.0f), 0.7f, 1.8f)
                });
                Object.DestroyImmediate(go);
            }
        }

        var playerSize = new CityRoadSamples.CarSize { name = "玩家車（找不到，用預設值）", halfLength = 2.3f, halfWidth = 1.0f };
        GameObject playerGo = GameObject.FindWithTag("Player");
        if (playerGo != null)
        {
            // 跟城市執行時同樣的量法：車長用 Player 物件（觸發器）、車寬用它的 Rigidbody 物件（演員車 Begin）
            Rigidbody rb = playerGo.GetComponent<Rigidbody>();
            if (rb == null) rb = playerGo.GetComponentInParent<Rigidbody>();
            if (rb == null) rb = playerGo.GetComponentInChildren<Rigidbody>();
            playerSize = new CityRoadSamples.CarSize
            {
                name = playerGo.name,
                halfLength = Mathf.Clamp(AggressiveDriverActor.MeasureHalfLength(playerGo, 2.3f), 1.5f, 3.5f),
                halfWidth = Mathf.Clamp(AggressiveDriverActor.MeasureHalfWidth(rb != null ? rb.gameObject : playerGo, 1.0f), 0.7f, 1.8f)
            };
        }

        // ── 存檔 ──
        var asset = AssetDatabase.LoadAssetAtPath<CityRoadSamples>(AssetPath);
        bool isNew = asset == null;
        if (isNew) asset = ScriptableObject.CreateInstance<CityRoadSamples>();
        asset.samples = samples;
        asset.actorSizes = actorSizes;
        asset.playerSize = playerSize;
        asset.sourceScene = EditorSceneManager.GetActiveScene().name;
        asset.exportedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        asset.summary = BuildSummary(samples, reasons, tried, actorSizes, playerSize, cancelled);
        if (isNew) AssetDatabase.CreateAsset(asset, AssetPath);
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssets();

        Debug.Log("<color=cyan>🗺 [鬼切RL] 城市路網訓練樣本已匯出 → " + AssetPath + "\n" + asset.summary + "</color>", asset);
        EditorGUIUtility.PingObject(asset);
    }

    private static string BuildSummary(List<CityRoadSamples.RoadSample> samples, Dictionary<string, int> reasons, int tried,
                                       List<CityRoadSamples.CarSize> actors, CityRoadSamples.CarSize player, bool cancelled)
    {
        var widths = samples.Select(s => Mathf.Abs(s.adjOffset)).ToList();
        int left = samples.Count(s => s.adjOffset < 0f);
        int ending = samples.Count(s => s.adjEndFromPlayer < 900f);
        int passedSpots = tried - reasons.Values.Sum();
        var sb = new StringBuilder();
        sb.AppendLine($"檢查 {tried} 個位置，{passedSpots} 個可以觸發鬼切（{100f * passedSpots / Mathf.Max(1, tried):F0}%），共 {samples.Count} 筆樣本" +
                      (cancelled ? "（⚠ 中途取消，只有部分路網）" : ""));
        sb.AppendLine($"隔壁車道：左側 {left} 筆、右側 {samples.Count - left} 筆；車道寬 {widths.Min():F1}～{widths.Max():F1}m（平均 {widths.Average():F1}m）");
        sb.AppendLine($"隔壁車道在前方會結束的：{ending} 筆（{100f * ending / samples.Count:F0}%）");
        sb.AppendLine("不能觸發的原因：" + string.Join("、", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        sb.AppendLine("鬼切車尺寸：" + (actors.Count == 0 ? "（觸發器沒有指定 Actor Prefabs，訓練場會用 5.6×2.8m）" :
                      string.Join("、", actors.Select(a => $"{a.name} {a.halfLength * 2f:F1}×{a.halfWidth * 2f:F1}m"))));
        sb.Append($"玩家車尺寸：{player.name} {player.halfLength * 2f:F1}×{player.halfWidth * 2f:F1}m");
        return sb.ToString();
    }
}
#endif
