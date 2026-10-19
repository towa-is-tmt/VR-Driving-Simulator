using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🎬 危險駕駛事件觸發器（鬼切／路霸）
///
/// 場景放一個空物件掛上它即可（不需要放觸發盒）。它會每隔一段時間檢查玩家狀況：
///   1. 玩家速度夠快、所在路段夠直、附近沒有路口
///   2. 玩家車道旁邊真的有「同向車道」（用白車＋灰車路網驗證，窄路與機車道排除）
///   3. 生成位置淨空
///   4. 問 DangerEventDirector 准不准
/// 全部通過才生成演員車（AggressiveDriverActor）開始演出。
///
/// 每一關沒過都會在 Console 說明原因（每 5 秒最多一次），方便 Debug。
/// 測試：Play 中按 F7 = 鬼切、F9 = 路霸（可在 Inspector 關閉），或元件右上角選單。
/// </summary>
public class AggressiveDriverEventTrigger : MonoBehaviour
{
    [Header("🎭 演員車")]
    [Tooltip("建議直接用 模型prefab/切車.prefab。身上原本的 AI 腳本（ReverseThreatAICar、CarNavigation…）" +
             "會在生成時自動移除，燈號與輪子的綁定會自動複製過來。放多個就隨機挑")]
    public GameObject[] actorPrefabs;
    public AudioClip hornClip;
    public AudioClip screechClip;
    [Range(0f, 3f)] public float actorVolume = 1f;
    [Tooltip("車身離地高度修正")]
    public float groundOffset = 0f;
    [Tooltip("模型朝向修正（度）。模型車頭不是 +Z 時才需要改")]
    public float modelYawOffset = 0f;
    [Tooltip("生成時要移除的腳本類別名稱（避免原本的 AI 跟演員搶方向盤）")]
    public string[] stripScriptTypes =
    {
        "ReverseThreatAICar", "AmbulanceAI", "CarNavigation", "HazardAICar", "CityCarAI", "CityTrafficAI",
        "WaypointFollowerAI", "CarLogicController", "ConstantMove", "RoadsideBrakeHazard", "TargetCarController",
        "DecisionRequester", "BehaviorParameters", "SpawnGroundSnap"
    };

    [Header("👤 玩家（留空會用 Player 標籤自動尋找）")]
    public Transform player;
    public Rigidbody playerRigidbody;

    [Header("🎲 劇本")]
    public bool enableGhostCutIn = true;
    public bool enableRoadHog = true;
    [Min(0f)] public float ghostWeight = 1f;
    [Min(0f)] public float hogWeight = 1f;
    [Tooltip("鬼切可以從玩家左側車道切進來")]
    public bool ghostFromLeft = true;
    [Tooltip("鬼切可以從玩家右側車道切進來")]
    public bool ghostFromRight = true;
    [Tooltip("路霸可以從左側超車（台灣法規規定的超車側）")]
    public bool hogOvertakeLeft = true;
    [Tooltip("路霸可以從右側超車（違規，但真實路霸常這樣做）")]
    public bool hogOvertakeRight = true;

    [Header("⏱ 自動觸發條件")]
    [Tooltip("關掉 = 只能用測試鍵／右鍵選單觸發")]
    public bool autoTrigger = true;
    [Tooltip("每幾秒檢查一次")]
    public float scanInterval = 1f;
    [Tooltip("場景開始後至少過幾秒才會自動觸發（讓玩家先熟悉車輛）")]
    public float startDelaySeconds = 30f;
    [Tooltip("玩家時速至少多少才觸發")]
    public float minPlayerSpeedKmH = 25f;
    [Tooltip("玩家前方至少要有多長的直路（公尺）")]
    public float requiredStraightAhead = 90f;
    [Tooltip("這段直路允許的最大偏轉角（度）")]
    public float maxHeadingChange = 15f;
    [Tooltip("前方直路內有路口節點就不觸發")]
    public bool avoidIntersections = true;

    [Header("🛣 車道偵測")]
    // 📊 實測這張地圖的車道寬約 5m，玩家靠車道邊緣開時離節點連線可達 2.5m，所以取 3m
    [Tooltip("玩家離車道中心線（節點連線）多遠內，才算「在這條車道上」。這張地圖車道約 5m 寬，建議 3")]
    public float laneSnapMax = 3f;
    // 📊 實測 cc.unity 路網（2026-09-29）：白車↔灰車兩套路網疊在同一車道時，彼此側向距離大多 0~1.5m；
    //    真正的隔壁同向車道集中在 4.5~5.5m（這張地圖車道比現實寬）。所以範圍取 3~6m。
    [Tooltip("隔壁車道離玩家車道中心線的最小距離（公尺）。白車／灰車兩套路網疊在同一車道時彼此相距約 0~1.5m，要排除掉")]
    public float adjacentMin = 3f;
    [Tooltip("隔壁車道離玩家車道中心線的最大距離（公尺）。這張地圖實測的隔壁同向車道集中在 4.5~5.5m")]
    public float adjacentMax = 6f;
    [Tooltip("同向判定：線段方向與玩家方向的點積至少多少（0.95 ≈ 18°以內）")]
    [Range(0.5f, 1f)] public float laneMinDot = 0.95f;
    // 📊 實測：isNarrowRoad 旗標標得很寬鬆（原本是給救護車選路用的），開著會排除掉 78% 的路段，
    //    剩下的路段只有 8% 找得到隔壁車道，自動觸發幾乎不會發生。
    //    關掉也安全 —— 觸發前一定要找到「真的平行、同向」的隔壁車道，單線的窄路本來就過不了這一關。
    [Tooltip("排除窄路（isNarrowRoad）。建議關閉：這個旗標標得很寬，開著會讓大部分路段都無法觸發")]
    public bool excludeNarrowRoads = false;
    [Tooltip("灰車路網沒有窄路旗標，離白車窄路節點多近就一併視為窄路")]
    public float grayNarrowMaskRadius = 6f;
    [Tooltip("隔壁車道要連續存在多長（公尺，從生成點算起）")]
    public float adjacentCheckLength = 80f;

    [Header("📍 生成位置")]
    [Tooltip("鬼切：生成在玩家後方幾公尺（隔壁車道）")]
    public float ghostSpawnBehind = 14f;
    [Tooltip("路霸：生成在玩家後方幾公尺（同車道）")]
    public float hogSpawnBehind = 35f;
    [Tooltip("車輛所在的圖層。這個專案的車輛放在 TrafficLight 圖層（第 6 層），白車 detectionLayer 也是用它")]
    public LayerMask vehicleMask;
    [Tooltip("地面射線用的圖層")]
    public LayerMask groundMask;

    [Header("🎬 導演")]
    [Tooltip("觸發前先問 DangerEventDirector")]
    public bool useDirector = true;
    [Tooltip("演出期間鎖住導演（其他危險因子暫停），結束後才開始算冷卻")]
    public bool lockDirectorDuringEvent = true;

    [Header("🤖 強化學習模式（鬼切）")]
    [Tooltip("把訓練好的 GhostCutIn.onnx 拖進來。留空 = 一律用規則式")]
    public Unity.Barracuda.NNModel ghostCutInModel;
    [Tooltip("編輯器裡發現 Ghost Cut In Model 是空的（重新匯入 onnx 時 GUID 變了、引用斷掉）→ 自動找名稱以 GhostCutIn 開頭、最新的模型填回來")]
    public bool autoFindGhostModel = true;
    [Tooltip("有模型時，每次鬼切由模型駕駛的機率。1 = 全部用模型；0.5 = 一半一半（方便在真實場景比較兩種 AI）")]
    [Range(0f, 1f)] public float rlModeChance = 1f;

    [Header("🎓 自適應難度（機器學習）")]
    [Tooltip("留空會自動找場景中的 AdaptiveDifficultyBandit；場景沒有就用下面的固定參數")]
    public AdaptiveDifficultyBandit adaptiveDifficulty;

    [Header("⚙️ 演出參數（沒有自適應難度時使用；有的話作為基底，被難度等級覆寫）")]
    public AggressiveDriverSettings settings = new AggressiveDriverSettings();

    [Header("🧪 測試")]
    public bool enableHotkeys = true;
    public KeyCode ghostTestKey = KeyCode.F7;
    public KeyCode hogTestKey = KeyCode.F9;   // F8 已被車損圖表(DamageDiagramPanel)使用
    [Tooltip("測試鍵觸發時略過導演與速度門檻")]
    public bool hotkeyBypassDirector = true;
    [Tooltip("按下後把玩家車傳送到下面的測試路段起點（路網分析算出的最長直線多車道）")]
    public KeyCode teleportTestKey = KeyCode.F10;
    [Tooltip("測試路段起點（X, Z）與車頭方向（Y 角度）。預設 = A 路段：約 400m 直線多車道")]
    public Vector3 testSpotXZYaw = new Vector3(786f, -181f, 289f);

    [Header("🔧 診斷")]
    public bool logChecks = true;
    [Tooltip("觸發時在 Scene 視窗畫出車道參考線（紫色）")]
    public bool drawPaths = true;
    [Tooltip("演員車輸出每個階段的 Log")]
    public bool actorLogPhases = true;

    // ── 設定版本（舊場景的欄位值會自動升級，不必手動改 Inspector）──
    private const int CurrentSettingsVersion = 4;
    [SerializeField, HideInInspector] private int settingsVersion = 0;

    private void OnValidate()
    {
#if UNITY_EDITOR
        // 🤖 模型引用斷掉就自動找回。實測：把新模型拖進 Unity 變成「GhostCutIn 1.onnx」、刪掉舊檔後，
        //    觸發器引用的是已刪除的舊檔 → 執行時 ghostCutInModel 是 null，鬼切車全部默默改用規則式
        if (ghostCutInModel == null && autoFindGhostModel)
            UnityEditor.EditorApplication.delayCall += AutoFindGhostModel;
#endif
        if (settingsVersion >= CurrentSettingsVersion) return;
        // v2：依 cc.unity 路網實測調整（只改「還是舊預設值」的欄位，你手動改過的不動）
        if (Mathf.Approximately(laneSnapMax, 2.2f)) laneSnapMax = 3f;
        if (Mathf.Approximately(adjacentMin, 2.6f)) adjacentMin = 3f;
        if (Mathf.Approximately(adjacentMax, 4.8f)) adjacentMax = 6f;
        // v3：玩家偏離多遠才算改道 2.5 → 3.5m（車道約 5m 寬，靠邊開不該被判改道）
        if (settings != null && Mathf.Approximately(settings.playerLeftPathLateral, 2.5f)) settings.playerLeftPathLateral = 3.5f;
        // v4：強化學習 v2 模型在更大的範圍訓練過（玩家 20～85 km/h、很慢的前車、玩家閃避換道），改接規則式的門檻放寬
        if (settings != null)
        {
            if (settings.rlPlayerSpeedRangeKmH == new Vector2(25f, 65f)) settings.rlPlayerSpeedRangeKmH = new Vector2(18f, 90f);
            if (Mathf.Approximately(settings.rlLeadSlowerLimitKmH, 40f)) settings.rlLeadSlowerLimitKmH = 100f;
            if (Mathf.Approximately(settings.rlPlayerLaneDriftRatio, 0.5f)) settings.rlPlayerLaneDriftRatio = 1f;
        }
        settingsVersion = CurrentSettingsVersion;
        Debug.Log($"<color=cyan>🔧 [危險駕駛AI] 觸發器設定已升級到 v{CurrentSettingsVersion}（依路網與實測調整），請存檔場景</color>", this);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

#if UNITY_EDITOR
    private void AutoFindGhostModel()
    {
        if (this == null || ghostCutInModel != null) return;
        Unity.Barracuda.NNModel best = null;
        System.DateTime newest = System.DateTime.MinValue;
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:NNModel"))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            if (!System.IO.Path.GetFileName(path).StartsWith("GhostCutIn")) continue;
            System.DateTime time = System.IO.File.GetLastWriteTime(path);
            if (time <= newest) continue;
            var m = UnityEditor.AssetDatabase.LoadAssetAtPath<Unity.Barracuda.NNModel>(path);
            if (m != null) { best = m; newest = time; }
        }
        if (best == null)
        {
            Debug.LogWarning("⚠ [危險駕駛AI] Ghost Cut In Model 是空的，也找不到 GhostCutIn 模型 → 鬼切車只會用規則式", this);
            return;
        }
        ghostCutInModel = best;
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"<color=cyan>🤖 [危險駕駛AI] Ghost Cut In Model 引用斷掉，已自動填入「{UnityEditor.AssetDatabase.GetAssetPath(best)}」，請存檔場景</color>", this);
    }
#endif

    // ── 執行狀態 ──
    private AggressiveDriverActor activeActor;
    private string lockedEventName;
    private float nextScanTime;
    private string lastRejectMsg = "";
    private float lastRejectTime = -99f;

    public bool IsEventRunning { get { return activeActor != null; } }

    private const string GhostName = "鬼切";
    private const string HogName = "路霸";

    private void Reset()
    {
        settingsVersion = CurrentSettingsVersion;
        vehicleMask = LayerMask.GetMask("TrafficLight", "AI_Car", "Car");
        groundMask = LayerMask.GetMask("Road", "Default");
    }

    private void Start()
    {
        if (enableGhostCutIn && rlModeChance > 0f)
        {
            if (ghostCutInModel == null)
                Debug.LogWarning("⚠ [危險駕駛AI] 觸發器沒有 Ghost Cut In Model（引用可能斷了）→ 這次鬼切車全部用規則式。請在 Inspector 重新指定 GhostCutIn.onnx", this);
            else if (logChecks)
                Debug.Log($"<color=cyan>🤖 [危險駕駛AI] 鬼切強化學習模型：{ghostCutInModel.name}（使用機率 {rlModeChance:P0}）</color>", this);
        }
        if (vehicleMask.value == 0) vehicleMask = LayerMask.GetMask("TrafficLight", "AI_Car", "Car");
        if (groundMask.value == 0) groundMask = LayerMask.GetMask("Road", "Default");

        if (DangerEventDirector.Instance != null)
        {
            if (enableGhostCutIn) DangerEventDirector.Instance.RegisterFactor(GhostName);
            if (enableRoadHog) DangerEventDirector.Instance.RegisterFactor(HogName);
        }
        else if (useDirector)
            Debug.LogWarning("[危險駕駛AI] 場景中找不到 DangerEventDirector，將不受全域節奏控制", this);

        if (actorPrefabs == null || actorPrefabs.Length == 0)
            Debug.LogWarning("[危險駕駛AI] 還沒指定演員車 Prefab（Actor Prefabs），無法觸發", this);

        LaneNetwork.Get(grayNarrowMaskRadius, logChecks);   // 預先建好路網快取，避免第一次觸發時卡頓
    }

    private void Update()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (enableHotkeys && Input.GetKeyDown(teleportTestKey)) TeleportPlayerToTestSpot();
        if (enableHotkeys && activeActor == null)
        {
            if (Input.GetKeyDown(ghostTestKey)) TryStart(DangerDriverScenario.鬼切, true);
            else if (Input.GetKeyDown(hogTestKey)) TryStart(DangerDriverScenario.路霸, true);
        }
#endif
        if (!autoTrigger || activeActor != null) return;
        if (Time.time < nextScanTime) return;
        nextScanTime = Time.time + scanInterval;
        if (Time.timeSinceLevelLoad < startDelaySeconds) return;

        DangerDriverScenario sc;
        if (!PickScenario(out sc)) return;
        TryStart(sc, false);
    }

    private bool PickScenario(out DangerDriverScenario sc)
    {
        float wg = enableGhostCutIn ? ghostWeight : 0f;
        float wh = enableRoadHog ? hogWeight : 0f;
        sc = DangerDriverScenario.鬼切;
        if (wg + wh <= 0f) return false;
        sc = Random.Range(0f, wg + wh) < wg ? DangerDriverScenario.鬼切 : DangerDriverScenario.路霸;
        return true;
    }

    [ContextMenu("🧪 立即觸發：鬼切")]
    private void TestGhost() { if (Application.isPlaying) TryStart(DangerDriverScenario.鬼切, true); }

    [ContextMenu("🧪 立即觸發：路霸")]
    private void TestHog() { if (Application.isPlaying) TryStart(DangerDriverScenario.路霸, true); }

    [ContextMenu("🧪 傳送玩家到測試路段")]
    private void TeleportPlayerToTestSpot()
    {
        if (!Application.isPlaying) return;
        ResolvePlayer();
        if (player == null) { Debug.LogWarning("[危險駕駛AI] 找不到玩家，無法傳送", this); return; }

        Vector3 pos = new Vector3(testSpotXZYaw.x, player.position.y, testSpotXZYaw.y);
        RaycastHit hit;
        if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out hit, 200f, groundMask, QueryTriggerInteraction.Ignore))
            pos.y = hit.point.y + 0.5f;
        Quaternion rot = Quaternion.Euler(0f, testSpotXZYaw.z, 0f);

        // 移動整台車（有剛體就移動剛體所在的物件），並把速度歸零
        Transform root = playerRigidbody != null ? playerRigidbody.transform : player;
        if (playerRigidbody != null)
        {
            playerRigidbody.velocity = Vector3.zero;
            playerRigidbody.angularVelocity = Vector3.zero;
            playerRigidbody.position = pos;
            playerRigidbody.rotation = rot;
        }
        root.SetPositionAndRotation(pos, rot);
        Debug.Log($"<color=cyan>🧪 [危險駕駛AI] 已把玩家傳送到測試路段 ({pos.x:F0}, {pos.y:F1}, {pos.z:F0})，車頭 {testSpotXZYaw.z:F0}°。" +
                  $"往前開到時速 30 以上再按 {ghostTestKey}／{hogTestKey}</color>", this);
    }

    [ContextMenu("🧪 強制結束目前演出")]
    private void ForceEnd() { if (activeActor != null) Destroy(activeActor.gameObject); }

    // ====================================================================
    //  觸發流程
    // ====================================================================

    /// <summary>嘗試開始一場演出。forced = 測試鍵觸發（略過速度門檻，視設定略過導演）</summary>
    public bool TryStart(DangerDriverScenario sc, bool forced)
    {
        if (activeActor != null) return Reject(sc, "已有演出進行中", forced);
        if (actorPrefabs == null || actorPrefabs.Length == 0) return Reject(sc, "沒有指定演員車 Prefab", forced);

        ResolvePlayer();
        if (player == null) return Reject(sc, "找不到玩家（Player 標籤）", forced);

        LaneNetwork net = LaneNetwork.Get(grayNarrowMaskRadius, logChecks);
        if (net.segCount == 0) return Reject(sc, "場景中沒有任何路網線段", forced);

        // 1) 玩家速度與方向
        Vector3 fwd = player.forward;
        float speed = 0f;
        if (playerRigidbody != null)
        {
            Vector3 vel = playerRigidbody.velocity; vel.y = 0f;
            speed = Vector3.Dot(vel, fwd);
            if (vel.magnitude > 1f) fwd = vel.normalized;
        }
        if (!forced && speed * 3.6f < minPlayerSpeedKmH)
            return Reject(sc, $"玩家時速 {speed * 3.6f:F0} < {minPlayerSpeedKmH:F0}", forced);

        // 2) 玩家所在車道
        int seg; float lat;
        if (!net.FindLaneAt(player.position, fwd, laneSnapMax, laneMinDot, excludeNarrowRoads, out seg, out lat))
            return Reject(sc, $"玩家不在任何可用車道上（{laneSnapMax}m 內找不到同向線段）→ " +
                              net.DescribeNearest(player.position, fwd, laneMinDot, excludeNarrowRoads) +
                              $"｜玩家位置 ({player.position.x:F0}, {player.position.z:F0})、時速 {speed * 3.6f:F0}" +
                              (speed * 3.6f < 3f ? "（停著時用車頭方向判斷，建議開動後再按）" : ""), forced);

        float back = Mathf.Max(ghostSpawnBehind, hogSpawnBehind) + 25f;
        LanePath path = LanePath.Build(net, seg, back, requiredStraightAhead + 80f, 35f);
        float dp;
        float sp = path.Project(player.position, out dp);

        // 3) 前方夠直、沒有路口
        float heading = path.HeadingChange(sp, sp + requiredStraightAhead);
        if (heading > maxHeadingChange)
            return Reject(sc, $"前方 {requiredStraightAhead:F0}m 轉角 {heading:F0}° > {maxHeadingChange:F0}°", forced);
        if (path.Length - sp < requiredStraightAhead * 0.8f)
            return Reject(sc, $"前方路網只剩 {path.Length - sp:F0}m（接不到下一段，可能是死路或路口轉彎）", forced);
        if (avoidIntersections && path.HasIntersectionBetween(sp - 10f, sp + requiredStraightAhead))
            return Reject(sc, "前方直路範圍內有路口節點", forced);

        // 4) 隔壁同向車道
        float leftOff, rightOff;
        FindAdjacentOffsets(net, path, sp, out leftOff, out rightOff);

        bool allowL = sc == DangerDriverScenario.鬼切 ? ghostFromLeft : hogOvertakeLeft;
        bool allowR = sc == DangerDriverScenario.鬼切 ? ghostFromRight : hogOvertakeRight;
        float spawnBehind = sc == DangerDriverScenario.鬼切 ? ghostSpawnBehind : hogSpawnBehind;
        float checkFrom = Mathf.Max(0f, sp - spawnBehind);
        float checkTo = checkFrom + adjacentCheckLength;

        var options = new List<float>();
        string adjReport = "";
        float failS;
        if (allowL && !float.IsNaN(leftOff))
        {
            if (path.AdjacentLaneContinuous(checkFrom, checkTo, leftOff, 10f, 1.3f, laneMinDot, excludeNarrowRoads, out failS)) options.Add(leftOff);
            else adjReport += $"左側車道在 {failS - sp:+0;-0}m 處中斷；";
        }
        if (allowR && !float.IsNaN(rightOff))
        {
            if (path.AdjacentLaneContinuous(checkFrom, checkTo, rightOff, 10f, 1.3f, laneMinDot, excludeNarrowRoads, out failS)) options.Add(rightOff);
            else adjReport += $"右側車道在 {failS - sp:+0;-0}m 處中斷；";
        }
        if (options.Count == 0)
        {
            string found = $"左={(float.IsNaN(leftOff) ? "無" : leftOff.ToString("F1") + "m")}、右={(float.IsNaN(rightOff) ? "無" : rightOff.ToString("F1") + "m")}";
            return Reject(sc, $"沒有可用的隔壁同向車道（偵測到 {found}；允許 左={allowL} 右={allowR}；{adjReport}" +
                              $"判定範圍 {adjacentMin}~{adjacentMax}m）", forced);
        }
        float adjOffset = options[Random.Range(0, options.Count)];

        // 5) 生成位置
        float sSpawn = sp - spawnBehind;
        if (sSpawn < 0f)
        {
            if (sp < 8f) return Reject(sc, $"玩家後方路網只有 {sp:F0}m，不夠生成", forced);
            sSpawn = 0f;
        }
        float dSpawn = sc == DangerDriverScenario.鬼切 ? adjOffset : 0f;

        GameObject prefab = actorPrefabs[Random.Range(0, actorPrefabs.Length)];
        if (prefab == null) return Reject(sc, "演員車 Prefab 欄位有空值", forced);

        float playerHalf = Mathf.Clamp(AggressiveDriverActor.MeasureHalfLength(player.gameObject, 2.3f), 1.5f, 3.5f);
        float actorHalfGuess = 2.4f;

        Vector3 spawnPos, spawnTan;
        path.Sample(sSpawn, out spawnPos, out spawnTan);
        Vector3 right = Vector3.Cross(Vector3.up, spawnTan);
        spawnPos += right * dSpawn;

        string blocker;
        if (!IsBoxClear(spawnPos, spawnTan, actorHalfGuess + 2f, out blocker))
            return Reject(sc, $"生成點被佔用（{blocker}）", forced);

        if (sc == DangerDriverScenario.路霸)
        {
            // 路霸和玩家之間不能夾著別台車，否則它逼的是那台車
            float from = sSpawn + actorHalfGuess;
            float to = sp - playerHalf - 2f;
            if (to - from > 2f)
            {
                Vector3 mid, midTan;
                path.Sample((from + to) * 0.5f, out mid, out midTan);
                if (!IsBoxClear(mid, midTan, (to - from) * 0.5f, out blocker))
                    return Reject(sc, $"玩家後方同車道有其他車（{blocker}）", forced);
            }
        }

        // 6) 問導演
        string eventName = sc == DangerDriverScenario.鬼切 ? GhostName : HogName;
        bool askDirector = useDirector && DangerEventDirector.Instance != null && !(forced && hotkeyBypassDirector);
        if (askDirector && !DangerEventDirector.Instance.RequestEvent(eventName))
            return Reject(sc, "導演否決（原因見上一行 🎬 事件否決）", forced);

        // 7) 自適應難度：挑這次的難度等級（回傳獨立的參數複本）
        if (adaptiveDifficulty == null) adaptiveDifficulty = AdaptiveDifficultyBandit.Instance;
        int level = -1;
        AggressiveDriverSettings runSettings = adaptiveDifficulty != null
            ? adaptiveDifficulty.PrepareSettings(sc, settings, out level)
            : settings;

        // 8) 生成
        float initialSpeed = Mathf.Max(speed, 3f) + (sc == DangerDriverScenario.鬼切
            ? runSettings.ghostSpeedAdvantageKmH : runSettings.hogApproachAdvantageKmH) / 3.6f;

        bool useRL = sc == DangerDriverScenario.鬼切 && ghostCutInModel != null && Random.value < rlModeChance;
        AggressiveDriverActor actor = SpawnActor(prefab, spawnPos, Quaternion.LookRotation(spawnTan),
            sc == DangerDriverScenario.鬼切 ? (useRL ? "鬼切車（強化學習）" : "鬼切車（危險駕駛AI）") : "路霸車（危險駕駛AI）", useRL);

        if (askDirector && lockDirectorDuringEvent)
        {
            DangerEventDirector.Instance.BeginLongEvent(eventName);
            lockedEventName = eventName;
        }

        activeActor = actor;
        if (adaptiveDifficulty != null)
        {
            AdaptiveDifficultyBandit bandit = adaptiveDifficulty;
            int chosen = level;
            actor.onOutcome = (a, o) => bandit.ReportOutcome(sc, chosen, o);
        }
        actor.Begin(sc, runSettings, net, path, adjOffset, sSpawn, dSpawn, initialSpeed,
                    player, playerRigidbody, playerHalf, vehicleMask, OnActorFinished);
        actor.SetAdjacentLaneEnd(ComputeAdjacentLaneEnd(path, checkFrom, adjOffset));   // 強化學習的觀測值之一

        if (drawPaths) path.DrawDebug(new Color(0.8f, 0.2f, 1f), adjOffset, 20f);
        if (logChecks)
            Debug.Log($"<color=#ff7f50>🎬 [危險駕駛AI] 開始「{eventName}」{(forced ? "（測試鍵）" : "")}：" +
                      $"玩家 {speed * 3.6f:F0} km/h、隔壁車道在{(adjOffset > 0 ? "右" : "左")}側 {Mathf.Abs(adjOffset):F1}m、" +
                      $"生成於後方 {sp - sSpawn:F0}m、前方直路 {requiredStraightAhead:F0}m 內轉角 {heading:F0}°</color>", this);
        return true;
    }

    /// <summary>隔壁車道（偏移 offset）從 from 往前，在哪個 s 結束。一路接到路徑盡頭都沒斷 → 回傳 10000</summary>
    public float ComputeAdjacentLaneEnd(LanePath path, float from, float offset)
    {
        float to = path.Length - 2f;
        if (to <= from) return 1e4f;
        float failS;
        if (path.AdjacentLaneContinuous(from, to, offset, 10f, 1.3f, laneMinDot, excludeNarrowRoads, out failS)) return 1e4f;
        return failS;
    }

    /// <summary>
    /// 🗺 匯出訓練樣本用：在指定位置與行進方向，用「跟 TryStart 完全相同的道路條件」檢查能不能觸發鬼切
    /// （不檢查玩家車速、生成點有沒有車、導演——那些跟道路無關）。
    /// 可以的話回傳每個可用的隔壁車道偏移，以及它在玩家前方幾公尺處結束。
    /// </summary>
    public bool EvaluateGhostRoadSpot(LaneNetwork net, Vector3 pos, Vector3 fwd,
                                      List<float> offsets, List<float> endsFromPlayer, out float heading, out string reason)
    {
        offsets.Clear(); endsFromPlayer.Clear();
        heading = 0f; reason = "";
        fwd.y = 0f; fwd.Normalize();

        int seg; float lat;
        if (!net.FindLaneAt(pos, fwd, laneSnapMax, laneMinDot, excludeNarrowRoads, out seg, out lat)) { reason = "不在可用車道上"; return false; }

        float back = Mathf.Max(ghostSpawnBehind, hogSpawnBehind) + 25f;
        LanePath path = LanePath.Build(net, seg, back, requiredStraightAhead + 80f, 35f);
        float dp;
        float sp = path.Project(pos, out dp);

        heading = path.HeadingChange(sp, sp + requiredStraightAhead);
        if (heading > maxHeadingChange) { reason = "前方轉角太大"; return false; }
        if (path.Length - sp < requiredStraightAhead * 0.8f) { reason = "前方路網太短"; return false; }
        if (avoidIntersections && path.HasIntersectionBetween(sp - 10f, sp + requiredStraightAhead)) { reason = "前方有路口"; return false; }

        float leftOff, rightOff;
        FindAdjacentOffsets(net, path, sp, out leftOff, out rightOff);
        float checkFrom = Mathf.Max(0f, sp - ghostSpawnBehind);
        float checkTo = checkFrom + adjacentCheckLength;
        float failS;
        if (ghostFromLeft && !float.IsNaN(leftOff) &&
            path.AdjacentLaneContinuous(checkFrom, checkTo, leftOff, 10f, 1.3f, laneMinDot, excludeNarrowRoads, out failS))
        { offsets.Add(leftOff); endsFromPlayer.Add(ComputeAdjacentLaneEnd(path, checkFrom, leftOff) - sp); }
        if (ghostFromRight && !float.IsNaN(rightOff) &&
            path.AdjacentLaneContinuous(checkFrom, checkTo, rightOff, 10f, 1.3f, laneMinDot, excludeNarrowRoads, out failS))
        { offsets.Add(rightOff); endsFromPlayer.Add(ComputeAdjacentLaneEnd(path, checkFrom, rightOff) - sp); }

        if (offsets.Count == 0) { reason = float.IsNaN(leftOff) && float.IsNaN(rightOff) ? "沒有隔壁同向車道" : "隔壁車道中斷"; return false; }
        if (sp - ghostSpawnBehind < 0f && sp < 8f) { reason = "後方路網太短"; return false; }
        return true;
    }

    /// <summary>找出玩家車道左右兩側最近的同向車道偏移量（找不到回傳 NaN）。負 = 左、正 = 右</summary>
    private void FindAdjacentOffsets(LaneNetwork net, LanePath path, float sp, out float leftOff, out float rightOff)
    {
        leftOff = float.NaN; rightOff = float.NaN;
        Vector3 p, tan;
        path.Sample(sp, out p, out tan);
        float bestL = float.MaxValue, bestR = float.MaxValue;

        for (int i = 0; i < net.segCount; i++)
        {
            if (Vector3.Dot(net.segDir[i], tan) < laneMinDot) continue;
            int a = net.segA[i], b = net.segB[i];
            if (net.isScooter[a] || net.isScooter[b]) continue;
            if (excludeNarrowRoads && (net.isNarrow[a] || net.isNarrow[b])) continue;

            float t, latOfP, perp;
            net.ProjectOnSegment(i, p, out t, out latOfP, out perp);
            if (t < -0.1f || t > 1.1f) continue;

            // p 在那條線段的右邊 latOfP → 那條線段在 p 的 −latOfP 側
            float offset = -latOfP;
            float abs = Mathf.Abs(offset);
            if (abs < adjacentMin || abs > adjacentMax) continue;

            if (offset < 0f && abs < bestL) { bestL = abs; leftOff = offset; }
            if (offset > 0f && abs < bestR) { bestR = abs; rightOff = offset; }
        }
    }

    private bool IsBoxClear(Vector3 center, Vector3 tan, float halfLength, out string blocker)
    {
        Collider[] hits = Physics.OverlapBox(center + Vector3.up * 1f, new Vector3(1.2f, 1f, halfLength),
                                             Quaternion.LookRotation(tan), vehicleMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (player != null && (h.transform == player || h.transform.IsChildOf(player))) continue;
            blocker = h.transform.root.name;
            return false;
        }
        blocker = "";
        return true;
    }

    private AggressiveDriverActor SpawnActor(GameObject prefab, Vector3 pos, Quaternion rot, string displayName, bool useRL)
    {
        // 先生成在「未啟用」的暫存物件底下：原本的 AI 腳本 Awake/Start 都還沒跑，就先把它們拿掉
        var holder = new GameObject("危險駕駛AI_生成暫存");
        holder.SetActive(false);
        GameObject go = Instantiate(prefab, pos, rot, holder.transform);
        go.name = displayName;

        AggressiveDriverActor actor = go.GetComponent<AggressiveDriverActor>();
        if (actor == null) actor = go.AddComponent<AggressiveDriverActor>();

        var strip = new HashSet<string>(stripScriptTypes ?? new string[0]);
        var removed = new List<string>();
        // 子類別（AmbulanceAI）要先刪，父類別的 RequireComponent 才不會擋
        var behaviours = go.GetComponentsInChildren<MonoBehaviour>(true);

        // 🔧 燈號綁定：直接從同一份元件清單找（實測 GetComponentInChildren 在未啟用的暫存物件底下會拿不到）
        ReverseThreatAICar white = null;
        foreach (var mb in behaviours)
        {
            var w = mb as ReverseThreatAICar;
            if (w != null) { white = w; break; }
        }
        // ⚠ 下面會把 white 刪掉，刪掉後 Unity 的 white != null 會變 false，所以先記下結果給 Log 用
        bool copiedBindings = white != null;
        if (copiedBindings) actor.CopyBindingsFrom(white);
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var mb in behaviours)
            {
                if (mb == null || mb == actor) continue;
                string tn = mb.GetType().Name;
                if (!strip.Contains(tn)) continue;
                DestroyImmediate(mb);
                removed.Add(tn);
            }
        }
        foreach (var agent in go.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
        {
            DestroyImmediate(agent);
            removed.Add("NavMeshAgent");
        }

        actor.hornClip = hornClip != null ? hornClip : actor.hornClip;
        actor.screechClip = screechClip != null ? screechClip : actor.screechClip;
        actor.volume = actorVolume;
        actor.groundMask = groundMask;
        actor.groundOffset = groundOffset;
        actor.modelYawOffset = modelYawOffset;
        actor.logPhases = actorLogPhases;
        actor.excludeNarrowRoads = excludeNarrowRoads;
        actor.drawPath = false;

        // 🤖 強化學習：掛上推論元件（直接用 Barracuda 跑 ONNX，不經過 ML-Agents 的 Agent／Academy）
        if (useRL)
        {
            var policy = go.AddComponent<GhostCutInCityPolicy>();
            policy.model = ghostCutInModel;
            policy.actor = actor;
            actor.AttachRLPolicy(policy);
        }

        go.transform.SetParent(null, true);
        Destroy(holder);

        if (logChecks)
            Debug.Log($"<color=grey>🧹 [危險駕駛AI] 生成「{displayName}」（{prefab.name}），" +
                      $"移除原本腳本：{(removed.Count > 0 ? string.Join("、", removed.ToArray()) : "無")}；" +
                      $"燈號綁定：{(copiedBindings ? "已從 ReverseThreatAICar 複製" : "沒有來源，請在演員身上手動指定")}</color>", go);
        return actor;
    }

    private void OnActorFinished(AggressiveDriverActor actor)
    {
        if (actor != activeActor) return;
        activeActor = null;
        ReleaseDirector();
    }

    private void ReleaseDirector()
    {
        if (string.IsNullOrEmpty(lockedEventName)) return;
        if (DangerEventDirector.Instance != null) DangerEventDirector.Instance.EndLongEvent();
        lockedEventName = null;
    }

    private void OnDisable()
    {
        // 觸發器被關掉時一定要解鎖導演，否則其他危險因子會永久停擺
        if (activeActor != null) Destroy(activeActor.gameObject);
        ReleaseDirector();
    }

    private void ResolvePlayer()
    {
        if (player == null)
        {
            GameObject p = GameObject.FindWithTag("Player");
            if (p != null) player = p.transform;
        }
        if (player != null && playerRigidbody == null)
        {
            playerRigidbody = player.GetComponent<Rigidbody>();
            if (playerRigidbody == null) playerRigidbody = player.GetComponentInParent<Rigidbody>();
            if (playerRigidbody == null) playerRigidbody = player.GetComponentInChildren<Rigidbody>();
        }
    }

    private bool Reject(DangerDriverScenario sc, string reason, bool forced)
    {
        if (!logChecks && !forced) return false;
        string msg = sc + "|" + reason;
        // 測試鍵一定印；自動檢查同樣理由 5 秒內只印一次
        if (!forced && msg == lastRejectMsg && Time.time - lastRejectTime < 5f) return false;
        lastRejectMsg = msg;
        lastRejectTime = Time.time;
        string where = player != null ? $"｜玩家位置 ({player.position.x:F0}, {player.position.z:F0})" : "";
        Debug.Log($"<color={(forced ? "orange" : "grey")}>🔍 [危險駕駛AI] {(forced ? "測試觸發" : "自動檢查")}「{sc}」未通過：{reason}{where}</color>", this);
        return false;
    }
}
