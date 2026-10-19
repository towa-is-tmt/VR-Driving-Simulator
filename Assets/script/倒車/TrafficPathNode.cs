using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
public class TrafficPathNode : MonoBehaviour
{
    // 💡 擴充列舉：整合了基礎轉向與 1456 的停讓 (Stop) / 預判減速 (Slow) 功能
    public enum IntersectionType { None, Normal, LeftTurnOnly, RightTurnOnly, Stop, Slow, BigStop }
    [Header("🛑 路口與節點設定")]
    public IntersectionType intersectionType = IntersectionType.None;
    public List<TrafficPathNode> nextChoices = new List<TrafficPathNode>();
    [Header("🔵 貝茲觸發距離覆寫")]
    [Tooltip("此節點專屬的貝茲觸發距離。0 = 使用車輛自己的預設值。窄路口建議 7，大路口 10~12")]
    public float bezierTriggerOverride = 0f;
    [Header("道路與避讓設定")]
    [Tooltip("勾選此欄位代表此處為【窄路/巷弄】")]
    public bool isNarrowRoad = false;
    [Tooltip("如果這裡有專屬的實體避讓點，請拖曳到這裡")]
    public TrafficPathNode yieldNode;
    [Header("🚑 救護車避讓狀態 (執行階段自動指派)")]
    public bool isOccupied = false;
    public int occupiedByVehicleID = 0;

    [Header("↰ 不需左轉禮讓")]
    [Tooltip("勾選後左轉不啟動對向讓直行偵測，也不會在轉完後上禮讓冷卻鎖。與灰車路網的 disableLeftTurnYield 同義")]
    public bool disableLeftTurnYield = false;

    [Header("🟡 黃色網狀線設定")]
    [Tooltip("勾選此欄位代表此節點位於黃色網狀線範圍內")]
    public bool isInsideYellowGrid = false;

    [Header("🚦 紅綠燈停等區設定")]
    [Tooltip("勾選此欄位代表此節點就是紅燈時應該蠕行接近的停止線節點")]
    public bool isTrafficLightStopZone = false;

    [Header("🚸 速限")]
    // 0 = 使用 PlayerSpeedLimitMonitor 上的全域預設(市區一般道路)。
    // 只有「不是預設值」的路段才需要標:學校/巷弄 30、快速道路 70。
    // 整條路可以在 Hierarchy 多選之後一次設定,不必逐顆點。
    [Tooltip("這個路段的速限(公里/小時)。0 = 用全域預設值,只標例外路段就好")]
    public int speedLimitKmH = 0;

    [Header("↔️ 雙向分隔道 (雙黃線)")]
    // 逆向偵測只在「有分向設施的大路」上才有意義。窄路/巷弄在路網上雖然只有
    // 一條單向節點鏈,實際上雙向都能通行,怎麼調參數都會誤報。
    //
    // 白名單(標了才判定)比黑名單(排除窄路)可靠:沒標的路自動豁免,零誤報。
    // 而且要標的是「有雙黃線的大路」,數量遠少於窄路。
    [Tooltip("這個路段是有分向線的雙向道(雙黃線)。PlayerWrongWayDetector 開啟白名單模式時,只有標記過的路段才做逆向判定")]
    public bool isDividedRoad = false;

    [Header("🚑 救護車左偏量覆寫")]
    // 救護車平常走在路網中線往左偏 ambulanceLaneLeftOffset(2m)的線上,
    // 那是為了避開往右讓道的車流。但在三線道上,那 2m 會把它推向內側車道,
    // 跟內側車道的車互相干擾。這個欄位讓你逐路段指定不同的偏移量。
    //
    // ⚠️ 哨兵值是「負數」而不是 0 —— 因為 0 是你會真的想設的值
    //    (三線道上讓救護車走車道正中央)。用 0 當哨兵就永遠設不出那個效果。
    [Tooltip("這個路段的救護車左偏量(公尺)。負數 = 不覆寫,用救護車自己的預設值;" +
             "0 = 走車道正中央不偏移;正數 = 指定偏移多少")]
    public float ambulanceLaneOffsetOverride = -1f;

#if UNITY_EDITOR
    // 🔧 節點連線上「XX.Xm」距離數字的顯示開關
    // 由選單 Tools > 路網 Gizmo > 顯示節點距離數字 切換,設定會記在 EditorPrefs
    // 只影響數字,節點球與連線不受影響
    public static bool ShowDistanceLabels = false;
#endif

    // 開發階段的視覺化輔助 (Gizmos)
    private void OnDrawGizmos()
    {
        // 1. 根據節點狀態或類型決定球體顏色
        if (isOccupied)
            Gizmos.color = Color.red; // 被佔用時顯示紅色
        else if (intersectionType == IntersectionType.BigStop)
            Gizmos.color = new Color(0.9f, 0.1f, 0.1f); // BigStop 大路口停讓 (亮鮮紅)
        else if (intersectionType == IntersectionType.Stop)
            Gizmos.color = Color.red; // Stop 節點
        else if (intersectionType == IntersectionType.Slow)
            Gizmos.color = new Color(1f, 0.5f, 0f); // Slow 節點 (橘色)
        else if (isInsideYellowGrid)
            Gizmos.color = Color.yellow; // 黃網格節點
        else
            Gizmos.color = isNarrowRoad ? new Color(1f, 0.6f, 0f) : Color.cyan; // 窄路橘黃色，一般節點青色
        Gizmos.DrawSphere(transform.position, intersectionType == IntersectionType.BigStop ? 0.6f : 0.4f);

        // ↰ 不需左轉禮讓的節點：外圍再畫一圈琥珀色，跟灰車路網的標示一致
        if (disableLeftTurnYield)
        {
            Gizmos.color = new Color(1f, 0.86f, 0.21f);
            Gizmos.DrawWireSphere(transform.position, 0.7f);
        }

        // 🚦 紅綠燈停等區節點：額外畫一個線框方塊標記，方便在場景中辨識
        if (isTrafficLightStopZone)
        {
            Gizmos.color = new Color(0f, 0.6f, 1f, 0.8f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.8f, new Vector3(0.6f, 0.6f, 0.6f));
        }

        // 2. 畫出前往下一個節點的連線
        if (nextChoices != null)
        {
            Gizmos.color = Color.white;
            foreach (var next in nextChoices)
            {
                if (next != null)
                {
                    Vector3 startPos = transform.position;
                    Vector3 endPos = next.transform.position;
                    Gizmos.DrawLine(startPos, endPos);
#if UNITY_EDITOR
                    if (ShowDistanceLabels)
                    {
                        float distance =
                            Vector3.Distance(startPos, endPos);
                        Vector3 middle =
                            (startPos + endPos) * 0.5f;
                        middle += Vector3.up * 1.0f;
                        Handles.color = Color.yellow;
                        Handles.Label(
                            middle + Vector3.up * 0.5f,
                            distance.ToString("F1") + "m");
                    }
#endif
                }
            }
        }
        // 3. 畫出與實體避讓點的關聯線
        if (yieldNode != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, yieldNode.transform.position);
        }
    }
#if UNITY_EDITOR
    // 🔵 選取此節點時，預覽車輛從各方向進彎的貝茲軌跡（跟 ReverseThreatAICar 同一套公式）
    private void OnDrawGizmosSelected()
    {
        if (nextChoices == null || nextChoices.Count == 0) return;

        // 找出所有連向我的上游節點，每個進彎方向畫一條預測曲線
        TrafficPathNode[] all = FindObjectsByType<TrafficPathNode>(FindObjectsSortMode.None);
        float triggerDist = bezierTriggerOverride > 0f ? bezierTriggerOverride : 10f; // 10 = 車輛預設值，改過要同步

        foreach (var prev in all)
        {
            if (prev == this || prev.nextChoices == null || !prev.nextChoices.Contains(this)) continue;

            Vector3 inDir = (transform.position - prev.transform.position).normalized;
            Vector3 p0 = transform.position - inDir * triggerDist; // 車在觸發距離處啟動貝茲的位置

            foreach (var next in nextChoices)
            {
                if (next == null) continue;
                Vector3 outDir = (next.transform.position - transform.position).normalized;
                if (Vector3.Angle(inDir, outDir) < 15f) continue; // 15 = 車輛的轉彎角度門檻，直線不畫

                // 跟車輛同規則：出口夠近→跨節點合併(P2=出口)；太遠→短程(P2=中點)。25 = mergeNodeDistanceThreshold
                Vector3 p2 = Vector3.Distance(transform.position, next.transform.position) < 25f
                    ? next.transform.position
                    : (transform.position + next.transform.position) / 2f;

                                // ⚠️ 0.6f 要跟車輛 Prefab 的 curveThroughNodeWeight 同步，不然預覽跟實跑不一致
                Vector3 p1 = Vector3.Lerp(transform.position,
                    2f * transform.position - 0.5f * (p0 + p2), 0.6f);

                Gizmos.color = Color.magenta;
                Vector3 prevPt = p0;
                for (int i = 1; i <= 24; i++)
                {
                    float t = i / 24f;
                    float omt = 1f - t;
                    Vector3 pt = omt * omt * p0 + 2f * omt * t * p1 + t * t * p2;
                    Gizmos.DrawLine(prevPt, pt);
                    prevPt = pt;
                }
            }
        }
    }
#endif
}
