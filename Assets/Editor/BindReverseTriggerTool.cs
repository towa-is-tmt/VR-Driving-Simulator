#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 把每個「倒車方塊」(ReverseTriggerController) 綁定到它自己的那台路邊靜停演員車。
///
/// 為什麼需要:
///   觸發盒原本靠「40m 內、玩家前方 ±60 度、離盒子最近」動態挑車,盒子本身不知道自己屬於哪台車。
///   實測有兩個盒子只距離 18m、兩台車分屬中央分隔島兩側(車頭方向相反)且都落在對方的 40m 內,
///   結果玩家踩 A 盒卻是對向車道的 B 車演出來。
///
/// 只綁「靜停演員車」(isStaticParked):
///   主車 / 新加入車 / 路邊停車 那些會動的演員車不綁,免得把它們釘死在某個盒子上。
///
/// 保守策略(寧可不綁,也不要綁錯):
///   - 距離要在 MAX_BIND_DISTANCE 以內
///   - 最近的那台要比第二近的近 AMBIGUOUS_RATIO 倍以上,否則視為分不清,跳過
///   跳過的盒子維持舊的動態搜尋行為(執行期已另外加了「靜停車必須與玩家同向」的保險)。
/// </summary>
public static class BindReverseTriggerTool
{
    private const float MAX_BIND_DISTANCE = 15f;
    private const float AMBIGUOUS_RATIO = 2.0f;

    [MenuItem("Tools/自動綁定倒車方塊 → 最近的靜停演員車")]
    private static void Bind()
    {
        var boxes = Object.FindObjectsByType<ReverseTriggerController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        var cars = Object.FindObjectsByType<ReverseThreatAICar>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None)
                         .Where(c => c != null && c.isStaticParked)
                         .ToList();

        if (boxes.Length == 0 || cars.Count == 0)
        {
            EditorUtility.DisplayDialog("綁定倒車方塊",
                $"找不到對象。\n觸發盒 {boxes.Length} 個 / 靜停演員車 {cars.Count} 台", "OK");
            return;
        }

        var plan = new List<(ReverseTriggerController box, ReverseThreatAICar car, float d)>();
        var skipped = new List<string>();

        foreach (var box in boxes)
        {
            ReverseThreatAICar best = null;
            float bestD = float.MaxValue, secondD = float.MaxValue;

            foreach (var car in cars)
            {
                float d = Flat(box.transform.position, car.transform.position);
                if (d < bestD) { secondD = bestD; bestD = d; best = car; }
                else if (d < secondD) { secondD = d; }
            }

            if (best == null || bestD > MAX_BIND_DISTANCE)
            {
                skipped.Add($"{box.name}：最近的車在 {bestD:F1}m 外(上限 {MAX_BIND_DISTANCE}m)");
                continue;
            }
            if (secondD < bestD * AMBIGUOUS_RATIO)
            {
                skipped.Add($"{box.name}：分不清({best.name} {bestD:F1}m vs 第二近 {secondD:F1}m)");
                continue;
            }
            plan.Add((box, best, bestD));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"將綁定 {plan.Count} 個觸發盒(共 {boxes.Length} 個)：\n");
        foreach (var p in plan.OrderBy(p => p.box.name))
            sb.AppendLine($"  {p.box.name}  →  {p.car.name}   ({p.d:F1} m)");
        if (skipped.Count > 0)
        {
            sb.AppendLine($"\n略過 {skipped.Count} 個(維持原本的動態搜尋)：");
            foreach (var s in skipped) sb.AppendLine("  " + s);
        }
        sb.AppendLine("\n會覆蓋這些盒子現有的 Reverse AI Cars 名單。");

        Debug.Log($"<color=cyan>🔗 [綁定倒車方塊] 預覽</color>\n{sb}");

        if (!EditorUtility.DisplayDialog("綁定倒車方塊",
            sb.Length > 1200 ? sb.ToString(0, 1200) + "\n…(完整清單見 Console)" : sb.ToString(),
            "執行綁定", "取消")) return;

        foreach (var p in plan)
        {
            Undo.RecordObject(p.box, "Bind reverse trigger");
            p.box.reverseAICars = new List<ReverseThreatAICar> { p.car };
            EditorUtility.SetDirty(p.box);
        }

        Debug.Log($"<color=lime>✅ 已綁定 {plan.Count} 個觸發盒,略過 {skipped.Count} 個</color>");
    }

    [MenuItem("Tools/列出倒車方塊的綁定狀態(診斷用)")]
    private static void ListBindings()
    {
        var boxes = Object.FindObjectsByType<ReverseTriggerController>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        var sb = new StringBuilder();
        int bound = 0;
        foreach (var box in boxes.OrderBy(b => b.name))
        {
            var list = box.reverseAICars == null
                ? new List<ReverseThreatAICar>()
                : box.reverseAICars.Where(c => c != null).ToList();
            if (list.Count > 0) bound++;
            sb.AppendLine(list.Count == 0
                ? $"  {box.name}  →  (未綁定,走動態搜尋)"
                : $"  {box.name}  →  {string.Join(", ", list.Select(c => c.name))}");
        }
        Debug.Log($"<color=cyan>🔗 觸發盒 {boxes.Length} 個,已綁定 {bound} 個</color>\n{sb}");
    }

    /// <summary>水平距離。演員車與觸發盒的高度差(約 0.3m)不該影響配對判斷。</summary>
    private static float Flat(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
#endif
