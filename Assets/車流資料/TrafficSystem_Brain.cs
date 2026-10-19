using UnityEngine;

public class TrafficSystem_Brain : MonoBehaviour
{
    public enum NormalPhase
    {
        A_to_C_Green,
        A_to_C_Yellow,
        A_to_C_AllRed,
        B_to_D_Green,
        B_to_D_Yellow,
        B_to_D_AllRed
    }

    public enum SpecialPhase
    {
        A_to_C_Green,
        A_to_C_Yellow,
        A_to_C_AllRed,
        B_to_D_Green,
        B_to_D_Yellow,
        B_to_D_AllRed,
        B_to_D_LeftGreen,
        B_to_D_LeftYellow,
        B_to_D_LeftAllRed
    }

    // 新增：閃光路口的狀態 (亮 / 滅)
    public enum BlinkPhase
    {
        BlinkOn,
        BlinkOff
    }

    [Header("=== 當前運行狀態 ===")]
    public NormalPhase normalPhase = NormalPhase.A_to_C_Green;
    public SpecialPhase specialPhase = SpecialPhase.A_to_C_Green;

    /// <summary>
    /// 目前是不是綠燈時相（一般路口）。黃燈與全紅都回 false。
    ///
    /// ⚠️ 這是「有某個方向是綠燈」，不是「你那個方向是綠燈」——
    ///    A_to_C_Green 代表 A↔C 綠而 B↔D 紅。全場只有一顆 Brain、時相是全域的，
    ///    所以這裡無法分辨路口，也無法分辨方向。
    ///    要知道「我這台車能不能走」請用車自己的 IsWaitingAtTrafficLight()。
    /// </summary>
    public bool IsNormalGreenNow =>
        normalPhase == NormalPhase.A_to_C_Green || normalPhase == NormalPhase.B_to_D_Green;

    /// <summary>
    /// 目前這個綠燈還剩幾秒。不是綠燈就回 0。
    ///
    /// 給危險因子判斷「現在發動來得及嗎」用 —— 例如行人穿越整套流程要跑約 5 秒，
    /// 綠燈只剩 2 秒時發動的話，等她跑到車前早就轉紅、整排車都停了，事件白演。
    /// timer 是 private 且每個時相都會歸零，所以這裡用「該時相長度 − 已經過」算。
    /// </summary>
    public float NormalGreenRemaining =>
        IsNormalGreenNow ? Mathf.Max(0f, normalGreenTime - normalTimer) : 0f;

    /// <summary>
    /// 特殊路口目前這個綠燈還剩幾秒。不是(直行)綠燈就回 0。
    /// 作法同 NormalGreenRemaining —— timer 是 private，用「時相長度 − 已經過」算。
    /// 純唯讀，給行人系統(PedestrianNode.GetRemainingGreenTime)判斷「還來得及過嗎」用，
    /// 不影響任何既有行為。
    /// </summary>
    public float SpecialGreenRemaining =>
        (specialPhase == SpecialPhase.A_to_C_Green || specialPhase == SpecialPhase.B_to_D_Green)
            ? Mathf.Max(0f, specialGreenTime - specialTimer) : 0f;

    // =========================================================
    // 【一般路口設定】
    // =========================================================
    [Header("【一般路口】時間設定 (秒)")]
    public float normalGreenTime = 10f;
    public float normalYellowTime = 3f;
    public float normalAllRedTime = 2f;
    private float normalTimer;

    [Header("燈光群組 - 南北向 (A -> C)")]
    public GameObject[] groupARed;
    public GameObject[] groupAYellow;
    public GameObject[] groupAGreen;

    [Header("燈光群組 - 東西向 (B -> D)")]
    public GameObject[] groupBRed;
    public GameObject[] groupBYellow;
    public GameObject[] groupBGreen;

    // =========================================================
    // 【特殊路口設定 (含左轉燈)】
    // =========================================================
    [Header("【特殊路口】時間設定 (秒)")]
    public float specialGreenTime = 10f;
    public float specialLeftGreenTime = 5f;
    public float specialYellowTime = 3f;
    public float specialAllRedTime = 2f;
    private float specialTimer;

    [Header("【特殊路口】燈光群組 - 南北向 (A -> C)")]
    public GameObject[] specialGroupARed;
    public GameObject[] specialGroupAYellow;
    public GameObject[] specialGroupAGreen;

    [Header("【特殊路口】燈光群組 - 東西向 (B -> D)")]
    public GameObject[] specialGroupBRed;
    public GameObject[] specialGroupBGreen;
    public GameObject[] specialGroupBLeftGreen;
    public GameObject[] specialGroupBYellow;

    // =========================================================
    // 【閃燈路口設定 (新增)】
    // =========================================================
    [Header("【閃燈路口】閃爍頻率設定")]
    public float blinkInterval = 0.8f; // 閃爍間隔時間 (秒)
    private float blinkTimer;
    private bool isBlinkOn = true;     // 當前閃爍切換開關

    [Header("【閃燈路口】燈光群組 - 主幹道 (閃黃燈，假設為 AC 向)")]
    public GameObject[] blinkMainYellow;

    [Header("【閃燈路口】燈光群組 - 支道 (閃紅燈，假設為 BD 向)")]
    public GameObject[] blinkSideRed;

    void Start()
    {
        normalTimer = 0f;
        specialTimer = 0f;
        blinkTimer = 0f;

        UpdateNormalLights();
        UpdateSpecialLights();
        UpdateBlinkLights();
    }

    void Update()
    {
        normalTimer += Time.deltaTime;
        specialTimer += Time.deltaTime;

        UpdateNormalStateMachine();
        UpdateSpecialStateMachine();
        UpdateBlinkStateMachine(); // 新增：更新閃燈狀態機
    }

    // ------------------------------------------
    // A. 一般路口狀態機
    // ------------------------------------------
    void UpdateNormalStateMachine()
    {
        switch (normalPhase)
        {
            case NormalPhase.A_to_C_Green:
                if (normalTimer >= normalGreenTime) SetNormalPhase(NormalPhase.A_to_C_Yellow);
                break;
            case NormalPhase.A_to_C_Yellow:
                if (normalTimer >= normalYellowTime) SetNormalPhase(NormalPhase.A_to_C_AllRed);
                break;
            case NormalPhase.A_to_C_AllRed:
                if (normalTimer >= normalAllRedTime) SetNormalPhase(NormalPhase.B_to_D_Green);
                break;
            case NormalPhase.B_to_D_Green:
                if (normalTimer >= normalGreenTime) SetNormalPhase(NormalPhase.B_to_D_Yellow);
                break;
            case NormalPhase.B_to_D_Yellow:
                if (normalTimer >= normalYellowTime) SetNormalPhase(NormalPhase.B_to_D_AllRed);
                break;
            case NormalPhase.B_to_D_AllRed:
                if (normalTimer >= normalAllRedTime) SetNormalPhase(NormalPhase.A_to_C_Green);
                break;
        }
    }

    void SetNormalPhase(NormalPhase newPhase)
    {
        normalPhase = newPhase;
        normalTimer = 0f;
        UpdateNormalLights();
    }

    void UpdateNormalLights()
    {
        SetGroupActive(groupAGreen, false); SetGroupActive(groupAYellow, false); SetGroupActive(groupARed, true);
        SetGroupActive(groupBGreen, false); SetGroupActive(groupBYellow, false); SetGroupActive(groupBRed, true);

        if (normalPhase == NormalPhase.A_to_C_Green) { SetGroupActive(groupAGreen, true); SetGroupActive(groupARed, false); }
        else if (normalPhase == NormalPhase.A_to_C_Yellow) { SetGroupActive(groupAYellow, true); SetGroupActive(groupARed, false); }
        else if (normalPhase == NormalPhase.B_to_D_Green) { SetGroupActive(groupBGreen, true); SetGroupActive(groupBRed, false); }
        else if (normalPhase == NormalPhase.B_to_D_Yellow) { SetGroupActive(groupBYellow, true); SetGroupActive(groupBRed, false); }
    }

    // ------------------------------------------
    // B. 特殊路口狀態機
    // ------------------------------------------
    void UpdateSpecialStateMachine()
    {
        switch (specialPhase)
        {
            case SpecialPhase.A_to_C_Green:
                if (specialTimer >= specialGreenTime) SetSpecialPhase(SpecialPhase.A_to_C_Yellow);
                break;
            case SpecialPhase.A_to_C_Yellow:
                if (specialTimer >= specialYellowTime) SetSpecialPhase(SpecialPhase.A_to_C_AllRed);
                break;
            case SpecialPhase.A_to_C_AllRed:
                if (specialTimer >= specialAllRedTime) SetSpecialPhase(SpecialPhase.B_to_D_Green);
                break;

            case SpecialPhase.B_to_D_Green:
                if (specialTimer >= specialGreenTime) SetSpecialPhase(SpecialPhase.B_to_D_Yellow);
                break;
            case SpecialPhase.B_to_D_Yellow:
                if (specialTimer >= specialYellowTime) SetSpecialPhase(SpecialPhase.B_to_D_AllRed);
                break;
            case SpecialPhase.B_to_D_AllRed:
                if (specialTimer >= specialAllRedTime) SetSpecialPhase(SpecialPhase.B_to_D_LeftGreen);
                break;
            case SpecialPhase.B_to_D_LeftGreen:
                if (specialTimer >= specialLeftGreenTime) SetSpecialPhase(SpecialPhase.B_to_D_LeftYellow);
                break;
            case SpecialPhase.B_to_D_LeftYellow:
                if (specialTimer >= specialYellowTime) SetSpecialPhase(SpecialPhase.B_to_D_LeftAllRed);
                break;
            case SpecialPhase.B_to_D_LeftAllRed:
                if (specialTimer >= specialAllRedTime) SetSpecialPhase(SpecialPhase.A_to_C_Green);
                break;
        }
    }

    void SetSpecialPhase(SpecialPhase newPhase)
    {
        specialPhase = newPhase;
        specialTimer = 0f;
        UpdateSpecialLights();
    }

    void UpdateSpecialLights()
    {
        SetGroupActive(specialGroupAGreen, false); SetGroupActive(specialGroupAYellow, false); SetGroupActive(specialGroupARed, true);
        SetGroupActive(specialGroupBGreen, false); SetGroupActive(specialGroupBLeftGreen, false); SetGroupActive(specialGroupBYellow, false); SetGroupActive(specialGroupBRed, true);

        switch (specialPhase)
        {
            case SpecialPhase.A_to_C_Green:
                SetGroupActive(specialGroupAGreen, true); SetGroupActive(specialGroupARed, false);
                break;
            case SpecialPhase.A_to_C_Yellow:
                SetGroupActive(specialGroupAYellow, true); SetGroupActive(specialGroupARed, false);
                break;
            case SpecialPhase.B_to_D_Green:
                SetGroupActive(specialGroupBGreen, true); SetGroupActive(specialGroupBRed, false);
                break;
            case SpecialPhase.B_to_D_Yellow:
                SetGroupActive(specialGroupBYellow, true); SetGroupActive(specialGroupBRed, false);
                break;
            case SpecialPhase.B_to_D_LeftGreen:
                SetGroupActive(specialGroupBRed, true); SetGroupActive(specialGroupBLeftGreen, true);
                break;
            case SpecialPhase.B_to_D_LeftYellow:
                SetGroupActive(specialGroupBYellow, true); SetGroupActive(specialGroupBRed, false); SetGroupActive(specialGroupBLeftGreen, false);
                break;
        }
    }

    // ------------------------------------------
    // C. 閃燈路口狀態機 (新增)
    // ------------------------------------------
    void UpdateBlinkStateMachine()
    {
        blinkTimer += Time.deltaTime;

        // 當計時器超過設定的閃爍頻率時切換開關
        if (blinkTimer >= blinkInterval)
        {
            blinkTimer = 0f;
            isBlinkOn = !isBlinkOn; // 亮 / 滅 切換
            UpdateBlinkLights();
        }
    }

    void UpdateBlinkLights()
    {
        // 根據 isBlinkOn 開關決定燈光亮或滅
        SetGroupActive(blinkMainYellow, isBlinkOn);
        SetGroupActive(blinkSideRed, isBlinkOn);
    }

    void SetGroupActive(GameObject[] group, bool state)
    {
        if (group == null) return;
        foreach (var obj in group)
        {
            if (obj != null) obj.SetActive(state);
        }
    }
}