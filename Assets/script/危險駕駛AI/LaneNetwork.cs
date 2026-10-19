using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 🛣 車道幾何快取：把白車 TrafficPathNode 與灰車 WaypointNode 兩套路網攤平成「節點 + 連線」，
/// 供危險駕駛 AI 查詢「玩家在哪條車道」「旁邊有沒有同向車道」「前面這段路直不直」。
///
/// ⚠️ 只讀路網，不修改任何節點，也不影響既有車流。
/// 做法跟 PlayerWrongWayDetector 的 BuildLaneCache 一致（窄路遮罩、灰車用距離判斷窄路）。
/// </summary>
public class LaneNetwork
{
    // ── 節點 ──
    public Vector3[] pos;
    public int[][] next;
    public int[][] prev;
    public bool[] isWhite;          // true = 白車 TrafficPathNode；false = 灰車 WaypointNode
    public bool[] isIntersection;   // 路口類節點（Stop / Slow / BigStop / Normal / 轉彎專用…）
    public bool[] isNarrow;         // 窄路（白車直接看旗標；灰車看離窄路節點多近）
    public bool[] isScooter;        // 灰車路網的機車專用道
    public int nodeCount;

    // ── 線段 ──
    public int[] segA;
    public int[] segB;
    public Vector3[] segDir;        // 水平、已正規化
    public float[] segLen;
    public int segCount;

    private static LaneNetwork cached;
    private static int cachedSceneHandle = -1;

    /// <summary>取得目前場景的路網快取（同一場景只建一次）</summary>
    public static LaneNetwork Get(float grayNarrowMaskRadius, bool log)
    {
        int handle = SceneManager.GetActiveScene().handle;
        if (cached != null && cachedSceneHandle == handle && cached.nodeCount > 0) return cached;

        cached = Build(grayNarrowMaskRadius, log);
        cachedSceneHandle = handle;
        return cached;
    }

    /// <summary>強制重建（路網在執行中被改動時用）</summary>
    public static void Invalidate() { cached = null; cachedSceneHandle = -1; }

    private static LaneNetwork Build(float grayNarrowMaskRadius, bool log)
    {
        var net = new LaneNetwork();

        TrafficPathNode[] whites = Object.FindObjectsOfType<TrafficPathNode>();
        WaypointNode[] grays = Object.FindObjectsOfType<WaypointNode>();

        int total = whites.Length + grays.Length;
        net.nodeCount = total;
        net.pos = new Vector3[total];
        net.isWhite = new bool[total];
        net.isIntersection = new bool[total];
        net.isNarrow = new bool[total];
        net.isScooter = new bool[total];

        var whiteIndex = new Dictionary<TrafficPathNode, int>(whites.Length);
        var grayIndex = new Dictionary<WaypointNode, int>(grays.Length);
        var narrowPositions = new List<Vector3>();

        int k = 0;
        foreach (var n in whites)
        {
            whiteIndex[n] = k;
            net.pos[k] = n.transform.position;
            net.isWhite[k] = true;
            net.isIntersection[k] = n.intersectionType != TrafficPathNode.IntersectionType.None;
            net.isNarrow[k] = n.isNarrowRoad;
            if (n.isNarrowRoad) narrowPositions.Add(n.transform.position);
            k++;
        }
        foreach (var n in grays)
        {
            grayIndex[n] = k;
            net.pos[k] = n.transform.position;
            net.isWhite[k] = false;
            net.isIntersection[k] = n.intersectionType != WaypointNode.IntersectionType.None;
            net.isScooter[k] = n.isScooterLane;
            k++;
        }

        // 灰車節點沒有 isNarrowRoad → 用「離白車窄路節點多近」當遮罩（兩套路網疊在同一條路上）
        float r2 = grayNarrowMaskRadius * grayNarrowMaskRadius;
        for (int i = whites.Length; i < total; i++)
        {
            foreach (var np in narrowPositions)
            {
                if ((net.pos[i] - np).sqrMagnitude < r2) { net.isNarrow[i] = true; break; }
            }
        }

        // 連線
        var nextLists = new List<int>[total];
        var prevLists = new List<int>[total];
        for (int i = 0; i < total; i++) { nextLists[i] = new List<int>(2); prevLists[i] = new List<int>(2); }

        foreach (var n in whites)
        {
            if (n.nextChoices == null) continue;
            int a = whiteIndex[n];
            foreach (var nx in n.nextChoices)
            {
                int b;
                if (nx == null || !whiteIndex.TryGetValue(nx, out b) || b == a) continue;
                nextLists[a].Add(b);
                prevLists[b].Add(a);
            }
        }
        foreach (var n in grays)
        {
            if (n.nextChoices == null) continue;
            int a = grayIndex[n];
            foreach (var nx in n.nextChoices)
            {
                int b;
                if (nx == null || !grayIndex.TryGetValue(nx, out b) || b == a) continue;
                nextLists[a].Add(b);
                prevLists[b].Add(a);
            }
        }

        net.next = new int[total][];
        net.prev = new int[total][];
        var sa = new List<int>();
        var sb = new List<int>();
        for (int i = 0; i < total; i++)
        {
            net.next[i] = nextLists[i].ToArray();
            net.prev[i] = prevLists[i].ToArray();
            foreach (int b in nextLists[i]) { sa.Add(i); sb.Add(b); }
        }

        net.segCount = sa.Count;
        net.segA = sa.ToArray();
        net.segB = sb.ToArray();
        net.segDir = new Vector3[net.segCount];
        net.segLen = new float[net.segCount];
        for (int i = 0; i < net.segCount; i++)
        {
            Vector3 d = net.pos[net.segB[i]] - net.pos[net.segA[i]];
            d.y = 0f;
            net.segLen[i] = d.magnitude;
            net.segDir[i] = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
        }

        if (log)
            Debug.Log($"<color=cyan>🛣 [危險駕駛AI] 路網快取完成：白車節點 {whites.Length}、灰車節點 {grays.Length}、" +
                      $"線段 {net.segCount}、窄路節點 {narrowPositions.Count}</color>");
        return net;
    }

    /// <summary>點到線段的水平投影。回傳沿線段的比例 t（未夾限）與帶號側向距離（+ = 線段右側）</summary>
    public void ProjectOnSegment(int seg, Vector3 p, out float t, out float lateral, out float perpDist)
    {
        Vector3 a = pos[segA[seg]];
        Vector3 d = segDir[seg];
        Vector3 ap = p - a; ap.y = 0f;
        float along = Vector3.Dot(ap, d);
        t = segLen[seg] > 0.001f ? along / segLen[seg] : 0f;
        Vector3 right = Vector3.Cross(Vector3.up, d);
        lateral = Vector3.Dot(ap, right);
        perpDist = Mathf.Abs(lateral);
    }

    /// <summary>
    /// 找「位置 p、行進方向 fwd」所在的車道線段（同向、最近）。
    /// </summary>
    public bool FindLaneAt(Vector3 p, Vector3 fwd, float maxDist, float minDot, bool excludeNarrow,
                           out int bestSeg, out float bestLateral)
    {
        bestSeg = -1; bestLateral = 0f;
        float bestDist = maxDist;
        fwd.y = 0f; fwd.Normalize();

        for (int i = 0; i < segCount; i++)
        {
            if (Vector3.Dot(segDir[i], fwd) < minDot) continue;
            if (excludeNarrow && (isNarrow[segA[i]] || isNarrow[segB[i]])) continue;
            if (isScooter[segA[i]] || isScooter[segB[i]]) continue;

            float t, lat, perp;
            ProjectOnSegment(i, p, out t, out lat, out perp);
            if (t < -0.05f || t > 1.05f) continue;
            if (perp < bestDist) { bestDist = perp; bestSeg = i; bestLateral = lat; }
        }
        return bestSeg >= 0;
    }

    /// <summary>
    /// 🔍 診斷用：說明「為什麼找不到玩家所在車道」——最近的線段有多遠、方向差幾度、是不是被窄路／機車道排除。
    /// </summary>
    public string DescribeNearest(Vector3 p, Vector3 fwd, float minDot, bool excludeNarrow)
    {
        fwd.y = 0f; fwd.Normalize();
        float bestAny = float.MaxValue, bestAnyAngle = 0f;
        float bestSame = float.MaxValue;
        string sameNote = "";
        for (int i = 0; i < segCount; i++)
        {
            float t, lat, perp;
            ProjectOnSegment(i, p, out t, out lat, out perp);
            if (t < -0.05f || t > 1.05f) continue;
            if (perp < bestAny) { bestAny = perp; bestAnyAngle = Vector3.Angle(segDir[i], fwd); }
            if (Vector3.Dot(segDir[i], fwd) >= minDot && perp < bestSame)
            {
                bestSame = perp;
                bool narrow = isNarrow[segA[i]] || isNarrow[segB[i]];
                bool scooter = isScooter[segA[i]] || isScooter[segB[i]];
                sameNote = scooter ? "（機車道，被排除）" : (excludeNarrow && narrow ? "（窄路，被排除）" : "");
            }
        }
        string any = bestAny < float.MaxValue ? $"最近的路網線段 {bestAny:F1}m、方向差 {bestAnyAngle:F0}°" : "附近沒有任何路網線段";
        string same = bestSame < float.MaxValue ? $"最近的同向線段 {bestSame:F1}m{sameNote}" : "附近沒有同向線段";
        return any + "；" + same;
    }

    /// <summary>
    /// 在點 p 附近（半徑 tolerance 內）有沒有一條同向、非機車道的車道線段。
    /// 用來驗證「隔壁車道」在整段路上都真的存在。
    /// </summary>
    public bool HasLaneNear(Vector3 p, Vector3 dir, float tolerance, float minDot, bool excludeNarrow)
    {
        dir.y = 0f; dir.Normalize();
        for (int i = 0; i < segCount; i++)
        {
            if (Vector3.Dot(segDir[i], dir) < minDot) continue;
            if (isScooter[segA[i]] || isScooter[segB[i]]) continue;
            if (excludeNarrow && (isNarrow[segA[i]] || isNarrow[segB[i]])) continue;

            float t, lat, perp;
            ProjectOnSegment(i, p, out t, out lat, out perp);
            if (t < -0.1f || t > 1.1f) continue;
            if (perp <= tolerance) return true;
        }
        return false;
    }
}

/// <summary>
/// 📏 一條車道的中心線（Frenet 座標的參考線）。
/// 位置用 (s = 沿線距離, d = 側向偏移，+ = 右) 表示，換道就只是改 d。
/// 只會往前延伸（append），所以 s 值在整個事件中都穩定。
/// </summary>
public class LanePath
{
    private readonly LaneNetwork net;
    private readonly List<Vector3> pts = new List<Vector3>();
    private readonly List<float> cum = new List<float>();
    private readonly List<int> nodes = new List<int>();
    private readonly HashSet<int> visited = new HashSet<int>();

    public float Length { get { return cum.Count > 0 ? cum[cum.Count - 1] : 0f; } }
    public int PointCount { get { return pts.Count; } }

    private LanePath(LaneNetwork net) { this.net = net; }

    /// <summary>
    /// 從線段 seg 出發，往後找 back 公尺、往前找 fwd 公尺，每個節點挑「最直」的下一個節點。
    /// 轉角超過 maxTurnDeg 就停（代表路口轉彎，不屬於這條直行車道）。
    /// </summary>
    public static LanePath Build(LaneNetwork net, int seg, float back, float fwd, float maxTurnDeg)
    {
        var path = new LanePath(net);
        int a = net.segA[seg];
        int b = net.segB[seg];

        // 往後走
        var backNodes = new List<int> { a };
        var localVisited = new HashSet<int> { a, b };
        float acc = 0f;
        int cur = a;
        Vector3 dir = net.segDir[seg];
        while (acc < back)
        {
            int best = -1; float bestAng = maxTurnDeg;
            foreach (int p in net.prev[cur])
            {
                if (localVisited.Contains(p)) continue;
                Vector3 d = net.pos[cur] - net.pos[p]; d.y = 0f;
                if (d.sqrMagnitude < 0.0001f) continue;
                float ang = Vector3.Angle(dir, d);
                if (ang <= bestAng) { bestAng = ang; best = p; }
            }
            if (best < 0) break;
            Vector3 step = net.pos[cur] - net.pos[best]; step.y = 0f;
            acc += step.magnitude;
            dir = step.normalized;
            cur = best;
            localVisited.Add(best);
            backNodes.Add(best);
        }
        backNodes.Reverse();
        foreach (int n in backNodes) path.AppendNode(n);
        path.AppendNode(b);

        path.ExtendForward(fwd, maxTurnDeg);
        return path;
    }

    private void AppendNode(int n)
    {
        Vector3 p = net.pos[n];
        if (pts.Count == 0) { cum.Add(0f); }
        else
        {
            Vector3 d = p - pts[pts.Count - 1]; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            cum.Add(cum[cum.Count - 1] + d.magnitude);
        }
        pts.Add(p);
        nodes.Add(n);
        visited.Add(n);
    }

    /// <summary>往前延伸 meters 公尺。回傳實際有沒有延伸到。</summary>
    public bool ExtendForward(float meters, float maxTurnDeg)
    {
        if (pts.Count < 2) return false;
        float start = Length;
        int cur = nodes[nodes.Count - 1];
        Vector3 dir = pts[pts.Count - 1] - pts[pts.Count - 2]; dir.y = 0f; dir.Normalize();

        while (Length - start < meters)
        {
            int best = -1; float bestAng = maxTurnDeg;
            foreach (int nx in net.next[cur])
            {
                if (visited.Contains(nx)) continue;
                Vector3 d = net.pos[nx] - net.pos[cur]; d.y = 0f;
                if (d.sqrMagnitude < 0.0001f) continue;
                float ang = Vector3.Angle(dir, d);
                if (ang <= bestAng) { bestAng = ang; best = nx; }
            }
            if (best < 0) break;
            Vector3 step = net.pos[best] - net.pos[cur]; step.y = 0f;
            dir = step.normalized;
            cur = best;
            AppendNode(best);
        }
        return Length - start > 0.5f;
    }

    /// <summary>取沿線距離 s 處的位置與切線（s 超出範圍會沿端點切線外插）</summary>
    public void Sample(float s, out Vector3 p, out Vector3 tangent)
    {
        int n = pts.Count;
        if (n < 2) { p = n == 1 ? pts[0] : Vector3.zero; tangent = Vector3.forward; return; }

        int i = FindSegmentIndex(s);
        Vector3 a = pts[i], b = pts[i + 1];
        Vector3 d = b - a;
        float len = cum[i + 1] - cum[i];
        tangent = new Vector3(d.x, 0f, d.z).normalized;
        float t = len > 0.001f ? (s - cum[i]) / len : 0f;   // 不夾限 → 端點外插
        p = a + d * t;
    }

    private int FindSegmentIndex(float s)
    {
        int lo = 0, hi = pts.Count - 2;
        if (s <= cum[0]) return 0;
        if (s >= cum[pts.Count - 1]) return pts.Count - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (cum[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>把世界座標投影到這條線上：回傳 s，並輸出帶號側向距離（+ = 右）</summary>
    public float Project(Vector3 w, out float lateral)
    {
        lateral = 0f;
        int n = pts.Count;
        if (n < 2) return 0f;

        float bestD2 = float.MaxValue, bestS = 0f;
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 a = pts[i];
            Vector3 d = pts[i + 1] - a; d.y = 0f;
            float len2 = d.sqrMagnitude;
            if (len2 < 0.0001f) continue;
            Vector3 aw = w - a; aw.y = 0f;
            float t = Mathf.Clamp01(Vector3.Dot(aw, d) / len2);
            // 頭尾兩段允許外插，玩家/演員稍微超出路徑兩端也能算
            if (i == 0) t = Mathf.Min(t, Vector3.Dot(aw, d) / len2);
            if (i == n - 2) t = Mathf.Max(t, Vector3.Dot(aw, d) / len2);
            Vector3 proj = d * t;
            float d2 = (aw - proj).sqrMagnitude;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestS = cum[i] + Mathf.Sqrt(len2) * t;
                Vector3 right = Vector3.Cross(Vector3.up, d.normalized);
                lateral = Vector3.Dot(aw - proj, right);
            }
        }
        return bestS;
    }

    /// <summary>s0 ~ s1 之間車道方向的最大偏轉角（度）。用來判斷「這段路夠不夠直」</summary>
    public float HeadingChange(float s0, float s1)
    {
        Vector3 p, t0, t;
        Sample(s0, out p, out t0);
        float maxAng = 0f;
        for (float s = s0; s <= s1; s += 4f)
        {
            Sample(s, out p, out t);
            maxAng = Mathf.Max(maxAng, Vector3.Angle(t0, t));
        }
        return maxAng;
    }

    /// <summary>s0 ~ s1 之間有沒有路口類節點</summary>
    public bool HasIntersectionBetween(float s0, float s1)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (cum[i] < s0 || cum[i] > s1) continue;
            if (net.isIntersection[nodes[i]]) return true;
        }
        return false;
    }

    /// <summary>
    /// 驗證「偏移 lateralOffset 的隔壁車道」在 s0 ~ s1 整段都存在（每 step 公尺抽查一次）。
    /// 失敗時 failS 回報第一個斷掉的位置，方便 Debug。
    /// </summary>
    public bool AdjacentLaneContinuous(float s0, float s1, float lateralOffset, float step, float tolerance,
                                       float minDot, bool excludeNarrow, out float failS)
    {
        failS = -1f;
        for (float s = s0; s <= s1; s += step)
        {
            Vector3 p, t;
            Sample(s, out p, out t);
            Vector3 right = Vector3.Cross(Vector3.up, t);
            if (!net.HasLaneNear(p + right * lateralOffset, t, tolerance, minDot, excludeNarrow))
            {
                failS = s;
                return false;
            }
        }
        return true;
    }

    /// <summary>在 Scene 視窗畫出這條線與偏移線（除錯用）</summary>
    public void DrawDebug(Color color, float lateralOffset, float duration)
    {
        for (int i = 0; i < pts.Count - 1; i++)
        {
            Vector3 a = pts[i] + Vector3.up * 0.3f;
            Vector3 b = pts[i + 1] + Vector3.up * 0.3f;
            Debug.DrawLine(a, b, color, duration);
            if (Mathf.Abs(lateralOffset) > 0.01f)
            {
                Vector3 d = pts[i + 1] - pts[i]; d.y = 0f;
                Vector3 r = Vector3.Cross(Vector3.up, d.normalized) * lateralOffset;
                Debug.DrawLine(a + r, b + r, color * 0.6f, duration);
            }
        }
    }
}
