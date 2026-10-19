using UnityEngine;
using System.Collections;

public class SimpleTrafficLight : MonoBehaviour
{
    [Header("�O�w����]�w")]
    public GameObject redLightObj;
    public GameObject yellowLightObj;
    public GameObject greenLightObj;

    [Header("�ɶ��]�w (��)")]
    public float greenTime = 5f;
    public float yellowTime = 2f;
    public float redTime = 5f;

    // �� AI Ū�������A�G���O�ζ��O�ɬ� true
    [HideInInspector]
    public bool isRedOrYellow = false;

    // 只有「紅燈」為 true。isRedOrYellow 把黃燈也算進去，
    // 但闖紅燈的判定必須排除黃燈（黃燈通過不算違規）。
    [HideInInspector]
    public bool isRed = false;

    void Start()
    {
        if (redLightObj == null || yellowLightObj == null || greenLightObj == null)
        {
            // �ץ��B�G�ϥΧ�����| UnityEngine.Debug
            UnityEngine.Debug.LogError("<color=red>�i���~�j����Ѧҿ򥢡I���ˬd Inspector�C</color>");
            return;
        }
        StartCoroutine(TrafficCycle());
    }

    IEnumerator TrafficCycle()
    {
        while (true)
        {
            // 1. ��O
            isRedOrYellow = false; isRed = false;
            SetLightStatus(false, false, true);
            yield return new WaitForSeconds(greenTime);

            // 2. ���O
            isRedOrYellow = true; isRed = false;   // 黃燈
            SetLightStatus(false, true, false);
            yield return new WaitForSeconds(yellowTime);

            // 3. ���O
            isRedOrYellow = true; isRed = true;    // 紅燈
            SetLightStatus(true, false, false);
            yield return new WaitForSeconds(redTime);
        }
    }

    void SetLightStatus(bool red, bool yellow, bool green)
    {
        redLightObj.GetComponent<Renderer>().material.color = red ? Color.red : Color.gray;
        yellowLightObj.GetComponent<Renderer>().material.color = yellow ? Color.yellow : Color.gray;
        greenLightObj.GetComponent<Renderer>().material.color = green ? Color.green : Color.gray;

        if (red) redLightObj.tag = "RedLight";
        else if (yellow) yellowLightObj.tag = "YellowLight";
        else { redLightObj.tag = "Untagged"; yellowLightObj.tag = "Untagged"; }
    }
}