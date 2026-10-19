using UnityEngine;
using UnityEditor;

public class SetupParkingLineRelay : EditorWindow
{
    [MenuItem("Tools/補上停車格壓線偵測（ParkingLineRelay）")]
    public static void Setup()
    {
        // 找出場景中所有 ParkingLineTrigger（掛在 LineTrigger側XX 父物件）
        ParkingLineTrigger[] triggers = Object.FindObjectsOfType<ParkingLineTrigger>(true);

        if (triggers.Length == 0)
        {
            Debug.LogWarning("[LineRelay] 場景找不到任何 ParkingLineTrigger！");
            return;
        }

        int added  = 0;
        int spaces = 0;

        foreach (var trig in triggers)
        {
            spaces++;
            // 這個父物件底下的每一條線 Collider
            Collider[] cols = trig.GetComponentsInChildren<Collider>(true);
            foreach (var col in cols)
            {
                // 跳過停車格範圍 Zone（那是另一套偵測）
                if (col.GetComponent<ParkingZoneTrigger>() != null) continue;

                // 確保是 Trigger
                col.isTrigger = true;

                // 沒有 Relay 才加
                if (col.GetComponent<ParkingLineRelay>() == null)
                {
                    Undo.AddComponent<ParkingLineRelay>(col.gameObject);
                    EditorUtility.SetDirty(col.gameObject);
                    added++;
                }
            }
        }

        Debug.Log($"✅ [LineRelay] 完成！掃描 {spaces} 個停車格，補上 {added} 個 ParkingLineRelay。");
    }
}