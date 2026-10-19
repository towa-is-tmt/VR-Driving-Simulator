using UnityEngine;

public class ConstantMove : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float defaultSpeed = 2.5f; // 基礎速度
    private float currentSpeed;

    [Header("References")]
    public Transform grayCar; // 在 Inspector 拖入灰車 (CarAgent)
    public float stopLineZ = 26f; // 與灰車腳本相同的停止線位置

    private Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        currentSpeed = defaultSpeed;
    }

    void FixedUpdate()
    {
        // 1. 取得環境狀態 (假設你有一個全域或單例獲取燈號的方法，或者這裡簡單判斷)
        // 為了簡單起見，我們可以在這裡加入判斷邏輯
        UpdateWhiteCarAI();

        // 2. 執行移動
        rb.MovePosition(rb.position + transform.forward * currentSpeed * Time.fixedDeltaTime);
    }

    private void UpdateWhiteCarAI()
    {
        // 預設恢復速度
        currentSpeed = defaultSpeed;

        // --- 邏輯 A: 距離灰車太近 (防止追撞) ---
        if (grayCar != null)
        {
            float distToGrayCar = grayCar.position.z - transform.position.z;

            // 如果灰車在前面 (z軸領先) 且距離小於 5 米，就停下
            if (distToGrayCar > 0 && distToGrayCar < 5f)
            {
                currentSpeed = 0f;
                return; // 優先處理防撞
            }
        }

        // --- 邏輯 B: 紅燈停等 ---
        // 這裡需要能讀取到目前紅綠燈的狀態
        // 假設你場景中有一個標記為 "RedLight" 的物件或是透過一個靜態變數讀取
        if (IsRedLightActive() && transform.position.z < stopLineZ && (stopLineZ - transform.position.z) < 6f)
        {
            currentSpeed = 0f;
        }
    }

    // 簡單的判定方法 (可根據你的 TrafficLight 邏輯調整)
    private bool IsRedLightActive()
    {
        // 這裡可以檢查場景中 RedLightBall 是否啟動，或者從 CarAgent 讀取
        // 暫時建議你在 Inspector 裡讓白車腳本也能看到紅綠燈物件
        GameObject redLight = GameObject.FindWithTag("RedLight");
        return redLight != null;
    }

    public void SetSpeed(float newSpeed)
    {
        defaultSpeed = newSpeed;
    }
}