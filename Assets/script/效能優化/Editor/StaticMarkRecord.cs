using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 靜態標記工具的「還原紀錄」。
/// 每次按「套用標記」都會把被改動的物件與它原本的 Static 旗標存在這個檔案，
/// 之後按「還原上次標記」就能一個不漏地改回去（重開 Unity 也有效）。
/// </summary>
public class StaticMarkRecord : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("物件的全域識別碼（GlobalObjectId），重開 Unity 也找得到")]
        public string id;
        [Tooltip("改動前的 Static 旗標")]
        public int originalFlags;
        [Tooltip("物件在階層中的路徑（只給人看，方便確認）")]
        public string path;
    }

    public string sceneName;
    public string time;
    public List<Entry> entries = new List<Entry>();
}
