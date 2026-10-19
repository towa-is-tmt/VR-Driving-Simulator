using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AudioManager : MonoBehaviour
{
    [Header("音效組件")]
    [Tooltip("請把掛載在物件上的 AudioSource 拖進來")]
    public AudioSource audioSource;

    [Header("設定參數")]
    [Tooltip("載入遊戲後要延遲幾秒才播放音效")]
    public float delayTime = 1.0f;

    private void Start()
    {
        Debug.Log("<color=white>[音效系統]</color> AudioManager 腳本已成功在 Start() 啟動！");

        // 1. 自動抓取組件
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        // 2. 嚴格安全性與狀態檢查
        if (audioSource == null)
        {
            Debug.LogError("<color=red>[音效錯誤]</color> 嚴重慘劇！這個物件上根本沒有掛載 AudioSource 組件，且 Inspector 欄位也是空的！");
            return;
        }

        if (audioSource.clip == null)
        {
            Debug.LogError($"<color=red>[音效錯誤]</color> 抓到了！物件 '{gameObject.name}' 上的 AudioSource 裡面，『AudioClip』欄位是空的！你忘記把 Assets 裡的音效拖進去了！");
            return;
        }

        // 3. 檢查是不是被 3D 音效距離坑了
        if (audioSource.spatialBlend > 0.5f)
        {
            Debug.LogWarning($"<color=orange>[音效提示]</color> 目前偵測到你開啟了 3D 音效 (Spatial Blend = {audioSource.spatialBlend})。如果物件縮放比不對或距離太遠，會完全聽不到聲音喔！");
        }

        // 4. 通過檢查，啟動協程
        StartCoroutine(PlayAudioAfterDelay());
    }

    private IEnumerator PlayAudioAfterDelay()
    {
        Debug.Log($"<color=yellow>[音效系統]</color> 協程啟動！開始倒數 {delayTime} 秒...");
        
        // 讓程式在這裡「暫停」等待 1 秒
        yield return new WaitForSeconds(delayTime);

        // 5. 播放前一刻的最終雙重確認
        if (audioSource != null && audioSource.clip != null)
        {
            // 強制設定音量為最大 (1.0f)，排除被調成靜音的可能
            audioSource.volume = 1.0f; 
            
            // 執行播放
            audioSource.Play();
            
            Debug.Log($"<color=lime>[音效播報成功]</color> 💥 轟！時間到！已成功對 AudioSource 下達 Play 指令！播放的音訊名稱為: {audioSource.clip.name}");
        }
        else
        {
            Debug.LogError("<color=red>[音效錯誤]</color> 在等待的 1 秒期間，AudioSource 或 Clip 突然不見了！");
        }
    }
}