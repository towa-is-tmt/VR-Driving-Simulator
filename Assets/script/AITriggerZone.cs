using UnityEngine;

public class AITriggerZone : MonoBehaviour
{
    public WaypointFollowerAI aiCar;

    public enum Mode { 左啟動, 右啟動, 加速超車 }
    public Mode 功能選擇;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            if (功能選擇 == Mode.左啟動) aiCar.ActivateLeft();
            else if (功能選擇 == Mode.右啟動) aiCar.ActivateRight();
            else if (功能選擇 == Mode.加速超車) aiCar.Boost();

            gameObject.SetActive(false); // 觸發後關閉避免重複
        }
    }
}