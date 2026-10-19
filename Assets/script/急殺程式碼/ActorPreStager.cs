using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🎬 前置演員佈署 —— 放在急煞／倒車觸發盒的「上游」。
///
/// 解決的問題：急煞需要「玩家正前方、同車道、行駛中的白車」，但車流是隨機的，
/// 那個位置常常剛好沒車，整個事件就靜靜地不發生，觸發盒等於白做。
///
/// 為什麼不在觸發盒當下生車（我原本的做法）：那時候玩家已經很近了，
/// 車一定生在他看得到的地方，會憑空冒出來。沒有遮蔽物的路段更藏不住。
///
/// 這支的做法是把時間拉長：玩家還在上游時就先檢查前方有沒有車，沒有就在
/// 指定路點生一台、指定它走哪條路，讓它「開」到定位。等玩家抵達急煞觸發盒時，
/// 那台車已經在前方跑了一段時間 —— 從玩家的角度看就是一台本來就在的車。
///
/// 前方本來就有車的話什麼都不做，不會多生。
/// </summary>
public class ActorPreStager : MonoBehaviour
{
    [Header("偵測：玩家前方到底有沒有車")]
    [Tooltip("往玩家前方掃多遠（公尺）。這個範圍內有車就不佈署")]
    public float scanRange = 70f;
    [Tooltip("掃描走廊的半寬（公尺）。約一個車道，太寬會把隔壁車道的車也算進來")]
    public float scanHalfWidth = 3f;
    [Tooltip("AI 車所在的層。跟觸發盒的 Ai Car Layer 設一樣")]
    public LayerMask aiCarLayer;
    [Tooltip("前方沒有白車、但車輛總數達到這個值就不佈署 —— 車道已經塞滿，生了也擠不進來")]
    public int crowdedThreshold = 3;

    [Header("佈署")]
    [Tooltip("要生成的白車 prefab（必須掛 ReverseThreatAICar）")]
    public GameObject actorPrefab;

    /// <summary>一條佈署路線：第一個節點是生成點，後面是它必須依序經過的節點。</summary>
    [System.Serializable]
    public class StageRoute
    {
        [Tooltip("第一個 = 生成點，後面依序是「一定要走這邊」的節點。\n" +
                 "只要填到「開上玩家會走的那條路」就好，之後會自動放手回到隨機選路。\n" +
                 "相鄰兩個節點必須是路網上真的接在一起的（後者要在前者的 Next Choices 裡）")]
        public List<TrafficPathNode> nodes = new List<TrafficPathNode>();
    }

    [Tooltip("佈署路線。可以放多條，每次隨機挑一條生成點沒被佔用的")]
    public List<StageRoute> routes = new List<StageRoute>();

    [Header("節流")]
    [Tooltip("同一個佈署器隔多久才允許再佈署一次（秒），避免玩家來回經過時生一堆車")]
    public float cooldownSeconds = 20f;
    [Tooltip("這個佈署器整場最多生幾台，防止意外情況下無限增生")]
    public int maxTotal = 4;

    [Header("診斷")]
    public bool logStaging = true;

    private float nextAllowedTime = -999f;
    private int stagedCount = 0;

    private void OnTriggerEnter(Collider other)
    {
        // 這兩條原本都是靜默 return —— 「明明沒白車卻不生車」查不出原因就是卡在這裡。
        // 尤其 Tag：盒子偵測到的可能是玩家車的子碰撞體，那個子物件不一定有 Player tag。
        if (!other.CompareTag("Player"))
        {
            if (logStaging)
                Debug.Log($"<color=grey>🎬 [前置佈署] {name} 有東西進來但 Tag 不是 Player" +
                          $"（是「{other.name}」，Tag={other.tag}）→ 忽略</color>", this);
            return;
        }

        if (logStaging)
            Debug.Log($"<color=white>🎬 [前置佈署] {name} 被玩家觸發</color>", this);

        if (Time.time < nextAllowedTime)
        {
            if (logStaging)
                Debug.Log($"<color=grey>🎬 [前置佈署] {name} 還在冷卻，剩 " +
                          $"{nextAllowedTime - Time.time:F1} 秒（Cooldown Seconds={cooldownSeconds}）</color>", this);
            return;
        }

        if (actorPrefab == null || routes == null || routes.Count == 0)
        {
            if (logStaging)
                Debug.LogWarning($"[前置佈署] {name} 沒有設定 Actor Prefab 或 Routes，不會佈署", this);
            return;
        }

        if (stagedCount >= maxTotal)
        {
            if (logStaging) Debug.Log($"<color=grey>🎬 [前置佈署] {name} 已達上限 {maxTotal} 台</color>", this);
            return;
        }

        ScanAhead(other.transform, out int whiteActors, out int totalVehicles);

        // 前方已經有能演的白車 → 什麼都不用做，這是最理想的情況
        if (whiteActors > 0)
        {
            if (logStaging)
                Debug.Log($"<color=grey>🎬 [前置佈署] {name} 玩家前方 {scanRange:F0}m 內已有 " +
                          $"{whiteActors} 台可用白車，不佈署</color>", this);
            return;
        }

        // 沒白車但車道已經塞滿（多半是灰車）→ 佈署一台也擠不進來，寧可不生
        if (totalVehicles >= crowdedThreshold)
        {
            if (logStaging)
                Debug.Log($"<color=grey>🎬 [前置佈署] {name} 前方沒有白車，但已有 {totalVehicles} 台車" +
                          $"（門檻 {crowdedThreshold}），車道太擠不佈署</color>", this);
            return;
        }

        StageRoute route = PickFreeRoute();
        if (route == null)
        {
            if (logStaging)
                Debug.LogWarning($"<color=orange>🎬 [前置佈署] {name} 的 {routes.Count} 條路線都不能用。\n" +
                    $"  可能原因：節點少於 2 個 / 第一個節點是空的 / 生成點 3m 內已經有車\n" +
                    $"  （Ai Car Layer 設錯的話「有沒有車」也會判錯）</color>", this);
            return;
        }

        if (Deploy(route)) nextAllowedTime = Time.time + cooldownSeconds;
    }

    /// <summary>
    /// 掃玩家正前方走廊，分開回報「可用的白車演員」與「車輛總數」。
    ///
    /// 這兩個數字的意義完全不同，不能混在一起：
    ///   白車 = 唯一能演急煞的角色（灰車 CarNavigation 根本沒有急煞功能），
    ///          有白車才代表事件演得出來 → 不需要佈署
    ///   總數 = 車道有多擠。前面塞滿灰車的話，就算佈署一台白車也擠不進來，
    ///          那種情況寧可不生，免得疊車或生出來排在最後面
    /// </summary>
    private void ScanAhead(Transform player, out int whiteActors, out int totalVehicles)
    {
        whiteActors = 0;
        totalVehicles = 0;

        Vector3 center = player.position + Vector3.up * 0.5f + player.forward * (scanRange * 0.5f);
        Vector3 half = new Vector3(scanHalfWidth, 1f, scanRange * 0.5f);

        Collider[] hits = Physics.OverlapBox(center, half, player.rotation, aiCarLayer,
                                             QueryTriggerInteraction.Ignore);
        HashSet<Transform> counted = new HashSet<Transform>();

        foreach (Collider c in hits)
        {
            if (c == null) continue;
            if (c.transform.IsChildOf(player.root)) continue;

            ReverseThreatAICar white = c.GetComponentInParent<ReverseThreatAICar>();
            CarNavigation gray = white == null ? c.GetComponentInParent<CarNavigation>() : null;

            Transform root = white != null ? white.transform : (gray != null ? gray.transform : null);
            if (root == null) continue;
            if (!counted.Add(root)) continue;         // 同一台車有多個 collider，只算一次

            totalVehicles++;

            // 路邊靜停的白車不能當演員（它是側切用的），救護車也有自己的任務
            if (white != null && !white.isStaticParked && !(white is AmbulanceAI))
                whiteActors++;
        }
    }

    private StageRoute PickFreeRoute()
    {
        List<StageRoute> free = new List<StageRoute>();
        foreach (StageRoute r in routes)
        {
            if (r == null || r.nodes == null || r.nodes.Count < 2) continue;
            if (r.nodes[0] == null) continue;
            if (Physics.CheckSphere(r.nodes[0].transform.position, 3f, aiCarLayer)) continue;
            free.Add(r);
        }
        if (free.Count == 0) return null;
        return free[Random.Range(0, free.Count)];
    }

    private bool Deploy(StageRoute route)
    {
        TrafficPathNode spawn = route.nodes[0];
        TrafficPathNode next = route.nodes[1];

        Vector3 dir = next.transform.position - spawn.transform.position;
        dir.y = 0f;
        Quaternion rot = dir.sqrMagnitude > 0.01f
            ? Quaternion.LookRotation(dir.normalized, Vector3.up)
            : spawn.transform.rotation;

        GameObject go = Instantiate(actorPrefab, spawn.transform.position, rot);
        SpawnGroundSnap.Apply(go);
        go.name = "Actor_PreStaged_" + Time.time;

        ReverseThreatAICar car = go.GetComponentInParent<ReverseThreatAICar>();
        if (car == null)
        {
            Debug.LogWarning($"[前置佈署] {name} 的 Actor Prefab（{actorPrefab.name}）" +
                             $"沒有 ReverseThreatAICar，不能當急煞演員", this);
            Destroy(go);
            return false;
        }

        car.isStaticParked = false;              // 要會跑，不是路邊停放
        car.currentWaypointNode = next;

        // 指定它開頭要走的路線，否則會在路口隨機轉走，玩家到了前方還是沒車
        car.SetForcedRoute(new List<TrafficPathNode>(route.nodes.GetRange(1, route.nodes.Count - 1)));

        stagedCount++;
        if (logStaging)
            Debug.Log($"<color=lime>🎬 [前置佈署] {name} 玩家前方沒車，已在 {spawn.name} 佈署一台" +
                      $"（指定路線 {route.nodes.Count - 1} 段，本場第 {stagedCount} 台）</color>", spawn);
        return true;
    }

    /// <summary>
    /// 事件開始時檢查手動路線的相鄰節點在路網上接不接得起來。
    /// 接不起來的話 SetForcedRoute 會靜靜跳過那一段，車就在路口轉走了 ——
    /// 這種錯從畫面上看不出來，所以要主動報。
    /// </summary>
    private void Start()
    {
        if (routes == null) return;

        for (int r = 0; r < routes.Count; r++)
        {
            StageRoute sr = routes[r];
            if (sr == null || sr.nodes == null) continue;

            for (int i = 0; i < sr.nodes.Count - 1; i++)
            {
                TrafficPathNode a = sr.nodes[i], b = sr.nodes[i + 1];
                if (a == null || b == null)
                {
                    Debug.LogWarning($"[前置佈署] {name} 路線 #{r} 的第 {i}/{i + 1} 個節點是空的", this);
                    continue;
                }
                if (a.nextChoices == null || !a.nextChoices.Contains(b))
                    Debug.LogWarning(
                        $"[前置佈署] {name} 路線 #{r}：「{a.name}」接不到「{b.name}」" +
                        $"（後者不在前者的 Next Choices 裡）。這一段會被跳過，車可能在路口轉錯邊。\n" +
                        $"請改拉路網上真的相鄰的節點", a);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        // 掃描走廊
        Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position + transform.forward * (scanRange * 0.5f),
                                      transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(scanHalfWidth * 2f, 2f, scanRange));
        Gizmos.matrix = Matrix4x4.identity;

        // 每條佈署路線
        if (routes == null) return;
        Gizmos.color = Color.green;
        foreach (StageRoute r in routes)
        {
            if (r == null || r.nodes == null) continue;
            for (int i = 0; i < r.nodes.Count; i++)
            {
                if (r.nodes[i] == null) continue;
                Gizmos.DrawWireSphere(r.nodes[i].transform.position, i == 0 ? 1.2f : 0.6f);
                if (i > 0 && r.nodes[i - 1] != null)
                    Gizmos.DrawLine(r.nodes[i - 1].transform.position, r.nodes[i].transform.position);
            }
        }
    }
}
