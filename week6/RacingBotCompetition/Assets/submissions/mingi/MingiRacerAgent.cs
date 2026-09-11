using System;
using System.Collections.Generic;
using RacingBotCup.Agent;
using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

// Runs after TrainingArena (-100): sample every physics tick, independently of DecisionPeriod.
[DefaultExecutionOrder(100)]
public class MingiRacerAgent : RacerAgent
{
    [Header("Observation shape (restart training after changing)")]
    [SerializeField, Range(1, 20)] int m_Waypoints = 5;
    [SerializeField, Min(0.1f)] float m_WaypointSpacing = 10f;
    [SerializeField, Range(1, 10)] int m_CurvatureWindows = 3;
    [SerializeField, Min(0.1f)] float m_CurvatureWindowLength = 20f;

    [Header("Curriculum (applied at episode boundaries)")]
    [SerializeField] bool m_AutomaticPromotion = true;
    [Tooltip("Manual stage when automatic is off; starting stage when automatic is on. Also set this when resuming Python training.")]
    [SerializeField, Range(1, 3)] int m_ManualStage = 1;
    [SerializeField, Min(1)] int m_EvaluationWindow = 100;
    [SerializeField, Min(1)] int m_MinEvaluationEpisodes = 50;
    [SerializeField, Range(0f, 1f)] float m_RequiredFinishRate = 0.8f;
    [Tooltip("Successful laps within their target time / ALL episodes. Failures never qualify.")]
    [SerializeField, Range(0f, 1f)] float m_RequiredTargetTimeRate = 0.6f;
    [SerializeField] bool m_LogStatistics = true;

    [Header("Lap target (frozen at the start of each episode)")]
    [Tooltip("Enable only when TrainingArena Randomize Each Episode is off. Its private setting is not modified or inferred.")]
    [SerializeField] bool m_UseFixedTargetLapTime;
    [SerializeField, Min(1f)] float m_FixedTargetLapTime = 60f;
    [SerializeField, Min(0.1f)] float m_TargetAverageSpeed = 12f;
    [SerializeField, Min(0f)] float m_StandingStartAllowance = 3f;

    [Header("Reward coefficients (metres / simulation seconds, never decisions)")]
    [SerializeField, Min(0f)] float m_ProgressReward = 0.02f;
    [SerializeField, Min(0f)] float m_TimePenaltyPerSecond = 0.02f;
    [SerializeField, Min(0f)] float m_OffTrackPenaltyPerSecond = 0.5f;
    [Tooltip("Stage 1 only, per NEW on-road metre, multiplied by centre score.")]
    [SerializeField, Min(0f)] float m_CenterRewardPerMetre = 0.01f;
    [Tooltip("Per NEW on-road metre, multiplied by forward speed / reference speed (clamped).")]
    [SerializeField, Min(0f)] float m_SpeedRewardPerMetre = 0.01f;
    [SerializeField, Min(0.1f)] float m_SpeedReference = 25f;
    [SerializeField, Min(0f)] float m_MinForwardSpeed = 0.5f;
    [Tooltip("Speed floor observed by the policy AND priced by the slow-speed penalty, in km/h.")]
    [SerializeField, Min(0f)] float m_Stage1MinSpeedKph = 18f;
    [SerializeField, Min(0f)] float m_Stage2MinSpeedKph = 60f;
    [SerializeField, Min(0f)] float m_Stage3MinSpeedKph = 80f;
    [Tooltip("Scaled by the shortfall: full rate when stopped, nothing at the stage floor.")]
    [SerializeField, Min(0f)] float m_SlowSpeedPenaltyPerSecond = 0.2f;
    [Tooltip("Seconds of the episode start that are exempt, so the standing start is not punished.")]
    [SerializeField, Min(0f)] float m_SlowSpeedGraceSeconds = 3f;
    [Tooltip("Surcharge on top of the slow-speed penalty while the car is travelling backwards.")]
    [SerializeField, Min(0f)] float m_ReversePenaltyPerSecond = 0.6f;
    [SerializeField, Min(0f)] float m_LapBonus = 20f;
    [SerializeField, Min(0f)] float m_TimeBonusWeight = 20f;
    [SerializeField, Min(0f)] float m_FailurePenalty = 10f;
    [Tooltip("Seconds without a new best progress before the run is abandoned. 0 disables.")]
    [SerializeField, Min(0f)] float m_StallTimeoutSeconds = 8f;
    [SerializeField, Min(0f)] float m_CollisionPenalty = 2f;

    [Header("Stage 1: learn to start and accelerate")]
    [Tooltip("Per NEW on-road metre in stage 1. Later stages use Progress Reward above.")]
    [SerializeField, Min(0f)] float m_Stage1ProgressReward = 0.05f;
    [Tooltip("Minimum forward speed in m/s for positive shaping in stage 1.")]
    [SerializeField, Min(0f)] float m_Stage1MinForwardSpeed = 0.05f;
    [Tooltip("Total launch reward available per episode, paid only for new best forward speeds.")]
    [SerializeField, Min(0f)] float m_LaunchBonus = 2f;
    [SerializeField, Min(0.1f)] float m_LaunchTargetSpeedKph = 15f;
    [SerializeField, Min(0f)] float m_LaunchWindowSeconds = 10f;

    [Header("OffTrack ground boundary sensor")]
    [SerializeField, Min(1f)] float m_BoundaryRange = 15f;
    [SerializeField, Range(0.1f, 2f)] float m_GroundSampleSpacing = 0.5f;
    [SerializeField, Range(1, 10)] int m_BoundaryRefinementSteps = 5;
    [SerializeField, Min(0.1f)] float m_GroundRayHeight = 3f;
    [SerializeField, Min(0.1f)] float m_GroundRayDepth = 8f;

    [Header("Obstacle / Ramp sensor (always present, all stages)")]
    [SerializeField, Min(1f)] float m_ObstacleRange = 50f;
    [SerializeField, Min(0f)] float m_ObstacleRayHeight = 0.6f;
    [SerializeField, Range(0f, 1f)] float m_ObstacleCastRadius = 0.35f;

    // Angles are fixed slots: left -> forward -> right. Each slot is distance, valid, obstacle, ramp.
    static readonly float[] s_ObstacleAngles = { -60f, -40f, -20f, 0f, 20f, 40f, 60f };
    static readonly Dictionary<string, CurriculumHistory> s_Histories = new Dictionary<string, CurriculumHistory>();

    sealed class CurriculumHistory
    {
        public int Stage;
        public readonly Queue<EpisodeResult> Results = new Queue<EpisodeResult>();
    }

    struct EpisodeResult
    {
        public bool Finished;
        public bool MetTarget;
    }

    enum RewardPart { Progress, Center, Speed, Time, OffTrack, Finish, Failure, Collision, SlowSpeed, Launch, Count }
    enum GroundKind { Missing, Road, OffTrack }

    readonly float[] m_RewardTotals = new float[(int)RewardPart.Count];
    readonly HashSet<int> m_HitObstacles = new HashSet<int>();
    readonly Dictionary<GameObject, bool> m_ObstacleOriginalStates = new Dictionary<GameObject, bool>();
    readonly List<Collider> m_GroundColliders = new List<Collider>();
    readonly float[] m_ObstacleObservations = new float[28];
    readonly RaycastHit[] m_ObstacleHits = new RaycastHit[64];

    BehaviorParameters m_BehaviorParams_;
    TrainingArena m_TrainingArena;
    TrackInstance m_TrackInstance;
    RaceClock m_Clock;
    CheckpointRing m_EpisodeCheckpoints;
    MingiRacerCollisionSensor m_CollisionSensor;
    CurriculumHistory m_History;
    bool m_SharedHistory;
    bool m_EpisodeActive;
    bool m_TerminalRecorded;
    bool m_TrainingEpisode;
    bool m_PreviousOnRoad;
    int m_CurrentStage = 1;
    int m_WaypointCount;
    int m_CurvatureCount;
    float m_TargetLapTime;
    float m_EpisodeLength;
    float m_LastElapsed;
    float m_LastProgress;
    float m_HighestProgress;
    float m_HighestLaunchSpeedFraction;
    float m_OffTrackSeconds;
    float m_StallSeconds;
    float m_CenterErrorSeconds;
    float m_ValidBoundarySeconds;
    float m_LeftDistance;
    float m_RightDistance;
    bool m_LeftValid;
    bool m_RightValid;
    float m_LastSensorTime = float.NegativeInfinity;

    public int CurrentStage => m_CurrentStage;
    public float TargetLapTime => m_TargetLapTime;
    public int ObservationSize => 10 + (m_WaypointCount > 0 ? m_WaypointCount : m_Waypoints) * 2
        + (m_CurvatureCount > 0 ? m_CurvatureCount : m_CurvatureWindows) + 4 + 28;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ClearCurriculumHistories() => s_Histories.Clear();

    public override void Initialize()
    {
        base.Initialize();
        m_WaypointCount = Mathf.Clamp(m_Waypoints, 1, 20);
        m_CurvatureCount = Mathf.Clamp(m_CurvatureWindows, 1, 10);
        m_CurrentStage = Mathf.Clamp(m_ManualStage, 1, 3);
        m_BehaviorParams_ = GetComponent<BehaviorParameters>();
        // ML-Agents 4.1 Agent.LazyInitialize calls Initialize BEFORE InitializeSensors.
        // Only the vector sensor size changes; action mapping, model and stack count are untouched.
        if (m_BehaviorParams_ != null)
            m_BehaviorParams_.BrainParameters.VectorObservationSize = ObservationSize;
        ResetSensorValues();
        // Car / Track are deliberately not accessed: RacerRig.Bind can happen later.
    }

    public override void OnEpisodeBegin()
    {
        // Explicit terminal callbacks supply results before EndEpisode. Never count an
        // Academy reset or an unreported MaxStep truncation as a successful episode.
        RestoreTrainingObstacles();
        m_EpisodeActive = false;
        m_EpisodeCheckpoints = null;
        base.OnEpisodeBegin(); // TrainingArena may rebuild the circuit AND replace Checkpoints.
        EnsureEpisode();       // All progress, clock, sensor and collision baselines follow reset.
    }

    bool EnsureEpisode()
    {
        if (!IsBound || Car == null || Car.Body == null || Track == null || Checkpoints == null)
            return false;
        if (m_EpisodeActive && ReferenceEquals(m_EpisodeCheckpoints, Checkpoints))
            return true;

        // Also handles the first Bind in TrainingArena.Start, after Agent.Initialize.
        m_EpisodeCheckpoints = Checkpoints;
        m_Clock = Car.GetComponent<RaceClock>();
        m_TrackInstance = FindBoundTrack();
        m_TrainingArena = FindOwningTrainingArena();
        m_TrainingEpisode = m_TrainingArena != null && m_BehaviorParams_ != null
            && m_BehaviorParams_.BehaviorType == BehaviorType.Default;
        SelectStageAtBoundary();
        m_EpisodeLength = Mathf.Max(1f, Track.TotalLength);
        m_TargetLapTime = m_UseFixedTargetLapTime
            ? Positive(m_FixedTargetLapTime, 60f)
            : m_EpisodeLength / Positive(m_TargetAverageSpeed, 12f) + NonNegative(m_StandingStartAllowance);
        m_LastElapsed = m_Clock != null ? m_Clock.Elapsed : 0f;
        m_LastProgress = Checkpoints.TraveledDistance;
        m_HighestProgress = Mathf.Clamp(m_LastProgress, 0f, m_EpisodeLength);
        m_HighestLaunchSpeedFraction = 0f;
        m_OffTrackSeconds = m_CenterErrorSeconds = m_ValidBoundarySeconds = m_StallSeconds = 0f;
        m_TerminalRecorded = false;
        m_HitObstacles.Clear();
        Array.Clear(m_RewardTotals, 0, m_RewardTotals.Length);
        ResetSensorValues();
        CacheGroundColliders();
        ApplyTrainingObstacles();
        m_PreviousOnRoad = IsFullyOnRoad();
        if (m_CollisionSensor != null) m_CollisionSensor.Owner = null;
        if (m_TrainingEpisode)
        {
            // The rigidbody is on the car ROOT, not on the agent child. This component
            // only forwards real contacts; it adds no collider/rigidbody or physics changes.
            m_CollisionSensor = Car.Body.GetComponent<MingiRacerCollisionSensor>();
            if (m_CollisionSensor == null)
                m_CollisionSensor = Car.Body.gameObject.AddComponent<MingiRacerCollisionSensor>();
            m_CollisionSensor.Owner = this;
        }
        m_EpisodeActive = true;
        return true;
    }

    TrackInstance FindBoundTrack()
    {
        foreach (var instance in FindObjectsByType<TrackInstance>(FindObjectsSortMode.None))
            if (instance.gameObject.scene == gameObject.scene && ReferenceEquals(instance.Model, Track))
                return instance;
        return null;
    }

    TrainingArena FindOwningTrainingArena()
    {
        if (m_TrackInstance == null) return null;
        // Existing Training scene groups one Arena, one Track and one Racer under each
        // Environment_N. EvaluationRunner builds rigs without TrainingArena.
        for (var scope = Car.transform.parent; scope != null; scope = scope.parent)
        {
            if (!m_TrackInstance.transform.IsChildOf(scope)) continue;
            var arenas = scope.GetComponentsInChildren<TrainingArena>();
            var racers = scope.GetComponentsInChildren<RacerAgent>();
            if (arenas.Length == 1 && arenas[0].isActiveAndEnabled
                && racers.Length == 1 && racers[0] == this)
                return arenas[0];
        }

        // The original single-arena SceneBootstrap uses separate scene roots.
        TrainingArena single = null;
        foreach (var arena in FindObjectsByType<TrainingArena>(FindObjectsSortMode.None))
        {
            if (arena.gameObject.scene != gameObject.scene || !arena.isActiveAndEnabled) continue;
            if (single != null) return null;
            single = arena;
        }
        if (single == null) return null;
        foreach (var racer in FindObjectsByType<RacerAgent>(FindObjectsSortMode.None))
            if (racer.gameObject.scene == gameObject.scene && racer != this) return null;
        return single;
    }

    void SelectStageAtBoundary()
    {
        if (!m_TrainingEpisode)
        {
            m_CurrentStage = 3;
            m_History = null;
            m_SharedHistory = false;
            return;
        }
        if (!m_AutomaticPromotion)
        {
            m_CurrentStage = Mathf.Clamp(m_ManualStage, 1, 3);
            if (m_History == null || m_SharedHistory || m_History.Stage != m_CurrentStage)
                m_History = new CurriculumHistory { Stage = m_CurrentStage };
            m_SharedHistory = false;
            TrimHistory();
            return;
        }

        // Multiple arenas sharing a behavior train ONE policy, so share its recent results.
        // A slower arena changes stage at its own NEXT episode; stale-stage results are ignored.
        m_SharedHistory = true;
        var key = gameObject.scene.handle + "/" + m_BehaviorParams_.BehaviorName;
        if (!s_Histories.TryGetValue(key, out m_History))
        {
            m_History = new CurriculumHistory { Stage = Mathf.Clamp(m_ManualStage, 1, 3) };
            s_Histories.Add(key, m_History);
        }
        TrimHistory();
        GetRates(out var finishRate, out var targetRate);
        var minimum = Mathf.Clamp(m_MinEvaluationEpisodes, 1, Mathf.Max(1, m_EvaluationWindow));
        if (m_History.Stage < 3 && m_History.Results.Count >= minimum
            && finishRate >= Mathf.Clamp01(m_RequiredFinishRate)
            && (m_History.Stage == 1 || targetRate >= Mathf.Clamp01(m_RequiredTargetTimeRate)))
        {
            m_History.Stage++;
            m_History.Results.Clear();
            Debug.Log($"[MingiRacerAgent] {m_BehaviorParams_.BehaviorName}: stage {m_History.Stage}; "
                + $"finish={finishRate:P0}, target={targetRate:P0}. Policy weights retained.", this);
        }
        m_CurrentStage = m_History.Stage;
    }

    void TrimHistory()
    {
        if (m_History == null) return;
        while (m_History.Results.Count > Mathf.Max(1, m_EvaluationWindow))
            m_History.Results.Dequeue();
    }

    void GetRates(out float finishRate, out float targetRate)
    {
        finishRate = targetRate = 0f;
        if (m_History == null || m_History.Results.Count == 0) return;
        foreach (var result in m_History.Results)
        {
            if (result.Finished) finishRate++;
            if (result.Finished && result.MetTarget) targetRate++;
        }
        finishRate /= m_History.Results.Count;
        targetRate /= m_History.Results.Count;
    }

    void FixedUpdate()
    {
        if (!EnsureEpisode()) return;
        AccumulateRewards();
        CheckStall();
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        EnsureEpisode();
        base.OnActionReceived(actions); // Preserve the exact continuous/discrete mapping.
    }

    public override void Heuristic(in ActionBuffers actionsOut) => base.Heuristic(in actionsOut);

    void AccumulateRewards()
    {
        if (!m_TrainingEpisode || m_TerminalRecorded || m_Clock == null) return;
        var elapsed = m_Clock.Elapsed;
        var dt = elapsed - m_LastElapsed;
        if (!IsFinite(dt) || dt <= 0f) return; // Includes repeated actions / terminal flush in one tick.
        m_LastElapsed = elapsed;
        RefreshBoundarySensor();

        var progress = Checkpoints.TraveledDistance;
        var delta = progress - m_LastProgress;
        m_LastProgress = progress;
        var boundedProgress = Mathf.Clamp(progress, 0f, m_EpisodeLength);
        var newMetres = Mathf.Max(0f, boundedProgress - m_HighestProgress);
        // Advance even OFF road. Re-entering cannot cash in distance travelled off road.
        m_HighestProgress = Mathf.Max(m_HighestProgress, boundedProgress);
        m_StallSeconds = newMetres > 0f ? 0f : m_StallSeconds + dt;
        var onRoad = IsFullyOnRoad();
        Pay(RewardPart.Time, -NonNegative(m_TimePenaltyPerSecond) * dt);
        if (!onRoad)
        {
            m_OffTrackSeconds += dt;
            Pay(RewardPart.OffTrack, -NonNegative(m_OffTrackPenaltyPerSecond) * dt);
        }
        if (m_LeftValid && m_RightValid)
        {
            m_CenterErrorSeconds += CenterError(m_LeftDistance, m_RightDistance) * dt;
            m_ValidBoundarySeconds += dt;
        }

        // Require road at BOTH endpoints, forward body velocity and track-direction velocity.
        // New-distance gating applies to ALL positive shaping, including the launch bonus.
        var alongSpeed = Vector3.Dot(Car.Body.linearVelocity, Projection.Forward);
        var forwardSpeed = Vector3.Dot(Car.Body.linearVelocity, Car.transform.forward);
        var minForwardSpeed = NonNegative(m_CurrentStage == 1 ? m_Stage1MinForwardSpeed : m_MinForwardSpeed);
        var canRewardProgress = onRoad && m_PreviousOnRoad && delta > 0f && newMetres > 0f
            && forwardSpeed > minForwardSpeed && alongSpeed > minForwardSpeed;
        PaySlowSpeedPenalty(forwardSpeed, elapsed, dt);
        PayLaunchBonus(forwardSpeed, alongSpeed, elapsed, canRewardProgress);
        if (canRewardProgress)
        {
            newMetres = Mathf.Min(newMetres, delta);
            var progressReward = m_CurrentStage == 1 ? m_Stage1ProgressReward : m_ProgressReward;
            Pay(RewardPart.Progress, NonNegative(progressReward) * newMetres);
            Pay(RewardPart.Speed, NonNegative(m_SpeedRewardPerMetre) * newMetres
                * Mathf.Clamp01(forwardSpeed / Positive(m_SpeedReference, 25f)));
            if (m_CurrentStage == 1 && m_LeftValid && m_RightValid)
                Pay(RewardPart.Center, NonNegative(m_CenterRewardPerMetre) * newMetres
                    * (1f - CenterError(m_LeftDistance, m_RightDistance)));
        }
        m_PreviousOnRoad = onRoad;
    }

    // Without this a car that cannot take a corner is free to shuffle for the arena's full 200 s:
    // the terminal penalty is discounted to nothing that far out, so the shuffle is a local
    // optimum and burns an episode's worth of samples going nowhere. Abandon the run instead.
    void CheckStall()
    {
        if (!m_TrainingEpisode || m_TerminalRecorded) return;
        var timeout = NonNegative(m_StallTimeoutSeconds);
        if (timeout <= 0f) return;
        if (m_LastElapsed < NonNegative(m_SlowSpeedGraceSeconds)) return; // Standing start is exempt.
        if (m_StallSeconds < timeout) return;

        OnRunFailed();
        EndEpisode();
    }

    bool IsFullyOnRoad()
    {
        if (IsOffTrack || Car.WheelsOffTrack > 0) return false;
        // Ground is decided using only actual Track/OffTrack colliders, not props or the car.
        return SampleGround(Car.transform.position) == GroundKind.Road;
    }

    // Every stage now has a priced floor (18 / 60 / 80 km/h). Stage 1 used to observe its floor
    // without ever being charged for missing it, which left crawling almost free.
    // The cost scales with the shortfall: a stopped car pays the full rate, a car at the floor
    // pays nothing. clamp01 prices reverse exactly like a stopped car on its own, which is what
    // made the brake/reverse shuffle at an untakeable corner cost nothing extra — hence the
    // separate surcharge below, which is what actually breaks that tie.
    void PaySlowSpeedPenalty(float forwardSpeed, float elapsed, float dt)
    {
        if (elapsed < NonNegative(m_SlowSpeedGraceSeconds)) return;
        var floorKph = StageMinSpeedKph(m_CurrentStage);
        if (floorKph <= 0f) return;
        var floor = floorKph / 3.6f; // km/h -> m/s, the same conversion the HUD uses.

        var shortfall = Mathf.Clamp01((floor - forwardSpeed) / floor);
        if (shortfall > 0f)
            Pay(RewardPart.SlowSpeed, -NonNegative(m_SlowSpeedPenaltyPerSecond) * shortfall * dt);

        if (forwardSpeed < 0f)
            Pay(RewardPart.SlowSpeed,
                -NonNegative(m_ReversePenaltyPerSecond) * Mathf.Clamp01(-forwardSpeed / floor) * dt);
    }

    void PayLaunchBonus(float forwardSpeed, float alongSpeed, float elapsed, bool canRewardProgress)
    {
        if (m_CurrentStage != 1 || elapsed > NonNegative(m_LaunchWindowSeconds)
            || !IsFinite(forwardSpeed) || !IsFinite(alongSpeed)) return;

        var targetSpeed = Positive(m_LaunchTargetSpeedKph, 15f) / 3.6f;
        // Use the lower forward speed so motion across the track cannot inflate the bonus.
        var fraction = Mathf.Clamp01(Mathf.Min(forwardSpeed, alongSpeed) / targetSpeed);
        var increase = Mathf.Max(0f, fraction - m_HighestLaunchSpeedFraction);
        // Track the maximum even when ineligible. Re-entering the road or retracing old ground
        // cannot cash in speed gained there, and braking then accelerating never pays twice.
        m_HighestLaunchSpeedFraction = Mathf.Max(m_HighestLaunchSpeedFraction, fraction);
        if (canRewardProgress)
            Pay(RewardPart.Launch, NonNegative(m_LaunchBonus) * increase);
    }

    float StageMinSpeedKph(int stage)
    {
        switch (stage)
        {
            case 1: return NonNegative(m_Stage1MinSpeedKph);
            case 2: return NonNegative(m_Stage2MinSpeedKph);
            default: return NonNegative(m_Stage3MinSpeedKph);
        }
    }

    static float CenterError(float left, float right) =>
        Mathf.Clamp01(Mathf.Abs(left - right) / Mathf.Max(left + right, 1e-5f));

    public override void OnLapCompleted(float elapsedSeconds)
    {
        if (!EnsureEpisode() || !m_TrainingEpisode || m_TerminalRecorded) return;
        if (!Checkpoints.LapComplete || !IsFinite(elapsedSeconds) || elapsedSeconds <= 0f)
        {
            OnRunFailed();
            return;
        }
        AccumulateRewards(); // Arena terminal callback precedes this agent's FixedUpdate.
        var bonus = NonNegative(m_LapBonus);
        if (m_CurrentStage >= 2)
            bonus += NonNegative(m_TimeBonusWeight) * (m_TargetLapTime / (m_TargetLapTime + elapsedSeconds));
        Pay(RewardPart.Finish, bonus);
        RecordTerminal(true, elapsedSeconds);
    }

    public override void OnRunFailed()
    {
        if (!EnsureEpisode() || !m_TrainingEpisode || m_TerminalRecorded) return;
        AccumulateRewards();
        Pay(RewardPart.Failure, -NonNegative(m_FailurePenalty));
        RecordTerminal(false, 0f);
    }

    void RecordTerminal(bool finished, float elapsed)
    {
        m_TerminalRecorded = true;
        if (m_History != null && m_History.Stage == m_CurrentStage)
        {
            m_History.Results.Enqueue(new EpisodeResult
                { Finished = finished, MetTarget = finished && elapsed <= m_TargetLapTime });
            TrimHistory();
        }
        if (!m_LogStatistics) return;
        var stats = Academy.Instance.StatsRecorder;
        GetRates(out var finishRate, out var targetRate);
        stats.Add("Curriculum/Stage", m_CurrentStage);
        stats.Add("Curriculum/WindowEpisodes", m_History != null ? m_History.Results.Count : 0);
        stats.Add("Curriculum/FinishRate", finishRate);
        stats.Add("Curriculum/TargetTimeRate", targetRate);
        stats.Add("Curriculum/TargetSeconds", m_TargetLapTime);
        stats.Add("Curriculum/Finished", finished ? 1f : 0f);
        if (finished)
        {
            stats.Add("Curriculum/LapSeconds", elapsed);
            stats.Add("Curriculum/LapToTargetRatio", elapsed / m_TargetLapTime);
        }
        stats.Add("Driving/BoundaryValidFraction", m_ValidBoundarySeconds / Mathf.Max(m_LastElapsed, 1e-5f));
        if (m_ValidBoundarySeconds > 0f)
            stats.Add("Driving/CenterError", m_CenterErrorSeconds / m_ValidBoundarySeconds);
        stats.Add("Driving/OffTrackFraction", m_OffTrackSeconds / Mathf.Max(m_LastElapsed, 1e-5f));
        stats.Add("Driving/UniqueObstacleCollisions", m_HitObstacles.Count);
        for (var i = 0; i < m_RewardTotals.Length; i++)
            stats.Add("Reward/" + (RewardPart)i, m_RewardTotals[i]);
    }

    void Pay(RewardPart part, float value)
    {
        if (!IsFinite(value) || value == 0f) return;
        m_RewardTotals[(int)part] += value;
        AddReward(value);
    }

    internal void RecordObstacleContact(Collider other)
    {
        if (!m_EpisodeActive || !m_TrainingEpisode || m_TerminalRecorded || m_CurrentStage != 3
            || Car == null || !Car.IsRacing || other == null) return;
        var prop = FindPropRoot(other.transform);
        if (prop == null || !prop.CompareTag(TrackTags.Obstacle)) return;
        // One charge per placed obstacle per episode. Child colliders, stay callbacks
        // and contact jitter cannot multiply the penalty.
        if (m_HitObstacles.Add(prop.GetInstanceID()))
            Pay(RewardPart.Collision, -NonNegative(m_CollisionPenalty));
    }

    void CacheGroundColliders()
    {
        m_GroundColliders.Clear();
        if (m_TrackInstance == null) return;
        // Destroy is deferred during play. Use the NEWEST Geometry child after Rebuild.
        var geometry = NewestChild(m_TrackInstance.transform, "Geometry");
        if (geometry == null) return;
        foreach (var collider in geometry.GetComponentsInChildren<Collider>())
            if (collider.CompareTag(TrackTags.Track) || collider.CompareTag(TrackTags.OffTrack))
                m_GroundColliders.Add(collider);
    }

    static Transform NewestChild(Transform parent, string childName)
    {
        for (var i = parent.childCount - 1; i >= 0; i--)
            if (parent.GetChild(i).name == childName) return parent.GetChild(i);
        return null;
    }

    GroundKind SampleGround(Vector3 position)
    {
        var ray = new Ray(position + Vector3.up * Positive(m_GroundRayHeight, 3f), Vector3.down);
        var length = Positive(m_GroundRayHeight, 3f) + Positive(m_GroundRayDepth, 8f);
        var nearest = float.PositiveInfinity;
        var result = GroundKind.Missing;
        foreach (var collider in m_GroundColliders)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy
                || collider.isTrigger || !collider.Raycast(ray, out var hit, length)) continue;
            // Road (y=0) wins over underlying Ground (-0.03) and Runoff (-0.015).
            // Only the closest valid ground surface counts; a miss is not OffTrack.
            if (hit.distance >= nearest) continue;
            nearest = hit.distance;
            result = collider.CompareTag(TrackTags.Track) ? GroundKind.Road : GroundKind.OffTrack;
        }
        return result;
    }

    void ResetSensorValues()
    {
        m_LeftDistance = m_RightDistance = Positive(m_BoundaryRange, 15f);
        m_LeftValid = m_RightValid = false;
        m_LastSensorTime = float.NegativeInfinity;
        Array.Clear(m_ObstacleObservations, 0, m_ObstacleObservations.Length);
        for (var i = 0; i < s_ObstacleAngles.Length; i++) m_ObstacleObservations[i * 4] = 1f;
    }

    void RefreshBoundarySensor()
    {
        if (m_LastSensorTime == Time.fixedTime) return;
        m_LastSensorTime = Time.fixedTime;
        m_LeftValid = m_RightValid = false;
        m_LeftDistance = m_RightDistance = Positive(m_BoundaryRange, 15f);
        var origin = Car.transform.position;
        if (SampleGround(origin) != GroundKind.Road) return;
        var right = Vector3.ProjectOnPlane(Car.transform.right, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.5f) return;
        m_LeftValid = FindBoundary(origin, -right, out m_LeftDistance);
        m_RightValid = FindBoundary(origin, right, out m_RightDistance);
    }

    bool FindBoundary(Vector3 origin, Vector3 direction, out float distance)
    {
        var range = Positive(m_BoundaryRange, 15f);
        var spacing = Mathf.Clamp(m_GroundSampleSpacing, 0.1f, 2f);
        distance = range;
        var previous = 0f;
        var samples = Mathf.CeilToInt(range / spacing);
        for (var i = 1; i <= samples; i++)
        {
            var next = Mathf.Min(i * spacing, range);
            var kind = SampleGround(origin + direction * next);
            if (kind == GroundKind.Missing) return false;
            if (kind == GroundKind.OffTrack)
            {
                for (var j = 0; j < Mathf.Clamp(m_BoundaryRefinementSteps, 1, 10); j++)
                {
                    var mid = (previous + next) * 0.5f;
                    kind = SampleGround(origin + direction * mid);
                    if (kind == GroundKind.Missing) return false;
                    if (kind == GroundKind.Road) previous = mid;
                    else next = mid;
                }
                distance = (previous + next) * 0.5f; // Horizontal metres, identical axis on both sides.
                return true;
            }
            previous = next;
        }
        return false;
    }

    Transform FindPropRoot(Transform child)
    {
        if (m_TrackInstance == null) return null;
        for (var node = child; node != null && node != m_TrackInstance.transform; node = node.parent)
        {
            // TrackObstacleBuilder tags placed prop roots, not necessarily collider children.
            if (node.parent != null && node.parent.name == "Obstacles"
                && node.parent.parent == m_TrackInstance.transform)
                return node.CompareTag(TrackTags.Obstacle) || node.CompareTag(TrackTags.Ramp) ? node : null;
        }
        return null;
    }

    void RefreshObstacleSensor()
    {
        Array.Clear(m_ObstacleObservations, 0, m_ObstacleObservations.Length);
        var range = Positive(m_ObstacleRange, 50f);
        var origin = Car.transform.position + Vector3.up * NonNegative(m_ObstacleRayHeight);
        var forward = Vector3.ProjectOnPlane(Car.transform.forward, Vector3.up).normalized;
        for (var i = 0; i < s_ObstacleAngles.Length; i++)
        {
            var offset = i * 4;
            m_ObstacleObservations[offset] = 1f;
            var direction = Quaternion.AngleAxis(s_ObstacleAngles[i], Vector3.up) * forward;
            var count = Physics.SphereCastNonAlloc(origin, Mathf.Max(0.01f, m_ObstacleCastRadius),
                direction, m_ObstacleHits, range, RacingLayers.SensorMask, QueryTriggerInteraction.Ignore);
            var hits = m_ObstacleHits;
            if (count == hits.Length) // Do not silently lose the closest relevant prop in a full buffer.
            {
                hits = Physics.SphereCastAll(origin, Mathf.Max(0.01f, m_ObstacleCastRadius),
                    direction, range, RacingLayers.SensorMask, QueryTriggerInteraction.Ignore);
                count = hits.Length;
            }
            var nearest = float.PositiveInfinity;
            for (var j = 0; j < count; j++)
            {
                if (hits[j].collider == null || hits[j].collider.attachedRigidbody == Car.Body) continue;
                var prop = FindPropRoot(hits[j].collider.transform);
                if (prop == null || hits[j].distance >= nearest) continue;
                nearest = hits[j].distance;
                m_ObstacleObservations[offset] = Mathf.Clamp01(nearest / range);
                m_ObstacleObservations[offset + 1] = 1f;
                m_ObstacleObservations[offset + 2] = prop.CompareTag(TrackTags.Obstacle) ? 1f : 0f;
                m_ObstacleObservations[offset + 3] = prop.CompareTag(TrackTags.Ramp) ? 1f : 0f;
            }
        }
    }

    void ApplyTrainingObstacles()
    {
        if (!m_TrainingEpisode || m_TrackInstance == null) return;
        // Include inactive descendants, and retiring roots still awaiting deferred Destroy.
        foreach (var node in m_TrackInstance.GetComponentsInChildren<Transform>(true))
        {
            if (node.parent == null || node.parent.name != "Obstacles"
                || node.parent.parent != m_TrackInstance.transform || !node.CompareTag(TrackTags.Obstacle))
                continue;
            // Never switch a mixed hierarchy containing Ramp off as a whole.
            var hasRamp = false;
            foreach (var descendant in node.GetComponentsInChildren<Transform>(true))
                if (descendant.CompareTag(TrackTags.Ramp)) { hasRamp = true; break; }
            if (hasRamp) continue;
            if (!m_ObstacleOriginalStates.ContainsKey(node.gameObject))
                m_ObstacleOriginalStates.Add(node.gameObject, node.gameObject.activeSelf);
            if (m_CurrentStage < 3) node.gameObject.SetActive(false);
        }
        Physics.SyncTransforms();
    }

    void RestoreTrainingObstacles()
    {
        // Restore only objects THIS training agent changed, with their original states.
        // Evaluation rigs never populate this dictionary.
        foreach (var entry in m_ObstacleOriginalStates)
            if (entry.Key != null && entry.Key.activeSelf != entry.Value) entry.Key.SetActive(entry.Value);
        m_ObstacleOriginalStates.Clear();
    }

    protected override void OnDisable()
    {
        RestoreTrainingObstacles();
        if (m_CollisionSensor != null) m_CollisionSensor.Owner = null;
        base.OnDisable();
        m_EpisodeActive = false;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (!EnsureEpisode())
        {
            for (var i = 0; i < ObservationSize - 32; i++) sensor.AddObservation(0f);
            sensor.AddObservation(1f); sensor.AddObservation(0f);
            sensor.AddObservation(1f); sensor.AddObservation(0f);
            for (var i = 0; i < s_ObstacleAngles.Length; i++)
            {
                sensor.AddObservation(1f); sensor.AddObservation(0f);
                sensor.AddObservation(0f); sensor.AddObservation(0f);
            }
            return;
        }
        sensor.AddObservation(Car.ForwardSpeed / 50f);
        sensor.AddObservation(StageMinSpeedKph(m_CurrentStage) / 3.6f / 50f); // Same scale as the speed above.
        // A negative throttle is the brake above ReverseEngageSpeed and reverse gear below it.
        // At the /50 scale above that switch sits at 0.016 and is lost to normalisation, so the
        // policy gets it as its own channel rather than having to resolve it out of the speed.
        sensor.AddObservation(Car.ForwardSpeed < CarSpec.ReverseEngageSpeed ? 1f : 0f);
        sensor.AddObservation(Car.LocalVelocity.x / 50f);
        sensor.AddObservation(Car.LocalAngularVelocity.y / 5f);
        sensor.AddObservation(Car.SteerAngleNormalized);
        sensor.AddObservation(Car.SlipAngle / 45f);
        var projection = Projection;
        sensor.AddObservation(projection.Lateral / Mathf.Max(0.5f, projection.Width * 0.5f));
        sensor.AddObservation(projection.Width / 12f);
        sensor.AddObservation(IsOffTrack ? 1f : 0f);
        for (var i = 1; i <= m_WaypointCount; i++)
        {
            var world = Track.SampleAtDistance(projection.Distance + i * m_WaypointSpacing).Position;
            var local = Car.transform.InverseTransformPoint(world);
            sensor.AddObservation(local.x / 50f);
            sensor.AddObservation(local.z / 50f);
        }
        for (var i = 0; i < m_CurvatureCount; i++)
            sensor.AddObservation(Mathf.Clamp(CurvatureAhead(i * m_CurvatureWindowLength,
                m_CurvatureWindowLength) * 30f, -3f, 3f));

        RefreshBoundarySensor();
        var range = Positive(m_BoundaryRange, 15f);
        sensor.AddObservation(Mathf.Clamp01(m_LeftDistance / range));
        sensor.AddObservation(m_LeftValid ? 1f : 0f);
        sensor.AddObservation(Mathf.Clamp01(m_RightDistance / range));
        sensor.AddObservation(m_RightValid ? 1f : 0f);
        RefreshObstacleSensor();
        foreach (var value in m_ObstacleObservations) sensor.AddObservation(value);
    }

    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static float NonNegative(float value) => IsFinite(value) ? Mathf.Max(0f, value) : 0f;
    static float Positive(float value, float fallback) => IsFinite(value) && value > 0f ? value : fallback;
}

// Runtime-only contact sensor on the existing car rigidbody. Kept in this file to
// respect the implementation boundary; it never changes vehicle physics.
[AddComponentMenu("")]
public sealed class MingiRacerCollisionSensor : MonoBehaviour
{
    [NonSerialized] public MingiRacerAgent Owner;
    void OnCollisionEnter(Collision collision)
    {
        if (Owner != null) Owner.RecordObstacleContact(collision.collider);
    }
}
