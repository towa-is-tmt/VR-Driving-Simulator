using System.Collections.Generic; // 必須引用，才能使用 List
using UnityEngine;

public class DualTriggerController : MonoBehaviour
{
    private bool isPlayerIn = false;

    // 使用 List 記錄當前所有在區域內的 AI
    private List<HazardAICar> aiCarsInZone = new List<HazardAICar>();

    private void Update()
    {
        // 核心邏輯：當玩家在裡面，且至少有一台 AI 在裡面時
        if (isPlayerIn && aiCarsInZone.Count > 0)
        {
            // 遍歷清單中所有的 AI 車
            foreach (HazardAICar car in aiCarsInZone)
            {
                // 這裡會呼叫你 HazardAICar.cs 裡的 public 函數
                if (car != null && car.CanBrake())
                {
                    if (DangerEventDirector.Instance != null && !DangerEventDirector.Instance.RequestEvent("急煞")) return;
                    car.TriggerBrakeFromZone();
                }
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        // 判定玩家車輛進入 (Tag 需設為 Player)
        if (other.CompareTag("Player"))
        {
            isPlayerIn = true;
            Debug.Log("👤 玩家進入感應區，準備判定急煞...");
        }

        // 判定 AI 車輛進入：嘗試取得 HazardAICar 組件
        HazardAICar ai = other.GetComponent<HazardAICar>();
        if (ai != null)
        {
            if (!aiCarsInZone.Contains(ai))
            {
                aiCarsInZone.Add(ai);
                Debug.Log($"🤖 AI ({other.name}) 進入，區內共有 {aiCarsInZone.Count} 台 AI");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        // 玩家離開
        if (other.CompareTag("Player"))
        {
            isPlayerIn = false;
        }

        // AI 離開：從清單中移除
        HazardAICar ai = other.GetComponent<HazardAICar>();
        if (ai != null)
        {
            aiCarsInZone.Remove(ai);
            Debug.Log($"🤖 AI ({other.name}) 離開，區內剩餘 {aiCarsInZone.Count} 台 AI");
        }
    }
}