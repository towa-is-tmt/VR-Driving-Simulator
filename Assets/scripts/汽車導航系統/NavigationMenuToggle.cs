using UnityEngine;

public class NavigationMenuToggle : MonoBehaviour
{
    [Header("選單面板")]
    public GameObject menuPanel;

    [Header("開關按鍵")]
    public KeyCode toggleKey = KeyCode.N;

    [Header("顯示設定")]
    [Tooltip("勾選＝導航選單固定顯示在畫面上、不受按鍵開關；取消＝維持原本按鍵切換")]
    public bool 固定顯示 = true;

    private bool isVisible = false;

    private void Start()
    {
        // 固定顯示＝開場就顯示；否則沿用原本預設隱藏
        isVisible = 固定顯示;
        menuPanel.SetActive(isVisible);

        // 固定顯示時同時解鎖並顯示滑鼠游標（讓選單一直可點）
        if (固定顯示)
        {
            Cursor.visible   = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    private void Update()
    {
        // 固定顯示時忽略按鍵開關，選單不會被隱藏
        if (固定顯示) return;

        if (Input.GetKeyDown(toggleKey))
        {
            isVisible = !isVisible;
            menuPanel.SetActive(isVisible);

            // 開選單時顯示滑鼠游標
            Cursor.visible   = isVisible;
            Cursor.lockState = isVisible ? CursorLockMode.None : CursorLockMode.Locked;
        }
    }
}
