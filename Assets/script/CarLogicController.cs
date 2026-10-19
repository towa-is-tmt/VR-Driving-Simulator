using System;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class CarLogicController : MonoBehaviour
{
    [Header("手動指定的車輛")]
    public List<GameObject> whiteCars = new List<GameObject>();

    [Header("車道設定")]
    public float centerLineX = 28f;
    public float leftLaneX = 21f;

    [Header("物理與速度")]
    public float moveSpeed = 45f;
    public float turnSpeed = 7.0f;
    public float laneChangeSpeed = 12f; // 切換車道的橫向力度

    [Header("超車參數")]
    public float rayHeight = 1.0f;      // 提高白色放射線起點
    public float detectionDist = 18f;   // 偵測多遠處有車
    public float safeForwardDist = 8f;  // 超越後要領先多少才切回

    [Header("紅綠燈與任務")]
    public float stopLineZ = 26f;
    public Renderer redLightBall;
    public Renderer greenLightBall;

    private Rigidbody rb;
    private float targetX;
    private bool isOvertaking = false;
    private Vector3 grayStartPos;
    private Quaternion grayStartRot;

    // 用來記錄目前正在超越哪一台車，避免被其他遠處的車干擾
    private GameObject currentTargetCar = null;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        grayStartPos = transform.position;
        grayStartRot = transform.rotation;
        targetX = centerLineX;
        ResetScenario();
    }

    void Update()
    {
        bool isRed = IsLightActive(redLightBall);
        float distToStopLine = stopLineZ - transform.position.z;

        float currentSpeed = moveSpeed;

        // 紅燈停等邏輯
        if (isRed && distToStopLine > 0)
        {
            if (distToStopLine < 15f)
            {
                currentSpeed = Mathf.Lerp(0, moveSpeed, distToStopLine / 15f);
                if (distToStopLine < 0.5f) currentSpeed = 0;
            }
        }
        else
        {
            // 執行超車 AI
            HandleOvertakeAI();
        }

        ApplyMovement(currentSpeed);
    }

    void HandleOvertakeAI()
    {
        // 1. 發射射線偵測前方白車
        Vector3 rayOrigin = transform.position + Vector3.up * rayHeight;
        RaycastHit hit;

        // 直接在 Raycast 成功後判斷，不需要額外的 foundCar 變數
        if (Physics.Raycast(rayOrigin, transform.forward, out hit, detectionDist))
        {
            foreach (var car in whiteCars)
            {
                if (car != null && hit.collider.gameObject == car)
                {
                    if (!isOvertaking)
                    {
                        currentTargetCar = car; // 鎖定這台要超越的車
                        isOvertaking = true;
                        targetX = leftLaneX;
                        Debug.Log("<color=yellow>【動作】發現障礙，切向左車道</color>");
                    }
                    break;
                }
            }
        }

        // 2. 判斷何時切回 (保持不變)
        if (isOvertaking)
        {
            if (currentTargetCar != null)
            {
                float zDiff = transform.position.z - currentTargetCar.transform.position.z;

                if (zDiff > safeForwardDist)
                {
                    targetX = centerLineX;
                    if (Mathf.Abs(transform.position.x - centerLineX) < 0.3f)
                    {
                        isOvertaking = false;
                        currentTargetCar = null;
                        Debug.Log("<color=green>【動作】超越完成，切回原車道</color>");
                    }
                }
            }
            else
            {
                isOvertaking = false;
                targetX = centerLineX;
            }
        }
    }
    void ApplyMovement(float speed)
    {
        if (rb == null) return;

        // 前進速度
        rb.velocity = Vector3.Lerp(rb.velocity, transform.forward * speed, Time.deltaTime * 10f);

        // 橫向位移
        Vector3 currentPos = transform.position;
        currentPos.x = Mathf.MoveTowards(currentPos.x, targetX, Time.deltaTime * laneChangeSpeed);
        transform.position = currentPos;

        // 轉向動態 (S型擺頭)
        float xError = targetX - transform.position.x;
        float steerAngle = Mathf.Clamp(xError * 20f, -25f, 25f);
        Quaternion targetRot = Quaternion.Euler(0, steerAngle, 0);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * turnSpeed);
    }

    public void ResetScenario()
    {
        transform.position = grayStartPos;
        transform.rotation = grayStartRot;
        if (rb) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        targetX = centerLineX;
        isOvertaking = false;
        currentTargetCar = null;
    }

    bool IsLightActive(Renderer r) { return r != null && r.material.color != Color.gray; }

    private void OnDrawGizmos()
    {
        // 畫出放射線 (白色，水平)
        Gizmos.color = Color.white;
        Vector3 rayOrigin = transform.position + Vector3.up * rayHeight;
        Gizmos.DrawLine(rayOrigin, rayOrigin + transform.forward * detectionDist);

        // 畫出偵測點
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(rayOrigin + transform.forward * detectionDist, 0.3f);
    }
}