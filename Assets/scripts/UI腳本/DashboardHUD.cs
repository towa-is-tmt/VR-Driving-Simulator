using UnityEngine;
using TMPro;

/// <summary>
/// 儀表板顯示 — 把車速與檔位寫到 TMP 文字上。
///
/// 車速直接從 Rigidbody 取得，所以 G923 與鍵盤兩種操作模式都適用，
/// 不必依賴任何一支控制腳本（切換操作方式時顯示不會中斷）。
///
/// 檔位讀 CarGearController，和考試偵測讀的是同一個來源，不會不同步。
///
/// 掛在儀表板的父物件上，或任一常駐物件都可以。
/// </summary>
public class DashboardHUD : MonoBehaviour
{
    [Header("資料來源（留空會自動尋找）")]
    [Tooltip("車輛的 Rigidbody，用來算時速")]
    public Rigidbody 車身;

    [Tooltip("檔位來源，與考試偵測共用同一個")]
    public CarGearController 檔位控制;

    [Header("顯示文字")]
    [Tooltip("用 TMP_Text 型別，UI 版（TextMeshProUGUI）與 3D 版（TextMeshPro）都能拖進來。\n" +
             "車內儀表板通常是 3D 版，VR 化後也繼續適用")]
    public TMP_Text 時速文字;
    public TMP_Text 檔位文字;

    [Header("時速格式")]
    [Tooltip("{0} 會替換成時速數字。例如「{0}」「{0} km/h」「時速 {0}」")]
    public string 時速格式 = "{0} km/h";

    [Tooltip("小數位數。0 = 只顯示整數（一般儀表板都是整數）")]
    [Range(0, 2)] public int 小數位數 = 0;

    [Tooltip("數字平滑變化，避免高速時跳動得太劇烈。0 = 不平滑")]
    [Range(0f, 20f)] public float 平滑速度 = 8f;

    [Header("檔位顯示")]
    [Tooltip("{0} 會替換成檔位字母。例如「{0}」「檔位 {0}」")]
    public string 檔位格式 = "{0}";

    [Tooltip("檔位文字依檔位變色")]
    public bool 檔位變色 = true;
    public Color P色 = new Color(0.49f, 0.58f, 0.68f);   // #7E93AD 灰
    public Color D色 = new Color(0.216f, 0.878f, 0.627f); // #37E0A0 綠
    public Color R色 = new Color(1f, 0.541f, 0.122f);     // #FF8A1F 橘

    [Header("超速提示（選填）")]
    [Tooltip("超過此時速時速文字變色。設 0 則不啟用")]
    public float 超速門檻 = 0f;
    public Color 超速色 = new Color(1f, 0.141f, 0.212f);  // #FF2436 紅

    private const float msToKmH = 3.6f;
    private float 顯示用時速 = 0f;
    private Color 時速原色 = Color.white;

    private void Awake()
    {
        if (時速文字 != null) 時速原色 = 時速文字.color;
    }

    private void Start()
    {
        自動尋找來源();
    }

    private void 自動尋找來源()
    {
        if (車身 == null)
        {
            // 優先從車輛控制腳本找，找不到再用 CarGearController 的物件
            var cpc = FindObjectOfType<CarPhysicsController>(true);
            if (cpc != null) 車身 = cpc.GetComponent<Rigidbody>();

            if (車身 == null)
            {
                var kb = FindObjectOfType<G923CarController>(true);
                if (kb != null) 車身 = kb.GetComponent<Rigidbody>();
            }
        }

        if (檔位控制 == null) 檔位控制 = FindObjectOfType<CarGearController>(true);

        if (車身 == null)     Debug.LogWarning("[儀表板] 找不到車輛 Rigidbody，時速無法顯示。");
        if (檔位控制 == null) Debug.LogWarning("[儀表板] 找不到 CarGearController，檔位無法顯示。");
    }

    private void Update()
    {
        更新時速();
        更新檔位();
    }

    private void 更新時速()
    {
        if (時速文字 == null || 車身 == null) return;

        float 實際時速 = 車身.velocity.magnitude * msToKmH;

        // 平滑處理，避免數字劇烈跳動
        顯示用時速 = 平滑速度 > 0f
                   ? Mathf.Lerp(顯示用時速, 實際時速, 1f - Mathf.Exp(-平滑速度 * Time.deltaTime))
                   : 實際時速;

        時速文字.text = string.Format(時速格式, 顯示用時速.ToString("F" + 小數位數));

        // 超速變色
        if (超速門檻 > 0f)
            時速文字.color = (實際時速 > 超速門檻) ? 超速色 : 時速原色;
    }

    private void 更新檔位()
    {
        if (檔位文字 == null || 檔位控制 == null) return;

        var 檔 = 檔位控制.currentGear;
        檔位文字.text = string.Format(檔位格式, 檔.ToString());

        if (!檔位變色) return;

        檔位文字.color = 檔 switch
        {
            CarGearController.AutomaticGear.D => D色,
            CarGearController.AutomaticGear.R => R色,
            _                                 => P色,
        };
    }
}
