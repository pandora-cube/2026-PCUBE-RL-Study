using System.Collections.Generic;
using RacingBotCup.Finals;
using RacingBotCup.Track;
using UnityEditor;
using UnityEngine;

namespace RacingBotCup.EditorTools
{
    /// <summary>
    /// The finals operator's panel. Everything about a finals is set in the default Inspector; this
    /// adds the one thing that is tedious by hand — drawing the circuits.
    /// </summary>
    [CustomEditor(typeof(FinalsDirector))]
    public sealed class FinalsDirectorEditor : Editor
    {
        /// <summary>The band finals seeds are drawn from — clear of the 0-99,999 practice range and
        /// of the published evaluation set, so nobody has trained on what comes out.</summary>
        const int k_SeedMin = 120000;
        const int k_SeedMax = 120999;

        const int k_SeedCount = 3;

        /// <summary>
        /// Generations to spend looking before giving up. A seed costs a few milliseconds and about
        /// half of them validate outright, so this is orders of magnitude more than a draw needs —
        /// it exists so a bad band cannot hang the editor.
        /// </summary>
        const int k_MaxAttempts = 400;

        string m_Message;
        MessageType m_MessageType = MessageType.None;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            if (GUILayout.Button("시드 랜덤 선택", GUILayout.Height(30f)))
            {
                Draw((FinalsDirector)target);
            }

            EditorGUILayout.LabelField(
                $"{k_SeedMin}~{k_SeedMax} 대역에서 검증을 통과하는 트랙 {k_SeedCount}개 (장애물 코스 1개 이상 포함)",
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(m_Message))
            {
                EditorGUILayout.HelpBox(m_Message, m_MessageType);
            }
        }

        void Draw(FinalsDirector director)
        {
            if (!TryPickSeeds(out var picked, out var error))
            {
                m_Message = error;
                m_MessageType = MessageType.Warning;
                return;
            }

            serializedObject.Update();
            var seeds = serializedObject.FindProperty("m_Seeds");
            seeds.arraySize = picked.Count;
            for (var i = 0; i < picked.Count; i++)
            {
                seeds.GetArrayElementAtIndex(i).intValue = picked[i].Seed;
            }

            // Goes through serializedObject rather than the field directly, so the draw lands on the
            // undo stack like any other Inspector edit.
            serializedObject.ApplyModifiedProperties();

            // Applying alone does not always raise the scene's own dirty flag, and a draw nobody is
            // prompted to save is a draw that quietly reverts on the next scene load.
            if (director.gameObject.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(director.gameObject.scene);
            }

            var description = new System.Text.StringBuilder("새 결선 트랙:\n");
            foreach (var candidate in picked)
            {
                description.Append($"  seed {candidate.Seed} — {candidate.Length:F0} m");
                description.Append(candidate.HasObstacle ? "  · 장애물 구간 포함\n" : "\n");
            }

            m_Message = description.ToString().TrimEnd();
            m_MessageType = MessageType.Info;
        }

        public readonly struct Candidate
        {
            public readonly int Seed;
            public readonly float Length;
            public readonly bool HasObstacle;

            public Candidate(int seed, float length, bool hasObstacle)
            {
                Seed = seed;
                Length = length;
                HasObstacle = hasObstacle;
            }
        }

        /// <summary>
        /// Draws <see cref="k_SeedCount"/> distinct seeds whose circuits pass the generator's own
        /// validation, at least one of them carrying an ObstacleStraight.
        ///
        /// The obstacle one is drawn first and the rest filled in around it, rather than drawing
        /// three and hoping: an ObstacleStraight turns up in roughly a quarter of circuits, so
        /// "draw three and retry the whole set" would throw away a lot of perfectly good seeds.
        /// </summary>
        public static bool TryPickSeeds(out List<Candidate> picked, out string error)
        {
            picked = new List<Candidate>(k_SeedCount);
            error = null;

            var random = new System.Random();
            var seen = new HashSet<int>();
            var attempts = 0;

            // A rejected seed logs a warning from the generator, and a draw rejects plenty. They are
            // noise here — the whole point of the button is to skip past them — so the log is muted
            // for the duration. Nothing else runs inside this window.
            var previousLogging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;

            try
            {
                while (picked.Count < k_SeedCount && attempts < k_MaxAttempts)
                {
                    attempts++;

                    var seed = random.Next(k_SeedMin, k_SeedMax + 1);
                    if (!seen.Add(seed))
                    {
                        continue;
                    }

                    var track = TrackGenerator.Generate(seed, enableHazardSections: true);
                    if (track is not { FullyValid: true })
                    {
                        continue;
                    }

                    var hasObstacle = HasObstacleSection(track);

                    // Until one is in the bag, only an obstacle circuit is accepted; after that,
                    // anything valid fills the remaining places.
                    var needObstacle = picked.Count == 0;
                    if (needObstacle && !hasObstacle)
                    {
                        continue;
                    }

                    picked.Add(new Candidate(seed, track.Model.TotalLength, hasObstacle));
                }
            }
            finally
            {
                Debug.unityLogger.logEnabled = previousLogging;
            }

            if (picked.Count < k_SeedCount)
            {
                error = $"{attempts}번 뽑는 동안 조건을 만족하는 시드를 {k_SeedCount}개 찾지 못했습니다. " +
                        "다시 눌러 보세요.";
                return false;
            }

            Shuffle(picked, random);
            return true;
        }

        static bool HasObstacleSection(GeneratedTrack track)
        {
            foreach (var section in track.Model.Sections)
            {
                if (section.Type == TrackSectionType.ObstacleStraight)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The obstacle circuit is always drawn first; without this it would always be
        /// raced first too.</summary>
        static void Shuffle(List<Candidate> candidates, System.Random random)
        {
            for (var i = candidates.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
        }
    }
}
