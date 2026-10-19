using System.Collections; // 必須加這行，才能使用 IEnumerator
using UnityEngine;
using UnityEngine.SceneManagement;

public class loadgame : MonoBehaviour
{
    // 這是給按鈕點擊呼叫的主功能
    public void ChangeScene(string sceneName)
    {
        // 啟動協程，讓它在背景倒數計時
        StartCoroutine(WaitAndLoad(sceneName));
    }

    // 這是在背景默默等待的計時器
    private IEnumerator WaitAndLoad(string sceneName)
    {
        // 這裡填寫你要等待的時間（秒），假設鐵捲門打開動畫總共跑 2.5 秒
        yield return new WaitForSeconds(5f);

        // 時間到了，這時才真正執行跳轉場景
        SceneManager.LoadScene(sceneName);
    }
}