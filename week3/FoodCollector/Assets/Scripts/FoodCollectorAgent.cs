using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class FoodAgent : MonoBehaviour
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
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
    }

    void Update()
    {
        SetAnimation();
    }

    void FixedUpdate()
    {
        MoveAgent();
    }

    public void MoveAgent()
    {
        // 수평 속도만 설정하고 중력(y)은 유지 -> 바닥에 붙어서 이동
        Vector3 vel = moveInput * moveSpeed;
        vel.y = rb.linearVelocity.y;
        rb.linearVelocity = vel;

        // 회전은 아래 MoveRotation으로만 제어.
        rb.angularVelocity = Vector3.zero;

        // 진행 방향으로 회전
        if (moveInput.sqrMagnitude > 0.01f)
        {
            Quaternion target = Quaternion.LookRotation(moveInput, Vector3.up);
            rb.MoveRotation(Quaternion.RotateTowards(rb.rotation, target, turnSpeed * Time.fixedDeltaTime));
        }
    }

    void SetAnimation()
    {
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
            animator.speed = animationSpeed;
            animator.SetFloat("Speed", moveInput.magnitude);
        }
    }
}
