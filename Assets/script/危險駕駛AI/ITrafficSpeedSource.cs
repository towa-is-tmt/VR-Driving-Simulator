using UnityEngine;

/// <summary>
/// 🚗 「我也是一台車」的共用介面。
///
/// 隊友的白車（ReverseThreatAICar）與灰車（CarNavigation）的雷達、跟車走廊、移動閘門，
/// 原本只認得「玩家、白車、灰車」三種東西，其他任何物件一律當成號誌桿忽略 ——
/// 危險駕駛 AI 的演員車（鬼切／路霸）因此在它們眼中是隱形的，會直接穿過去。
///
/// 任何不是白車／灰車的車輛，只要實作這個介面，就會被當成一般前車處理（跟車、減速、閘門擋住）。
/// </summary>
public interface ITrafficSpeedSource
{
    /// <summary>目前車速（公尺/秒）</summary>
    float CurrentSpeed { get; }

    /// <summary>車輛本體（用來判斷車頭方向，例如是不是對向車）</summary>
    Transform VehicleTransform { get; }
}

/// <summary>給白車／灰車腳本用的查詢工具，一行就能判斷碰到的是不是外部車輛</summary>
public static class ExternalTrafficVehicle
{
    public static ITrafficSpeedSource Get(Collider c)
    {
        return c == null ? null : c.GetComponentInParent<ITrafficSpeedSource>();
    }

    public static bool Is(Collider c)
    {
        return Get(c) != null;
    }

    public static float SpeedOf(Collider c, float fallback)
    {
        ITrafficSpeedSource s = Get(c);
        return s != null ? s.CurrentSpeed : fallback;
    }

    public static Transform TransformOf(Collider c)
    {
        ITrafficSpeedSource s = Get(c);
        return s != null ? s.VehicleTransform : null;
    }
}
