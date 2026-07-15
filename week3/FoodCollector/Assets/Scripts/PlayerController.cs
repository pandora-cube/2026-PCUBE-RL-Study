using UnityEngine;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : Agent
{
    [Tooltip("이동 힘 배율 (레퍼런스 moveSpeed)")]
    public float moveSpeed = 2f;
    [Tooltip("회전 속도 (deg/s, 레퍼런스 turnSpeed)")]
    public float turnSpeed = 300f;
    [Tooltip("애니메이션 재생 속도 배율 (1 = 원본)")]
    public float animationSpeed = 1f;
    [Tooltip("에피소드 시작 스폰 범위 (부모 로컬 ±). 벽 안쪽으로 여유를 둘 것")]
    public float spawnRange = 11f;

    Rigidbody rb;
    Animator animator;

    float m_Forward;
    float m_Rotate;
    FoodArea m_Area; // 음식 재배치를 담당하는 area

    public override void Initialize()
    {
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        // 음식 관리는 area가 담당. 상위 계층에서 FoodArea를 찾는다.
        m_Area = GetComponentInParent<FoodArea>();
    }

    public override void OnEpisodeBegin()
    {
        // 위치·회전·속도 초기화 (스폰을 랜덤화해 탐색 다양성 확보)
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.localPosition = new Vector3(
            Random.Range(-spawnRange, spawnRange), 0.6f, Random.Range(-spawnRange, spawnRange));
        transform.localEulerAngles = new Vector3(0f, Random.Range(0f, 360f), 0f);

        // 매 에피소드마다 area가 모든 음식을 랜덤 재배치 → 에피소드를 독립적으로
        if (m_Area != null) m_Area.ResetFoods();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 로컬 x, z 속도 관측
        var localVelocity = transform.InverseTransformDirection(rb.linearVelocity);
        sensor.AddObservation(localVelocity.x);
        sensor.AddObservation(localVelocity.z);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // 정책은 [-1,1] 밖 값을 낼 수 있으므로 클램프 (Heuristic ±1과 스케일 일치)
        m_Forward = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        m_Rotate  = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

        // 시간 패널티: 빨리 먹도록 유도. MaxStep 무제한(0)이면 0으로 나눔 방지로 스킵.
        if (MaxStep > 0) AddReward(-1f / MaxStep);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // 상태(m_Forward/m_Rotate) 세팅은 OnActionReceived 한 곳에서만. 여기선 액션만 채운다.
        var ca = actionsOut.ContinuousActions;
        ca[0] = 0f;
        ca[1] = 0f;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    ca[0] = 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  ca[0] = -1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) ca[1] = 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  ca[1] = -1f;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("food"))
        {
            AddReward(1f);
            Debug.Log($"[Food] +1 (누적 {GetCumulativeReward():F2})");
            if (other.TryGetComponent(out Collectible c)) c.Respawn();
        }
        else if (other.CompareTag("badFood"))
        {
            AddReward(-1f);
            Debug.Log($"[BadFood] -1 (누적 {GetCumulativeReward():F2})");
            if (other.TryGetComponent(out Collectible c)) c.Respawn();
        }
    }

    void Update()
    {
        SetAnimation();
    }

    void FixedUpdate()
    {
        MoveAgent();
    }

    void MoveAgent()
    {
        Vector3 dirToGo = transform.forward * m_Forward;
        rb.AddForce(dirToGo * moveSpeed, ForceMode.VelocityChange);

        float yaw = m_Rotate * turnSpeed * Time.fixedDeltaTime;
        rb.MoveRotation(rb.rotation * Quaternion.AngleAxis(yaw, Vector3.up));

        if (rb.linearVelocity.sqrMagnitude > 25f) // 최고 속도 제한
            rb.linearVelocity *= 0.95f;

        // 벽 접촉 마찰로 생기는 회전 누적 방지
        rb.angularVelocity = Vector3.zero;
    }

    void SetAnimation()
    {
        if (animator == null) return;
        animator.speed = animationSpeed;
        Vector3 hv = rb.linearVelocity; hv.y = 0f;
        animator.SetFloat("Speed", hv.magnitude);
    }
}
