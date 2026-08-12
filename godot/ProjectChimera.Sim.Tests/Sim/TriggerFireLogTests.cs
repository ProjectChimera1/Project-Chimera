#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.15 — the non-folded trigger-debug OBSERVATION BUFFER <see cref="TriggerFireLog"/>: per-exec fire
    /// counters + a fixed-capacity tick-stamped ring of recent fires. The unit half pins Record/Reset/ring-cap/Count/
    /// newest-first behavior; the DIFFERENTIAL GUARD proves the buffer is checksum-neutral — a director run WITH a
    /// <see cref="TriggerFireLog"/> attached (the write genuinely executes) produces a byte-identical
    /// <c>SimChecksum</c> to one with a <c>null</c> fire log (the write is genuinely absent — the director's
    /// <c>_fireLog?.Record</c> skips it), so the fire-log write demonstrably does not enter the fold; and
    /// <c>SimChecksum.AlgoVersion</c> stays 21. The exec→authored mapping the overlay uses for names/navigation is
    /// pinned in the Godot-free tier here too (it must be correct under non-default trigger Priority).
    /// </summary>
    public class TriggerFireLogTests
    {
        // ── Unit: Record / Count / Reset / ring-cap / newest-first / TotalRecorded / Clear ──

        [Fact]
        public void Record_IncrementsPerExecCount_AndRingNewestFirst()
        {
            var log = new TriggerFireLog();
            log.Reset(3);
            Assert.Equal(3, log.ExecCount);

            log.Record(0, 5);
            log.Record(2, 6);
            log.Record(0, 7);

            Assert.Equal(2, log.Count(0));
            Assert.Equal(0, log.Count(1));
            Assert.Equal(1, log.Count(2));
            Assert.Equal(3L, log.TotalRecorded);

            // Newest-first: (0,7) then (2,6) then (0,5).
            Assert.Equal(3, log.RecentCount);
            Assert.Equal(0, log.Recent(0).ExecIdx); Assert.Equal(7, log.Recent(0).Tick);
            Assert.Equal(2, log.Recent(1).ExecIdx); Assert.Equal(6, log.Recent(1).Tick);
            Assert.Equal(0, log.Recent(2).ExecIdx); Assert.Equal(5, log.Recent(2).Tick);
        }

        [Fact]
        public void Ring_CapsAtCapacity_KeepingNewest()
        {
            var log = new TriggerFireLog();
            log.Reset(1);
            int total = TriggerFireLog.RingCapacity + 50;
            for (int t = 0; t < total; t++) log.Record(0, t);

            Assert.Equal(TriggerFireLog.RingCapacity, log.RecentCount);
            Assert.Equal(total, log.Count(0));            // counts are NOT capped
            Assert.Equal((long)total, log.TotalRecorded); // total tracks every Record
            // Newest entry is the last tick recorded; the oldest retained is total-RingCapacity.
            Assert.Equal(total - 1, log.Recent(0).Tick);
            Assert.Equal(total - TriggerFireLog.RingCapacity, log.Recent(TriggerFireLog.RingCapacity - 1).Tick);
        }

        [Fact]
        public void Reset_ZeroesCountsAndEmptiesRing_GrowsInPlace()
        {
            var log = new TriggerFireLog();
            log.Reset(2);
            log.Record(0, 1);
            log.Record(1, 2);

            log.Reset(4); // grow + zero
            Assert.Equal(4, log.ExecCount);
            Assert.Equal(0, log.Count(0));
            Assert.Equal(0, log.Count(1));
            Assert.Equal(0, log.RecentCount);
            Assert.Equal(0L, log.TotalRecorded);
        }

        [Fact]
        public void OutOfRange_CountReturnsZero_RecentReturnsDefault()
        {
            var log = new TriggerFireLog();
            log.Reset(1);
            log.Record(0, 9);
            Assert.Equal(0, log.Count(5));       // out-of-range exec
            Assert.Equal(0, log.Count(-1));
            Assert.Equal(0, log.Recent(9).Tick); // out-of-range ring index → default
        }

        [Fact]
        public void Clear_EmptiesEverything()
        {
            var log = new TriggerFireLog();
            log.Reset(2);
            log.Record(0, 1);
            log.Clear();
            Assert.Equal(0, log.ExecCount);
            Assert.Equal(0, log.RecentCount);
            Assert.Equal(0L, log.TotalRecorded);
            Assert.Equal(0, log.Count(0));
        }

        [Fact]
        public void Generation_BumpsOnResetAndClear_EvenWhenTotalReturnsToSameValue()
        {
            // The overlay's fired-log gates a rebuild on TotalRecorded, but an F5 re-apply of a match_start-heavy
            // scenario can drop the total to 0 and climb straight back to the SAME pre-reset high-water within one
            // frame. Generation is the unambiguous reset signal that TotalRecorded equality alone cannot give.
            var log = new TriggerFireLog();
            log.Reset(1);
            int g0 = log.Generation;
            log.Record(0, 1);
            log.Record(0, 2);
            Assert.Equal(2L, log.TotalRecorded);

            log.Reset(1);          // F5 re-apply: total drops to 0, generation advances
            Assert.True(log.Generation > g0);
            int g1 = log.Generation;
            log.Record(0, 1);
            log.Record(0, 2);
            Assert.Equal(2L, log.TotalRecorded); // total is back on the pre-reset value…
            Assert.True(g1 > g0);                // …but the generation change proves a reset occurred

            log.Clear();
            Assert.True(log.Generation > g1);    // Clear advances it too
        }

        // ── Differential guard: checksum-neutral with vs without the buffer ──

        private static ScenarioData FiringScenario() => new()
        {
            Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.Global },
            },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name    = "on_start",
                    Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "score", Value = 42 } },
                },
            },
        };

        /// <summary>Run the firing scenario N ticks through a director with the given (or no) fire log, returning a
        /// single SimChecksum folded over the sim stores AFTER the run (world/buildings/resources/factions/vars — the
        /// stores this director mutates; vars folds at v16). One post-run fold, not a per-tick stream — a mid-run
        /// divergence still surfaces in the final fold.</summary>
        private static uint RunChecksum(int ticks, TriggerFireLog? fireLog)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var vars      = new DslVarTable();
            var factions  = new FactionRegistry(2);

            var director = new ScenarioDirector(buildings, resources, vars,
                fireLog: fireLog);
            director.LoadScenario(FiringScenario());
            for (int i = 0; i < ticks; i++) director.Tick(world, Fixed.One);

            return SimChecksum.Compute(world, buildings, resources, factions, vars: vars);
        }

        [Fact]
        public void DifferentialGuard_ChecksumByteIdentical_WithVsWithoutBuffer()
        {
            const int Ticks = 4;
            uint withBuffer    = RunChecksum(Ticks, new TriggerFireLog());
            uint withoutBuffer = RunChecksum(Ticks, null);

            // WITH the buffer the fire-log write executes on every fire; with null it is genuinely skipped
            // (_fireLog?.Record). Equal checksums therefore prove precisely this: the fire-log WRITE has no side
            // effect on the folded sim stores this run computes over (world/buildings/resources/factions/vars) — the
            // Record() path does not mutate any folded state. (It does not, and cannot, prove that a FUTURE change
            // which passed the buffer into SimChecksum.Compute stays neutral — that regression is guarded instead by
            // the AlgoVersion pin below plus the golden/re-baseline suite, which would move if the fold changed.)
            Assert.Equal(withoutBuffer, withBuffer); // the fire-log write does not perturb the fold
            Assert.Equal(25, SimChecksum.AlgoVersion);
        }

        // ── Exec→authored mapping (overlay names + click-to-navigate) under non-default trigger priority ──

        private static ScenarioData TwoTriggerPriorityScenario() => new()
        {
            Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.Global },
            },
            Triggers = new[]
            {
                // Authored index 0, LOW priority — sorts SECOND in exec order.
                new TriggerDefinition
                {
                    Name = "alpha", Priority = 0,
                    Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "score", Value = 1 } },
                },
                // Authored index 1, HIGH priority — sorts FIRST in exec order.
                new TriggerDefinition
                {
                    Name = "beta", Priority = 10,
                    Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "score", Value = 2 } },
                },
            },
        };

        [Fact]
        public void ExecToAuthoredMapping_ResolvesFiredExecToAuthoredTrigger_UnderNonDefaultPriority()
        {
            var world    = new EntityWorld();
            var vars     = new DslVarTable();
            var fireLog  = new TriggerFireLog();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars,
                fireLog: fireLog);
            director.LoadScenario(TwoTriggerPriorityScenario());

            // Exec order is Priority-desc: exec 0 = "beta" (authored index 1); exec 1 = "alpha" (authored index 0).
            // The overlay must resolve each exec back to the AUTHORED trigger — a raw exec-as-authored index would
            // mislabel and mis-navigate here (the whole point of this test).
            Assert.Equal(2, fireLog.ExecCount);
            Assert.Equal(1, fireLog.AuthoredIndex(0)); // exec 0 → authored Triggers[1] (beta)
            Assert.Equal(0, fireLog.AuthoredIndex(1)); // exec 1 → authored Triggers[0] (alpha)

            director.Tick(world, Fixed.One); // match_start fires both, in exec order (beta then alpha)

            // Newest-first: Recent(0) is the last recorded (exec 1 = alpha); Recent(1) is exec 0 = beta.
            Assert.Equal(2, fireLog.RecentCount);
            Assert.Equal(0, fireLog.AuthoredIndex(fireLog.Recent(0).ExecIdx)); // alpha → authored 0
            Assert.Equal(1, fireLog.AuthoredIndex(fireLog.Recent(1).ExecIdx)); // beta  → authored 1
        }

        [Fact]
        public void AttachedBuffer_ObservesTheFire()
        {
            var world     = new EntityWorld();
            var vars      = new DslVarTable();
            var fireLog   = new TriggerFireLog();
            var director  = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars,
                fireLog: fireLog);
            director.LoadScenario(FiringScenario());

            director.Tick(world, Fixed.One); // tick 1 — match_start fires exec 0

            Assert.Equal(1, fireLog.ExecCount);
            Assert.Equal(1, fireLog.Count(0));
            Assert.True(fireLog.TotalRecorded >= 1);
            Assert.Equal(0, fireLog.Recent(0).ExecIdx);
            Assert.Equal(1, fireLog.Recent(0).Tick); // stamped with the deterministic sim tick (the _publishTick source)
        }

        [Fact]
        public void LoadScenario_ResetsTheBuffer()
        {
            var world     = new EntityWorld();
            var vars      = new DslVarTable();
            var fireLog   = new TriggerFireLog();
            var director  = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars,
                fireLog: fireLog);
            director.LoadScenario(FiringScenario());
            director.Tick(world, Fixed.One);
            Assert.Equal(1, fireLog.Count(0));

            director.LoadScenario(FiringScenario()); // re-apply (F5) — the buffer resets alongside the fire guards
            Assert.Equal(0, fireLog.Count(0));
            Assert.Equal(0, fireLog.RecentCount);
            Assert.Equal(0L, fireLog.TotalRecorded);
        }

        // ── Production wiring: the host-owned fire log is the one the director writes (the overlay reads THIS one) ──

        [Fact]
        public void SimulationHost_WiresItsFireLogThroughTheDirector_AndClearsItOnReset()
        {
            // The overlay renders host.TriggerFireLog. That instance is only populated because SimulationHost passes
            // its own buffer into the ScenarioDirector ctor. The per-director tests above hand a buffer straight to a
            // bare director, so they would ALL stay green if that production wiring line were dropped — the overlay
            // would then silently show zero fires forever. This test exercises the real host→director wiring.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ScenarioDirector.LoadScenario(FiringScenario());
            host.ScenarioDirector.Tick(host.World, Fixed.One); // match_start fires exec 0

            Assert.True(host.TriggerFireLog.TotalRecorded >= 1);
            Assert.Equal(1, host.TriggerFireLog.Count(0));
            Assert.Equal(1, host.TriggerFireLog.ExecCount);

            host.ClearForReset(); // Edit↔Play reset must empty the non-folded observation buffer too
            Assert.Equal(0, host.TriggerFireLog.ExecCount);
            Assert.Equal(0, host.TriggerFireLog.RecentCount);
            Assert.Equal(0L, host.TriggerFireLog.TotalRecorded);
        }
    }
}
