using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 車輛損傷偵測 — 掛在車輛的 Rigidbody 物件上（和 CarPhysicsController 同一個）。
///
/// 用 OnCollisionEnter 記錄每一次碰撞：撞到什麼、撞到車身哪個部位、力道多大。
/// 只負責記錄，不影響駕駛手感與計分。
///
/// 兩道過濾避免雜訊：
///   ① 圖層過濾：地面／道路這種一直接觸的物件請加進「忽略的圖層」
///   ② 力道門檻：低於門檻的輕觸不記錄
///   ③ 同物件冷卻：短時間內重複碰同一物件只記一次（避免沿牆磨擦刷出上百筆）
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarDamageDetector : MonoBehaviour
{
    [Header("過濾設定")]
    [Tooltip("這些圖層的碰撞不列入車損（地面、道路等一直接觸的物件）")]
    public LayerMask 忽略的圖層;

    [Tooltip("撞擊力低於此值視為輕觸，不記錄")]
    public float 最小撞擊力 = 0.8f;

    [Tooltip("同一個物件在這段秒數內重複碰撞只記一次")]
    public float 同物件冷卻秒數 = 1.5f;

    [Header("損傷分級門檻（依撞擊力）")]
    [Tooltip("小於此值＝輕微")]
    public float 中度門檻 = 4f;

    [Tooltip("大於此值＝嚴重")]
    public float 嚴重門檻 = 12f;

    [Header("碰撞音效")]
    [Tooltip("播放碰撞聲的 AudioSource。留空會自動抓本物件上的")]
    public AudioSource 音源;

    [Tooltip("一般碰撞音效")]
    public AudioClip 碰撞音效;

    [Tooltip("嚴重碰撞時改用這個（選填，留空則一律用上面那個）")]
    public AudioClip 嚴重碰撞音效;

    [Range(0f, 1f)] public float 音量 = 0.8f;

    [Tooltip("音量隨撞擊力道變化：輕碰小聲、猛撞大聲")]
    public bool 音量依力道 = true;

    [Tooltip("每次播放隨機微調音調，避免連續碰撞聽起來像複製貼上")]
    [Range(0f, 0.3f)] public float 音調隨機範圍 = 0.08f;

    [Header("行為")]
    [Tooltip("進入場景時自動清空舊紀錄，開始新的一次記錄")]
    public bool 開場自動重置 = true;

    [Header("除錯")]
    public bool 除錯訊息 = true;

    private readonly Dictionary<int, float> 上次碰撞時間 = new Dictionary<int, float>();

    private void Start()
    {
        if (開場自動重置) DamageLog.開始新紀錄();
        if (音源 == null) 音源 = GetComponent<AudioSource>();
    }

    /// <summary>重新開始訓練時呼叫，可接到「重新開始」按鈕的 OnClick</summary>
    public void 重置紀錄()
    {
        上次碰撞時間.Clear();
        DamageLog.開始新紀錄();
    }

    private void OnCollisionEnter(Collision c)
    {
        // ① 圖層過濾
        if ((忽略的圖層.value & (1 << c.gameObject.layer)) != 0) return;

        // ② 力道門檻
        float 力 = c.impulse.magnitude;
        if (力 < 最小撞擊力) return;

        // ③ 同物件冷卻
        int id = c.gameObject.GetInstanceID();
        if (上次碰撞時間.TryGetValue(id, out float 上次) &&
            Time.time - 上次 < 同物件冷卻秒數) return;
        上次碰撞時間[id] = Time.time;

        // 取第一個接觸點來判斷撞到車身哪裡
        Vector3 接觸點 = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
        var 部位 = 判斷部位(接觸點);
        var 程度 = 判斷程度(力);

        DamageLog.新增(部位, 程度, 力, c.gameObject.name, 接觸點);
        播放碰撞音效(程度, 力);

        if (除錯訊息)
            Debug.Log($"[車損] {部位} 受到{程度}損傷｜撞到「{c.gameObject.name}」｜力道 {力:F1}");
    }

    /// <summary>
    /// 把接觸點換算成車身座標，用「方位角」判斷最接近八個偵測點中的哪一個。
    ///
    /// 車身座標：+Z 為車頭方向、+X 為車輛右側。
    /// 以車頭為 0 度順時針計算，每 45 度一區，各區中心正好落在一個偵測點上：
    ///     0°車頭中 ／ 45°右前角 ／ 90°右側中 ／ 135°右後角
    ///   180°車尾中 ／ 225°左後角 ／ 270°左側中 ／ 315°左前角
    /// </summary>
    private DamageLog.部位 判斷部位(Vector3 世界接觸點)
    {
        Vector3 本地 = transform.InverseTransformPoint(世界接觸點);

        // 以車頭(+Z)為 0 度，向右(+X)為正，取 0~360
        float 角度 = Mathf.Atan2(本地.x, 本地.z) * Mathf.Rad2Deg;
        if (角度 < 0f) 角度 += 360f;

        // 每區 45 度，+22.5 讓區間中心對準偵測點
        int 區 = Mathf.FloorToInt(((角度 + 22.5f) % 360f) / 45f);

        switch (區)
        {
            case 0:  return DamageLog.部位.車頭中;
            case 1:  return DamageLog.部位.右前角;
            case 2:  return DamageLog.部位.右側中;
            case 3:  return DamageLog.部位.右後角;
            case 4:  return DamageLog.部位.車尾中;
            case 5:  return DamageLog.部位.左後角;
            case 6:  return DamageLog.部位.左側中;
            default: return DamageLog.部位.左前角;
        }
    }

    private DamageLog.程度 判斷程度(float 力)
    {
        if (力 >= 嚴重門檻) return DamageLog.程度.嚴重;
        if (力 >= 中度門檻) return DamageLog.程度.中度;
        return DamageLog.程度.輕微;
    }

    /// <summary>依損傷程度與力道播放碰撞聲</summary>
    private void 播放碰撞音效(DamageLog.程度 程度, float 力)
    {
        if (音源 == null) return;

        // 嚴重碰撞有專用音效就用它，否則一律用一般音效
        AudioClip clip = (程度 == DamageLog.程度.嚴重 && 嚴重碰撞音效 != null)
                       ? 嚴重碰撞音效
                       : 碰撞音效;
        if (clip == null) return;

        float 播放音量 = 音量;
        if (音量依力道)
        {
            // 把力道從「最小門檻 ~ 嚴重門檻」映射成 0.4 ~ 1 的音量倍率
            float t = Mathf.InverseLerp(最小撞擊力, 嚴重門檻, 力);
            播放音量 *= Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(t));
        }

        // 隨機微調音調，讓連續碰撞不會聽起來一模一樣
        float 原音調 = 音源.pitch;
        音源.pitch = 1f + Random.Range(-音調隨機範圍, 音調隨機範圍);
        音源.PlayOneShot(clip, 播放音量);
        音源.pitch = 原音調;
    }
}
