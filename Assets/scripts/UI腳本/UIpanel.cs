using UnityEngine;

using UnityEngine.InputSystem;



public class UIPanelToggle : MonoBehaviour

{

    [Header("要控制的 UI 面板")]

    [SerializeField] private GameObject panelToToggle;



    [Header("輸入按鍵設定")]

    [SerializeField] private InputAction toggleAction;



    private void OnEnable()

    {

        // 啟用輸入監聽

        toggleAction.Enable();

        toggleAction.performed += OnTogglePerformed;

    }



    private void OnDisable()

    {

        // 註銷監聽並停用

        toggleAction.performed -= OnTogglePerformed;

        toggleAction.Disable();

    }



    private void OnTogglePerformed(InputAction.CallbackContext context)

    {

        TogglePanel();

    }



    // 切換顯示/隱藏的核心邏輯

    public void TogglePanel()

    {

        if (panelToToggle != null)

        {

            // 讀取當前狀態並反轉 (true -> false, false -> true)

            bool isActive = panelToToggle.activeSelf;

            panelToToggle.SetActive(!isActive);

        }

    }

}