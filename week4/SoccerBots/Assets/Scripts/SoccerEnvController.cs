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

    [Tooltip("Scatter the ball and non-goalie players across the whole pitch each reset " +
             "(instead of near their kickoff spots) to improve striker generalization. " +
             "Goalies always keep their goal line.")]
    public bool strongRandomization = true;

    [Tooltip("VsGoalie lesson only: per-step penalty scale for a ball that sits still. The " +
             "penalty grows exponentially the longer the ball stays put, pushing strikers to " +
             "go fetch a ball behind them instead of loafing. 0 disables it.")]
    public float ballStallPenaltyScale = 0.00005f;

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

    // VsGoalie lesson (2 attacking strikers vs the other team's lone goalie): the attacker/defender
    // roles are fixed for the round, so we can shape defense specifically. Set in ResetScene.
    bool m_VsGoalieLesson;
    Team m_DefenderTeam;      // the team whose goalie is defending
    bool m_BallOnDefenderHalf; // hysteresis flag for the "cleared across midfield" reward
    int m_BallStillSteps;      // consecutive FixedUpdate steps the ball has been ~motionless
    float m_StallPenaltyPaid;  // total stall penalty charged this round (kept bounded, see below)

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
        }
        // Group membership is (re)built per lesson in ResetScene, so the field can shrink/grow.
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

        if (m_VsGoalieLesson)
        {
            RewardBallClearance();
            PenalizeBallStalling();
        }
    }

    // VsGoalie lesson only: if the ball sits still too long the attackers are loafing (typically
    // the ball is behind them and they never turn to fetch it). Penalize the attacking group by an
    // amount that grows exponentially with how long the ball has stayed put, so dawdling snowballs.
    void PenalizeBallStalling()
    {
        const float stillSpeed = 0.15f; // below this the ball counts as "not moving"
        const int graceSteps = 100;     // ~2s @ 50fps of stillness tolerated before it bites
        const float tau = 100f;         // steps for the exponent to grow by 1

        if (ballRb.linearVelocity.sqrMagnitude < stillSpeed * stillSpeed)
            m_BallStillSteps++;
        else
            m_BallStillSteps = 0;

        const float episodeBudget = 1f; // total stall penalty per round can't exceed a goal's worth

        var over = m_BallStillSteps - graceSteps;
        if (over <= 0 || ballStallPenaltyScale <= 0f || m_StallPenaltyPaid >= episodeBudget)
            return;

        // exp curve, tiny at first then steep. Per-step exponent capped (e^6); ALSO cap the running
        // total per round, otherwise a ball stuck for hundreds of steps accumulates -10..-20 group
        // reward and swamps the +/-1 goal signal (which is what tanked Striker Group Cumulative Reward).
        var attackerGroup = m_DefenderTeam == Team.Blue ? m_RedAgentGroup : m_BlueAgentGroup;
        var penalty = ballStallPenaltyScale * (Mathf.Exp(Mathf.Min(over / tau, 6f)) - 1f);
        penalty = Mathf.Min(penalty, episodeBudget - m_StallPenaltyPaid);
        m_StallPenaltyPaid += penalty;
        attackerGroup.AddGroupReward(-penalty);
    }

    // VsGoalie lesson only: reward the defending goalie when the ball is pushed across midfield,
    // from the defender's half into the attacker's half (a successful clearance). The hysteresis
    // band means the ball must go clearly onto the defender's half and then clearly out, so it
    // can't be farmed by jitter around the halfway line.
    void RewardBallClearance()
    {
        // Blue defends the -x goal, Red the +x goal. "clear" > 0 means the ball is on the
        // attacker's half (away from the defended goal).
        var defGoalSign = m_DefenderTeam == Team.Blue ? -1f : 1f;
        var clear = -(ball.transform.position.x - transform.position.x) * defGoalSign;
        const float band = 2f;
        if (m_BallOnDefenderHalf && clear > band)
        {
            var defenderGroup = m_DefenderTeam == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
            defenderGroup.AddGroupReward(0.5f);
            m_BallOnDefenderHalf = false;
        }
        else if (clear < -band)
        {
            m_BallOnDefenderHalf = true;
        }
    }

    public void ResetBall()
    {
        // Strong mode spreads the ball across most of the pitch (kept off the ±20 goal lines);
        // otherwise a small jitter around the center spot.
        var range = strongRandomization ? new Vector2(14f, 7f) : new Vector2(2.5f, 2.5f);
        var randomPosX = Random.Range(-range.x, range.x);
        var randomPosZ = Random.Range(-range.y, range.y);

        ball.transform.position = m_BallStartingPos + new Vector3(randomPosX, 0f, randomPosZ);
        ballRb.linearVelocity = Vector3.zero;
        ballRb.angularVelocity = Vector3.zero;
    }

    public void GoalTouched(Team scoredTeam)
    {
        var scoredGroup = scoredTeam == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
        var concededGroup = scoredTeam == Team.Blue ? m_RedAgentGroup : m_BlueAgentGroup;

        if (m_VsGoalieLesson && scoredTeam == m_DefenderTeam)
        {
            // The ball went into the ATTACKER's own (undefended) goal, which registers as the
            // goalie's team "scoring". Don't credit the goalie for it — instead punish the
            // attacking strikers hard for the own goal.
            concededGroup.AddGroupReward(-2f);
        }
        else
        {
            // Flat +1 for a goal. Time pressure already comes from each striker's per-step
            // existential penalty (-1/N per step); ALSO discounting the goal by time double-counts
            // it, so a scored episode netted only 1 - 2t/N. That kept the Striker's mean cumulative
            // reward below the curriculum threshold and blocked lesson progression.
            scoredGroup.AddGroupReward(1f);
            concededGroup.AddGroupReward(-1f);
        }

        // Mirror the goal onto each active striker's INDIVIDUAL reward.
        foreach (var item in AgentsList)
        {
            var a = item.Agent;
            if (!a.gameObject.activeSelf || a.position != AgentSoccer.Position.Striker)
                continue;
            a.AddReward(a.team == scoredTeam ? 1f : -1f);
        }

        if (scoredTeam == Team.Blue) blueScore++; else redScore++;
        Debug.Log($"Goal! Blue {blueScore} - {redScore} Red");

        scoredGroup.EndGroupEpisode();
        concededGroup.EndGroupEpisode();
        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;
        m_BallStillSteps = 0; // ball is reset to rest below; don't carry stillness across rounds
        m_StallPenaltyPaid = 0f;

        // Curriculum lesson (from Python environment_parameters): 0 = 2 strikers vs empty goal,
        // 1 = 2 strikers vs a lone goalie, 2 = full 3v3 self-play. Default 2 when run in-editor.
        var lesson = Academy.Instance.EnvironmentParameters.GetWithDefault("lesson", 2f);

        Team randTeam = Random.value < 0.5f ? Team.Blue : Team.Red;
        // randTeam's strikers attack; the other team's lone goalie defends (see ActiveInLesson).
        m_VsGoalieLesson = lesson >= 1f && lesson < 2f;
        m_DefenderTeam = randTeam == Team.Blue ? Team.Red : Team.Blue;
        foreach (var item in AgentsList)
        {
            var agent = item.Agent;
            var active = ActiveInLesson(agent, lesson, randTeam);
            agent.gameObject.SetActive(active); // benched agents leave the field: no body, no decisions.

            var group = agent.team == Team.Blue ? m_BlueAgentGroup : m_RedAgentGroup;
            if (!active)
            {
                group.UnregisterAgent(agent);
                continue;
            }
            group.RegisterAgent(agent);

            // Field-local pitch half-extents that keep spawns off the walls/goals. initialPos is
            // world-space and there are many field copies, so we work relative to the field root.
            const float pitchHalfX = 19f; // goal line begins ~20.2
            const float pitchHalfZ = 8f;  // side walls at ~9.8

            Vector3 newStartPos;
            Quaternion newRot;
            if (strongRandomization && agent.position != AgentSoccer.Position.Goalie)
            {
                // Scatter non-goalie players anywhere on the pitch, facing a random direction, so the
                // striker generalizes past the near-home kickoff layout. Goalies keep their line.
                var lx = Random.Range(-pitchHalfX, pitchHalfX);
                var lz = Random.Range(-pitchHalfZ, pitchHalfZ);
                newStartPos = new Vector3(transform.position.x + lx, agent.initialPos.y, transform.position.z + lz);
                newRot = Quaternion.Euler(0, Random.Range(0f, 360f), 0);
            }
            else
            {
                // Default: small depth (x) jitter around home, clamped to the pitch, authored facing.
                var newX = agent.initialPos.x + Random.Range(-5f, 5f);
                var localX = Mathf.Clamp(newX - transform.position.x, -pitchHalfX, pitchHalfX);
                newStartPos = new Vector3(transform.position.x + localX, agent.initialPos.y, agent.initialPos.z);
                newRot = Quaternion.Euler(0, agent.rotSign * Random.Range(80f, 100f), 0);
            }
            agent.transform.SetPositionAndRotation(newStartPos, newRot);

            item.Rb.linearVelocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        //Reset Ball
        ResetBall();

        // Arm the clearance reward: only count a clearance once the ball has been clearly on the
        // defender's half, so a center kickoff drifting upfield doesn't hand out a free reward.
        var defGoalSign = m_DefenderTeam == Team.Blue ? -1f : 1f;
        m_BallOnDefenderHalf = -(ball.transform.position.x - transform.position.x) * defGoalSign < -2f;
    }

    // Who is on the field for a given curriculum lesson. Blue always attacks the Red goal
    // (see SoccerBallController), so Blue strikers are the ones being trained up.
    static bool ActiveInLesson(AgentSoccer agent, float lesson, Team team = Team.Blue)
    {
        
        var striker = agent.team == team && agent.position == AgentSoccer.Position.Striker;
        if (lesson < 1f)   // Lesson 0: only the attacking strikers, empty goal.
            return striker;
        if (lesson < 2f)   // Lesson 1: strikers vs one defending goalie.
            return striker || (agent.team != team && agent.position == AgentSoccer.Position.Goalie);
        return true;       // Lesson 2: full 3v3.
    }
}
