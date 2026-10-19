using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class car_move1 : MonoBehaviour
{
    public float moveSpeed = 10f;   // 前進速度
    public float turnSpeed = 50f;   // 旋轉速度

    void Update()
    {
        // 1. 取得輸入 (W/S 控制前後, A/D 控制左右)
        float move = -Input.GetAxis("Vertical") * moveSpeed * Time.deltaTime;
        float rotate = Input.GetAxis("Horizontal") * turnSpeed * Time.deltaTime;

        // 2. 執行移動 (朝著物件的前方 Z 軸)
        transform.Translate(0, 0, move);

        // 3. 執行旋轉 (繞著物件的垂直 Y 軸)
        transform.Rotate(0, rotate, 0);
    }
}