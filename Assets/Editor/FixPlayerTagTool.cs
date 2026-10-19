#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 🏷 Player Tag 修正工具
///
/// 問題背景:
///   場景裡只要有超過一個物件掛著 "Player" 標籤,
///   GameObject.FindGameObjectWithTag("Player") 會回傳「任意」一個,
///   AI 就可能抓到沒有 Rigidbody / DriveCar 的空物件,
///   導致倒車偵測、危險因子等全部失效,而且每次執行抓到的還可能不一樣。
///
/// 這個工具會:
///   1. 掃描目前開啟的場景(含停用物件)中所有 Tag = Player 的物件
///   2. 找出「唯一合格」的那個 —— 必須同時有 Rigidbody 和 DriveCar
///   3. 把其餘全部改成 Untagged
///
/// 選單:Tools → 🏷 修正 Player Tag (只保留有 Rigidbody+DriveCar 的)
/// 先用「只檢查不修改」看一遍,確認無誤再執行修正。
/// </summary>
public static class FixPlayerTagTool
{
    private const string MenuFix   = "Tools/🏷 修正 Player Tag (只保留有 Rigidbody+DriveCar 的)";
    private const string MenuCheck = "Tools/🏷 檢查 Player Tag (只列出,不修改)";

    [MenuItem(MenuCheck)]
    private static void CheckOnly() { Run(false); }

    [MenuItem(MenuFix)]
    private static void FixAll() { Run(true); }

    private static void Run(bool doFix)
    {
        List<GameObject> tagged = new List<GameObject>();

        // Resources.FindObjectsOfTypeAll 連停用物件也找得到,
        // 這點很重要 —— 停用物件被啟用後一樣會干擾 FindGameObjectWithTag
        foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null) continue;
            if (EditorUtility.IsPersistent(go)) continue;          // 排除 Project 裡的 prefab 資產
            if (go.hideFlags != HideFlags.None) continue;          // 排除編輯器內部物件
            if (!go.CompareTag("Player")) continue;
            tagged.Add(go);
        }

        if (tagged.Count == 0)
        {
            Debug.LogWarning("🏷 場景中找不到任何 Tag = Player 的物件。玩家車可能沒設標籤。");
            return;
        }

        // 找出合格者:同時有 Rigidbody 和 DriveCar
        GameObject keeper = null;
        foreach (GameObject go in tagged)
        {
            if (go.GetComponent<Rigidbody>() != null && go.GetComponent<DriveCar>() != null)
            {
                if (keeper == null) keeper = go;
                else Debug.LogWarning($"🏷 有超過一個合格物件,保留第一個:{Path(keeper)},另一個是 {Path(go)}");
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"🏷 掃到 {tagged.Count} 個 Tag = Player 的物件:");
        foreach (GameObject go in tagged)
        {
            bool hasRb = go.GetComponent<Rigidbody>() != null;
            bool hasDc = go.GetComponent<DriveCar>() != null;
            string verdict = (go == keeper) ? "✓ 保留(合格)" : "✗ 應改為 Untagged";
            sb.AppendLine($"   {verdict}  {Path(go)}");
            sb.AppendLine($"        Rigidbody={(hasRb ? "有" : "無")}  DriveCar={(hasDc ? "有" : "無")}  " +
                          $"啟用中={(go.activeInHierarchy ? "是" : "否")}");
        }

        if (keeper == null)
        {
            sb.AppendLine();
            sb.AppendLine("★ 找不到「同時有 Rigidbody 和 DriveCar」的物件,為了安全起見不做任何修改。");
            sb.AppendLine("   請先確認玩家車的 Rigidbody 和 DriveCar 在同一個物件上,再執行一次。");
            Debug.LogError(sb.ToString());
            return;
        }

        if (!doFix)
        {
            sb.AppendLine();
            sb.AppendLine("(這是檢查模式,沒有改動任何東西。要修正請執行 Tools → 🏷 修正 Player Tag)");
            Debug.Log(sb.ToString());
            return;
        }

        int changed = 0;
        foreach (GameObject go in tagged)
        {
            if (go == keeper) continue;
            Undo.RecordObject(go, "Fix Player Tag");
            go.tag = "Untagged";
            EditorUtility.SetDirty(go);
            changed++;
        }

        if (changed > 0)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        sb.AppendLine();
        sb.AppendLine($"✅ 已將 {changed} 個物件改為 Untagged,只留下 {Path(keeper)}。");
        sb.AppendLine("   記得按 Ctrl+S 存檔。可用 Ctrl+Z 復原。");
        Debug.Log(sb.ToString());
    }

    private static string Path(GameObject go)
    {
        Transform t = go.transform;
        string path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + " / " + path;
        }
        return path;
    }
}
#endif
