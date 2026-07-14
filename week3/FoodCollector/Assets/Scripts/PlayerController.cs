using UnityEngine;
using UnityEngine.InputSystem;

// 레거시 Input이 막혀 있어 New Input System(Keyboard.current)으로 입력을 읽는다.
[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
    [Tooltip("이동 힘 배율 (레퍼런스 moveSpeed)")]
    public float moveSpeed = 2f;
    [Tooltip("회전 속도 (deg/s, 레퍼런스 turnSpeed)")]
    public float turnSpeed = 300f;
    [Tooltip("애니메이션 재생 속도 배율 (1 = 원본)")]
    public float animationSpeed = 1f;

    Rigidbody rb;
    Animator animator;

    // 레퍼런스 continuousActions 대응 (Heuristic로 채운다)
    float m_Forward; // W/S -> actions[0]
    float m_Rotate;  // A/D -> actions[2]

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    void Update()
    {
        Heuristic();
        SetAnimation();
    }

    void FixedUpdate()
    {
        MoveAgent();
    }

    // 레퍼런스 FoodCollectorAgent.Heuristic()
    void Heuristic()
    {
        m_Forward = 0f;
        m_Rotate = 0f;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    m_Forward = 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  m_Forward = -1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) m_Rotate = 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  m_Rotate = -1f;
    }

    void MoveAgent()
    {
        Vector3 dirToGo = transform.forward * m_Forward;
        Vector3 rotateDir = transform.up * m_Rotate;

        rb.AddForce(dirToGo * moveSpeed, ForceMode.VelocityChange);
        transform.Rotate(rotateDir, Time.fixedDeltaTime * turnSpeed);

        if (rb.linearVelocity.sqrMagnitude > 25f) // 최고 속도 제한
            rb.linearVelocity *= 0.95f;

        // 벽 접촉 마찰로 생기는 회전 누적 방지 (회전은 transform.Rotate로만 제어)
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
