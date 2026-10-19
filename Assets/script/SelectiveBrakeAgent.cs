using System.Diagnostics;
using UnityEngine;

public class SelectiveBrakeAgent : MonoBehaviour
{
    [Header("目標設定")]
    public Transform playerCar;     // 拖入你的 VR 玩家車

    [Header("移動參數")]
    public float moveSpeed = 10f;   // 平常前進速度
    public float brakeHardness = 8f; // 煞車有多硬（越大越急）

    [Header("偵測參數")]
    public float detectDistance = 15f; // 玩家在後方多近內會觸發
    [Range(0, 1)]
    public float angleStrictness = 0.7f; // 數值越大，玩家必須越靠近「正後方」才會觸發

    private bool isInsideBrakeZone = false;
    private float currentSpeed;

    void Start()
    {
        currentSpeed = moveSpeed;
    }

    void Update()
    {
        bool shouldEmergencyBrake = false;

        // 判斷 1：AI 是否在隱形層內
        if (isInsideBrakeZone)
        {
            // 判斷 2：計算玩家與 AI 的距離
            float dist = Vector3.Distance(transform.position, playerCar.position);

            if (dist < detectDistance)
            {
                // 判斷 3：計算玩家是否在 AI 的「後方」
                // 我們計算從 AI 指向玩家的向量
                Vector3 toPlayer = (playerCar.position - transform.position).normalized;

                // 使用點積 (Dot Product) 比較「AI 的後方 (-transform.forward)」與「指向玩家的向量」
                // 1 代表完全重合（正後方），0 代表垂直
                float lookMatch = Vector3.Dot(toPlayer, -transform.forward);

                if (lookMatch > angleStrictness)
                {
                    shouldEmergencyBrake = true;
                }
            }
        }

        // 執行動作
        if (shouldEmergencyBrake)
        {
            // 急煞：速度迅速降至 0
            currentSpeed = Mathf.Lerp(currentSpeed, 0, Time.deltaTime * brakeHardness);
            if (currentSpeed < 0.1f) currentSpeed = 0;
            UnityEngine.Debug.Log("⚠️ 警告：玩家在後方逼近，執行路口急煞！");
        }
        else
        {
            // 正常行駛：恢復速度
            currentSpeed = Mathf.MoveTowards(currentSpeed, moveSpeed, Time.deltaTime * 5f);
        }

        // 移動 AI
        transform.Translate(Vector3.forward * currentSpeed * Time.deltaTime);
    }

    // 進入隱形層觸發
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("BrakeZone"))
        {
            isInsideBrakeZone = true;
        }
    }

    // 離開隱形層觸發
    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("BrakeZone"))
        {
            isInsideBrakeZone = false;
        }
    }
}