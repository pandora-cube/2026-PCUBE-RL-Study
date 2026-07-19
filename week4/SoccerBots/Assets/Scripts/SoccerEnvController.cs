using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

// spawn/reset the players and ball, handle a goal (score + group reward + reset)
public class SoccerEnvController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public AgentSoccer Agent;
        [HideInInspector]
        public Vector3 StartingPos;
        [HideInInspector]
        public Quaternion StartingRot;
        [HideInInspector]
        public Rigidbody Rb;
    }

    [Tooltip("Steps before the round auto-resets (0 = never)")]
    public int MaxEnvironmentSteps = 25000;

    public GameObject ball;
    [HideInInspector]
    public Rigidbody ballRb;
    Vector3 m_BallStartingPos;

    // List of players on this field.
    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    public int blueScore;
    public int redScore;

    int m_ResetTimer;
    SimpleMultiAgentGroup m_BlueAgentGroup;
    SimpleMultiAgentGroup m_RedAgentGroup;

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        m_BallStartingPos = ball.transform.position;

        m_BlueAgentGroup = new SimpleMultiAgentGroup();
        m_RedAgentGroup = new SimpleMultiAgentGroup();

        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();

            if (item.Agent.team == Team.Blue)
                m_BlueAgentGroup.RegisterAgent(item.Agent);
            else
                m_RedAgentGroup.RegisterAgent(item.Agent);
        }
        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer += 1;
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            m_BlueAgentGroup.GroupEpisodeInterrupted();
            m_RedAgentGroup.GroupEpisodeInterrupted();
            ResetScene();
        }
    }

    public void ResetBall()
    {
        var randomPosX = Random.Range(-2.5f, 2.5f);
        var randomPosZ = Random.Range(-2.5f, 2.5f);

        ball.transform.position = m_BallStartingPos + new Vector3(randomPosX, 0f, randomPosZ);
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
    }

    public void GoalTouched(Team scoredTeam)
    {
        var scoredGroup = scoredTeam == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
        var concededGroup = scoredTeam == Team.Blue ? m_RedAgentGroup : m_BlueAgentGroup;

        // Faster goals are worth more, mirroring each agent's per-step existential reward.
        scoredGroup.AddGroupReward(1f - (float)m_ResetTimer / MaxEnvironmentSteps);
        concededGroup.AddGroupReward(-1f);

        if (scoredTeam == Team.Blue) blueScore++; else redScore++;
        Debug.Log($"Goal! Blue {blueScore} - {redScore} Red");

        scoredGroup.EndGroupEpisode();
        concededGroup.EndGroupEpisode();
        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;

        //Reset Players
        foreach (var item in AgentsList)
        {
            var randomPosX = Random.Range(-5f, 5f);
            var newStartPos = item.Agent.initialPos + new Vector3(randomPosX, 0f, 0f);
            var rot = item.Agent.rotSign * Random.Range(80.0f, 100.0f);
            var newRot = Quaternion.Euler(0, rot, 0);
            item.Agent.transform.SetPositionAndRotation(newStartPos, newRot);

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        //Reset Ball
        ResetBall();
    }
}
