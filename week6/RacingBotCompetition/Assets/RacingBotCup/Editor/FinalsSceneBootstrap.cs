using System.IO;
using RacingBotCup.Finals;
using RacingBotCup.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// Builds the finals scene (기획서 §8) from code, the same way the training and evaluation
    /// scenes are built — one menu item away from being restored if it is edited into a corner.
    ///
    /// What comes out is a scene with nothing to wire: the director already has the car prefab, the
    /// circuit materials and props, three seeds and the overlay. The organisers fill in the roster —
    /// a name, a prefab and an .onnx per racer — and press Play.
    /// </summary>
    public static class FinalsSceneBootstrap
    {
        public const string FinalsScenePath = SceneBootstrap.SceneDirectory + "/Finals.unity";

        /// <summary>
        /// Circuits nobody has practised on. Deliberately outside the 0–100,000 band the guide hands
        /// out for practice, and nowhere near the published evaluation set — the finals are the one
        /// run that a policy cannot have been fitted to (기획서 §7).
        ///
        /// All three pass the generator's own validation outright (<c>GeneratedTrack.FullyValid</c>),
        /// and they are picked short/medium/long — 976 m, 1,535 m and 1,915 m — so the event is not
        /// three variations on the same lap. Replace them with anything, but check the console: a
        /// seed the generator only half-satisfies warns, and it does so because it left a corner
        /// tighter than the rules intend.
        /// </summary>
        static readonly int[] k_FinalsSeeds = { 120014, 120005, 120015 };

        [MenuItem("RacingBotCup/Build Finals Scene", priority = 1)]
        public static void BuildFromMenu()
        {
            Build();
            EditorUtility.DisplayDialog("RacingBot Cup", $"Created:\n{FinalsScenePath}", "OK");
        }

        public static void Build()
        {
            Directory.CreateDirectory(SceneBootstrap.SceneDirectory);

            // Deliberately does not bake the prefabs itself. RebuildPrefabs writes *both*, and the
            // agent prefab is hand-tuned and committed — baking it back to code defaults to get hold
            // of a missing car would quietly throw away somebody's sensor rig.
            if (PrefabBaker.LoadCarPrefab() == null)
            {
                Debug.LogWarning(
                    "[RacingBotCup] No RaceCar prefab yet — run RacingBotCup > Rebuild Prefabs, then " +
                    "build this scene again. It is being created without a car for now.");
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneBootstrap.AddEnvironment();

            var host = new GameObject("FinalsDirector");
            var director = host.AddComponent<FinalsDirector>();

            var serialized = new SerializedObject(director);
            serialized.FindProperty("m_CarPrefab").objectReferenceValue = PrefabBaker.LoadCarPrefab();
            SceneBootstrap.AssignMaterials(serialized);
            SceneBootstrap.AssignProps(serialized);

            var seeds = serialized.FindProperty("m_Seeds");
            seeds.arraySize = k_FinalsSeeds.Length;
            for (var i = 0; i < k_FinalsSeeds.Length; i++)
            {
                seeds.GetArrayElementAtIndex(i).intValue = k_FinalsSeeds[i];
            }

            FillSampleRoster(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var hud = new GameObject("FinalsHud");
            var overlay = hud.AddComponent<FinalsHud>();

            var hudSerialized = new SerializedObject(overlay);
            hudSerialized.FindProperty("m_Director").objectReferenceValue = director;
            hudSerialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, FinalsScenePath);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Two entries on the sample agent with no model, so the scene runs the moment it is opened
        /// and the overlay has something to show. Both are driven by the keyboard until an .onnx is
        /// dropped in — which makes it a working smoke test, not a demo of anything.
        /// </summary>
        static void FillSampleRoster(SerializedObject serialized)
        {
            var agentPrefab = PrefabBaker.LoadAgentPrefab();
            var entries = serialized.FindProperty("m_Entries");
            entries.arraySize = 2;

            for (var i = 0; i < entries.arraySize; i++)
            {
                var entry = entries.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("Name").stringValue = $"Racer {i + 1}";
                entry.FindPropertyRelative("AgentPrefab").objectReferenceValue = agentPrefab;
                entry.FindPropertyRelative("Model").objectReferenceValue = null;
            }
        }
    }
}
