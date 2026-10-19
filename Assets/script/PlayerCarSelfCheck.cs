using UnityEngine;

/// <summary>
/// 🩺 玩家車自我診斷
///
/// 掛在「有 Rigidbody 和 Collider 的那個物件」上,也就是 vehicle   ONLY LOD0。
///
/// 會回報三件事:
///   1. 啟動時檢查 Tag / Collider / Rigidbody / DriveCar / IPlayerVehicleState 是否正確,
///      並列出場景中所有 Tag=Player 的物件(抓出重複的假玩家)。
///   2. 每秒回報一次車速與按鍵狀態 —— 用來確認車到底有沒有在動。
///   3. 進入任何 Trigger 區域時印出對方名稱 —— 用來確認有沒有真的碰到觸發區。
///
/// 診斷完就可以把這個元件移除。
/// </summary>
public class PlayerCarSelfCheck : MonoBehaviour
{
    [Tooltip("每隔幾秒回報一次車速狀態")]
    public float reportInterval = 1f;

    private Rigidbody rb;
    private float nextReportTime;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();

        Debug.Log(
            $"<color=yellow>===== 玩家車自我診斷 =====</color>\n" +
            $"  掛載物件      : {name}\n" +
            $"  Tag           : {tag}   {(CompareTag("Player") ? "✓" : "★ 不是 Player!")}\n" +
            $"  Rigidbody     : {(rb != null ? $"有 (mass={rb.mass}, isKinematic={rb.isKinematic})" : "★ 沒有")}\n" +
            $"  Collider 數量 : {GetComponents<Collider>().Length}\n" +
            $"  DriveCar      : {DescribeDriveCar()}\n" +
            $"  介面 IPlayerVehicleState : {(GetComponent<IPlayerVehicleState>() != null ? "有 ✓" : "★ 沒有")}");

        // 列出場景中所有帶 Player 標籤的物件,揪出重複
        GameObject[] tagged = GameObject.FindGameObjectsWithTag("Player");
        string list = "";
        foreach (GameObject go in tagged)
            list += $"\n     - {HierarchyPath(go.transform)}" +
                    $"   (Rigidbody={(go.GetComponent<Rigidbody>() != null ? "有" : "無")}," +
                    $" 介面={(go.GetComponent<IPlayerVehicleState>() != null ? "有" : "無")})";

        if (tagged.Length == 1)
            Debug.Log($"<color=lime>  場景中 Tag=Player 的物件只有 1 個 ✓</color>{list}");
        else
            Debug.LogError(
                $"★★ 場景中有 {tagged.Length} 個 Tag=Player 的物件!{list}\n" +
                $"   FindGameObjectWithTag 會回傳其中「任意」一個,AI 很可能抓錯對象。\n" +
                $"   請把多餘的改成 Untagged,只留有 Rigidbody + DriveCar 的那個。");
    }

    private string DescribeDriveCar()
    {
        DriveCar dc = GetComponent<DriveCar>();
        if (dc == null) return "★ 這個物件上沒有 DriveCar";
        return dc.enabled ? "有,已啟用 ✓" : "★ 有,但元件被停用(勾勾沒打開)";
    }

    private void Update()
    {
        if (Time.time < nextReportTime) return;
        nextReportTime = Time.time + reportInterval;

        float speed = rb != null ? rb.velocity.magnitude : -1f;
        DriveCar dc = GetComponent<DriveCar>();
        string reversing = dc != null ? dc.IsReversing.ToString() : "(無 DriveCar)";

        // 🔧 輪胎狀態 —— 判斷「有沒有踩到地」「馬達力矩有沒有進去」「煞車是不是還踩著」
        WheelCollider[] wheels = GetComponentsInChildren<WheelCollider>();
        string wheelInfo = "";
        int grounded = 0;
        foreach (WheelCollider w in wheels)
        {
            if (w.isGrounded) grounded++;
            wheelInfo += $"\n     {w.name,-24} 著地={(w.isGrounded ? "是" : "★否")}" +
                         $"  馬達={w.motorTorque,7:F0}  煞車={w.brakeTorque,6:F0}  RPM={w.rpm,7:F1}";
        }

        string verdict = "";
        if (grounded == 0) verdict = "\n   ★ 四輪全部沒著地 → 車身可能卡在別的碰撞體上,輪胎懸空當然推不動";
        else if (grounded < wheels.Length) verdict = $"\n   ★ 只有 {grounded}/{wheels.Length} 個輪子著地";

        Debug.Log(
            $"<color=cyan>[車況]</color> 速度={speed:F2} m/s ({speed * 3.6f:F0} km/h) | " +
            $"W={Input.GetKey(KeyCode.W)} S={Input.GetKey(KeyCode.S)} | " +
            $"IsReversing={reversing}\n" +
            $"   位置={transform.position}  剛體constraints={(rb != null ? rb.constraints.ToString() : "?")}" +
            wheelInfo + verdict);
    }

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"<color=lime>[進入觸發區]</color> {name} → {HierarchyPath(other.transform)}" +
                  $"   (對方 Tag={other.tag})");
    }

    private static string HierarchyPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + " / " + path;
        }
        return path;
    }
}
