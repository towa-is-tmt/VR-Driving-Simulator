using UnityEngine;
using TMPro;

/// <summary>
/// 方向燈判定 — 偵測「轉彎沒打方向燈」並依模式處理。
///
///   自由模式 → 只跳提示，不影響任何分數
///   訓練模式 → 跳提示並扣分（預設 5 分）
///
/// 轉彎的判定方式：
///   累積車身的偏航角變化，超過設定角度即視為完成一次轉彎。
///   只在「靠近路口路點」時才判定，避免一般彎道、S 型坡道、變換車道被誤判。
///   （路口靠路點名稱關鍵字辨識，沿用你既有的路網命名，例如 Node_十字路口01）
///
/// 掛在車輛（vehicle）上。
/// </summary>
public class TurnSignalJudge : MonoBehaviour
{
    [Header("來源（留空自動尋找）")]
    [Tooltip("隊友的方向燈腳本 CarIndicatorController，留空會自動尋找")]
    public CarIndicatorController 方向燈;
    public Rigidbody 車身;
    public ScoreManager 計分;
    public TrainingModeManager 模式管理;
    public WaypointNetwork 路網;

    [Header("轉彎判定")]
    [Tooltip("累積偏航角超過此值視為完成一次轉彎（度）")]
    public float 判定轉彎角度 = 50f;

    [Tooltip("低於此速度不判定，避免原地打方向盤被算成轉彎（km/h）")]
    public float 最低判定速度 = 5f;

    [Tooltip("連續這麼久沒有明顯轉向就重置累積角度（秒）")]
    public float 轉彎中斷秒數 = 1.5f;

    [Tooltip("每秒偏航超過此值才算「正在轉彎」（度/秒）")]
    public float 轉彎速率門檻 = 12f;

    [Header("路口限制")]
    [Tooltip("只有靠近路口時才判定。取消勾選則任何轉彎都判定")]
    public bool 只在路口判定 = true;

    [Tooltip("路點名稱含此關鍵字者視為路口")]
    public string 路口關鍵字 = "路口";

    [Tooltip("距離路口多近才算在路口範圍內（公尺）")]
    public float 路口判定半徑 = 20f;

    [Header("扣分")]
    [Tooltip("訓練模式未打方向燈的扣分。自由模式一律不扣")]
    public int 扣分分數 = 5;

    [Tooltip("同一次違規後多久內不再重複判定（秒）")]
    public float 違規冷卻秒數 = 3f;

    [Tooltip("勾選＝只要在這次轉彎前打過燈就算數，不管提早多久（建議）。\n" +
             "取消＝只有在下方的記憶秒數內打的燈才算")]
    public bool 不限打燈時間 = true;

    [Tooltip("打燈記憶時間（秒）。僅在「不限打燈時間」取消勾選時生效")]
    public float 打燈記憶秒數 = 6f;

    [Header("提示文字")]
    public TMP_Text 提示文字;
    public string 提示內容 = "忘記打方向燈！";

    [Tooltip("訓練模式時附加在提示後面的扣分文字，{0} 會替換成扣分數")]
    public string 扣分附註 = "　−{0} 分";
    public Color 提示顏色 = new Color(1f, 0.141f, 0.212f);   // #FF2436 紅
    public float 提示顯示秒數 = 2.5f;

    [Header("提示音效（選填）")]
    public AudioSource 音源;
    public AudioClip 違規音效;
    [Range(0f, 1f)] public float 音量 = 0.8f;

    [Header("除錯")]
    public bool 除錯訊息 = true;

    private const float msToKmH = 3.6f;

    private float 上一幀偏航;
    private float 累積偏航;
    private float 未轉彎計時;
    private float 冷卻計時;
    private float 提示計時;

    // 最後一次打左／右燈的時間點（Time.time）。用時間而非布林，
    // 才能涵蓋「轉彎前就先打燈」的正常駕駛行為。
    private float 上次打左燈時間 = -999f;
    private float 上次打右燈時間 = -999f;

    // 上一次判定完成的時間，用來界定「這次轉彎之前」的範圍
    private float 判定起始時間 = -999f;

    // 由 TurnSignalJudgePauseZone 控制：進入暫停區時停掉轉彎判定
    private bool 偵測暫停中 = false;

    private void Start()
    {
        if (方向燈 == null) 方向燈 = GetComponent<CarIndicatorController>()
                                  ?? FindObjectOfType<CarIndicatorController>(true);
        if (車身   == null) 車身   = GetComponent<Rigidbody>();
        if (計分   == null) 計分   = FindObjectOfType<ScoreManager>(true);
        if (模式管理 == null) 模式管理 = FindObjectOfType<TrainingModeManager>(true);
        if (路網   == null) 路網   = FindObjectOfType<WaypointNetwork>(true);
        if (音源   == null) 音源   = GetComponent<AudioSource>();

        上一幀偏航 = transform.eulerAngles.y;
        隱藏提示();
    }

    private void Update()
    {
        更新提示();
        if (冷卻計時 > 0f) 冷卻計時 -= Time.deltaTime;

        // 打燈記錄在暫停期間仍持續，否則出區後會把「一直打著的燈」誤判成沒打
        記錄打燈();      // 隨時記錄，不限於轉彎期間

        if (偵測暫停中) return;   // 在暫停區內只停掉轉彎判定

        追蹤轉彎();
    }

    // ── 外部控制（給 TurnSignalJudgePauseZone 用）─────────
    /// <summary>進入暫停區：停止轉彎判定，並清掉進區前累積的偏航</summary>
    public void 暫停偵測()
    {
        if (偵測暫停中) return;
        偵測暫停中 = true;
        重置累積();
    }

    /// <summary>離開暫停區：恢復轉彎判定</summary>
    public void 恢復偵測()
    {
        if (!偵測暫停中) return;
        偵測暫停中 = false;
        重置累積();
        上一幀偏航 = transform.eulerAngles.y;   // 必須重抓，否則第一幀會累加整段暫停期間的轉向
    }

    /// <summary>持續記錄最後一次打燈的時間</summary>
    private void 記錄打燈()
    {
        if (方向燈 == null) return;

        // 只認單邊方向燈；雙閃（Hazard）不算有打方向燈
        if (方向燈.current == CarIndicatorController.instate.Left)  上次打左燈時間 = Time.time;
        if (方向燈.current == CarIndicatorController.instate.Right) 上次打右燈時間 = Time.time;
    }

    private void 追蹤轉彎()
    {
        if (車身 == null) return;

        float 目前偏航 = transform.eulerAngles.y;
        float 每幀變化 = Mathf.DeltaAngle(上一幀偏航, 目前偏航);
        上一幀偏航 = 目前偏航;

        float 速度 = 車身.velocity.magnitude * msToKmH;
        if (速度 < 最低判定速度) { 重置累積(); return; }

        float 偏航速率 = Mathf.Abs(每幀變化) / Mathf.Max(Time.deltaTime, 0.0001f);

        if (偏航速率 >= 轉彎速率門檻)
        {
            累積偏航 += 每幀變化;
            未轉彎計時 = 0f;

            if (Mathf.Abs(累積偏航) >= 判定轉彎角度) 完成一次轉彎();
        }
        else
        {
            未轉彎計時 += Time.deltaTime;
            if (未轉彎計時 >= 轉彎中斷秒數) 重置累積();
        }
    }

    private void 完成一次轉彎()
    {
        bool 右轉 = 累積偏航 > 0f;      // Unity 的 Y 軸正向為順時針（右轉）

        // ⚠ 必須在 重置累積() 之前先取得判定結果，否則會讀到被清空的值
        float 上次打燈 = 右轉 ? 上次打右燈時間 : 上次打左燈時間;

        // 不限時間：只要「上次判定之後」打過這個方向的燈就算數，提早多久都不扣分
        bool 有正確打燈 = 不限打燈時間
                        ? (上次打燈 > 判定起始時間)
                        : (Time.time - 上次打燈) <= 打燈記憶秒數;

        重置累積();

        // 判定完就把打燈紀錄清掉，下一次轉彎必須重新打燈
        上次打左燈時間 = 上次打右燈時間 = -999f;
        判定起始時間   = Time.time;

        if (冷卻計時 > 0f) return;
        if (只在路口判定 && !在路口附近()) 
        {
            if (除錯訊息) Debug.Log("[方向燈判定] 有轉彎但不在路口，略過");
            return;
        }

        if (有正確打燈)
        {
            if (除錯訊息)
                Debug.Log($"[方向燈判定] {(右轉 ? "右" : "左")}轉有打燈 ✔" +
                          $"（{Time.time - 上次打燈:F1} 秒前打的）");
            return;
        }

        違規(右轉);
    }

    private void 違規(bool 右轉)
    {
        冷卻計時 = 違規冷卻秒數;

        bool 訓練模式 = 模式管理 != null &&
                        模式管理.currentMode == TrainingModeManager.GameMode.Training;

        // 訓練模式才顯示扣分文字，自由模式只提示
        顯示提示(訓練模式);

        if (音源 != null && 違規音效 != null) 音源.PlayOneShot(違規音效, 音量);

        if (訓練模式 && 計分 != null)
        {
            計分.扣分(扣分分數, $"{(右轉 ? "右" : "左")}轉未打方向燈");
            if (除錯訊息) Debug.Log($"[方向燈判定] 訓練模式 → 扣 {扣分分數} 分");
        }
        else if (除錯訊息)
        {
            Debug.Log("[方向燈判定] 自由模式 → 只提示不扣分");
        }
    }

    private bool 在路口附近()
    {
        if (路網 == null || 路網.allNodes == null) return true;   // 沒路網就不限制

        foreach (var n in 路網.allNodes)
        {
            if (n == null) continue;
            if (!n.name.Contains(路口關鍵字)) continue;
            if (Vector3.Distance(transform.position, n.transform.position) <= 路口判定半徑)
                return true;
        }
        return false;
    }

    private void 重置累積()
    {
        累積偏航 = 0f;
        未轉彎計時 = 0f;
        // 打燈時間不在這裡清除，改由「打燈記憶秒數」自然過期
    }

    // ── 提示 ─────────────────────────────────────────────
    private void 顯示提示(bool 含扣分 = false)
    {
        if (提示文字 == null) return;

        提示文字.text = 含扣分 && 扣分分數 > 0
                      ? 提示內容 + string.Format(扣分附註, 扣分分數)
                      : 提示內容;
        提示文字.color = 提示顏色;
        提示文字.enabled = true;
        提示計時 = 提示顯示秒數;
    }

    private void 更新提示()
    {
        if (提示計時 <= 0f) return;
        提示計時 -= Time.deltaTime;
        if (提示計時 <= 0f) 隱藏提示();
    }

    private void 隱藏提示()
    {
        if (提示文字 != null) 提示文字.enabled = false;
    }
}
