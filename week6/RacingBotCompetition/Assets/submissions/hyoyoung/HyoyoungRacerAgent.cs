using Unity.MLAgents.Sensors;
using UnityEngine;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// A worked example of a competition entry. Copy this file, rename the class, and change
    /// whatever you like — this is a starting point, not a recommendation.
    ///
    /// It shows the two things you are expected to design (기획서 §3):
    /// <list type="number">
    /// <item><b>Observations</b> — what the policy is allowed to see, in
    /// <see cref="CollectObservations"/> plus whatever sensor components you add in the Inspector.</item>
    /// <item><b>Rewards</b> — what counts as doing well, in <see cref="OnDriveApplied"/> and the
    /// two episode hooks. Rewards exist only while training and never touch your score.</item>
    /// </list>
    ///
    /// Everything read below comes from <see cref="RacerAgent"/>. Values are scaled to roughly
    /// -1..1 by hand here; a policy learns much faster when its inputs are on a similar scale, and
    /// raw metres per second next to a 0-to-1 lap fraction is not.
    /// </summary>
    public class HyoyoungRacerAgent : RacerAgent
    {
        [Header("Observation shape")]
        [Tooltip("How many centreline points ahead to look at.")]
        [SerializeField] int m_Waypoints = 5;

        [Tooltip("Metres between those points.")]
        [SerializeField] float m_WaypointSpacing = 10f;

        [Tooltip("How many curvature windows ahead to sample.")]
        [SerializeField] int m_CurvatureWindows = 3;

        [SerializeField] float m_CurvatureWindowLength = 20f;

        [Header("Reward shaping")]
        [Tooltip("한 바퀴를 다 돌았을 때 누적되는 전진 보상의 총량 (트랙 길이와 무관하게 랩당 이 값). " +
                 "이전에는 미터당 고정값이라 짧은/긴 트랙마다 총량이 달라졌음")]
        [SerializeField] float m_ProgressReward = 0.3f;

        [SerializeField] float m_LapBonus = 20f;

        [SerializeField] float m_FailurePenalty = 3f;

        [Tooltip("진전 여부를 확인할 결정 구간 길이")]
        [SerializeField] int m_StuckWindowDecisions = 25;

        [Tooltip("이 창 동안 순변위가 이 거리(m) 미만이면 정지로 간주")]
        [SerializeField] float m_StuckDistanceThreshold = 1f;

        [Tooltip("정지 지속 페널티 (정지로 판정된 창 동안 매 틱 반복 부과)")]
        [SerializeField] float m_StuckPenalty = 0.03f;

        [Header("Collision penalty")]
        [Tooltip("브레이크나 오프로드로 설명되지 않는 급격한 감속(=충돌)에 부과하는 페널티. " +
                 "정면 충돌이면 최대 이 값의 2배까지 커짐")]
        [SerializeField] float m_ImpactPenalty = 0.15f;

        [Tooltip("이 값(m/s^2)을 넘는 감속을 충돌로 판정. 풀브레이크가 대략 25, 오프로드 저항이 " +
                 "추가로 15 정도라 그 합보다 위로 잡아야 함")]
        [SerializeField] float m_ImpactDecelThreshold = 35f;

        [Tooltip("스로틀이 이 값 이하면 브레이크로 보고 감속을 고의로 취급 (충돌 아님)")]
        [SerializeField] float m_BrakeThreshold = -0.05f;

        [Tooltip("이 속도(m/s) 미만에서의 감속은 충돌로 치지 않음")]
        [SerializeField] float m_ImpactMinSpeed = 3f;

        [Tooltip("한 번 충돌로 판정된 뒤 다시 과금하기까지의 쿨다운(초). 한 번의 충돌이 " +
                 "여러 틱에 걸쳐 중복 과금되는 것을 막음")]
        [SerializeField] float m_ImpactCooldown = 0.5f;

        float m_LastProgress;
        float m_WindowStartProgress;
        int m_WindowDecisionCount;
        bool m_IsStuck;

        float m_LastSpeed;
        float m_LastSpeedTime;
        float m_ImpactCooldownRemaining;

        /// <summary>
        /// Total floats written below. Put this number in BehaviorParameters →
        /// Vector Observation → Space Size, or the policy and the observations disagree and
        /// ML-Agents throws on the first decision.
        /// </summary>
        public int ObservationSize => 8 + m_Waypoints * 2 + m_CurvatureWindows;

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!IsBound)
            {
                // Bound by the harness before the first decision; this guard only matters if you
                // drop the agent into a scene by hand.
                for (var i = 0; i < ObservationSize; i++)
                {
                    sensor.AddObservation(0f);
                }

                return;
            }

            // --- how the car is moving (5 floats) ---
            sensor.AddObservation(Car.ForwardSpeed / 50f);
            sensor.AddObservation(Car.LocalVelocity.x / 50f);
            sensor.AddObservation(Car.LocalAngularVelocity.y / 5f);
            sensor.AddObservation(Car.SteerAngleNormalized);
            sensor.AddObservation(Car.SlipAngle / 45f);

            // --- where it is on the circuit (3 floats) ---
            var projection = Projection;
            var halfWidth = Mathf.Max(0.5f, projection.Width * 0.5f);
            sensor.AddObservation(projection.Lateral / halfWidth);   // ±1 at the road edges
            sensor.AddObservation(projection.Width / 12f);
            sensor.AddObservation(IsOffTrack ? 1f : 0f);

            // --- what is coming up (m_Waypoints * 2 + m_CurvatureWindows floats) ---
            for (var i = 1; i <= m_Waypoints; i++)
            {
                var local = WaypointLocal(i * m_WaypointSpacing);
                sensor.AddObservation(local.x / 50f);
                sensor.AddObservation(local.z / 50f);
            }

            for (var i = 0; i < m_CurvatureWindows; i++)
            {
                var curvature = CurvatureAhead(i * m_CurvatureWindowLength, m_CurvatureWindowLength);
                sensor.AddObservation(Mathf.Clamp(curvature * 30f, -3f, 3f));
            }
        }

        // ------------------------------------------------------------------
        // 보상 설계 — 여기서부터가 여러분의 영역입니다.
        // ------------------------------------------------------------------

        protected override void OnDriveApplied(float steer, float throttle)
        {
            if (!IsBound)
            {
                return;
            }

            // Progress along the centreline, as a fraction of the lap rather than in metres, so the
            // same drive down a short circuit and a long one is paid the same. Measured as a delta
            // so the agent is paid for advancing, not for being far along.
            var progress = Checkpoints.Progress;
            AddReward((progress - m_LastProgress) * m_ProgressReward);
            m_LastProgress = progress;

            // 정지 지속 시간 추적, 일정 시간 이상 멈춰있으면 짧은 창 동안만 후진+조향을 살짝 유도
            m_WindowDecisionCount++;
            if (m_WindowDecisionCount >= m_StuckWindowDecisions)
            {
                var netMoved = Mathf.Abs(progress - m_WindowStartProgress) * Track.TotalLength;
                m_IsStuck = netMoved < m_StuckDistanceThreshold;
                m_WindowStartProgress = progress;
                m_WindowDecisionCount = 0;
            }

            if (m_IsStuck)
            {
                AddReward(-m_StuckPenalty);
            }

            ChargeForImpact(throttle);
        }

        /// <summary>
        /// 충돌 감지: 차가 브레이크/오프로드로는 설명 안 되는 속도로 급감속하면 충돌로 보고
        /// 과금한다. 차량 쪽에 충돌 콜백이 없어서 속도 변화율로 추론.
        /// </summary>
        void ChargeForImpact(float throttle)
        {
            var speed = Car.Speed;
            var now = Time.fixedTime;
            var deltaTime = now - m_LastSpeedTime;
            var previousSpeed = m_LastSpeed;

            m_LastSpeed = speed;
            m_LastSpeedTime = now;
            m_ImpactCooldownRemaining = Mathf.Max(0f, m_ImpactCooldownRemaining - Mathf.Max(0f, deltaTime));

            if (deltaTime <= 0f)
            {
                return;
            }

            if (m_ImpactCooldownRemaining > 0f ||
                previousSpeed < m_ImpactMinSpeed ||
                throttle <= m_BrakeThreshold)
            {
                return;
            }

            var deceleration = (previousSpeed - speed) / deltaTime;
            if (deceleration < m_ImpactDecelThreshold)
            {
                return;
            }

            var severity = Mathf.Clamp01((deceleration - m_ImpactDecelThreshold) / m_ImpactDecelThreshold);
            AddReward(-m_ImpactPenalty * (1f + severity));
            m_ImpactCooldownRemaining = m_ImpactCooldown;
        }

        public override void OnLapCompleted(float elapsedSeconds)
        {
            // Finishing is worth a lot, finishing quickly a little more.
            AddReward(Mathf.Max(1f, m_LapBonus - elapsedSeconds * 0.05f));
        }

        public override void OnRunFailed()
        {
            AddReward(-m_FailurePenalty);
        }

        public override void OnEpisodeBegin()
        {
            m_LastProgress = 0f;
            m_WindowStartProgress = 0f;
            m_WindowDecisionCount = 0;
            m_IsStuck = false;
            m_LastSpeed = 0f;
            m_LastSpeedTime = Time.fixedTime;
            m_ImpactCooldownRemaining = 0f;
            base.OnEpisodeBegin();
        }

    }
}
