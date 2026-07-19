using System.Collections.Generic;
using UnityEngine;

// 구현: soccer field's game logic
// spawn/reset the players and ball
// handle a goal (score + reset). 
// 구현할 것: 보상
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

    private int m_ResetTimer;

    void Start()
    {
        ballRb = ball.GetComponent<Rigidbody>();
        m_BallStartingPos = ball.transform.position;
        foreach (var item in AgentsList)
        {
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();
        }
        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer += 1;
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
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
        if (scoredTeam == Team.Blue)
        {
            blueScore++;
        }
        else
        {
            redScore++;
        }
        Debug.Log($"Goal! Blue {blueScore} - {redScore} Red");
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
