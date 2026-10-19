using UnityEngine;

/// <summary>
/// 💥 玩家車碰撞音效
///
/// 掛在「玩家車」上（有 Rigidbody 的那個物件，也就是 vehicle ONLY LOD0）。
///
/// 因為腳本只存在於玩家車上，AI 車彼此相撞不會經過這裡 —— 天生就只限
/// 「玩家 ↔ AI 車」，不需要額外判斷誰撞誰，也不會有 AI 互撞洗版的問題。
///
/// 涵蓋的 AI 車種：
///   ReverseThreatAICar → 白車、側邊切入車、倒車威脅車、救護車（AmbulanceAI 繼承它）
///   CarNavigation      → 灰車
/// 預設「撞到什麼都算」——護欄、建築、路燈都會有音效與力回饋。
/// 想回到只認 AI 車的舊行為，把 onlyAIVehicles 勾起來。
/// </summary>
public class PlayerCollisionAudio : MonoBehaviour
{
    [Header("音效（可放多個，每次隨機挑一個）")]
    [Tooltip("建議放 聲音/撞擊_輕.wav 與 聲音/撞擊_重.wav")]
    public AudioClip[] collisionClips;
    [Tooltip("播放用的 AudioSource。留空會自動新增一個，不影響引擎音效")]
    public AudioSource audioSource;

    [Header("音量")]
    [Range(0f, 3f)]
    public float maxVolume = 1.2f;
    [Tooltip("撞擊速度達到這個值時音量最大（m/s）")]
    public float loudImpactSpeed = 12f;

    [Header("觸發條件")]
    // 原本寫死只認 AI 車,撞護欄、建築、路燈完全沒有音效與力回饋。
    // 以駕訓模擬器來說那不合理 —— 撞到什麼都是撞到,都該有回饋也都該計入評分。
    [Tooltip("只有撞到 AI 車才算。關掉的話撞護欄、建築、路燈等靜態物件也會有音效與力回饋")]
    public bool onlyAIVehicles = false;
    [Tooltip("撞擊相對速度低於這個值就不播，避免輕微磨蹭一直響（m/s）")]
    public float minImpactSpeed = 1.5f;
    [Tooltip("兩次碰撞音效之間至少間隔幾秒，避免連續接觸變成連發")]
    public float cooldown = 0.35f;

    [Header("撞擊回饋（留空會自動尋找）")]
    [Tooltip("撞到時切斷動力並損失車速")]
    public CarPhysicsController carPhysics;
    [Tooltip("撞到時讓方向盤模型抖動")]
    public G923SteeringWheel steeringWheel;
    [Tooltip("撞到時讓實體方向盤產生力回饋衝擊。沒接方向盤 SDK 時它會自動空轉，不影響其他功能")]
    public SteeringWheelFeedback wheelFeedback;

    // 撞擊文字提示已移除：撞到的當下車損系統(CarDamageDetector → DamageToast)已經報過
    // 「撞擊部位 ＋ 撞到什麼」，資訊量比「發生碰撞」高得多，兩則訊息還會搶同一個文字物件。
    // 分工：畫面文字歸車損系統，這支只負責撞擊的「感官回饋」——
    // 音效、動力懲罰、方向盤抖動與力回饋，以及把碰撞登記給危險感知。

    [Header("診斷")]
    public bool logCollisions = true;

    /// <summary>累計碰撞次數，可供考照評分讀取（是否含靜態物件由 onlyAIVehicles 決定）</summary>
    public int CollisionCount { get; private set; }

    private float nextAllowedTime = -1f;

    private void Start()
    {
        // 兩個都在玩家車的階層裡：CarPhysicsController 在同物件，方向盤在子物件
        if (carPhysics == null) carPhysics = GetComponent<CarPhysicsController>();
        if (carPhysics == null) carPhysics = GetComponentInParent<CarPhysicsController>();
        if (steeringWheel == null) steeringWheel = GetComponentInChildren<G923SteeringWheel>(true);
        if (wheelFeedback == null) wheelFeedback = GetComponent<SteeringWheelFeedback>();
        if (wheelFeedback == null) wheelFeedback = GetComponentInParent<SteeringWheelFeedback>();
        if (wheelFeedback == null) wheelFeedback = GetComponentInChildren<SteeringWheelFeedback>(true);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (Time.time < nextAllowedTime) return;
        if (collisionClips == null || collisionClips.Length == 0) return;

        if (onlyAIVehicles && !IsAIVehicle(collision.collider)) return;

        float impactSpeed = collision.relativeVelocity.magnitude;
        if (impactSpeed < minImpactSpeed) return;

        nextAllowedTime = Time.time + cooldown;
        CollisionCount++;

        // 撞得越用力越大聲
        float t = Mathf.Clamp01(impactSpeed / Mathf.Max(0.1f, loudImpactSpeed));
        float volume = Mathf.Lerp(maxVolume * 0.35f, maxVolume, t);

        // 重撞優先挑後面的音檔（依慣例把重撞放在陣列後面）
        int index = (collisionClips.Length > 1 && t > 0.55f)
            ? Random.Range(collisionClips.Length / 2, collisionClips.Length)
            : Random.Range(0, Mathf.Max(1, collisionClips.Length / 2));
        AudioClip clip = collisionClips[Mathf.Clamp(index, 0, collisionClips.Length - 1)];
        if (clip == null) return;

        EnsureAudioSource();
        audioSource.pitch = Random.Range(0.92f, 1.08f);
        audioSource.PlayOneShot(clip, volume);

        // 物理後果：頓一下 + 掉速。跟音效用同一個 t 當力道分級
        if (carPhysics != null) carPhysics.ApplyImpactPenalty(impactSpeed);
        if (steeringWheel != null) steeringWheel.ApplyImpactShake(t);

        // 🎮 實體方向盤的力回饋衝擊。沿用同一個 t —— 音效音量、動力懲罰、
        //    方向盤模型抖動、力回饋強度全部同一套力道分級，撞擊的輕重才會一致。
        if (wheelFeedback != null) wheelFeedback.ApplyImpact(t);

        // 🎯 把碰撞登記到進行中的危險因子上。
        //    反應偵測只看「速度有沒有掉」，而撞擊的速度暴跌比任何煞車都猛 ——
        //    少了這一行，撞上去會被判成「反應良好」，撞得越用力分數還越漂亮。
        if (HazardAwarenessTracker.Instance != null)
            HazardAwarenessTracker.Instance.ReportCollision(collision.collider.transform, t);

        if (logCollisions)
            Debug.Log($"<color=orange>💥 [碰撞] 玩家車撞到「{collision.collider.name}」" +
                      $"（撞擊速度 {impactSpeed:F1} m/s，累計 {CollisionCount} 次）</color>", collision.collider);
    }

    /// <summary>是不是 AI 車輛。onlyAIVehicles 關閉時這個判斷不再當成過濾條件，只保留給診斷用。</summary>
    private bool IsAIVehicle(Collider other)
    {
        if (other == null) return false;
        if (other.GetComponentInParent<ReverseThreatAICar>() != null) return true;  // 白車 / 側切車 / 倒車車 / 救護車
        if (other.GetComponentInParent<CarNavigation>() != null) return true;       // 灰車
        return false;
    }

    private void EnsureAudioSource()
    {
        if (audioSource != null) return;

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;   // 撞的是自己的車，用 2D 才有「就在身上」的衝擊感
        audioSource.dopplerLevel = 0f;
    }
}
