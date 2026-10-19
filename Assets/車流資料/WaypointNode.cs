using UnityEngine;
using System.Collections.Generic;
using Unity.MLAgents;


#if UNITY_EDITOR
using UnityEditor;
#endif

public class WaypointNode : MonoBehaviour
{
    public enum LaneDirection { A, B, C, D }
    public enum IntersectionType { None, Stop, Slow, BigStop }

    [Header("分類設置")]
    public LaneDirection laneDirection;
    public IntersectionType intersectionType = IntersectionType.None;
    public List<WaypointNode> nextChoices;
    public float bezierTriggerDistance = 10f;

    [Header("紅綠燈停等點")]
    public bool isTrafficLightStopZone = false;

    [Header("黃色網狀線")]
    public bool isInsideYellowGrid = false;

    [Header("不需左轉禮讓")]
    [Tooltip("勾選後：1. 左轉不啟動對向讓直行偵測 / 2. 轉彎目標速度提升為 14f")]
    public bool disableLeftTurnYield = false;

    [Header("機車專用車道")]
    public bool isScooterLane = false;

    [Header("禁止機車偏移")]
    public bool disallowScooterOffset = false;

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

    [Header("顯示設置")]
#if UNITY_EDITOR
    // ⚡ 效能：以下開關由選單 Tools > 路網 Gizmo 切換，記在 EditorPrefs（見 TrafficPathGizmoToggle）
    //    舊版每個節點每一幀都顯示距離文字，1278 個節點 ≈ 上千個 Handles.Label，Scene 視窗會非常卡
    public static bool ShowDistanceLabels = false;   // 連線中間的「XX.XXm」
    public static bool ShowStatusLabels = true;      // 分岔 ➔N / 死路 [ ? ] / 孤兒 [ + ] / STOP 等文字
    public static float LabelMaxDistance = 80f;      // 離 Scene 攝影機超過這個距離就不畫文字

    // 孤兒點快取：舊版每個節點每一幀都 FindObjectsByType 全掃一次（1278×1278 ≈ 160 萬次比對/幀）
    // 改成全體共用一份「有被連入的節點」清單，最多每秒重建一次
    private static readonly HashSet<WaypointNode> nodesWithIncoming = new HashSet<WaypointNode>();
    private static double orphanCacheTime = -999;
    private static int orphanCacheFrame = -1;

    // 文字樣式快取：舊版每次畫字都 new GUIStyle，每幀上千次配置記憶體
    private static GUIStyle cachedAlertStyle;
    private static GUIStyle cachedDistanceStyle;
    private static GUIStyle cachedIconStyle;
#endif
    private float gizmoVerticalOffset = 0.5f;
    private float arrowSize = 1.5f;

    private void OnDrawGizmos()
    {
        // 1. 根據方向決定基礎連線顏色
        Color baseColor = Color.white;
        switch (laneDirection)
        {
            case LaneDirection.A: baseColor = Color.red; break;
            case LaneDirection.B: baseColor = Color.yellow; break;
            case LaneDirection.C: baseColor = Color.green; break;
            case LaneDirection.D: baseColor = Color.blue; break;
        }
        // 如果勾選了「機車專用道」，覆蓋原本的顏色為紫色
        if (isScooterLane)
        {
            baseColor = new Color(0.75f, 0.25f, 1f); // 亮紫色
        }
        // 如果勾選了「不需左轉禮讓」覆蓋顏色
        if (disableLeftTurnYield)
        {
            baseColor = new Color(1f, 0.86f, 0.21f);
        }

        Vector3 offset = Vector3.up * gizmoVerticalOffset;
        Vector3 myPos = transform.position + offset;

        // 2. 畫出當前節點 (如果是特殊類型或網狀線，畫大一點)
        Gizmos.color = baseColor;
        bool isSpecial = (intersectionType != IntersectionType.None) || isInsideYellowGrid;
        Gizmos.DrawSphere(myPos, isSpecial ? 0.4f : 0.25f);

#if UNITY_EDITOR
        // 3. 繪製「停、慢、網狀線」特殊標示 (優化重疊邏輯)
        DrawIntersectionIcon(myPos);

        if (disallowScooterOffset)
        {
            Gizmos.color = new Color(210f / 255f, 233f / 255f, 255f / 255f, 1f);
            Gizmos.DrawWireSphere(myPos, 0.6f);
        }

        // 檢查目標點數量，如果大於 1 則在節點旁邊顯示數量
        // ---------------------------------------------------------
        int validChoicesCount = 0;
        if (nextChoices != null)
        {
            foreach (var node in nextChoices)
            {
                if (node != null) validChoicesCount++;
            }
        }

        bool drawLabels = ShowStatusLabels && IsNearSceneCamera(myPos);
        if (cachedAlertStyle == null)
        {
            cachedAlertStyle = new GUIStyle();
            cachedAlertStyle.fontSize = 13;
            cachedAlertStyle.fontStyle = FontStyle.Bold;
        }
        GUIStyle alertStyle = cachedAlertStyle;

        // 狀況 A：分岔路口提示 (目標點 > 1)
        if (!drawLabels) { }
        else if (validChoicesCount > 1)
        {
            alertStyle.normal.textColor = Color.cyan;
            alertStyle.alignment = TextAnchor.MiddleLeft;
            Handles.Label(myPos + Vector3.right * 0.4f + Vector3.up * 0.2f, "➔ " + validChoicesCount.ToString(), alertStyle);
        }
        // 狀況 B：死路提示 (主動未連出，目標點 = 0)
        else if (validChoicesCount == 0)
        {
            alertStyle.normal.textColor = Color.magenta;
            alertStyle.alignment = TextAnchor.MiddleLeft;
            // 飄在節點右側
            Handles.Label(myPos + Vector3.right * 0.4f + Vector3.up * 0.2f, "[ ? ]", alertStyle);
        }

        // 狀況 C：孤兒點提示 (被動未連入，沒有任何其他點連向我)
        if (drawLabels && CheckIfOrphanNode())
        {
            alertStyle.normal.textColor = Color.white;
            alertStyle.alignment = TextAnchor.MiddleRight;
            // 飄在節點左側，避免跟上面的右側文字撞在一起
            Handles.Label(myPos + Vector3.left * 0.4f + Vector3.up * 0.2f, "[ + ]", alertStyle);
        }
#endif

        if (nextChoices != null)
        {
            foreach (var next in nextChoices)
            {
                if (next != null)
                {
                    Vector3 nextPos = next.transform.position + offset;
                    Gizmos.color = baseColor;

                    // 畫連線
                    Gizmos.DrawLine(myPos, nextPos);

#if UNITY_EDITOR
                    // 畫指向箭頭
                    DrawArrow(myPos, nextPos, baseColor);

                    // 畫距離文字
                    if (ShowDistanceLabels && IsNearSceneCamera(myPos))
                    {
                        float distance = Vector3.Distance(transform.position, next.transform.position);
                        // 文字放在兩點連線的正中間
                        Vector3 labelPos = (myPos + nextPos) * 0.5f;

                        if (cachedDistanceStyle == null)
                        {
                            cachedDistanceStyle = new GUIStyle();
                            cachedDistanceStyle.fontSize = 10;
                            cachedDistanceStyle.fontStyle = FontStyle.Bold;
                            cachedDistanceStyle.alignment = TextAnchor.MiddleCenter;
                        }
                        GUIStyle distanceStyle = cachedDistanceStyle;
                        distanceStyle.normal.textColor = baseColor; // 顏色跟隨車道

                        // 將文字稍微往上偏移 0.35m，避免跟箭頭完全重疊
                        Handles.Label(labelPos + Vector3.up * 0.35f, distance.ToString("F2") + "m", distanceStyle);
                    }
#endif
                }
            }
        }
    }

#if UNITY_EDITOR
    // 🔵 選取此節點時，預覽車輛從各方向進彎的貝茲軌跡（跟 CarNavigation 的 StartBezierTurn_Improved 同一套公式）
    private void OnDrawGizmosSelected()
    {
        if (nextChoices == null || nextChoices.Count == 0) return;

        // 找出所有連向我的上游節點，每個進彎方向畫一條預測曲線
        WaypointNode[] all = FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
        float triggerDist = bezierTriggerDistance > 0f ? bezierTriggerDistance : 10f;

        foreach (var prev in all)
        {
            if (prev == this || prev.nextChoices == null || !prev.nextChoices.Contains(this)) continue;

            Vector3 inDir = (transform.position - prev.transform.position).normalized;
            Vector3 p0 = transform.position - inDir * triggerDist; // 車在觸發距離處啟動貝茲的位置

            foreach (var next in nextChoices)
            {
                if (next == null) continue;
                Vector3 outDir = (next.transform.position - transform.position).normalized;
                if (Vector3.Angle(inDir, outDir) < 15f) continue; // 15 = 轉彎角度門檻，直線不畫

                // 跟 CarNavigation 同規則：出口夠近→跨節點合併(P2=出口)；太遠→短程(P2=中點)。25 = 合併距離門檻
                Vector3 p2 = Vector3.Distance(transform.position, next.transform.position) < 25f
                    ? next.transform.position
                    : (transform.position + next.transform.position) / 2f;

                // 跟 StartBezierTurn_Improved 相同的貝茲控制點公式
                Vector3 p1 = 2f * transform.position - 0.5f * (p0 + p2);

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
#if UNITY_EDITOR
    // 檢查自己是不是完全沒人理的「孤兒點」
    private bool CheckIfOrphanNode()
    {
        // ⚡ 每秒最多重建一次共用快取（同一幀內絕不重建）；結果跟舊版逐一比對相同
        double now = EditorApplication.timeSinceStartup;
        if (orphanCacheFrame != Time.frameCount && now - orphanCacheTime > 1.0)
        {
            orphanCacheTime = now;
            orphanCacheFrame = Time.frameCount;
            nodesWithIncoming.Clear();
            foreach (var node in FindObjectsByType<WaypointNode>(FindObjectsSortMode.None))
            {
                if (node == null || node.nextChoices == null) continue;
                foreach (var nx in node.nextChoices)
                    if (nx != null && nx != node) nodesWithIncoming.Add(nx);
            }
        }
        return !nodesWithIncoming.Contains(this); // 沒人連我 = 孤兒
    }

    /// <summary>⚡ 只在 Scene 攝影機附近畫文字（Handles.Label 是 Gizmo 裡最貴的呼叫）</summary>
    private static bool IsNearSceneCamera(Vector3 pos)
    {
        Camera cam = Camera.current;
        if (cam == null) return true;
        return (cam.transform.position - pos).sqrMagnitude < LabelMaxDistance * LabelMaxDistance;
    }
    private void DrawIntersectionIcon(Vector3 pos)
    {
        if (cachedIconStyle == null)
        {
            cachedIconStyle = new GUIStyle();
            cachedIconStyle.alignment = TextAnchor.MiddleCenter;
            cachedIconStyle.fontStyle = FontStyle.Bold;
        }
        GUIStyle iconStyle = cachedIconStyle;
        bool drawText = ShowStatusLabels && IsNearSceneCamera(pos);

        // ---------------------------------------------------------
        // 方案：黃色網狀線改在最高空（2.2m）繪製超大醒目的純文字/符號標籤
        // ---------------------------------------------------------
        if (isInsideYellowGrid)
        {
            Gizmos.color = Color.yellow;
            Vector3 groundPos = transform.position; // 地面高度

            // 地面黃色網格菱形 (Grid Pad) 依舊保留，方便俯瞰
            float radius = 0.6f;
            Vector3 pTop = groundPos + Vector3.forward * radius;
            Vector3 pBottom = groundPos + Vector3.back * radius;
            Vector3 pLeft = groundPos + Vector3.left * radius;
            Vector3 pRight = groundPos + Vector3.right * radius;
            Gizmos.DrawLine(pTop, pRight); Gizmos.DrawLine(pRight, pBottom);
            Gizmos.DrawLine(pBottom, pLeft); Gizmos.DrawLine(pLeft, pTop);
            Gizmos.DrawLine(pTop, pBottom); Gizmos.DrawLine(pLeft, pRight);

            Vector3 alertPos = pos + Vector3.up * 2.2f;

            iconStyle.normal.textColor = Color.yellow;
            iconStyle.fontSize = 18;
            if (drawText) Handles.Label(alertPos, "[ ! ]", iconStyle);
        }

        // ---------------------------------------------------------
        // STOP / BIG STOP / SLOW 交通規則標誌
        // ---------------------------------------------------------
        if (intersectionType == IntersectionType.BigStop)
        {
            // BIG STOP 放在最高的 1.8 公尺，使用較大尺寸與深紅/強烈顏色
            Vector3 bigStopPos = pos + Vector3.up * 1.8f;
            Color bigStopColor = new Color(0.9f, 0.1f, 0.1f); // 亮鮮紅/強烈紅色

            Gizmos.color = bigStopColor;
            Gizmos.DrawWireCube(bigStopPos, new Vector3(1.6f, 1.6f, 1.6f)); // 框框加大到 1.6m

            iconStyle.normal.textColor = bigStopColor;
            iconStyle.fontSize = 15; // 字體放大
            if (drawText) Handles.Label(bigStopPos, "BIG STOP", iconStyle);
        }
        else if (intersectionType == IntersectionType.Stop)
        {
            // STOP 放在 1.5 公尺高
            Vector3 stopPos = pos + Vector3.up * 1.5f;
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(stopPos, new Vector3(1.1f, 1.1f, 1.1f));

            iconStyle.normal.textColor = Color.red;
            iconStyle.fontSize = 13;
            if (drawText) Handles.Label(stopPos, "STOP", iconStyle);
        }
        else if (intersectionType == IntersectionType.Slow)
        {
            // SLOW 放在 1.1 公尺高
            Vector3 slowPos = pos + Vector3.up * 1.1f;
            Gizmos.color = new Color(1f, 0.5f, 0f); // 橘色
            Gizmos.DrawWireCube(slowPos, new Vector3(0.8f, 0.8f, 0.8f));

            iconStyle.normal.textColor = new Color(1f, 0.5f, 0f);
            iconStyle.fontSize = 13;
            if (drawText) Handles.Label(slowPos, "SLOW", iconStyle);
        }
        if (isTrafficLightStopZone)
        {
            Vector3 lightZonePos = pos + Vector3.up * 0.7f;
            Gizmos.color = new Color(1f, 0.86f, 0.21f);
            Gizmos.DrawWireCube(lightZonePos, new Vector3(0.8f, 0.8f, 0.8f));

            iconStyle.normal.textColor = new Color(1f, 0.86f, 0.21f);
            iconStyle.fontSize = 13;
            if (drawText) Handles.Label(lightZonePos, "", iconStyle);
        }
    }

    private void DrawArrow(Vector3 from, Vector3 to, Color color)
    {
        Handles.color = color;
        Vector3 direction = (to - from).normalized;
        if (direction == Vector3.zero) return;

        Vector3 arrowPos = Vector3.Lerp(from, to, 0.7f);
        Quaternion rotation = Quaternion.LookRotation(direction);

        Handles.ArrowHandleCap(0, arrowPos, rotation, arrowSize, EventType.Repaint);
    }
#endif
}
