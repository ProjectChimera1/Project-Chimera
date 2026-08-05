#nullable enable
using System;
using ProjectChimera.Combat;           // DamageResolver
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden; // GoldenApplierScenario
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-551 — the per-tick <see cref="DeathLog"/> is deliberately NOT persisted by <see cref="SaveGameState"/>,
    /// which is correct only because a save is taken BETWEEN ticks, where the log is empty. That was a silent
    /// coupling: nothing enforced it, and a future mid-tick save path would have dropped a tick's <c>unit_dies</c>
    /// attribution with no signal at all. Two halves are proved here:
    ///
    /// <para>(a) <b>the guard</b> — <see cref="SaveGameState.CaptureFrom"/> now throws on an undrained log instead of
    /// silently capturing a save that cannot represent it;</para>
    ///
    /// <para>(b) <b>the premise the guard rests on</b> — "the log is empty at the tick boundary" was NOT universally
    /// true. The <c>ScenarioDirector</c>'s trigger-less early-out skips <c>UpdateSnapshots</c> (the log's only wipe
    /// point), so a scenario with zero triggers — which is exactly what the save/load harness and any trigger-free
    /// user map is — accumulated a record per combat death, tick after tick, until <see cref="DeathLog.CAPACITY"/>.
    /// The early-out now drains the log like every other transient rail on that path.</para>
    ///
    /// Godot-free, Tier-1. Nothing here folds into <c>SimChecksum</c> (the log never was folded), so no golden moves.
    /// </summary>
    public class DeathLogSaveGuardTests
    {
        // ── Harness (mirrors SaveLoadTests: the trigger-LESS golden applier scenario) ────────────────────────────

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
        }

        private static Applied BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, SlotDefs = slotDefs };
        }

        private static SaveGameState Capture(SimulationHost host)
        {
            var table = CanonicalEffectDescriptorTable.Build(host.AbilityRegistry, host.ItemRegistry);
            return SaveGameState.CaptureFrom(host, table);
        }

        /// <summary>Spawn a throwaway hostile and kill it through the single death choke point, so exactly one
        /// genuine record lands in the log (no scenario unit is disturbed).</summary>
        private static void KillOneThrowaway(SimulationHost host)
        {
            EntityWorld w = host.World;
            int id = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            DamageResolver.KillEntity(w, id, Faction.Player1, null, null);
            Assert.False(w.IsAlive(id));
        }

        // ── (a) The guard ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The DW-551 vector, stated directly: a capture reached with the log non-empty is a capture taken MID-tick,
        /// and the save it would produce silently loses that tick's <c>unit_dies</c> attribution. Fail-closed instead.
        /// </summary>
        [Fact]
        public void CaptureFrom_Throws_WhenTheDeathLogHoldsUndrainedRecords()
        {
            Applied h = BuildApplied();
            for (int i = 0; i < 5; i++) h.Host.StepOnce();

            KillOneThrowaway(h.Host);                       // stand in for a mid-tick capture: the log is live
            Assert.Equal(1, h.Host.World.DeathLog.Count);

            var ex = Assert.Throws<InvalidOperationException>(() => Capture(h.Host));
            Assert.Contains("DeathLog", ex.Message);
            Assert.Contains("tick boundary", ex.Message);
        }

        /// <summary>The fence on the guard: the record count is reported, so the message names the real state rather
        /// than a generic "invalid" — and more than one undrained record trips it just the same.</summary>
        [Fact]
        public void CaptureFrom_Throw_ReportsHowManyRecordsAreUndrained()
        {
            Applied h = BuildApplied();
            for (int i = 0; i < 5; i++) h.Host.StepOnce();

            KillOneThrowaway(h.Host);
            KillOneThrowaway(h.Host);
            KillOneThrowaway(h.Host);
            Assert.Equal(3, h.Host.World.DeathLog.Count);

            var ex = Assert.Throws<InvalidOperationException>(() => Capture(h.Host));
            Assert.Contains("3 undrained record", ex.Message);
        }

        // ── (b) The premise: the trigger-less early-out must drain the log ───────────────────────────────────────

        /// <summary>
        /// The regression the guard exposed. A trigger-less scenario never reaches <c>UpdateSnapshots</c>, so before
        /// DW-551 each combat death left a record behind FOREVER: after N kill-then-tick rounds the log held N
        /// records, and a perfectly ordinary between-ticks save would have hit the new guard. It must be empty after
        /// EVERY tick, no matter how many deaths that tick saw.
        /// </summary>
        [Fact]
        public void TriggerLessDirectorTick_DrainsTheDeathLog_SoRecordsNeverAccumulate()
        {
            Applied h = BuildApplied();
            for (int i = 0; i < 5; i++) h.Host.StepOnce();

            for (int round = 0; round < 6; round++)
            {
                KillOneThrowaway(h.Host);
                KillOneThrowaway(h.Host);
                Assert.Equal(2, h.Host.World.DeathLog.Count);   // recorded within the tick…

                h.Host.StepOnce();                              // …and the director (last in the tick) drains it
                Assert.Equal(0, h.Host.World.DeathLog.Count);
            }
        }

        /// <summary>
        /// The two halves joined: the ordinary in-match save path. Deaths happen, the tick completes, the save is
        /// taken between ticks — and it must go through untouched. Without the early-out drain this capture throws;
        /// without the guard it silently produced an under-specified save. Also pins that the guard is not merely
        /// satisfied but that the resulting state is a real, self-consistent one.
        /// </summary>
        [Fact]
        public void CaptureFrom_Succeeds_AtTheTickBoundaryAfterCombatDeaths()
        {
            Applied h = BuildApplied();
            for (int i = 0; i < 5; i++) h.Host.StepOnce();

            KillOneThrowaway(h.Host);
            h.Host.StepOnce();                                  // the tick that carries the death completes

            Assert.Equal(0, h.Host.World.DeathLog.Count);
            SaveGameState state = Capture(h.Host);
            state.Validate("deathlog-guard");                   // must not throw
            Assert.Equal(h.Host.CurrentTick, state.Tick);
        }
    }
}
