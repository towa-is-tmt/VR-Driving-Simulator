using UnityEngine;

/// <summary>
/// 遊戲設定 — 跨場景保存的設定值，並用 PlayerPrefs 記住玩家的選擇。
///
/// 用 static 不掛物件，換場景不會被銷毀（和 TrainingSession 同樣做法）。
/// 數值以 0~100 儲存，和 UI 拉條顯示一致；實際套用時再換算成 0~1。
/// </summary>
public static class GameSettings
{
    public enum 設定項目 { 無, 主音量, 語音導航, 引擎音效 }

    /// <summary>操作方式。數值＝選項按鈕的索引，順序必須是 [0] G923、[1] 鍵盤</summary>
    public enum 操作方式類型 { G923 = 0, 鍵盤 = 1 }

    /// <summary>遊戲螢幕。數值＝選項按鈕的索引，順序必須是 [0] PC版、[1] VR版</summary>
    public enum 遊戲螢幕類型 { PC = 0, VR = 1 }

    /// <summary>二選一的設定群組</summary>
    public enum 選項群組 { 無, 操作方式, 遊戲螢幕 }

    // ── 預設值 ──
    public const float 主音量預設   = 80f;
    public const float 語音導航預設 = 75f;
    public const float 引擎音效預設 = 70f;

    private static float _主音量   = 主音量預設;
    private static float _語音導航 = 語音導航預設;
    private static float _引擎音效 = 引擎音效預設;

    public const 操作方式類型 操作方式預設 = 操作方式類型.G923;
    public const 遊戲螢幕類型 遊戲螢幕預設 = 遊戲螢幕類型.PC;

    private static 操作方式類型 _操作方式 = 操作方式預設;
    private static 遊戲螢幕類型 _遊戲螢幕 = 遊戲螢幕預設;

    private static bool 已載入 = false;

    /// <summary>任何設定變更時發出，套用端訂閱此事件即可即時反應</summary>
    public static event System.Action 設定變更;

    // ── 存取 ──────────────────────────────────────────────
    public static float 主音量
    {
        get { 確保已載入(); return _主音量; }
        set { 確保已載入(); _主音量 = Mathf.Clamp(value, 0f, 100f); 儲存並通知(); }
    }

    public static float 語音導航
    {
        get { 確保已載入(); return _語音導航; }
        set { 確保已載入(); _語音導航 = Mathf.Clamp(value, 0f, 100f); 儲存並通知(); }
    }

    public static float 引擎音效
    {
        get { 確保已載入(); return _引擎音效; }
        set { 確保已載入(); _引擎音效 = Mathf.Clamp(value, 0f, 100f); 儲存並通知(); }
    }

    public static 操作方式類型 操作方式
    {
        get { 確保已載入(); return _操作方式; }
        set { 確保已載入(); _操作方式 = value; 儲存並通知(); }
    }

    public static 遊戲螢幕類型 遊戲螢幕
    {
        get { 確保已載入(); return _遊戲螢幕; }
        set { 確保已載入(); _遊戲螢幕 = value; 儲存並通知(); }
    }

    /// <summary>目前是否為 G923 操作（給 car 場景切換元件用）</summary>
    public static bool 是G923模式 => 操作方式 == 操作方式類型.G923;

    /// <summary>依群組設定選中的索引（給 OptionToggleGroup 呼叫）</summary>
    public static void 設群組(選項群組 群組, int 索引)
    {
        switch (群組)
        {
            case 選項群組.操作方式: 操作方式 = (操作方式類型)Mathf.Clamp(索引,0,1); break;
            case 選項群組.遊戲螢幕: 遊戲螢幕 = (遊戲螢幕類型)Mathf.Clamp(索引,0,1); break;
        }
    }

    /// <summary>依群組取得目前索引</summary>
    public static int 取群組(選項群組 群組) => 群組 switch
    {
        選項群組.操作方式 => (int)操作方式,
        選項群組.遊戲螢幕 => (int)遊戲螢幕,
        _ => 0,
    };

    /// <summary>依項目取值（給拉條共用）</summary>
    public static float 取值(設定項目 項目) => 項目 switch
    {
        設定項目.主音量   => 主音量,
        設定項目.語音導航 => 語音導航,
        設定項目.引擎音效 => 引擎音效,
        _                 => 0f,
    };

    /// <summary>依項目設值（給拉條共用）</summary>
    public static void 設值(設定項目 項目, float v)
    {
        switch (項目)
        {
            case 設定項目.主音量:   主音量   = v; break;
            case 設定項目.語音導航: 語音導航 = v; break;
            case 設定項目.引擎音效: 引擎音效 = v; break;
        }
    }

    // ── 換算成 0~1，套用時用 ──────────────────────────────
    public static float 主音量比例   => 主音量   / 100f;
    public static float 語音導航比例 => 語音導航 / 100f;
    public static float 引擎音效比例 => 引擎音效 / 100f;

    // ── 存檔 ──────────────────────────────────────────────
    private const string K主 = "設定_主音量";
    private const string K語 = "設定_語音導航";
    private const string K引 = "設定_引擎音效";
    private const string K操 = "設定_操作方式";
    private const string K螢 = "設定_遊戲螢幕";

    private static void 確保已載入()
    {
        if (已載入) return;
        已載入 = true;                                   // 先設旗標，避免遞迴
        _主音量   = PlayerPrefs.GetFloat(K主, 主音量預設);
        _語音導航 = PlayerPrefs.GetFloat(K語, 語音導航預設);
        _引擎音效 = PlayerPrefs.GetFloat(K引, 引擎音效預設);
        _操作方式 = (操作方式類型)PlayerPrefs.GetInt(K操, (int)操作方式預設);
        _遊戲螢幕 = (遊戲螢幕類型)PlayerPrefs.GetInt(K螢, (int)遊戲螢幕預設);
    }

    private static void 儲存並通知()
    {
        PlayerPrefs.SetFloat(K主, _主音量);
        PlayerPrefs.SetFloat(K語, _語音導航);
        PlayerPrefs.SetFloat(K引, _引擎音效);
        PlayerPrefs.SetInt(K操, (int)_操作方式);
        PlayerPrefs.SetInt(K螢, (int)_遊戲螢幕);
        PlayerPrefs.Save();
        設定變更?.Invoke();
    }

    /// <summary>還原成預設值</summary>
    public static void 重設()
    {
        確保已載入();
        _主音量 = 主音量預設; _語音導航 = 語音導航預設; _引擎音效 = 引擎音效預設;
        _操作方式 = 操作方式預設; _遊戲螢幕 = 遊戲螢幕預設;
        儲存並通知();
    }
}
