using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🚶 行人生成器（回收式）—— 讓街上「一直有人」。
///
/// 原本行人走到 despawn 節點就 Destroy，人口只減不增，街景撐不久。
/// 這支不用 Instantiate、也不用做 prefab：走到終點的行人被「回收」送回一個起點繼續走，
/// 同一批行人循環利用，人口恆定。
///
/// 用法：把這個元件掛在場景任一空物件上（例如「行人資料」根物件）即可。
///   Spawn Nodes 留空 → 開場自動用「現有行人的起始節點」當回收起點，零設定。
///   要指定行人從哪些節點（重新）出現，就手動把節點拉進 Spawn Nodes。
///
/// 想要街上人更多：先在「行人資料」裡複製幾個行人（walker），這支會自動把它們都納入。
/// </summary>
public class PedestrianSpawner : MonoBehaviour
{
    public static PedestrianSpawner Instance { get; private set; }

    [Header("行人（重新）出現的節點")]
    [Tooltip("留空 = 開場自動用現有行人的起始節點。要控制出現位置就手動拉節點進來")]
    public List<PedestrianNode> spawnNodes = new List<PedestrianNode>();

    [Tooltip("印出每次回收")]
    public bool logRecycle = false;

    private void Awake()
    {
        // 只留一個生成器；重複的自我停用，避免兩個搶著回收
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[行人生成器] 場景已有一個 PedestrianSpawner，{name} 這個多的自動停用", this);
            enabled = false;
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (spawnNodes == null) spawnNodes = new List<PedestrianNode>();

        // 沒手動指定 → 自動用現有行人的起始目標節點
        if (spawnNodes.Count == 0)
        {
            foreach (var w in FindObjectsByType<PedestrianWalker>(FindObjectsSortMode.None))
            {
                if (w == null) continue;
                PedestrianNode n = w.currentTargetNode;
                if (n != null && !spawnNodes.Contains(n)) spawnNodes.Add(n);
            }
            Debug.Log($"<color=lime>[行人生成器] 自動蒐集到 {spawnNodes.Count} 個回收起點</color>", this);
        }
    }

    /// <summary>把走到終點的行人回收送回一個隨機起點，繼續走。成功回 true。</summary>
    public bool TryRecycle(PedestrianWalker w)
    {
        if (w == null || spawnNodes == null || spawnNodes.Count == 0) return false;

        // 隨機挑一個非空的起點
        PedestrianNode node = null;
        for (int tries = 0; tries < 4 && node == null; tries++)
            node = spawnNodes[Random.Range(0, spawnNodes.Count)];
        if (node == null) return false;

        w.RespawnAt(node);
        if (logRecycle)
            Debug.Log($"<color=grey>🚶 [行人生成器] 回收 {w.name} → {node.name}</color>", this);
        return true;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
