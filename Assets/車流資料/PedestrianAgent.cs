using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class PedestrianAgent : Agent
{
    public Transform carTransform;
    public CarMove carScript;
    public float speed = 10f;
    private Rigidbody rb;
    public Animator anim;

    // ====== 用於擺脫絕對座標的變數 ======
    private Vector3 initialScenePosition; // 新增：記錄遊戲剛開始時，你手動在場景擺的位置
    private Vector3 startPosition;        // 記錄每輪的起點（含隨機擾動後）
    [Header("馬路設定(相對距離)")]
    private float sidewalkWidth = 0.67f; // 離開起點多遠算踏入馬路 (原本 -4.0 到 -3.6 的距離)
    private float roadWidth = 6.6f;     // 馬路的總寬度 (原本 -4.0 到 3.0 的總距離)
    // ==========================================

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        // 記錄初始位置
        initialScenePosition = this.transform.localPosition;
    }

    public override void OnEpisodeBegin()
    {
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 不再使用死資料，而是以你在場景擺的位置為基準，加上隨機微調
        this.transform.localPosition = new Vector3(
            initialScenePosition.x + Random.Range(-0.1f, 0.1f),
            initialScenePosition.y,
            initialScenePosition.z
        );

        // 記錄這一輪真正的起點（用於後續相對距離計算）
        startPosition = this.transform.localPosition;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 改進：觀察相對於「起點」的位置，而不是世界/本地絕對座標
        Vector3 relativeToStart = this.transform.localPosition - startPosition;
        sensor.AddObservation(relativeToStart); // (3)

        sensor.AddObservation(rb.velocity.x); // (1)

        // 提供與車子的相對座標差
        Vector3 relativePos = carTransform.localPosition - this.transform.localPosition;
        sensor.AddObservation(relativePos.x); // (1)
        sensor.AddObservation(relativePos.z); // (1)

        sensor.AddObservation(carScript.speed); // (1)
        sensor.AddObservation(carScript.isMoving ? 1f : 0f); // (1)

        // 總計依舊是 8 個觀察值，Inspector 的 Space Size 維持 8 即可
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // --- 1. 動作執行 ---
        int moveAction = actions.DiscreteActions[0];
        Vector3 moveDir = Vector3.zero;

        if (moveAction == 1) moveDir = transform.forward;
        else if (moveAction == 2) moveDir = -transform.forward;

        if (anim != null)
        {
            float currentHorizontalSpeed = new Vector3(rb.velocity.x, 0, rb.velocity.z).magnitude;
            anim.SetFloat("MoveSpeed", currentHorizontalSpeed);
        }

        // --- 2. 變數準備（改為相對距離） ---
        // 計算行人相對於起點，在 X 軸上往前走了多遠
        float movedDistanceX = this.transform.localPosition.x - startPosition.x;
        float distToCar = Vector3.Distance(this.transform.position, carTransform.position);

        // --- 3. 核心判定邏輯（全相對化） ---

        // 【改良型禁足：隱形牆邏輯】
        // 車在 15m 外時，如果想往前衝（剛出發沒多久 movedDistanceX < sidewalkWidth），沒收速度
        //if (distToCar > 25f && movedDistanceX < sidewalkWidth && moveAction == 1)
        //{
        //    AddReward(-0.005f);
        //    rb.velocity = Vector3.zero;
        //    return;
        //}
        //if (moveAction == 2)
        //{
        //    AddReward(-0.005f);
        //    rb.velocity = Vector3.zero;
        //    return;
        //}

        // 執行物理移動
        rb.velocity = moveDir * speed;
        float currentCarSpeed = carScript.speed;

        // A. 安全區 (剛出發，還在路邊)
        if (movedDistanceX <= sidewalkWidth)
        {
            // 引導獎勵
            if (distToCar < 12f && distToCar > 5f)
            {
                AddReward(-0.001f);
            }
            else
            {
                AddReward(0.0005f);
            }

            // 退後太多（相對於起點往後退了 0.95m）
            if (movedDistanceX < -0.85f)
            {
                SetReward(-1.0f);
                Debug.Log("<color=yellow>行人退後到路邊草叢了</color>");
                ResetEpisodeScene();
                return;
            }
        }
        // B. 馬路上 (危險區：離開了路邊草叢，但還沒穿越整條馬路)
        else if (movedDistanceX > sidewalkWidth && movedDistanceX <= roadWidth)
        {
            // 1. 只要在馬路上，每固定一幀就給予微小的「時間懲罰」
            // 待在馬路上的時間越長，扣越多分，強迫他用最短時間通過！
            AddReward(-0.02f);

            if (moveAction == 1)
            {
                AddReward(0.1f); // 保持原本的合法衝刺期鼓勵
            }

            // 2. 加重發呆的懲罰（從 -0.05f 提高到 -0.5f）
            // 並且，如果他在馬路上敢停下來或往後退 (moveAction != 1)
            // 就要給他狠狠的扣分，讓他知道在馬路中間停下是致命的
            if (rb.velocity.magnitude < 1.0f || moveAction != 1)
            {
                AddReward(-0.5f); // 嚴禁在馬路中間發呆或回頭
            }
        }
        // C. 成功抵達對面 (前進距離超過了馬路寬度)
        else if (movedDistanceX > roadWidth)
        {
            if (distToCar >= 6f && distToCar <= 15f)
            {
                Debug.Log($"<color=cyan>【完美鬼切！】</color> 距離：{distToCar:F2}m | 車速：{currentCarSpeed:F2} m/s");
                SetReward(50.0f);
            }
            else if (distToCar < 6f)
            {
                Debug.Log($"<color=orange>【極限過馬路!】</color> 距離：{distToCar:F2}m | 車速：{currentCarSpeed:F2} m/s");
                SetReward(10.0f);
            }
            else
            {
                Debug.Log($"<color=white>【成功過馬路】</color> 距離：{distToCar:F2}m | 車速：{currentCarSpeed:F2} m/s");
                SetReward(10.0f);
            }

            ResetEpisodeScene();
            return;
        }

        // --- 4. 異常判定（Z 軸改為相對起點的偏移） ---
        float relativeZ = this.transform.localPosition.z - startPosition.z;
        if (this.transform.localPosition.y < 0f || Mathf.Abs(relativeZ) > 5f)
        {
            Debug.Log("<color=yellow>觸發 Z 軸出界重置</color>");
            SetReward(-1.0f);
            ResetEpisodeScene();
            return;
        }

        // --- 5. 漏車判定 (膽小懲罰) ---
        if (carTransform.localPosition.z < this.transform.localPosition.z - 7f)
        {
            if (carScript.isMoving)
            {
                Debug.Log("<color=yellow>太膽小了，沒趕上時機</color>");
                SetReward(-5.0f);
                ResetEpisodeScene();
                return;
            }
        }

        // --- 6. 生存代價 ---
        AddReward(-0.001f);

        // 保持 Y 軸穩定
        Vector3 currentVel = rb.velocity;
        currentVel.y = 0;
        rb.velocity = currentVel;

        // 這裡的高度 1.2f 也可以考慮改成 startPosition.y，就徹底不綁定任何絕對數值
        transform.localPosition = new Vector3(transform.localPosition.x, startPosition.y, transform.localPosition.z);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Car"))
        {
            float currentCarSpeed = carScript.speed;
            Debug.Log($"<color=red>被車撞</color> | 車速：{currentCarSpeed:F2} m/s");
            SetReward(-5.0f);
            ResetEpisodeScene();
        }
    }

    // 提取出來的重置方法，減少重複代碼
    private void ResetEpisodeScene()
    {
        carScript.ResetCar();
        EndEpisode();
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActions = actionsOut.DiscreteActions;
        discreteActions[0] = 0;
        if (Input.GetKey(KeyCode.W)) discreteActions[0] = 1;
        else if (Input.GetKey(KeyCode.S)) discreteActions[0] = 2;
    }
}