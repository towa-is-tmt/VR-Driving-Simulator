using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 遊戲螢幕模式套用器 — 依設定在 PC 與 VR 之間切換 Canvas 的呈現方式。
///
///   PC  → Screen Space - Overlay，貼在平面螢幕上（原本的樣子）
///   VR  → World Space，變成漂浮在 3D 空間中的實體面板
///
/// VR 模式除了改 renderMode，還必須處理三件事，否則面板會跑到世界原點、
/// 大小離譜、而且點不到：
///   ① 縮放：World Space 的 1 單位 = 1 公尺，1920 寬的 Canvas 不縮小會有 1920 公尺
///   ② 位置與朝向：要擺到玩家前方（通常掛在相機或車內儀表板下）
///   ③ 事件相機：worldCamera 沒設的話射線點不到 UI
///
/// 切回 PC 時會還原成原本的父物件、位置與縮放。
/// </summary>
public class ScreenModeApplier : MonoBehaviour
{
    [Header("要切換的 Canvas")]
    [Tooltip("勾選＝自動抓場景中所有 Canvas；取消＝只處理下面手動指定的")]
    public bool 自動尋找所有Canvas = true;

    [Tooltip("手動指定要切換的 Canvas（自動尋找關閉時才會讀這裡）")]
    public Canvas[] 指定Canvas;

    [Header("VR 模式設定")]
    [Tooltip("VR 時 Canvas 的縮放。World Space 下 1 單位 = 1 公尺，\n" +
             "面板實際寬度 ≒ Canvas 寬度 × 此值（例如 1920 × 0.05 = 96 公尺，通常太大，可再調小）")]
    public float VR縮放 = 0.05f;

    [Tooltip("VR 時把 Canvas 掛到這個物件底下（例如車內儀表板或 XR 相機）。留空則維持原本父物件")]
    public Transform VR時的父物件;

    [Tooltip("相對父物件往前擺多遠（公尺）")]
    public float VR前方距離 = 1.5f;

    [Tooltip("VR 時負責 UI 射線的相機。留空會自動用 Camera.main")]
    public Camera VR事件相機;

    [Header("除錯")]
    public bool 除錯訊息 = true;

    // 記住原始狀態，切回 PC 時還原
    private class 原始狀態
    {
        public Transform 父物件;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public RenderMode renderMode;
        public Camera worldCamera;
    }

    private readonly Dictionary<Canvas, 原始狀態> 原始 = new Dictionary<Canvas, 原始狀態>();

    private void OnEnable()
    {
        GameSettings.設定變更 += 套用;
        套用();
    }

    private void OnDisable()
    {
        GameSettings.設定變更 -= 套用;
    }

    /// <summary>依目前設定切換所有 Canvas，也可手動呼叫</summary>
    public void 套用()
    {
        bool 是VR = (GameSettings.遊戲螢幕 == GameSettings.遊戲螢幕類型.VR);

        foreach (var c in 取得Canvas清單())
        {
            if (c == null) continue;
            記住原始(c);

            if (是VR) 切成WorldSpace(c);
            else      還原成Overlay(c);
        }

        if (除錯訊息)
            Debug.Log($"[遊戲螢幕] 已切換為 {(是VR ? "VR（World Space）" : "PC（Overlay）")}");
    }

    private IEnumerable<Canvas> 取得Canvas清單()
    {
        if (自動尋找所有Canvas)
            return FindObjectsOfType<Canvas>(true);

        return 指定Canvas ?? new Canvas[0];
    }

    private void 記住原始(Canvas c)
    {
        if (原始.ContainsKey(c)) return;

        var t = c.transform;
        原始[c] = new 原始狀態
        {
            父物件        = t.parent,
            localPosition = t.localPosition,
            localRotation = t.localRotation,
            localScale    = t.localScale,
            renderMode    = c.renderMode,
            worldCamera   = c.worldCamera,
        };
    }

    private void 切成WorldSpace(Canvas c)
    {
        c.renderMode  = RenderMode.WorldSpace;
        c.worldCamera = VR事件相機 != null ? VR事件相機 : Camera.main;

        var t = c.transform;

        if (VR時的父物件 != null)
        {
            t.SetParent(VR時的父物件, false);
            t.localPosition = new Vector3(0f, 0f, VR前方距離);
            t.localRotation = Quaternion.identity;
        }

        t.localScale = Vector3.one * VR縮放;
    }

    private void 還原成Overlay(Canvas c)
    {
        if (!原始.TryGetValue(c, out var o)) return;

        var t = c.transform;
        t.SetParent(o.父物件, false);
        t.localPosition = o.localPosition;
        t.localRotation = o.localRotation;
        t.localScale    = o.localScale;

        c.renderMode  = o.renderMode;
        c.worldCamera = o.worldCamera;
    }
}
