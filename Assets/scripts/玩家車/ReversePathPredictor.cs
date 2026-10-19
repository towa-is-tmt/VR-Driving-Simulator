using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(LineRenderer))] 
public class WheelPathPredictor : MonoBehaviour
{
    [Header("四個輪胎參照物 (請手動拖入)")]
    public Transform frontWheelLeft;   // 左前輪
    public Transform frontWheelRight;  // 右前輪
    public Transform rearWheelLeft;    // 左後輪
    public Transform rearWheelRight;   // 右後輪

    [Header("路徑渲染器")]
    public LineRenderer frontLeftLine;  // 左前輪 LineRenderer
    public LineRenderer frontRightLine; // 右前輪 LineRenderer
    public LineRenderer rearLeftLine;   // 左後輪 LineRenderer
    public LineRenderer rearRightLine;  // 右後輪 LineRenderer

    [Header("轉向來源 (建議拖入前輪 WheelCollider)")]
    [Tooltip("打角的正式來源。DriveCar 與 CarPhysicsController 都會寫入這個值，且不含輪胎滾動污染。留空才會退回舊行為：讀 frontWheelLeft 的 local Y。")]
    public WheelCollider frontSteerCollider;

    [Header("路徑與幾何設定")]
    public int segments = 50;           // 線條平滑度
    public float predictDistance = 15f; // 預測長度
    public float wheelBase = 2.8f;      // 軸距 (前後輪距離)

    // 只在第一次退回舊行為時警告一次，避免洗版
    private bool steerSourceWarned = false;

    void LateUpdate()
    {
        // 檢查參照物是否齊全
        if (frontWheelLeft == null || frontWheelRight == null || 
            rearWheelLeft == null || rearWheelRight == null) return;

        // 1. 取得轉向打角
        //    優先讀 WheelCollider.steerAngle：這是控制器實際寫入的乾淨數值，
        //    不會因為輪胎滾動 (繞 X 軸) 造成 Euler 分解翻轉而跳 180 度。
        float steerAngle;
        if (frontSteerCollider != null)
        {
            steerAngle = frontSteerCollider.steerAngle;
        }
        else
        {
            // 舊行為：假設前輪物件自己的 local Y 就是打角。
            // 注意本專案的轉向角掛在 flSteerHandle 上，輪胎 LOD0 的 Y 恆為 0。
            steerAngle = frontWheelLeft.localEulerAngles.y;
            if (!steerSourceWarned)
            {
                steerSourceWarned = true;
                Debug.LogWarning("<color=orange>[預測線]</color> frontSteerCollider 未指定，改讀 frontWheelLeft 的 local Y。若該物件本身不帶轉向角，線條將不會彎曲。");
            }
        }

        if (steerAngle > 180f) steerAngle -= 360f;

        // 直線死區保護
        if (Mathf.Abs(steerAngle) < 0.5f)
        {
            steerAngle = 0f;
        }

        // 2. 執行繪製
        DrawWheelPaths(steerAngle);
    }

    void DrawWheelPaths(float steerAngle)
    {
        List<Vector3> frontLeftPoints = new List<Vector3>();
        List<Vector3> frontRightPoints = new List<Vector3>();
        List<Vector3> rearLeftPoints = new List<Vector3>();
        List<Vector3> rearRightPoints = new List<Vector3>();

        // 1. 讀取實體輪胎起點
        Vector3 flPos = frontWheelLeft.position;
        Vector3 frPos = frontWheelRight.position;
        Vector3 rlPos = rearWheelLeft.position;
        Vector3 rrPos = rearWheelRight.position;

        // 計算輪距與軸距姿態
        float trackWidth = Vector3.Distance(flPos, frPos);
        float halfTrack = trackWidth * 0.5f;

        // 算出現場前後輪軸的中心點
        Vector3 frontAxleCenter = (flPos + frPos) * 0.5f;
        Vector3 rearAxleCenter = (rlPos + rrPos) * 0.5f;

        // 前進與倒車的模擬中心點
        Vector3 forwardCarCenter = (frontAxleCenter + rearAxleCenter) * 0.5f;
        Vector3 backwardCarCenter = forwardCarCenter;

        Vector3 carForward = (frontAxleCenter - rearAxleCenter).normalized;
        Quaternion fRot = Quaternion.LookRotation(carForward, Vector3.up);
        Quaternion bRot = Quaternion.LookRotation(carForward, Vector3.up);

        float stepDist = predictDistance / segments;
        float steerRad = steerAngle * Mathf.Deg2Rad;

        // 阿克曼轉向角步進 (弧度 = 步長 / 軸距 * Tan(打角))
        bool isStraight = Mathf.Abs(steerAngle) < 0.1f;
        float deltaTheta = isStraight ? 0f : (stepDist / wheelBase) * Mathf.Tan(steerRad) * Mathf.Rad2Deg;

        Vector3 upOffset = Vector3.up * 0.05f;

        for (int i = 0; i <= segments; i++)
        {
            // === 1. 計算【前輪】當前模擬點 (向前) ===
            Vector3 fRight = fRot * Vector3.right;
            Vector3 fForward = fRot * Vector3.forward;
            Vector3 currentFrontAxle = forwardCarCenter + fForward * (wheelBase * 0.5f);

            frontLeftPoints.Add(currentFrontAxle - (fRight * halfTrack) + upOffset);
            frontRightPoints.Add(currentFrontAxle + (fRight * halfTrack) + upOffset);

            // === 2. 計算【後輪】當前模擬點 (向後) ===
            Vector3 bRight = bRot * Vector3.right;
            Vector3 bForward = bRot * Vector3.forward;
            Vector3 currentRearAxle = backwardCarCenter - bForward * (wheelBase * 0.5f);

            rearLeftPoints.Add(currentRearAxle - (bRight * halfTrack) + upOffset);
            rearRightPoints.Add(currentRearAxle + (bRight * halfTrack) + upOffset);

            // === 3. 更新下一個模擬步驟的車身位置與角度 ===
            if (isStraight)
            {
                forwardCarCenter += fForward * stepDist;
                backwardCarCenter -= bForward * stepDist;
            }
            else
            {
                // 前輪向前推進並轉向
                forwardCarCenter += fForward * stepDist;
                fRot *= Quaternion.Euler(0, deltaTheta, 0);

                // 後輪向後推進並轉向 (倒車時轉向角符號相反)
                backwardCarCenter -= bForward * stepDist;
                bRot *= Quaternion.Euler(0, -deltaTheta, 0);
            }
        }

        // 4. 套用到 LineRenderer
        UpdateLine(frontLeftLine, frontLeftPoints);
        UpdateLine(frontRightLine, frontRightPoints);
        UpdateLine(rearLeftLine, rearLeftPoints);
        UpdateLine(rearRightLine, rearRightPoints);
    }

    void UpdateLine(LineRenderer lr, List<Vector3> points)
    {
        if (lr != null)
        {
            lr.positionCount = points.Count;
            lr.SetPositions(points.ToArray());
        }
    }
}
