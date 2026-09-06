using System.Collections.Generic;
using NUnit.Framework;
using RacingBotCup.Finals;

namespace RacingBotCup.Tests
{
    /// <summary>
    /// The finals payout (기획서 §8): six points for a win down to none for sixth, nothing at all
    /// for a retirement, added up across the circuits.
    /// </summary>
    public sealed class FinalsScoringTests
    {
        static FinalsRacer Finisher(string name, float time)
        {
            return new FinalsRacer { Name = name, State = RacerState.Finished, Time = time };
        }

        static FinalsRacer Retired(string name, float distance)
        {
            return new FinalsRacer { Name = name, State = RacerState.Retired, Distance = distance };
        }

        [Test]
        public void FastestLapTakesSixPoints()
        {
            var field = new List<FinalsRacer>
            {
                Finisher("slow", 70f),
                Finisher("quick", 60f),
                Finisher("middling", 65f),
            };

            FinalsScoring.AwardMap(field, seed: 1);

            Assert.AreEqual("quick", field[0].Name);
            Assert.AreEqual(6, field[0].MapPoints);
            Assert.AreEqual(4, field[1].MapPoints);
            Assert.AreEqual(3, field[2].MapPoints);
        }

        [Test]
        public void RetirementScoresNothingAndSortsBehindEveryFinisher()
        {
            var field = new List<FinalsRacer>
            {
                Retired("crashed", 900f),
                Finisher("last", 120f),
            };

            FinalsScoring.AwardMap(field, seed: 1);

            Assert.AreEqual("last", field[0].Name);
            Assert.AreEqual(6, field[0].MapPoints);
            Assert.AreEqual(0, field[1].MapPoints, "a retirement scores nothing, however far it got");
        }

        [Test]
        public void SixthPlaceScoresNothingToo()
        {
            var field = new List<FinalsRacer>();
            for (var i = 0; i < 6; i++)
            {
                field.Add(Finisher($"racer{i}", 60f + i));
            }

            FinalsScoring.AwardMap(field, seed: 1);

            CollectionAssert.AreEqual(
                new[] { 6, 4, 3, 2, 1, 0 },
                field.ConvertAll(racer => racer.MapPoints));
        }

        [Test]
        public void PointsAccumulateAcrossMaps()
        {
            var alice = Finisher("alice", 60f);
            var bob = Finisher("bob", 61f);
            var field = new List<FinalsRacer> { alice, bob };

            FinalsScoring.AwardMap(field, seed: 1);

            // Second circuit: bob wins it, alice retires.
            alice.State = RacerState.Retired;
            bob.Time = 59f;
            FinalsScoring.AwardMap(field, seed: 2);

            Assert.AreEqual(6, alice.TotalPoints);
            Assert.AreEqual(10, bob.TotalPoints);
            Assert.AreEqual(2, alice.History.Count);

            FinalsScoring.OrderChampionship(field);
            Assert.AreEqual("bob", field[0].Name);
            Assert.AreEqual(1, field[0].Position);
        }

        [Test]
        public void LevelOnPointsIsBrokenByFinishesThenTotalTime()
        {
            // Both won one circuit and were second on the other, so both sit on 10.
            var alice = Finisher("alice", 60f);
            var bob = Finisher("bob", 61f);
            var field = new List<FinalsRacer> { alice, bob };

            FinalsScoring.AwardMap(field, seed: 1);

            alice.Time = 70f;
            bob.Time = 62f;
            FinalsScoring.AwardMap(field, seed: 2);

            Assert.AreEqual(10, alice.TotalPoints);
            Assert.AreEqual(10, bob.TotalPoints);

            FinalsScoring.OrderChampionship(field);
            Assert.AreEqual("bob", field[0].Name, "same points, same finishes — quicker overall wins");
        }

        [Test]
        public void StillRacingSortsByDistanceAndAheadOfRetirements()
        {
            var running = new FinalsRacer { Name = "running", Distance = 400f };
            var further = new FinalsRacer { Name = "further", Distance = 800f };
            var field = new List<FinalsRacer> { running, Retired("out", 1500f), further, Finisher("home", 90f) };

            FinalsScoring.Order(field);

            CollectionAssert.AreEqual(
                new[] { "home", "further", "running", "out" },
                field.ConvertAll(racer => racer.Name));
        }
    }
}
