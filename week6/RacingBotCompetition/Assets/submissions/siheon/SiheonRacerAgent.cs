using UnityEngine;
using Unity.MLAgents.Sensors;

namespace RacingBotCup.Agent
{
    /// <summary>
    /// Observation redesign, trained from scratch as racer-sensor-02.
    ///
    /// avoid-01 and finish-01 both plateaued around a 0.75 training clear rate, and finish-01's
    /// extra 2M steps moved neither the clear rate nor the entropy. What the policy could see was
    /// the limit, not how it was rewarded, so this agent changes the observations and leaves the
    /// reward numbers where finish-01 left them.
    ///
    /// Three things the old shape could not express:
    ///
    /// 1. The DNF rule. A run is abandoned after <see cref="Eval.RaceRules.OffTrackDnfSeconds"/>
    ///    with all four wheels off the road, and neither the wheel count nor the elapsed off-road
    ///    time was observed. The old off-track flag came from <c>Projection.IsOnRoad</c> — the car's
    ///    centre, a different test from the one that ends the episode.
    /// 2. How sharp the corner ahead really is. A signed mean over a 20 m window cancels out in an
    ///    S-bend and flattens a hairpin, so this also reports the peak absolute curvature per band
    ///    and looks 100 m ahead rather than 50 m.
    /// 3. Whether the car is already too fast for that corner. Speed and curvature were both
    ///    observed, but the policy had to learn the relation between them from scratch; the safe
    ///    cornering speed is cheap to compute and is given directly.
    ///
    /// Everything here is a read of the API on <see cref="RacerAgent"/> — no track, physics or
    /// scoring behaviour is touched.
    /// </summary>
    public class SiheonRacerAgent : RacerAgent
    {
        /// <summary>Lookahead distances for the centreline waypoints, in metres.</summary>
        static readonly float[] k_WaypointDistances = { 4f, 8f, 14f, 22f, 32f, 45f, 62f, 85f };

        /// <summary>Start of each curvature band, in metres ahead of the car.</summary>
        static readonly float[] k_BandStarts = { 0f, 15f, 35f, 60f };

        /// <summary>Length of each curvature band. Near bands are short so a hairpin stays sharp.</summary>
        static readonly float[] k_BandLengths = { 15f, 20f, 25f, 40f };

        /// <summary>Samples taken across a band when looking for its peak curvature.</summary>
        const int k_BandSamples = 6;

        /// <summary>
        /// Curvature scale. The tightest corner the generator produces is about 0.114 /m
        /// (an 8.8 m radius), so this puts a hairpin near 1 and leaves a straight near 0.
        /// </summary>
        const float k_CurvatureScale = 8f;

        /// <summary>Ceiling on the reported safe speed, m/s. Roughly the car's cornering ceiling.</summary>
        const float k_MaxSafeSpeed = 45f;

        [Header("Cornering model")]
        [Tooltip("Lateral acceleration the tyres can hold on tarmac, m/s². Only shapes the safe-speed " +
                 "observation; the tyre model itself lives in CarSpec. speed-03 and earlier used 9.8, " +
                 "one g — but CarController.ApplyGrip multiplies the friction curve by " +
                 "CarSpec.SidewaysStiffness (2.4), so one g understated every corner by about 55%. " +
                 "Unity's slip-based model will not deliver the full 2.4 g, so this starts between.")]
        [SerializeField] float m_LateralGrip = 17f;

        [Tooltip("Braking deceleration assumed when judging whether a corner can still be made, m/s².")]
        [SerializeField] float m_BrakingDeceleration = 9.8f;

        [Tooltip("How much of the road's half-width counts towards straightening a bend. Below 1 to " +
                 "leave room for obstacles, the road edge, and a line that does not start wide.")]
        [Range(0f, 1f)]
        [SerializeField] float m_WidthUsage = 0.7f;

        [Header("Reward shaping")]
        [Tooltip("Reward per metre of positive progress along the centreline.")]
        [SerializeField] float m_ProgressReward = 0.04f;

        [Tooltip("Fraction of positive progress reward earned off-road. Reverse progress keeps its full cost.")]
        [Range(0f, 1f)]
        [SerializeField] float m_OffTrackProgressMultiplier = 0.25f;

        [Tooltip("Small per-decision cost that favours quicker finishes.")]
        [SerializeField] float m_TimePenalty = 0.001f;

        [Tooltip("Per-decision cost for having the car's centre off the road. Deliberately mild: " +
                 "using the full width of the lane is how obstacles get avoided.")]
        [SerializeField] float m_OffRoadPenalty = 0.005f;

        [Tooltip("Per-decision cost once all four wheels are off the road — the state the DNF timer counts.")]
        [SerializeField] float m_WheelsOffPenalty = 0.02f;

        [Tooltip("Extra cost added in proportion to how far the DNF timer has run.")]
        [SerializeField] float m_DnfRampPenalty = 0.05f;

        [Tooltip("Extra per-decision penalty while the car is heavily tipped over.")]
        [SerializeField] float m_TippedPenalty = 0.02f;

        [Tooltip("Finish bonus per metre-per-second of average lap speed. This is the only term that " +
                 "pays for speed — progress reward covers the same distance however long it took, so " +
                 "at the flat bonus sensor-02 trained with, six seconds of lap time was worth 0.3 " +
                 "out of about 69.")]
        [SerializeField] float m_LapSpeedBonus = 0.6f;

        [Tooltip("Floor on the finish bonus, so crawling over the line still beats not finishing.")]
        [SerializeField] float m_MinLapBonus = 4f;

        [SerializeField] float m_FailurePenalty = 6f;

        float m_LastProgress;

        /// <summary>
        /// Seconds all four wheels have been off the road, mirroring
        /// <see cref="Racing.RaceContext.OffTrackDuration"/>. Kept here because the context is not
        /// exposed to the policy, and this is the clock that ends the episode.
        /// </summary>
        float m_WheelsOffSeconds;

        // 10 vehicle values + 7 placement values + waypoints (x, z) + two readings per curvature
        // band + 2 speed-safety values. Must match BehaviorParameters → Space Size.
        public int ObservationSize =>
            10 + 7 + k_WaypointDistances.Length * 2 + k_BandStarts.Length * 2 + 2;

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!IsBound)
            {
                for (var i = 0; i < ObservationSize; i++)
                {
                    sensor.AddObservation(0f);
                }

                return;
            }

            CollectVehicleState(sensor);
            CollectPlacement(sensor);
            CollectRoadAhead(sensor);
        }

        /// <summary>Ten values describing what the car itself is doing.</summary>
        void CollectVehicleState(VectorSensor sensor)
        {
            sensor.AddObservation(Car.ForwardSpeed / 40f);
            sensor.AddObservation(Car.LocalVelocity.x / 15f);
            sensor.AddObservation(Car.LocalAngularVelocity.y / 3f);
            sensor.AddObservation(Car.SteerAngleNormalized);
            sensor.AddObservation(Car.SlipAngle / 45f);

            // Roll and pitch, so a dangerous tip is visible before the car is stuck on its side.
            sensor.AddObservation(Car.transform.up.y);
            sensor.AddObservation(Car.transform.forward.y);
            sensor.AddObservation(Car.LocalAngularVelocity.x / 3f);
            sensor.AddObservation(Car.LocalAngularVelocity.z / 3f);

            // The wheel count, not just a boolean: three wheels off is one mistake from a DNF.
            sensor.AddObservation(Car.WheelsOffTrack / 4f);
        }

        /// <summary>Seven values placing the car on the road, including how close the DNF is.</summary>
        void CollectPlacement(VectorSensor sensor)
        {
            var projection = Projection;
            var halfWidth = Mathf.Max(0.5f, projection.Width * 0.5f);

            sensor.AddObservation(Mathf.Clamp(projection.Lateral / halfWidth, -3f, 3f));
            sensor.AddObservation(projection.Width / 12f);
            sensor.AddObservation(Car.AllWheelsOffTrack ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(m_WheelsOffSeconds / Eval.RaceRules.OffTrackDnfSeconds));

            // How far the body is turned away from the road, split into sine and cosine so that
            // pointing backwards is not confused with pointing straight ahead.
            var tangent = Flat(projection.Forward);
            var heading = Flat(Car.transform.forward);
            var headingError = Vector3.SignedAngle(tangent, heading, Vector3.up) * Mathf.Deg2Rad;
            sensor.AddObservation(Mathf.Sin(headingError));
            sensor.AddObservation(Mathf.Cos(headingError));

            // Where the car is actually travelling, which in a slide is not where it points.
            var worldVelocity = Car.transform.TransformDirection(Car.LocalVelocity);
            var travel = Flat(worldVelocity);
            var driftError = travel.sqrMagnitude < 0.01f
                ? 0f
                : Vector3.SignedAngle(tangent, travel, Vector3.up) * Mathf.Deg2Rad;
            sensor.AddObservation(Mathf.Sin(driftError));
        }

        /// <summary>Waypoints, per-band curvature, and how the current speed compares to the corner.</summary>
        void CollectRoadAhead(VectorSensor sensor)
        {
            // Bearing rather than raw offset: dividing by the lookahead keeps a 4 m and an 85 m
            // waypoint on the same scale, so the near ones do not vanish next to the far ones.
            foreach (var distance in k_WaypointDistances)
            {
                var local = WaypointLocal(distance);
                sensor.AddObservation(Mathf.Clamp(local.x / distance, -2f, 2f));
                sensor.AddObservation(Mathf.Clamp(local.z / distance, -2f, 2f));
            }

            for (var band = 0; band < k_BandStarts.Length; band++)
            {
                var start = k_BandStarts[band];
                var length = k_BandLengths[band];

                // The signed mean says which way the road bends; the peak says how hard. An S-bend
                // cancels in the mean and a hairpin flattens in it, and the peak survives both.
                var mean = CurvatureAhead(start, length);
                sensor.AddObservation(Mathf.Clamp(mean * k_CurvatureScale, -1f, 1f));

                // Reported after the width credit below, so a chicane the car can straighten reads
                // as the gentle bend it becomes rather than as the centreline's wiggle.
                var peakInBand = EffectiveCurvature(PeakCurvature(start, length), length);
                sensor.AddObservation(Mathf.Clamp01(peakInBand * k_CurvatureScale));
            }

            var peak = PeakCurvatureAhead(k_BandStarts[0], 100f, out var peakDistance);
            var cornerSpeed = SafeCorneringSpeed(EffectiveCurvature(peak, peakDistance));

            // Not "am I above the corner's speed" but "can I still get down to it in time". The
            // first reads as danger the moment any hairpin comes within 100 m, even on a straight
            // where there is ample room to brake; speed-03 sat at 120 km/h with that signal pinned.
            // v² = v_corner² + 2·a·d is the speed a full braking effort can still shed by then.
            var reachable = Mathf.Sqrt(cornerSpeed * cornerSpeed +
                                       2f * m_BrakingDeceleration * Mathf.Max(0f, peakDistance));
            sensor.AddObservation(Mathf.Clamp((Car.ForwardSpeed - reachable) / 20f, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp01(peakDistance / 100f));
        }

        /// <summary>Largest absolute curvature sampled across one band.</summary>
        float PeakCurvature(float metresAhead, float window)
        {
            return PeakCurvatureAhead(metresAhead, window, out _);
        }

        /// <summary>
        /// Largest absolute curvature between <paramref name="metresAhead"/> and the end of the
        /// window, and how far ahead it sits. Samples <see cref="Track.TrackModel.SampleAtDistance"/>
        /// directly rather than using <c>AverageCurvature</c>, which is a mean by definition.
        /// </summary>
        float PeakCurvatureAhead(float metresAhead, float window, out float distanceToPeak)
        {
            distanceToPeak = window;

            if (Track == null)
            {
                return 0f;
            }

            var origin = Projection.Distance;
            var peak = 0f;

            for (var i = 0; i < k_BandSamples; i++)
            {
                var offset = metresAhead + window * i / (k_BandSamples - 1f);
                var curvature = Mathf.Abs(Track.SampleAtDistance(origin + offset).Curvature);

                if (curvature > peak)
                {
                    peak = curvature;
                    distanceToPeak = offset;
                }
            }

            return peak;
        }

        /// <summary>
        /// The cornering speed a given curvature allows, from v = sqrt(a / k). An estimate for the
        /// policy to read, not a limit imposed on it — nothing here overrides the driver's input.
        /// </summary>
        float SafeCorneringSpeed(float curvature)
        {
            return curvature < 1e-4f
                ? k_MaxSafeSpeed
                : Mathf.Min(k_MaxSafeSpeed, Mathf.Sqrt(m_LateralGrip / curvature));
        }

        /// <summary>
        /// Curvature left after using the width of the road to straighten the bend.
        ///
        /// A racing line is not the centreline. Over a window of length d, following a centreline of
        /// curvature k means a sagitta of k·d²/8; cutting from one edge to the other removes up to
        /// half the road's width from that, which leaves k − 4·w/d². The road runs 6–12 m wide, so a
        /// chicane wandering three or four metres over sixty flattens to nothing while a hairpin's
        /// 8.8 m radius barely moves — exactly the distinction the policy should be making.
        /// </summary>
        float EffectiveCurvature(float curvature, float window)
        {
            if (curvature <= 0f || window <= 1f)
            {
                return Mathf.Max(0f, curvature);
            }

            var halfWidth = Mathf.Max(0.5f, Projection.Width * 0.5f) * Mathf.Clamp01(m_WidthUsage);
            return Mathf.Max(0f, curvature - 4f * halfWidth / (window * window));
        }

        static Vector3 Flat(Vector3 value)
        {
            value.y = 0f;
            return value.sqrMagnitude < 1e-6f ? Vector3.forward : value.normalized;
        }

        protected override void OnDriveApplied(float steer, float throttle)
        {
            if (!IsBound)
            {
                return;
            }

            var progress = Checkpoints.TraveledDistance;
            var progressDelta = progress - m_LastProgress;

            // Unchanged from finish-01: full credit for progress anywhere on the road including the
            // edges, a quarter of it for gaining ground off-road, and the full cost for reversing
            // either way — discounting reverse progress too would make off-road shuffling pay.
            var progressScale = IsOffTrack && progressDelta > 0f
                ? Mathf.Clamp01(m_OffTrackProgressMultiplier)
                : 1f;
            AddReward(progressDelta * m_ProgressReward * progressScale);
            m_LastProgress = progress;

            AddReward(-m_TimePenalty);

            if (IsOffTrack)
            {
                AddReward(-m_OffRoadPenalty);
            }

            // The DNF timer counts all four wheels off the road, so the penalty is charged against
            // that same state and grows as the timer runs down. finish-01 charged a flat cost for
            // the car's centre leaving the road, which is neither the state that ends the run nor a
            // reason to keep the car pinned to the middle of the lane.
            if (Car.AllWheelsOffTrack)
            {
                m_WheelsOffSeconds += Time.fixedDeltaTime * Eval.RaceRules.DecisionPeriod;
                var ramp = Mathf.Clamp01(m_WheelsOffSeconds / Eval.RaceRules.OffTrackDnfSeconds);
                AddReward(-m_WheelsOffPenalty - m_DnfRampPenalty * ramp);
            }
            else
            {
                m_WheelsOffSeconds = 0f;
            }

            if (Car.transform.up.y < 0.5f)
            {
                AddReward(-m_TippedPenalty);
            }
        }

        /// <summary>
        /// Pays the finish bonus on average lap speed rather than on elapsed time.
        ///
        /// The score is baseline time over agent time, which is a ratio of average speeds, and a
        /// circuit is a fresh random length every episode — the published seeds range from a 42 s
        /// baseline to a 97 s one. Charging elapsed seconds would hand a short circuit a far bigger
        /// bonus than a long one driven exactly as well, teaching "hope for a short track" on top of
        /// "go faster". Speed is the part the policy controls.
        /// </summary>
        public override void OnLapCompleted(float elapsedSeconds)
        {
            if (elapsedSeconds <= 0.01f || Track == null)
            {
                AddReward(m_MinLapBonus);
                return;
            }

            var averageSpeed = Track.TotalLength / elapsedSeconds;
            AddReward(Mathf.Max(m_MinLapBonus, m_LapSpeedBonus * averageSpeed));
        }

        public override void OnRunFailed()
        {
            AddReward(-m_FailurePenalty);
        }

        public override void OnEpisodeBegin()
        {
            m_LastProgress = 0f;
            m_WheelsOffSeconds = 0f;
            base.OnEpisodeBegin();
        }
    }
}
