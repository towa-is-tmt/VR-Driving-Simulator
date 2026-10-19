using UnityEngine;

/// <summary>
/// 📏 安全距離監看（兩秒法則）
///
/// 掛在玩家車上（有 Rigidbody 的那個物件）。
///
/// 判定標準用**兩秒法則**：安全距離 = 車速(m/s) × 2。
/// 這是市區駕訓實際教的標準，也對應「反應時間 + 煞停距離」的物理現實。
/// 沒有採用高速公路的「時速一半（公尺）」，因為那條是高快速公路專用，套到市區會過寬；
/// 也沒有用固定公尺數，因為那在慢速時過嚴、快速時過鬆，跟真實判斷邏輯不符。
///
/// 涵蓋 ReverseThreatAICar（白車 / 側切車 / 救護車）與 CarNavigation（灰車 / 機車）。
/// </summary>
public class FollowingDistanceMonitor : MonoBehaviour
{
    [Header("兩秒法則")]
    [Tooltip("安全距離 = 車速(m/s) × 這個秒數。2 秒是乾燥路面的通用標準，雨天建議 3～4")]
    public float safeSeconds = 2f;
    [Tooltip("安全距離的下限。塞車或低速時貼近本來就正常，不該一直警告")]
    public float minSafeDistance = 5f;

    [Header("TTC 前方防撞警示（快撞上了）")]
    // 距離本身不代表危險，時間才代表：
    //   同樣 20m，前車同速行駛 = 毫無危險；前車停住 = 1.8 秒後撞上。
    // 固定距離規則分不出這兩種，TTC 可以。
    //
    // 而且 TTC 直接對應「你剎不剎得住」—— 你需要的時間 = 反應時間 + 煞停時間，
    // 兩者同單位可以直接比較。真實車輛的前方防撞警示（FCW）就是用 TTC，業界標準 2~2.5 秒。
    [Tooltip("碰撞剩餘時間低於這個秒數就警告。真實 FCW 系統大多設在 2~2.5 秒")]
    public float ttcWarningSeconds = 2.5f;
    [Tooltip("接近速度低於這個值就不算 TTC。同速跟車時接近速度趨近 0，那種情況交給兩秒法則（m/s）")]
    public float minClosingSpeed = 0.5f;
    [Tooltip("TTC 判定的持續時間門檻。比兩秒法則短，因為這是急迫狀況（秒）")]
    public float ttcSustainSeconds = 0.4f;

    [Header("極近距離（快撞上了）")]
    // 上面的 TTC 判定需要「正在接近」才成立，
    // 但在紅燈前用 3 km/h 慢慢貼上去也是會撞的，只是慢而已。
    // 這一層不看接近速度，只要還在往前移動、而且距離已經很近就警告。
    [Tooltip("間距小於這個值就視為快撞上（公尺）")]
    public float criticalDistance = 3f;
    [Tooltip("只要朝前速度高於這個值就算還在移動（km/h）。停住排隊不會觸發")]
    public float criticalMinSpeedKmH = 1f;

    [Header("防擾民保護")]
    [Tooltip("兩秒法則的最低判定車速。低於此改由上面的固定距離保險接手（km/h）")]
    public float minCheckSpeedKmH = 20f;
    [Tooltip("要連續跟太近這麼久才警告。短暫的切入、變換車道不該算違規（秒）")]
    public float sustainSeconds = 1.5f;
    [Tooltip("兩次警告之間的最短間隔（秒）")]
    public float cooldownSeconds = 10f;

    [Header("前方偵測範圍")]
    [Tooltip("偵測走廊的長度。要能涵蓋高速時的安全距離")]
    public float corridorLength = 45f;
    [Tooltip("走廊的半寬。約自己車道的寬度，太寬會把隔壁車道的車算進來")]
    public float corridorHalfWidth = 1.4f;
    [Tooltip("走廊從車身中心往前多遠開始。應該設在車頭位置，太大會漏掉貼很近的車")]
    public float corridorStartOffset = 1.8f;
    [Tooltip("車身中心到車頭的距離。間距從這個點量起，量出來才是真正的「保險桿到前車」的距離。\n設 0 的話會從車身中心量，會多算半個車長")]
    public float frontBumperOffset = 2f;
    // 沒有這道檢查的話，轉彎時偵測走廊會跟著車頭掃過對向車道，
    // 掃到迎面來車就報「請保持安全距離」—— 而且對向車是相對高速接近，
    // 兩秒法則的門檻一下就破，特別容易誤報。
    [Tooltip("前車的車頭必須與我同向。0.5≈夾角 60 度內。用來排除對向來車與橫向車流")]
    [Range(-1f, 1f)]
    public float sameDirectionDot = 0.5f;

    [Header("警告顯示")]
    [TextArea(2, 3)]
    [Tooltip("{0} 會被換成實際距離。訊息類型靠顏色區分，不用圖示")]
    public string warningMessage = "與前車距離 {0:F0} 公尺\n請保持安全距離";
    public Color warningColor = new Color(1f, 0.62f, 0.05f);
    public float displaySeconds = 4f;

    [Header("警告音效（可選）")]
    public AudioClip warningSound;
    public AudioSource warningAudioSource;
    [Range(0f, 2f)]
    public float warningVolume = 0.8f;

    [Header("診斷")]
    public bool logWarnings = true;
    [Tooltip("持續印出當下的間距與 TTC，用來確認程式看到的距離跟你眼睛看到的是否一致")]
    public bool debugLogGap = false;

    private float nextDebugTime = 0f;

    /// <summary>累計未保持安全距離的次數，可供考照評分讀取</summary>
    public int WarningCount { get; private set; }

    private Rigidbody rb;
    private float tooCloseTimer = 0f;
    private float nextAllowedTime = -1f;
    private float prevGap = Mathf.Infinity;   // 上一幀的間距，用來算接近速度

    private const float MsToKmH = 3.6f;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();
    }

    private void Update()
    {
        if (rb == null) return;

        float speedMs = Vector3.Dot(rb.velocity, transform.forward);   // 只算朝前的速度
        float speedKmH = speedMs * MsToKmH;

        float gap = GetDistanceToVehicleAhead();
        if (float.IsInfinity(gap))
        {
            tooCloseTimer = 0f;
            prevGap = Mathf.Infinity;
            if (debugLogGap && Time.time >= nextDebugTime)
            {
                nextDebugTime = Time.time + 0.5f;
                Debug.Log($"[安全距離診斷] 前方走廊內沒偵測到車輛（車速 {speedKmH:F0} km/h）");
            }
            return;
        }

        // 接近速度：間距每秒縮小多少。用間距變化率算，不必知道前車的速度
        float closingSpeed = float.IsInfinity(prevGap)
            ? 0f
            : (prevGap - gap) / Mathf.Max(0.0001f, Time.deltaTime);
        prevGap = gap;

        // ── 主判定：兩秒法則（需達到最低判定車速）──
        float required = Mathf.Max(minSafeDistance, speedMs * safeSeconds);
        bool twoSecondViolation = speedKmH >= minCheckSpeedKmH && gap < required;

        // ── 輔助判定：TTC 前方防撞。只有正在接近時才有意義，
        //    同速跟車的接近速度趨近 0，TTC 無限大不會誤報。
        float ttc = closingSpeed > minClosingSpeed ? gap / closingSpeed : Mathf.Infinity;
        bool ttcViolation = ttc <= ttcWarningSeconds;

        // ── 極近距離：不看接近速度，只要還在往前移動就警告。
        //    紅燈前用 3 km/h 慢慢貼上去一樣會撞，只是撞得慢而已。
        bool criticalViolation = gap < criticalDistance && speedKmH >= criticalMinSpeedKmH;

        if (debugLogGap && Time.time >= nextDebugTime)
        {
            nextDebugTime = Time.time + 0.5f;
            string ttcText = float.IsInfinity(ttc) ? "∞（沒在接近）" : $"{ttc:F1}s";
            Debug.Log($"[安全距離診斷] 間距 {gap:F1}m｜車速 {speedKmH:F0} km/h｜接近 {closingSpeed:F1} m/s｜" +
                      $"TTC {ttcText}｜兩秒法則需 {required:F1}m｜" +
                      $"觸發：兩秒={twoSecondViolation} TTC={ttcViolation} 極近={criticalViolation}" +
                      $"｜冷卻剩 {Mathf.Max(0f, nextAllowedTime - Time.time):F1}s");
        }

        if (!twoSecondViolation && !ttcViolation && !criticalViolation)
        {
            tooCloseTimer = 0f;
            return;
        }

        // 越急迫的判定，持續時間門檻越短
        float needSustain = criticalViolation ? 0f
                          : ttcViolation ? ttcSustainSeconds
                          : sustainSeconds;

        tooCloseTimer += Time.deltaTime;
        if (tooCloseTimer >= needSustain)
        {
            string rule = criticalViolation ? "極近距離"
                        : ttcViolation ? $"TTC {ttc:F1}s"
                        : "兩秒法則";
            Warn(gap, required, speedKmH, rule);
        }
    }

    /// <summary>回傳正前方最近一台 AI 車的實際間距，沒有就回 Infinity。</summary>
    private float GetDistanceToVehicleAhead()
    {
        Vector3 center = transform.TransformPoint(
            new Vector3(0f, 0.5f, corridorStartOffset + corridorLength * 0.5f));
        Vector3 halfExtents = new Vector3(corridorHalfWidth, 0.9f, corridorLength * 0.5f);

        Collider[] hits = Physics.OverlapBox(center, halfExtents, transform.rotation,
                                             ~0, QueryTriggerInteraction.Ignore);

        // 從車頭量，不是從車身中心。用中心量的話會多算半個車長，
        // 保險桿都快貼上了（實際 1m）程式卻算成 3m，所有門檻都會偏鬆。
        Vector3 measureFrom = transform.TransformPoint(new Vector3(0f, 0.5f, frontBumperOffset));

        float nearest = Mathf.Infinity;
        foreach (var h in hits)
        {
            if (h == null) continue;
            if (h.transform.IsChildOf(transform.root)) continue;   // 排除自己

            Transform ai = GetAIVehicleRoot(h);
            if (ai == null) continue;

            // 🚗 只有同向行駛的車才算「前車」。
            //    你不可能跟在一台迎面開來的車後面 —— 那是對向車，不是跟車對象。
            if (Vector3.Dot(transform.forward, ai.forward) < sameDirectionDot) continue;

            // 用碰撞體邊界的最近點算實際間距，不是車心到車心
            float d = Vector3.Distance(measureFrom, h.bounds.ClosestPoint(measureFrom));
            if (d < nearest) nearest = d;
        }
        return nearest;
    }

    /// <summary>
    /// 回傳這個碰撞體所屬的 AI 車 Transform，不是 AI 車就回 null。
    /// 從 bool 改成回傳 Transform，是因為呼叫端需要那台車的車頭方向來排除對向來車。
    /// </summary>
    private Transform GetAIVehicleRoot(Collider other)
    {
        var white = other.GetComponentInParent<ReverseThreatAICar>();   // 白車 / 側切 / 救護車
        if (white != null) return white.transform;

        var gray = other.GetComponentInParent<CarNavigation>();         // 灰車 / 機車
        if (gray != null) return gray.transform;

        return null;
    }

    private void Warn(float gap, float required, float speedKmH, string rule)
    {
        if (Time.time < nextAllowedTime) return;

        nextAllowedTime = Time.time + cooldownSeconds;
        tooCloseTimer = 0f;
        WarningCount++;

        if (HazardAwarenessTracker.Instance != null && !string.IsNullOrEmpty(warningMessage))
            HazardAwarenessTracker.Instance.ShowMessage(
                string.Format(warningMessage, gap), warningColor, displaySeconds);

        PlaySound();

        if (logWarnings)
            Debug.LogWarning($"📏 [安全距離／{rule}] 車速 {speedKmH:F0} km/h 應保持 {required:F1}m，" +
                             $"實際只有 {gap:F1}m（累計 {WarningCount} 次）");
    }

    private void PlaySound()
    {
        if (warningSound == null) return;

        if (warningAudioSource == null)
        {
            warningAudioSource = gameObject.AddComponent<AudioSource>();
            warningAudioSource.playOnAwake = false;
            warningAudioSource.loop = false;
            warningAudioSource.spatialBlend = 0f;   // 系統提示，2D
            warningAudioSource.dopplerLevel = 0f;
        }
        warningAudioSource.PlayOneShot(warningSound, warningVolume);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0f, 0.35f);
        Gizmos.matrix = Matrix4x4.TRS(
            transform.TransformPoint(new Vector3(0f, 0.5f, corridorStartOffset + corridorLength * 0.5f)),
            transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero,
            new Vector3(corridorHalfWidth * 2f, 1.8f, corridorLength));
    }
#endif
}
