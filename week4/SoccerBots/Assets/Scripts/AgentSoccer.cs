using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

public enum Team
{
    Blue = 0,
    Red = 1
}

public class AgentSoccer : Agent
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
    float m_ExistentialReward;

    [HideInInspector]
    public Rigidbody agentRb;
    SoccerSettings m_SoccerSettings;
    SoccerEnvController m_EnvController;

    // Reset anchor + spawn rotation, read by SoccerEnvController.ResetScene().
    public Vector3 initialPos;
    public float rotSign;

    public override void Initialize()
    {
        initialPos = transform.position;
        rotSign = team == Team.Blue ? 1f : -1f;

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
        m_EnvController = GetComponentInParent<SoccerEnvController>();
        agentRb = GetComponent<Rigidbody>();
        agentRb.maxAngularVelocity = 500;

        // Goalie is rewarded for time survived, striker penalized, so scoring fast is favored.
        var maxSteps = m_EnvController != null && m_EnvController.MaxEnvironmentSteps > 0
            ? m_EnvController.MaxEnvironmentSteps
            : 1;
        m_ExistentialReward = 1f / maxSteps;
    }

    public override void OnEpisodeBegin()
    {
        agentRb.linearVelocity = Vector3.zero;
        agentRb.angularVelocity = Vector3.zero;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(agentRb.linearVelocity);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        AddReward(position == Position.Goalie ? m_ExistentialReward : -m_ExistentialReward);

        // Goalie shaping: hold the goal line. Penalize straying in depth (x) only, so it stays
        // free to slide laterally (z) to block. Scale tied to the existential reward so it can't
        // drown out the +/-1 goal signal: at home the goalie nets +existential, far from home goes negative.
        if (position == Position.Goalie)
        {
            var strayed = Mathf.Abs(transform.position.x - initialPos.x);
            AddReward(-m_ExistentialReward * strayed);
        }

        MoveAgent(actions.DiscreteActions);
    }

    public void MoveAgent(ActionSegment<int> act)
    {
        var dirToGo = Vector3.zero;
        var rotateDir = Vector3.zero;
        m_KickPower = 0f;

        switch (act[0]) // 0 stop, 1 forward, 2 back
        {
            case 1: dirToGo = transform.forward * m_ForwardSpeed; m_KickPower = 1f; break;
            case 2: dirToGo = transform.forward * -m_ForwardSpeed; break;
        }

        switch (act[1]) // 0 stop, 1 strafe right, 2 strafe left
        {
            case 1: dirToGo = transform.right * m_LateralSpeed; break;
            case 2: dirToGo = transform.right * -m_LateralSpeed; break;
        }

        switch (act[2]) // 0 stop, 1 rotate right, 2 rotate left
        {
            case 1: rotateDir = transform.up * 1f; break;
            case 2: rotateDir = transform.up * -1f; break;
        }

        transform.Rotate(rotateDir, Time.deltaTime * 100f);
        agentRb.AddForce(dirToGo * m_SoccerSettings.agentRunSpeed, ForceMode.VelocityChange);
    }
    
    // Keyboard control used when no trained model/trainer is attached (Behavior Type = Default/Heuristic).
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var kb = Keyboard.current;
        var d = actionsOut.DiscreteActions;
        d[0] = kb == null ? 0 : kb.wKey.isPressed ? 1 : kb.sKey.isPressed ? 2 : 0;
        d[1] = kb == null ? 0 : kb.eKey.isPressed ? 1 : kb.qKey.isPressed ? 2 : 0;
        d[2] = kb == null ? 0 : kb.dKey.isPressed ? 1 : kb.aKey.isPressed ? 2 : 0;
    }

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

            if (position == Position.Striker)
            {
                var relativePosition = (transform.position - c.transform.position).normalized;
                AddReward((team == Team.Blue ? -1 : 1) * relativePosition.x * 0.01f);
            }
        }
    }
}
