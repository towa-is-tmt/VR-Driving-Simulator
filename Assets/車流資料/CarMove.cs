using UnityEngine;

public class CarMove : MonoBehaviour
{
    public float speed = 12f;
    private Vector3 initialScenePosition; // 鎖定擺放位置
    private Vector3 startPos;
    public bool isMoving = false;
    private float delayTimer = 0f;

    void Start()
    {
        initialScenePosition = transform.localPosition;
        ResetCar();
    }

    void Update()
    {
        if (!isMoving)
        {
            delayTimer -= Time.deltaTime;
            if (delayTimer <= 0) isMoving = true;
            return;
        }

        // 沿著自己的前方移動
        transform.Translate(Vector3.forward * speed * Time.deltaTime);

        // 判定車子是否已經開過頭 (距離起點 ?m)
        if (Vector3.Distance(startPos, transform.localPosition) > 100f)
        {
            ResetCar();
        }
    }

    public void ResetCar()
    {
        isMoving = false;
        // 讓車子出現的位置在 25m ~ 40m 之間隨機，而不是固定 startPos
        transform.localPosition = initialScenePosition + new Vector3(0, 0, Random.Range(0f, 20f));
        startPos = transform.localPosition;
        //Debug.Log($"車子重置！位置：{transform.localPosition.z}");
        delayTimer = Random.Range(0f, 2f); // 隨機延遲，讓 AI 無法死背時間
        speed = Random.Range(11f, 18f); // 模擬不同車速
    }
    
    // 增加一個屬性讓別人可以直接拿 km/h
    public float SpeedKmh => speed * 3.6f;
}