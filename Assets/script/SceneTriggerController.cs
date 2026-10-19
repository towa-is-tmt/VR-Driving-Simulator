using UnityEngine;

public class SceneTriggerController : MonoBehaviour
{
    public WaypointFollowerAI grayCarAI;
    private bool isTriggered = false;

    // 當有物體進入這個觸發器時
    private void OnTriggerEnter(Collider other)
    {
        // 檢查撞到的是不是玩家（檢查標籤或是特定名稱）
        // 這裡建議你的玩家車輛要設定 Tag 為 "Player"
        if (!isTriggered && other.CompareTag("Player"))
        {
            isTriggered = true;
            if (grayCarAI != null)
            {
                grayCarAI.ActivateRight();
                UnityEngine.Debug.Log("<color=green>【感測器】VR車輛已通過，啟動AI！</color>");
            }
        }
    }
}