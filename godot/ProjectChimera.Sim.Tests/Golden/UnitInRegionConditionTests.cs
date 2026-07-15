#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 6.4 — the <c>unit_in_region</c> condition evaluated by <see cref="ScenarioDirector"/>: a live unit of
    /// the named faction inside the region's rect makes the condition TRUE (Fixed inclusive point-in-rect over
    /// ascending-entity-id positions), gating a victory action; no such unit makes it FALSE. Mirrors
    /// <c>ScenarioDirectorThresholdTests</c>' drive-a-real-director style. Both outcomes are deterministic (same
    /// inputs → same result across repeated runs — the replay-equality proxy).
    /// </summary>
    public class UnitInRegionConditionTests
    {
        private const string RegionId = "hill";

        // A region rect [-10,-10 → 10,10] as a resolved RegionStore.
        private static RegionStore HillStore() => new RegionStore(
            new[] { RegionId },
            new[] { new FixedRect(Fixed.FromInt(-10), Fixed.FromInt(-10), Fixed.FromInt(10), Fixed.FromInt(10)) });

        /// <summary>
        /// Drive a fresh director one tick with a match_start → unit_in_region(RegionId, faction 0) → victory trigger.
        /// A single Player1 unit is spawned at (<paramref name="ux"/>, <paramref name="uz"/>). Returns whether victory fired.
        /// </summary>
        private static bool VictoryFires(Fixed ux, Fixed uz, RegionStore? store = null, Faction unitFaction = Faction.Player1)
        {
            var resources = new ResourceStore(Fixed.Zero);
            var director = new ScenarioDirector(new BuildingStore(), resources);
            bool fired = false;
            director.OnVictory = _ => fired = true;
            director.SetRegionStore(store ?? HillStore());
            director.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "koth",
                        Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = RegionId } },
                        Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                    },
                },
            });

            var world = new EntityWorld();
            world.Create(new FixedVec3(ux, Fixed.Zero, uz), unitFaction, Fixed.FromInt(100), Fixed.FromInt(3));
            director.Tick(world, Fixed.FromInt(1));
            return fired;
        }

        [Fact]
        public void UnitInsideRegion_ConditionTrue_VictoryFires()
        {
            Assert.True(VictoryFires(Fixed.Zero, Fixed.Zero));
            Assert.True(VictoryFires(Fixed.FromInt(5), Fixed.FromInt(-5)));
        }

        [Fact]
        public void UnitOutsideRegion_ConditionFalse_NoVictory()
        {
            Assert.False(VictoryFires(Fixed.FromInt(50), Fixed.FromInt(50)));
            Assert.False(VictoryFires(Fixed.FromInt(11), Fixed.Zero)); // just outside MaxX
        }

        [Fact]
        public void UnitExactlyOnEdge_IsInclusive_VictoryFires()
        {
            Assert.True(VictoryFires(Fixed.FromInt(10), Fixed.Zero));   // on MaxX
            Assert.True(VictoryFires(Fixed.FromInt(-10), Fixed.FromInt(10))); // on the MinX/MaxZ corner
        }

        [Fact]
        public void WrongFactionInsideRegion_ConditionFalse()
        {
            // The trigger scans faction 0 (Player1); a Player2 unit inside the region must NOT satisfy it.
            Assert.False(VictoryFires(Fixed.Zero, Fixed.Zero, unitFaction: Faction.Player2));
        }

        [Fact]
        public void UnresolvedRegionId_EvaluatesFalse_NoCrash()
        {
            // Empty store (no regions resolved) ⇒ TryGetIndex fails ⇒ condition false (validator blocks this pre-tick;
            // this guards the shadow-mode-reachable path).
            Assert.False(VictoryFires(Fixed.Zero, Fixed.Zero, store: RegionStore.Empty));
        }

        [Fact]
        public void DeadUnitInsideRegion_IsAliveGuardHolds_NoVictory()
        {
            // The IsAlive guard in the unit_in_region scan is load-bearing: a DEAD unit of the scanned faction
            // sitting INSIDE the region (a freed slot still below HighWaterMark) must NOT satisfy the condition.
            var resources = new ResourceStore(Fixed.Zero);
            var director = new ScenarioDirector(new BuildingStore(), resources);
            bool fired = false;
            director.OnVictory = _ => fired = true;
            director.SetRegionStore(HillStore());
            director.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "koth",
                        Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = RegionId } },
                        Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                    },
                },
            });

            var world = new EntityWorld();
            int id = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.Destroy(id); // despawn: freed slot stays below HighWaterMark, Flags cleared → IsAlive(id) == false
            Assert.False(world.IsAlive(id));

            director.Tick(world, Fixed.FromInt(1));
            Assert.False(fired); // dead unit inside the region must not satisfy unit_in_region
        }

        [Fact]
        public void Deterministic_SameInputsProduceSameOutcome_AcrossRepeatedRuns()
        {
            // Replay-equality proxy: the same seed/inputs yield the identical fire result every run (no float/Random).
            for (int i = 0; i < 5; i++)
            {
                Assert.True(VictoryFires(Fixed.Zero, Fixed.Zero));
                Assert.False(VictoryFires(Fixed.FromInt(99), Fixed.FromInt(99)));
            }
        }
    }
}
