using UnityEngine;

/// <summary>
/// 🖥 VR 儀表板螢幕:小地圖 / 上帝視角 切換
///
/// 設計原則:VR 裡「視角永遠留在駕駛座」,不要把攝影機拉到天上(會暈、會失去定位)。
/// 改成同一塊儀表板螢幕切換顯示內容 —— 就像真車的倒車顯影。
///
/// 運作方式:
///   兩台攝影機都把畫面渲染到同一張 RenderTexture,但同一時間只啟用一台。
///   儀表板上的 Quad 貼那張 RenderTexture,所以螢幕內容會跟著切換。
///
/// 切換條件:預設讀 DriveCar 的 IPlayerVehicleState.IsReversing。
///   現在按 S 倒車就會切成上帝視角;之後接 G923,只要讓 DriveCar 的
///   IsReversing 反映排檔桿的 R 檔,這裡完全不用改。
///
/// 掛在哪:隨便一個常駐物件都可以,建議掛在 playerCAR 根節點上。
/// </summary>
public class VRDashboardScreen : MonoBehaviour
{
    [Header("🎥 兩台攝影機 (都要把 Target Texture 設成同一張 RenderTexture)")]
    [Tooltip("平常顯示的小地圖攝影機")]
    public Camera minimapCamera;
    [Tooltip("倒車時顯示的上帝視角攝影機")]
    public Camera godViewCamera;

    [Header("🚗 切換來源")]
    [Tooltip("玩家車上實作 IPlayerVehicleState 的元件所在物件(通常是 vehicle ONLY LOD0)。留空會自動用 Tag=Player 尋找")]
    public GameObject playerVehicle;

    [Tooltip("找不到 IPlayerVehicleState 時的備援按鍵(測試用)")]
    public KeyCode fallbackKey = KeyCode.R;

    [Header("⏱ 切換平滑")]
    [Tooltip("排檔切換後要維持幾秒才切回,避免快速切換造成畫面閃爍")]
    public float switchHoldTime = 0.4f;

    [Header("🔧 診斷")]
    public bool debugLog = false;

    private IPlayerVehicleState vehicleState;
    private bool showingGodView = false;
    private float lastSwitchTime = -999f;

    private void Start()
    {
        ResolveVehicle();

        if (minimapCamera == null || godViewCamera == null)
        {
            Debug.LogError("🖥 [儀表板螢幕] minimapCamera 或 godViewCamera 沒有指定,無法運作。");
            enabled = false;
            return;
        }

        if (minimapCamera.targetTexture == null || godViewCamera.targetTexture == null)
            Debug.LogWarning("🖥 [儀表板螢幕] 有攝影機的 Target Texture 是空的。兩台都要指定成同一張 RenderTexture。");
        else if (minimapCamera.targetTexture != godViewCamera.targetTexture)
            Debug.LogWarning("🖥 [儀表板螢幕] 兩台攝影機的 Target Texture 不是同一張,螢幕不會正確切換。");

        ApplyState(false, true);
    }

    private void ResolveVehicle()
    {
        if (playerVehicle == null)
            playerVehicle = GameObject.FindGameObjectWithTag("Player");

        if (playerVehicle != null)
            vehicleState = playerVehicle.GetComponent<IPlayerVehicleState>();

        if (vehicleState == null)
            Debug.LogWarning($"🖥 [儀表板螢幕] 找不到 IPlayerVehicleState" +
                             $"{(playerVehicle != null ? $"(檢查的物件是 {playerVehicle.name})" : "(也找不到 Tag=Player 的物件)")}," +
                             $" 改用按鍵 {fallbackKey} 手動切換。");
    }

    private void Update()
    {
        // 車子可能是執行時生成的,沒抓到就持續重試
        if (vehicleState == null && playerVehicle == null) ResolveVehicle();

        bool wantGodView = vehicleState != null
            ? vehicleState.IsReversing
            : Input.GetKey(fallbackKey);

        if (wantGodView == showingGodView) return;

        // 維持時間沒到就先不切,避免手排來回撥造成閃爍
        if (Time.time - lastSwitchTime < switchHoldTime) return;

        ApplyState(wantGodView, false);
    }

    private void ApplyState(bool godView, bool force)
    {
        if (!force && godView == showingGodView) return;

        showingGodView = godView;
        lastSwitchTime = Time.time;

        minimapCamera.enabled = !godView;
        godViewCamera.enabled = godView;

        if (debugLog)
            Debug.Log($"🖥 [儀表板螢幕] 切換為 {(godView ? "上帝視角(倒車中)" : "小地圖")}");
    }

    /// <summary>供外部(例如 G923 排檔腳本)直接指定,不透過 IsReversing</summary>
    public void SetGodView(bool on) => ApplyState(on, false);
}
