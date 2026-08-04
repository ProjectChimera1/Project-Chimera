#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Sim.Tests.Golden; // ReflectionProbe (named-failure white-box lookups)
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-197 — <see cref="TriggerEnabledStore"/> out-of-range / stale-tail bounds. The ledger defect:
    /// <c>IsEnabled</c> returned <b>true</b> for any out-of-range index, <c>Clear()</c> zeroed only <c>Count</c>,
    /// and <c>Reset(count)</c> never wiped the buffer past <c>count</c> — so a shrinking Edit→Play re-apply (an
    /// author deletes a trigger and re-plays) left a stale enabled tail behind a smaller <c>Count</c>.
    ///
    /// <para><b>Reachability</b> (the ledger's first step of closure): <c>ScenarioDirector</c>'s sweep gates read
    /// <c>IsEnabled(idx)</c> for <c>idx</c> over the exec list, and the director's exec bookkeeping SURVIVES
    /// <c>SimulationHost.ClearForReset</c> until the re-apply's <c>LoadScenario</c> (the same reset window the
    /// Story 7.6 batch-row guard — review P8 — already treats as tickable). In that window the store's
    /// <c>Count</c> is 0, so EVERY sweep read is out-of-range; returning true there un-suppresses stale triggers
    /// — including authored-disabled ones, because Story 7.13 dropped the redundant <c>TriggerNode.Enabled</c>
    /// sweep check (the runtime mask SUBSUMES the authored flag). A stale trigger firing into the cleared world
    /// is exactly the additive-reset leak class Story 3.10 exists to prevent.</para>
    ///
    /// <para>All Godot-free. Nothing here can move a checksum or golden: the SimChecksum fold reads
    /// <c>IsEnabled(i)</c> only for <c>i &lt; Count</c> (always in-range), and in normal play the exec count
    /// equals the store count, so in-range behavior is byte-identical.</para>
    /// </summary>
    public class TriggerEnabledStoreBoundsTests
    {
        // ── Store-level bounds ──────────────────────────────────────────────────────────────────

        [Fact]
        public void IsEnabled_OutOfRangeIndex_ReturnsFalse()
        {
            var store = new TriggerEnabledStore();
            store.Reset(3);

            // In-range: the Reset all-true seed is intact (no over-suppression from the bounds fix).
            Assert.True(store.IsEnabled(0));
            Assert.True(store.IsEnabled(1));
            Assert.True(store.IsEnabled(2));

            // Out-of-range: false, never true — Count is the live bound.
            Assert.False(store.IsEnabled(3));
            Assert.False(store.IsEnabled(-1));
            Assert.False(store.IsEnabled(int.MaxValue));
            Assert.False(store.IsEnabled(int.MinValue));
        }

        [Fact]
        public void ShrinkingReapply_QuiescesTheDeletedTriggersIndex()
        {
            // The realistic Edit→Play shrink the DW-193 same-count test never exercises: 3 triggers loaded,
            // the author deletes one, Clear + Reset(2) re-seeds. Index 2 was enabled in the previous
            // scenario; behind the smaller Count it must now read DISABLED, not the stale true.
            var store = new TriggerEnabledStore();
            store.Reset(3);
            Assert.True(store.IsEnabled(2)); // live in the 3-trigger scenario

            store.Clear();                   // ClearForReset half of the round-trip
            store.Reset(2);                  // the re-apply's smaller LoadScenario re-seed

            Assert.Equal(2, store.Count);
            Assert.True(store.IsEnabled(0));
            Assert.True(store.IsEnabled(1));
            Assert.False(store.IsEnabled(2)); // the deleted trigger's old index is quiesced
        }

        [Fact]
        public void Clear_QuiescesEveryPreviouslyLiveIndex()
        {
            // The reset-window contract: between ClearForReset and the next LoadScenario the director's stale
            // exec list still sweeps IsEnabled(idx) — every read must come back false (quiesce), because the
            // runtime mask subsumes the authored Enabled flag at the sweep gates.
            var store = new TriggerEnabledStore();
            store.Reset(3);
            store.Clear();

            Assert.Equal(0, store.Count);
            Assert.False(store.IsEnabled(0));
            Assert.False(store.IsEnabled(1));
            Assert.False(store.IsEnabled(2));
        }

        [Fact]
        public void ResetAndClear_WipeTheStaleEnabledTail()
        {
            // Defense-in-depth half of DW-197: the buffer itself must not retain stale enabled bits past Count,
            // so no future caller that bypasses IsEnabled's bound can ever observe them. The buffer is
            // deliberately private — probe it white-box (named-failure idiom).
            var field = ReflectionProbe.Field(typeof(TriggerEnabledStore), "_enabled");
            var store = new TriggerEnabledStore();

            store.Reset(5); // all five seeded true
            store.Reset(2); // shrink: [2..4] is now the stale tail
            bool[] buf = ReflectionProbe.Read<bool[]>(field, store);
            for (int i = 2; i < buf.Length; i++)
                Assert.False(buf[i], $"Reset(2) left a stale enabled bit at buffer index {i}.");

            store.Reset(4); // regrow live range, then wipe-on-Clear
            store.Clear();
            buf = ReflectionProbe.Read<bool[]>(field, store);
            for (int i = 0; i < buf.Length; i++)
                Assert.False(buf[i], $"Clear() left a stale enabled bit at buffer index {i}.");
        }

        // ── Director-level reachability regression: a tick INSIDE the reset window ─────────────

        [Fact]
        public void ResetWindowTick_DoesNotFireStaleTriggers_IntoTheClearedWorld()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;

            // Pre-reset: ONE live Player1 unit, so the probe's "unit_count(P1) <= 0" event can never match
            // while the scenario is legitimately running.
            host.World.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                              Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(3));
            host.ScenarioDirector.LoadScenario(BuildStaleProbeScenario());

            Fixed orePre = host.Resources.Ore[(int)Faction.Player1];
            for (int i = 0; i < 5; i++) host.StepOnce();

            // TEETH #1: the probe must NOT have fired pre-reset — RunOnce would latch it off and turn the
            // window assertion below vacuous.
            Assert.Equal(orePre, host.Resources.Ore[(int)Faction.Player1]);

            // ── The reset window: world emptied, store Count → 0, director exec bookkeeping SURVIVES. ──
            host.ClearForReset();
            Assert.Equal(0, host.TriggerEnabled.Count);
            Fixed oreWindow = host.Resources.Ore[(int)Faction.Player1]; // the Clear() starting-ore reseed

            for (int i = 0; i < 3; i++) host.StepOnce(); // ticks INSIDE the window

            // THE DW-197 REGRESSION: with the world cleared, unit_count(P1) == 0 matches "<= 0" on every
            // window tick, and the ONLY remaining sweep gate for the stale exec is IsEnabled(0) against a
            // Count of 0. Out-of-range true fires the stale trigger into the cleared world (+100 ore);
            // bounded-false quiesces it.
            Assert.Equal(oreWindow, host.Resources.Ore[(int)Faction.Player1]);

            // TEETH #2 (fixture liveness + the re-apply restores normal behavior): after the re-apply's
            // LoadScenario the SAME event definition matches legitimately (the world is genuinely empty now),
            // so the RunOnce probe fires exactly once. A mistyped fixture would fail here, proving the window
            // assertion above was not green-by-inertness.
            host.ScenarioDirector.LoadScenario(BuildStaleProbeScenario());
            for (int i = 0; i < 3; i++) host.StepOnce();
            Assert.Equal(oreWindow + Fixed.FromInt(100), host.Resources.Ore[(int)Faction.Player1]);
        }

        /// <summary>One RunOnce trigger: <c>unit_count_threshold</c> (Player1 slot, <c>count &lt;= 0</c>) →
        /// <c>add_resources</c> +100 ore to Player1 — the durable, store-observable probe action.</summary>
        private static ScenarioData BuildStaleProbeScenario()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "StaleProbe", RunOnce = true });
            g.Nodes.Add(new EventNode  { Id = 1, Kind = "unit_count_threshold", Faction = 0, Count = 0, Operator = "<=" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "add_resources", Faction = 0, Amount = Fixed.FromInt(100) });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
        }
    }
}
