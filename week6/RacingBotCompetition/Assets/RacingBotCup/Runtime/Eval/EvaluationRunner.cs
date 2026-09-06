using System;
using System.Collections;
using System.Collections.Generic;
using RacingBotCup.Agent;
using RacingBotCup.Racing;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.InferenceEngine;
using Unity.MLAgents;
using UnityEngine;

namespace RacingBotCup.Eval
{
    /// <summary>
    /// Runs the whole evaluation. There is no unwatched, fast-forwarded mode any more — every
    /// circuit runs on its own, in seed order, paced to real time, start to finish, on screen. That
    /// used to be one of two paths, the other racing every circuit in the seed set at once with no
    /// wall-clock pacing, on the theory that neither could change a score. Neither premise held:
    ///
    /// The dominant break — running many <c>Academy.EnvironmentStep()</c> inference calls back to
    /// back with no real Unity frame in between left later decisions in that span reading a stale
    /// result from the inference backend rather than the one just scheduled for them. Right after
    /// the very first decision of a run (nothing to be stale yet) the two paths matched to nine
    /// significant figures; by the second, the fast-forwarded path was steering a materially
    /// different car than the one paced to real time ever was. A policy reacting to the wrong
    /// observation sends the rest of the lap somewhere else entirely — sometimes far enough to flip
    /// finish/DNF outright.
    ///
    /// The smaller break — PhysX does not promise that a rigidbody's simulated result is unaffected
    /// by how many *other*, untouching rigidbodies share the scene; ten circuits' worth of cars at
    /// once nudged it by a handful of ULPs relative to one circuit alone. Usually inconsequential,
    /// except for how easily that is enough to flip a ray sensor's hit/miss right at a graze — the
    /// challenger and the baseline start a lap overlapping, and <see cref="IgnoreCollisionsBetween"/>
    /// only silences the physics *response* between them, not each other's raycasts.
    ///
    /// Racing every circuit on its own at real-time pace fixes both, and means what gets scored is
    /// always exactly what was watched happen — there is no second, faster path whose numbers might
    /// differ. The cost is wall-clock time: this is no longer the roughly-tenfold speedup racing
    /// everything at once used to be, which is also why <see cref="RaceRules.TimeoutMultiplier"/>
    /// and <see cref="RaceRules.BaselineTimeoutSeconds"/> are both tuned low — a car going nowhere
    /// now costs real time to sit out.
    ///
    /// Each circuit puts the baseline and the competitor's car through it side by side — collisions
    /// between them are disabled, so a car sharing the circuit never affects the other's physics —
    /// and the chase camera follows the competitor's car.
    ///
    /// Alongside them sits the ghost: the translucent reference car, or none at all — see
    /// <see cref="GhostMode"/>. By default it is the baseline itself, costing nothing extra. Point
    /// <see cref="Request.Ghost"/> at a model instead and the ghost becomes a third car running it,
    /// with the baseline still lapping (its time is the score) but hidden. Either way the ghost is
    /// scenery — no result of its is ever read.
    /// </summary>
    public sealed class EvaluationRunner : MonoBehaviour
    {
        /// <summary>Spacing between environments. Comfortably wider than the largest circuit.</summary>
        const float k_EnvironmentSpacing = 1600f;

        const float k_SettleSeconds = 0.5f;

        public sealed class Request
        {
            public GameObject CarPrefab;
            public GameObject AgentPrefab;
            public ModelAsset Model;
            public SeedSet Seeds;
            public TrackMaterials Materials;
            public TrackPropCatalogue Props;
            public string ParticipantId = "unknown";
            public string Note;

            /// <summary>Who occupies the ghost slot, or <see cref="GhostMode.None"/> for nobody —
            /// see <see cref="BuildEnvironment"/>.</summary>
            public GhostConfig Ghost = new GhostConfig();
        }

        public readonly struct Progress
        {
            public readonly int Finished;
            public readonly int Total;
            public readonly float ElapsedSimSeconds;

            public Progress(int finished, int total, float elapsedSimSeconds)
            {
                Finished = finished;
                Total = total;
                ElapsedSimSeconds = elapsedSimSeconds;
            }

            public override string ToString()
            {
                return $"{Finished}/{Total} environments finished ({ElapsedSimSeconds:F0}s simulated)";
            }
        }

        /// <summary>
        /// One circuit with its two timed cars, and — when somebody is watching a model ghost — a
        /// third car that is only there to be looked at.
        /// </summary>
        sealed class Environment
        {
            public int Seed;
            public GameObject Root;
            public RacerRig Baseline;
            public RacerRig Challenger;
            public RaceContext BaselineContext;
            public RaceContext ChallengerContext;
            public RunResult BaselineResult;
            public RunResult ChallengerResult;

            /// <summary>Null unless a model ghost was asked for. Never scored.</summary>
            public RacerRig Ghost;
            public RaceContext GhostContext;

            /// <summary>The ghost's lap is over. It keeps no result — this only stops it driving.</summary>
            public bool GhostDone;

            /// <summary>
            /// Deliberately blind to the ghost: the run ends when both timed cars are in, whether or
            /// not the scenery finished its own lap. Letting a slow ghost hold the circuit open would
            /// make it cost wall-clock time, which is exactly what it is not allowed to do.
            /// </summary>
            public bool Done => BaselineResult != null && ChallengerResult != null;
        }

        public IEnumerator Run(
            Request request,
            Action<SubmissionPayload> onComplete,
            Action<Progress> onProgress = null,
            Action<string> onError = null)
        {
            if (request.CarPrefab == null || request.AgentPrefab == null)
            {
                onError?.Invoke("Assign both a car prefab and an agent prefab before running.");
                yield break;
            }

            if (request.Ghost is { NeedsOwnCar: true })
            {
                // Scenery it may be, but it is scenery inside the same physics scene and the same
                // inference pass as the car being timed, so it cannot be promised to leave the
                // scores bit-identical the way merely watching does.
                Debug.LogWarning(
                    "[RacingBotCup] Racing against a model ghost. Nothing it does is scored, but it " +
                    "does share the run — set the ghost back to the baseline bot for the laps you " +
                    "intend to submit.");
            }

            var previousFixedDelta = Time.fixedDeltaTime;
            var previousRunInBackground = Application.runInBackground;
            var previousAutoStep = !Academy.IsInitialized || Academy.Instance.AutomaticSteppingEnabled;
            var previousSimulationMode = Physics.simulationMode;
            var previousSkidMarks = SkidMarks.GloballyEnabled;

            Time.fixedDeltaTime = CarSpec.FixedDeltaTime;
            Academy.Instance.AutomaticSteppingEnabled = false;
            Application.runInBackground = true;
            Physics.simulationMode = SimulationMode.Script;

            // Every run is watched now, so there is always someone to see the tyre marks.
            SkidMarks.GloballyEnabled = true;

            var arena = new GameObject("EvaluationArena");
            var environments = new List<Environment>();
            var dt = CarSpec.FixedDeltaTime;

            try
            {
                var seeds = request.Seeds.Seeds;

                // One circuit at a time, in seed order. Finished circuits are left sitting where
                // they raced rather than torn down, so cleanup stays a single sweep over
                // everything ever built, once, at the very end, instead of needing its own
                // per-seed teardown.
                for (var i = 0; i < seeds.Length; i++)
                {
                    var environment = BuildEnvironment(request, seeds[i], GridPosition(i), arena.transform);
                    if (environment == null)
                    {
                        onError?.Invoke("Could not build an environment. Check the console.");
                        yield break;
                    }

                    environments.Add(environment);

                    var batch = new List<Environment> { environment };
                    Physics.SyncTransforms();
                    Settle(batch, dt);
                    BeginBatch(batch);

                    var seedsAlreadyDone = i;
                    yield return StepBatch(batch, dt, elapsed =>
                        onProgress?.Invoke(new Progress(seedsAlreadyDone, seeds.Length, elapsed)));

                    FillTimeouts(batch);

                    // Left sitting rather than destroyed (see above), so the chase camera needs
                    // its own way to tell "just finished" apart from "still racing" — otherwise,
                    // once several finished cars have piled up, FindObjectsByType's unspecified
                    // ordering can hand it any of them on its next periodic re-search, and the
                    // camera cuts to wherever that one stopped, 1,600 m away.
                    environment.Baseline.Car.IsRacing = false;
                    environment.Challenger.Car.IsRacing = false;

                    if (environment.Ghost != null)
                    {
                        environment.Ghost.Car.IsRacing = false;
                    }
                }
            }
            finally
            {
                foreach (var environment in environments)
                {
                    environment.Baseline?.Driver.EndRun();
                    environment.Challenger?.Driver.EndRun();
                    environment.Ghost?.Driver.EndRun();
                }

                if (arena != null)
                {
                    Destroy(arena);
                }

                Time.fixedDeltaTime = previousFixedDelta;
                Application.runInBackground = previousRunInBackground;
                Physics.simulationMode = previousSimulationMode;
                SkidMarks.GloballyEnabled = previousSkidMarks;
                if (Academy.IsInitialized)
                {
                    Academy.Instance.AutomaticSteppingEnabled = previousAutoStep;
                }
            }

            onComplete?.Invoke(BuildPayload(request, environments));
        }

        // ------------------------------------------------------------------
        // Per-step advance
        // ------------------------------------------------------------------

        static void BeginBatch(List<Environment> batch)
        {
            foreach (var environment in batch)
            {
                environment.BaselineContext.Reset();
                environment.ChallengerContext.Reset();
                environment.Baseline.Driver.BeginRun();
                environment.Challenger.Driver.BeginRun();

                if (environment.Ghost != null)
                {
                    environment.GhostContext.Reset();
                    environment.Ghost.Driver.BeginRun();
                    environment.GhostDone = false;
                }
            }
        }

        /// <summary>
        /// Steps every environment in the batch together, once per tick, until all of them are done
        /// or the shared timeout elapses. <paramref name="batch"/> is always a single circuit now —
        /// see the class comment — but the stepping itself does not need to know that.
        ///
        /// Paces one tick to one wall-clock <paramref name="dt"/> so a lap takes as long to watch as
        /// it would to drive. <c>Physics.Simulate</c> advances simulated time instantly and costs
        /// almost no real time to call, so without this a run would go exactly as fast as the editor
        /// can render frames — which in a light scene is a lot faster than the lap itself.
        /// </summary>
        IEnumerator StepBatch(List<Environment> batch, float dt, Action<float> onTick)
        {
            var maxSteps = Mathf.CeilToInt(RaceRules.BaselineTimeoutSeconds / dt);

            // An absolute schedule rather than "wait dt each tick": the latter accumulates the cost
            // of every tick's own work (however small) into drift over a multi-minute run, this does
            // not.
            var nextRealTime = Time.realtimeSinceStartup;

            for (var step = 0; step < maxSteps; step++)
            {
                var finished = 0;
                var anyDecision = false;

                foreach (var environment in batch)
                {
                    AdvanceCar(environment, isBaseline: true, dt, ref anyDecision);
                    AdvanceCar(environment, isBaseline: false, dt, ref anyDecision);
                    AdvanceGhost(environment, dt, ref anyDecision);

                    if (environment.Done)
                    {
                        finished++;
                    }
                }

                // One Academy step for every agent that asked, no matter how many are running.
                if (anyDecision)
                {
                    Academy.Instance.EnvironmentStep();
                }

                foreach (var environment in batch)
                {
                    if (environment.BaselineResult == null)
                    {
                        environment.Baseline.Car.Step(dt);
                    }

                    if (environment.ChallengerResult == null)
                    {
                        environment.Challenger.Car.Step(dt);
                    }

                    if (environment.Ghost != null && !environment.GhostDone)
                    {
                        environment.Ghost.Car.Step(dt);
                    }
                }

                Physics.Simulate(dt);

                if (finished >= batch.Count)
                {
                    yield break;
                }

                nextRealTime += dt;
                onTick?.Invoke(step * dt);

                while (Time.realtimeSinceStartup < nextRealTime)
                {
                    yield return null;
                }
            }
        }

        /// <summary>Anything still running when the clock ran out timed out.</summary>
        static void FillTimeouts(List<Environment> batch)
        {
            foreach (var environment in batch)
            {
                environment.BaselineResult ??= Timeout(environment, environment.BaselineContext, "BaselineBot");
                environment.ChallengerResult ??= Timeout(environment, environment.ChallengerContext, "Agent");
            }
        }

        void AdvanceCar(Environment environment, bool isBaseline, float dt, ref bool anyDecision)
        {
            var result = isBaseline ? environment.BaselineResult : environment.ChallengerResult;
            if (result != null)
            {
                return;
            }

            var context = isBaseline ? environment.BaselineContext : environment.ChallengerContext;
            var rig = isBaseline ? environment.Baseline : environment.Challenger;

            context.Refresh(dt);

            if (context.LapCompletedThisTick)
            {
                Finish(environment, isBaseline, RunStatus.Finished, context, rig);
                return;
            }

            if (context.OffTrackDuration >= RaceRules.OffTrackDnfSeconds)
            {
                Finish(environment, isBaseline, RunStatus.DidNotFinish, context, rig);
                return;
            }

            // The agent's clock is capped at a multiple of the baseline's, but only once the
            // baseline has actually set a time on this circuit.
            if (!isBaseline &&
                environment.BaselineResult is { Status: RunStatus.Finished } baseline &&
                context.ElapsedTime > baseline.Time * RaceRules.TimeoutMultiplier)
            {
                Finish(environment, false, RunStatus.TimedOut, context, rig);
                return;
            }

            rig.Driver.Tick();
            anyDecision |= rig.Driver is RacerAgent;
        }

        /// <summary>
        /// Drives the ghost, if there is one.
        ///
        /// Nothing reads what it does, so it needs none of <see cref="AdvanceCar"/>'s machinery —
        /// no result, no status, no timeout against the baseline. It only needs enough of the
        /// context to know when its own lap is over, at which point it stops driving and sits where
        /// it finished. The ghost is always a policy, so it always asks for a decision.
        /// </summary>
        static void AdvanceGhost(Environment environment, float dt, ref bool anyDecision)
        {
            if (environment.Ghost == null || environment.GhostDone)
            {
                return;
            }

            var context = environment.GhostContext;
            context.Refresh(dt);

            if (context.LapCompletedThisTick || context.OffTrackDuration >= RaceRules.OffTrackDnfSeconds)
            {
                environment.GhostDone = true;
                environment.Ghost.Car.SetInput(0f, 0f);
                return;
            }

            environment.Ghost.Driver.Tick();
            anyDecision = true;
        }

        static void Finish(
            Environment environment,
            bool isBaseline,
            RunStatus status,
            RaceContext context,
            RacerRig rig)
        {
            // Back off the part of the final step taken after the line, so the lap time is a
            // continuous function of where the car actually was rather than a multiple of 20 ms.
            var adjustment = status == RunStatus.Finished
                ? (1f - context.Checkpoints.LapCrossingFraction) * CarSpec.FixedDeltaTime
                : 0f;

            var result = new RunResult
            {
                Seed = environment.Seed,
                Driver = rig.Driver.DriverName,
                Status = status,
                Time = context.ElapsedTime - adjustment,
                CheckpointsPassed = context.Checkpoints.Passed,
                CheckpointCount = context.Checkpoints.Count,
                DistanceTraveled = context.Checkpoints.TraveledDistance,
            };

            if (isBaseline)
            {
                environment.BaselineResult = result;
            }
            else
            {
                environment.ChallengerResult = result;
            }

            rig.Car.SetInput(0f, 0f);
        }

        static RunResult Timeout(Environment environment, RaceContext context, string driver)
        {
            return new RunResult
            {
                Seed = environment.Seed,
                Driver = driver,
                Status = RunStatus.TimedOut,
                Time = context.ElapsedTime,
                CheckpointsPassed = context.Checkpoints.Passed,
                CheckpointCount = context.Checkpoints.Count,
                DistanceTraveled = context.Checkpoints.TraveledDistance,
            };
        }

        // ------------------------------------------------------------------
        // Setup
        // ------------------------------------------------------------------

        static Vector3 GridPosition(int index)
        {
            const int columns = 4;
            return new Vector3(
                index % columns * k_EnvironmentSpacing,
                0f,
                index / columns * k_EnvironmentSpacing);
        }

        Environment BuildEnvironment(Request request, int seed, Vector3 origin, Transform parent)
        {
            var root = new GameObject($"Env_{seed}");
            root.transform.SetParent(parent, false);
            root.transform.position = origin;

            var trackObject = new GameObject("Circuit");
            trackObject.transform.SetParent(root.transform, false);

            var track = trackObject.AddComponent<TrackInstance>();
            track.Seed = seed;

            // Evaluation now races the same hazard-eligible generator training does, so the
            // published seed set (eval_seeds.json) includes SharpHairpin/ObstacleStraight/RampCorner
            // circuits alongside plain ones — a policy that has only ever seen clean corners in
            // training would otherwise meet its first hazard on the scored run itself.
            track.EnableHazardSections = true;

            CopyMaterials(track, request.Materials);
            CopyProps(track, request.Props);
            track.Rebuild();

            var baseline = RacerBuilder.BuildBaseline(request.CarPrefab, root.transform);
            var challenger = RacerBuilder.BuildAgent(
                request.CarPrefab,
                request.AgentPrefab,
                request.Model,
                manualStepping: true,
                parent: root.transform);

            if (baseline == null || challenger == null)
            {
                return null;
            }

            var environment = new Environment
            {
                Seed = seed,
                Root = root,
                Baseline = baseline,
                Challenger = challenger,
                Ghost = BuildGhost(request, root.transform),
            };

            if (environment.Ghost != null)
            {
                environment.Ghost.Root.name = "GhostCar";
                environment.Ghost.MakeGhost();

                // The baseline keeps running — the score is measured against its time — but the
                // ghost slot is taken, and three near-identical cars on one circuit read as a mess
                // rather than as a comparison.
                baseline.Hide();
            }
            else if (request.Ghost.Mode == GhostMode.BaselineBot)
            {
                baseline.MakeGhost();
            }
            // else GhostMode.None: the baseline races in plain view, same as the challenger.

            environment.BaselineContext = baseline.PlaceOnTrack(track.Model);
            environment.ChallengerContext = challenger.PlaceOnTrack(track.Model);

            IgnoreCollisionsBetween(baseline.Root, challenger.Root);

            if (environment.Ghost != null)
            {
                environment.GhostContext = environment.Ghost.PlaceOnTrack(track.Model);
                IgnoreCollisionsBetween(environment.Ghost.Root, baseline.Root);
                IgnoreCollisionsBetween(environment.Ghost.Root, challenger.Root);
            }

            return environment;
        }

        /// <summary>The competitor's own past model, in a car of its own, when they asked for
        /// one — see <see cref="GhostMode.Model"/>.</summary>
        static RacerRig BuildGhost(Request request, Transform parent)
        {
            if (request.Ghost is not { NeedsOwnCar: true })
            {
                return null;
            }

            // Their past model was trained against some version of their agent. Usually the current
            // one, hence the fallback; the override is for when the sensors have moved on since.
            var prefab = request.Ghost.AgentPrefab != null ? request.Ghost.AgentPrefab : request.AgentPrefab;

            var ghost = RacerBuilder.BuildAgent(
                request.CarPrefab,
                prefab,
                request.Ghost.Model,
                manualStepping: true,
                parent: parent);

            if (ghost == null)
            {
                Debug.LogWarning(
                    "[RacingBotCup] The ghost car could not be built, so the baseline bot is shown " +
                    "instead. Check that the ghost's agent prefab matches the model it is running.");
            }

            return ghost;
        }

        static void CopyMaterials(TrackInstance track, TrackMaterials materials)
        {
            if (materials == null)
            {
                return;
            }

            track.Materials.Road = materials.Road;
            track.Materials.Runoff = materials.Runoff;
            track.Materials.Ground = materials.Ground;
        }

        /// <summary>Feeds the same barrier/crate/log/container/ramp prefabs training uses to whatever
        /// hazard sections <see cref="BuildEnvironment"/>'s seed rolls — without these a RampCorner or
        /// ObstacleStraight would still shape the centreline but have nothing physical placed on it.</summary>
        static void CopyProps(TrackInstance track, TrackPropCatalogue props)
        {
            if (props == null)
            {
                return;
            }

            track.Props.Barriers = props.Barriers;
            track.Props.Crates = props.Crates;
            track.Props.Logs = props.Logs;
            track.Props.Containers = props.Containers;
            track.Props.Ramp = props.Ramp;
        }

        /// <summary>
        /// The baseline shares the circuit with the competitor's car, so they must pass through one
        /// another. Without this the ghost would be a rolling roadblock.
        ///
        /// Public because the finals need exactly the same thing, six cars at a time — see
        /// <see cref="Finals.FinalsDirector"/>.
        /// </summary>
        public static void IgnoreCollisionsBetween(GameObject a, GameObject b)
        {
            var first = a.GetComponentsInChildren<Collider>(true);
            var second = b.GetComponentsInChildren<Collider>(true);

            foreach (var left in first)
            {
                foreach (var right in second)
                {
                    Physics.IgnoreCollision(left, right, true);
                }
            }
        }

        static void Settle(List<Environment> environments, float dt)
        {
            var steps = Mathf.CeilToInt(k_SettleSeconds / dt);
            for (var i = 0; i < steps; i++)
            {
                foreach (var environment in environments)
                {
                    environment.Baseline.Car.SetInput(0f, 0f);
                    environment.Challenger.Car.SetInput(0f, 0f);
                    environment.Baseline.Car.Step(dt);
                    environment.Challenger.Car.Step(dt);

                    if (environment.Ghost != null)
                    {
                        environment.Ghost.Car.SetInput(0f, 0f);
                        environment.Ghost.Car.Step(dt);
                    }
                }

                Physics.Simulate(dt);
            }
        }

        // ------------------------------------------------------------------
        // Scoring
        // ------------------------------------------------------------------

        SubmissionPayload BuildPayload(Request request, List<Environment> environments)
        {
            var tracks = new List<TrackScore>(environments.Count);
            foreach (var environment in environments)
            {
                if (environment.BaselineResult is not { Status: RunStatus.Finished })
                {
                    Debug.LogError(
                        $"[RacingBotCup] The baseline bot failed on seed {environment.Seed} " +
                        $"({environment.BaselineResult?.Status}). This seed cannot be scored and " +
                        "should be replaced in eval_seeds.json.");
                }

                tracks.Add(ScoreAggregator.BuildTrackScore(
                    environment.Seed, environment.BaselineResult, environment.ChallengerResult));
            }

            var payload = new SubmissionPayload
            {
                ParticipantId = string.IsNullOrWhiteSpace(request.ParticipantId)
                    ? "unknown"
                    : request.ParticipantId.Trim(),
                SubmittedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                SeedSetId = request.Seeds.Id,
                Seeds = request.Seeds.Seeds,
                Note = request.Note,
                Score = ScoreAggregator.Aggregate(tracks),
                Integrity = new IntegrityBlock
                {
                    CarSpecHash = CarSpec.SpecHash,
                    TrackGeneratorVersion = TrackGenerator.Version,
                    ScoreModuleVersion = ScoreAggregator.Version,
                    BaselineBotVersion = BaselineBot.Version,
                    RulesHash = RaceRules.Hash,
                    AgentHash = AgentFingerprint(request.AgentPrefab, request.Model),
                },
            };

            SubmissionCodec.Sign(payload);
            return payload;
        }

        /// <summary>
        /// Identifies the entry. With sensors now free-form there is no config to hash, so this is
        /// the prefab and model that produced the score.
        /// </summary>
        static string AgentFingerprint(GameObject agentPrefab, ModelAsset model)
        {
            var prefabName = agentPrefab == null ? "none" : agentPrefab.name;
            var modelName = model == null ? "heuristic" : model.name;
            return $"{prefabName}/{modelName}";
        }
    }
}
