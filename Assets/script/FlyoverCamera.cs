using UnityEngine;

/// <summary>
/// 🎬 空拍運鏡 —— 從起點平滑飛到終點，給成果影片錄「拉升 reveal」用。
///
/// 用法：
///   1. 新建兩個空物件（例如 空拍_起點 / 空拍_終點），在 Scene 視窗擺好位置與角度
///   2. 新建一台 Camera，把這支腳本掛上去，拖入起點與終點
///   3. Play → 按 O → 用 OBS 錄
///
/// ⚠️ 錄之前一定要改的三件事（錄完記得改回來）：
///   ① Lighting → Environment → Other Settings → Fog 的 End：300 → 3000
///      不改的話 300 公尺外全灰，整張地圖看不到。
///   ② 這台 Camera 的 Far Clipping Plane：1000 → 5000
///      預設 1000，從高空看遠處建築會直接被裁掉。
///   ③ GameModeManager（UI介面/模式管理）四個車流數量調到最高
///      空拍的重點是「這張地圖是活的」，沒車就只是一張靜態模型。
///   另外把 CarNavigation.debugLogDetection 等診斷開關全部取消勾選，不然會拖垮 Editor。
/// </summary>
public class FlyoverCamera : MonoBehaviour
{
    [Header("運鏡兩端")]
    [Tooltip("建議：離地約 30m、俯角 20 度、對著有車流的街道")]
    public Transform 起點;

    [Tooltip("建議：離地約 500m、俯角 55 度、涵蓋整張地圖")]
    public Transform 終點;

    [Header("時間")]
    [Tooltip("從起點到終點的秒數。影片裡這一顆給 7 秒")]
    public float 秒數 = 7f;

    [Tooltip("勾選＝Play 之後自動開始，不必按鍵（錄影時畫面比較乾淨）")]
    public bool 自動開始 = false;

    [Tooltip("自動開始前先等幾秒，讓你有時間按下錄影")]
    public float 開始前等待 = 2f;

    [Header("手動觸發")]
    public KeyCode 開始鍵 = KeyCode.O;

    // 進度。負值代表沒在跑
    private float 進度 = -1f;

    private void Start()
    {
        if (自動開始) Invoke(nameof(開始運鏡), 開始前等待);
    }

    private void Update()
    {
        if (Input.GetKeyDown(開始鍵)) 開始運鏡();
        if (進度 < 0f) return;

        if (起點 == null || 終點 == null)
        {
            Debug.LogWarning("🎬 [空拍] 起點或終點沒有設定，停止運鏡。");
            進度 = -1f;
            return;
        }

        進度 += Time.deltaTime / Mathf.Max(0.01f, 秒數);

        // SmoothStep：起步慢、中間快、結尾慢。
        // 觀眾前兩秒認出「這是剛才開車的街」，後面才被拉開看到規模。
        float p = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(進度));

        transform.position = Vector3.Lerp(起點.position, 終點.position, p);
        transform.rotation = Quaternion.Slerp(起點.rotation, 終點.rotation, p);

        if (進度 >= 1f) 進度 = -1f;
    }

    /// <summary>▶️ 從頭開始跑一次運鏡</summary>
    public void 開始運鏡()
    {
        if (起點 == null || 終點 == null)
        {
            Debug.LogWarning("🎬 [空拍] 請先在 Inspector 指定 起點 與 終點。");
            return;
        }

        進度 = 0f;
        transform.position = 起點.position;
        transform.rotation = 起點.rotation;
    }

    /// <summary>⏹️ 中途停下（保持在目前位置）</summary>
    public void 停止運鏡() => 進度 = -1f;

    // Scene 視窗畫出路徑，擺位置時比較好對
    private void OnDrawGizmos()
    {
        if (起點 == null || 終點 == null) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(起點.position, 終點.position);
        Gizmos.DrawWireSphere(起點.position, 3f);
        Gizmos.DrawWireSphere(終點.position, 8f);

        // 兩端的視線方向
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(起點.position, 起點.forward * 25f);
        Gizmos.DrawRay(終點.position, 終點.forward * 60f);
    }
}
