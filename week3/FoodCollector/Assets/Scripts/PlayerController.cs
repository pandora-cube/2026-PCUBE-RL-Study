using UnityEngine;
using UnityEngine.InputSystem;

// WASD(또는 방향키)로 캐릭터를 월드 XZ 평면에서 이동시키고,
// 이동 방향을 바라보게 회전시키며, Animator의 "Speed"로 걷기/대기 전환을 구동한다.
[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
    [Tooltip("이동 속도 (m/s)")]
    public float moveSpeed = 5f;
    [Tooltip("회전 속도 (deg/s)")]
    public float turnSpeed = 720f;
    [Tooltip("애니메이션 재생 속도 배율 (1 = 원본)")]
    public float animationSpeed = 1f;

    Rigidbody rb;
    Animator animator;
    Vector3 moveInput;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        animator = GetComponent<Animator>();
        // 넘어지지 않도록 X,Z 회전만 잠그고 Y(방향 전환)는 스크립트로 제어
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    void Update()
    {
        // 새 Input System: 별도 에셋 없이 키보드를 직접 읽는다.
        var kb = Keyboard.current;
        float h = 0f, v = 0f;
        if (kb != null)
        {
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  h -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    v += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  v -= 1f;
        }
        moveInput = new Vector3(h, 0f, v);
        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();

        if (animator != null)
        {
            animator.speed = animationSpeed;                 // 인스펙터에서 실시간 조절 가능
            animator.SetFloat("Speed", moveInput.magnitude);
        }
    }

    void FixedUpdate()
    {
        // 수평 속도만 설정하고 중력(y)은 유지 -> 바닥에 붙어서 이동
        Vector3 vel = moveInput * moveSpeed;
        vel.y = rb.linearVelocity.y;
        rb.linearVelocity = vel;

        // 벽에 닿을 때 접촉 마찰로 생기는 회전(각속도)이 누적돼 캐릭터가
        // 이상하게 도는 것을 막는다. 회전은 아래 MoveRotation으로만 제어.
        rb.angularVelocity = Vector3.zero;

        // 진행 방향으로 회전
        if (moveInput.sqrMagnitude > 0.01f)
        {
            Quaternion target = Quaternion.LookRotation(moveInput, Vector3.up);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, turnSpeed * Time.fixedDeltaTime));
        }
    }
}
