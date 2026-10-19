using System;
using UnityEngine;

/// <summary>
/// IDM（智慧駕駛模型）參數。
/// 一組參數 = 一種駕駛性格。一般駕駛與路霸用的是同一條公式，差別只在數字。
/// </summary>
[Serializable]
public class IDMParams
{
    [Tooltip("期望車頭時距 T（秒）。一般駕駛 1.2~1.5；路霸貼尾 0.3~0.6")]
    public float timeHeadway = 1.2f;

    [Tooltip("最大加速度 a（m/s²）。一般 1.5~2；路霸 3~4")]
    public float maxAccel = 1.5f;

    [Tooltip("舒適減速度 b（m/s²）。一般 2~3；路霸 4~5（習慣重踩煞車）")]
    public float comfortDecel = 3f;

    [Tooltip("停車最小間距 s0（公尺）。一般 2；路霸 1~1.5")]
    public float minGap = 2f;

    [Tooltip("加速度指數 δ，文獻標準值 4，通常不用改")]
    public float delta = 4f;

    public IDMParams Clone() { return (IDMParams)MemberwiseClone(); }

    /// <summary>一般駕駛（當作「玩家應該怎麼開」的參考模型）</summary>
    public static IDMParams NormalDriver()
    {
        return new IDMParams { timeHeadway = 1.2f, maxAccel = 1.5f, comfortDecel = 3f, minGap = 2f, delta = 4f };
    }

    /// <summary>路霸／鬼切駕駛</summary>
    public static IDMParams AggressiveDriver()
    {
        return new IDMParams { timeHeadway = 0.45f, maxAccel = 3.5f, comfortDecel = 4.5f, minGap = 1.2f, delta = 4f };
    }
}

/// <summary>
/// 🧮 交通流模型的純數學工具（不碰任何場景物件，可單獨測試）
///
///   ● IDM  (Treiber, Hennecke &amp; Helbing, 2000)：縱向，決定油門／煞車
///   ● MOBIL(Kesting, Treiber &amp; Helbing, 2007)：橫向，決定要不要換車道
///
/// 危險駕駛 AI 的做法是「同一套模型、調成激進參數」，而不是另外寫一堆 if：
///   - 路霸貼尾 = IDM 的車頭時距 T 調到 0.45 秒
///   - 鬼切     = MOBIL 的禮讓係數 p = 0（完全不管後車），
///                並把安全準則反過來用：刻意挑「後車（玩家）需要的減速度」剛好等於難度目標的瞬間切進來
/// </summary>
public static class DrivingModel
{
    /// <summary>
    /// IDM 加速度（m/s²，正值加速、負值煞車）。
    /// </summary>
    /// <param name="v">自己的速度（m/s）</param>
    /// <param name="v0">期望速度（m/s）</param>
    /// <param name="gap">與前車的淨間距（車尾到車頭，公尺）。沒有前車傳 float.PositiveInfinity</param>
    /// <param name="approachRate">接近速率 = 自己速度 − 前車速度（正值代表越來越近）</param>
    public static float IdmAccel(float v, float v0, float gap, float approachRate, IDMParams p)
    {
        v = Mathf.Max(0f, v);
        v0 = Mathf.Max(0.1f, v0);

        // 自由路段項：越接近期望速度，加速度越小
        float free = 1f - Mathf.Pow(v / v0, p.delta);

        if (float.IsInfinity(gap) || gap > 1000f)
            return p.maxAccel * free;

        gap = Mathf.Max(0.1f, gap);

        // 期望間距 s*：停車間距 + 時距 × 速度 + 接近時的動態補償
        float dynamicTerm = v * p.timeHeadway + v * approachRate / (2f * Mathf.Sqrt(p.maxAccel * p.comfortDecel));
        float sStar = p.minGap + Mathf.Max(0f, dynamicTerm);

        float ratio = sStar / gap;
        return p.maxAccel * (free - ratio * ratio);
    }

    /// <summary>
    /// 如果前面突然插進一台車，後車「需要」多大的減速度（m/s²，正值 = 要煞車）。
    /// 這就是 MOBIL 安全準則裡的 −a'(新後車)。
    /// 後車用自己目前速度當期望速度，只看「因為被插隊」造成的煞車需求。
    /// </summary>
    public static float ImposedDecel(float followerSpeed, float leaderSpeed, float gap, IDMParams followerModel)
    {
        float a = IdmAccel(followerSpeed, Mathf.Max(followerSpeed, 0.1f), gap, followerSpeed - leaderSpeed, followerModel);
        return -a;
    }

    /// <summary>
    /// MOBIL 誘因準則：換道後的好處是否大於門檻。
    /// politeness = 0 就是完全自私（路霸／鬼切）；一般駕駛約 0.2~0.5。
    /// </summary>
    public static bool MobilIncentive(float selfAccNew, float selfAccOld,
                                      float newFollowerAccNew, float newFollowerAccOld,
                                      float oldFollowerAccNew, float oldFollowerAccOld,
                                      float politeness, float threshold)
    {
        float gain = (selfAccNew - selfAccOld)
                   + politeness * ((newFollowerAccNew - newFollowerAccOld) + (oldFollowerAccNew - oldFollowerAccOld));
        return gain > threshold;
    }

    /// <summary>MOBIL 安全準則：新後車被迫的減速度不超過 bSafe</summary>
    public static bool MobilSafe(float newFollowerAccNew, float bSafe)
    {
        return newFollowerAccNew >= -bSafe;
    }

    /// <summary>
    /// 運動學版本的「後車需要多大減速度才不會撞」（m/s²）。
    /// 比 IDM 的 ImposedDecel 直觀：考慮後車的反應時間，也考慮前車「切入後還會煞車」的計畫。
    ///
    /// 前車：以 leaderSpeed 等速 → leaderBrakeDelay 秒後以 leaderDecel 減速 leaderBrakeDuration 秒（不低於 leaderFloor）→ 等速
    /// 後車：反應時間內維持原速 → 之後以固定減速度 a 煞車，速度降到跟前車一樣就跟著前車走
    /// 回傳「讓兩車間距始終 ≥ minGap」所需的最小 a。15 m/s² 都救不回來就回傳 99。
    /// </summary>
    public static float RequiredDecelToAvoid(float gap, float followerSpeed, float leaderSpeed,
                                             float leaderDecel, float leaderFloor, float leaderBrakeDelay, float leaderBrakeDuration,
                                             float reactionTime, float minGap)
    {
        if (!WouldCollide(gap, followerSpeed, leaderSpeed, leaderDecel, leaderFloor, leaderBrakeDelay, leaderBrakeDuration, reactionTime, minGap, 0f))
            return 0f;
        if (WouldCollide(gap, followerSpeed, leaderSpeed, leaderDecel, leaderFloor, leaderBrakeDelay, leaderBrakeDuration, reactionTime, minGap, 15f))
            return 99f;

        float lo = 0f, hi = 15f;
        for (int i = 0; i < 14; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (WouldCollide(gap, followerSpeed, leaderSpeed, leaderDecel, leaderFloor, leaderBrakeDelay, leaderBrakeDuration, reactionTime, minGap, mid)) lo = mid;
            else hi = mid;
        }
        return hi;
    }

    private static bool WouldCollide(float gap, float vf, float vl, float leaderDecel, float leaderFloor,
                                     float leaderBrakeDelay, float leaderBrakeDuration, float reactionTime, float minGap, float followerDecel)
    {
        const float dt = 0.05f;
        float g = gap;
        for (float t = 0f; t < 10f; t += dt)
        {
            // 前車
            if (t >= leaderBrakeDelay && t < leaderBrakeDelay + leaderBrakeDuration && vl > leaderFloor)
                vl = Mathf.Max(leaderFloor, vl - leaderDecel * dt);

            // 後車
            if (t >= reactionTime && vf > vl)
                vf = Mathf.Max(vl, vf - followerDecel * dt);

            g += (vl - vf) * dt;
            if (g < minGap) return true;
            if (t > leaderBrakeDelay + leaderBrakeDuration && vf <= vl + 0.01f) return false;   // 已經穩定，不會再更近
        }
        return false;
    }

    /// <summary>碰撞時間 TTC（秒）。沒有在接近就回傳無限大。</summary>
    public static float TimeToCollision(float gap, float followerSpeed, float leaderSpeed)
    {
        float closing = followerSpeed - leaderSpeed;
        if (closing <= 0.01f) return float.PositiveInfinity;
        return Mathf.Max(0f, gap) / closing;
    }

    /// <summary>車頭時距 THW（秒）= 間距 ÷ 後車速度</summary>
    public static float TimeHeadway(float gap, float followerSpeed)
    {
        if (followerSpeed <= 0.3f) return float.PositiveInfinity;
        return Mathf.Max(0f, gap) / followerSpeed;
    }

    /// <summary>
    /// 五次多項式平滑曲線 0→1（起點、終點的速度與加速度都是 0）。
    /// 換道的橫向位移用這條，才不會有「瞬間甩尾」的不自然感。
    /// </summary>
    public static float Quintic(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t * (10f + t * (-15f + 6f * t));
    }

    /// <summary>Quintic 對 t 的導數（用來算橫向速度 → 車頭偏角）</summary>
    public static float QuinticDerivative(float t)
    {
        t = Mathf.Clamp01(t);
        return 30f * t * t * (1f - t) * (1f - t);
    }
}
