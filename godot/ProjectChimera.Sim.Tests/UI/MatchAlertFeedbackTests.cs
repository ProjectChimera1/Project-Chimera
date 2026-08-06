#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Sim.Tests.Golden; // CombatResetScenario + GoldenChecksumReplay
using ProjectChimera.UI;               // UnderAttackThrottle + DenialReasonText
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 11.4 (FR-74) — the Godot-free proofs for the match-feedback floor:
    ///   (a) the under-attack region/time throttle policy (same region within the window → suppressed);
    ///   (b) the guard-sourced denial + production-completion event plumbing (the SINGLE guard stamps the reason +
    ///       faction; the queue carries them) and its checksum-NEUTRALITY (the queue is not a SimChecksum input);
    ///   (c) the denial-reason→text map is TOTAL (every reason maps);
    ///   (d) no hash AlgoVersion pin moves (this story folds nothing).
    /// All Godot-free — the presentation Node layer is verified in-engine.
    /// </summary>
    public class MatchAlertFeedbackTests
    {
        // ── (a) UnderAttackThrottle ─────────────────────────────────────────────────────────────

        [Fact]
        public void Throttle_SameRegionWithinWindow_IsSuppressed()
        {
            var throttle = new UnderAttackThrottle(cellSize: 24.0, windowSec: 8.0);

            Assert.True(throttle.ShouldAlert(0f, 0f, nowSec: 0.0));   // first hit in the region → alert
            Assert.False(throttle.ShouldAlert(5f, 3f, nowSec: 1.0));  // same 24-unit cell, +1s → suppressed
            Assert.False(throttle.ShouldAlert(0f, 0f, nowSec: 7.9));  // still inside the 8s window → suppressed
        }

        [Fact]
        public void Throttle_DifferentRegion_Alerts()
        {
            var throttle = new UnderAttackThrottle(cellSize: 24.0, windowSec: 8.0);

            Assert.True(throttle.ShouldAlert(0f, 0f, nowSec: 0.0));    // cell (0,0)
            Assert.True(throttle.ShouldAlert(100f, 0f, nowSec: 0.1));  // a far-away cell → its own alert stream
        }

        [Fact]
        public void Throttle_AfterWindow_AlertsAgain()
        {
            var throttle = new UnderAttackThrottle(cellSize: 24.0, windowSec: 8.0);

            Assert.True(throttle.ShouldAlert(0f, 0f, nowSec: 0.0));
            Assert.False(throttle.ShouldAlert(0f, 0f, nowSec: 5.0)); // inside the window
            Assert.True(throttle.ShouldAlert(0f, 0f, nowSec: 8.1));  // window elapsed → the sustained raid re-alerts once
        }

        [Fact]
        public void Throttle_Clear_ResetsSuppression()
        {
            var throttle = new UnderAttackThrottle();
            Assert.True(throttle.ShouldAlert(0f, 0f, 0.0));
            Assert.False(throttle.ShouldAlert(0f, 0f, 0.5));
            throttle.Clear();
            Assert.True(throttle.ShouldAlert(0f, 0f, 0.6)); // forgotten → alerts immediately
        }

        // ── (c) DenialReasonText totality ───────────────────────────────────────────────────────

        [Fact]
        public void DenialReasonText_MapsEveryReason()
        {
            foreach (DenialReason r in Enum.GetValues(typeof(DenialReason)))
                Assert.False(string.IsNullOrEmpty(DenialReasonText.For(r)),
                    $"DenialReason.{r} has no text mapping (the map is not total).");
        }

        // ── (b) guard-sourced denial plumbing (BuildingSystem.TrainUnit) ────────────────────────

        private static FactionDefinition TrainFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker",  Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "melee_a", Category = "Melee",  Hp = 100f }); // index 1
            return f;
        }

        [Fact]
        public void TrainUnit_SupplyCapped_PushesGuardSourcedDenial()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 0; // force the supply gate to reject
            var sys = new BuildingSystem(buildings, resources, TrainFaction());

            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            var events = new CombatEventQueue();

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1, events: events));

            Assert.Equal(1, events.Count);
            CombatEvent e = events.Get(0);
            Assert.Equal(CombatEventType.OrderDenied, e.Type);
            Assert.Equal(DenialReason.SupplyCapped, e.Reason);      // the guard stamped the reason it computed
            Assert.Equal(Faction.Player1, e.Faction);              // and the acting faction (local-only feedback filter)
        }

        [Fact]
        public void TrainUnit_NullQueue_StaysSilent()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.SupplyCap[(int)Faction.Player1] = 0;
            var sys = new BuildingSystem(buildings, resources, TrainFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            // A null sink (golden / headless / replay) must be a graceful no-op, never a crash.
            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1, events: null));
        }

        [Fact]
        public void SpawnTrainedUnit_PushesTrainingCompleteWithFaction()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, TrainFaction());

            var events = new CombatEventQueue();
            sys.SetCombatEvents(events); // completion cue rides the wired queue (as SimulationHost wires it)

            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            sys.Tick(world, Fixed.FromInt(100)); // expire the train timer → SpawnTrainedUnit fires

            bool found = false;
            for (int i = 0; i < events.Count; i++)
            {
                CombatEvent e = events.Get(i);
                if (e.Type == CombatEventType.TrainingComplete && e.Faction == Faction.Player1) found = true;
            }
            Assert.True(found, "TrainingComplete was not pushed for the training faction.");
        }

        // ── the CombatEventQueue contract (the golden-safe seam) ────────────────────────────────

        [Fact]
        public void CombatEventQueue_PushDenied_CarriesFactionAndReason()
        {
            var q = new CombatEventQueue();
            q.PushDenied(FixedVec3.Zero, Faction.Player2, DenialReason.NeedCrystal);
            q.Push(CombatEventType.MeleeHit, FixedVec3.Zero, Faction.Player1); // faction-stamped hit overload

            Assert.Equal(2, q.Count);
            Assert.Equal(DenialReason.NeedCrystal, q.Get(0).Reason);
            Assert.Equal(Faction.Player2, q.Get(0).Faction);
            Assert.Equal(DenialReason.None, q.Get(1).Reason);
            Assert.Equal(Faction.Player1, q.Get(1).Faction);
        }

        // ── (b/d) determinism: faction-stamped hit/kill pushes are checksum-NEUTRAL + pins unchanged ──

        [Fact]
        public void FightingScenario_TwoRuns_ProduceByteIdenticalChecksum()
        {
            const int N = 40;

            IReadOnlyList<GoldenChecksumReplay.Sample> run1 = RunSamples(BuildFightingHost(), N);
            IReadOnlyList<GoldenChecksumReplay.Sample> run2 = RunSamples(BuildFightingHost(), N);

            Assert.Equal(N, run1.Count);
            GoldenChecksumReplay.Divergence? d = GoldenChecksumReplay.CompareSequences(run1, run2);
            Assert.True(d is null,
                d is null ? "" : $"stamping the victim faction on hit/kill pushes perturbed the checksum: "
                                 + GoldenChecksumReplay.DescribeDivergence(d.Value));
        }

        [Fact]
        public void HashAlgoVersions_AreUnchanged()
        {
            // Story 11.4 folds nothing — every pin stays exactly where SimResetTests pins it.
            Assert.Equal(23, SimChecksum.AlgoVersion);
            Assert.Equal(14, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        private static SimulationHost BuildFightingHost()
        {
            SimulationHost host = CombatResetScenario.Build();
            CombatResetScenario.CastAt(host);
            return host;
        }

        private static IReadOnlyList<GoldenChecksumReplay.Sample> RunSamples(SimulationHost host, int ticks)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            host.SetChecksumSink((t, h) => seq.Add(new GoldenChecksumReplay.Sample(t, h)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }
    }
}
