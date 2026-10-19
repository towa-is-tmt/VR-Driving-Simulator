using UnityEngine;
using UnityEditor;

public class SetupLayerCollision : EditorWindow
{
    [MenuItem("Tools/設定停車格 Layer 碰撞")]
    public static void Setup()
    {
        int lineSensor  = LayerMask.NameToLayer("LineSensor");
        int parkingLine = LayerMask.NameToLayer("ParkingLine");
        int parkingZone = LayerMask.NameToLayer("ParkingZone");
        int vehicleBody = LayerMask.NameToLayer("VehicleBody");
        int spiralWall  = LayerMask.NameToLayer("SpiralWall");
        int spiralGoal  = LayerMask.NameToLayer("SpiralGoal");
        int rampLine = LayerMask.NameToLayer("RampLine");
        int rampZone = LayerMask.NameToLayer("RampZone");

        // ── 檢查 Layer 是否存在 ──────────────────────────────
        bool allLayersExist = true;

        if (lineSensor  == -1) { Debug.LogError("❌ Layer 不存在：LineSensor");  allLayersExist = false; }
        if (parkingLine == -1) { Debug.LogError("❌ Layer 不存在：ParkingLine"); allLayersExist = false; }
        if (parkingZone == -1) { Debug.LogError("❌ Layer 不存在：ParkingZone"); allLayersExist = false; }
        if (vehicleBody == -1) { Debug.LogError("❌ Layer 不存在：VehicleBody"); allLayersExist = false; }
        if (spiralWall  == -1) { Debug.LogError("❌ Layer 不存在：SpiralWall");  allLayersExist = false; }
        if (spiralGoal  == -1) { Debug.LogError("❌ Layer 不存在：SpiralGoal");  allLayersExist = false; }
        if (rampLine == -1) { Debug.LogError("❌ Layer 不存在：RampLine"); allLayersExist = false; }
        if (rampZone == -1) { Debug.LogError("❌ Layer 不存在：RampZone"); allLayersExist = false; }
        
        if (!allLayersExist)
        {
            Debug.LogError("⛔ 有 Layer 尚未建立，請先到 Tags and Layers 新增後再執行！");
            return;
        }

        // ── 設定碰撞並驗證 ───────────────────────────────────
        SetAndVerify(lineSensor,  parkingLine, "LineSensor ↔ ParkingLine");
        SetAndVerify(lineSensor,  parkingZone, "LineSensor ↔ ParkingZone");
        SetAndVerify(vehicleBody, parkingZone, "VehicleBody ↔ ParkingZone");
        SetAndVerify(vehicleBody, spiralWall,  "VehicleBody ↔ SpiralWall");
        SetAndVerify(vehicleBody, spiralGoal,  "VehicleBody ↔ SpiralGoal");
        SetAndVerify(lineSensor,  rampLine, "LineSensor ↔ RampLine");
        SetAndVerify(vehicleBody, rampZone, "VehicleBody ↔ RampZone");

        Debug.Log("✅ 所有 Layer 碰撞設定完成！");
    }

    private static void SetAndVerify(int layerA, int layerB, string label)
    {
        Physics.IgnoreLayerCollision(layerA, layerB, false);

        // 驗證是否成功設定（IgnoreLayerCollision 設 false 代表允許碰撞）
        bool isIgnored = Physics.GetIgnoreLayerCollision(layerA, layerB);
        if (isIgnored)
        {
            Debug.LogError($"❌ 設定失敗：{label} 仍然被忽略！");
        }
        else
        {
            Debug.Log($"✅ 設定成功：{label}");
        }
    }
}