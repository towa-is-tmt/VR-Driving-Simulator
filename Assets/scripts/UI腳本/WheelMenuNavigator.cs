using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 選單方向盤導航 — 用 G923 方向盤上的按鍵操作選單，不需要滑鼠。
///
/// 對應動作（定義於 G923Controls.inputactions）：
///   butup / butdown / butleft / butright → 移動高亮
///   confirm                              → 按下（Button）或勾選（Toggle）
///   esc                                  → 執行「返回」
///
/// 支援 Button 與 Toggle 兩種：訓練項目的卡片是 Toggle，一般按鈕是 Button，
/// 兩者都繼承自 Selectable，所以統一用 Selectable 收集。
///
/// 掛法：掛在每一個選單面板上（模式選擇、項目選擇、操作介紹…）。
/// 注意：請把面板內所有按鈕的 Button/Toggle → Navigation 設為 None，
///       否則 EventSystem 內建導航會和本腳本互搶，高亮會亂跳。
/// </summary>
public class WheelMenuNavigator : MonoBehaviour
{
    [Header("輸入設定")]
    [Tooltip("拖入 G923Controls.inputactions 資產")]
    public InputActionAsset 輸入設定;

    [Tooltip("動作所在的 Action Map 名稱")]
    public string 動作對應表名稱 = "car";

    [Header("可選取項目（高亮移動順序）")]
    [Tooltip("勾選＝自動抓取本面板底下所有 Button 與 Toggle，依階層順序排列")]
    public bool 自動尋找按鈕 = true;

    [Tooltip("手動指定順序時使用（Button 或 Toggle 皆可）；自動尋找關閉時才會讀這裡")]
    public Selectable[] 按鈕清單;

    [Header("移動方向")]
    public bool 使用上下鍵 = true;
    public bool 使用左右鍵 = true;
    [Tooltip("移到最後一個再往下時，回到第一個")]
    public bool 循環選取 = true;

    [Header("返回")]
    [Tooltip("按下 esc（方向盤的跳出畫面鍵）時要觸發的按鈕，可留空")]
    public Button 返回按鈕;

    [Header("其他")]
    [Tooltip("選取被清空時（例如點到空白處）自動重新選回上一個項目")]
    public bool 自動維持選取 = true;

    [Tooltip("執行時自動把項目的 Navigation 設為 None，避免 EventSystem 內建導航和本腳本互搶（建議保持勾選，就不必手動一顆顆改）")]
    public bool 自動關閉內建導航 = true;

    [Header("除錯")]
    [Tooltip("勾選後印出裝置、綁定狀況與每次觸發，用來排查方向盤沒反應的問題")]
    public bool 除錯訊息 = false;

    // ── 內部 ──
    // 所有啟用中的導航器；只有「最後啟用的那一個」會處理輸入，
    // 這樣操作介紹疊在主選單上時，不會兩個面板同時反應。
    private static readonly List<WheelMenuNavigator> 作用中清單 = new List<WheelMenuNavigator>();
    private static WheelMenuNavigator 最上層 =>
        作用中清單.Count > 0 ? 作用中清單[作用中清單.Count - 1] : null;

    private InputAction 上, 下, 左, 右, 確認, 離開;
    private readonly List<(InputAction 動作, System.Action<InputAction.CallbackContext> 回呼)> 已掛回呼
        = new List<(InputAction, System.Action<InputAction.CallbackContext>)>();

    private readonly List<Selectable> 目前項目 = new List<Selectable>();
    private int 目前索引 = 0;

    // ── 生命週期 ─────────────────────────────────────────
    private void OnEnable()
    {
        建立清單();
        綁定輸入();

        // ⚠ 只有真的取得輸入來源的面板才進入作用中清單。
        //   否則一個沒指定 輸入設定 的面板疊上來，會搶走控制權卻又收不到輸入，
        //   結果是整個選單都不能操作。
        if (輸入設定 != null)
        {
            作用中清單.Remove(this);
            作用中清單.Add(this);      // 最後啟用的排最上層
        }
        else
        {
            Debug.LogWarning($"[方向盤導航] 「{name}」沒有指定 輸入設定（G923Controls），" +
                             $"此面板不會接管方向盤操作。請在 Inspector 拖入 inputactions 資產。");
        }
        // 延後一影格再選取：確保 EventSystem 與其他 Start() 都已就緒
        StartCoroutine(延遲選取());
    }

    private void OnDisable()
    {
        作用中清單.Remove(this);
        解除輸入();
    }

    private IEnumerator 延遲選取()
    {
        yield return null;              // 等一影格
        建立清單();                      // 此時其他腳本可能已改變按鈕的可用狀態
        選取(0);
    }

    private void Update()
    {
        if (!自動維持選取) return;
        if (最上層 != this) return;     // 非最上層面板不搶選取
        var es = EventSystem.current;
        if (es == null || 目前項目.Count == 0) return;

        // 選取被清掉（點到空白處）或跑到本面板以外，就選回來
        var 現在 = es.currentSelectedGameObject;
        if (現在 == null || !現在.transform.IsChildOf(transform))
            選取(目前索引);
    }

    // ── 清單 ─────────────────────────────────────────────
    private void 建立清單()
    {
        目前項目.Clear();

        IEnumerable<Selectable> 來源 = 自動尋找按鈕
            ? GetComponentsInChildren<Selectable>(false)
            : (IEnumerable<Selectable>)(按鈕清單 ?? new Selectable[0]);

        foreach (var s in 來源)
        {
            if (s == null) continue;
            if (!s.gameObject.activeInHierarchy) continue;
            if (!s.IsInteractable()) continue;      // 灰掉的自動跳過

            // 關掉 Unity 內建導航，避免 EventSystem 依畫面位置自行跳選，
            // 和本腳本的索引移動互相抵銷（症狀就是「按了不動」）。
            if (自動關閉內建導航 && s.navigation.mode != Navigation.Mode.None)
            {
                var nav = s.navigation;
                nav.mode = Navigation.Mode.None;
                s.navigation = nav;
            }

            目前項目.Add(s);
        }

        if (目前索引 >= 目前項目.Count) 目前索引 = 0;

        if (除錯訊息)
        {
            string list = "";
            foreach (var s in 目前項目) list += "\n     ↳ " + s.name + "  (" + s.GetType().Name + ")";
            Debug.Log($"[方向盤導航] {name} 收集到 {目前項目.Count} 個可選項目{list}");
        }
    }

    // ── 輸入 ─────────────────────────────────────────────
    private InputAction 取動作(string 名稱)
    {
        if (輸入設定 == null) return null;
        var map = 輸入設定.FindActionMap(動作對應表名稱, false);
        return map?.FindAction(名稱, false);
    }

    private void 綁定輸入()
    {
        if (輸入設定 == null)
        {
            Debug.LogWarning("[方向盤導航] 沒有指定 輸入設定（G923Controls），方向盤無法操作選單。");
            return;
        }

        上 = 取動作("butup");   下 = 取動作("butdown");
        左 = 取動作("butleft"); 右 = 取動作("butright");
        確認 = 取動作("confirm"); 離開 = 取動作("esc");

        if (除錯訊息) 印出診斷();

        if (使用上下鍵) { 掛(上, _ => 移動(-1)); 掛(下, _ => 移動(+1)); }
        if (使用左右鍵) { 掛(左, _ => 左右(-1)); 掛(右, _ => 左右(+1)); }
        掛(確認, _ => 按下目前項目());
        掛(離開, _ => 執行返回());
    }

    private void 印出診斷()
    {
        Debug.Log("[方向盤導航] ===== 已連接的裝置 =====");
        foreach (var d in InputSystem.devices)
            Debug.Log($"[方向盤導航]   {d.displayName}   layout={d.layout}");

        Debug.Log("[方向盤導航] ===== 動作綁定狀況 =====");
        foreach (var pair in new (string, InputAction)[]{
            ("butup",上),("butdown",下),("butleft",左),
            ("butright",右),("confirm",確認),("esc",離開)})
        {
            var a = pair.Item2;
            if (a == null)
            { Debug.LogWarning($"[方向盤導航] {pair.Item1} → ⚠ 資產裡找不到這個動作"); continue; }

            if (a.controls.Count == 0)
                Debug.LogWarning($"[方向盤導航] {pair.Item1} → ⚠ 動作存在，但沒對應到任何實體控制項");
            else
            {
                string list = "";
                foreach (var c in a.controls) list += "\n     ↳ " + c.path;
                Debug.Log($"[方向盤導航] {pair.Item1} → ✔ 綁到 {a.controls.Count} 個控制項{list}");
            }
        }
    }

    private void 掛(InputAction a, System.Action<InputAction.CallbackContext> f)
    {
        if (a == null) return;

        System.Action<InputAction.CallbackContext> 包裝 = ctx =>
        {
            if (!isActiveAndEnabled) return;        // 面板隱藏時不處理
            if (最上層 != this) return;             // 只有最上層面板可操作（避免疊層互搶）
            if (除錯訊息)
                Debug.Log($"[方向盤導航] {name} 觸發：{ctx.action.name}  來源：{ctx.control.path}");
            f.Invoke(ctx);
        };

        a.performed += 包裝.Invoke;
        已掛回呼.Add((a, 包裝));
        if (!a.enabled) a.Enable();
    }

    private void 解除輸入()
    {
        // ⚠ 不可呼叫 a.Disable()：InputActionAsset 的動作是所有面板共用的同一個實例，
        //   在這裡停用會讓其他正在顯示的面板也失效。只解除自己掛的回呼即可。
        foreach (var (動作, 回呼) in 已掛回呼)
            if (動作 != null) 動作.performed -= 回呼.Invoke;
        已掛回呼.Clear();
    }

    // ── 導航 ─────────────────────────────────────────────
    /// <summary>把內部索引對齊 EventSystem 目前選中的項目</summary>
    private void 同步索引()
    {
        var es = EventSystem.current;
        if (es == null) return;
        var 選中 = es.currentSelectedGameObject;
        if (選中 == null) return;

        for (int i = 0; i < 目前項目.Count; i++)
            if (目前項目[i] != null && 目前項目[i].gameObject == 選中) { 目前索引 = i; return; }
    }

    /// <summary>
    /// 左右鍵的行為：
    ///   選中的是設定拉條 → 調整數值（每次依拉條設定的方向盤步進，預設 5）
    ///   其他情況        → 照原本的方式移動高亮
    /// 這樣上下鍵換項目、左右鍵調數值，操作直覺且不互相干擾。
    /// </summary>
    private void 左右(int 方向)
    {
        var es = EventSystem.current;
        if (es != null && es.currentSelectedGameObject != null)
        {
            var 拉條 = es.currentSelectedGameObject.GetComponent<SettingsSlider>();
            if (拉條 != null)
            {
                if (方向 > 0) 拉條.增加(); else 拉條.減少();
                return;
            }
        }

        移動(方向);
    }

    private void 移動(int 方向)
    {
        建立清單();                 // 卡片可能被 ButtonGate 改變可用狀態，每次重建最保險
        if (目前項目.Count == 0) return;

        同步索引();

        int 新索引 = 目前索引 + 方向;
        if (循環選取)
        {
            if (新索引 < 0) 新索引 = 目前項目.Count - 1;
            if (新索引 >= 目前項目.Count) 新索引 = 0;
        }
        else 新索引 = Mathf.Clamp(新索引, 0, 目前項目.Count - 1);

        選取(新索引);
    }

    private void 選取(int 索引)
    {
        if (目前項目.Count == 0) return;
        目前索引 = Mathf.Clamp(索引, 0, 目前項目.Count - 1);

        var go = 目前項目[目前索引].gameObject;

        // 透過 EventSystem 選取：ButtonHoverScale 會收到 OnSelect，
        // 自動放大並播放滑過音效，不需另外處理。
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(go);
    }

    private void 按下目前項目()
    {
        if (目前項目.Count == 0) return;
        同步索引();

        var s = 目前項目[目前索引];
        if (s == null || !s.IsInteractable()) return;

        // 只送一次 Submit 就好：
        //   Button 的 OnSubmit 內部會呼叫 onClick，Toggle 的 OnSubmit 會切換 isOn，
        //   ButtonHoverScale 的 OnSubmit 會播點擊音效。
        //   若額外再呼叫 onClick.Invoke() 會導致 Button 被觸發兩次。
        ExecuteEvents.Execute(s.gameObject,
                              new BaseEventData(EventSystem.current),
                              ExecuteEvents.submitHandler);
    }

    private void 執行返回()
    {
        if (返回按鈕 == null || !返回按鈕.IsInteractable()) return;
        返回按鈕.onClick.Invoke();
    }
}
