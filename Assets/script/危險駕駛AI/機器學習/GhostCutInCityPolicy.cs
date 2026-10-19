using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Barracuda;

/// <summary>
/// 🤖 城市裡的鬼切車「推論用大腦」（直接用 Barracuda 執行 GhostCutIn.onnx）
///
/// 為什麼不用 ML-Agents 的 Agent 元件：
///   實測在遊戲中動態掛上 Agent + DecisionRequester 時，ML-Agents 2.0.2 的決策流程沒有正常啟動 ——
///   模型每一步都在跑，但 CollectObservations 從未被呼叫，模型拿到的永遠是 0。
///   改成自己載入 ONNX、每 0.1 秒餵一次觀測、讀回動作，完全不經過 Academy，流程簡單也好除錯。
///
/// 模型格式（mlagents 0.26 匯出）：輸入 obs_0 [1, 21]；輸出 continuous_actions [1, 2]，
///   已在模型內做過 clamp(−3, 3) / 3，數值就在 −1～1。
/// 觀測值由 AggressiveDriverActor.FillRLObservations → GhostCutInRL.FillObservations 提供，
///   訓練場呼叫的是同一個函式。
///
/// ⚠ v2 改成 21 個觀測值。第一版（12 個觀測值）的模型放進來會被偵測出來，鬼切車自動改用規則式。
/// </summary>
public class GhostCutInCityPolicy : MonoBehaviour
{
    [HideInInspector] public AggressiveDriverActor actor;
    [HideInInspector] public NNModel model;

    [Tooltip("每隔幾個物理步做一次決策（訓練時 Decision Period = 5，也就是 0.1 秒）")]
    public int decisionPeriod = 5;
    [Tooltip("🔍 診斷：每秒印一次觀測值與模型輸出")]
    public bool logDiagnostics = true;

    /// <summary>油門／煞車（−1～1）</summary>
    public float AccelCmd { get; private set; }
    /// <summary>橫移（−1～1，+ = 往玩家車道）</summary>
    public float LatCmd { get; private set; }
    public bool IsReady { get { return worker != null; } }
    /// <summary>模型名稱（= Project 視窗裡 onnx 檔的名稱，例如 GhostCutIn_v6）。寫進 Log 與事件紀錄 CSV，一眼看出用的是哪一版</summary>
    public string ModelName { get { return model != null ? model.name : "（無模型）"; } }
    /// <summary>模型載入或推論失敗 → 演員車自動退回規則式</summary>
    public bool Failed { get; private set; }

    private const string InputName = "obs_0";
    private const string OutputName = "continuous_actions";

    private IWorker worker;
    private readonly float[] obs = new float[GhostCutInRL.ObservationSize];
    private int stepCounter;
    private int decisions;
    private float nextDiagTime;

    // 用 Awake：觸發器在物件啟用前就指定好 model，啟用當下立刻載入，演員車第一個 FixedUpdate 就能用
    private void Awake()
    {
        if (model == null) { Failed = true; Debug.LogWarning("[強化學習] 沒有指定模型，鬼切車會退回規則式", this); return; }
        try
        {
            Model runtime = ModelLoader.Load(model);

            // 檢查模型的觀測數量跟程式一致（第一版模型是 12 個，v2 是 21 個）
            int modelObs = InputWidth(runtime);
            if (modelObs > 0 && modelObs != GhostCutInRL.ObservationSize)
            {
                Failed = true;
                Debug.LogError($"[強化學習] 模型「{model.name}」需要 {modelObs} 個觀測值，但程式提供 {GhostCutInRL.ObservationSize} 個 —— " +
                               "這是舊版模型。請用 v2 訓練場重新訓練後再匯入；這次鬼切車改用規則式", this);
                return;
            }

            worker = WorkerFactory.CreateWorker(WorkerFactory.Type.CSharpBurst, runtime, false);
            if (logDiagnostics)
                Debug.Log($"<color=#b388ff>🤖 [強化學習診斷] 已載入模型「{model.name}」（Barracuda 直接推論，觀測 {GhostCutInRL.ObservationSize} 個" +
                          (modelObs > 0 ? "，已確認與模型一致" : "，無法讀取模型輸入大小") + "）</color>", this);
        }
        catch (Exception e)
        {
            worker = null;
            Failed = true;
            Debug.LogError("[強化學習] 模型載入失敗，鬼切車會退回規則式：" + e.Message, this);
        }
    }

    /// <summary>模型輸入 obs_0 的寬度（批次維度以外所有大於 0 的維度相乘）。讀不到回傳 −1</summary>
    private static int InputWidth(Model m)
    {
        foreach (var input in m.inputs)
        {
            if (input.name != InputName || input.shape == null || input.shape.Length == 0) continue;
            int prod = 1;
            for (int i = 1; i < input.shape.Length; i++) if (input.shape[i] > 0) prod *= input.shape[i];
            return prod;
        }
        return -1;
    }

    private void FixedUpdate()
    {
        if (worker == null || actor == null) return;
        if (stepCounter++ % Mathf.Max(1, decisionPeriod) != 0) return;   // 0.1 秒決策一次，中間沿用上一個動作（跟訓練時相同）
        Decide();
    }

    private void Decide()
    {
        actor.FillRLObservations(obs);
        try
        {
            using (var input = new Tensor(1, obs.Length, obs))
            {
                worker.Execute(new Dictionary<string, Tensor> { { InputName, input } });
                Tensor output = worker.PeekOutput(OutputName);
                AccelCmd = Mathf.Clamp(output[0], -1f, 1f);
                LatCmd = Mathf.Clamp(output[1], -1f, 1f);
            }
            decisions++;
        }
        catch (Exception e)
        {
            Debug.LogError("[強化學習] 推論失敗（輸入 obs_0／輸出 continuous_actions 名稱或大小不符？）：" + e.Message, this);
            worker.Dispose();
            worker = null;
            Failed = true;
            AccelCmd = 0f; LatCmd = 0f;
            return;
        }

        if (logDiagnostics && Time.time >= nextDiagTime)
        {
            nextDiagTime = Time.time + 1f;
            var parts = new string[obs.Length];
            for (int i = 0; i < obs.Length; i++) parts[i] = obs[i].ToString("F2");
            Debug.Log($"<color=#b388ff>🤖 [強化學習診斷] 第 {decisions} 次決策｜觀測 [{string.Join(", ", parts)}]｜" +
                      $"輸出 油門 {AccelCmd:+0.00;-0.00}、橫移（往玩家車道）{LatCmd:+0.00;-0.00}</color>", this);
        }
    }

    private void OnDisable()
    {
        // 改接規則式時會停用這個元件 → 不再推論
        AccelCmd = 0f; LatCmd = 0f;
    }

    private void OnDestroy()
    {
        if (worker != null) worker.Dispose();
        worker = null;
    }
}
