using System.Collections.Generic;
using RacingBotCup.Racing;
using UnityEngine;

namespace RacingBotCup.Finals
{
    public enum RacerState
    {
        /// <summary>On the circuit, lap not yet resolved.</summary>
        Racing = 0,

        /// <summary>Crossed the line. The only state that scores points.</summary>
        Finished = 1,

        /// <summary>Off the road too long, or still out there when the clock ran out.</summary>
        Retired = 2,
    }

    /// <summary>What one racer did on one circuit, kept so the final table can show the whole event.</summary>
    public struct MapRecord
    {
        public int Seed;
        public RacerState State;
        public float Time;
        public int Points;
    }

    /// <summary>
    /// One entry in the finals: a name, the car it is driving right now, and everything it has
    /// scored so far. Rebuilt onto a fresh car for every circuit — the points carry over, the rig
    /// does not.
    /// </summary>
    public sealed class FinalsRacer
    {
        public string Name;
        public Color Colour = Color.white;

        /// <summary>Null between circuits.</summary>
        public RacerRig Rig;

        /// <summary>Null between circuits.</summary>
        public RaceContext Context;

        public RacerState State = RacerState.Racing;

        /// <summary>Lap time when finished, time on track otherwise.</summary>
        public float Time;

        /// <summary>Metres of valid progress — how the still-running and the retired are ordered.</summary>
        public float Distance;

        public int MapPoints;
        public int TotalPoints;

        /// <summary>Position on the current circuit, 1-based. 0 until the standings are first built.</summary>
        public int Position;

        public readonly List<MapRecord> History = new List<MapRecord>();

        public bool IsRacing => State == RacerState.Racing;
    }

    /// <summary>
    /// The finals rules from 기획서 §8: each circuit is a solo time attack, and the order of the lap
    /// times pays out points that add up across the event.
    /// </summary>
    public static class FinalsScoring
    {
        /// <summary>Points for 1st through 6th. Sixth scores nothing, and neither does a retirement.</summary>
        public static readonly int[] PointsByPosition = { 6, 4, 3, 2, 1, 0 };

        /// <summary>Maximum grid. Fixed by the points table having six places in it.</summary>
        public const int MaxRacers = 6;

        public static int PointsForPosition(int position)
        {
            var index = position - 1;
            return index >= 0 && index < PointsByPosition.Length ? PointsByPosition[index] : 0;
        }

        /// <summary>
        /// Race order, live or final: finishers by lap time, then whoever is still going by how far
        /// they have got, then retirements the same way. The same comparison drives the standings
        /// board mid-race and the result table at the end, so the board never reshuffles at the flag.
        /// </summary>
        public static int Compare(FinalsRacer a, FinalsRacer b)
        {
            if (a.State != b.State)
            {
                return Rank(a.State).CompareTo(Rank(b.State));
            }

            return a.State == RacerState.Finished
                ? a.Time.CompareTo(b.Time)
                : b.Distance.CompareTo(a.Distance);
        }

        /// <summary>
        /// Board order of the three states. Spelled out rather than left to the enum's own values,
        /// which have to keep <see cref="RacerState.Racing"/> at zero so a fresh racer defaults to
        /// it — and that would sort a car still driving ahead of one already home.
        /// </summary>
        static int Rank(RacerState state)
        {
            return state switch
            {
                RacerState.Finished => 0,
                RacerState.Racing => 1,
                _ => 2,
            };
        }

        /// <summary>Sorts in place into race order and stamps each racer's position.</summary>
        public static void Order(List<FinalsRacer> racers)
        {
            racers.Sort(Compare);
            for (var i = 0; i < racers.Count; i++)
            {
                racers[i].Position = i + 1;
            }
        }

        /// <summary>
        /// Closes one circuit: orders the field, pays out, and files the result. Only finishers
        /// score — a retirement takes zero however far it got.
        /// </summary>
        public static void AwardMap(List<FinalsRacer> racers, int seed)
        {
            Order(racers);

            foreach (var racer in racers)
            {
                racer.MapPoints = racer.State == RacerState.Finished
                    ? PointsForPosition(racer.Position)
                    : 0;

                racer.TotalPoints += racer.MapPoints;
                racer.History.Add(new MapRecord
                {
                    Seed = seed,
                    State = racer.State,
                    Time = racer.Time,
                    Points = racer.MapPoints,
                });
            }
        }

        /// <summary>
        /// Championship order: points, then — for two racers level on points — the one who has
        /// finished more circuits, then their combined time. Without the tiebreak two racers who
        /// took the same points in a different order would sit in list order, which is the order
        /// they happened to be typed into the Inspector.
        /// </summary>
        public static void OrderChampionship(List<FinalsRacer> racers)
        {
            racers.Sort((a, b) =>
            {
                if (a.TotalPoints != b.TotalPoints)
                {
                    return b.TotalPoints.CompareTo(a.TotalPoints);
                }

                var finishes = Finishes(b).CompareTo(Finishes(a));
                return finishes != 0 ? finishes : TotalTime(a).CompareTo(TotalTime(b));
            });

            for (var i = 0; i < racers.Count; i++)
            {
                racers[i].Position = i + 1;
            }
        }

        static int Finishes(FinalsRacer racer)
        {
            var count = 0;
            foreach (var record in racer.History)
            {
                if (record.State == RacerState.Finished)
                {
                    count++;
                }
            }

            return count;
        }

        static float TotalTime(FinalsRacer racer)
        {
            var total = 0f;
            foreach (var record in racer.History)
            {
                total += record.State == RacerState.Finished ? record.Time : 0f;
            }

            return total;
        }
    }
}
