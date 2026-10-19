using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

public class HazardAICar : MonoBehaviour
{
    [Header("1. 循環路徑設定")]
    public List<Transform> waypoints;
    public int currentWaypointIndex = 0;
    public float rotationSpeed = 5f;
    public float arrivalDistance = 2.0f;

    [Header("2. 基礎移動參數")]
    public float moveSpeed = 10f;
    public float brakeHardness = 10f;

    [Header("3. 區域觸發急煞設定 (DualTriggerController 呼叫)")]
    public float stopDuration = 2.0f;
    public float cooldownTime = 3.0f;

    [Header("4. 跟車與偵測系統 (對應 TrafficLight Layer)")]
    public float detectionDistance = 8f;  // 偵測前方多遠
    public float safeDistance = 3.5f;     // 距離多近要完全停下
    public LayerMask detectionLayer;      // 在 Inspector 選擇 TrafficLight

    // ====== 🛠️ 新增：紅綠燈停等變數 ======
    [Header("5. 紅綠燈狀態 (由外部路口 Trigger 控制)")]
    [SerializeField] private bool isWaitingAtTrafficLight = false;

    private float currentHealthSpeed;
    private bool isEmergencyBraking = false; // 區域急煞狀態
    private bool isInCooldown = false;        // 急煞冷卻狀態
    private float calculatedTargetSpeed;      // 經過跟車計算後的目標速度

    void Start()
    {
        currentHealthSpeed = moveSpeed;
        if (waypoints != null && waypoints.Count > currentWaypointIndex)
        {
            transform.LookAt(waypoints[currentWaypointIndex]);
        }
    }

    void Update()
    {
        // 核心邏輯 A：計算偵測與跟車速度 (已內含紅綠燈判斷)
        HandleForwardDetection();

        // 核心邏輯 B：速度平滑過渡 (Lerp)
        currentHealthSpeed = Mathf.Lerp(currentHealthSpeed, calculatedTargetSpeed, Time.deltaTime * brakeHardness);

        // 核心邏輯 C：導航與移動
        if (waypoints != null && waypoints.Count > 0)
        {
            // 修正小細節：如果時速貼近 0，就不要硬轉向，避免車子原地抖動
            HandleNavigation();
        }
    }

    void HandleForwardDetection()
    {
        // 優先權 1：如果正在執行方塊觸發的「急煞」，速度直接為 0
        if (isEmergencyBraking)
        {
            calculatedTargetSpeed = 0;
            return;
        }

        // ====== 🛠️ 新增優先權 2：紅綠燈判斷 ======
        // 如果外部紅綠燈 Trigger 通知需要停等，速度直接為 0，且不執行下方的射線偵測
        if (isWaitingAtTrafficLight)
        {
            calculatedTargetSpeed = 0;
            return;
        }

        // 優先權 3：前方沒事，發射射線偵測前方障礙物或跟車
        RaycastHit hit;
        Vector3 origin = transform.position + Vector3.up * 0.5f;

        if (Physics.Raycast(origin, transform.forward, out hit, detectionDistance, detectionLayer))
        {
            float distance = hit.distance;

            if (distance <= safeDistance)
            {
                // 離前車或紅燈太近了，停下
                calculatedTargetSpeed = 0;
                UnityEngine.Debug.DrawRay(origin, transform.forward * distance, Color.red);
            }
            else
            {
                // 在偵測範圍內：距離越近，速度越慢 (線性減速)
                float speedPercent = (distance - safeDistance) / (detectionDistance - safeDistance);
                calculatedTargetSpeed = moveSpeed * speedPercent;
                UnityEngine.Debug.DrawRay(origin, transform.forward * distance, Color.yellow);
            }
        }
        else
        {
            // 前方沒東西，恢復正常限速
            calculatedTargetSpeed = moveSpeed;
            UnityEngine.Debug.DrawRay(origin, transform.forward * detectionDistance, Color.green);
        }
    }

    void HandleNavigation()
    {
        Transform targetWP = waypoints[currentWaypointIndex];
        Vector3 direction = targetWP.position - transform.position;
        direction.y = 0;

        if (direction != Vector3.zero && currentHealthSpeed > 0.1f) // 加上速度限制，停下時不盲目旋轉
        {
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }

        // 執行位移
        transform.position += transform.forward * currentHealthSpeed * Time.deltaTime;

        // 抵達路點判定
        if (Vector3.Distance(transform.position, targetWP.position) < arrivalDistance)
        {
            currentWaypointIndex = (currentWaypointIndex + 1) % waypoints.Count;
        }
    }

    // --- 供外部 DualTriggerController 呼叫的介面 ---
    public bool CanBrake() => !isEmergencyBraking && !isInCooldown;

    public void TriggerBrakeFromZone()
    {
        if (CanBrake()) StartCoroutine(ExecuteBrakeRoutine());
    }

    IEnumerator ExecuteBrakeRoutine()
    {
        isEmergencyBraking = true;
        UnityEngine.Debug.Log($"<color=red>🛑 區域觸發急煞：{gameObject.name}</color>");
        yield return new WaitForSeconds(stopDuration);

        isEmergencyBraking = false;
        isInCooldown = true;
        yield return new WaitForSeconds(cooldownTime);
        isInCooldown = false;
    }

    // ====== 🛠️ 新增：供外部紅綠燈路口 Trigger 呼叫的公開方法 ======
    /// <summary>
    /// 設定此車輛是否需要因紅綠燈停下
    /// </summary>
    /// <param name="stop">true 代表紅燈強制停下，false 代表綠燈可以通行</param>
    public void SetTrafficLightStatus(bool stop)
    {
        isWaitingAtTrafficLight = stop;
    }
}