using UnityEngine;
using UnityEditor;

public class WaypointConnector : EditorWindow
{
    [MenuItem("Tools/路點連接工具")]
    public static void ShowWindow()
    {
        GetWindow<WaypointConnector>("路點連接工具");
    }

    private NavWaypointNode nodeA;
    private NavWaypointNode nodeB;

    private void OnGUI()
    {
        GUILayout.Label("選取兩個路點建立雙向連接", EditorStyles.boldLabel);

        nodeA = (NavWaypointNode)EditorGUILayout.ObjectField("路點 A", nodeA, typeof(NavWaypointNode), true);
        nodeB = (NavWaypointNode)EditorGUILayout.ObjectField("路點 B", nodeB, typeof(NavWaypointNode), true);

        if (GUILayout.Button("建立雙向連接"))
        {
            if (nodeA != null && nodeB != null)
            {
                if (!nodeA.connections.Contains(nodeB))
                    nodeA.connections.Add(nodeB);
                if (!nodeB.connections.Contains(nodeA))
                    nodeB.connections.Add(nodeA);

                EditorUtility.SetDirty(nodeA);
                EditorUtility.SetDirty(nodeB);

                Debug.Log($"✅ 已連接：{nodeA.name} ↔ {nodeB.name}");
            }
            else
            {
                Debug.LogWarning("請選取兩個路點！");
            }
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("快速連接：選取的兩個物件"))
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected.Length == 2)
            {
                var a = selected[0].GetComponent<NavWaypointNode>();
                var b = selected[1].GetComponent<NavWaypointNode>();

                if (a != null && b != null)
                {
                    if (!a.connections.Contains(b))
                        a.connections.Add(b);
                    if (!b.connections.Contains(a))
                        b.connections.Add(a);

                    EditorUtility.SetDirty(a);
                    EditorUtility.SetDirty(b);

                    Debug.Log($"✅ 已連接：{a.name} ↔ {b.name}");
                }
            }
            else
            {
                Debug.LogWarning("請在 Hierarchy 同時選取兩個路點物件！");
            }
        }
    }
}