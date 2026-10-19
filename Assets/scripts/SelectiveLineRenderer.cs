using UnityEngine;
using UnityEngine.Rendering;

public class SelectiveLineRenderer : MonoBehaviour
{
    [Header("唯一看的到線條的攝影機")]
    public Camera subCamera;     // 拖入你的副攝影機 (例如倒車顯影/輸出攝影機)

    [Header("軌跡線清單")]
    public LineRenderer[] lines; // 拖入你的 4 條 LineRenderer

    void OnEnable()  => RenderPipelineManager.beginCameraRendering += OnCamRender;
    void OnDisable() => RenderPipelineManager.beginCameraRendering -= OnCamRender;

    void OnCamRender(ScriptableRenderContext src, Camera cam)
    {
        // 白名單邏輯：只有當「目前正在渲染的攝影機」是 subCamera 時才開啟線條，其餘一律關閉
        bool shouldShow = (cam == subCamera);
        SetLines(shouldShow);
    }

    void SetLines(bool visible)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] != null) 
            {
                lines[i].enabled = visible;
            }
        }
    }
}