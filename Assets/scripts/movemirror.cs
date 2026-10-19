using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class movemirror : MonoBehaviour
{
    
    public float rotationSpeed = 50f;
    //public Transform mirrorCamera;

    void Update()
    {
        RotateControl();
    }

    void RotateControl()
    {
        // 取得當前的歐拉角 (Euler Angles)
        //Vector3 currentRotation = transform.eulerAngles;

        // --- X 軸旋轉控制 ---
        // 按下 5 向上旋轉 (減少 X)
        if (Input.GetKey(KeyCode.Keypad5))
        {
            transform.Rotate(Vector3.right * rotationSpeed * Time.deltaTime);
        }
        // 按下 2 向下旋轉 (增加 X)
        if (Input.GetKey(KeyCode.Keypad2))
        {
            transform.Rotate(Vector3.left * rotationSpeed * Time.deltaTime);
        }

        // --- Y 軸旋轉控制 ---
        // 按下 1 向左旋轉 (減少 Y)
        if (Input.GetKey(KeyCode.Keypad1))
        {
            transform.Rotate(Vector3.up * rotationSpeed * Time.deltaTime);
        }
        // 按下 3 向右旋轉 (增加 Y)
        if (Input.GetKey(KeyCode.Keypad3))
        {
            transform.Rotate(Vector3.down * rotationSpeed * Time.deltaTime);
        }
    }
}