using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TrainingItemSelector : MonoBehaviour
{
    [System.Serializable]
    public class Item
    {
        public string itemName;
        public Toggle toggle;
    }

    [Header("訓練項目（開放式清單，可增減）")]
    public List<Item> items = new List<Item>();

    public void SelectAll()
    {
        foreach (var it in items) if (it.toggle != null) it.toggle.isOn = true;
    }

    public void ClearAll()
    {
        foreach (var it in items) if (it.toggle != null) it.toggle.isOn = false;
    }

    public List<string> GetSelected()
    {
        var result = new List<string>();
        foreach (var it in items)
            if (it.toggle != null && it.toggle.isOn) result.Add(it.itemName);
        return result;
    }
}