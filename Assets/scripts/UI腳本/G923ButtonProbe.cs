using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;   // onAnyButtonPress 的 Call() 擴充方法在這裡

/// <summary>
/// G923 按鍵偵測器 — 一次把方向盤上每顆按鍵的「實際路徑」印到 Console。
///
/// 用途：要把方向盤按鍵接到選單前，得先知道每顆實體按鍵在 Input System 裡叫什麼。
/// 用法：掛在場景任一物件上 → Play → 方向盤上每顆鍵、方向帽各方向都按一次
///       → 看 Console 印出的路徑 → 對照後填回 G923Controls.inputactions。
///
/// ⚠ 這是除錯工具，測完請把元件停用或刪除。
/// </summary>
public class G923ButtonProbe : MonoBehaviour
{
    [Header("偵測設定")]
    [Tooltip("同時印出搖桿/踏板類的軸向變化（會比較吵，預設關閉）")]
    public bool 也偵測軸向 = false;

    [Tooltip("軸向要變化超過多少才印（避免踏板雜訊洗版）")]
    [Range(0.05f, 0.9f)] public float 軸向門檻 = 0.5f;

    private IDisposable 訂閱;

    private void OnEnable()
    {
        Debug.LogWarning("[G923偵測] 已啟動。請依序按下方向盤上每一顆按鍵、撥動方向帽四個方向。");
        Debug.LogWarning("[G923偵測] 每按一次會印出一行，把整段 Console 內容複製回報即可。");

        訂閱 = InputSystem.onAnyButtonPress.Call(ctrl =>
        {
            Debug.Log($"[G923偵測] 按下 → 路徑: {ctrl.path}\n" +
                      $"              名稱: {ctrl.name}   裝置: {ctrl.device.displayName}");
        });
    }

    private void OnDisable()
    {
        訂閱?.Dispose();
        訂閱 = null;
    }

    private void Update()
    {
        if (!也偵測軸向) return;

        foreach (var device in InputSystem.devices)
        {
            // 只看方向盤類裝置，避免鍵盤滑鼠洗版
            if (!device.displayName.ToLower().Contains("g923") &&
                !device.displayName.ToLower().Contains("logitech")) continue;

            foreach (var c in device.allControls)
            {
                if (c is UnityEngine.InputSystem.Controls.AxisControl axis)
                {
                    float v = axis.ReadValue();
                    if (Mathf.Abs(v) >= 軸向門檻)
                        Debug.Log($"[G923偵測] 軸向 → {c.path} = {v:F2}");
                }
            }
        }
    }
}
