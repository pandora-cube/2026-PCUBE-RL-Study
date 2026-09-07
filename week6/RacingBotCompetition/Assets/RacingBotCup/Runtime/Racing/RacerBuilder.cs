using RacingBotCup.Agent;
using RacingBotCup.Eval;
using RacingBotCup.Track;
using RacingBotCup.Vehicle;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace RacingBotCup.Racing
{
    /// <summary>A car plus the driver bolted into it, ready to be pointed at a circuit.</summary>
    public sealed class RacerRig
    {
        /// <summary>Where an idle rig waits while another one is being timed.</summary>
        static readonly Vector3 k_ParkingPosition = new Vector3(0f, -2000f, 0f);

        public CarController Car { get; }

        public IDriver Driver { get; }

        public GameObject Root => Car.gameObject;

        public RacerRig(CarController car, IDriver driver)
        {
            Car = car;
            Driver = driver;
        }

        /// <summary>Moves the rig out of the way and freezes it.</summary>
        public void Park()
        {
            Car.ResetTo(k_ParkingPosition, Quaternion.identity);
            Car.Body.isKinematic = true;
            Car.IsRacing = false;
        }

        /// <summary>
        /// Puts the car on the start line. The model already carries its world position — parallel
        /// environments rebase the whole circuit rather than offsetting every query.
        /// </summary>
        public RaceContext PlaceOnTrack(TrackModel model)
        {
            Car.Body.isKinematic = false;
            Car.SurfaceProvider = model;
            Car.IsRacing = true;

            var startPose = model.GetStartPose(RaceRules.StartHeightOffset);
            Car.ResetTo(startPose.position, startPose.rotation);

            var checkpoints = new CheckpointRing(
                model,
                RaceRules.CheckpointCount,
                RaceRules.CheckpointLateralTolerance);

            var context = new RaceContext(Car, model, checkpoints);
            Driver.Bind(context);

            // Gives the HUD something to read. Added here rather than baked into the car prefab so
            // that a car which is not on a circuit has no clock to show.
            var clock = Car.GetComponent<RaceClock>() ?? Car.gameObject.AddComponent<RaceClock>();
            clock.Bind(context);

            return context;
        }

        /// <summary>
        /// Stops the car being drawn without taking it off the circuit — it still laps, and its
        /// result is still recorded. The baseline needs this when something else is occupying the
        /// ghost slot: its time is what the score is measured against, so it has to run either way,
        /// but there is only room on screen for one reference car.
        /// </summary>
        public void Hide()
        {
            foreach (var renderer in Root.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = false;
            }
        }

        /// <summary>
        /// Renders the car as a translucent ghost, and tags it as one. Used for whichever car shares
        /// the circuit with the competitor's as a reference, so the two can be compared at a glance.
        /// </summary>
        public void MakeGhost(float alpha = 0.35f)
        {
            // The tag, not the transparency, is what keeps the camera and the HUD off it — a ghost
            // running a policy looks exactly like the competitor's own car to both of them.
            if (Root.GetComponent<GhostCar>() == null)
            {
                Root.AddComponent<GhostCar>();
            }

            foreach (var renderer in Root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.materials;
                for (var i = 0; i < materials.Length; i++)
                {
                    materials[i] = MakeTransparent(materials[i], alpha);
                }

                renderer.materials = materials;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        /// <summary>
        /// Repaints the car in one racer's colour, so a field of six identical cars can be told
        /// apart at a glance — the tag over the car and its row on the standings board are drawn in
        /// the same colour. Cosmetic only: reading <c>renderer.materials</c> instantiates this car's
        /// own copies, so no other car changes colour with it, and nothing physical is touched.
        /// </summary>
        public void Tint(Color colour)
        {
            foreach (var renderer in Root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.materials)
                {
                    // URP's Lit uses _BaseColor; the older built-in property is there for anything
                    // in the pack still on a legacy shader.
                    if (material.HasProperty("_BaseColor"))
                    {
                        material.SetColor("_BaseColor", colour);
                    }

                    if (material.HasProperty("_Color"))
                    {
                        material.SetColor("_Color", colour);
                    }
                }
            }
        }

        static Material MakeTransparent(Material source, float alpha)
        {
            var copy = new Material(source);

            // URP's Lit shader switches to the transparent pass through these four properties
            // together; setting the colour alone leaves it rendering fully opaque.
            copy.SetFloat("_Surface", 1f);
            copy.SetFloat("_Blend", 0f);
            copy.SetFloat("_ZWrite", 0f);
            copy.SetOverrideTag("RenderType", "Transparent");
            copy.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            copy.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            copy.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            copy.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            copy.DisableKeyword("_ALPHATEST_ON");

            foreach (var property in new[] { "_BaseColor", "_Color" })
            {
                if (copy.HasProperty(property))
                {
                    var colour = copy.GetColor(property);
                    colour.a = alpha;
                    copy.SetColor(property, colour);
                }
            }

            return copy;
        }
    }

    /// <summary>
    /// Assembles racers from prefabs.
    ///
    /// The car comes from a baked prefab so competitors can see and inspect it, and the agent is a
    /// separate prefab they own — their sensors, their observation code, their BehaviorParameters.
    /// Attaching the agent as a child of the sealed car keeps the physics identical for everyone
    /// while leaving the sensor rig completely open.
    ///
    /// ML-Agents pieces are wired while the GameObject is deactivated on purpose:
    /// <c>Agent.OnEnable</c> snapshots its sensors and brain parameters once, and a component on a
    /// live GameObject enables immediately.
    /// </summary>
    public static class RacerBuilder
    {
        public static RacerRig BuildBaseline(GameObject carPrefab, Transform parent = null)
        {
            var car = InstantiateCar(carPrefab, parent, "BaselineCar");
            if (car == null)
            {
                return null;
            }

            var bot = car.gameObject.AddComponent<BaselineBot>();
            return new RacerRig(car, bot);
        }

        /// <param name="agentPrefab">
        /// The competitor's agent: a <see cref="RacerAgent"/> subclass with BehaviorParameters and
        /// whatever sensors they chose. Instantiated as a child of the car at its origin.
        /// </param>
        /// <param name="fallbackBehavior">
        /// What to do when no model is supplied. Evaluation wants <c>HeuristicOnly</c> so the scene
        /// stays drivable by hand; training must use <c>Default</c>, which is what connects the
        /// agent to the Python trainer.
        /// </param>
        public static RacerRig BuildAgent(
            GameObject carPrefab,
            GameObject agentPrefab,
            ModelAsset model,
            bool manualStepping = true,
            Transform parent = null,
            BehaviorType fallbackBehavior = BehaviorType.HeuristicOnly)
        {
            var car = InstantiateCar(carPrefab, parent, "AgentCar");
            if (car == null)
            {
                return null;
            }

            if (agentPrefab == null)
            {
                Debug.LogError("[RacingBotCup] No agent prefab supplied. Assign one built from RacerAgent.");
                return null;
            }

            var agentObject = Object.Instantiate(agentPrefab, car.transform);
            agentObject.name = "Agent";
            agentObject.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            agentObject.SetActive(false);

            var agent = agentObject.GetComponent<RacerAgent>();
            if (agent == null)
            {
                Debug.LogError($"[RacingBotCup] '{agentPrefab.name}' has no RacerAgent component on its root.");
                Object.Destroy(car.gameObject);
                return null;
            }

            var behavior = agentObject.GetComponent<BehaviorParameters>()
                           ?? agentObject.AddComponent<BehaviorParameters>();
            behavior.DeterministicInference = true;

            if (model != null)
            {
                behavior.Model = model;
                behavior.BehaviorType = BehaviorType.InferenceOnly;
            }
            else
            {
                behavior.BehaviorType = fallbackBehavior;
            }

            agent.Configure(manualStepping);

            var layer = RacingLayers.VehicleLayer;
            if (layer >= 0)
            {
                foreach (var child in agentObject.GetComponentsInChildren<Transform>(true))
                {
                    child.gameObject.layer = layer;
                }
            }

            agentObject.SetActive(true);
            return new RacerRig(car, agent);
        }

        static CarController InstantiateCar(GameObject carPrefab, Transform parent, string name)
        {
            if (carPrefab == null)
            {
                Debug.LogError("[RacingBotCup] No car prefab supplied. Run RacingBotCup > Rebuild Prefabs.");
                return null;
            }

            // A prefab that already carries the rig — the baked RaceCar — is used as it is. Anything
            // else is taken for vehicle art and gets a rig assembled around it by the same factory
            // that baked RaceCar in the first place, so a different-looking car costs nothing but
            // dragging one of the pack's presets into the field. See CarFactory for what that does
            // and does not keep identical.
            if (carPrefab.GetComponent<CarController>() == null)
            {
                var built = CarFactory.Build(carPrefab, parent, name);

                // The baked prefab carries one; without it a car on a different preset would leave
                // no tyre marks and read as a different simulation rather than a different shape.
                if (built != null && built.GetComponent<SkidMarks>() == null)
                {
                    built.gameObject.AddComponent<SkidMarks>();
                }

                return built;
            }

            var instance = Object.Instantiate(carPrefab, parent);
            instance.name = name;

            // A prefab saved out of a scene keeps whatever pose it was sitting at, and that pose is
            // not as harmless as it looks: the liveries in Prefabs/ used to disagree about it (some
            // at the origin, some carrying an offset and a few degrees of yaw), and cars identical
            // in every physics value down to the bit came out reliably 6-12 ms apart over a 49 s lap
            // purely by which side of that split they fell on. PlaceOnTrack overwrites the pose long
            // before the lights go out, so it cannot move a start line — it just seeds the float
            // arithmetic differently.
            //
            // Resetting here is only half the cure and is kept as a backstop: Awake, and with it the
            // WheelCollider registration, has already run by this point, so a prefab that ships with
            // a pose still races a hair differently. The prefabs themselves are stored at the origin,
            // which is what actually makes them interchangeable — keep any new car that way.
            instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            return instance.GetComponent<CarController>();
        }
    }
}
