using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using Unity.XR.CoreUtils;

public class VRstart : MonoBehaviour
{
  private XROrigin xrOrigin;

    [Header("目標駕駛座定位點")]
    [Tooltip("請把車內設定好的 Driver_Seat_Eye_Target 拖進來")]
    public Transform driverSeatTarget;

    private void Start()
    {
        xrOrigin = GetComponent<XROrigin>();
        
        // 延遲一小段時間執行，確保 VR 系統已經完全讀取到頭盔的第一幀硬體旋轉數據
        Invoke(nameof(RecenterPlayer), 0.2f);
        RecenterPlayer();
    }

    private void Update()
    {
        
        // 如果在測試過程中位置或視角又歪了，隨時可以在鍵盤按 R 鍵一秒回正
        if (Input.GetKeyDown(KeyCode.R))
        {
            RecenterPlayer();
        }
    }

    public void RecenterPlayer()
    {
        if (xrOrigin == null || xrOrigin.Camera == null || driverSeatTarget == null) return;

        // =================【第一步：修正旋轉誤差 (Rotation)】=================
        // 1. 計算出「目標前方(Z軸)」與「目前頭盔真正面向的前方」在 Y 軸上的旋轉角度差
        float currentYaw = xrOrigin.Camera.transform.localEulerAngles.y;
        float targetYaw = driverSeatTarget.eulerAngles.y;
        
        // 2. 算出補正角度：目標角度扣掉頭盔自帶的角度
        float yawOffset = targetYaw - currentYaw;

        // 3. 強制將 XR Origin 的轉向設定過去
        transform.rotation = Quaternion.Euler(0f, yawOffset, 0f);

        // =================【第二步：修正位置誤差 (Position)】=================
        // 4. 當旋轉對齊後，再計算目前相機在世界坐標中，距離目標點偏離了多少
        Vector3 currentCameraWorldPos = xrOrigin.Camera.transform.position;
        Vector3 targetWorldPos = driverSeatTarget.position;

        // 5. 算出位移落差
        Vector3 positionOffset = targetWorldPos - currentCameraWorldPos;

        // 6. 補正 XR Origin 的位置，讓相機位置跟目標點完全重疊
        transform.position += positionOffset;

        Debug.Log("<color=lime>[VR 旋轉校準成功]</color> 已完美對齊駕駛座！正前方已鎖定擋風玻璃，不再需要轉頭！");
    }
}