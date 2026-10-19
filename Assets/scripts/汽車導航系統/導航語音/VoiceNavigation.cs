using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoiceNavigation : MonoBehaviour
{
    [Header("組件引用")]
    public NavigationManager navigationManager;
    public Transform vehicle;

    [Header("音效來源")]
    public AudioSource audioSource;

    [Header("完整句音檔")]
    [Tooltip("開始導航")]
    public AudioClip startNav;
    [Tooltip("已抵達目的地，目的地在您的左手邊")]
    public AudioClip arriveLeft;
    [Tooltip("已抵達目的地，目的地在您的右手邊")]
    public AudioClip arriveRight;
    [Tooltip("您已抵達目的地（不分左右）")]
    public AudioClip arriveDirect;
    [Tooltip("您即將抵達目的地")]
    public AudioClip approaching;
    [Tooltip("您已偏離導航路線，正在為您重新計算")]
    public AudioClip recalculate;

    [Header("轉彎提示音檔")]
    [Tooltip("前方100公尺左轉")]
    public AudioClip far100Left;
    [Tooltip("前方100公尺右轉")]
    public AudioClip far100Right;
    [Tooltip("請在下一個路口左轉")]
    public AudioClip nearLeft;
    [Tooltip("請在下一個路口右轉")]
    public AudioClip nearRight;
    [Tooltip("前方請迴轉")]
    public AudioClip uTurn;

    [Header("轉彎提示設定")]
    [Tooltip("『前方100公尺』播報區間的上限（公尺）：距離超過此值太遠，不播")]
    public float far100MaxDistance    = 120f;
    [Tooltip("遠/近分界（公尺）：距離 > 此值播『100公尺左/右轉』，≤ 此值播『前方路口左/右轉』")]
    public float turnBoundaryDistance = 80f;
    [Tooltip("接近轉彎點多近視為已通過（公尺）")]
    public float passDistance         = 5f;
    [Tooltip("轉彎判定角度門檻（度）")]
    public float turnAngleThreshold   = 30f;
    [Tooltip("迴轉判定角度門檻（度）")]
    public float uTurnAngleThreshold  = 150f;

    [Header("抵達設定")]
    [Tooltip("單點目的地：抵達距離（公尺）")]
    public float arriveDistance        = 15f;
    [Tooltip("Box目的地：距離Box邊緣多近算抵達（公尺）")]
    public float boxArriveDistance     = 8f;
    [Tooltip("即將抵達：沿路徑剩餘距離（公尺）")]
    public float approachingDistance   = 30f;

    [Header("語音間隔")]
    public float minVoiceGap = 1f;

    [Header("除錯")]
    [Tooltip("勾選後，在 Console 印出前方最近路點的轉角/方向，以及抵達左右判定，用來調整與除錯")]
    public bool debugTurnLog = false;

    private enum Priority { Turn = 0, Important = 1 }

    private NavWaypointNode lastAnnouncedFarNode  = null;
    private NavWaypointNode lastAnnouncedNearNode = null;
    private NavWaypointNode lastDebugNode         = null;
    private bool hasArrived     = false;
    private bool hasApproaching = false;

    private List<NavWaypointNode> lastPath = null;
    private bool wasNavigating = false;

    private Priority currentPriority  = Priority.Turn;
    private float    lastPlayEndTime  = 0f;
    private float    lastStartNavTime = -10f;

    private void Start()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
            audioSource = gameObject.AddComponent<AudioSource>();
    }

    private void Update()
    {
        if (navigationManager == null || vehicle == null) return;

        var path = navigationManager.currentPath;

        if (path != null && path.Count >= 2)
        {
            lastPath      = path;
            wasNavigating = true;

            CheckTurnAnnouncement(path);
            CheckArrival(path);
        }
        else
        {
            // 路徑被清掉了。若剛剛還在導航、尚未播抵達，而且目的地仍在
            //（＝ NavigationManager 判定抵達自動停止，不是手動取消）→ 補播抵達語音。
            if (wasNavigating && !hasArrived
                && lastPath != null && lastPath.Count >= 2
                && navigationManager.currentDestinationTransform != null)
            {
                hasArrived = true;
                AnnounceArrival(lastPath);
            }

            wasNavigating  = false;
            hasArrived     = false;
            hasApproaching = false;
            lastPath       = null;
        }
    }

    // ── 轉彎提示 ──────────────────────────────────────────────

    private void CheckTurnAnnouncement(List<NavWaypointNode> path)
    {
        // 先算出車輛目前落在路徑的哪一段（最近線段）。
        // 只評估「車輛前方」的轉彎路點；已經開過的路點一律跳過，
        // 避免舊路點卡在迴圈裡一直 return，導致後面的轉彎全部漏報。
        int currentSegment = GetNearestSegmentIndex(path);
        bool debuggedThisFrame = false;

        for (int i = 1; i < path.Count - 1; i++)
        {
            if (path[i] == null || path[i - 1] == null || path[i + 1] == null) continue;

            NavWaypointNode turnNode = path[i];

            // 已經通過的路點 → 直接跳過（不要覆蓋追蹤器，否則會破壞防重複）
            if (i <= currentSegment) continue;

            float distToTurn = Vector3.Distance(vehicle.position, turnNode.transform.position);
            float angle      = GetTurnAngle(path[i - 1], turnNode, path[i + 1]);
            int   turnDir    = ClassifyTurn(angle);

            // 除錯：印出前方最近一個路點的轉角與方向（含被判為直行的，方便抓漏報）
            if (debugTurnLog && !debuggedThisFrame && distToTurn <= far100MaxDistance)
            {
                debuggedThisFrame = true;
                if (turnNode != lastDebugNode)
                {
                    lastDebugNode = turnNode;
                    Debug.Log($"[語音] 前方路點 {turnNode.name}：轉角 {angle:F1}° → {DirName(turnDir)}（距離 {distToTurn:F0}m）");
                }
            }

            if (turnDir == 0) continue;   // 直行，往後找下一個轉彎

            // 已經很接近轉彎點 → 視為已播，往後找下一個轉彎（避免卡住）
            if (distToTurn < passDistance)
            {
                lastAnnouncedFarNode  = turnNode;
                lastAnnouncedNearNode = turnNode;
                continue;
            }

            // ── 「前方100公尺左/右轉」：只在 turnBoundaryDistance ~ far100MaxDistance 這段播（例：80~120m）──
            // 玩家若一開始就比 turnBoundaryDistance 近，永遠不會進到這段 → 不會播「100公尺」
            if (distToTurn > turnBoundaryDistance && distToTurn <= far100MaxDistance)
            {
                if (lastAnnouncedFarNode != turnNode)
                {
                    lastAnnouncedFarNode = turnNode;
                    RequestPlay(GetFarClip(turnDir), Priority.Turn);
                }
            }

            // ── 「前方路口左/右轉」：passDistance ~ turnBoundaryDistance 這段播（例：5~80m）──
            if (distToTurn > passDistance && distToTurn <= turnBoundaryDistance)
            {
                if (lastAnnouncedNearNode != turnNode)
                {
                    lastAnnouncedNearNode = turnNode;
                    RequestPlay(GetNearClip(turnDir), Priority.Turn);
                }
            }

            // 找到「前方最近一個」轉彎並處理完 → 這一幀結束
            return;
        }
    }

    // 回傳車輛目前最接近的路段索引（segment i 指 path[i]→path[i+1]）
    private int GetNearestSegmentIndex(List<NavWaypointNode> path)
    {
        int nearest = 0;
        float minSegDist = float.MaxValue;

        for (int i = 0; i < path.Count - 1; i++)
        {
            if (path[i] == null || path[i + 1] == null) continue;
            float d = DistancePointToSegment(
                vehicle.position,
                path[i].transform.position,
                path[i + 1].transform.position
            );
            if (d < minSegDist)
            {
                minSegDist = d;
                nearest = i;
            }
        }
        return nearest;
    }

    // 三點夾角（帶正負號，左負右正）
    private float GetTurnAngle(NavWaypointNode prev, NavWaypointNode current, NavWaypointNode next)
    {
        Vector3 dirIn  = (current.transform.position - prev.transform.position).normalized;
        Vector3 dirOut = (next.transform.position - current.transform.position).normalized;
        return Vector3.SignedAngle(dirIn, dirOut, Vector3.up);
    }

    // 依夾角判定方向：0=直行, 1=左轉, 2=右轉, 3=迴轉
    private int ClassifyTurn(float angle)
    {
        if (Mathf.Abs(angle) >= uTurnAngleThreshold) return 3;
        if (angle < -turnAngleThreshold) return 1;
        if (angle >  turnAngleThreshold) return 2;
        return 0;
    }

    private string DirName(int turnDir)
    {
        switch (turnDir)
        {
            case 1: return "左轉";
            case 2: return "右轉";
            case 3: return "迴轉";
            default: return "直行";
        }
    }

    private AudioClip GetFarClip(int turnDir)
    {
        if (turnDir == 3) return uTurn;
        if (turnDir == 1) return far100Left;
        if (turnDir == 2) return far100Right;
        return null;
    }

    private AudioClip GetNearClip(int turnDir)
    {
        if (turnDir == 3) return uTurn;
        if (turnDir == 1) return nearLeft;
        if (turnDir == 2) return nearRight;
        return null;
    }

    // ── 抵達目的地 ────────────────────────────────────────────

    private void CheckArrival(List<NavWaypointNode> path)
    {
        NavWaypointNode destNode = path[path.Count - 1];

        DestinationZone zone = null;
        if (navigationManager.currentDestinationTransform != null)
            zone = navigationManager.currentDestinationTransform.GetComponent<DestinationZone>();

        // 實際距離（判定已抵達）
        float actualDist;
        bool  isInside;

        if (zone != null)
        {
            actualDist = zone.DistanceToBox(vehicle.position);
            isInside   = actualDist <= boxArriveDistance;
        }
        else
        {
            actualDist = Vector3.Distance(vehicle.position, destNode.transform.position);
            isInside   = actualDist <= arriveDistance;
        }

        // 路徑剩餘距離（判定即將抵達）
        float pathRemaining = GetRemainingPathDistance(path);

        if (pathRemaining <= approachingDistance && !isInside && !hasApproaching)
        {
            hasApproaching = true;
            RequestPlay(approaching, Priority.Important);
        }

        if (isInside && !hasArrived)
        {
            hasArrived = true;
            AnnounceArrival(path);
        }
    }

    // 播放抵達語音（左右手邊 / 不分左右）
    private void AnnounceArrival(List<NavWaypointNode> path)
    {
        DestinationZone zone = null;
        if (navigationManager.currentDestinationTransform != null)
            zone = navigationManager.currentDestinationTransform.GetComponent<DestinationZone>();

        if (zone != null && zone.arriveType == DestinationZone.ArriveType.Direct)
            RequestPlay(arriveDirect, Priority.Important);
        else
            RequestPlay(GetArriveSideClip(path), Priority.Important);
    }

    // 判斷目的地在左手邊還右手邊：
    // 用「進入目的地的路徑方向」當前方（穩定），不用車子當下朝向（停車時會亂跳，導致左右忽對忽錯）
    private AudioClip GetArriveSideClip(List<NavWaypointNode> path)
    {
        // 進場方向 = 最後一段路徑的方向
        Vector3 approachDir;
        if (path.Count >= 2 &&
            path[path.Count - 1] != null && path[path.Count - 2] != null)
            approachDir = path[path.Count - 1].transform.position
                        - path[path.Count - 2].transform.position;
        else
            approachDir = vehicle.forward; // 退路：只有一個點時才用車頭
        approachDir.y = 0f;
        approachDir.Normalize();

        // 目的地實際位置（側停格 Box 的中心），相對最後一個路點的偏移
        Vector3 destPos = (navigationManager.currentDestinationTransform != null)
            ? navigationManager.currentDestinationTransform.position
            : path[path.Count - 1].transform.position;

        Vector3 toDest = destPos - path[path.Count - 1].transform.position;
        toDest.y = 0f;

        // 若 Box 幾乎壓在路點上（偏移太小），退而用「車 → Box」方向
        if (toDest.sqrMagnitude < 0.01f)
        {
            toDest = destPos - vehicle.position;
            toDest.y = 0f;
        }

        float side = Vector3.SignedAngle(approachDir, toDest, Vector3.up);

        if (debugTurnLog)
            Debug.Log($"[語音] 抵達左右判定：angle {side:F1}° → {(side < 0 ? "左手邊" : "右手邊")}");

        return side < 0 ? arriveLeft : arriveRight;
    }

    // 沿路徑到目的地的剩餘距離
    private float GetRemainingPathDistance(List<NavWaypointNode> path)
    {
        if (path == null || path.Count < 2) return float.MaxValue;

        int nearestSegment = GetNearestSegmentIndex(path);

        float remaining = Vector3.Distance(
            vehicle.position,
            path[nearestSegment + 1].transform.position
        );

        for (int i = nearestSegment + 1; i < path.Count - 1; i++)
        {
            if (path[i] == null || path[i + 1] == null) continue;
            remaining += Vector3.Distance(
                path[i].transform.position,
                path[i + 1].transform.position
            );
        }

        return remaining;
    }

    private float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float sqr = ab.sqrMagnitude;
        if (sqr < 0.0001f) return Vector3.Distance(point, a);
        float t = Vector3.Dot(point - a, ab) / sqr;
        t = Mathf.Clamp01(t);
        Vector3 closest = a + t * ab;
        return Vector3.Distance(point, closest);
    }

    // ── 公開方法 ──────────────────────────────────────────────

    public void PlayStartNav()
    {
        if (Time.time - lastStartNavTime < 3f) return;
        lastStartNavTime = Time.time;

        ResetAnnouncements();
        RequestPlay(startNav, Priority.Important);
    }

    public void PlayRecalculate()
    {
        ResetAnnouncements();
        RequestPlay(recalculate, Priority.Important);
    }

    private void ResetAnnouncements()
    {
        lastAnnouncedFarNode  = null;
        lastAnnouncedNearNode = null;
        lastDebugNode         = null;
        hasArrived     = false;
        hasApproaching = false;
    }

    // ── 語音播放核心 ──────────────────────────────────────────

    private void RequestPlay(AudioClip clip, Priority priority)
    {
        if (clip == null) return;

        if (audioSource.isPlaying)
        {
            if (priority > currentPriority)
                PlayNow(clip, priority);
        }
        else
        {
            if (Time.time - lastPlayEndTime >= minVoiceGap)
                PlayNow(clip, priority);
            else if (priority == Priority.Important)
                StartCoroutine(PlayAfterGap(clip, priority));
        }
    }

    private void PlayNow(AudioClip clip, Priority priority)
    {
        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.Play();
        currentPriority = priority;
        lastPlayEndTime = Time.time + clip.length;
    }

    private IEnumerator PlayAfterGap(AudioClip clip, Priority priority)
    {
        float wait = minVoiceGap - (Time.time - lastPlayEndTime);
        if (wait > 0) yield return new WaitForSeconds(wait);
        PlayNow(clip, priority);
    }
}