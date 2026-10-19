using UnityEngine;

// 🎮 模式管理器:切換訓練/自由模式時,套用各自的車流數量設定
public class GameModeManager : MonoBehaviour
{
    public static GameModeManager Instance;
    public static bool IsTrainingMode = false;

    [Header("啟動時的模式")]
    public bool startInTrainingMode = true;

    [Header("🎓 訓練模式車流(1/4 地圖)")]
    public int trainingWhiteCarCount = 20;
    public int trainingGrayCarCount = 10;

    [Header("🗺️ 自由模式車流(全地圖)")]
    public int freeWhiteCarCount = 31;
    public int freeGrayCarCount = 15;

    private void Awake() { Instance = this; }

    private void Start()
    {
        // 模式由車庫選單透過 TrainingSession 帶過來；
        // 沒經過選單（直接播 cc 場景測試）時，退回 Inspector 的 startInTrainingMode。
        bool training = startInTrainingMode;
        if (TrainingSession.gameMode == TrainingSession.Mode.Training) training = true;
        else if (TrainingSession.gameMode == TrainingSession.Mode.Free) training = false;

        ApplyMode(training);
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.T))
        {
            ApplyMode(!IsTrainingMode);
        }
    }
    // 之後你的選單/按鈕要切模式,就呼叫這個
    public void ApplyMode(bool training)
    {
        IsTrainingMode = training;

        if (TrafficSpawner.Instance != null)
            TrafficSpawner.Instance.targetCarCount = training ? trainingWhiteCarCount : freeWhiteCarCount;

        GrayTrafficSpawner gray = FindObjectOfType<GrayTrafficSpawner>();
        if (gray != null)
            gray.targetCarCount = training ? trainingGrayCarCount : freeGrayCarCount;

        UnityEngine.Debug.Log($"🎮 模式切換:{(training ? "訓練模式" : "自由模式")} | 白車 {(training ? trainingWhiteCarCount : freeWhiteCarCount)} 台 | 灰車 {(training ? trainingGrayCarCount : freeGrayCarCount)} 台");
    }
}