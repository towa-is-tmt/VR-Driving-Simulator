#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 自動把場景中所有紅綠燈燈號物件接到 TrafficSystem_Brain 的 Group A/B 陣列
/// 判斷依據:燈物件的名稱(Red_A / Yellow_A / Green_A → A 組;Red_B / ... → B 組)
///
/// 用法:選單 Tools → 自動接線:紅綠燈 → Brain
/// </summary>
public static class WireTrafficLightsTool
{
    [MenuItem("Tools/自動接線:紅綠燈 → Brain")]
    private static void WireLights()
    {
        var brain = Object.FindFirstObjectByType<TrafficSystem_Brain>();
        if (brain == null)
        {
            EditorUtility.DisplayDialog("紅綠燈接線", "場景裡找不到 TrafficSystem_Brain", "OK");
            return;
        }

        // 掃描全場景的燈號物件(用名稱判斷)
        var allT = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        var aRed = new List<GameObject>(); var aYel = new List<GameObject>(); var aGrn = new List<GameObject>();
        var bRed = new List<GameObject>(); var bYel = new List<GameObject>(); var bGrn = new List<GameObject>();

        foreach (var t in allT)
        {
            string n = t.name;
            // 只認 Red_A / Red_A (1) 這種命名
            if (n.StartsWith("Red_A")) aRed.Add(t.gameObject);
            else if (n.StartsWith("Yellow_A")) aYel.Add(t.gameObject);
            else if (n.StartsWith("Green_A")) aGrn.Add(t.gameObject);
            else if (n.StartsWith("Red_B")) bRed.Add(t.gameObject);
            else if (n.StartsWith("Yellow_B")) bYel.Add(t.gameObject);
            else if (n.StartsWith("Green_B")) bGrn.Add(t.gameObject);
        }

        int before = (brain.groupARed?.Length ?? 0) + (brain.groupAYellow?.Length ?? 0) + (brain.groupAGreen?.Length ?? 0)
                   + (brain.groupBRed?.Length ?? 0) + (brain.groupBYellow?.Length ?? 0) + (brain.groupBGreen?.Length ?? 0);

        if (!EditorUtility.DisplayDialog("紅綠燈自動接線",
            $"掃描到的燈號物件:\n" +
            $"  A組(南北向)  紅 {aRed.Count} / 黃 {aYel.Count} / 綠 {aGrn.Count}\n" +
            $"  B組(東西向)  紅 {bRed.Count} / 黃 {bYel.Count} / 綠 {bGrn.Count}\n\n" +
            $"Brain 目前總共接了 {before} 個燈,將全部重新接線(含新路口)。",
            "執行接線", "取消")) return;

        Undo.RecordObject(brain, "Wire traffic lights");
        brain.groupARed = aRed.ToArray();
        brain.groupAYellow = aYel.ToArray();
        brain.groupAGreen = aGrn.ToArray();
        brain.groupBRed = bRed.ToArray();
        brain.groupBYellow = bYel.ToArray();
        brain.groupBGreen = bGrn.ToArray();
        EditorUtility.SetDirty(brain);

        int after = aRed.Count + aYel.Count + aGrn.Count + bRed.Count + bYel.Count + bGrn.Count;
        Debug.Log($"<color=lime>✅ 紅綠燈接線完成:{before} → {after} 個燈物件</color>\n" +
                  $"   A組 紅{aRed.Count} 黃{aYel.Count} 綠{aGrn.Count} | B組 紅{bRed.Count} 黃{bYel.Count} 綠{bGrn.Count}", brain);
    }

    /// <summary>
    /// 只保留「新路口」的紅綠燈:選取重新拖進來的那包,刪掉裡面除了新路口紅綠燈以外的所有東西
    /// (節點、車、大腦、以及跟現有重疊的紅綠燈)
    /// </summary>
    [MenuItem("Tools/只保留新路口紅綠燈(選取重新拖進來的那包)")]
    private static void KeepOnlyNewLights()
    {
        GameObject root = Selection.activeGameObject;
        if (root == null)
        {
            EditorUtility.DisplayDialog("只保留新路口紅綠燈", "請先在 Hierarchy 選取「重新拖進來的那個根物件」", "OK");
            return;
        }

        // 1) 刪掉這包裡的節點、車、大腦(路網已經合併過了,不需要再來一次)
        var nodes = root.GetComponentsInChildren<WaypointNode>(true).ToList();
        var cars = root.GetComponentsInChildren<CarNavigation>(true).ToList();
        var brains = root.GetComponentsInChildren<TrafficSystem_Brain>(true).ToList();

        // 2) 這包裡的觸發區,跟場景現有的比對:重疊的刪掉,只留新路口的
        var incoming = root.GetComponentsInChildren<TrafficLightTrigger>(true).ToList();
        var existing = Object.FindObjectsByType<TrafficLightTrigger>(FindObjectsSortMode.None)
                             .Where(t => !t.transform.IsChildOf(root.transform)).ToList();

        var dup = new List<TrafficLightTrigger>();
        foreach (var inc in incoming)
        {
            bool overlap = existing.Any(ex =>
                Vector3.Distance(new Vector3(inc.transform.position.x, 0, inc.transform.position.z),
                                 new Vector3(ex.transform.position.x, 0, ex.transform.position.z)) < 6f);
            if (overlap) dup.Add(inc);
        }

        if (!EditorUtility.DisplayDialog("只保留新路口紅綠燈",
            $"這包裡有:\n" +
            $"  路網節點 {nodes.Count}(將刪除,已合併過)\n" +
            $"  車輛 {cars.Count}、大腦 {brains.Count}(將刪除)\n" +
            $"  紅綠燈觸發區 {incoming.Count},其中與現有重疊 {dup.Count}(將刪除)\n\n" +
            $"→ 最後只會留下 {incoming.Count - dup.Count} 組新路口的紅綠燈",
            "執行", "取消")) return;

        int n = 0;
        foreach (var x in nodes) if (x != null) { Undo.DestroyObjectImmediate(x.gameObject); n++; }
        foreach (var x in cars) if (x != null) { Undo.DestroyObjectImmediate(x.gameObject); n++; }
        foreach (var x in brains) if (x != null) { Undo.DestroyObjectImmediate(x.gameObject); n++; }

        // 刪重疊的紅綠燈:連同它的燈桿父物件一起
        foreach (var d in dup)
        {
            if (d == null) continue;
            Transform pole = d.transform;
            while (pole.parent != null && pole.parent != root.transform && !pole.parent.name.Contains("紅綠燈"))
                pole = pole.parent;
            if (pole.parent != null && pole.parent.name.Contains("紅綠燈")) pole = pole.parent;
            Undo.DestroyObjectImmediate(pole.gameObject);
            n++;
        }

        Debug.Log($"<color=lime>✅ 清理完成,刪除 {n} 個物件</color>\n" +
                  $"   保留新路口紅綠燈:{incoming.Count - dup.Count} 組\n" +
                  $"   ⚠️ 接著跑「Tools → 自動接線:紅綠燈 → Brain」讓新燈會變色");
    }

    /// <summary>
    /// 清掉「跟現有紅綠燈重疊」的多餘燈桿(不限剛拖進來的那包,全場景掃)
    /// 用法:選單執行,會保留每個位置最先出現的一組,其餘刪除
    /// </summary>
    [MenuItem("Tools/清除場景中所有重疊的紅綠燈(去重)")]
    private static void DedupeAllTrafficLights()
    {
        var all = Object.FindObjectsByType<TrafficLightTrigger>(FindObjectsSortMode.None).ToList();
        var keep = new List<TrafficLightTrigger>();
        var dup = new List<TrafficLightTrigger>();

        const float TOL = 12f; // 同一路口同方向的觸發區視為重複

        foreach (var t in all)
        {
            bool isDup = keep.Any(k =>
                Vector3.Distance(new Vector3(k.transform.position.x, 0, k.transform.position.z),
                                 new Vector3(t.transform.position.x, 0, t.transform.position.z)) < TOL
                && k.laneType == t.laneType
                && k.isSpecialIntersection == t.isSpecialIntersection);   // 同方向、同路口類型才算重複
            if (isDup) dup.Add(t); else keep.Add(t);
        }

        if (!EditorUtility.DisplayDialog("清除重疊的紅綠燈",
            $"全場景觸發區:{all.Count}\n" +
            $"保留(不重複):{keep.Count}\n" +
            $"刪除(重疊):{dup.Count}\n\n" +
            $"容差 {TOL}m,同方向才視為重複。\n連同它的燈桿一起刪除。",
            "執行去重", "取消")) return;

        int n = 0;
        foreach (var d in dup)
        {
            if (d == null) continue;
            // 往上找燈桿(名稱含「紅綠燈」),找不到就刪觸發區本身
            Transform target = d.transform;
            Transform p = d.transform.parent;
            while (p != null)
            {
                if (p.name.Contains("紅綠燈")) { target = p; break; }
                p = p.parent;
            }
            Undo.DestroyObjectImmediate(target.gameObject);
            n++;
        }
        Debug.Log($"<color=lime>✅ 去重完成,刪除 {n} 組重疊紅綠燈,剩下 {keep.Count} 組</color>");
    }

    /// <summary>
    /// 直接對「燈桿模型」去重:不管有沒有觸發區,位置太近的同名燈桿只留一根
    /// </summary>
    [MenuItem("Tools/清除重疊的紅綠燈桿(依位置去重)")]
    private static void DedupePoles()
    {
        // 找出所有燈桿:名稱含「紅綠燈」,且它是該模型的根(父物件名稱不含「紅綠燈」)
        var poles = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.name.Contains("紅綠燈"))
            .Where(t => t.parent == null || !t.parent.name.Contains("紅綠燈"))
            .ToList();

        const float TOL = 3f;   // 3 公尺內視為同一根
        var keep = new List<Transform>();
        var dup = new List<Transform>();

        foreach (var p in poles)
        {
            bool isDup = keep.Any(k =>
                Vector3.Distance(new Vector3(k.position.x, 0, k.position.z),
                                 new Vector3(p.position.x, 0, p.position.z)) < TOL);
            if (isDup) dup.Add(p); else keep.Add(p);
        }

        if (!EditorUtility.DisplayDialog("燈桿去重",
            $"場景中的紅綠燈桿:{poles.Count}\n" +
            $"保留:{keep.Count}\n" +
            $"刪除(位置重疊 {TOL}m 內):{dup.Count}\n\n" +
            "注意:如果同一路口本來就有多根燈桿(不同角落),距離超過 3m 就不會被誤刪。",
            "執行去重", "取消")) return;

        int n = 0;
        foreach (var d in dup) { if (d != null) { Undo.DestroyObjectImmediate(d.gameObject); n++; } }
        Debug.Log($"<color=lime>✅ 刪除 {n} 根重疊燈桿,剩下 {keep.Count} 根</color>\n" +
                  "⚠️ 刪完記得重跑「自動接線:紅綠燈 → Brain」(因為有些燈物件被刪了)");
    }

    [MenuItem("Tools/列出所有紅綠燈桿位置(診斷用)")]
    private static void ListPoles()
    {
        var poles = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
            .Where(t => t.name.Contains("紅綠燈"))
            .Where(t => t.parent == null || !t.parent.name.Contains("紅綠燈"))
            .OrderBy(t => t.position.x).ToList();
        Debug.Log($"<color=cyan>🚦 場景燈桿共 {poles.Count} 根</color>");
        foreach (var p in poles)
            Debug.Log($"   {p.name}  x={p.position.x:F0}  z={p.position.z:F0}", p.gameObject);
    }

    [MenuItem("Tools/檢查紅綠燈觸發區設定")]
    private static void CheckTriggers()
    {
        var triggers = Object.FindObjectsByType<TrafficLightTrigger>(FindObjectsSortMode.None);
        int ns = 0, ew = 0;
        foreach (var t in triggers)
        {
            if (t.laneType == TrafficLightTrigger.LaneType.A_C) ns++; else ew++;
        }
        Debug.Log($"<color=cyan>🚦 觸發區共 {triggers.Length} 個:南北向(A組) {ns} 個 / 東西向(B組) {ew} 個</color>\n" +
                  "若某個新路口的車不會停,檢查該路口觸發區的 Lane Type / Is Special Intersection 是否設對。");
    }
}
#endif
