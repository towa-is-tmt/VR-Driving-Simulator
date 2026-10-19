using System.Collections.Generic;
using UnityEngine;

public class IntersectionManager : MonoBehaviour
{
    [Header("路口設定")]
    public string intersectionName = "Intersection_1";
    [Tooltip("印出路口配額的批准/踩入/強清理。排查完就關掉,每台車進出都會印")]
    public bool logIntersection = false;

    [SerializeField] private HashSet<ReverseThreatAICar> activeCars = new HashSet<ReverseThreatAICar>();

    [Header("擁擠判定參數")]
    public int maxAllowedCars = 2;

    [Header("🛡️ 自動超距清理 (防計數殘留)")]
    [Tooltip("如果車子距離路口中心超過這個公尺，強行踢出名單")]
    public float forceClearDistance = 25f;
    private float cleanupTimer = 0f;

    public bool IsBlocked => activeCars.Count >= maxAllowedCars;
    public bool IsEmergencyMode { get; private set; } = false;

    private void Update()
    {
        // 每一幀自動清理已被銷毀的無效車輛
        activeCars.RemoveWhere(car => car == null);

        // ⏱️ 每 1 秒進行一次物理距離大掃除，防止邊緣擠壓導致 OnTriggerExit 沒觸發
        cleanupTimer += Time.deltaTime;
        if (cleanupTimer >= 1.0f)
        {
            cleanupTimer = 0f;
            activeCars.RemoveWhere(car => {
                if (car == null) return true;
                float dist = Vector3.Distance(transform.position, car.transform.position);
                if (dist > forceClearDistance)
                {
                    if (logIntersection) UnityEngine.Debug.Log($"<color=red>🧹 [管理器強清理] {car.gameObject.name} 距離路口 {dist:F1} 米已超限，強制剔除計數！</color>");
                    return true;
                }
                return false;
            });
        }
    }

    public bool RequestEntry(ReverseThreatAICar car)
    {
        if (car.CompareTag("Ambulance"))
        {
            activeCars.Add(car);
            return true;
        }

        if (IsEmergencyMode) return false;

        // 🌟 防呆：如果這台車本來就在名單裡（實體已經踩進去了），直接放行，不重複計數
        if (activeCars.Contains(car)) return true;

        if (IsBlocked) return false;

        activeCars.Add(car);
        if (logIntersection) UnityEngine.Debug.Log($"<color=yellow>🧬 [路口配額] {intersectionName} 批准 ({car.gameObject.name})。目前內部總數: {activeCars.Count}</color>");
        return true;
    }

    public void TriggerEmergencyStatus(bool active)
    {
        IsEmergencyMode = active;
        if (active)
        {
            foreach (var car in activeCars)
            {
                if (car != null && !car.CompareTag("Ambulance"))
                {
                    if (DangerEventDirector.Instance != null && !DangerEventDirector.Instance.RequestEvent("路口衝刺")) return;
                    car.TriggerIntersectionRush();
                }
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        ReverseThreatAICar car = other.GetComponentInParent<ReverseThreatAICar>();
        if (car != null)
        {
            if (activeCars.Add(car))
            {
                if (logIntersection) UnityEngine.Debug.Log($"<color=orange>🚘 [路口實體進] {intersectionName} ({car.gameObject.name}) 踩入網格。目前總數: {activeCars.Count}</color>");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        ReverseThreatAICar car = other.GetComponentInParent<ReverseThreatAICar>();
        if (car != null)
        {
            if (activeCars.Remove(car))
            {
                UnityEngine.Debug.Log($"<color=green>✅ [路口實體出] {intersectionName} ({car.gameObject.name}) 離開。目前剩餘: {activeCars.Count}</color>");
            }
        }
    }
}