// 這裡原本有 using System.Diagnostics; 但它一個型別都沒被用到，
// 反而讓 Debug 在 UnityEngine.Debug 與 System.Diagnostics.Debug 之間撞名（CS0104）。
// 移除比逐行寫 UnityEngine.Debug 乾淨，也避免之後新增的 Debug.Log 又踩一次。
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// 操作方式管理器 — 依設定的「操作方式」開關車輛的輸入相關元件。
///
///   G923 模式  → 啟用 CarPhysicsController 與 PlayerInput（方向盤驅動）
///   鍵盤(PC)   → 停用這兩支，改由鍵盤控制腳本接手
///
/// 觸發時機有兩個，缺一不可：
///   ① 場景載入完成時（進 car 場景才有車可以設定）
///   ② 設定變更時（玩家在遊戲中改設定要立刻生效）
///
/// 設定值統一存在 GameSettings，會寫入 PlayerPrefs，重開遊戲也記得。
/// </summary>
public class InputManager : MonoBehaviour
{
    public static InputManager Instance { get; private set; }

    [Header("目標場景")]
    [Tooltip("哪個場景載入後要套用（駕駛場景名稱）")]
    public string 駕駛場景名稱 = "car";

    [Header("車輛物件")]
    [Tooltip("車輛物件名稱。找不到時會改用型別搜尋當備援")]
    public string 車輛物件名稱 = "vehicle";

    [Header("鍵盤模式要啟用的元件")]
    [Tooltip("勾選＝進入駕駛場景時自動在車上尋找 KeyboardCarController。\n" +
             "本管理器在車庫場景、車子在 car 場景，跨場景無法拖引用，所以預設用自動尋找。")]
    public bool 自動尋找鍵盤控制 = true;

    [Tooltip("同場景內的額外元件才需要手動指定；跨場景的請用上面的自動尋找")]
    public Behaviour[] 鍵盤模式啟用;

    [Header("切換時的安全處理")]
    [Tooltip("切換操作方式時把車速歸零。\n" +
             "避免行進中切換後失去動力與煞車，車子卻靠慣性一路滑行、完全無法控制。")]
    public bool 切換時停車 = true;

    [Tooltip("沒有控制腳本接手時施加的駐車煞車力，避免車子在斜坡自行溜動")]
    public float 駐車煞車力 = 5000f;

    [Header("除錯")]
    public bool 除錯訊息 = true;

    private void Awake()
    {
        // 跨場景保留單一實例。
        // ⚠ 重複時只 Destroy「這個元件」，不是整個 GameObject——
        //   因為本腳本可能和場景專屬腳本（例如 GarageMenuController）掛在同一物件上，
        //   若 Destroy(gameObject) 會把那些腳本一起銷毀，導致回主選單後按鈕全部失效。
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // 只讓「這個元件的物件」在許可時常駐；若和場景腳本共用物件，
        // DontDestroyOnLoad 仍會保留整個物件，因此仍建議把本腳本放獨立空物件。
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += 場景載入完成;
        GameSettings.設定變更 += 套用設定;      // 設定一改就即時生效
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= 場景載入完成;
        GameSettings.設定變更 -= 套用設定;
    }

    private void 場景載入完成(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == 駕駛場景名稱) 套用設定();
    }

    /// <summary>
    /// 給設定選單的按鈕呼叫。
    /// 參數 true ＝ 鍵盤(PC)、false ＝ G923，可在 Inspector 的 OnClick 勾選。
    /// </summary>
    public void SetControlMode(bool isKeyboard)
    {
        GameSettings.操作方式 = isKeyboard
            ? GameSettings.操作方式類型.鍵盤
            : GameSettings.操作方式類型.G923;
        // GameSettings 會發出「設定變更」事件，套用由 套用設定() 統一處理
    }

    /// <summary>依目前設定開關元件</summary>
    public void 套用設定()
    {
        bool 用G923 = GameSettings.是G923模式;

        // 手動指定的元件（同場景用）
        if (鍵盤模式啟用 != null)
            foreach (var b in 鍵盤模式啟用)
                if (b != null) b.enabled = !用G923;

        var 車 = 尋找車輛();
        if (車 == null)
        {
            // 不在駕駛場景時找不到車是正常的，不需要警告
            if (除錯訊息 && SceneManager.GetActiveScene().name == 駕駛場景名稱)
                Debug.LogWarning($"[操作方式] 在 {駕駛場景名稱} 場景找不到車輛，無法套用。");
            return;
        }

        // ⚠ 用泛型 GetComponent，不要用帶空格的字串（"Car Physics Controller" 永遠找不到）
        var carPhysics = 車.GetComponent<CarPhysicsController>();
        var playerInput = 車.GetComponent<PlayerInput>();

        // 鍵盤控制腳本在車上，跨場景無法拖引用，改用自動尋找
        KeyboardCarController 鍵盤控制 = null;
        if (自動尋找鍵盤控制)
        {
            鍵盤控制 = 車.GetComponent<KeyboardCarController>()
                    ?? 車.GetComponentInChildren<KeyboardCarController>(true);
            if (鍵盤控制 != null) 鍵盤控制.enabled = !用G923;
        }

        // G923 模式才需要這兩支：方向盤與踏板都是透過 Player Input 送進 CarPhysicsController
        if (carPhysics != null) carPhysics.enabled = 用G923;
        if (playerInput != null) playerInput.enabled = 用G923;

        if (切換時停車) 停下車輛(車);

        if (除錯訊息)
            Debug.Log($"[操作方式] 目前為 {(用G923 ? "G923" : "鍵盤(PC)")}　" +
                      $"CarPhysicsController={(carPhysics != null ? (用G923 ? "啟用" : "停用") : "找不到")}　" +
                      $"PlayerInput={(playerInput != null ? (用G923 ? "啟用" : "停用") : "找不到")}　" +
                      $"鍵盤控制={(鍵盤控制 != null ? (用G923 ? "停用" : "啟用") : "找不到")}");
    }

    /// <summary>目前模式是否有車輛控制腳本會接手</summary>
    private bool 有控制腳本接手()
    {
        if (GameSettings.是G923模式) return true;      // G923 由 CarPhysicsController 接手

        // 鍵盤模式：手動指定或自動找到的控制腳本，有一個就算
        if (鍵盤模式啟用 != null)
            foreach (var b in 鍵盤模式啟用) if (b != null) return true;

        if (自動尋找鍵盤控制 && FindObjectOfType<KeyboardCarController>(true) != null)
            return true;

        return false;
    }

    /// <summary>先用名稱找，找不到再用型別搜尋當備援</summary>
    private GameObject 尋找車輛()
    {
        var go = GameObject.Find(車輛物件名稱);
        if (go != null) return go;

        var cpc = FindObjectOfType<CarPhysicsController>(true);
        return cpc != null ? cpc.gameObject : null;
    }

    /// <summary>
    /// 把車速歸零。切換操作方式的瞬間，控制腳本可能剛被停用，
    /// 此時若還有殘餘速度，車子會失控滑行，所以直接清掉速度與角速度。
    /// </summary>
    private void 停下車輛(GameObject 車)
    {
        if (車 == null) return;

        var rb = 車.GetComponent<Rigidbody>() ?? 車.GetComponentInParent<Rigidbody>();
        if (rb == null) return;

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 輪子也要清，否則 WheelCollider 會把殘餘轉速再傳回車身
        foreach (var w in 車.GetComponentsInChildren<WheelCollider>(true))
        {
            w.motorTorque = 0f;
            w.brakeTorque = 駐車煞車力;
        }

        // ⚠ 只有「等一下會有控制腳本接手」時才解除煞車。
        //   若目前模式沒有任何車輛控制腳本在跑，就沒人會再設定 brakeTorque，
        //   輪子會完全自由轉動，車子在斜坡上會自己溜走。
        if (有控制腳本接手()) StartCoroutine(解除煞車(車));
        else if (除錯訊息)
            Debug.LogWarning("[操作方式] 目前模式沒有可用的車輛控制腳本，" +
                             "已保持駐車煞車避免車輛自行滑動。");
    }

    /// <summary>下一影格解除強制煞車，讓玩家能重新起步</summary>
    private System.Collections.IEnumerator 解除煞車(GameObject 車)
    {
        yield return null;
        if (車 == null) yield break;

        foreach (var w in 車.GetComponentsInChildren<WheelCollider>(true))
            w.brakeTorque = 0f;
    }
}