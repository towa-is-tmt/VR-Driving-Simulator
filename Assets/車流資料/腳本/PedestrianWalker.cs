using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public class PedestrianWalker : MonoBehaviour
{
    [Header("=== 路徑點設定 ===")]
    public PedestrianNode previousNode;        // 前一個路徑點
    public PedestrianNode currentTargetNode;   // 目標路徑點
    
    [Header("=== 行人路徑預看 ===")]
    private int pathLookAheadCount = 3;
    private PedestrianNode[] plannedNodes = new PedestrianNode[4];

    [Header("=== 移動速度設定 (m/s) ===")]
    private float walkSpeed = 3f;
    private float runSpeed = 5f;
    private float rotationSpeed = 5.0f;
    private float reachDistance = 0.2f;

    [Header("=== 閃黃燈路口：左側來車安全偵測 ===")]
    [SerializeField] private LayerMask vehicleLayer;
    // 行人距離 BlinkSystem Node 多遠時開始停下檢查
    private float blinkStopDistance = 0.3f;
    // 左側來車偵測框，相對於 BlinkNode 的位置 (左方, 高度, 前方)
    private Vector3 blinkCarBoxOffset = new Vector3(-5f, 0.8f, 6f);
    // 偵測框大小
    private Vector3 blinkCarBoxSize = new Vector3(10f, 2f, 10f);

    private bool blinkCarDetected = false;
    // 是否正在閃黃燈路口 Stop 點檢查來車
    private bool isBlinkCheckingCar = false;
    // 已經確認安全，正在通過閃黃燈路口
    private bool blinkCrossingCommitted = false;

    // 行人停下觀察的最短時間（秒）與計時器
    private float blinkCheckDuration = 0.1f;
    private float blinkCheckTimer = 0f;

    [Header("=== 前方避障設定 ===")]
    private float obstacleCheckDistance = 1.3f; // 中央
    private float sideObstacleCheckDistance = 1.0f;  // 左右
    private float obstacleCheckRadius = 0.4f;   // 偵測球體半徑
    public LayerMask pedestrianLayer;          // 請在 Inspector 指定行人的 Layer
    private bool isBlockedByFrontPerson = false;
    private bool isQueuedBehindPedestrian = false; // 排隊在前方行人後方，等待前方行人移動
    private bool isBlockedByFrontVehicle = false;
    // 跟隨前方同方向行人
    private bool isFollowingMovingPedestrian = false;
    // 判斷「同方向」的門檻 1 = 完全同方向，0 = 90度
    private float sameDirectionThreshold = 0.7f;
    // 太靠近時停止，避免撞進前方行人
    private float pedestrianFollowStopDistance = 0.8f;
    // 判定行人是否真的有在走
    private float pedestrianMovingSpeedThreshold = 0.1f;

    private Vector3 avoidanceDir = Vector3.zero; // 避障轉向向量
    // 三顆偵測球體的偏移量與角度
    private Vector3 centerPedSphereOffset = new Vector3(0f, 1.0f, 0f);
    private float centerPedSphereAngle = 0f;
    private Vector3 leftPedSphereOffset = new Vector3(0f, 1.0f, 0f); //(左側, 高度, 前方)
    private float leftPedSphereAngle = -12f;
    private Vector3 rightPedSphereOffset = new Vector3(0f, 1.0f, 0f);
    private float rightPedSphereAngle = 12f;
    //--------------------------------------------------------------------------
    // 自由區設定
    //--------------------------------------------------------------------------
    [Header("=== 自由活動區設定 ===")]
    private Vector2 wanderPauseRange = new Vector2(2f, 4f);
    private PedestrianFreeZone currentZone;
    private Coroutine wanderRoutine;
    private Coroutine moveRoutine;

    // === 自由區避障鎖定 ===
    private bool isFreeZoneAvoiding = false;
    private Vector3 freeZoneAvoidanceDir = Vector3.zero;
    private float freeZoneAvoidanceTimer = 0f;
    // 決定閃避後，至少維持這個時間
    private float freeZoneAvoidanceDuration = 0.8f;
    // 閃避時以避障方向為主
    private float freeZoneAvoidanceWeight = 0.8f;

    [Header("=== 聊天與單人動作觸發設定 ===")]
    private float chatChance = 20f;
    private float chatDetectRadius = 1.5f;
    private float chatCooldownDuration = 10f;
    private float chatCooldown = 0f;

    [Header("=== 聊天種類設定 ===")]
    [Tooltip("被觸發聊天時，變成生氣版聊天的機率 (0~100)")]
    private float angryChatChance = 20f;
    private float safetyTimeout = 20f;

    private float currentMoveSpeed;
    private bool isWaitingForLight = false;

    [Header("=== 講電話道具 (已手動掛在手部骨骼下，直接拖物件進來) ===")]
    [Tooltip("對應 CellPhone 動作的手機物件")]
    [SerializeField] private GameObject cellPhoneObject;
    [Tooltip("對應 PlayPhone 動作的手機物件")]
    [SerializeField] private GameObject playPhoneObject;

    // 取得目前行人移動速度
    public float GetCurrentMoveSpeed()
    {
        return currentMoveSpeed;
    }
    // 取得行人是否正在等待紅綠燈
    public bool IsWaitingForLight()
    {
        return isWaitingForLight;
    }
    // 取得行人是否正在排隊等待前方行人移動
    public bool IsWaitingInPedestrianQueue()
    {
        return isWaitingForLight || isQueuedBehindPedestrian;
    }
    private Vector3 lastPosition;
    private float actualSpeed = 0f;

    public float GetActualSpeed()
    {
        return actualSpeed;
    }
    private enum RoamState { OnPath, FreeRoam, Chatting, DoingAction }
    private RoamState roamState = RoamState.OnPath;

    private bool IsFreeRoaming => roamState == RoamState.FreeRoam || roamState == RoamState.Chatting || roamState == RoamState.DoingAction;

    private Animator anim;
    // 建立行人預看路徑
    private void BuildPlannedPath()
    {
        if (currentTargetNode == null)
            return;

        plannedNodes = new PedestrianNode[pathLookAheadCount + 1];

        // 第 0 個一定是目前目標
        plannedNodes[0] = currentTargetNode;

        // 往後預先抽 3 個
        PedestrianNode node = currentTargetNode;

        for (int i = 1; i < plannedNodes.Length; i++)
        {
            if (node == null)
            {
                plannedNodes[i] = null;
                continue;
            }

            node = node.GetRandomNextNode();
            plannedNodes[i] = node;
        }
    }

    [Header("=== 被玩家撞到(警示) ===")]
    [Tooltip("關掉＝玩家輾過去不會有反應")]
    public bool detectPlayerHit = true;
    [Tooltip("玩家車中心離行人多近算撞到(公尺)")]
    public float playerHitRadius = 1.2f;
    [Tooltip("要偵測的層。玩家車的層要包含")]
    public LayerMask playerHitLayer = ~0;
    [Tooltip("撞到時顯示的警示文字")]
    public string playerHitMessage = "注意行人！";
    public float playerHitMessageSeconds = 2.5f;
    [Tooltip("同一次撞不重複跳的冷卻秒數")]
    public float playerHitCooldown = 3f;
    private float lastPlayerHitTime = -999f;

    /// <summary>玩家車壓到背景行人 → 透過危險感知跳紅字警示(只警示,不影響行人自身移動)。</summary>
    private void CheckPlayerHit()
    {
        if (!detectPlayerHit) return;
        if (Time.time - lastPlayerHitTime < playerHitCooldown) return;

        Collider[] hits = Physics.OverlapSphere(transform.position + Vector3.up * 0.8f,
                                                playerHitRadius, playerHitLayer, QueryTriggerInteraction.Ignore);
        foreach (var c in hits)
        {
            if (c == null) continue;
            Rigidbody rb = c.attachedRigidbody;
            bool isPlayer = (rb != null && rb.CompareTag("Player")) || c.CompareTag("Player");
            if (!isPlayer) continue;

            lastPlayerHitTime = Time.time;
            float speed = rb != null ? rb.velocity.magnitude : 0f;
            if (HazardAwarenessTracker.Instance != null)
                HazardAwarenessTracker.Instance.ShowMessage(
                    playerHitMessage, new Color(1f, 0.25f, 0.2f), playerHitMessageSeconds);
            Debug.LogWarning($"<color=orange>💥 [背景行人] {name} 被玩家撞到" +
                             $"(車速 {speed * 3.6f:F0} km/h)</color>", this);
            return;
        }
    }

    void Start()
    {
        currentMoveSpeed = walkSpeed;
        lastPosition = transform.position;
        anim = GetComponentInChildren<Animator>();

        if (cellPhoneObject != null) cellPhoneObject.SetActive(false);
        if (playPhoneObject != null) playPhoneObject.SetActive(false);

        if (currentTargetNode == null)
        {
            FindNearestNode();
        }
        // 初始化未來路徑
        BuildPlannedPath();
    }

    void Update()
    {
        actualSpeed = Vector3.Distance(transform.position, lastPosition) /
                  Mathf.Max(Time.deltaTime, 0.0001f);

        lastPosition = transform.position;

        CheckPlayerHit();   // 🚶 玩家車撞到背景行人 → 跳警示

        if (IsFreeRoaming)
        {
            float animSpeed = actualSpeed;

            if (roamState == RoamState.Chatting ||
                roamState == RoamState.DoingAction)
            {
                animSpeed = 0f;
            }

            UpdateAnimation(animSpeed);
            return;
        }

        if (currentTargetNode == null) return;

        UpdateSpeedState();
        avoidanceDir = CheckObstacleInFront();

        float distance = Vector3.Distance(transform.position, currentTargetNode.transform.position);

        // 1. 條件判定：從一般道路準備進入閃黃燈路口，且距離到達停等範圍時
        bool isApproachingBlinkSystem = (previousNode == null || previousNode.intersectionType == IntersectionType.None) &&
                                        (currentTargetNode.intersectionType == IntersectionType.BlinkSystem) &&
                                        !blinkCrossingCommitted;

        if (isApproachingBlinkSystem && distance <= blinkStopDistance)
        {
            isBlinkCheckingCar = true;
            isWaitingForLight = true;
        }

        // 2. 到達 Node 的判定
        if (distance <= reachDistance)
        {
            SetNextNode();
        }

        // 3. 停等邏輯與左側來車檢查
        if (isWaitingForLight)
        {
            if (currentTargetNode != null)
            {
                if (currentTargetNode.intersectionType == IntersectionType.BlinkSystem)
                {
                    if (isBlinkCheckingCar)
                    {
                        // 累積觀察計時器
                        blinkCheckTimer += Time.deltaTime;

                        // 必須停滿 1.5 秒觀察時間，且「期間完全沒有車輛靠近」才放行
                        if (blinkCheckTimer >= blinkCheckDuration)
                        {
                            if (CanPedestrianCross())
                            {
                                isBlinkCheckingCar = false;
                                blinkCrossingCommitted = true;
                                isWaitingForLight = false;
                                blinkCheckTimer = 0f;
                            }
                        }
                    }
                }
                else
                {
                    // 一般號誌邏輯
                    if (currentTargetNode.CanCross() && currentTargetNode.GetRemainingGreenTime() > 5.0f)
                    {
                        isWaitingForLight = false;
                        AdvanceToNextNode();
                    }
                }
            }
        }

        // 4. 動態控制移動與動畫
        if (isWaitingForLight || isBlockedByFrontVehicle || isQueuedBehindPedestrian)
        {
            UpdateAnimation(0f); // 紅綠燈等待，或正前方偵測到車，都停止
        }
        else
        {
            MoveTowardsNode();
            UpdateAnimation(currentMoveSpeed);
        }
    }
    /// <summary>
    /// 偵測行人左側區域有無來車（以行人自身位置與面向為基準）
    /// </summary>
    private bool IsCarInBlinkDetectionBox()
    {
        blinkCarDetected = false;

        if (vehicleLayer.value == 0)
            return false;

        // 【修改點】以行人自身的 transform 計算中心點與旋轉
        Vector3 boxCenter = transform.position + transform.rotation * blinkCarBoxOffset;

        Collider[] cars = Physics.OverlapBox(boxCenter, blinkCarBoxSize * 0.5f, transform.rotation, vehicleLayer);

        blinkCarDetected = cars.Length > 0;
        return blinkCarDetected;
    }

    private bool CanPedestrianCross()
    {
        if (currentTargetNode == null) return false;

        if (currentTargetNode.intersectionType == IntersectionType.None) return true;

        if (currentTargetNode.intersectionType == IntersectionType.BlinkSystem)
        {
            return !IsCarInBlinkDetectionBox();
        }

        return currentTargetNode.CanCross() && currentTargetNode.GetRemainingGreenTime() > 5f;
    }

    /// <summary>
    /// 🚶 把行人回收到指定起點,重設路徑與路口狀態,繼續走(給 PedestrianSpawner 用)。
    /// 取代「走到終點就 Destroy」—— 同一個行人循環利用,不必 Instantiate、人口恆定。
    /// </summary>
    public void RespawnAt(PedestrianNode spawnNode)
    {
        if (spawnNode == null) return;

        transform.position = spawnNode.transform.position;
        previousNode = spawnNode;
        currentTargetNode = spawnNode.GetRandomNextNode();
        if (currentTargetNode == null) currentTargetNode = spawnNode;   // 沒有下一點就先待著

        // 面向下一個目標,免得回收後先原地轉身
        Vector3 dir = currentTargetNode.transform.position - transform.position; dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

        // 清掉上一段帶來的路口/閃燈旗標
        blinkCrossingCommitted = false;
        isBlinkCheckingCar = false;
        isWaitingForLight = false;
        currentMoveSpeed = walkSpeed;
        lastPosition = transform.position;

        BuildPlannedPath();
    }

    void SetNextNode()
    {
        // 優先檢查：如果到達的當前目標點是消失點
        if (currentTargetNode != null && currentTargetNode.isDespawnNode)
        {
            // 🚶 有行人生成器就「回收送回起點」繼續走(活城市,人口恆定);沒有就照舊銷毀
            if (PedestrianSpawner.Instance != null && PedestrianSpawner.Instance.TryRecycle(this))
                return;
            Destroy(gameObject);
            return;
        }

        bool isAtCrosswalkStart = (currentTargetNode.intersectionType != IntersectionType.None) &&
                                  (previousNode == null || previousNode.intersectionType == IntersectionType.None);

        if (isAtCrosswalkStart)
        {
            if (currentTargetNode.intersectionType == IntersectionType.BlinkSystem)
            {
                if (blinkCrossingCommitted)
                {
                    isWaitingForLight = false;
                    AdvanceToNextNode();
                    return;
                }

                if (!CanPedestrianCross())
                {
                    isBlinkCheckingCar = true;
                    isWaitingForLight = true;
                    return;
                }

                blinkCrossingCommitted = true;
                isBlinkCheckingCar = false;
                isWaitingForLight = false;
                AdvanceToNextNode();
                return;
            }

            float remainingTime = currentTargetNode.GetRemainingGreenTime();
            if (!currentTargetNode.CanCross() || remainingTime <= 5.0f)
            {
                isWaitingForLight = true;
                return;
            }
        }

        isWaitingForLight = false;
        AdvanceToNextNode();
    }

    void AdvanceToNextNode()
    {
        PedestrianNode oldNode = currentTargetNode;
        PedestrianNode nextNode = plannedNodes[1]; // 已經提前抽好的下一個 Node
        
        if (nextNode == null)
            return;

        previousNode = currentTargetNode;
        currentTargetNode = nextNode;

        // 預看路徑往前移
        plannedNodes[0] = plannedNodes[1];
        plannedNodes[1] = plannedNodes[2];
        plannedNodes[2] = plannedNodes[3];

        // 最後一格才抽新的
        plannedNodes[3] = plannedNodes[2] != null ? plannedNodes[2].GetRandomNextNode() : null;

        // 離開閃黃燈區域時重置所有檢查狀態
        if (oldNode != null && oldNode.intersectionType == IntersectionType.BlinkSystem &&
            nextNode.intersectionType != IntersectionType.BlinkSystem)
        {
            blinkCrossingCommitted = false;
            isBlinkCheckingCar = false;
            blinkCarDetected = false;
            blinkCheckTimer = 0f;
        }
    }
    // 判斷兩個行人是否有相同的未來路徑
    private bool IsSameFuturePath(PedestrianWalker other)
    {
        if (other == null)
            return false;

        if (plannedNodes == null || other.plannedNodes == null)
            return false;

        int sameNodeCount = 0;

        for (int i = 0; i < plannedNodes.Length; i++)
        {
            if (plannedNodes[i] == null)
                continue;

            for (int j = 0; j < other.plannedNodes.Length; j++)
            {
                if (other.plannedNodes[j] == null)
                    continue;

                if (plannedNodes[i] == other.plannedNodes[j])
                {
                    sameNodeCount++;
                    break;
                }
            }
        }

        // 至少有 2 個共同 Node，才算同一路徑
        return sameNodeCount >= 2;
    }

    /// <summary>
    /// 偵測前方障礙物並回傳避障轉向向量
    /// </summary>
    Vector3 CheckObstacleInFront()
    {
        // 中央行人偵測球
        Vector3 centerOrigin =transform.position + transform.rotation * centerPedSphereOffset;
        Vector3 centerDir = Quaternion.Euler(0f, centerPedSphereAngle, 0f) *transform.forward;
        // 左右行人偵測球的位置
        Vector3 leftOrigin = transform.position + transform.rotation * leftPedSphereOffset;
        Vector3 rightOrigin = transform.position + transform.rotation * rightPedSphereOffset;
        // 左右行人偵測球的角度
        Vector3 leftDir = Quaternion.Euler(0f, leftPedSphereAngle, 0f) * transform.forward;
        Vector3 rightDir = Quaternion.Euler(0f, rightPedSphereAngle, 0f) *transform.forward;
        // 三顆行人 SphereCast
        bool hitCenter = Physics.SphereCast( centerOrigin, obstacleCheckRadius, centerDir, out RaycastHit hitC, obstacleCheckDistance, pedestrianLayer ) && hitC.transform != transform;
        bool hitLeft = Physics.SphereCast( leftOrigin, obstacleCheckRadius,leftDir, out RaycastHit hitL, sideObstacleCheckDistance, pedestrianLayer ) && hitL.transform != transform;
        bool hitRight = Physics.SphereCast(rightOrigin, obstacleCheckRadius, rightDir, out RaycastHit hitR, sideObstacleCheckDistance, pedestrianLayer ) && hitR.transform != transform;
        // 是否有前方行人
        isBlockedByFrontPerson = hitCenter || hitLeft || hitRight;

        // 車輛偵測
        Vector3 vehicleOrigin = transform.position + Vector3.up * 1.0f;
        // 正中間額外偵測車輛
        isBlockedByFrontVehicle = Physics.SphereCast(vehicleOrigin, obstacleCheckRadius, transform.forward, out RaycastHit vehicleHit, obstacleCheckDistance, vehicleLayer);
        // 判斷前方行人是否正在等紅綠燈
        isQueuedBehindPedestrian = false;
        isFollowingMovingPedestrian = false;

        RaycastHit[] pedestrianHits = new RaycastHit[]
        { hitC, hitL, hitR };

        bool[] pedestrianHitStates = new bool[]
        { hitCenter, hitLeft, hitRight };

        PedestrianWalker followingPedestrian = null;
        float closestFollowDistance = Mathf.Infinity;

        for (int i = 0; i < pedestrianHits.Length; i++)
        {
            if (!pedestrianHitStates[i])
                continue;

            PedestrianWalker frontPedestrian =
                pedestrianHits[i].collider.GetComponentInParent<PedestrianWalker>();

            if (frontPedestrian == null || frontPedestrian == this)
                continue;

            // 前方行人正在等紅綠燈，或本身就是排隊中的行人
            if (frontPedestrian.IsWaitingInPedestrianQueue())
            {
                isQueuedBehindPedestrian = true;
                break;
            }

            // 判斷未來 4 個 Node 是否屬於同一路徑
            bool sameFuturePath = IsSameFuturePath(frontPedestrian);

            // =========================================================
            // 同一路徑
            // =========================================================
            if (sameFuturePath)
            {
                // 判斷對方是不是在自己的前方
                Vector3 toFront =
                    frontPedestrian.transform.position - transform.position;

                toFront.y = 0f;

                float forwardDot = 0f;

                if (toFront.sqrMagnitude > 0.0001f)
                {
                    forwardDot = Vector3.Dot(
                        transform.forward.normalized,
                        toFront.normalized
                    );
                }

                // 對方確實在自己前方
                if (forwardDot > 0.5f)
                {
                    float samePathSpeed =
                        frontPedestrian.GetActualSpeed();

                    // 對方正在移動 → 跟隨
                    if (samePathSpeed > pedestrianMovingSpeedThreshold)
                    {
                        isFollowingMovingPedestrian = true;

                        float samePathDistance =
                            Vector3.Distance(
                                transform.position,
                                frontPedestrian.transform.position
                            );

                        if (samePathDistance < closestFollowDistance)
                        {
                            closestFollowDistance = samePathDistance;
                            followingPedestrian = frontPedestrian;
                        }
                    }
                    else
                    {
                        // 同一路徑，但前方行人停下
                        isQueuedBehindPedestrian = true;
                    }
                }

                // 同一路徑但對方在側面
                // 不要觸發左右閃避
                continue;
            }

            // =========================================================
            // 不同未來路徑
            // 才進入一般方向判斷
            // =========================================================

            float directionDot = Vector3.Dot(
                transform.forward.normalized,
                frontPedestrian.transform.forward.normalized
            );

            float differentPathSpeed =
                frontPedestrian.GetActualSpeed();

            // 對方正在移動
            if (differentPathSpeed > pedestrianMovingSpeedThreshold)
            {
                // 同方向，但不是同一路徑
                if (directionDot >= sameDirectionThreshold)
                {
                    isFollowingMovingPedestrian = true;

                    float differentPathDistance =
                        Vector3.Distance(
                            transform.position,
                            frontPedestrian.transform.position
                        );

                    if (differentPathDistance < closestFollowDistance)
                    {
                        closestFollowDistance = differentPathDistance;
                        followingPedestrian = frontPedestrian;
                    }
                }
            }
        }
        // 如果前方有人正在等紅綠燈就不要左右閃避，直接排隊
        if (isQueuedBehindPedestrian)
        {
            return Vector3.zero;
        }
        // 前方是「同方向正在走的行人」 不要左右亂閃，改成跟隨速度
        if (isFollowingMovingPedestrian && followingPedestrian != null)
        {
            float frontSpeed = followingPedestrian.GetActualSpeed();

            // 已經太接近前方行人
            if (closestFollowDistance <= pedestrianFollowStopDistance)
            {
                currentMoveSpeed = 0f;
            }
            else
            {
                // 自己不能比前面的人走得更快
                currentMoveSpeed =
                    Mathf.Min(currentMoveSpeed, frontSpeed);
            }

            return Vector3.zero;
        }
        Vector3 steer = Vector3.zero;

        if (hitLeft) steer += transform.right;
        if (hitRight) steer -= transform.right;
        // 中央偵測到行人，而且左右沒有偵測到
        if (hitCenter && steer == Vector3.zero)
        {
            steer = (GetInstanceID() % 2 == 0) ? transform.right : -transform.right;
        }
        // 如果左右都有行人就隨機選擇一邊
        if (hitLeft && hitRight)
        {
            steer = (GetInstanceID() % 2 == 0) ? transform.right : -transform.right;
        }
        

        return steer.normalized;
    }
    private Vector3 CheckFreeZoneObstacle()
    {
        Vector3 centerOrigin =
            transform.position +
            transform.rotation * centerPedSphereOffset;

        Vector3 leftOrigin =
            transform.position +
            transform.rotation * leftPedSphereOffset;

        Vector3 rightOrigin =
            transform.position +
            transform.rotation * rightPedSphereOffset;

        Vector3 centerDir = transform.forward;

        Vector3 leftDir =
            Quaternion.Euler(0f, leftPedSphereAngle, 0f) *
            transform.forward;

        Vector3 rightDir =
            Quaternion.Euler(0f, rightPedSphereAngle, 0f) *
            transform.forward;

        bool hitCenter =
            Physics.SphereCast(
                centerOrigin,
                obstacleCheckRadius,
                centerDir,
                out RaycastHit hitC,
                obstacleCheckDistance,
                pedestrianLayer
            ) &&
            hitC.transform != transform;

        bool hitLeft =
            Physics.SphereCast(
                leftOrigin,
                obstacleCheckRadius,
                leftDir,
                out RaycastHit hitL,
                sideObstacleCheckDistance,
                pedestrianLayer
            ) &&
            hitL.transform != transform;

        bool hitRight =
            Physics.SphereCast(
                rightOrigin,
                obstacleCheckRadius,
                rightDir,
                out RaycastHit hitR,
                sideObstacleCheckDistance,
                pedestrianLayer
            ) &&
            hitR.transform != transform;

        // 沒有人
        if (!hitCenter && !hitLeft && !hitRight)
        {
            return Vector3.zero;
        }

        // 左右都有障礙 → 停下來
        if (hitLeft && hitRight)
        {
            return Vector3.zero;
        }

        // 左邊有人 → 往右閃
        if (hitLeft)
        {
            return transform.right;
        }

        // 右邊有人 → 往左閃
        if (hitRight)
        {
            return -transform.right;
        }

        // 只有正前方有人
        if (hitCenter)
        {
            // 讓不同的人選擇不同方向，避免大家全部往同一邊
            return (GetInstanceID() % 2 == 0)
                ? transform.right
                : -transform.right;
        }

        return Vector3.zero;
    }
    void UpdateSpeedState()
    {
        bool isCrossingStreet = (previousNode != null && previousNode.intersectionType != IntersectionType.None) &&
                                (currentTargetNode != null && currentTargetNode.intersectionType != IntersectionType.None);

        if (isCrossingStreet)
        {
            float remainingTime = previousNode.GetRemainingGreenTime();
            bool canCross = previousNode.CanCross();

            if (!canCross || (remainingTime > 0 && remainingTime <= 5.0f))
            {
                currentMoveSpeed = runSpeed;
                return;
            }
        }

        currentMoveSpeed = walkSpeed;
    }

    void MoveTowardsNode()
    {
        Vector3 targetPosition = currentTargetNode.transform.position;
        Vector3 targetDirection = (targetPosition - transform.position).normalized;

        Vector3 finalDirection = (targetDirection + avoidanceDir * 1.2f).normalized;

        if (finalDirection != Vector3.zero)
        {
            Quaternion targetRotation = Quaternion.LookRotation(finalDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }

        transform.position += transform.forward * currentMoveSpeed * Time.deltaTime;
    }

    void UpdateAnimation(float speed)
    {
        if (anim != null)
        {
            // ★ 移動全部交給程式控制
            anim.applyRootMotion = false;

            float currentAnimSpeed = anim.GetFloat("Speed");

            float smoothedSpeed =
                Mathf.Lerp(
                    currentAnimSpeed,
                    speed,
                    Time.deltaTime * 10f
                );

            anim.SetFloat("Speed", smoothedSpeed);
        }
    }

    void FindNearestNode()
    {
        PedestrianNode[] allNodes = FindObjectsOfType<PedestrianNode>();
        float closestDistance = Mathf.Infinity;
        PedestrianNode closestNode = null;

        foreach (var node in allNodes)
        {
            float dist = Vector3.Distance(transform.position, node.transform.position);
            if (dist < closestDistance)
            {
                closestDistance = dist;
                closestNode = node;
            }
        }
        currentTargetNode = closestNode;
        previousNode = null;

        BuildPlannedPath();  // 重新建立未來路徑
    }

    private void OnTriggerEnter(Collider other)
    {
        if (roamState != RoamState.OnPath) return;

        if (other.TryGetComponent<PedestrianFreeZone>(out var zone))
        {
            currentZone = zone;
            roamState = RoamState.FreeRoam;
            StopAllCoroutinesSafely();
            wanderRoutine = StartCoroutine(WanderInZone());
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (currentZone == null || other.gameObject != currentZone.gameObject) return;

        Collider[] hitColliders = Physics.OverlapSphere(transform.position, 0.5f);
        PedestrianFreeZone nextZone = null;

        foreach (var hit in hitColliders)
        {
            if (hit.gameObject != other.gameObject && hit.TryGetComponent<PedestrianFreeZone>(out var zone))
            {
                nextZone = zone;
                break;
            }
        }
        if (nextZone != null)
        {
            currentZone = nextZone;
        }
        else
        {
            StopAllCoroutinesSafely();
            currentZone = null;
            roamState = RoamState.OnPath;
            FindNearestNode();
        }
    }

    private void StopAllCoroutinesSafely()
    {
        if (wanderRoutine != null) { StopCoroutine(wanderRoutine); wanderRoutine = null; }
        if (moveRoutine != null) { StopCoroutine(moveRoutine); moveRoutine = null; }
    }

    private IEnumerator WanderInZone()
    {
        while (roamState == RoamState.FreeRoam && currentZone != null)
        {
            // =========================
            // 1. 隨機走到一個位置
            // =========================
            Vector3 target = currentZone.GetRandomPoint();

            moveRoutine = StartCoroutine(MoveToPoint(target));
            yield return moveRoutine;

            if (roamState != RoamState.FreeRoam)
                yield break;


            // =========================
            // 2. 隨機抽一個動作
            // =========================
            string selectedAction = GetRandomAction();

            if (anim != null && !string.IsNullOrEmpty(selectedAction))
            {
                StartAction(selectedAction);
                yield break;
            }


            // =========================
            // 3. 沒抽到動作 → 原地休息
            // =========================
            float waitTime = Random.Range(
                wanderPauseRange.x,
                wanderPauseRange.y
            );

            float pauseTimer = 0f;
            Vector3 pausePosition = transform.position;

            while (pauseTimer < waitTime &&
                   roamState == RoamState.FreeRoam)
            {
                // 固定在原地
                transform.position = pausePosition;

                pauseTimer += Time.deltaTime;

                if (chatCooldown > 0f)
                    chatCooldown -= Time.deltaTime;

                TryTriggerChat();

                yield return null;
            }
        }
    }

    private IEnumerator MoveToPoint(Vector3 targetPosition)
    {
        while (roamState == RoamState.FreeRoam)
        {
            if (chatCooldown > 0f)
                chatCooldown -= Time.deltaTime;

            TryTriggerChat();

            if (roamState != RoamState.FreeRoam)
                yield break;

            targetPosition.y = transform.position.y;

            Vector3 targetDirection =
                targetPosition - transform.position;

            float distance = targetDirection.magnitude;

            if (distance <= reachDistance)
                yield break;

            targetDirection.Normalize();

            // =====================================================
            // 自由區避障鎖定計時
            // =====================================================
            if (isFreeZoneAvoiding)
            {
                freeZoneAvoidanceTimer -= Time.deltaTime;

                // 時間到了，而且前方已經沒有障礙
                if (freeZoneAvoidanceTimer <= 0f)
                {
                    Vector3 checkAgain = CheckFreeZoneObstacle();

                    if (checkAgain == Vector3.zero)
                    {
                        isFreeZoneAvoiding = false;
                        freeZoneAvoidanceDir = Vector3.zero;
                    }
                    else
                    {
                        // 還有人，繼續保持原本的閃避方向
                        freeZoneAvoidanceTimer =
                            freeZoneAvoidanceDuration;
                    }
                }
            }

            // =====================================================
            // 還沒有進入避障狀態 → 偵測一次
            // =====================================================
            if (!isFreeZoneAvoiding)
            {
                Vector3 detectedAvoidance =
                    CheckFreeZoneObstacle();

                if (detectedAvoidance != Vector3.zero)
                {
                    isFreeZoneAvoiding = true;

                    // 記住這次決定的方向
                    freeZoneAvoidanceDir =
                        detectedAvoidance.normalized;

                    freeZoneAvoidanceTimer =
                        freeZoneAvoidanceDuration;
                }
            }

            // =====================================================
            // 決定最後移動方向
            // =====================================================
            Vector3 finalDirection;

            if (isFreeZoneAvoiding &&
                freeZoneAvoidanceDir != Vector3.zero)
            {
                // 避障期間：
                // 80% 往閃避方向
                // 20% 保留原本目標方向
                finalDirection =
                    (
                        targetDirection * (1f - freeZoneAvoidanceWeight) +
                        freeZoneAvoidanceDir * freeZoneAvoidanceWeight
                    ).normalized;
            }
            else
            {
                // 沒有人 → 正常往隨機目標
                finalDirection = targetDirection;
            }

            // =====================================================
            // 旋轉
            // =====================================================
            if (finalDirection != Vector3.zero)
            {
                Quaternion targetRotation =
                    Quaternion.LookRotation(finalDirection);

                transform.rotation =
                    Quaternion.Slerp(
                        transform.rotation,
                        targetRotation,
                        rotationSpeed * Time.deltaTime
                    );
            }

            // =====================================================
            // 移動
            // =====================================================
            transform.position +=
                finalDirection * walkSpeed * Time.deltaTime;

            yield return null;
        }

        // 離開移動時清除避障狀態
        isFreeZoneAvoiding = false;
        freeZoneAvoidanceDir = Vector3.zero;
        freeZoneAvoidanceTimer = 0f;
    }

    private void StartAction(string triggerName)
    {
        StopAllCoroutinesSafely();
        roamState = RoamState.DoingAction;
        if (anim != null) anim.SetTrigger(triggerName);

        // 依照這次觸發的動作名稱，開對應的手機
        if (cellPhoneObject != null) cellPhoneObject.SetActive(triggerName == "CellPhone");
        if (playPhoneObject != null) playPhoneObject.SetActive(triggerName == "PlayPhone");

        StartCoroutine(ActionRoutine(triggerName));
    }
    public class PedestrianAction
    {
        public string actionName;
        [Range(0f, 100f)]
        public float probability;

        public PedestrianAction(string name, float prob)
        {
            actionName = name;
            probability = prob;
        }
    }
    private List<PedestrianAction> actions = new List<PedestrianAction>()
    {
        new PedestrianAction("PlayPhone", 15f),
        new PedestrianAction("CellPhone", 15f),
        new PedestrianAction("Warrior Idle", 15f),
        new PedestrianAction("Stretch", 15f)
    };
    private string GetRandomAction()
    {
        // 直接抽 0 ~ 100
        float randomValue = Random.Range(0f, 100f);

        float current = 0f;

        foreach (PedestrianAction action in actions)
        {
            current += action.probability;

            if (randomValue < current)
            {
                return action.actionName;
            }
        }

        // 沒抽到任何動作
        // 剩下的百分比就是「不做動作」
        return "";
    }
    private IEnumerator ActionRoutine(string triggerName)
    {
        Vector3 lockedPosition = transform.position;

        float enterTimer = 0f;
        const float enterTimeout = 3f;
        bool hasEnteredAction = false;

        while (enterTimer < enterTimeout)
        {
            transform.position = lockedPosition;

            if (anim != null)
            {
                anim.applyRootMotion = false;
                AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);

                if (state.IsTag(triggerName))
                {
                    hasEnteredAction = true;
                    break;
                }
            }

            enterTimer += Time.deltaTime;
            yield return null;
        }

        float safetyTimer = 0f;
        const float maxActionTime = 60f;

        while (roamState == RoamState.DoingAction)
        {
            if (anim != null) anim.applyRootMotion = false;
            transform.position = lockedPosition;
            UpdateAnimation(0f);

            if (anim != null && hasEnteredAction)
            {
                AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);

                if (state.IsTag(triggerName) && state.normalizedTime >= 1f) break;
                if (!state.IsTag(triggerName) && !anim.IsInTransition(0)) break;
            }

            safetyTimer += Time.deltaTime;
            if (safetyTimer >= maxActionTime) break;

            yield return null;
        }

        transform.position = lockedPosition;

        // 動作結束，兩支手機都收起來
        if (cellPhoneObject != null) cellPhoneObject.SetActive(false);
        if (playPhoneObject != null) playPhoneObject.SetActive(false);

        if (currentZone != null)
        {
            roamState = RoamState.FreeRoam;
            wanderRoutine = StartCoroutine(WanderInZone());
        }
        else
        {
            roamState = RoamState.OnPath;
            FindNearestNode();
        }
    }
    private PedestrianWalker lastChatCandidate = null;
    private void TryTriggerChat()
    {
        if (roamState != RoamState.FreeRoam) return;
        if (chatCooldown > 0f) return;

        Collider[] nearby =Physics.OverlapSphere( transform.position, chatDetectRadius, pedestrianLayer);

        PedestrianWalker candidate = null;
        float closestDistance = Mathf.Infinity;

        foreach (var c in nearby)
        {
            if (c.transform == transform) continue;

            PedestrianWalker other = c.GetComponentInParent<PedestrianWalker>();
            if (other == null || other == this) continue;

            if (other.roamState != RoamState.FreeRoam) continue;
            if (other.chatCooldown > 0f) continue;

            float distance =
            Vector3.Distance( transform.position, other.transform.position);

            if (distance < closestDistance)
            {
                closestDistance = distance;
                candidate = other;
            }
        }

        // 附近沒有人
        if (candidate == null)
        {
            lastChatCandidate = null;
            return;
        }

        // 還是同一個人，不要每幀重新抽機率
        if (candidate == lastChatCandidate)
            return;

        // 記住這次遇到的人
        lastChatCandidate = candidate;

        // ==========================================
        // 真正的「遇到人 → X% 機率聊天」
        // ==========================================
        if (Random.Range(0f, 100f) >= chatChance)
            return;

        // 決定這次是不是生氣聊天
        bool isAngry = Random.Range(0f, 100f) < angryChatChance;

        StartChat(candidate, isAngry);
        candidate.StartChat(this, isAngry);
    }

    private void StartChat(PedestrianWalker partner, bool isAngry)
    {
        StopAllCoroutinesSafely();

        roamState = RoamState.Chatting;

        if (anim != null)
        {
            anim.SetTrigger(isAngry ? "ChatAngry" : "ChatNormal");
        }

        StartCoroutine(ChatRoutine(partner, isAngry ? "ChatAngry" : "ChatNormal"));
    }

    private IEnumerator ChatRoutine(PedestrianWalker partner, string triggerName)
    {
        Vector3 lockedPosition = transform.position;

        Vector3 lookDir = partner.transform.position - transform.position;
        lookDir.y = 0f;
        if (lookDir != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(lookDir);
        }

        // 等進入正確的聊天動畫狀態
        float enterTimer = 0f;
        const float enterTimeout = 3f;
        bool hasEnteredChat = false;

        while (enterTimer < enterTimeout)
        {
            transform.position = lockedPosition;

            if (anim != null)
            {
                anim.applyRootMotion = false;
                AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);

                if (state.IsTag(triggerName))
                {
                    hasEnteredChat = true;
                    break;
                }
            }

            enterTimer += Time.deltaTime;
            yield return null;
        }

        // 等動畫真正播完才收尾，不再用寫死的秒數
        float safetyTimer = 0f;
        const float maxChatTime = 30f;

        while (roamState == RoamState.Chatting)
        {
            if (anim != null) anim.applyRootMotion = false;
            transform.position = lockedPosition;
            UpdateAnimation(0f);

            if (anim != null && hasEnteredChat)
            {
                AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);

                if (state.IsTag(triggerName) && state.normalizedTime >= 1f) break;
                if (!state.IsTag(triggerName) && !anim.IsInTransition(0)) break;
            }

            safetyTimer += Time.deltaTime;
            if (safetyTimer >= maxChatTime) break;

            yield return null;
        }

        transform.position = lockedPosition;
        chatCooldown = chatCooldownDuration;

        if (currentZone != null)
        {
            roamState = RoamState.FreeRoam;
            wanderRoutine = StartCoroutine(WanderInZone());
        }
        else
        {
            roamState = RoamState.OnPath;
            FindNearestNode();
        }
    }

    private void OnDrawGizmos()
    {
        // =========================
        // 中央
        // =========================
        Vector3 centerOrigin =
            transform.position +
            transform.rotation * centerPedSphereOffset;

        Vector3 centerDir =
            Quaternion.Euler(0f, centerPedSphereAngle, 0f) *
            transform.forward;


        // =========================
        // 左邊
        // =========================
        Vector3 leftOrigin =
            transform.position +
            transform.rotation * leftPedSphereOffset;

        Vector3 leftDir =
            Quaternion.Euler(0f, leftPedSphereAngle, 0f) *
            transform.forward;


        // =========================
        // 右邊
        // =========================
        Vector3 rightOrigin =
            transform.position +
            transform.rotation * rightPedSphereOffset;

        Vector3 rightDir =
            Quaternion.Euler(0f, rightPedSphereAngle, 0f) *
            transform.forward;


        // =========================
        // 行人偵測球
        // =========================
        Gizmos.color =
            isBlockedByFrontPerson
            ? Color.red
            : Color.green;

        // 中央
        DrawSphereCastGizmo( centerOrigin, centerDir, obstacleCheckDistance, obstacleCheckRadius);

        // 左
        DrawSphereCastGizmo(
            leftOrigin,
            leftDir,
            sideObstacleCheckDistance,
            obstacleCheckRadius
        );

        // 右
        DrawSphereCastGizmo(
            rightOrigin,
            rightDir,
            sideObstacleCheckDistance,
            obstacleCheckRadius
        );


        // =========================
        // 車輛中央偵測球
        // =========================
        Vector3 vehicleOrigin =
            transform.position + Vector3.up * 1.0f;

        Gizmos.color =
            isBlockedByFrontVehicle
            ? Color.red
            : Color.cyan;

        Gizmos.DrawWireSphere(
            vehicleOrigin +
            transform.forward * obstacleCheckDistance,
            obstacleCheckRadius
        );
    }
    private void DrawSphereCastGizmo(Vector3 origin, Vector3 direction, float distance, float radius)
    {
        direction.Normalize();

        // 起點球
        Gizmos.DrawWireSphere(origin, radius);

        // 終點球
        Gizmos.DrawWireSphere(
            origin + direction * distance,
            radius
        );

        // 連接球體外圍的線
        Vector3 right = Vector3.Cross(direction, Vector3.up).normalized;

        if (right == Vector3.zero)
            right = Vector3.Cross(direction, Vector3.forward).normalized;

        Vector3 up = Vector3.Cross(right, direction).normalized;

        Gizmos.DrawLine(
            origin + right * radius,
            origin + direction * distance + right * radius
        );

        Gizmos.DrawLine(
            origin - right * radius,
            origin + direction * distance - right * radius
        );

        Gizmos.DrawLine(
            origin + up * radius,
            origin + direction * distance + up * radius
        );

        Gizmos.DrawLine(
            origin - up * radius,
            origin + direction * distance - up * radius
        );
    }
}