using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class AmbulanceSpawner : MonoBehaviour
{
    [Header("🚑 救護車設定")]
    public GameObject ambulancePrefab;

    [Header("⏱️ 隨機觸發機制控制")]
    public float checkInterval = 120f;
    [Range(0f, 100f)]
    public float spawnChance = 100f;

    [Header("🎯 尾隨生成設定 (讓救護車專程找玩家)")]
    [Tooltip("救護車要在距離玩家多遠的後方路徑點出生？(單位：公尺)")]
    public float spawnBehindDistance = 60f;

    [Tooltip("允許生成點偏離玩家行駛中心線的左右最大距離 (公尺)。設 2.5米可精準鎖定同一車道。")]
    public float maxLaneLateralOffset = 2.5f;

    [Tooltip("核心紅框前後允許搜尋的緩衝距離 (公尺)。數值越小綠框越短。")]
    public float spawnDistanceBuffer = 10f;

    [Header("🛡️ 秩序安全設定")]
    public float safeCheckRadius = 5f;
    public LayerMask aiCarLayer;
    [Header("⏱️ 救護車專屬節奏")]
    [Tooltip("兩次救護車之間至少間隔幾秒(壓低頻率,把寬路讓給其他危險因子)")]
    public float minAmbulanceInterval = 240f;   // 4 分鐘
    private float lastAmbulanceTime = -999f;
    private TrafficPathNode[] cachedNodes;
    private Transform playerTransform;

    private void Start()
    {
        cachedNodes = FindObjectsOfType<TrafficPathNode>();
        if (ambulancePrefab == null)
        {
            UnityEngine.Debug.LogError("🚨 AmbulanceSpawner: 忘記拉救護車 Prefab 囉！");
            return;
        }

        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null) playerTransform = playerObj.transform;

        StartCoroutine(AmbulanceCheckRoutine());
    }

    private IEnumerator AmbulanceCheckRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(checkInterval); // 建議調小到 8~10 秒,較常檢查條件

            if (FindObjectOfType<AmbulanceAI>() != null) continue;                       // 已有救護車
            if (DangerEventDirector.Instance != null &&
                DangerEventDirector.Instance.IsLongEventActive) continue;                 // 🔒 事故等長事件進行中,不搶

            // 🔧 救護車不走 RequestEvent(它用 BeginLongEvent 直接開場),原本完全繞過冷卻,
            //    導致「側邊切入剛演完,下一秒救護車就來」,玩家來不及反應。
            //    這裡補上最低反應間隔的檢查。
            if (DangerEventDirector.Instance != null &&
                !DangerEventDirector.Instance.IsMinGapSatisfied)
            {
                if (DangerEventDirector.Instance.logRejections)
                    UnityEngine.Debug.Log($"<color=orange>🎬 事件否決:救護車 —— 未達最低反應間隔,還要等 " +
                                          $"{DangerEventDirector.Instance.RemainingMinGap:F1} 秒</color>");
                continue;
            }
            if (Time.time - lastAmbulanceTime < minAmbulanceInterval) continue;           // ⏱️ 還沒到最小間隔
            if (!IsPlayerOnWideRoad()) continue;                                          // 玩家不在寬路

            // 條件都過 → 嘗試生成(內部已做後方節點周圍淨空檢查)
            SpawnAmbulanceBehindPlayer();
        }
    }

    // 玩家目前是否在寬路上:找離玩家最近的節點,看它是不是非窄路
    private bool IsPlayerOnWideRoad()
    {
        if (playerTransform == null || cachedNodes == null) return false;
        TrafficPathNode nearest = null;
        float best = Mathf.Infinity;
        foreach (var n in cachedNodes)
        {
            if (n == null) continue;
            float d = (n.transform.position - playerTransform.position).sqrMagnitude;
            if (d < best) { best = d; nearest = n; }
        }
        return nearest != null && !nearest.isNarrowRoad;
    }
    public void SpawnAmbulanceBehindPlayer()
    {
        if (AccidentDirector.IsAccidentActive || FindObjectOfType<AmbulanceAI>() != null) return;
        if (playerTransform == null) return;

        TrafficPathNode[] allNodes = FindObjectsOfType<TrafficPathNode>();
        TrafficPathNode bestSpawnNode = null;
        float bestDistanceDiff = float.MaxValue;

        // 🛠️ 新增一個暫存標記：記錄選中的這個點到底是不是彎道
        bool isChosenNodeACurve = false;

        Vector3 playerPos = playerTransform.position;
        Vector3 playerForward = playerTransform.forward;

        HashSet<TrafficPathNode> allYieldNodes = new HashSet<TrafficPathNode>();
        foreach (var node in allNodes)
        {
            if (node != null && node.yieldNode != null)
            {
                allYieldNodes.Add(node.yieldNode);
            }
        }

        foreach (var node in allNodes)
        {
            if (node == null) continue;

            // 1. 避讓點過濾
            if (allYieldNodes.Contains(node)) continue;
            // 🚑 窄路過濾：救護車只在寬路節點生成
            if (node.isNarrowRoad) continue;

            // 2. 彎道標記邏輯
            bool isThisNodeACurve = false;
            if (node.intersectionType != TrafficPathNode.IntersectionType.None)
            {
                isThisNodeACurve = true;
            }
            else if (node.nextChoices != null && node.nextChoices.Count > 0)
            {
                foreach (var nextNode in node.nextChoices)
                {
                    if (nextNode == null) continue;
                    float linkDot = Vector3.Dot(node.transform.forward, nextNode.transform.forward);
                    if (linkDot < 0.92f) { isThisNodeACurve = true; break; }
                }
            }

            // --- 🔄 替換區域開始 ---

            // 將節點位置轉換到玩家的局部空間 (Local Space)
            Vector3 relativeNodePos = playerTransform.InverseTransformPoint(node.transform.position);

            // [過濾 A]：判定在後方 (Z < -5)，且距離不超過 100 公尺 (避免跑太遠)
            if (relativeNodePos.z >= -5f || relativeNodePos.z < -100f) continue;

            // [過濾 B]：鎖定同車道！X 軸偏移必須小於設定值 (這是防止跑到對向的關鍵)
            if (Mathf.Abs(relativeNodePos.x) > maxLaneLateralOffset) continue;

            // [過濾 C]：改用「路網行進方向」判斷,不靠節點自身旋轉(節點旋轉常常沒對準路,會誤放對向道)
            if (node.nextChoices == null || node.nextChoices.Count == 0) continue;
            TrafficPathNode dirNext = node.nextChoices[0];
            if (dirNext == null) continue;
            Vector3 travelDir = (dirNext.transform.position - node.transform.position).normalized;

            // C-1：這條路的行進方向要跟玩家同向(排除對向車道)
            if (Vector3.Dot(playerTransform.forward, travelDir) < 0.7f) continue;

            // C-2：行進方向要「朝向玩家」——救護車會順著路往玩家開,而不是背對玩家越開越遠
            Vector3 nodeToPlayerDir = (playerTransform.position - node.transform.position).normalized;
            if (Vector3.Dot(travelDir, nodeToPlayerDir) < 0.5f) continue;

            // --- 🔄 替換區域結束 ---

            // 4. 計算與目標距離（紅框）的前後緩衝帶
            float dist = Vector3.Distance(playerPos, node.transform.position);
            if (dist > spawnBehindDistance - spawnDistanceBuffer && dist < spawnBehindDistance + spawnDistanceBuffer)
            {
                // 5. 周圍疊車重疊檢查
                bool isNodeBlocked = Physics.CheckSphere(node.transform.position, safeCheckRadius, aiCarLayer);
                if (isNodeBlocked) continue;

                // 6. 挑選離完美紅框線最近的節點
                float diff = Mathf.Abs(dist - spawnBehindDistance);
                if (diff < bestDistanceDiff)
                {
                    bestDistanceDiff = diff;
                    bestSpawnNode = node;
                    isChosenNodeACurve = isThisNodeACurve;
                }
            }
        }
        // 8. 精準降落生成
        
        if (bestSpawnNode != null)
        {
            // 🟢 生成點淨空檢查：節點 6m 內有任何車就放棄這一輪，等下次再試
            Collider[] occupants = Physics.OverlapSphere(bestSpawnNode.transform.position, 6f);
            foreach (var o in occupants)
            {
                bool isVehicle = o.CompareTag("Player") || o.transform.root.CompareTag("Player")
                    || o.GetComponentInParent<ReverseThreatAICar>() != null
                    || o.GetComponentInParent<CarNavigation>() != null;
                if (isVehicle) return; // 有車佔位，這輪不生
            }

            Vector3 spawnPos = bestSpawnNode.transform.position;
            // 車頭朝「行進方向」(往下一個節點),不靠節點旋轉,避免一出生就朝錯邊
            Quaternion spawnRot = bestSpawnNode.transform.rotation;
            if (bestSpawnNode.nextChoices != null && bestSpawnNode.nextChoices.Count > 0 && bestSpawnNode.nextChoices[0] != null)
            {
                Vector3 fwd = (bestSpawnNode.nextChoices[0].transform.position - bestSpawnNode.transform.position).normalized;
                if (fwd.sqrMagnitude > 0.01f) spawnRot = Quaternion.LookRotation(fwd, Vector3.up);
            }

            GameObject ambyGO = Instantiate(ambulancePrefab, spawnPos, spawnRot);
            lastAmbulanceTime = Time.time; // ⏱️ 記錄本次生成,開始算間隔
            AmbulanceAI ambyAI = ambyGO.GetComponent<AmbulanceAI>();


            if (ambyAI != null)
            {
                ambyAI.currentWaypointNode = bestSpawnNode;

                // 🚨【核心機制對接】：如果好不容易選到的點是彎道，強制給牠灌入「彎道降速限制」！
                if (isChosenNodeACurve)
                {
                    ambyAI.InitializeSlowSpeedForCurve();
                    UnityEngine.Debug.Log($"<color=yellow>⚠️ [彎道防甩飛出生] 救護車出生在彎道點：{bestSpawnNode.name}，已強制鎖定初速度為低速！</color>");
                }
                else
                {
                    UnityEngine.Debug.Log($"<color=cyan>🚑 [直線車道生成成功] 救護車正常出生在大直路上：{bestSpawnNode.name}</color>");
                }
            }
        }
        else
        {
            UnityEngine.Debug.LogWarning("⚠️ 後方緩衝範圍內都沒有可用節點，本次放棄生成。");
        }
    }

    private void OnDrawGizmos()
    {
        if (!UnityEngine.Application.isPlaying || playerTransform == null)
        {
            GameObject previewPlayer = GameObject.FindWithTag("Player");
            if (previewPlayer != null) playerTransform = previewPlayer.transform;
            else return;
        }

        Matrix4x4 originalMatrix = Gizmos.matrix;
        Gizmos.matrix = playerTransform.localToWorldMatrix;

        float minZ = -(spawnBehindDistance + spawnDistanceBuffer);
        float maxZ = -(spawnBehindDistance - spawnDistanceBuffer);
        if (maxZ > -10f) maxZ = -10f;

        float centerZ = (minZ + maxZ) / 2f;
        float lengthZ = Mathf.Abs(minZ - maxZ);

        Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
        Gizmos.DrawCube(new Vector3(0f, 0.5f, centerZ), new Vector3(maxLaneLateralOffset * 2f, 1f, lengthZ));

        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(new Vector3(0f, 0.5f, centerZ), new Vector3(maxLaneLateralOffset * 2f, 1f, lengthZ));

        Gizmos.color = Color.red;
        Vector3 idealCenter = new Vector3(0f, 0.5f, -spawnBehindDistance);
        Gizmos.DrawWireCube(idealCenter, new Vector3(maxLaneLateralOffset * 2f, 1.2f, 1.5f));

        Gizmos.matrix = originalMatrix;

        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(playerTransform.position + Vector3.up * 2f, playerTransform.forward * 5f);
    }
}