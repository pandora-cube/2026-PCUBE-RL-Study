using Unity.MLAgents.Sensors;
using UnityEngine;
using RacingBotCup.Track;

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
    public class SeongwonRacerAgent : RacerAgent
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
        [Tooltip("Per metre of forward progress along the circuit.")]
        [SerializeField] float m_ProgressReward = 0.02f;

        [Tooltip("Charged every decision, so standing still is never comfortable.")]
        [SerializeField] float m_TimePenalty = 0.001f;

        [Tooltip("Charged while any part of the car is off the racing surface.")]
        [SerializeField] float m_OffTrackPenalty = 0.01f;

        [SerializeField] float m_LapBonus = 20f;

        [SerializeField] float m_FailurePenalty = 3f;

        [Tooltip("장애물에 부딪혔을 때 처벌")]
        [SerializeField] float m_ObstaclePenalty = 0.1f;
        //가운데 라인 유지하지 못 할 때 패널티
        [SerializeField] float m_CenterPenalty = 0.001f;

        [Header("구간에 따른 보상 Shaping")]
        [SerializeField] float m_AheadTypeDetermin = 40f;
        [SerializeField] float m_HairpinSpeed = 100f;
        [SerializeField] float m_hairpinPenalty = 0.0003f;

        [SerializeField] float m_StraightSpeed = 100f;
        [SerializeField] float m_StraightReward = 0.0005f;

        float m_LastProgress;

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

            float offWheelRatio = Mathf.Clamp01(Car.WheelsOffTrack / 4f);
            float onWheelRatio = 1f - offWheelRatio;

            // 지난 결정 이후 전진한 거리
            float progress = Checkpoints.TraveledDistance;
            float progressDelta = progress - m_LastProgress;
            m_LastProgress = progress;

            float forwardDelta = Mathf.Max(0f, progressDelta);

            var currentType = SectionAhead(0f).Type;

            bool allowsOffRoad =
                currentType == TrackSectionType.RampCorner ||
                currentType == TrackSectionType.ObstacleStraight;

            float validProgressDelta = allowsOffRoad
                ? forwardDelta
                : forwardDelta * onWheelRatio;

            // 램프·장애물 우회 중에도 전진 보상 유지
            AddReward(validProgressDelta * m_ProgressReward);

            // 멈춰 있으면 계속 불리하도록 유지
            AddReward(-m_TimePenalty);

            // 일반 구간에서만 장외 페널티 적용
            if (!allowsOffRoad)
            {
                AddReward(
                    -offWheelRatio * offWheelRatio * m_OffTrackPenalty);
            }

            // 전방 구간에 따른 거리 비례 shaping
            var middleType = SectionAhead(m_AheadTypeDetermin * 0.5f).Type;
            var aheadType = SectionAhead(m_AheadTypeDetermin).Type;

            bool needsSlowdown =
                IsSlowSection(currentType) ||
                IsSlowSection(middleType) ||
                IsSlowSection(aheadType);

            bool allStraight =
                currentType == TrackSectionType.Straight &&
                middleType == TrackSectionType.Straight &&
                aheadType == TrackSectionType.Straight;

            float speed = Mathf.Max(0f, Car.ForwardSpeed);

            if (needsSlowdown)
            {
                float targetSpeed = Mathf.Max(1f, m_HairpinSpeed);
                float overspeedRatio =
                    Mathf.Max(0f, speed - targetSpeed) / targetSpeed;

                AddReward(
                    -overspeedRatio * overspeedRatio
                    * m_hairpinPenalty
                    * forwardDelta);
            }
            else if (allStraight)
            {
                float targetSpeed = Mathf.Max(1f, m_StraightSpeed);
                float speedRatio = Mathf.Clamp01(speed / targetSpeed);

                AddReward(
                    speedRatio
                    * m_StraightReward
                    * validProgressDelta);
                float halfWidth = Mathf.Max(0.5f, Projection.Width * 0.5f);

                // 중앙 = 0, 도로 가장자리 = 1
                float lateralRatio =
                    Mathf.Abs(Projection.Lateral) / halfWidth;

                // 중앙 주변에는 여유를 허용
                const float freeZone = 0.2f;

                float deviation = Mathf.Clamp01(
                    (lateralRatio - freeZone) / (1f - freeZone));

                AddReward(
                    -deviation * deviation
                    * m_CenterPenalty
                    * forwardDelta);
            }

        }

        private static bool IsSlowSection(TrackSectionType type)
        {
            return type == TrackSectionType.Hairpin
                || type == TrackSectionType.SharpHairpin
                || type == TrackSectionType.Chicane;
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
            base.OnEpisodeBegin();
        }

        public void OnCollisionEnter(Collision collision)
        {
            //장애물 충돌 처벌
            if (collision.gameObject.CompareTag("Obstacle"))
            {
                AddReward(-m_ObstaclePenalty);
            }
        }
    }
}
