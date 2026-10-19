using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 車損紀錄 — 存放本次駕駛期間車輛受到的所有損傷。
///
/// 用 static 不掛物件，換場景也不會被銷毀，和 TrainingSession 同樣的做法。
/// 目前只負責「記錄」，不影響計分；之後若要扣分，直接讀這裡的資料即可。
/// </summary>
public static class DamageLog
{
    /// <summary>損傷程度</summary>
    public enum 程度 { 輕微, 中度, 嚴重 }

    /// <summary>
    /// 車身八個偵測點（俯視圖，依順時針排列，索引與 UI 上的八個點一一對應）
    ///   0 左前角  1 車頭中  2 右前角  3 右側中
    ///   4 右後角  5 車尾中  6 左後角  7 左側中
    /// </summary>
    public enum 部位
    {
        左前角 = 0,
        車頭中 = 1,
        右前角 = 2,
        右側中 = 3,
        右後角 = 4,
        車尾中 = 5,
        左後角 = 6,
        左側中 = 7,
    }

    /// <summary>八個點的總數，UI 端建立圖示陣列時可用</summary>
    public const int 部位數量 = 8;

    /// <summary>單筆損傷紀錄</summary>
    public class 紀錄
    {
        public float 發生秒數;      // 從本次駕駛開始起算
        public 部位 撞擊部位;
        public 程度 損傷程度;
        public float 撞擊力;        // 物理衝量大小，供之後計分用
        public string 撞到的物件;
        public Vector3 世界座標;

        public string 時間字串
        {
            get
            {
                int m = Mathf.FloorToInt(發生秒數 / 60f);
                int s = Mathf.FloorToInt(發生秒數 % 60f);
                return $"{m:00}:{s:00}";
            }
        }
    }

    public static readonly List<紀錄> 全部紀錄 = new List<紀錄>();

    /// <summary>
    /// 每記錄一筆損傷就會發出。行駛中的即時提示（DamageToast）靠這個接收。
    /// ⚠ 訂閱者務必在 OnDisable 取消訂閱，否則換場景會留下失效的參考。
    /// </summary>
    public static event System.Action<紀錄> 發生損傷;

    /// <summary>本次駕駛的開始時間，用來算「發生秒數」</summary>
    private static float 起始時間 = 0f;

    // ── 統計 ──────────────────────────────────────────────
    public static int 總次數 => 全部紀錄.Count;
    public static bool 有損傷 => 全部紀錄.Count > 0;

    public static int 計數(程度 p)
    {
        int n = 0;
        foreach (var r in 全部紀錄) if (r.損傷程度 == p) n++;
        return n;
    }

    /// <summary>累計撞擊力，之後要換算扣分可以用這個</summary>
    public static float 總撞擊力
    {
        get
        {
            float t = 0f;
            foreach (var r in 全部紀錄) t += r.撞擊力;
            return t;
        }
    }

    // ── 操作 ──────────────────────────────────────────────
    /// <summary>開始新的一次駕駛（清空舊紀錄並重設計時）</summary>
    public static void 開始新紀錄()
    {
        全部紀錄.Clear();
        起始時間 = Time.time;
        Debug.Log("[車損] 已開始新的一次紀錄");
    }

    public static void 新增(部位 部, 程度 度, float 力, string 物件名, Vector3 座標)
    {
        var 新紀錄 = new 紀錄
        {
            發生秒數 = Mathf.Max(0f, Time.time - 起始時間),
            撞擊部位 = 部,
            損傷程度 = 度,
            撞擊力   = 力,
            撞到的物件 = 物件名,
            世界座標 = 座標,
        };

        全部紀錄.Add(新紀錄);
        發生損傷?.Invoke(新紀錄);   // 通知行駛中的即時提示
    }

    // ── 依部位查詢（俯視圖上色用）────────────────────────
    /// <summary>該部位被撞幾次</summary>
    public static int 部位次數(部位 p)
    {
        int n = 0;
        foreach (var r in 全部紀錄) if (r.撞擊部位 == p) n++;
        return n;
    }

    /// <summary>該部位最嚴重的損傷程度；沒被撞過回傳 false</summary>
    public static bool 部位最嚴重(部位 p, out 程度 結果)
    {
        結果 = 程度.輕微;
        bool 有 = false;
        foreach (var r in 全部紀錄)
        {
            if (r.撞擊部位 != p) continue;
            if (!有 || r.損傷程度 > 結果) { 結果 = r.損傷程度; 有 = true; }
        }
        return 有;
    }

    public static void 清除() => 全部紀錄.Clear();
}
