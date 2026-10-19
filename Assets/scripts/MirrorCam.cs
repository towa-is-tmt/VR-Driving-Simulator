using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MirrorCam : MonoBehaviour
{
    public Transform playerCamera; // 玩家的頭 (Main Camera)
    public Transform mirrorPlane;
    private float initialLocalZ; // 用來儲存一開始在 Scene 擺好的深度
    // Start is called before the first frame update
    void Start()
    {
        if (mirrorPlane != null)
        {
            // 取得相機相對於鏡子的初始 Z 軸位置
            initialLocalZ = mirrorPlane.InverseTransformPoint(transform.position).z;
        }
    }
    void LateUpdate()
    {
        if (playerCamera == null || mirrorPlane == null) return;

        // 步驟 1：取得玩家相對於鏡子的位置 (例如：右2, 前5)
        Vector3 localPlayer = mirrorPlane.InverseTransformPoint(playerCamera.position);
        // 步驟 2：反轉 Z 軸，得到對稱點 (例如：右2, 後5)
        Vector3 mirrorRelativePos = new Vector3(localPlayer.x, localPlayer.y-0.2f, -initialLocalZ);
        // 步驟 3：將對稱點轉換回世界座標，並移動攝影機
        transform.position = mirrorPlane.TransformPoint(mirrorRelativePos);

        // 步驟 4：讓攝影機回頭盯著鏡子看
        transform.LookAt(mirrorPlane.position, mirrorPlane.up);
       
        //Vector3 reflectedForward = Vector3.Reflect(playerCamera.forward, mirrorPlane.forward);
        //Vector3 reflectedUp = Vector3.Reflect(playerCamera.up, mirrorPlane.forward);

        // 將反射後的向量應用到 Camera 上
        //transform.rotation = Quaternion.LookRotation(reflectedForward, reflectedUp);
    }
}

// 頭對鏡子的相對座標
// 鏡子的相機移動到相對位置  -Z
// 鏡子相機的世界座標  !!  移動相機UnityEditor.TransformWorldPlacementJSON:{"position":{"x":410.87982177734377,"y":2.7455291748046877,"z":960.443359375},"rotation":{"x":-0.01988477259874344,"y":0.765572726726532,"z":0.008157080970704556,"w":0.6429905295372009},"scale":{"x":1.5,"y":1.4999998807907105,"z":1.4999998807907105}}
