using UnityEngine;
using UnityEngine.InputSystem;

public enum Team
{
    Blue = 0,
    Red = 1
}

// 앞으로 적용할 것: bservations / rewards / policy
// 현재 적용된 것: movement, kick physics
public class AgentSoccer : MonoBehaviour
{
    public enum Position
    {
        Striker,
        Goalie,
        Generic
    }

    // Set per-player in the prefab/scene (blue vs red, and role).
    public Team team;
    public Position position;

    float m_KickPower;
    const float k_Power = 2000f;
    float m_LateralSpeed;
    float m_ForwardSpeed;

    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;

    // Reset anchor + spawn rotation, read by SoccerEnvController.ResetScene().
    public Vector3 initialPos;
    public float rotSign;

    void Awake()
    {
        if (team == Team.Blue)
        {
            initialPos = new Vector3(transform.position.x - 5f, .5f, transform.position.z);
            rotSign = 1f;
        }
        else
        {
            initialPos = new Vector3(transform.position.x + 5f, .5f, transform.position.z);
            rotSign = -1f;
        }

        if (position == Position.Goalie)
        {
            m_LateralSpeed = 1.0f;
            m_ForwardSpeed = 1.0f;
        }
        else if (position == Position.Striker)
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.3f;
        }
        else
        {
            m_LateralSpeed = 0.3f;
            m_ForwardSpeed = 1.0f;
        }

        m_SoccerSettings = FindFirstObjectByType<SoccerSettings>();
        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;
    }

    void FixedUpdate()
    {
        MoveByInput();
    }

    // Keyboard control (every keyboard player shares these keys):
    // W/S = forward/back, Q/E = strafe left/right, A/D = rotate.
    void MoveByInput()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        m_KickPower = 0f;

        if (kb.wKey.isPressed)
        {
            dirToGo = transform.forward * m_ForwardSpeed;
            m_KickPower = 1f;
        }
        else if (kb.sKey.isPressed)
        {
            dirToGo = transform.forward * -m_ForwardSpeed;
        }

        // Strafe overrides forward, matching the original discrete-action move.
        if (kb.eKey.isPressed)
        {
            dirToGo = transform.right * m_LateralSpeed;
        }
        else if (kb.qKey.isPressed)
        {
            dirToGo = transform.right * -m_LateralSpeed;
        }

        if (kb.aKey.isPressed)
        {
            rotateDir = transform.up * -1f;
        }
        else if (kb.dKey.isPressed)
        {
            rotateDir = transform.up * 1f;
        }

        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed, ForceMode.VelocityChange);
    }

    /// <summary>
    /// Used to provide a "kick" to the ball.
    /// </summary>
    void OnCollisionEnter(Collision c)
    {
        var force = k_Power * m_KickPower;
        if (position == Position.Goalie)
        {
            force = k_Power;
        }
        if (c.gameObject.CompareTag("ball"))
        {
            var dir = c.contacts[0].point - transform.position;
            dir = dir.normalized;
            c.gameObject.GetComponent<Rigidbody>().AddForce(dir * force);
        }
    }
}
