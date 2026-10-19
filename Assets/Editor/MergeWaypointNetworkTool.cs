#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 路網合併工具
/// 用法:
///   1. 把 車流同捆包.prefab 拖進場景,右鍵 → Unpack Prefab Completely
///   2. 在 Hierarchy 選取「剛拖進來的那個根物件」
///   3. 選單 Tools → 合併路網(刪除重複節點並自動接縫)
///
/// 做的事:
///   - 新來的節點若跟你場景現有節點重疊(容差內) → 視為「重複」,刪除
///   - 刪除前先把它的 nextChoices 連線「轉嫁」到你現有的對應節點上(自動接縫)
///   - 只保留真正新增的節點,而且連線會接回你原本的路網
/// </summary>
public static class MergeWaypointNetworkTool
{
    private const float MERGE_TOLERANCE = 2.5f; // 幾公尺內視為同一個節點

    [MenuItem("Tools/合併路網(刪除重複節點並自動接縫)")]
    private static void MergeNetwork()
    {
        GameObject newRoot = Selection.activeGameObject;
        if (newRoot == null)
        {
            EditorUtility.DisplayDialog("路網合併", "請先在 Hierarchy 選取「剛拖進場景的那個根物件」", "OK");
            return;
        }

        // 新來的節點(在選取物件底下)
        var incoming = newRoot.GetComponentsInChildren<WaypointNode>(true).ToList();
        // 場景既有節點(不在選取物件底下)
        var existing = Object.FindObjectsByType<WaypointNode>(FindObjectsSortMode.None)
                             .Where(n => !n.transform.IsChildOf(newRoot.transform)).ToList();

        if (incoming.Count == 0)
        {
            EditorUtility.DisplayDialog("路網合併", "選取的物件底下找不到 WaypointNode", "OK");
            return;
        }

        // 建立「新節點 → 既有對應節點」的對照表(重疊者)
        var map = new Dictionary<WaypointNode, WaypointNode>();
        foreach (var inc in incoming)
        {
            WaypointNode best = null;
            float bestD = MERGE_TOLERANCE;
            foreach (var ex in existing)
            {
                float d = Vector3.Distance(
                    new Vector3(inc.transform.position.x, 0, inc.transform.position.z),
                    new Vector3(ex.transform.position.x, 0, ex.transform.position.z));
                if (d < bestD) { bestD = d; best = ex; }
            }
            if (best != null) map[inc] = best;
        }

        var keep = incoming.Where(n => !map.ContainsKey(n)).ToList();

        if (!EditorUtility.DisplayDialog("路網合併確認",
            $"新來節點:{incoming.Count}\n" +
            $"與現有重疊(將刪除):{map.Count}\n" +
            $"真正新增(將保留):{keep.Count}\n\n" +
            "會自動把連線轉嫁到你現有的節點上(接縫)。\n請先確定場景已備份!",
            "執行合併", "取消")) return;

        Undo.RegisterFullObjectHierarchyUndo(newRoot, "Merge Waypoint Network");

        int rewired = 0, seamAdded = 0;

        // A. 保留下來的新節點:把指向「重複節點」的連線改指向你既有的節點
        foreach (var k in keep)
        {
            if (k.nextChoices == null) continue;
            for (int i = 0; i < k.nextChoices.Count; i++)
            {
                var t = k.nextChoices[i];
                if (t != null && map.ContainsKey(t)) { k.nextChoices[i] = map[t]; rewired++; }
            }
            EditorUtility.SetDirty(k);
        }

        // B. 你既有的節點:把「重複節點原本的出口」補進來(接縫),只補缺的
        foreach (var pair in map)
        {
            var incNode = pair.Key;      // 要被刪的新節點
            var exNode = pair.Value;     // 你現有的對應節點
            if (incNode.nextChoices == null) continue;
            if (exNode.nextChoices == null) exNode.nextChoices = new List<WaypointNode>();

            foreach (var t in incNode.nextChoices)
            {
                if (t == null) continue;
                var target = map.ContainsKey(t) ? map[t] : t;  // 出口若也是重複的,轉成你現有的
                if (target == exNode) continue;
                if (!exNode.nextChoices.Contains(target))
                {
                    exNode.nextChoices.Add(target);
                    seamAdded++;
                    EditorUtility.SetDirty(exNode);
                }
            }
        }

        // C. 刪除重複的新節點
        foreach (var dup in map.Keys) Undo.DestroyObjectImmediate(dup.gameObject);

        Debug.Log($"<color=lime>✅ 路網合併完成</color>\n" +
                  $"   刪除重複節點:{map.Count}\n" +
                  $"   保留新增節點:{keep.Count}\n" +
                  $"   連線轉嫁:{rewired}\n" +
                  $"   接縫新增連線:{seamAdded}");
    }

    [MenuItem("Tools/刪除新路網底下的車輛與號誌大腦(選取新路網根物件)")]
    private static void RemoveIncomingCarsAndBrain()
    {
        GameObject newRoot = Selection.activeGameObject;
        if (newRoot == null)
        {
            EditorUtility.DisplayDialog("清理", "請先在 Hierarchy 選取「剛拖進場景的那個根物件」", "OK");
            return;
        }

        var cars = newRoot.GetComponentsInChildren<CarNavigation>(true).ToList();
        var brains = newRoot.GetComponentsInChildren<TrafficSystem_Brain>(true).ToList();

        if (!EditorUtility.DisplayDialog("清理新路網底下的多餘物件",
            $"將刪除:\n" +
            $"  車輛(CarNavigation):{cars.Count} 台\n" +
            $"  號誌大腦(TrafficSystem_Brain):{brains.Count} 個\n\n" +
            "你場景原本的車輛與大腦完全不動。\n" +
            "(車流數量由你的 GrayTrafficSpawner 控管,不需要他包裡的車)",
            "執行刪除", "取消")) return;

        int n = 0;
        foreach (var c in cars) { if (c != null) { Undo.DestroyObjectImmediate(c.gameObject); n++; } }
        foreach (var b in brains) { if (b != null) { Undo.DestroyObjectImmediate(b.gameObject); n++; } }

        Debug.Log($"<color=lime>✅ 刪除了 {n} 個多餘物件(車輛 {cars.Count} 台、大腦 {brains.Count} 個)</color>");
    }

    [MenuItem("Tools/清除重複的紅綠燈(選取新路網根物件)")]
    private static void RemoveDuplicateTrafficLights()
    {
        GameObject newRoot = Selection.activeGameObject;
        if (newRoot == null)
        {
            EditorUtility.DisplayDialog("清除重複紅綠燈", "請先在 Hierarchy 選取「剛拖進場景的那個根物件」", "OK");
            return;
        }

        // 新來的觸發區 vs 你現有的觸發區
        var incoming = newRoot.GetComponentsInChildren<TrafficLightTrigger>(true).ToList();
        var existing = Object.FindObjectsByType<TrafficLightTrigger>(FindObjectsSortMode.None)
                             .Where(t => !t.transform.IsChildOf(newRoot.transform)).ToList();

        // 找出重疊的(容差內)
        var dupTriggers = new List<TrafficLightTrigger>();
        foreach (var inc in incoming)
        {
            foreach (var ex in existing)
            {
                float d = Vector3.Distance(
                    new Vector3(inc.transform.position.x, 0, inc.transform.position.z),
                    new Vector3(ex.transform.position.x, 0, ex.transform.position.z));
                if (d < 4f) { dupTriggers.Add(inc); break; }
            }
        }

        // 找出重疊的燈桿模型(名稱含「紅綠燈」且位置重疊)
        var incPoles = newRoot.GetComponentsInChildren<Transform>(true)
                              .Where(t => t.name.Contains("紅綠燈")).ToList();
        var exPoles = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                            .Where(t => t.name.Contains("紅綠燈") && !t.IsChildOf(newRoot.transform)).ToList();
        var dupPoles = new List<Transform>();
        foreach (var ip in incPoles)
        {
            foreach (var ep in exPoles)
            {
                float d = Vector3.Distance(
                    new Vector3(ip.position.x, 0, ip.position.z),
                    new Vector3(ep.position.x, 0, ep.position.z));
                if (d < 4f) { dupPoles.Add(ip); break; }
            }
        }

        if (!EditorUtility.DisplayDialog("清除重複紅綠燈",
            $"新來觸發區:{incoming.Count}\n" +
            $"其中與現有重疊(將刪除):{dupTriggers.Count}\n" +
            $"保留(新路口):{incoming.Count - dupTriggers.Count}\n\n" +
            $"重疊的燈桿模型(將刪除):{dupPoles.Count}\n\n" +
            "你原本的紅綠燈與 Brain 設定完全不動。",
            "執行清除", "取消")) return;

        int n = 0;
        foreach (var t in dupTriggers) { if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); n++; } }
        foreach (var p in dupPoles) { if (p != null) { Undo.DestroyObjectImmediate(p.gameObject); n++; } }

        Debug.Log($"<color=lime>✅ 清除了 {n} 個重複的紅綠燈物件</color>\n" +
                  $"   保留新路口觸發區:{incoming.Count - dupTriggers.Count} 個\n" +
                  $"   ⚠️ 記得:新路口的燈物件要加進 TrafficSystem_Brain 的 Group A/B 陣列,燈才會變色!");
    }

    [MenuItem("Tools/修復不會動的車(指派最近的路徑點)")]
    private static void FixStuckCars()
    {
        var cars = Object.FindObjectsByType<CarNavigation>(FindObjectsSortMode.None);
        var nodes = Object.FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
        if (nodes.Length == 0) { Debug.LogError("場景裡找不到任何 WaypointNode"); return; }

        int fixedCount = 0;
        foreach (var c in cars)
        {
            if (c.currentWaypointNode != null) continue;   // 有節點的不動

            // 找最近、而且有出口的節點
            WaypointNode best = null; float bestD = float.MaxValue;
            foreach (var n in nodes)
            {
                if (n.nextChoices == null || n.nextChoices.Count == 0) continue;
                float d = Vector3.Distance(c.transform.position, n.transform.position);
                if (d < bestD) { bestD = d; best = n; }
            }
            if (best != null)
            {
                Undo.RecordObject(c, "Fix car waypoint");
                c.currentWaypointNode = best;
                EditorUtility.SetDirty(c);
                fixedCount++;
                Debug.Log($"🔧 {c.name} 指派到最近節點 {best.name}(距離 {bestD:F1}m)", c);
            }
        }
        Debug.Log($"<color=lime>✅ 修復了 {fixedCount} 台不會動的車</color>");
    }

    [MenuItem("Tools/刪除場景中沒有路徑點的車(交給 Spawner 重生)")]
    private static void DeleteCarsWithoutNode()
    {
        var cars = Object.FindObjectsByType<CarNavigation>(FindObjectsSortMode.None);
        var bad = cars.Where(c => c.currentWaypointNode == null).ToList();
        if (!EditorUtility.DisplayDialog("刪除無路徑點的車",
            $"找到 {bad.Count} 台沒有路徑點的車(不會動)。\n刪掉後由 GrayTrafficSpawner 自動補生。", "刪除", "取消")) return;
        foreach (var c in bad) Undo.DestroyObjectImmediate(c.gameObject);
        Debug.Log($"<color=lime>✅ 刪除了 {bad.Count} 台無路徑點的車</color>");
    }

    [MenuItem("Tools/一鍵清除所有空連線(None)")]
    private static void CleanNullLinks()
    {
        var all = Object.FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
        int removed = 0, affected = 0;

        foreach (var n in all)
        {
            if (n.nextChoices == null) continue;
            int before = n.nextChoices.Count;
            n.nextChoices.RemoveAll(x => x == null);
            int diff = before - n.nextChoices.Count;
            if (diff > 0)
            {
                removed += diff; affected++;
                EditorUtility.SetDirty(n);
                Debug.Log($"🧹 清除 {diff} 格空連線:{n.name}", n);
            }
        }
        Debug.Log($"<color=lime>✅ 共清除 {removed} 格空連線,影響 {affected} 個節點</color>");
    }

    [MenuItem("Tools/檢查路網(找出斷點與孤兒節點)")]
    private static void CheckNetwork()
    {
        var all = Object.FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
        int dead = 0, orphan = 0, nullRef = 0;
        var linked = new HashSet<WaypointNode>();

        foreach (var n in all)
            if (n.nextChoices != null)
                foreach (var t in n.nextChoices)
                    if (t != null) linked.Add(t);

        foreach (var n in all)
        {
            int valid = 0, myNull = 0;
            if (n.nextChoices != null)
                foreach (var t in n.nextChoices) { if (t == null) { nullRef++; myNull++; } else valid++; }

            if (myNull > 0) Debug.LogError($"❌ 空連線({myNull} 格 None):{n.name}  ← 點我跳到節點", n);
            if (valid == 0) { dead++; Debug.LogWarning($"⛔ 死路(沒有出口):{n.name}", n); }
            if (!linked.Contains(n)) { orphan++; Debug.LogWarning($"🔸 孤兒(沒人連向它):{n.name}", n); }
        }

        Debug.Log($"<color=cyan>🔍 路網檢查:總節點 {all.Length} | 死路 {dead} | 孤兒 {orphan} | 空連線 {nullRef}</color>");
    }
}
#endif
