using System;
using System.Collections;
using System.Collections.Generic;
using RacingBotCup.Agent;
using RacingBotCup.Eval;
using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.InferenceEngine;
using Unity.MLAgents;
using UnityEngine;

namespace RacingBotCup.Finals
{
    /// <summary>
    /// Runs the finals (기획서 §8): the whole field on one private circuit at a time, three
    /// circuits, points added up as it goes.
    ///
    /// It borrows the evaluation harness's stepping rather than its scoring — manual Academy
    /// stepping, <c>Physics.Simulate</c> once per tick, paced to real time — because a policy
    /// driven any other way drives differently (see <see cref="EvaluationRunner"/>). What it does
    /// not borrow is the baseline bot: there is no ratio to compute here, the lap times are raced
    /// directly against each other.
    ///
    /// Every car starts from the same pose and passes through the others — the finals are still six
    /// simultaneous time attacks, not a race with contact (기획서 §2), so nobody's line can be
    /// spoiled by somebody else's. That is also why the grid is not staggered: a car placed a metre
    /// back would have a metre further to drive.
    ///
    /// A racer may bring their own car (<see cref="Entry.CarPrefab"/>). Anyone who does not gets the
    /// shared one in their own colour. Worth knowing before handing out liveries: mass, torque,
    /// steering lock and grip all come from <see cref="CarSpec"/> and stay identical, but the wheel
    /// positions, wheel radius and body box are measured off the art
    /// (<see cref="CarFactory.Build"/>) — so a different preset is a slightly different car, not
    /// just a different paint job.
    /// </summary>
    public sealed class FinalsDirector : MonoBehaviour
    {
        public enum Phase
        {
            Idle,
            Racing,

            /// <summary>Circuit over, standings up, waiting for the operator to send the field out again.</summary>
            Intermission,

            Finished,
        }

        [Serializable]
        public sealed class Entry
        {
            [Tooltip("차 위와 순위표에 표시될 이름")]
            public string Name;

            [Tooltip("RacerAgent를 상속한 컴포넌트가 붙은 프리팹. 비워 두면 베이스라인 봇이 대신 달립니다")]
            public GameObject AgentPrefab;

            [Tooltip("학습된 .onnx 모델. 비워 두면 키보드로 조작됩니다")]
            public ModelAsset Model;

            [Tooltip("이 참가자의 차. 비워 두면 공용 RaceCar에 참가자 색을 입혀서 씁니다.\n" +
                     "PolygonStreetRacer의 아트 프리팹(SM_Veh_..._Preset_..)을 그대로 넣으면 됩니다.\n" +
                     "주의: 차종이 바뀌면 휠베이스·휠 반경·차체 크기가 함께 바뀝니다 (성능이 완전히 같지 않습니다)")]
            public GameObject CarPrefab;
        }

        [Header("결선 참가자 (최대 6명)")]
        [SerializeField] List<Entry> m_Entries = new List<Entry>();

        [Header("결선 트랙")]
        [Tooltip("비공개 결선 시드. 순서대로 한 맵씩 진행합니다")]
        [SerializeField] int[] m_Seeds = { 120014, 120005, 120015 };

        [Header("설정 (기본값 그대로 두세요)")]
        [Tooltip("차를 따로 지정하지 않은 참가자가 타는 공용 차")]
        [SerializeField] GameObject m_CarPrefab;
        [SerializeField] TrackMaterials m_Materials = new TrackMaterials();
        [SerializeField] TrackPropCatalogue m_Props = new TrackPropCatalogue();

        [Tooltip("한 맵의 제한 시간. 남아 있는 차는 리타이어 처리됩니다")]
        [SerializeField] float m_TimeLimitSeconds = 150f;

        [SerializeField] bool m_StartOnPlay = true;

        /// <summary>Livery colours, in entry order. Six distinct hues, so the tag over a car and its
        /// row on the standings board are the same colour.</summary>
        static readonly Color[] k_Colours =
        {
            new Color(0.95f, 0.32f, 0.28f),
            new Color(0.35f, 0.62f, 0.98f),
            new Color(0.42f, 0.86f, 0.45f),
            new Color(0.98f, 0.80f, 0.25f),
            new Color(0.82f, 0.48f, 0.95f),
            new Color(0.30f, 0.88f, 0.86f),
        };

        readonly List<FinalsRacer> m_Racers = new List<FinalsRacer>();
        readonly List<FinalsRacer> m_Standings = new List<FinalsRacer>();

        GameObject m_MapRoot;
        bool m_ContinuePressed;

        public Phase CurrentPhase { get; private set; } = Phase.Idle;

        /// <summary>Which circuit is up, 1-based. 0 before the first one is built.</summary>
        public int MapNumber { get; private set; }

        public int MapCount => m_Seeds?.Length ?? 0;

        public int CurrentSeed { get; private set; }

        /// <summary>Seconds raced on the current circuit.</summary>
        public float RaceTime { get; private set; }

        public float TimeLimitSeconds => m_TimeLimitSeconds;

        /// <summary>The field, in race order — the standings board reads this directly.</summary>
        public IReadOnlyList<FinalsRacer> Standings => m_Standings;

        /// <summary>The racer the camera is on, or null while nobody has been picked.</summary>
        public FinalsRacer Spectating { get; private set; }

        public string Status { get; private set; } = "idle";

        void Start()
        {
            if (m_StartOnPlay)
            {
                StartFinals();
            }
        }

        public void StartFinals()
        {
            if (CurrentPhase != Phase.Idle)
            {
                return;
            }

            if (m_Seeds == null || m_Seeds.Length == 0)
            {
                Fail("결선 시드가 비어 있습니다.");
                return;
            }

            if (m_Entries.Count == 0)
            {
                Fail("참가자가 없습니다. 이름·프리팹·모델을 등록하세요.");
                return;
            }

            StartCoroutine(RunSession());
        }

        /// <summary>The 'next circuit' button. Ignored except between circuits.</summary>
        public void Continue()
        {
            m_ContinuePressed = true;
        }

        /// <summary>Points the chase camera at one racer. Null hands it back to whoever is leading.</summary>
        public void Spectate(FinalsRacer racer)
        {
            Spectating = racer;

            var camera = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (camera != null)
            {
                camera.Spectate(racer?.Rig?.Car);
            }
        }

        // ------------------------------------------------------------------
        // Session
        // ------------------------------------------------------------------

        IEnumerator RunSession()
        {
            BuildField();

            var previousFixedDelta = Time.fixedDeltaTime;
            var previousRunInBackground = Application.runInBackground;
            var previousAutoStep = !Academy.IsInitialized || Academy.Instance.AutomaticSteppingEnabled;
            var previousSimulationMode = Physics.simulationMode;
            var previousSkidMarks = SkidMarks.GloballyEnabled;

            Time.fixedDeltaTime = CarSpec.FixedDeltaTime;
            Academy.Instance.AutomaticSteppingEnabled = false;
            Application.runInBackground = true;
            Physics.simulationMode = SimulationMode.Script;
            SkidMarks.GloballyEnabled = true;

            try
            {
                for (var map = 0; map < m_Seeds.Length; map++)
                {
                    MapNumber = map + 1;
                    CurrentSeed = m_Seeds[map];
                    RaceTime = 0f;
                    Status = $"map {MapNumber}/{m_Seeds.Length} — seed {CurrentSeed}";

                    if (!BuildMap(CurrentSeed))
                    {
                        Fail("맵을 만들지 못했습니다. 콘솔을 확인하세요.");
                        yield break;
                    }

                    Settle();
                    BeginMap();
                    CurrentPhase = Phase.Racing;

                    yield return StepMap();

                    RetireStragglers();
                    FinalsScoring.AwardMap(m_Racers, CurrentSeed);
                    RefreshStandings();
                    LogMap();

                    var last = map == m_Seeds.Length - 1;
                    if (last)
                    {
                        m_Standings.Clear();
                        m_Standings.AddRange(m_Racers);
                        FinalsScoring.OrderChampionship(m_Standings);
                        CurrentPhase = Phase.Finished;
                        Status = $"finished — {m_Standings[0].Name} wins with {m_Standings[0].TotalPoints}";
                        yield break;
                    }

                    CurrentPhase = Phase.Intermission;
                    m_ContinuePressed = false;
                    while (!m_ContinuePressed)
                    {
                        yield return null;
                    }

                    TearDownMap();
                }
            }
            finally
            {
                Time.fixedDeltaTime = previousFixedDelta;
                Application.runInBackground = previousRunInBackground;
                Physics.simulationMode = previousSimulationMode;
                SkidMarks.GloballyEnabled = previousSkidMarks;
                if (Academy.IsInitialized)
                {
                    Academy.Instance.AutomaticSteppingEnabled = previousAutoStep;
                }
            }
        }

        /// <summary>
        /// Steps every car in the field together, once per tick, until the circuit is settled or the
        /// time limit is up.
        ///
        /// One tick is one wall-clock <c>FixedDeltaTime</c>, the same pacing evaluation uses: a lap
        /// takes as long to watch as it does to drive, and — the part that matters — every policy
        /// gets a real frame between decisions, which is what keeps the inference backend from
        /// handing a car the previous car's result.
        /// </summary>
        IEnumerator StepMap()
        {
            var dt = CarSpec.FixedDeltaTime;
            var maxSteps = Mathf.CeilToInt(m_TimeLimitSeconds / dt);
            var nextRealTime = Time.realtimeSinceStartup;

            for (var step = 0; step < maxSteps; step++)
            {
                var running = 0;
                var anyDecision = false;

                foreach (var racer in m_Racers)
                {
                    if (!racer.IsRacing || racer.Context == null)
                    {
                        continue;
                    }

                    var context = racer.Context;
                    context.Refresh(dt);
                    racer.Time = context.ElapsedTime;
                    racer.Distance = context.Checkpoints.TraveledDistance;

                    if (context.LapCompletedThisTick)
                    {
                        Finish(racer, RacerState.Finished);
                        continue;
                    }

                    if (context.OffTrackDuration >= RaceRules.OffTrackDnfSeconds)
                    {
                        Finish(racer, RacerState.Retired);
                        continue;
                    }

                    racer.Rig.Driver.Tick();
                    anyDecision |= racer.Rig.Driver is RacerAgent;
                    running++;
                }

                // One Academy step for the whole field, whatever its size.
                if (anyDecision)
                {
                    Academy.Instance.EnvironmentStep();
                }

                foreach (var racer in m_Racers)
                {
                    if (racer.IsRacing && racer.Rig != null)
                    {
                        racer.Rig.Car.Step(dt);
                    }
                }

                Physics.Simulate(dt);
                RaceTime = (step + 1) * dt;
                RefreshStandings();

                if (running == 0)
                {
                    yield break;
                }

                nextRealTime += dt;
                while (Time.realtimeSinceStartup < nextRealTime)
                {
                    yield return null;
                }
            }
        }

        void Finish(FinalsRacer racer, RacerState state)
        {
            // Back off the part of the final step taken after the line, so a lap time is a
            // continuous function of where the car actually was rather than a multiple of 20 ms.
            var adjustment = state == RacerState.Finished
                ? (1f - racer.Context.Checkpoints.LapCrossingFraction) * CarSpec.FixedDeltaTime
                : 0f;

            racer.Time = racer.Context.ElapsedTime - adjustment;
            racer.State = state;
            racer.Rig.Car.SetInput(0f, 0f);
            racer.Rig.Car.IsRacing = false;
        }

        /// <summary>Anything still out there when the time limit landed retires where it stands.</summary>
        void RetireStragglers()
        {
            foreach (var racer in m_Racers)
            {
                if (racer.IsRacing)
                {
                    Finish(racer, RacerState.Retired);
                }
            }
        }

        void RefreshStandings()
        {
            m_Standings.Clear();
            m_Standings.AddRange(m_Racers);
            FinalsScoring.Order(m_Standings);

            // Nobody picked a car yet: sit on whoever is leading rather than on nothing.
            if (Spectating == null && m_Standings.Count > 0)
            {
                Spectate(m_Standings[0]);
            }
        }

        void LogMap()
        {
            foreach (var racer in m_Standings)
            {
                var time = racer.State == RacerState.Finished
                    ? RaceClock.Format(racer.Time)
                    : "RETIRED";

                Debug.Log($"[RacingBotCup] map {MapNumber} (seed {CurrentSeed}) " +
                          $"P{racer.Position} {racer.Name} {time} " +
                          $"+{racer.MapPoints} → {racer.TotalPoints}");
            }
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        void BuildField()
        {
            m_Racers.Clear();

            if (m_Entries.Count > FinalsScoring.MaxRacers)
            {
                Debug.LogWarning(
                    $"[RacingBotCup] {m_Entries.Count} entries registered but the points table has " +
                    $"{FinalsScoring.MaxRacers} places. Only the first {FinalsScoring.MaxRacers} race.");
            }

            var count = Mathf.Min(m_Entries.Count, FinalsScoring.MaxRacers);
            for (var i = 0; i < count; i++)
            {
                var entry = m_Entries[i];
                m_Racers.Add(new FinalsRacer
                {
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? $"Racer {i + 1}" : entry.Name.Trim(),
                    Colour = k_Colours[i % k_Colours.Length],
                });
            }
        }

        bool BuildMap(int seed)
        {
            m_MapRoot = new GameObject($"FinalsMap_{seed}");

            var trackObject = new GameObject("Circuit");
            trackObject.transform.SetParent(m_MapRoot.transform, false);

            var track = trackObject.AddComponent<TrackInstance>();
            track.Seed = seed;
            track.EnableHazardSections = true;
            CopyMaterials(track);
            CopyProps(track);
            track.Rebuild();

            for (var i = 0; i < m_Racers.Count; i++)
            {
                var racer = m_Racers[i];
                var entry = m_Entries[i];

                var carPrefab = entry.CarPrefab != null ? entry.CarPrefab : m_CarPrefab;

                // An entry with no prefab is driven by the baseline bot. It is how the scene can be
                // run — and the whole three-circuit flow watched — before anyone's .onnx exists,
                // and it fills a short grid with something that actually laps.
                var rig = entry.AgentPrefab == null
                    ? RacerBuilder.BuildBaseline(carPrefab, m_MapRoot.transform)
                    : RacerBuilder.BuildAgent(
                        carPrefab,
                        entry.AgentPrefab,
                        entry.Model,
                        manualStepping: true,
                        parent: m_MapRoot.transform);

                if (rig == null)
                {
                    Debug.LogError($"[RacingBotCup] '{racer.Name}' could not be built — check the prefab.");
                    return false;
                }

                rig.Root.name = $"Racer_{racer.Name}";

                // Only the shared car gets painted. A racer who brought their own is already
                // distinguishable, and repainting it would throw away the look they picked — their
                // colour stays on the name tag and the standings row either way.
                if (entry.CarPrefab == null)
                {
                    rig.Tint(racer.Colour);
                }

                racer.Rig = rig;
                racer.Context = rig.PlaceOnTrack(track.Model);
                racer.State = RacerState.Racing;
                racer.Time = 0f;
                racer.Distance = 0f;
                racer.MapPoints = 0;
            }

            // Every pair, so no car can ever shove another off its line.
            for (var i = 0; i < m_Racers.Count; i++)
            {
                for (var j = i + 1; j < m_Racers.Count; j++)
                {
                    EvaluationRunner.IgnoreCollisionsBetween(m_Racers[i].Rig.Root, m_Racers[j].Rig.Root);
                }
            }

            // The camera was following a car that no longer exists.
            Spectating = null;
            RefreshStandings();
            return true;
        }

        void TearDownMap()
        {
            foreach (var racer in m_Racers)
            {
                racer.Rig?.Driver.EndRun();
                racer.Rig = null;
                racer.Context = null;
            }

            if (m_MapRoot != null)
            {
                Destroy(m_MapRoot);
                m_MapRoot = null;
            }
        }

        /// <summary>Lets the suspension load up before the lights go out, so nobody starts mid-bounce.</summary>
        void Settle()
        {
            const float settleSeconds = 0.5f;
            var dt = CarSpec.FixedDeltaTime;

            Physics.SyncTransforms();

            var steps = Mathf.CeilToInt(settleSeconds / dt);
            for (var i = 0; i < steps; i++)
            {
                foreach (var racer in m_Racers)
                {
                    racer.Rig.Car.SetInput(0f, 0f);
                    racer.Rig.Car.Step(dt);
                }

                Physics.Simulate(dt);
            }
        }

        void BeginMap()
        {
            foreach (var racer in m_Racers)
            {
                racer.Context.Reset();
                racer.Rig.Driver.BeginRun();
            }
        }

        void CopyMaterials(TrackInstance track)
        {
            if (m_Materials == null)
            {
                return;
            }

            track.Materials.Road = m_Materials.Road;
            track.Materials.Runoff = m_Materials.Runoff;
            track.Materials.Ground = m_Materials.Ground;
        }

        void CopyProps(TrackInstance track)
        {
            if (m_Props == null)
            {
                return;
            }

            track.Props.Barriers = m_Props.Barriers;
            track.Props.Crates = m_Props.Crates;
            track.Props.Logs = m_Props.Logs;
            track.Props.Containers = m_Props.Containers;
            track.Props.Ramp = m_Props.Ramp;
        }

        void Fail(string message)
        {
            Status = "failed";
            CurrentPhase = Phase.Finished;
            Debug.LogError($"[RacingBotCup] {message}");
        }
    }
}
