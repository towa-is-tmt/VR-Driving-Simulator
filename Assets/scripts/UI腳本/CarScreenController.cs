using UnityEngine;

// 車內電子螢幕：倒車檔顯示倒車鏡頭，其餘顯示小地圖
public class CarScreenController : MonoBehaviour
{
    [Header("檔位（留空自動找）")]
    public CarGearController gearController;

    [Header("兩個面板（重疊，切換顯示）")]
    public GameObject minimapPanel;   // 小地圖(RawImage + 圖示)
    public GameObject backupPanel;    // 倒車畫面(RawImage = RT_倒車)

    [Header("相機（省效能：只開需要的）")]
    public Camera minimapCamera;
    public Camera backupCamera;

    private bool lastReversing = false;

    void Start()
    {
        if (gearController == null) gearController = FindObjectOfType<CarGearController>();
        Apply(false);   // 開場顯示小地圖
    }

    void Update()
    {
        if (gearController == null) return;
        bool reversing = gearController.currentGear == CarGearController.AutomaticGear.R;
        if (reversing != lastReversing) Apply(reversing);
    }

    void Apply(bool reversing)
    {
        lastReversing = reversing;
        if (minimapPanel != null)  minimapPanel.SetActive(!reversing);
        if (backupPanel != null)   backupPanel.SetActive(reversing);
        if (minimapCamera != null) minimapCamera.enabled = !reversing;  // 倒車時關小地圖相機
        if (backupCamera != null)  backupCamera.enabled  = reversing;   // 只倒車時開倒車相機
    }
}