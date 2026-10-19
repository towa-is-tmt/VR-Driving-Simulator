using UnityEngine;
using static WaypointNode;

public enum IntersectionType
{
    None,
    NormalSystem,
    SpecialSystem,
    BlinkSystem,
    AlleySystem //�@��p�Ѥl���f
}

public enum PedestrianCrossDirection
{
    AC_Path,
    BD_Path
}

// �ק��I�G�אּ�����~�� MonoBehaviour
public class PedestrianNode : MonoBehaviour
{
    [Header("=== ��H���I�]�w ===")]
    public PedestrianNode[] nextNodes;

    [Tooltip("�Ŀ��A��H��F�����I�ɷ|��������(Destroy)")]
    public bool isDespawnNode = false; // �s�W�G�������I���

    [Header("=== ���x�ި�]�w ===")]
    public IntersectionType intersectionType = IntersectionType.None;
    public PedestrianCrossDirection crossDirection = PedestrianCrossDirection.AC_Path;
    public TrafficSystem_Brain trafficBrain;

    public PedestrianNode GetRandomNextNode()
    {
        if (nextNodes == null || nextNodes.Length == 0) return null;
        int randomIndex = Random.Range(0, nextNodes.Length);
        return nextNodes[randomIndex];
    }

    public bool CanCross()
    {
        if (intersectionType == IntersectionType.None || intersectionType == IntersectionType.AlleySystem || trafficBrain == null)
            return true;

        switch (intersectionType)
        {
            case IntersectionType.NormalSystem:
                if (crossDirection == PedestrianCrossDirection.AC_Path)
                    return trafficBrain.normalPhase == TrafficSystem_Brain.NormalPhase.A_to_C_Green;
                else
                    return trafficBrain.normalPhase == TrafficSystem_Brain.NormalPhase.B_to_D_Green;

            case IntersectionType.SpecialSystem:
                if (crossDirection == PedestrianCrossDirection.AC_Path)
                    return trafficBrain.specialPhase == TrafficSystem_Brain.SpecialPhase.A_to_C_Green;
                else
                    return trafficBrain.specialPhase == TrafficSystem_Brain.SpecialPhase.B_to_D_Green;

            case IntersectionType.BlinkSystem:
                // �ק��I�G�{�O���f���\��H�H�ɳq��
                return true;
        }

        return false;
    }

    public float GetRemainingGreenTime()
    {
        // �p�Ѥl���f�P None �^�ǵL���ɶ��A���ݰ����]�L�ݤp�]
        if (intersectionType == IntersectionType.None || intersectionType == IntersectionType.AlleySystem || trafficBrain == null)
            return 999f;

        if (intersectionType == IntersectionType.None || trafficBrain == null) return 999f;

        if (intersectionType == IntersectionType.NormalSystem)
        {
            if (CanCross())
            {
                return trafficBrain.NormalGreenRemaining;
            }
        }
        else if (intersectionType == IntersectionType.SpecialSystem)
        {
            if (CanCross())
            {
                return trafficBrain.SpecialGreenRemaining;
            }
        }
        else if (intersectionType == IntersectionType.BlinkSystem)
        {
            // �ק��I�G�{�O���f�^�ǵL��/�R���ɶ��A�קK��H��]�λ~�P�w�����O
            return 999f;
        }

        return 0f;
    }

    private void OnDrawGizmos()
    {
        // �p�G�O�����`�I�A��ܶ¦�y��F�_�h���쥻�W�h���
        if (isDespawnNode)
        {
            Gizmos.color = Color.black;
        }
        else if (intersectionType == IntersectionType.AlleySystem)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f); // �p�Ѥl���f�e�����
        }
        else
        {
            Gizmos.color = (intersectionType == IntersectionType.None) ? Color.cyan : Color.red;
        }

        Gizmos.DrawSphere(transform.position, 0.3f);

        if (nextNodes != null)
        {
            Gizmos.color = Color.yellow;
            foreach (var next in nextNodes)
            {
                if (next != null)
                {
                    Vector3 startPos = transform.position;
                    Vector3 endPos = next.transform.position;

                    // 1. �e�X�s���u
                    Gizmos.DrawLine(startPos, endPos);

                    // 2. ø�s���V�b�Y
                    DrawArrowGizmo(startPos, endPos, Gizmos.color);
                }
            }
        }
    }

    private void DrawArrowGizmo(Vector3 start, Vector3 end, Color color)
    {
        Gizmos.color = color;

        // �p���V�V�q
        Vector3 direction = (end - start).normalized;
        if (direction == Vector3.zero) return;

        // �]�w�b�Y�b�u�q 80% ����m�A�άO�Z�����I�T�w�Z��
        Vector3 arrowPos = Vector3.Lerp(start, end, 0.8f);

        // �p��b�Y���j�p�P����
        float arrowSize = 0.4f;
        Vector3 right = Vector3.Cross(direction, Vector3.up);
        if (right == Vector3.zero)
        {
            right = Vector3.Cross(direction, Vector3.forward);
        }
        right.Normalize();

        // �e�X�b�Y�����l
        Vector3 arrowHeadA = arrowPos - direction * arrowSize + right * (arrowSize * 0.5f);
        Vector3 arrowHeadB = arrowPos - direction * arrowSize - right * (arrowSize * 0.5f);

        Gizmos.DrawLine(arrowPos, arrowHeadA);
        Gizmos.DrawLine(arrowPos, arrowHeadB);
    }
}