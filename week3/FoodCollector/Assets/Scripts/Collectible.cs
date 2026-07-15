using UnityEngine;

// 플레이어와 닿으면 사라졌다가 스테이지 안 랜덤 위치에 다시 나타난다.
// (실제로는 파괴하지 않고 위치만 옮겨 개수를 항상 N개로 유지)
[RequireComponent(typeof(Collider))]
public class Collectible : MonoBehaviour
{
    [Tooltip("재배치 범위 (부모 로컬 기준 ±값). 벽 안쪽으로 여유를 둘 것")]
    public float spawnHalfExtent = 13f;
    [Tooltip("플레이어를 식별하는 태그")]
    public string playerTag = "Player";

    void Reset()
    {
        // 에디터에서 붙이는 순간 트리거로 설정
        GetComponent<Collider>().isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(playerTag))
            Respawn();
    }

    public void Respawn()
    {
        Vector3 lp = transform.localPosition;
        lp.x = Random.Range(-spawnHalfExtent, spawnHalfExtent);
        lp.z = Random.Range(-spawnHalfExtent, spawnHalfExtent);
        transform.localPosition = lp; // 높이(y)는 그대로 유지

        // Y축 회전 랜덤화 (X,Z 기울기는 유지)
        Vector3 e = transform.localEulerAngles;
        e.y = Random.Range(0f, 360f);
        transform.localEulerAngles = e;
    }
}
