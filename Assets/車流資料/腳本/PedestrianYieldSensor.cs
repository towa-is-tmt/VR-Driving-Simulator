using UnityEngine;

/// <summary>
/// 🚶🚗 行人禮讓感測器 —— 掛在 AI 車上（白車 ReverseThreatAICar / 灰車 CarNavigation
/// 會在各自 Start 裡自動 AddComponent，不必手動加到 prefab）。
///
/// 每隔一小段時間往車前掃一個盒子，偵測到「背景行人 PedestrianWalker 擋在車的行進路徑上」
/// 就叫車降速蠕行、很近就停；行人離開就放行。若行人卡在路中央太久（逾時），
/// 就緩慢推過去，避免整排車被永久卡死（＝使用者選的方案 B）。
///
/// 純加值：只透過車的 SetPedestrianYield() 掛勾控速，完全不碰車的避讓/回歸核心。
/// 只認 PedestrianWalker，所以不會去理「行人衝出（CrossingPedestrian）」那套針對玩家的危險因子。
/// </summary>
public class PedestrianYieldSensor : MonoBehaviour
{
    [Header("偵測範圍")]
    [Tooltip("車前偵測距離（公尺）")]
    public float detectDistance = 8f;
    [Tooltip("行人比這個距離還近就直接停（公尺）")]
    public float stopDistance = 2.5f;
    [Tooltip("偵測盒的半寬（公尺），約一個車道；太寬會把人行道上的行人也算進來")]
    public float halfWidth = 1.2f;
    [Tooltip("偵測盒高度（公尺）")]
    public float height = 1.8f;
    [Tooltip("要掃描的層。預設全部，靠 PedestrianWalker 元件過濾；想省效能可只留行人所在的層")]
    public LayerMask scanLayers = ~0;

    [Header("蠕行 / 逾時（方案 B）")]
    [Tooltip("偵測到行人時，降到原速的幾成（蠕行）")]
    [Range(0f, 1f)] public float creepFactor = 0.10f;
    [Tooltip("連續禮讓超過這麼久，就緩慢推過去，避免行人卡路中央塞死交通（秒）")]
    public float yieldTimeout = 6f;
    [Tooltip("逾時後推過去用的動力，比蠕行再快一點點")]
    [Range(0f, 1f)] public float pushThroughFactor = 0.18f;

    [Header("效能")]
    [Tooltip("幾秒掃一次（不必每幀）")]
    public float scanInterval = 0.15f;

    private ReverseThreatAICar white;
    private CarNavigation gray;
    private float nextScanTime;
    private float yieldingSince = -1f;   // 開始連續禮讓的時間；-1 = 目前沒在禮讓

    private void Awake()
    {
        white = GetComponent<ReverseThreatAICar>();
        gray = GetComponent<CarNavigation>();
    }

    private void Update()
    {
        if (Time.time < nextScanTime) return;
        nextScanTime = Time.time + Mathf.Max(0.03f, scanInterval);

        float pedDist = NearestPedestrianAhead();

        if (pedDist < 0f)
        {
            // 車前沒有擋路的行人 → 交還速度控制權
            if (yieldingSince >= 0f) { yieldingSince = -1f; ApplyYield(-1f); }
            return;
        }

        if (yieldingSince < 0f) yieldingSince = Time.time;
        float yieldingFor = Time.time - yieldingSince;

        float factor;
        if (yieldingFor >= yieldTimeout)
            factor = pushThroughFactor;      // 卡太久 → 緩慢推過去（防死結）
        else if (pedDist <= stopDistance)
            factor = 0f;                     // 行人很近 → 停
        else
            factor = creepFactor;            // 前方有行人 → 蠕行慢慢逼近

        ApplyYield(factor);
    }

    /// <summary>
    /// 回傳最近一個「擋在車行進路徑上」的行人距離（沿車頭方向的前方距離）；沒有就回 -1。
    /// </summary>
    private float NearestPedestrianAhead()
    {
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) return -1f;
        fwd.Normalize();

        Vector3 center = transform.position + Vector3.up * (height * 0.5f)
                       + fwd * (detectDistance * 0.5f);
        Vector3 half = new Vector3(halfWidth, height * 0.5f, detectDistance * 0.5f);

        Collider[] hits = Physics.OverlapBox(center, half,
            Quaternion.LookRotation(fwd, Vector3.up), scanLayers, QueryTriggerInteraction.Ignore);

        float nearest = -1f;
        foreach (Collider c in hits)
        {
            if (c == null) continue;
            PedestrianWalker ped = c.GetComponentInParent<PedestrianWalker>();
            if (ped == null) continue;

            Vector3 to = ped.transform.position - transform.position; to.y = 0f;
            float ahead = Vector3.Dot(to, fwd);
            if (ahead <= 0f || ahead > detectDistance) continue;   // 在後方或超出距離
            if (nearest < 0f || ahead < nearest) nearest = ahead;
        }
        return nearest;
    }

    /// <summary>把禮讓係數丟給這台車（白車或灰車）。負數 = 解除。</summary>
    private void ApplyYield(float factor01)
    {
        if (white != null) white.SetPedestrianYield(factor01);
        else if (gray != null) gray.SetPedestrianYield(factor01);
    }
}
