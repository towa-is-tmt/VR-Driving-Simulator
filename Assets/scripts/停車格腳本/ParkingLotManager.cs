using System.Collections.Generic;
using UnityEngine;

public class ParkingLotManager : MonoBehaviour
{
    [SerializeField] private List<ParkingSpaceManager> lots;

    // 由 ParkingDetector.onParkingSuccess 呼叫
    public void CompleteExam()
    {
        Debug.Log("✅ 考試通過！停車成功");
        // TODO: 接考試通過 UI 或流程
    }

    // 由 ParkingDetector.onParkingFail 呼叫
    public void OnParkingFail()
    {
        Debug.Log("❌ 停車失敗");
        // TODO: 接考試失敗 UI 或流程
    }

    public bool AnySpaceAvailable()
    {
        foreach (var lot in lots)
            if (lot.HasAnyAvailable()) return true;
        return false;
    }
}