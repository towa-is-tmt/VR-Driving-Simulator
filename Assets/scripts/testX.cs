using UnityEngine;

public class testX : MonoBehaviour
{
    // 移動速度，可以在 Unity 屬性面板中調整
    public float speed = 5.0f;

    void Update()
    {
        // 初始化移動向量
        Vector3 movement = Vector3.zero;

        // Y 鍵控制 X 軸正向 (右)，H 鍵控制 X 軸負向 (左)
        if (Input.GetKey(KeyCode.Y)) movement.x += 1;
        if (Input.GetKey(KeyCode.H)) movement.x -= 1;

        // G 鍵控制 Z 軸正向 (前)，J 鍵控制 Z 軸負向 (後)
        if (Input.GetKey(KeyCode.G)) movement.z += 1;
        if (Input.GetKey(KeyCode.J)) movement.z -= 1;

        // 根據時間與速度更新位置
        transform.Translate(movement.normalized * speed * Time.deltaTime);
    }
}