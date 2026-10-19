using UnityEngine;

/// <summary>
/// 🏍 機車騎士 IK —— 把雙手釘在手把上、雙腳釘在踏板上
///
/// 【為什麼需要這支】
///   騎士播的是 Mixamo 的 Driving.fbx，那是「開汽車」的動作：
///   雙手在方向盤位置（胸前、兩手靠近）。套到機車上，手會停在手把的上方偏後，
///   看起來就是懸空抓不到握把。
///
///   但那個動畫本身有價值 —— 它讓騎士有細微的呼吸與晃動，比完全靜止自然得多。
///   所以不是把 Animator 關掉，而是「動畫照播，手腳另外釘住」，這正是 IK 的用途。
///
/// 【前置條件（少一個就完全沒作用）】
///   1. 角色的 Avatar 必須是 Humanoid —— Unity 的 AvatarIKGoal 只支援 Humanoid
///   2. Animator Controller 的該 Layer 必須勾選 IK Pass
///      （Controller 視窗 → Base Layer 右邊的齒輪 → IK Pass）
///      沒勾的話 OnAnimatorIK 根本不會被呼叫，而且不會有任何錯誤訊息 ——
///      所以下面加了一道保險：兩秒後還沒被呼叫過就主動警告。
///
/// 【怎麼退回原樣】
///   四個權重全部設 0 就等於沒有這支腳本，動畫回到原本的樣子。
/// </summary>
// ⚠️ 刻意不用 [RequireComponent(typeof(Animator))]:
//    掛錯層時它會安靜地在該物件上生一顆「空的 Animator」(沒 Controller 沒 Avatar),
//    腳本抓到那顆之後一切看似正常,卻永遠收不到 OnAnimatorIK ——
//    而且錯誤訊息會指向 IK Pass,把人帶往完全錯誤的方向。
//    寧可沒有 Animator 就直接報錯。
public class ScooterRiderIK : MonoBehaviour
{
    [Header("目標點（留空會自動用名字尋找）")]
    [Tooltip("留空的話會在機車物件底下找名為 LeftHand_Target 的子物件")]
    public Transform leftHandTarget;
    public Transform rightHandTarget;
    public Transform leftFootTarget;
    public Transform rightFootTarget;

    [Header("權重（全部設 0 = 等於關閉這支腳本）")]
    // 位置權重決定「手要不要移到目標點」，旋轉權重決定「手腕角度要不要跟著目標點」。
    // 腳的旋轉權重通常給低一點：踏板的角度不一定跟腳掌自然角度一致，
    // 給滿的話腳踝會扭得很怪。
    [Range(0f, 1f)] public float handPositionWeight = 1f;
    [Range(0f, 1f)] public float handRotationWeight = 1f;
    [Range(0f, 1f)] public float footPositionWeight = 1f;
    [Range(0f, 1f)] public float footRotationWeight = 0.6f;

    [Header("腳掌打平")]
    // 「墊腳尖」是旋轉問題,位置偏移解不了 —— 腳踝的角度必須被控制。
    // 但直接照抄 *_Target 的旋轉常常是歪的(那些預留點的角度不見得對),
    // 要人工湊出正確角度很痛苦。
    //
    // 這裡改成「用目標點的朝向決定腳尖指向,再把整個腳掌壓平到機車的水平面」——
    // 這就是 Unity 官方腳部 IK 範例的做法(LookRotation(forward, 地面法線))。
    // 用機車的 up 而不是世界的 up,機車傾斜時腳掌才會跟著踏板一起斜。
    [Tooltip("把腳掌壓平到機車踏板的水平面,而不是照抄目標點的旋轉。解決墊腳尖最直接的做法。" +
             "★ 需要 Foot Rotation Weight 大於 0 才會生效,建議 0.8~1")]
    public bool levelFeetToBike = true;

    [Header("微調偏移 —— 位置（公尺）")]
    // 以「機車的方向」為基準,四肢通用:
    //     Z = 前後（正 = 往車頭方向）
    //     X = 左右（正 = 往車身右邊）
    //     Y = 上下（正 = 往上）
    // 用機車座標而不是目標點自己的座標,是因為四個目標點的朝向各不相同,
    // 那樣「往前一點」在每隻手腳會指向不同方向,調起來會瘋掉。
    [Tooltip("左手位置微調。Z 正 = 往車頭伸,Y 正 = 往上")]
    public Vector3 leftHandOffset;
    public Vector3 rightHandOffset;
    [Tooltip("左腳位置微調。Y 正 = 腳掌往上抬")]
    public Vector3 leftFootOffset;
    public Vector3 rightFootOffset;

    [Header("微調偏移 —— 旋轉（度）")]
    // ⚠️ 旋轉偏移要看得到效果,對應的 Rotation Weight 必須大於 0。
    //    現在 Foot Rotation Weight = 0,所以腳的旋轉偏移不會有反應 ——
    //    想調腳掌角度的話要先把那個權重拉到 0.3 以上。
    public Vector3 leftHandRotOffset;
    public Vector3 rightHandRotOffset;
    public Vector3 leftFootRotOffset;
    public Vector3 rightFootRotOffset;

    [Header("診斷")]
    [Tooltip("開場印出找到哪些目標點；IK Pass 沒開時也會警告")]
    public bool logSetup = true;

    private Animator animator;
    private Transform offsetRoot;   // 偏移量的基準座標系 = 機車本體
    private bool ikEverRan;      // OnAnimatorIK 有沒有被呼叫過 —— 用來偵測 IK Pass 沒開
    private bool rotWarnDone;    // 旋轉偏移無效的提醒只講一次
    private float warnTime = -1f;

    private void Awake()
    {
        animator = GetComponent<Animator>();

        // ⚠️ OnAnimatorIK 是 Unity 的訊息,只會發給「Animator 所在的那一顆 GameObject」。
        //    這支腳本必須跟 Animator 在同一顆物件上(也就是騎士,不是機車根物件)。
        if (animator == null)
        {
            Animator inChild = GetComponentInChildren<Animator>();
            Debug.LogError($"[騎士IK] ★掛錯層了 —— 「{name}」上面沒有 Animator。" +
                           $"這支腳本必須掛在「有 Animator 的那顆騎士物件」上。" +
                           (inChild != null
                              ? $"找到了,應該掛在子物件「{inChild.name}」上。"
                              : "往子物件找也沒找到 Animator。"), this);
            enabled = false;
            return;
        }
        if (animator.runtimeAnimatorController == null)
        {
            Debug.LogError($"[騎士IK] ★「{name}」上的 Animator 沒有指定 Controller —— " +
                           $"這通常代表腳本掛在機車根物件上,而那顆 Animator 是被自動加上去的空元件。" +
                           $"請把這支腳本(和那顆空 Animator)移到騎士物件上。", this);
            enabled = false;
            return;
        }

        AutoFindTargets();
        warnTime = Time.time + 2f;

        if (logSetup)
        {
            if (animator != null && !animator.isHuman)
                Debug.LogWarning($"[騎士IK] {name} 的 Avatar 不是 Humanoid，IK 完全無法運作。" +
                                 $"請把角色模型的 Rig → Animation Type 設成 Humanoid。", this);
            else
                Debug.Log($"<color=orange>🏍 [騎士IK] 已啟動｜左手={Show(leftHandTarget)}｜右手={Show(rightHandTarget)}" +
                          $"｜左腳={Show(leftFootTarget)}｜右腳={Show(rightFootTarget)}</color>", this);
        }
    }

    private string Show(Transform t) => t != null ? t.name : "★找不到";

    /// <summary>
    /// 沒手動指定就自己找。從機車本體（掛 CarNavigation 的那一層）往下遞迴搜尋，
    /// 不用 transform.root —— 場景裡的機車可能掛在很上層的容器底下，
    /// 用 root 會把整座城市當成搜尋範圍。
    /// </summary>
    private void AutoFindTargets()
    {
        Transform bikeRoot = null;
        CarNavigation nav = GetComponentInParent<CarNavigation>();
        if (nav != null) bikeRoot = nav.transform;
        if (bikeRoot == null) bikeRoot = transform.root;
        offsetRoot = bikeRoot;

        if (leftHandTarget == null) leftHandTarget = FindDeep(bikeRoot, "LeftHand_Target");
        if (rightHandTarget == null) rightHandTarget = FindDeep(bikeRoot, "RightHand_Target");
        if (leftFootTarget == null) leftFootTarget = FindDeep(bikeRoot, "LeftFoot_Target");
        if (rightFootTarget == null) rightFootTarget = FindDeep(bikeRoot, "RightFoot_Target");
    }

    private static Transform FindDeep(Transform root, string targetName)
    {
        if (root == null) return null;
        if (root.name == targetName) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), targetName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// ⚠️ 這個方法只有在 Animator Controller 的該 Layer 勾了 IK Pass 才會被呼叫。
    ///    沒勾的話 Unity 不會報錯，只是靜靜地什麼都不做 —— 所以下面 Update 有保險。
    /// </summary>
    private void OnAnimatorIK(int layerIndex)
    {
        ikEverRan = true;
        if (animator == null || !animator.isHuman) return;

        Apply(AvatarIKGoal.LeftHand, leftHandTarget, handPositionWeight, handRotationWeight,
              leftHandOffset, leftHandRotOffset, false);
        Apply(AvatarIKGoal.RightHand, rightHandTarget, handPositionWeight, handRotationWeight,
              rightHandOffset, rightHandRotOffset, false);
        Apply(AvatarIKGoal.LeftFoot, leftFootTarget, footPositionWeight, footRotationWeight,
              leftFootOffset, leftFootRotOffset, levelFeetToBike);
        Apply(AvatarIKGoal.RightFoot, rightFootTarget, footPositionWeight, footRotationWeight,
              rightFootOffset, rightFootRotOffset, levelFeetToBike);
    }

    /// <summary>目標點的世界座標(已套用偏移)。Gizmo 與 IK 都用這一支,畫出來的就是實際生效的位置。</summary>
    private Vector3 GoalPosition(Transform target, Vector3 offset)
    {
        if (target == null) return Vector3.zero;
        if (offset == Vector3.zero) return target.position;
        // 用 TransformDirection 而不是 TransformPoint:只要方向,不要被機車的縮放放大偏移量
        Vector3 dir = offsetRoot != null ? offsetRoot.TransformDirection(offset) : offset;
        return target.position + dir;
    }

    /// <summary>
    /// 目標旋轉。levelToBike = true 時只取目標點的「朝向」(腳尖指哪邊),
    /// 再把腳掌壓平到機車的水平面 —— 目標點本身的俯仰與側傾被忽略,所以不會墊腳尖。
    /// </summary>
    private Quaternion GoalRotation(Transform target, Vector3 rotOffset, bool levelToBike)
    {
        Quaternion baseRot = target.rotation;

        if (levelToBike && offsetRoot != null)
        {
            Vector3 up = offsetRoot.up;
            Vector3 fwd = Vector3.ProjectOnPlane(target.forward, up);   // 把朝向壓到水平面上
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.ProjectOnPlane(offsetRoot.forward, up);
            if (fwd.sqrMagnitude > 0.0001f) baseRot = Quaternion.LookRotation(fwd.normalized, up);
        }

        return baseRot * Quaternion.Euler(rotOffset);
    }

    private void Apply(AvatarIKGoal goal, Transform target, float posWeight, float rotWeight,
                       Vector3 posOffset, Vector3 rotOffset, bool levelToBike)
    {
        // 目標點不存在或權重 0 → 明確把權重歸零，讓動畫原本的姿勢完全接手
        if (target == null || posWeight <= 0f)
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            return;
        }

        animator.SetIKPositionWeight(goal, posWeight);
        animator.SetIKPosition(goal, GoalPosition(target, posOffset));

        animator.SetIKRotationWeight(goal, rotWeight);
        if (rotWeight > 0f) animator.SetIKRotation(goal, GoalRotation(target, rotOffset, levelToBike));
    }

    /// <summary>
    /// 「我明明設了旋轉偏移,怎麼沒反應」是這支腳本最容易踩的坑:
    /// 位置與旋轉是兩個獨立的權重,旋轉權重 0 的話 SetIKRotation 根本不會被呼叫,
    /// 而且 Unity 不會有任何提示。這裡主動檢查並講清楚。
    /// </summary>
    private void WarnUselessRotationOffsets()
    {
        bool handOffsetSet = leftHandRotOffset != Vector3.zero || rightHandRotOffset != Vector3.zero;
        bool footOffsetSet = leftFootRotOffset != Vector3.zero || rightFootRotOffset != Vector3.zero;

        if (handOffsetSet && handRotationWeight < 0.2f)
            Debug.LogWarning($"[騎士IK] {name}:手部設了旋轉偏移,但 Hand Rotation Weight 只有 " +
                             $"{handRotationWeight:F2} —— 幾乎看不出效果。要調手腕角度請先把它拉到 0.5 以上。", this);

        if (footOffsetSet && footRotationWeight < 0.2f)
            Debug.LogWarning($"[騎士IK] {name}:腳部設了旋轉偏移,但 Foot Rotation Weight 只有 " +
                             $"{footRotationWeight:F2} —— 完全不會套用。要調腳掌角度請先把它拉到 0.4 以上。", this);
    }

    private void Update()
    {
        // 旋轉偏移設了卻沒作用 —— 只在啟動後檢查一次,不洗版
        if (!rotWarnDone && Time.time > 1f) { rotWarnDone = true; WarnUselessRotationOffsets(); }

        // IK Pass 沒勾是最常見的失敗，而且完全沒有錯誤訊息可循 —— 主動講出來
        if (!ikEverRan && warnTime > 0f && Time.time > warnTime)
        {
            warnTime = -1f;
            if (logSetup)
                Debug.LogWarning($"[騎士IK] {name}（Animator 在同一顆物件上、Controller 也有）：" +
                                 $"OnAnimatorIK 兩秒內一次都沒被呼叫 —— " +
                                 $"到這一步還沒被呼叫的話,原因就是 Controller 的 Layer 沒勾 IK Pass。" +
                                 $"打開 Controller 視窗,點 Base Layer 右邊的齒輪圖示,勾 IK Pass。", this);
        }
    }

#if UNITY_EDITOR
    // 選取騎士時把四個目標點畫出來，位置對不對一眼就看得到
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.green;
        DrawTarget(leftHandTarget, leftHandOffset); DrawTarget(rightHandTarget, rightHandOffset);
        Gizmos.color = Color.cyan;
        DrawTarget(leftFootTarget, leftFootOffset); DrawTarget(rightFootTarget, rightFootOffset);
    }

    private void DrawTarget(Transform t, Vector3 offset)
    {
        if (t == null) return;
        Vector3 goal = GoalPosition(t, offset);
        Gizmos.DrawWireSphere(goal, 0.05f);           // 實際生效的位置
        Gizmos.DrawLine(goal, goal + t.forward * 0.12f);
        if (offset != Vector3.zero)
        {
            Color c = Gizmos.color;
            Gizmos.color = new Color(c.r, c.g, c.b, 0.35f);
            Gizmos.DrawWireSphere(t.position, 0.03f);  // 原始目標點
            Gizmos.DrawLine(t.position, goal);         // 偏移了多少
            Gizmos.color = c;
        }
    }
#endif
}
