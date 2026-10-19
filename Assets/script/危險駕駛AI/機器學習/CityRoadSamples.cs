using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🗺 城市路網訓練樣本：從 cc.unity 匯出的「哪些地方真的能觸發鬼切、那裡的路長什麼樣子」。
///
/// 由選單 Tools > 危險駕駛AI > 匯出城市路網訓練樣本 產生（必須在 cc.unity 裡執行）。
/// 每一筆樣本都是用「跟觸發器 TryStart 完全相同的條件」檢查過的位置，
/// 所以訓練場抽到的車道寬、左右側、隔壁車道多長就斷掉，都是城市裡真的會遇到的情況。
/// 同時記錄實際的車身尺寸（切車 prefab 縮放 1.4 倍，車寬 2.8m），訓練時不再用假設值。
/// </summary>
[CreateAssetMenu(menuName = "危險駕駛AI/城市路網訓練樣本", fileName = "城市路網訓練樣本")]
public class CityRoadSamples : ScriptableObject
{
    [Serializable]
    public class RoadSample
    {
        [Tooltip("隔壁車道中心的側向偏移（m，負 = 左側、正 = 右側）")]
        public float adjOffset;
        [Tooltip("以玩家位置為 0，隔壁車道在前方幾公尺處結束（沒有結束 = 999）")]
        public float adjEndFromPlayer;
        [Tooltip("前方直路範圍內的最大轉角（度）")]
        public float headingChange;
        [Tooltip("這個位置的世界座標（除錯用）")]
        public Vector3 position;
    }

    [Serializable]
    public class CarSize
    {
        public string name;
        public float halfLength;
        public float halfWidth;
    }

    [Header("🛣 道路樣本")]
    public List<RoadSample> samples = new List<RoadSample>();

    [Header("🚗 車身尺寸（實測）")]
    [Tooltip("鬼切車（觸發器 Actor Prefabs 裡的每一台）")]
    public List<CarSize> actorSizes = new List<CarSize>();
    [Tooltip("玩家車")]
    public CarSize playerSize = new CarSize { name = "玩家車", halfLength = 2.3f, halfWidth = 1.0f };

    [Header("📝 匯出資訊")]
    public string sourceScene;
    public string exportedAt;
    [TextArea(3, 12)] public string summary;

    public RoadSample RandomSample()
    {
        if (samples == null || samples.Count == 0) return null;
        return samples[UnityEngine.Random.Range(0, samples.Count)];
    }

    public CarSize RandomActorSize()
    {
        if (actorSizes == null || actorSizes.Count == 0) return null;
        return actorSizes[UnityEngine.Random.Range(0, actorSizes.Count)];
    }
}
