using UnityEngine;

// 대상을 고정된 오프셋으로 따라간다. 회전은 하지 않으므로(각도 고정)
// 월드 기준 WASD 이동이 헷갈리지 않는다.
public class CameraFollow : MonoBehaviour
{
    public Transform target;
    public Vector3 offset = new Vector3(0f, 9f, -5f);
    [Tooltip("따라가는 부드러움 (0에 가까울수록 즉시)")]
    public float smoothTime = 0.15f;

    Vector3 vel;

    void LateUpdate()
    {
        if (target == null) return;
        Vector3 goal = target.position + offset;
        transform.position = Vector3.SmoothDamp(transform.position, goal, ref vel, smoothTime);
    }
}
