
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 🚙 灰車數量監控:少了就在玩家周圍的 WaypointNode 路網補生(套路同白車 TrafficSpawner)
public class GrayTrafficSpawner : MonoBehaviour
{
    [Header("🚙 灰車數量控管")]
    public int targetCarCount = 15;
    public GameObject grayCarPrefab;
    public float checkInterval = 1f;
    public float ambulanceThinFactor = 0.5f;
    [Header("🏍 機車")]
    [Tooltip("機車 prefab，可以拖多個顏色進來，生成時隨機挑一個。留空 = 完全不生機車")]
    public GameObject[] scooterPrefabs;
    [Range(0f, 1f)]
    [Tooltip("每次補生時生機車的機率。0 = 全汽車，1 = 全機車。機車跟汽車共用上面的 Target Car Count 預算，總量不會爆")]
    public float scooterRatio = 0.3f;
    [Tooltip("救護車在場時暫停生成機車,改成只生汽車;救護車離場後自動恢復。" +
             "機車會鑽車、車身常是斜的,在救護車避讓與故障車走廊的方向篩選裡都最容易出狀況")]
    public bool suppressScootersDuringAmbulance = true;

    [Header("🛡️ 防重疊")]
    public float safeCheckRadius = 10f;
    public LayerMask carLayer; // 勾灰車+白車的 Layer

    [Header("🫧 玩家周圍生成泡泡")]
    public float minSpawnDistance = 60f;
    public float maxSpawnDistance = 150f;
    [Header("🧹 距離栓繩")]
    public float maxKeepDistance = 200f;

    [Header("🚑 救護車來時的清退（比照白車 TrafficSpawner 的多退機制）")]
    // 舊版只有「補」沒有「退」:救護車一出現,目標從 35 降到 17,但場上那 35 台
    // 一台都不會少 —— 只能等它們自己開遠被回收,所以看起來完全沒有讓路。
    // 白車那邊有 TrimExcessCars(),這裡補上對應的一套。
    [Tooltip("超過目標車數時主動清退。取消勾選 = 舊行為(只補不退)")]
    public bool trimExcessCars = true;
    // Target Car Count 是「場上灰車的總量上限」,不只是生成器自己的配額 ——
    // 計數用 FindObjectsOfType<CarNavigation>(),手動擺在場景裡的車也算進去。
    // 場景目前手擺了 54 台(全部沒勾 isStaticObstacle),所以開場會收斂到 35 台。
    // 這跟白車 TrafficSpawner 的行為一致(它一直維持在 40 台)。
    //
    // 若某些手擺的車是刻意當佈景的(停車格裡的車等),請在那些車上勾 isStaticObstacle,
    // 它們就不會被列入計數,也不會被任何清退機制動到。
    [Tooltip("只在救護車在場時才清退。勾選 = 平時不管總量,只有救護車來時才收斂到一半")]
    public bool trimOnlyDuringAmbulance = false;
    [Tooltip("玩家這個半徑內絕不清退(球形,擋後方與側面)")]
    public float trimSafeRadius = 45f;
    [Tooltip("玩家正前方這個距離內不清退 —— 直路上遠處的車照樣看得一清二楚")]
    public float forwardSafeDistance = 120f;
    [Range(0f, 1f)]
    [Tooltip("多接近正前方才算在視野裡。0.5 約等於左右各 60 度")]
    public float forwardSafeDot = 0.5f;
    [Tooltip("救護車這個半徑內不清退 —— 玩家的視線幾乎都跟著救護車走")]
    public float ambulanceSafeRadius = 80f;
    [Tooltip("印出每次清退了哪幾台、以及被保護擋下幾台")]
    public bool logTrim = false;
    [Tooltip("印出每一台新生成的汽車/機車與它的節點")]
    public bool logSpawn = false;
    private WaypointNode[] allNodes;
    private Transform playerTransform;

    private void Start()
    {
        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) playerTransform = playerObj.transform;
        allNodes = FindObjectsOfType<WaypointNode>();
        StartCoroutine(MonitorRoutine());
    }

    IEnumerator MonitorRoutine()
    {
        while (true)
        {
            int count = 0;
            foreach (var c in FindObjectsOfType<CarNavigation>())
            {
                if (c.isStaticObstacle) continue; // 靜態路障車不列入計數也不回收
                if (playerTransform != null &&
                    Vector3.Distance(playerTransform.position, c.transform.position) > maxKeepDistance)
                {
                    Destroy(c.gameObject); // 🧹 漂太遠,回收
                    continue;
                }
                count++;
            }

            int effectiveTarget = AmbulanceAI.IsAmbulanceActive
    ? Mathf.RoundToInt(targetCarCount * ambulanceThinFactor)
    : targetCarCount;
            if (count < effectiveTarget)
            {
                for (int i = count; i < effectiveTarget; i++) TrySpawn();
            }
            else if (trimExcessCars && count > effectiveTarget
                     && (!trimOnlyDuringAmbulance || AmbulanceAI.IsAmbulanceActive))
            {
                TrimExcess(count - effectiveTarget);
            }

            yield return new WaitForSeconds(checkInterval);
        }
    }

    /// <summary>
    /// 🧹 清退多出來的灰車。保護規則比照白車的 TrimExcessCars():
    /// 靜態停放車、正在演出/倒車的、正在避讓救護車的、玩家附近與視野內的、
    /// 救護車周圍的,一律不動。剩下的由遠到近淡出。
    ///
    /// 用 ForceFadeOutClear() 而不是 Destroy() —— 縮小淡出,對後照鏡友善,
    /// 不會有車在玩家眼前憑空消失。
    /// </summary>
    void TrimExcess(int amountToRemove)
    {
        Transform ambulanceTf = null;
        if (ambulanceSafeRadius > 0f && AmbulanceAI.IsAmbulanceActive)
        {
            AmbulanceAI amb = FindObjectOfType<AmbulanceAI>();
            if (amb != null) ambulanceTf = amb.transform;
        }

        List<CarNavigation> candidates = new List<CarNavigation>();
        int byView = 0, byAmbulance = 0, byState = 0;

        foreach (var car in FindObjectsOfType<CarNavigation>())
        {
            if (car == null || car.gameObject == null) continue;
            if (car.isStaticObstacle) continue;              // 路邊佈景車留著
            if (car.IsForcedReversing) { byState++; continue; }        // 倒車演出中,別打斷
            if (car.IsYieldingForAmbulanceNow) { byState++; continue; }// 正在讓道,清掉反而更亂

            if (playerTransform != null)
            {
                Vector3 toCar = car.transform.position - playerTransform.position;
                float dist = toCar.magnitude;
                if (dist < trimSafeRadius) continue;         // 🛡️ 玩家附近絕不清

                // 👁️ 玩家正前方視野內不清 —— 直路上遠處的車照樣看得一清二楚
                if (forwardSafeDistance > 0f && dist < forwardSafeDistance && dist > 0.01f &&
                    Vector3.Dot(playerTransform.forward, toCar / dist) > forwardSafeDot)
                { byView++; continue; }
            }

            if (ambulanceTf != null &&
                Vector3.Distance(ambulanceTf.position, car.transform.position) < ambulanceSafeRadius)
            { byAmbulance++; continue; }

            candidates.Add(car);
        }

        // 由遠到近排序,先清最遠的
        if (playerTransform != null)
            candidates.Sort((a, b) =>
                Vector3.Distance(playerTransform.position, b.transform.position)
                .CompareTo(Vector3.Distance(playerTransform.position, a.transform.position)));

        int removed = 0;
        foreach (var car in candidates)
        {
            if (removed >= amountToRemove) break;
            car.ForceFadeOutClear();   // 縮小淡出,不瞬間消失
            removed++;
        }

        // 保護規則可能讓候選不足 —— 這是刻意的取捨:寧可多幾台車,
        // 也不要玩家看到車在眼前消失。下一輪還會再試。
        if (logTrim && amountToRemove > 0)
            Debug.Log($"<color=grey>🧹 [灰車多退] 清退 {removed}/{amountToRemove} 台" +
                      $"（視野內保護 {byView}、救護車周圍保護 {byAmbulance}、演出中保護 {byState}）</color>", this);
    }

    void TrySpawn()
    {
        if (allNodes == null || playerTransform == null) return;

        // 🚑 救護車在場時不生機車。
        //    IsAmbulanceActive 是 static bool,救護車 Start 設 true、OnDestroy 設回 false,
        //    所以救護車一離場,機車下一次補生就自動恢復,不需要另外做還原。
        //    已經在路上的機車不會被清掉 —— 它們會隨著開遠被既有的回收機制收走,
        //    而且救護車期間 ambulanceThinFactor 本來就會把車流總量壓低。
        bool ambulanceBlocksScooters = suppressScootersDuringAmbulance && AmbulanceAI.IsAmbulanceActive;

        // 🏍 先決定這次生汽車還是機車 —— 候選節點的篩選條件不一樣（汽車不能生在機車專用道）
        bool spawnScooter = !ambulanceBlocksScooters
                            && scooterPrefabs != null && scooterPrefabs.Length > 0
                            && Random.value < scooterRatio;

        GameObject prefab = null;
        if (spawnScooter)
        {
            // 隨機挑一個顏色。陣列裡有沒拖東西的空格就多試幾次，全空就退回生汽車
            for (int i = 0; i < 4 && prefab == null; i++)
                prefab = scooterPrefabs[Random.Range(0, scooterPrefabs.Length)];
        }
        if (prefab == null) { prefab = grayCarPrefab; spawnScooter = false; }
        if (prefab == null) return;

        List<WaypointNode> candidates = new List<WaypointNode>();
        foreach (var node in allNodes)
        {
            if (node == null || node.nextChoices == null || node.nextChoices.Count == 0) continue;
            if (node.intersectionType != WaypointNode.IntersectionType.None) continue; // 不在路口生
            if (node.isInsideYellowGrid || node.isTrafficLightStopZone) continue;      // 不在黃網格/停止線生

            // 🏍 汽車不生在機車專用道上（機車兩種車道都能生）。
            //    生出來之後 PickRandomChoice 雖然會把汽車導離機車道，但起點錯了還是會看到
            //    一台汽車憑空出現在機車道上再切出來。
            if (!spawnScooter && node.isScooterLane) continue;

            // 🚫 事故現場周圍一律不生車。
            //    封鎖清單只涵蓋「對向入口」與 Inspector 手動指定的節點,
            //    但「觸發盒 → 故障車」之間那段路的節點不在清單裡,
            //    生成器照樣會在那裡補車 —— 玩家一觸發就看到新車從事故區冒出來。
            if (AccidentDirector.IsAccidentActive)
            {
                // 指定路線上的節點(頭尾之間,沿路網走訪出來的)
                if (AccidentDirector.NoSpawnNodesGray.Contains(node)) continue;
                // 球形半徑(沒指定路線時的作法,或路線之外的近距離保護)
                if (AccidentDirector.ActiveNoSpawnRadius > 0f
                    && Vector3.Distance(node.transform.position, AccidentDirector.ActiveAccidentPos)
                       < AccidentDirector.ActiveNoSpawnRadius) continue;
            }

            // 🔧 事故封鎖中的節點不生車。
            //    封鎖清單原本只讓「已經在路上的車」改道,生成器完全沒檢查,
            //    結果會直接在封鎖區裡憑空生出新車 —— 等於封鎖白做。
            //    對向車流節奏也靠這個清單,不擋住的話「一批 2 台」永遠算不準。
            if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedNodes != null
                && AccidentDirector.BlockedNodes.Contains(node)) continue;
            // 下一個節點也被封鎖 → 生出來馬上就要改道,不如不生
            if (AccidentDirector.IsAccidentActive && AccidentDirector.BlockedNodes != null)
            {
                bool allNextBlocked = true;
                foreach (var nx in node.nextChoices)
                    if (nx != null && !AccidentDirector.BlockedNodes.Contains(nx)) { allNextBlocked = false; break; }
                if (allNextBlocked) continue;
            }

            float dist = Vector3.Distance(playerTransform.position, node.transform.position);
            if (dist < minSpawnDistance || dist > maxSpawnDistance) continue;
            candidates.Add(node);
        }
        if (candidates.Count == 0) return;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            WaypointNode node = candidates[Random.Range(0, candidates.Count)];
            WaypointNode next = node.nextChoices[Random.Range(0, node.nextChoices.Count)];
            if (next == null) continue;
            if (Physics.CheckSphere(node.transform.position, safeCheckRadius, carLayer)) continue;

            Quaternion rot = Quaternion.LookRotation(
                (next.transform.position - node.transform.position).normalized, Vector3.up);

            // 直接生成在節點高度，實際落地交給 SpawnGroundSnap。
            // 原本這裡有 +0.5「讓車輕輕落地」，但車是 Kinematic 不受重力、永遠不會落下，
            // 那 0.5m 只會變成永久的浮空，所以移除。
            GameObject go = Instantiate(prefab, node.transform.position, rot);
            SpawnGroundSnap.Apply(go);   // 射線打空時就維持節點高度，不再額外調整
            go.name = (spawnScooter ? "Scooter_Replenish_" : "Gray_Replenish_") + Time.time;

            CarNavigation nav = go.GetComponentInParent<CarNavigation>();
            if (nav != null) nav.currentWaypointNode = next;

            if (logSpawn) UnityEngine.Debug.Log($"<color=lime>🐣 [{(spawnScooter ? "機車" : "灰車")}出生] {prefab.name} | 節點:{node.name} | 離玩家 {Vector3.Distance(playerTransform.position, node.transform.position):F0}m</color>", node);
            return;
        }
    }
}