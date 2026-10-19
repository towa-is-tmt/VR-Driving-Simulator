using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public class CarIndicatorController : MonoBehaviour
{
    public TextMeshPro left;
    public TextMeshPro right;
    public float blink = 0.4f;
    public string cleft = "l";
    public string cright = "r";
    public enum instate { Off, Left, Right, Hazard };
    public instate current = instate.Off;

    private Coroutine blinkCoroutine;
    private Coroutine autoCloseCoroutine;
    private bool islighton = false;
    private int soundCounter = 0;

    void Start()
    {
        ClearTexts();
        // 💡 初始化設定：把方向燈音效直接指定給 AudioSource 的 clip
        if (place != null && lights != null)
        {
            place.clip = lights;
            place.loop = false; // 確保它不要自己亂循環，由我們程式控制
        }
    }

    public void Onleft(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            if (current == instate.Left)
            {
                SetIndicatorState(instate.Off);
            }
            else
            {
                SetIndicatorState(instate.Left);
            }
        }
    }

    public void Onright(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            if (current == instate.Right)
            {
                SetIndicatorState(instate.Off);
            }
            else
            {
                SetIndicatorState(instate.Right);
            }
        }
    }

    public void Ondouble(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            if (current == instate.Hazard)
            {
                SetIndicatorState(instate.Off);
            }
            else
            {
                SetIndicatorState(instate.Hazard);
            }
        }
    }

    public void SetIndicatorState(instate newstate)
    {
        current = newstate;

        // 1. 清除文字閃爍與音效的協程
        if (blinkCoroutine != null)
        {
            StopCoroutine(blinkCoroutine);
            blinkCoroutine = null;
        }

        // 2. 清除舊的 10 秒倒數協程
        if (autoCloseCoroutine != null)
        {
            StopCoroutine(autoCloseCoroutine);
            autoCloseCoroutine = null;
        }

        ClearTexts();

        // 💡 關鍵修改：當狀態變成 Off（無論是手動按掉還是10秒時間到），立刻強制切斷聲音！
        if (current == instate.Off)
        {
            if (place != null)
            {
                place.Stop();
            }
        }
        else
        {
            blinkCoroutine = StartCoroutine(blinkroutine());

            // 如果是左右方向燈，就啟動 10 秒自動關閉
            if (current == instate.Left || current == instate.Right)
            {
                autoCloseCoroutine = StartCoroutine(AutoCloseRoutine(10f));
            }
        }
    }

    private IEnumerator AutoCloseRoutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        SetIndicatorState(instate.Off); // 10 秒時間到，自動切回關閉
    }

    private IEnumerator blinkroutine()
    {
        islighton = true;
        soundCounter = 0;

        while (true)
        {
            if (islighton)
            {
                bool needPlaySound = false;

                if (current == instate.Left || current == instate.Hazard)
                {
                    if (left != null) left.text = $"<color=green>{cleft}</color>";
                    needPlaySound = true;
                }
                if (current == instate.Right || current == instate.Hazard)
                {
                    if (right != null) right.text = $"<color=green>{cright}</color>";
                    needPlaySound = true;
                }

                // 💡 改用優化後的 playlight()
                if (needPlaySound && soundCounter % 2 == 0)
                {
                    playlight();
                }

                soundCounter++;
            }
            else
            {
                ClearTexts();
            }
            islighton = !islighton;
            yield return new WaitForSeconds(blink);
        }
    }

    private void ClearTexts()
    {
        if (left != null) left.text = "";
        if (right != null) right.text = "";
    }

    [Header("音效設定")]
    public AudioSource score;
    public AudioClip baba;
    public AudioSource place;
    public AudioClip lights;

    // 💡 修正 1：修改 Onspeaker，同時處理「按下」與「放開」
    public void Onspeaker(InputAction.CallbackContext context)
    {
        HandleHornInput(context);
    }

    public void Onspeakers(InputAction.CallbackContext context)
    {
        HandleHornInput(context);
    }
    /// 統一處理喇叭的按鍵狀態
    private void HandleHornInput(InputAction.CallbackContext context)
    {
        if (score == null || baba == null) return;

        // 1. 當玩家「剛按下」按鍵
        if (context.started)
        {
            // 如果喇叭本來就沒在叫，才播放（防止 Input System 重複觸發造成聲音突兀斷掉）
            if (!score.isPlaying)
            {
                score.clip = baba;
                score.loop = true; // 💡 開啟循環，這樣長按才會一直叫
                score.Play();
                Debug.Log("baba start");
            }
        }
        // 2. 當玩家「放開」按鍵
        else if (context.canceled)
        {
            score.Stop(); // 💡 立刻切斷聲音
            score.loop = false; // 關閉循環，恢復預設
            Debug.Log("baba stop");
        }
    }

    // 💡 關鍵修改：改用 .Play() 播放
    private void playlight()
    {
        if (place != null && lights != null)
        {
            place.Play(); // 改成 Play() 之後，只要外部 call place.Stop() 就會被強制掐斷
            Debug.Log("light");
        }
    }
}