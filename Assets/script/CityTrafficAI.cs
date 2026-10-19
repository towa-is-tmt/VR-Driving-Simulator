using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// ⚠️ 注意：已移除 using System.Diagnostics; 以免導致 Debug.DrawRay 失效

public class CityTrafficAI : MonoBehaviour
{
    private NavMeshAgent navAgent;

    [Header("路徑引導系統")]
    public List<Transform> laneWaypoints = new List<Transform>();
    public int currentTargetIndex = 0;
    public float nodeArrivalRange = 3f;

    [Header("速度設定")]
    public float normalSpeed = 8.0f;    // 正常行駛速度
    public float slowSpeed = 6.0f;      // 減速時的速度

    [Header("歐卡式避障感應")]
    public float sensorRange = 25.0f;   // 感應範圍
    public LayerMask obstacleLayers;

    void Start()
    {
        navAgent = GetComponent<NavMeshAgent>();
        navAgent.speed = normalSpeed;

        // 設定隨機避障優先級，避免兩車重疊時發呆
        navAgent.avoidancePriority = UnityEngine.Random.Range(10, 90);

        if (laneWaypoints.Count > 0)
        {
            ApplyNewDestination();
        }
    }

    void Update()
    {
        // 核心偵測函式
        ExecuteSensorDetection();

        // 檢查路徑有效性
        if (navAgent.pathStatus == NavMeshPathStatus.PathPartial)
        {
            UnityEngine.Debug.LogWarning(gameObject.name + "：找不到完整的導航路徑！");
        }

        // 到達節點判斷
        if (!navAgent.pathPending && navAgent.remainingDistance < nodeArrivalRange)
        {
            MoveToNextNode();
        }
    }

    void MoveToNextNode()
    {
        if (laneWaypoints.Count == 0) return;

        currentTargetIndex++;

        // 循環判斷
        if (currentTargetIndex >= laneWaypoints.Count)
        {
            UnityEngine.Debug.Log(gameObject.name + "：完成一圈，重置路徑");
            currentTargetIndex = 0;

            // 如果起點與終點不連通，可使用 navAgent.Warp(laneWaypoints[0].position);
            navAgent.ResetPath();
        }

        ApplyNewDestination();
    }

    void ApplyNewDestination()
    {
        if (laneWaypoints.Count > currentTargetIndex && laneWaypoints[currentTargetIndex] != null)
        {
            navAgent.SetDestination(laneWaypoints[currentTargetIndex].position);
        }
    }

    void ExecuteSensorDetection()
    {
        // 發射點往前推 2.2m，高度提升至 0.8m，確保不被地表或自身碰撞擋住
        Vector3 sensorStartPos = transform.position + (transform.forward * 2.2f) + (Vector3.up * 0.8f);

        RaycastHit sensorHit;

        // 畫出偵錯紅線（請務必開啟 Scene 視窗的 Gizmos）
        UnityEngine.Debug.DrawRay(sensorStartPos, transform.forward * sensorRange, Color.red);

        // 偵測包含 AI_Car 與 TrafficLight 層的障礙物
        if (Physics.Raycast(sensorStartPos, transform.forward, out sensorHit, sensorRange, obstacleLayers))
        {
            // 【核心排除邏輯】如果是射中自己，就忽略
            if (sensorHit.collider.transform.root == this.transform.root)
            {
                ReleaseBrakes();
                return;
            }

            // 射中目標，畫出綠色連線
            UnityEngine.Debug.DrawLine(sensorStartPos, sensorHit.point, Color.green);

            // A. 紅綠燈偵測
            if (sensorHit.collider.CompareTag("TrafficSensor"))
            {
                SimpleTrafficLight lightCtrl = sensorHit.collider.GetComponentInParent<SimpleTrafficLight>();
                if (lightCtrl != null && lightCtrl.isRedOrYellow)
                {
                    ControlSpeed(sensorHit.distance);
                    return;
                }
            }
            // B. 車輛或其他障礙物偵測
            else
            {
                ControlSpeed(sensorHit.distance);
                return;
            }
        }

        // 前方無礙，放開煞車
        ReleaseBrakes();
    }

    void ControlSpeed(float distance)
    {
        navAgent.isStopped = false;

        // 1. 絕對停止區 (6m 內)
        if (distance <= 6.0f)
        {
            navAgent.isStopped = true;
            navAgent.velocity = Vector3.zero;
            navAgent.speed = 0;
        }
        // 2. 極低速爬行區 (6m - 10m)
        else if (distance <= 10.0f)
        {
            navAgent.speed = 1.0f;
        }
        // 3. 動態緩速區 (10m - 25m)
        else if (distance <= 25.0f)
        {
            float speedRatio = (distance - 10.0f) / (25.0f - 10.0f);
            navAgent.speed = Mathf.Lerp(1.5f, normalSpeed, speedRatio);
        }
        else
        {
            navAgent.speed = normalSpeed;
        }
    }

    void ReleaseBrakes()
    {
        if (navAgent != null && navAgent.isActiveAndEnabled)
        {
            navAgent.isStopped = false;
            navAgent.speed = normalSpeed;
        }
    }

    // 輔助函式：緊急停止
    void PerformEmergencyStop()
    {
        if (navAgent != null && navAgent.isActiveAndEnabled)
        {
            navAgent.isStopped = true;
            navAgent.velocity = Vector3.zero;
        }
    }
}