using UnityEngine;

/// <summary>
/// 🛬 生成貼地校正（純工具類，不含任何車輛行為）
///
/// 為什麼需要這個：
///   AI 車的 Rigidbody 是 Kinematic，完全不受重力影響；
///   而移動邏輯裡有一行 targetPosition.y = transform.position.y 把 Y 永久鎖住。
///   所以生成器「抬高 0.5m 讓車輕輕落地」的假設不成立 —— 車子永遠不會落下來，
///   會固定飄在半空中，再疊加各個路徑節點自己的高度差。
///
/// 這裡在生成的當下把車直接放到路面上，之後就交給原本的邏輯，不再干預。
/// 只動 Transform 的 Y，不碰任何避讓、繞行、倒車或速度邏輯。
/// </summary>
public static class SpawnGroundSnap
{
    /// <summary>從車輛位置往上多少公尺開始往下找路面</summary>
    public const float RayStartHeight = 3f;
    /// <summary>往下搜尋的距離</summary>
    public const float RayDistance = 15f;
    /// <summary>落地後留的縫隙，避免車體跟路面 Z-fighting</summary>
    public const float Clearance = 0.02f;

    /// <summary>開啟後會在 Console 說明每次貼地的結果，排查完可以關掉</summary>
    public static bool LogDiagnostics = false;

    /// <summary>
    /// 把車放到正下方的路面上。
    /// </summary>
    /// <param name="car">要校正的車</param>
    /// <param name="fallbackDrop">
    /// 找不到路面時要往下降多少。傳入生成器額外抬高的量（目前是 0.5），
    /// 這樣即使射線打空，車子至少會回到路網節點的高度，而不是一直飄著。
    /// 傳 0 則完全不動。
    /// </param>
    public static void Apply(GameObject car, float fallbackDrop = 0f)
    {
        if (car == null) return;

        Transform root = car.transform;
        Vector3 start = root.position + Vector3.up * RayStartHeight;

        RaycastHit[] hits = Physics.RaycastAll(
            start, Vector3.down, RayDistance, ~0, QueryTriggerInteraction.Ignore);

        float surfaceY = float.NegativeInfinity;
        bool found = false;
        string hitName = "(無)";

        foreach (var h in hits)
        {
            // 跳過車子自己的碰撞體
            if (h.collider.transform.IsChildOf(root)) continue;
            // 跳過其他車輛：路面沒有 Rigidbody，車輛才有
            if (h.collider.attachedRigidbody != null) continue;

            // 取最高的一個命中點，也就是最上層的路面
            if (h.point.y > surfaceY)
            {
                surfaceY = h.point.y;
                hitName = h.collider.name;
                found = true;
            }
        }

        if (!found)
        {
            if (fallbackDrop > 0f)
                root.position -= Vector3.up * fallbackDrop;

            if (LogDiagnostics)
                Debug.LogWarning(
                    $"[貼地] {car.name} 射線沒打到路面（起點 {start}，往下 {RayDistance}m，" +
                    $"總命中 {hits.Length} 個但都被排除）。" +
                    (fallbackDrop > 0f ? $"已改用節點高度（下降 {fallbackDrop}m）。" : "維持原高度。"), car);
            return;
        }

        // 車底相對 pivot 的距離。用世界邊界計算，所以 pivot 在車中心或車底都適用。
        // includeInactive = true：有些車的模型放在停用的子物件下（例如 LOD 切換），不能漏掉。
        float bottomOffset = 0f;
        string boundsSource = "找不到邊界";

        Renderer[] renderers = car.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            bottomOffset = root.position.y - b.min.y;
            boundsSource = $"{renderers.Length} 個 Renderer";
        }
        else
        {
            // 退而求其次用碰撞體的邊界
            Collider[] cols = car.GetComponentsInChildren<Collider>(true);
            if (cols.Length > 0)
            {
                Bounds b = cols[0].bounds;
                for (int i = 1; i < cols.Length; i++)
                    b.Encapsulate(cols[i].bounds);
                bottomOffset = root.position.y - b.min.y;
                boundsSource = $"{cols.Length} 個 Collider";
            }
        }

        float beforeY = root.position.y;
        root.position = new Vector3(
            root.position.x,
            surfaceY + bottomOffset + Clearance,
            root.position.z);

        if (LogDiagnostics)
            Debug.Log($"<color=lime>[貼地] {car.name} 命中「{hitName}」路面 y={surfaceY:F2}，" +
                      $"車底偏移 {bottomOffset:F2}（來源：{boundsSource}）→ " +
                      $"高度 {beforeY:F2} 調整為 {root.position.y:F2}</color>", car);
    }
}
