using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

public class TruckSimAI : MonoBehaviour
{
    private NavMeshAgent agent;

    [Header("路徑設定")]
    public List<Transform> waypoints; // 拖入該車道的 Node 列表
    private int currentIndex = 0;

    [Header("前車偵測")]
    public float detectionDistance = 10f; // 偵測距離

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        UpdateDestination();
    }

    void Update()
    {
        // 1. 歐卡式前車偵測 (防撞)
        CheckForObstacles();

        // 2. 抵達檢查點後換下一個
        if (!agent.pathPending && agent.remainingDistance < 1.5f)
        {
            SetNextWaypoint();
        }
    }

    void SetNextWaypoint()
    {
        if (waypoints.Count == 0) return;
        currentIndex = (currentIndex + 1) % waypoints.Count; // 循環跑
        UpdateDestination();
    }

    void UpdateDestination()
    {
        if (waypoints[currentIndex] != null)
            agent.SetDestination(waypoints[currentIndex].position);
    }

    void CheckForObstacles()
    {
        RaycastHit hit;
        // 從車頭高度發射射線
        Vector3 rayOrigin = transform.position + Vector3.up * 0.5f;

        if (Physics.Raycast(rayOrigin, transform.forward, out hit, detectionDistance))
        {
            // 如果撞到別台車 (Tag 設為 AI_Car) 或紅綠燈感應區
            if (hit.collider.CompareTag("AI_Car") || hit.collider.CompareTag("TrafficSensor"))
            {
                // 如果是紅綠燈，判斷燈號
                SimpleTrafficLight light = hit.collider.GetComponentInParent<SimpleTrafficLight>();
                if (light != null && light.isRedOrYellow)
                {
                    StopAgent();
                    return;
                }

                // 如果是前車，跟著停下
                if (hit.collider.CompareTag("AI_Car"))
                {
                    StopAgent();
                    return;
                }
            }
        }
        ResumeAgent();
    }

    void StopAgent()
    {
        agent.isStopped = true;
        agent.velocity = Vector3.zero;
    }

    void ResumeAgent()
    {
        agent.isStopped = false;
    }
}