using System.Collections.Generic;

// ── 跨場景暫存：車庫選好的設定，帶到駕駛場景 ──────────
// static 不掛任何物件，LoadScene 不會銷毀它
public static class TrainingSession
{
    public enum Mode { None, Training, Free }

    // 這次要玩的模式
    public static Mode gameMode = Mode.None;

    // 訓練勾選的項目名稱（要和 TrainingItemController.itemName 一致）
    public static List<string> selectedItems = new List<string>();

    // 清空（回車庫重選時呼叫）
    public static void Clear()
    {
        gameMode = Mode.None;
        selectedItems.Clear();
    }
}