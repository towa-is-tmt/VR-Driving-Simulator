using UnityEngine;

public class WaypointFollowerAI : MonoBehaviour
{
    [Header("路徑設定")]
    public Transform[] leftWaypoints;
    public Transform[] rightWaypoints;

    [Header("運動參數")]
    public float baseSpeed = 25f;
    public float fastSpeed = 55f;
    public float turnSpeed = 5f;

    private float currentMoveSpeed;
    private Transform[] currentPath;
    private int currentWaypointIndex = 0;
    private bool isMoving = false;
    private bool isWaiting = false; // 是否正在等紅燈

    void Start()
    {
        currentMoveSpeed = baseSpeed;
        // 壓低重心防止亂轉
        if (GetComponent<Rigidbody>() != null)
        {
            GetComponent<Rigidbody>().centerOfMass = new Vector3(0, -0.9f, 0);
        }
    }

    void Update()
    {
        if (!isMoving || currentPath == null || currentWaypointIndex >= currentPath.Length) return;

        float targetSpeed = baseSpeed;

        if (isWaiting)
        {
            // 紅黃燈：平滑減速至 0
            targetSpeed = 0;
            currentMoveSpeed = Mathf.Lerp(currentMoveSpeed, targetSpeed, Time.deltaTime * 5f);
            if (currentMoveSpeed < 0.1f) currentMoveSpeed = 0;
        }
        else
        {
            // 綠燈：恢復速度（若當前速度高於 baseSpeed，表示正在 Boost，Lerp 會平滑回歸）
            currentMoveSpeed = Mathf.Lerp(currentMoveSpeed, baseSpeed, Time.deltaTime * 2f);
        }

        // 只有速度夠快才執行移動邏輯
        if (currentMoveSpeed > 0.05f)
        {
            MoveLogic();
        }
    }

    void MoveLogic()
    {
        Transform target = currentPath[currentWaypointIndex];
        Vector3 direction = (target.position - transform.position);
        direction.y = 0;

        if (direction != Vector3.zero)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction), turnSpeed * Time.deltaTime);
        }
        transform.position = Vector3.MoveTowards(transform.position, target.position, currentMoveSpeed * Time.deltaTime);

        if (Vector3.Distance(transform.position, target.position) < 0.6f)
            currentWaypointIndex++;
    }

    // --- 外部接口 (包含修正 AITriggerZone 報錯的 Boost) ---
    public void Boost()
    {
        currentMoveSpeed = fastSpeed;
        isWaiting = false; // 加速時忽略目前的停車狀態
    }

    public void ActivateLeft() { StartRoute(leftWaypoints); }
    public void ActivateRight() { StartRoute(rightWaypoints); }

    private void StartRoute(Transform[] path)
    {
        if (isMoving) return;
        currentPath = path;
        currentWaypointIndex = 0;
        isMoving = true;
    }

    // 更新後的感應邏輯
    private void OnTriggerStay(Collider other)
    {
        // 強制印出所有撞到的東西，如果 Console 沒跑出這行，就是物理設定問題
        // UnityEngine.Debug.Log("正在接觸物件：" + other.name);

        if (other.CompareTag("TrafficSensor"))
        {
            // 嘗試從該物件或其父物件取得紅綠燈腳本
            SimpleTrafficLight lightSystem = other.GetComponentInParent<SimpleTrafficLight>();

            if (lightSystem != null)
            {
                isWaiting = lightSystem.isRedOrYellow;
                // 測試用印出：確認有沒有讀到紅綠燈狀態
                // UnityEngine.Debug.Log("讀取到燈號！目前是否停車：" + isWaiting);
            }
            else
            {
                UnityEngine.Debug.LogError("錯誤：在 " + other.name + " 身上或父物件找不到 SimpleTrafficLight 腳本！");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("TrafficSensor"))
        {
            isWaiting = false; // 離開感應區（過馬路後）恢復
        }
    }
}