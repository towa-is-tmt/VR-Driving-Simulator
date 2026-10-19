using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(Rigidbody))]
public class ParkingDetector : MonoBehaviour
{
    [Header("感測器（拖入四個 SphereCollider）")]
    public SphereCollider[] sensors;

    [Header("組件引用")]
    [Tooltip("檔位腳本")]
    public CarGearController gearController;

    [Header("偵測設定")]
    [Tooltip("靜止速度閾值")]
    [SerializeField] private float stopVelocityThreshold = 0.05f;
    [Tooltip("靜止角速度閾值")]
    [SerializeField] private float stopAngularThreshold  = 0.05f;
    [Tooltip("打P檔需靜止幾秒")]
    public float requiredStopDuration  = 3f;   // 改為 public，讓 UI 能讀取以顯示進度
    [Tooltip("進入停車格後的校正緩衝時間")]
    [SerializeField] private float correctionDelay       = 1.5f;
    [Tooltip("重生後要開多快(m/s)才算「動過」，超過才重新武裝")]
    [SerializeField] private float armMoveThreshold      = 1f;

    [Header("偵測類型開關（訓練只選一種時，另一種自動關）")]
    public bool detectSide    = true;   // 偵測路邊側停
    public bool detectReverse = true;   // 偵測倒車入庫

    [Header("事件")]
    public UnityEvent onReverseParkSuccess; // 倒車入庫成功
    public UnityEvent onSideParkSuccess;    // 路邊側停成功
    public UnityEvent onReverseParkFail;    // 倒車入庫失敗
    public UnityEvent onSideParkFail;       // 路邊側停失敗

    [Header("停好後又駛離的處理")]
    [Tooltip("勾選 = 停好通過後，若整台車又駛離車格，收回該項通過並判失敗（＝必須停在格內才算數）。\n" +
             "⚠️ 一台車跑多個關卡時：停好後開往下一關也會被收回。若你的流程是停好就換關，請保持取消勾選（永久保留）。\n" +
             "預設關閉：本專案是「停好就換下一關」的多關卡流程，開著會讓上一關在你開往下一關時被判失敗、連分數一起扣。")]
    public bool revokeOnLeave = false;

    [Header("即時狀態（Debug用）")]
    [Tooltip("P檔靜止計時")]
    public float stopTimer     = 0f;
    [Tooltip("P檔靜止是否完成")]
    public bool pGearConfirmed = false;

    private Rigidbody rb;
    private bool isInParkingZone  = false;
    private bool isCorrectionMode = false;
    private bool isEvaluating     = false;
    private bool armed            = true;   // false = 重生後尚未武裝，停車不算數
    private bool passedAwaitingLeave = false;  // 已通過、尚未離開；整台駛離時要收回
    private bool passedWasSide        = false; // 這次通過的是側停(true)還是倒車入庫(false)
    private string currentSpaceId = "";
    private Coroutine correctionCoroutine;

    private Dictionary<string, HashSet<string>> sensorsPerSpace = new Dictionary<string, HashSet<string>>();
    private HashSet<string> sensorsOnLine = new HashSet<string>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = GetComponentInParent<Rigidbody>();

        if (gearController == null)
            gearController = GetComponentInChildren<CarGearController>();
        if (gearController == null)
            gearController = GetComponentInParent<CarGearController>();
        if (gearController == null)
            Debug.LogError("[Parking] 找不到 CarGearController！");
    }

    // 重生/瞬移後呼叫：解除武裝，必須先開動過一次，停車才會重新計算
    public void DisarmUntilMoved()
    {
        armed        = false;
        stopTimer    = 0f;
        isEvaluating = false;
        Debug.Log("[Parking] 已解除武裝，需先開動才會重新偵測停車");
    }

    void FixedUpdate()
    {
        if (!isInParkingZone || isCorrectionMode || isEvaluating) return;
        if (gearController == null) return;

        // 重生後未武裝：先開動過(速度超過門檻)才恢復偵測
        if (!armed)
        {
            if (rb.velocity.magnitude > armMoveThreshold)
            {
                armed = true;
                Debug.Log("[Parking] 偵測到移動 → 重新武裝，停車開始計算");
            }
            else { stopTimer = 0f; return; }
        }

        bool isP       = gearController.currentGear == CarGearController.AutomaticGear.P;
        bool isStopped = rb.velocity.magnitude        < stopVelocityThreshold
                      && rb.angularVelocity.magnitude < stopAngularThreshold;

        if (isP && isStopped)
        {
            stopTimer += Time.fixedDeltaTime;
            Debug.Log($"[Parking] P檔靜止中：{stopTimer:F1}s / {requiredStopDuration}s");

            if (stopTimer >= requiredStopDuration)
            {
                pGearConfirmed = true;
                isEvaluating   = true;
                EvaluateParking();
            }
        }
        else
        {
            if (stopTimer > 0f)
                Debug.Log("[Parking] P檔靜止中斷，重置計時！");
            stopTimer = 0f;
        }
    }

    private void EvaluateParking()
    {
        string fullSpace = FindSpaceContainingAllSensors();

        // 判定是側停還是倒車入庫（用格子前綴）
        bool isSidePark = (fullSpace ?? currentSpaceId).Contains("路邊停車");

        // ★ 訓練沒選這種停車 → 完全忽略（不判成功也不判失敗）
        if (isSidePark && !detectSide)
        {
            Debug.Log("[Parking] 未啟用側停偵測，忽略此次停車");
            ResetState();
            return;
        }
        if (!isSidePark && !detectReverse)
        {
            Debug.Log("[Parking] 未啟用倒車入庫偵測，忽略此次停車");
            ResetState();
            return;
        }

        if (fullSpace == null)
        {
            Debug.Log("[Parking] 失敗：四輪不在同一個停車格內（車身跨格或未完全入格）");
            if (isSidePark) onSideParkFail?.Invoke();
            else            onReverseParkFail?.Invoke();
            ResetState();
            return;
        }

        currentSpaceId = fullSpace;
        Debug.Log($"[Parking] 四輪皆在同一格 '{fullSpace}'，開始壓線檢查");

        bool anyContact = sensorsOnLine.Count > 0;
        if (anyContact)
            Debug.Log($"[Parking] 仍有 sensor 壓線：{string.Join(", ", sensorsOnLine)}");

        if (anyContact)
        {
            Debug.Log("[Parking] 失敗：仍有碰線");
            if (isSidePark) onSideParkFail?.Invoke();
            else            onReverseParkFail?.Invoke();
            ResetState();
            return;
        }

        // ── 成功 ──────────────────────────────────────────────
        if (isSidePark)
        {
            Debug.Log($"[Parking] 路邊側停成功！SpaceId：'{currentSpaceId}'");
            onSideParkSuccess?.Invoke();
        }
        else
        {
            Debug.Log($"[Parking] 倒車入庫成功！SpaceId：'{currentSpaceId}'");
            onReverseParkSuccess?.Invoke();
        }

        if (revokeOnLeave)
        {
            // 不清空格內追蹤：維持在格內狀態，持續監看是否駛離。
            // 整台車離開時 OnSensorExitZone 會收回通過並判失敗。
            passedAwaitingLeave = true;
            passedWasSide       = isSidePark;
            isEvaluating        = true;   // 已判定，避免重複評分（但保留 sensorsPerSpace / isInParkingZone）
            Debug.Log("[Parking] 已通過，持續監看 —— 整台車駛離車格會收回此次通過");
        }
        else
        {
            ResetState();   // 舊行為：通過即永久保留
        }
    }

    private string FindSpaceContainingAllSensors()
    {
        // ⚠️ 先確認 sensors 陣列真的掛滿了。
        //    之前踩過的坑：四格全 {fileID:0}（沒掛感測器）時，下面的 null 略過會讓
        //    「四輪都在格內」空洞地成立 —— 只要任何一顆輪胎進到任一格就判成功，
        //    車子明顯超出格子也會過。所以沒掛滿一律無法判定（回 null → 上層判失敗）。
        int valid = 0;
        foreach (var s in sensors) if (s != null) valid++;
        if (sensors == null || sensors.Length == 0 || valid < sensors.Length)
        {
            Debug.LogError($"[Parking] sensors 沒掛滿（有效 {valid}/{(sensors == null ? 0 : sensors.Length)}）"
                         + " —— 無法判定停車，請把四顆輪胎 Sensor 拖進 ParkingDetector 的 Sensors 欄位。");
            return null;
        }

        foreach (var kv in sensorsPerSpace)
        {
            bool all = true;
            foreach (var sensor in sensors)
            {
                if (sensor == null) { all = false; break; }   // 保險：任何一顆缺失都不算「全部在格內」
                if (!kv.Value.Contains(sensor.gameObject.name)) { all = false; break; }
            }
            if (all) return kv.Key;
        }
        return null;
    }

    public void OnSensorEnterZone(string spaceId, string sensorName)
    {
        if (!sensorsPerSpace.ContainsKey(spaceId))
            sensorsPerSpace[spaceId] = new HashSet<string>();
        sensorsPerSpace[spaceId].Add(sensorName);

        if (!isInParkingZone)
        {
            isInParkingZone = true;
            isEvaluating    = false;
            pGearConfirmed  = false;
            stopTimer       = 0f;
            currentSpaceId  = spaceId;
            Debug.Log($"[Parking] 進入停車格，spaceId：'{spaceId}'");

            if (correctionCoroutine != null) StopCoroutine(correctionCoroutine);
            correctionCoroutine = StartCoroutine(EndCorrectionMode());
        }

        Debug.Log($"[Zone] {sensorName} 進入 {spaceId}，該格內：{sensorsPerSpace[spaceId].Count}/4");
    }

    public void OnSensorExitZone(string spaceId, string sensorName)
    {
        if (sensorsPerSpace.ContainsKey(spaceId))
        {
            sensorsPerSpace[spaceId].Remove(sensorName);
            if (sensorsPerSpace[spaceId].Count == 0)
                sensorsPerSpace.Remove(spaceId);
        }

        Debug.Log($"[Zone] {sensorName} 離開 {spaceId}");

        if (TotalSensorsInside() == 0)
        {
            if (passedAwaitingLeave)
            {
                Debug.Log("[Parking] 停好後整台車又駛離車格 → 收回此次通過並判失敗");
                if (passedWasSide) onSideParkFail?.Invoke();
                else               onReverseParkFail?.Invoke();
            }
            else
            {
                Debug.Log("[Parking] 車子完全離開停車格，重置偵測");
            }
            ResetState();
        }
    }

    private int TotalSensorsInside()
    {
        int total = 0;
        foreach (var kv in sensorsPerSpace)
            total += kv.Value.Count;
        return total;
    }

    public void OnSensorTouchLine(string sensorName)
    {
        sensorsOnLine.Add(sensorName);
    }

    public void OnSensorLeaveLine(string sensorName)
    {
        sensorsOnLine.Remove(sensorName);
    }

    private IEnumerator EndCorrectionMode()
    {
        isCorrectionMode = true;
        yield return new WaitForSeconds(correctionDelay);
        isCorrectionMode = false;
        Debug.Log("[Parking] 校正模式結束，開始偵測P檔狀態");
    }

    private void ResetState()
    {
        isInParkingZone  = false;
        isCorrectionMode = false;
        isEvaluating     = false;
        stopTimer        = 0f;
        pGearConfirmed   = false;
        passedAwaitingLeave = false;
        passedWasSide       = false;
        currentSpaceId   = "";
        sensorsPerSpace.Clear();
        sensorsOnLine.Clear();
        if (correctionCoroutine != null)
        {
            StopCoroutine(correctionCoroutine);
            correctionCoroutine = null;
        }
    }
}