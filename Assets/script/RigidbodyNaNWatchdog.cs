using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 🩺 Rigidbody 數值爆炸偵測器
///
/// 用途:找出是「哪一個」Rigidbody 的位置/速度變成 NaN 或 Infinity,
/// 也就是 Console 那句 "Skipped updating the transform of this Rigidbody
/// because its components are infinite" 的元兇。Unity 本身不會告訴你是誰。
///
/// 用法:
///   1. Hierarchy 空白處右鍵 → Create Empty,命名隨意(例如 __NaN偵測器)
///   2. 把這個腳本拖上去
///   3. 按 Play,看 Console 的 ☠️ 訊息 —— 會印出完整階層路徑
///   4. 點那則訊息,Hierarchy 會自動選取到出問題的物件
///
/// 查完就可以把這個物件刪掉,它只是診斷工具。
/// </summary>
public class RigidbodyNaNWatchdog : MonoBehaviour
{
    [Tooltip("每隔幾秒重新掃描一次場景上的 Rigidbody 清單(車子是動態生成的,需要定期重掃)")]
    public float rescanInterval = 2f;

    [Tooltip("同一個物件最多回報幾次,避免洗版")]
    public int maxReportsPerObject = 1;

    private Rigidbody[] bodies;
    private float nextRescanTime;
    private readonly Dictionary<Rigidbody, int> reportCount = new Dictionary<Rigidbody, int>();

    private void FixedUpdate()
    {
        if (Time.time >= nextRescanTime)
        {
            bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            nextRescanTime = Time.time + rescanInterval;
        }
        if (bodies == null) return;

        foreach (Rigidbody rb in bodies)
        {
            if (rb == null) continue;

            bool bad = !IsFinite(rb.position)
                    || !IsFinite(rb.velocity)
                    || !IsFinite(rb.angularVelocity)
                    || !IsFinite(rb.centerOfMass)
                    || !IsFinite(rb.transform.position);
            if (!bad) continue;

            int n;
            reportCount.TryGetValue(rb, out n);
            if (n >= maxReportsPerObject) continue;
            reportCount[rb] = n + 1;

            Debug.LogError(
                $"☠️ [NaN/Infinity 偵測] {HierarchyPath(rb.transform)}\n" +
                $"   position        = {rb.position}\n" +
                $"   velocity        = {rb.velocity}\n" +
                $"   angularVelocity = {rb.angularVelocity}\n" +
                $"   centerOfMass    = {rb.centerOfMass}\n" +
                $"   mass={rb.mass}  drag={rb.drag}  isKinematic={rb.isKinematic}\n" +
                $"   lossyScale      = {rb.transform.lossyScale}\n" +
                $"   (點這則訊息可跳到該物件)",
                rb.gameObject);
        }
    }

    private static bool IsFinite(Vector3 v)
    {
        return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
              || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }

    private static string HierarchyPath(Transform t)
    {
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + " / " + path;
        }
        return path;
    }
}
