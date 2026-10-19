using UnityEngine;
using UnityEngine.InputSystem;

public class CarGearController : MonoBehaviour
{
    public enum AutomaticGear { D, P, R }

    [Header("目前自排檔位")]
    public AutomaticGear currentGear = AutomaticGear.P;

    void Update()
    {
        // ── 鍵盤備用輸入（測試用，有 G923 時不影響）──────────
        if (Input.GetKeyDown(KeyCode.Alpha1)) SetGear(AutomaticGear.P, "鍵盤");
        if (Input.GetKeyDown(KeyCode.Alpha2)) SetGear(AutomaticGear.D, "鍵盤");
        if (Input.GetKeyDown(KeyCode.Alpha3)) SetGear(AutomaticGear.R, "鍵盤");
    }

    #region 接收 G923 排檔輸入訊號 (由 Player Input 元件呼叫)

    public void Ondd(InputAction.CallbackContext context)
    {
        // ⚠️ 只認 started（推入的那一瞬間），不要用 performed。
        //    performed 對「維持按下狀態」的裝置會每幀重複觸發 —— H 型排檔桿或
        //    保持狀態的按鍵停在某個位置時，檔位會被鎖死在那一檔，
        //    鍵盤或其他來源切過去下一幀就被推回來（症狀：永遠切不到 P）。
        if (context.started) SetGear(AutomaticGear.D, "G923");
    }

    public void Onrr(InputAction.CallbackContext context)
    {
        if (context.started) SetGear(AutomaticGear.R, "G923");   // 同上：只認推入瞬間
    }

    public void Onpp(InputAction.CallbackContext context)
    {
        if (context.started) SetGear(AutomaticGear.P, "G923");   // 同上：只認推入瞬間
    }

    #endregion

    // performed 會連續觸發，這裡只在檔位真的改變時才印 Log，避免洗版
    void SetGear(AutomaticGear gear, string source)
    {
        if (currentGear == gear) return;
        currentGear = gear;
        Debug.Log($"[檔位] {source} 切換到 {gear} 檔");
    }
}
