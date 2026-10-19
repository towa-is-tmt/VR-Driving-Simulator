#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 圖層重新對應工具 — 專案整合時，把物件從「舊的 Layer 編號」搬到「新的 Layer 名稱」。
///
/// 使用情境：
///   你的專案 Layer 6 是 ParkingLine，但隊友的專案 Layer 6 已經被別的東西佔用。
///   匯入你的場景後，物件身上還帶著舊編號 6，在他的專案裡意義完全不同。
///   這支工具依照對照表，把它們搬到正確的新編號。
///
/// ⚠ 使用前務必先備份，或用版本控制。這個操作會直接修改場景與 Prefab。
/// ⚠ 只在「你匯入的場景」上執行。若在隊友原本的場景執行，
///    他自己位於相同舊編號的物件也會被搬走。
///
/// 開啟方式：Tools → 圖層重新對應
/// </summary>
public class LayerRemapTool : EditorWindow
{
    [System.Serializable]
    private class 對應項
    {
        public int 舊編號;
        public string 新圖層名稱;
        public bool 啟用 = true;
    }

    private List<對應項> 對照表 = new List<對應項>();
    private Vector2 捲動;
    private bool 也處理Prefab = false;
    private string Prefab資料夾 = "Assets";
    private string 結果訊息 = "";

    [MenuItem("Tools/圖層重新對應")]
    public static void 開啟視窗()
    {
        var w = GetWindow<LayerRemapTool>("圖層重新對應");
        w.minSize = new Vector2(460, 420);
        w.載入預設對照表();
    }

    /// <summary>預設帶入這個專案原本的九個自訂圖層</summary>
    private void 載入預設對照表()
    {
        if (對照表.Count > 0) return;
        對照表 = new List<對應項>
        {
            new 對應項 { 舊編號 = 6,  新圖層名稱 = "ParkingLine"  },
            new 對應項 { 舊編號 = 7,  新圖層名稱 = "ParkingZone"  },
            new 對應項 { 舊編號 = 8,  新圖層名稱 = "LineSensor"   },
            new 對應項 { 舊編號 = 9,  新圖層名稱 = "VehicleBody"  },
            new 對應項 { 舊編號 = 10, 新圖層名稱 = "SpiralWall"   },
            new 對應項 { 舊編號 = 11, 新圖層名稱 = "SpiralGoal"   },
            new 對應項 { 舊編號 = 12, 新圖層名稱 = "RampZone"     },
            new 對應項 { 舊編號 = 13, 新圖層名稱 = "RampLine"     },
            new 對應項 { 舊編號 = 14, 新圖層名稱 = "地板加道路"    },
        };
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "把物件從「舊編號」搬到「新圖層名稱」對應的編號。\n" +
            "只在你匯入進來的場景上執行，否則會影響對方原本的物件。\n" +
            "執行前請先備份專案。", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("對照表", EditorStyles.boldLabel);

        捲動 = EditorGUILayout.BeginScrollView(捲動, GUILayout.Height(220));
        for (int i = 0; i < 對照表.Count; i++)
        {
            var 項 = 對照表[i];
            EditorGUILayout.BeginHorizontal();

            項.啟用 = EditorGUILayout.Toggle(項.啟用, GUILayout.Width(18));
            EditorGUILayout.LabelField("舊編號", GUILayout.Width(45));
            項.舊編號 = EditorGUILayout.IntField(項.舊編號, GUILayout.Width(40));
            EditorGUILayout.LabelField("→", GUILayout.Width(18));
            項.新圖層名稱 = EditorGUILayout.TextField(項.新圖層名稱);

            int 新編號 = LayerMask.NameToLayer(項.新圖層名稱);
            GUI.color = 新編號 >= 0 ? Color.green : Color.red;
            EditorGUILayout.LabelField(新編號 >= 0 ? $"= {新編號}" : "找不到", GUILayout.Width(60));
            GUI.color = Color.white;

            if (GUILayout.Button("−", GUILayout.Width(22))) { 對照表.RemoveAt(i); i--; }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("新增一列")) 對照表.Add(new 對應項());

        EditorGUILayout.Space();
        也處理Prefab = EditorGUILayout.Toggle("同時處理 Prefab", 也處理Prefab);
        if (也處理Prefab)
            Prefab資料夾 = EditorGUILayout.TextField("Prefab 搜尋資料夾", Prefab資料夾);

        EditorGUILayout.Space();

        if (GUILayout.Button("① 預覽（只統計，不修改）", GUILayout.Height(26)))
            執行(false);

        GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
        if (GUILayout.Button("② 實際套用（會修改場景）", GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog("確認套用",
                "這會直接修改目前開啟的場景。\n確定已經備份了嗎？", "確定套用", "取消"))
                執行(true);
        }
        GUI.backgroundColor = Color.white;

        if (!string.IsNullOrEmpty(結果訊息))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(結果訊息, MessageType.Info);
        }
    }

    private void 執行(bool 實際修改)
    {
        // 先把「舊編號 → 新編號」算好，並檢查新圖層是否存在
        var 對應 = new Dictionary<int, int>();
        var 缺少 = new List<string>();

        foreach (var 項 in 對照表)
        {
            if (!項.啟用 || string.IsNullOrEmpty(項.新圖層名稱)) continue;
            int 新 = LayerMask.NameToLayer(項.新圖層名稱);
            if (新 < 0) { 缺少.Add(項.新圖層名稱); continue; }
            對應[項.舊編號] = 新;
        }

        if (缺少.Count > 0)
        {
            結果訊息 = "⚠ 這些圖層在目前專案中不存在，請先到 Tags and Layers 建立：\n" +
                       string.Join("、", 缺少);
            return;
        }

        int 場景筆數 = 0, Prefab筆數 = 0;
        var 統計 = new Dictionary<string, int>();

        // ── 場景 ──
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            var scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded) continue;

            foreach (var root in scene.GetRootGameObjects())
                場景筆數 += 處理物件(root, 對應, 實際修改, 統計);

            if (實際修改) EditorSceneManager.MarkSceneDirty(scene);
        }

        // ── Prefab ──
        if (也處理Prefab)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { Prefab資料夾 });
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var go = PrefabUtility.LoadPrefabContents(path);
                int n = 處理物件(go, 對應, 實際修改, 統計);
                if (實際修改 && n > 0) PrefabUtility.SaveAsPrefabAsset(go, path);
                PrefabUtility.UnloadPrefabContents(go);
                Prefab筆數 += n;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(實際修改 ? "✔ 已套用" : "預覽結果（尚未修改）");
        sb.AppendLine($"場景物件：{場景筆數} 個");
        if (也處理Prefab) sb.AppendLine($"Prefab 物件：{Prefab筆數} 個");
        sb.AppendLine();
        foreach (var kv in 統計) sb.AppendLine($"  {kv.Key}：{kv.Value} 個");
        if (實際修改) sb.AppendLine("\n記得 Ctrl+S 存檔！");

        結果訊息 = sb.ToString();
        Debug.Log("[圖層重新對應] " + 結果訊息);
    }

    /// <summary>遞迴處理一個物件與其所有子物件，回傳修改筆數</summary>
    private int 處理物件(GameObject go, Dictionary<int, int> 對應,
                          bool 實際修改, Dictionary<string, int> 統計)
    {
        int count = 0;

        if (對應.TryGetValue(go.layer, out int 新編號) && go.layer != 新編號)
        {
            string key = $"{LayerMask.LayerToName(go.layer)}({go.layer}) → " +
                         $"{LayerMask.LayerToName(新編號)}({新編號})";
            統計[key] = 統計.TryGetValue(key, out int c) ? c + 1 : 1;

            if (實際修改)
            {
                Undo.RecordObject(go, "圖層重新對應");
                go.layer = 新編號;
                EditorUtility.SetDirty(go);
            }
            count++;
        }

        foreach (Transform child in go.transform)
            count += 處理物件(child.gameObject, 對應, 實際修改, 統計);

        return count;
    }
}
#endif
