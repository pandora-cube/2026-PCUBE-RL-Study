using System.IO;
using RacingBotCup.Agent;
using RacingBotCup.Eval;
using RacingBotCup.Track;
using RacingBotCup.UI;
using RacingBotCup.Vehicle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Builds the training and evaluation scenes from code.
    ///
    /// Both scenes come out ready to use: the training scene has a circuit and a car already on the
    /// start line, and the evaluation scene has one component with everything wired but the two
    /// fields a competitor fills in. Generating them from a menu means the setup is readable,
    /// diffable, and one click away from being restored.
    /// </summary>
    public static class SceneBootstrap
    {
        public const string SceneDirectory = "Assets/RacingBotCup/Scenes";
        public const string TrainingScenePath = SceneDirectory + "/Training.unity";
        public const string EvaluationScenePath = SceneDirectory + "/Evaluation.unity";

        const string k_ConfigDirectory = "Assets/RacingBotCup/Config";
        const string k_SeedSetPath = k_ConfigDirectory + "/eval_seeds.json";
        const string k_SubmissionConfigPath = k_ConfigDirectory + "/SubmissionConfig.asset";

        const string k_MaterialFolder = "Assets/PolygonStreetRacer/Materials/Roads/";

        // Picked by sampling the textures: Mat_03 is the darkest asphalt. The run-off takes its sand
        // colour from the swatch strip, which is the same in all of them. Grass is not in the pack,
        // so it is generated — see GeneratedMaterials.
        const string k_RoadMaterialPath = k_MaterialFolder + "PolygonStreetRacer_Road_Mat_03.mat";
        const string k_RunoffMaterialPath = k_MaterialFolder + "PolygonStreetRacer_Road_Mat_01.mat";

        const string k_PropFolder = "Assets/PolygonStreetRacer/Prefabs/Props/";

        static readonly string[] k_BarrierPrefabPaths =
        {
            k_PropFolder + "SM_Prop_Barrier_Concrete_01.prefab",
            k_PropFolder + "SM_Prop_Barrier_Concrete_02.prefab",
            k_PropFolder + "SM_Prop_Barrier_Concrete_03.prefab",
            k_PropFolder + "SM_Prop_Barrier_Concrete_04.prefab",
        };

        static readonly string[] k_CratePrefabPaths =
        {
            k_PropFolder + "SM_Prop_Crate_01.prefab",
            k_PropFolder + "SM_Prop_Crate_02.prefab",
        };

        static readonly string[] k_LogPrefabPaths =
        {
            k_PropFolder + "SM_Prop_Log_Single_01.prefab",
            k_PropFolder + "SM_Prop_Log_Single_02.prefab",
            k_PropFolder + "SM_Prop_Log_Single_03.prefab",
            k_PropFolder + "SM_Prop_Log_Single_04.prefab",
            k_PropFolder + "SM_Prop_Log_Single_05.prefab",
            k_PropFolder + "SM_Prop_Log_Single_06.prefab",
        };

        static readonly string[] k_ContainerPrefabPaths =
        {
            k_PropFolder + "SM_Prop_Container_Small_01.prefab",
            k_PropFolder + "SM_Prop_Container_Small_Doors_01.prefab",
            k_PropFolder + "SM_Prop_Container_Small_Stack_01.prefab",
            k_PropFolder + "SM_Prop_Container_Small_Stack_02.prefab",
        };

        const string k_RampPrefabPath = k_PropFolder + "SM_Prop_Ramp_Mesh_01.prefab";

        /// <summary>Circuit the training scene opens on. Any practice seed does.</summary>
        const int k_DefaultTrainingSeed = 4242;

        /// <summary>
        /// How many training areas run side by side. ML-Agents batches experience from every agent
        /// that shares a behaviour name, so replicating the whole environment — track, car and
        /// arena, not just the agent — is what actually parallelises training inside a single
        /// Editor session: four cars collecting steps every physics tick instead of one.
        /// </summary>
        const int k_ParallelEnvironments = 4;

        /// <summary>
        /// Distance between environments. Matches the spacing evaluation uses between its circuits —
        /// comfortably wider than the largest circuit a random seed can produce, so two training
        /// areas never overlap even as each one randomises independently every episode.
        /// </summary>
        const float k_EnvironmentSpacing = 1600f;

        [MenuItem("RacingBotCup/Build Scenes", priority = 0)]
        public static void BuildScenesFromMenu()
        {
            BuildScenes();
            EditorUtility.DisplayDialog(
                "RacingBot Cup",
                $"Created:\n{TrainingScenePath}\n{EvaluationScenePath}",
                "OK");
        }

        /// <summary>Rebuilds both scenes. Separate from the menu entry so automation can call it
        /// without a modal dialog blocking the editor.</summary>
        public static void BuildScenes()
        {
            Directory.CreateDirectory(SceneDirectory);
            Directory.CreateDirectory(k_ConfigDirectory);

            if (PrefabBaker.LoadCarPrefab() == null)
            {
                PrefabBaker.RebuildPrefabs();
            }

            EnsureSubmissionConfig();
            BuildTrainingScene();
            BuildEvaluationScene();

            AssetDatabase.Refresh();
        }

        static void BuildTrainingScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddEnvironment();

            for (var i = 0; i < k_ParallelEnvironments; i++)
            {
                BuildTrainingEnvironment(i);
            }

            EditorSceneManager.SaveScene(scene, TrainingScenePath);
        }

        /// <summary>
        /// One self-contained training area: its own circuit, car, agent and arena, sitting far
        /// enough from the others that none of their geometry overlaps. Each starts on its own seed
        /// purely so the scene reads as four distinct environments the moment it opens — every arena
        /// still rerolls its own track independently once training starts.
        /// </summary>
        static void BuildTrainingEnvironment(int index)
        {
            var root = new GameObject($"Environment_{index}");
            root.transform.position = GridPosition(index);

            var track = CreateTrack(k_DefaultTrainingSeed + index, root.transform);
            var car = CreateCar(track, root.transform);

            var arenaObject = new GameObject("TrainingArena");
            arenaObject.transform.SetParent(root.transform, false);
            var component = arenaObject.AddComponent<TrainingArena>();

            var serialized = new SerializedObject(component);
            serialized.FindProperty("m_Track").objectReferenceValue = track;
            serialized.FindProperty("m_Car").objectReferenceValue = car;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static Vector3 GridPosition(int index)
        {
            var columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(k_ParallelEnvironments)));
            return new Vector3(
                index % columns * k_EnvironmentSpacing,
                0f,
                index / columns * k_EnvironmentSpacing);
        }

        static void BuildEvaluationScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddEnvironment();

            var host = new GameObject("RaceEvaluator");
            var evaluator = host.AddComponent<RaceEvaluator>();

            var serialized = new SerializedObject(evaluator);
            serialized.FindProperty("m_CarPrefab").objectReferenceValue = PrefabBaker.LoadCarPrefab();
            serialized.FindProperty("m_AgentPrefab").objectReferenceValue = PrefabBaker.LoadAgentPrefab();
            serialized.FindProperty("m_SeedSetAsset").objectReferenceValue = LoadAsset<TextAsset>(k_SeedSetPath);
            serialized.FindProperty("m_SubmissionConfig").objectReferenceValue =
                LoadAsset<SubmissionConfig>(k_SubmissionConfigPath);
            serialized.FindProperty("m_RunOnStart").boolValue = true;
            AssignMaterials(serialized);
            AssignProps(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, EvaluationScenePath);
        }

        /// <summary>Creates a circuit — parented, so a training area can be offset as one unit — and
        /// bakes its geometry into the scene.</summary>
        static TrackInstance CreateTrack(int seed, Transform parent)
        {
            var trackObject = new GameObject("Circuit");
            trackObject.transform.SetParent(parent, false);
            var track = trackObject.AddComponent<TrackInstance>();

            var serialized = new SerializedObject(track);
            serialized.FindProperty("m_Seed").intValue = seed;
            AssignMaterials(serialized);
            AssignProps(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            track.Rebuild();
            return track;
        }

        /// <summary>Drops the car on the start line with the sample agent already attached.</summary>
        static CarController CreateCar(TrackInstance track, Transform parent)
        {
            var carPrefab = PrefabBaker.LoadCarPrefab();
            var agentPrefab = PrefabBaker.LoadAgentPrefab();

            if (carPrefab == null || agentPrefab == null)
            {
                Debug.LogError("[RacingBotCup] Prefabs are missing. Run RacingBotCup > Rebuild Prefabs.");
                return null;
            }

            var carObject = (GameObject)PrefabUtility.InstantiatePrefab(carPrefab);
            carObject.name = "RaceCar";
            carObject.transform.SetParent(parent, false);

            var agentObject = (GameObject)PrefabUtility.InstantiatePrefab(agentPrefab);
            agentObject.name = "Agent";
            agentObject.transform.SetParent(carObject.transform, false);

            // The track's own model was already rebased to its (offset) transform, so this is a
            // world-space pose that lands the car correctly no matter which environment it is in.
            var pose = track.Model.GetStartPose(RaceRules.StartHeightOffset);
            carObject.transform.SetPositionAndRotation(pose.position, pose.rotation);

            return carObject.GetComponent<CarController>();
        }

        /// <summary>Light, camera, chase view and HUD — shared with the finals scene.</summary>
        internal static void AddEnvironment()
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(48f, 140f, 0f);

            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.farClipPlane = 3000f;
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(0f, 40f, -60f),
                Quaternion.Euler(25f, 0f, 0f));

            // Following the car is the default: watching a lap from a fixed overhead camera means
            // watching a few pixels move.
            cameraObject.AddComponent<ChaseCamera>();
            cameraObject.AddComponent<RaceHud>();
        }

        static void EnsureSubmissionConfig()
        {
            if (AssetDatabase.LoadAssetAtPath<SubmissionConfig>(k_SubmissionConfigPath) != null)
            {
                return;
            }

            var config = ScriptableObject.CreateInstance<SubmissionConfig>();
            AssetDatabase.CreateAsset(config, k_SubmissionConfigPath);
        }

        internal static void AssignMaterials(SerializedObject serialized)
        {
            var materials = serialized.FindProperty("m_Materials");
            if (materials == null)
            {
                return;
            }

            materials.FindPropertyRelative("Road").objectReferenceValue =
                LoadAsset<Material>(k_RoadMaterialPath);
            materials.FindPropertyRelative("Runoff").objectReferenceValue =
                LoadAsset<Material>(k_RunoffMaterialPath);
            materials.FindPropertyRelative("Ground").objectReferenceValue =
                GeneratedMaterials.LoadOrCreateGrass();
        }

        /// <summary>Loads the circuit materials for tools that build a track outside a scene.</summary>
        public static TrackMaterials LoadMaterials()
        {
            return new TrackMaterials
            {
                Road = LoadAsset<Material>(k_RoadMaterialPath),
                Runoff = LoadAsset<Material>(k_RunoffMaterialPath),
                Ground = GeneratedMaterials.LoadOrCreateGrass(),
            };
        }

        internal static void AssignProps(SerializedObject serialized)
        {
            var props = serialized.FindProperty("m_Props");
            if (props == null)
            {
                return;
            }

            AssignPrefabArray(props.FindPropertyRelative("Barriers"), k_BarrierPrefabPaths);
            AssignPrefabArray(props.FindPropertyRelative("Crates"), k_CratePrefabPaths);
            AssignPrefabArray(props.FindPropertyRelative("Logs"), k_LogPrefabPaths);
            AssignPrefabArray(props.FindPropertyRelative("Containers"), k_ContainerPrefabPaths);
            props.FindPropertyRelative("Ramp").objectReferenceValue = LoadAsset<GameObject>(k_RampPrefabPath);
        }

        static void AssignPrefabArray(SerializedProperty arrayProperty, string[] paths)
        {
            if (arrayProperty == null)
            {
                return;
            }

            arrayProperty.arraySize = paths.Length;
            for (var i = 0; i < paths.Length; i++)
            {
                arrayProperty.GetArrayElementAtIndex(i).objectReferenceValue = LoadAsset<GameObject>(paths[i]);
            }
        }

        /// <summary>Loads the prop catalogue for tools that build a track outside a scene.</summary>
        public static TrackPropCatalogue LoadProps()
        {
            return new TrackPropCatalogue
            {
                Barriers = LoadAssets<GameObject>(k_BarrierPrefabPaths),
                Crates = LoadAssets<GameObject>(k_CratePrefabPaths),
                Logs = LoadAssets<GameObject>(k_LogPrefabPaths),
                Containers = LoadAssets<GameObject>(k_ContainerPrefabPaths),
                Ramp = LoadAsset<GameObject>(k_RampPrefabPath),
            };
        }

        static T[] LoadAssets<T>(string[] paths) where T : Object
        {
            var results = new T[paths.Length];
            for (var i = 0; i < paths.Length; i++)
            {
                results[i] = LoadAsset<T>(paths[i]);
            }

            return results;
        }

        static T LoadAsset<T>(string path) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                Debug.LogWarning($"[RacingBotCup] Missing asset at {path}; the scene will fall back to defaults.");
            }

            return asset;
        }
    }
}
