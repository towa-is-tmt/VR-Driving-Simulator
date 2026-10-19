using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class TrafficSpawner : MonoBehaviour
{
    public static TrafficSpawner Instance;

    [Header("🚗 總車輛數控管")]
    [Tooltip("期望場上隨時保持的 AI 車輛總數")]
    public int targetCarCount = 31;

    [Header("🔇 Console 診斷開關（排查完就關掉,不影響任何行為）")]
    [Tooltip("印出每次補車/清退的數量統計")]
    public bool logPopulation = false;
    [Tooltip("印出每一台被栓繩回收（漂太遠）的車")]
    public bool logTether = false;
    [Tooltip("印出每一台新生成的車與它的節點")]
    public bool logSpawn = false;

    [Header("📦 產生載具設定")]
    public GameObject aiCarPrefab;
    /*
    [Header("📍 隨機生成起點名單")]
    public List<Transform> spawnPoints = new List<Transform>();

    [Header("🛣️ 路徑導航指派")]
    public List<TrafficPathNode> initialWaypoints = new List<TrafficPathNode>();
    */
    [Header("⏳ 監控頻率")]
    public float checkInterval = 1f;

    [Header("🛡️ 安全防重疊設定")]
    public float safeCheckRadius = 10f;
    public LayerMask aiCarLayer;
    [Header("🚑 救護車來時減車流")]
    [Tooltip("救護車出現時,目標車數乘這個(0.5=砍半);離場自動恢復")]
    public float ambulanceThinFactor = 0.5f;

    [Header("🛡️ 多退機制保護半徑")]
    [Tooltip("清退多餘車輛時,玩家這距離內的車絕不刪(避免眼前/後照鏡憑空消失)。這是球形,對後方與側面用")]
    public float trimSafeRadius = 60f;
    // 球形半徑對「正前方」遠遠不夠 —— 直路上看得到 150m 以上,60m 的球只擋得住近處。
    // 所以正前方另外拉一條更長的保護,後方側面維持原本的球形就好
    // (保護範圍無謂放大的話會湊不到要刪的數量,車流會一直超標)。
    [Tooltip("玩家正前方這個距離內的車不刪。直路看得比 trimSafeRadius 遠得多")]
    public float forwardSafeDistance = 150f;
    [Range(0f, 1f)]
    [Tooltip("多少角度內算「正前方」。0.5≈左右各 60 度")]
    public float forwardSafeDot = 0.5f;
    // 玩家的視線幾乎都黏在救護車上,它周圍是最容易穿幫的地方,
    // 但原本只保護救護車「本身」,周圍的車照刪。
    [Tooltip("救護車這個距離內的車不刪。0 = 不做這個保護")]
    public float ambulanceSafeRadius = 80f;

    [Header("🫧 玩家周圍生成泡泡")]
    [Tooltip("生成點離玩家的最近距離（太近會被看到憑空出現）")]
    public float minSpawnDistance = 60f;
    [Tooltip("生成點離玩家的最遠距離（太遠等於沒生）")]
    public float maxSpawnDistance = 150f;
    [Header("🧹 距離栓繩")]
    [Tooltip("離玩家超過這個距離的車直接回收(要大於玩家視距,避免看到消失)")]
    public float maxKeepDistance = 200f;
    private TrafficPathNode[] allNodes;   // 全地圖節點快取
    private Transform playerTransform;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        // 抓取玩家的 Transform，用來做「太遠刪除」的依據
        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) playerTransform = playerObj.transform;
        allNodes = FindObjectsOfType<TrafficPathNode>();   // 👈 新增這行

        StartCoroutine(MonitorTrafficRoutine());
    }

    IEnumerator MonitorTrafficRoutine()
    {
        while (true)
        {
            // 1. 數場上現在有幾台活著的 AI 車
            ReverseThreatAICar[] activeCars = FindObjectsOfType<ReverseThreatAICar>();
            // 🧹 距離栓繩:漂出玩家周圍的車直接回收,下一輪自動在玩家附近補回
            if (playerTransform != null)
            {
                foreach (var car in activeCars)
                {
                    if (car == null) continue;
                    if (car is AmbulanceAI) continue;      // 救護車不歸這裡管
                    if (car.isStaticParked) continue;      // 路邊演員車不回收
                    if (Vector3.Distance(playerTransform.position, car.transform.position) > maxKeepDistance)
                    {
                        if (logTether) UnityEngine.Debug.Log($"🧹 [栓繩回收] {car.gameObject.name} 漂離玩家太遠,回收重分配");
                        Destroy(car.gameObject);
                    }
                }
            }
            // 計數排除救護車(牠不是普通車流,不該佔名額也不該被清)
            int currentCount = 0;
            foreach (var car in activeCars)
                if (car != null && !(car is AmbulanceAI)) currentCount++;

            // 🚑 救護車出現時,目標車數打折,把路讓出來;離場自動恢復
            int effectiveTarget = AmbulanceAI.IsAmbulanceActive
                ? Mathf.RoundToInt(targetCarCount * ambulanceThinFactor)
                : targetCarCount;

            // 2. 判斷多退少補
            if (currentCount < effectiveTarget)
            {
                int carsToSpawn = effectiveTarget - currentCount;
                if (logPopulation) UnityEngine.Debug.Log($"📊 場上 AI 車: {currentCount},低於目標 {effectiveTarget},補 {carsToSpawn} 台");
                for (int i = 0; i < carsToSpawn; i++) TrySpawnVehicle();
            }
            else if (currentCount > effectiveTarget)
            {
                int carsToDestroy = currentCount - effectiveTarget;
                if (logPopulation) UnityEngine.Debug.Log($"🚨 場上 AI 車: {currentCount},高於目標 {effectiveTarget},清退 {carsToDestroy} 台");
                TrimExcessCars(activeCars, carsToDestroy);
            }

            yield return new WaitForSeconds(checkInterval);
        }
    }

    /// <summary>
    /// 核心優化：隨機且安全地刪除多出來的車
    /// </summary>
    void TrimExcessCars(ReverseThreatAICar[] allCars, int amountToRemove)
    {
        // 🚑 救護車位置只查一次。旗標是純 static bool,幾乎零成本;
        //    真的有救護車在場才做 FindObjectOfType,而這個函式最快也是每 checkInterval 才進來一次
        Transform ambulanceTf = null;
        if (ambulanceSafeRadius > 0f && AmbulanceAI.IsAmbulanceActive)
        {
            AmbulanceAI amb = FindObjectOfType<AmbulanceAI>();
            if (amb != null) ambulanceTf = amb.transform;
        }

        // 先篩出「可以安全刪」的候選:排除救護車、危險因子演出中、路邊演員、
        // 玩家保護半徑內、玩家正前方視野內、救護車周圍
        List<ReverseThreatAICar> candidates = new List<ReverseThreatAICar>();
        int protectedByView = 0, protectedByAmbulance = 0;

        foreach (var car in allCars)
        {
            if (car == null || car.gameObject == null) continue;
            if (car is AmbulanceAI) continue;           // 🚑 救護車絕不刪
            if (car.isStaticParked) continue;           // 路邊演員留著
            if (!car.CanTakeAction()) continue;         // 正在演危險因子(急煞/倒車/衝出/冷卻)→ 不刪,別破壞互動

            if (playerTransform != null)
            {
                Vector3 toCar = car.transform.position - playerTransform.position;
                float dist = toCar.magnitude;

                if (dist < trimSafeRadius) continue;    // 🛡️ 玩家保護半徑內絕不刪(球形,擋後方與側面)

                // 👁️ 玩家正前方視野內不刪 —— 直路上遠處的車照樣看得一清二楚
                if (forwardSafeDistance > 0f && dist < forwardSafeDistance && dist > 0.01f &&
                    Vector3.Dot(playerTransform.forward, toCar / dist) > forwardSafeDot)
                {
                    protectedByView++;
                    continue;
                }
            }

            // 🚑 救護車周圍不刪 —— 玩家的視線幾乎都跟著救護車走
            if (ambulanceTf != null &&
                Vector3.Distance(ambulanceTf.position, car.transform.position) < ambulanceSafeRadius)
            {
                protectedByAmbulance++;
                continue;
            }

            candidates.Add(car);
        }

        // 由遠到近排序,先刪最遠的
        if (playerTransform != null)
            candidates.Sort((a, b) =>
                Vector3.Distance(playerTransform.position, b.transform.position)
                .CompareTo(Vector3.Distance(playerTransform.position, a.transform.position)));

        int removedCount = 0;
        foreach (var aiCar in candidates)
        {
            if (removedCount >= amountToRemove) break;
            if (logPopulation) UnityEngine.Debug.Log($"🧹 [多退機制] 淡出清退遠處車輛: {aiCar.gameObject.name}");
            aiCar.ForceFadeOutClear(); // 淡出,不瞬間消失(對後照鏡友善)
            removedCount++;
        }

        // 保護規則可能讓候選不足,那就刪不到目標數量 —— 這是刻意的取捨:
        // 寧可場上多幾台車,也不要玩家看到車在眼前消失。下一輪還會再試。
        if (removedCount < amountToRemove)
            if (logPopulation) UnityEngine.Debug.Log($"🛡️ [多退機制] 只清退 {removedCount}/{amountToRemove} 台" +
                                  $"（視野內保護 {protectedByView} 台、救護車周圍保護 {protectedByAmbulance} 台）");
    }

    void TrySpawnVehicle()
    {
        if (aiCarPrefab == null || allNodes == null || playerTransform == null) return;

        // 1. 篩選候選節點 —— 所有「這裡不能生」的條件都寫在這個迴圈裡
        List<TrafficPathNode> candidates = new List<TrafficPathNode>();
        foreach (var node in allNodes)
        {
            if (node == null || node.nextChoices == null || node.nextChoices.Count == 0) continue;

            //if (node.isNarrowRoad) continue;                                              // 不在窄路生
            if (node.intersectionType != TrafficPathNode.IntersectionType.None) continue; // 👈 不在路口生
            if (node.isInsideYellowGrid || node.isTrafficLightStopZone) continue;         // 👈 不在黃網格/停止線生

            // 🚫 事故現場周圍一律不生車。
            //    封鎖清單只涵蓋「對向入口」與 Inspector 手動指定的節點,
            //    但「觸發盒 → 故障車」之間那段路的節點不在清單裡,
            //    生成器照樣會在那裡補車 —— 玩家一觸發就看到新車從事故區冒出來。
            if (AccidentDirector.IsAccidentActive)
            {
                // 指定路線上的節點(頭尾之間,沿路網走訪出來的)
                if (AccidentDirector.NoSpawnNodesWhite.Contains(node)) continue;
                // 球形半徑(沒指定路線時的作法,或路線之外的近距離保護)
                if (AccidentDirector.ActiveNoSpawnRadius > 0f
                    && Vector3.Distance(node.transform.position, AccidentDirector.ActiveAccidentPos)
                       < AccidentDirector.ActiveNoSpawnRadius) continue;
            }

            // 🔧 事故封鎖中的節點不生車。
            //    封鎖清單原本只讓「已經在路上的車」改道,生成器完全沒檢查,
            //    結果會直接在封鎖區裡憑空生出新車 —— 等於封鎖白做。
            //    對向車流節奏也靠這個清單,不擋住的話「一批 2 台」永遠算不準。
            if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedPathNodes != null
                && AccidentDirector.BlockedPathNodes.Contains(node)) continue;
            // 下一個節點也全被封鎖 → 生出來馬上就要改道,不如不生
            if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedPathNodes != null)
            {
                bool allNextBlocked = true;
                foreach (var nx in node.nextChoices)
                    if (nx != null && !AccidentDirector.BlockedPathNodes.Contains(nx)) { allNextBlocked = false; break; }
                if (allNextBlocked) continue;
            }

            float dist = Vector3.Distance(playerTransform.position, node.transform.position);
            if (dist < minSpawnDistance || dist > maxSpawnDistance) continue;

            candidates.Add(node);
        }
        if (candidates.Count == 0) return;

        // 2. 隨機挑節點,防重疊檢查通過就生
        for (int attempt = 0; attempt < 10; attempt++)
        {
            TrafficPathNode node = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            TrafficPathNode nextNode = node.nextChoices[UnityEngine.Random.Range(0, node.nextChoices.Count)];
            if (nextNode == null) continue;

            if (Physics.CheckSphere(node.transform.position, safeCheckRadius, aiCarLayer)) continue;

            Quaternion rot = Quaternion.LookRotation(
                (nextNode.transform.position - node.transform.position).normalized, Vector3.up);

            // 直接生成在節點高度，實際落地交給 SpawnGroundSnap。
            // 原本這裡有 +0.5「讓車輕輕落地」，但車是 Kinematic 不受重力、永遠不會落下，
            // 那 0.5m 只會變成永久的浮空，所以移除。
            Vector3 spawnPos = node.transform.position;

            GameObject newCarGo = Instantiate(aiCarPrefab, spawnPos, rot);
            SpawnGroundSnap.Apply(newCarGo);   // 射線打空時就維持節點高度，不再額外調整
            newCarGo.name = "Dynamic_Replenish_AI_" + Time.time;
            if (logSpawn) UnityEngine.Debug.Log($"<color=lime>🐣 [新車出生] 節點:{node.name} | 座標:{node.transform.position} | 離玩家 {Vector3.Distance(playerTransform.position, node.transform.position):F0}m</color>", node);
            ReverseThreatAICar aiScript = newCarGo.GetComponentInParent<ReverseThreatAICar>();
            if (aiScript != null)
            {
                aiScript.isStaticParked = false;
                aiScript.currentWaypointNode = nextNode;
            }
            return;
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // 找玩家(編輯模式下也能畫)
        Transform p = playerTransform;
        if (p == null)
        {
            GameObject playerObj = GameObject.FindWithTag("Player");
            if (playerObj != null) p = playerObj.transform;
        }
        if (p == null) return;

        // ★ 只有在編輯器「選取玩家車（或它的父/子物件）」時才畫這些生成半徑圈，
        //    平常不點玩家車就完全隱藏，不擋視野。這個生成器元件其實掛在
        //    「監視車輛數量白車」上、不是玩家車，所以不能單純用 OnDrawGizmosSelected，
        //    改用 Selection 判斷「選到的是不是玩家車那一棵」。
        Transform sel = UnityEditor.Selection.activeTransform;
        if (sel == null) return;
        bool 選到玩家車 = sel == p || p.IsChildOf(sel) || sel.IsChildOf(p);
        if (!選到玩家車) return;

        // 🔴 紅圈 = 栓繩回收線(超過就回收)
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(p.position, maxKeepDistance);

        // 🟢 綠圈 = 生成泡泡外圈
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(p.position, maxSpawnDistance);

        // 🔵 青圈 = 生成泡泡內圈(比這近不生,防穿幫)
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(p.position, minSpawnDistance);
    }
#endif
}