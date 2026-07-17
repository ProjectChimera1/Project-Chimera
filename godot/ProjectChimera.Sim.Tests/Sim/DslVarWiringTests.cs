#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;   // ApplyModifierEffect / Modifier / StackRule / StatusFlags / DirectHpDeltaEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.3 (review follow-up) — teeth for the PRODUCTION host wiring the unit suite could not see:
    ///
    /// 1. <c>SimulationHost</c> passes <c>Vars</c> through <c>SimulationLoop.EnableChecksums</c> into the per-tick
    ///    <c>SimChecksum.Compute</c>. Because null ≡ empty by design, deleting the <c>, Vars</c> argument compiled
    ///    and passed EVERY prior test (goldens carry empty tables; the coverage guard calls Compute directly) while
    ///    silently reopening the exact variable/timer silent-desync hole this story closes. These tests assert at
    ///    the LOOP-EMITTED checksum surface, so that one-argument regression turns RED.
    /// 2. <c>SimulationHost</c> wires the run_effect sinks via <c>ScenarioDirector.SetEffectRuntime</c>. Deleting
    ///    that call also compiled and passed everything (unit tests use only the sink-less DirectHpDeltaEffect); in
    ///    production a trigger-embedded apply_modifier would then throw mid-tick. The host-level apply_modifier test
    ///    makes that regression RED too.
    /// </summary>
    public class DslVarWiringTests
    {
        private const int TICKS = 3;

        /// <summary>A host stepping a scenario whose match_start trigger runs <paramref name="action"/> once.</summary>
        private static SimulationHost HostWithMatchStartAction(TriggerAction action)
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            host.ScenarioDirector.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "t", RunOnce = true,
                        Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Actions = new[] { action },
                    },
                },
            });
            return host;
        }

        private static List<uint> ChecksumSequence(SimulationHost host, int ticks)
        {
            var seq = new List<uint>(ticks);
            host.SetChecksumSink((_, h) => seq.Add(h));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        [Fact]
        public void VariableWrite_MovesTheLoopEmittedChecksum()
        {
            // Identical hosts except for the fired action: a set_variable (mutates the folded DslVarTable) versus a
            // display_message (checksum-neutral). The LOOP-emitted checksums must diverge — this is only true when
            // the host actually handed Vars to EnableChecksums (null ≡ empty masks the unwired case everywhere else).
            var varHost = HostWithMatchStartAction(new TriggerAction { Type = "set_variable", Variable = "v", Value = 7 });
            var msgHost = HostWithMatchStartAction(new TriggerAction { Type = "display_message", Text = "m" });

            List<uint> withWrite = ChecksumSequence(varHost, TICKS);
            List<uint> without   = ChecksumSequence(msgHost, TICKS);
            Assert.NotEqual(withWrite, without);
        }

        [Fact]
        public void IdenticalScenarios_ProduceIdenticalLoopChecksumSequences()
        {
            // The I/O-matrix determinism row at HOST altitude: two fresh runs of the same scenario with live
            // variable state produce a byte-identical loop-emitted SimChecksum sequence.
            var a = HostWithMatchStartAction(new TriggerAction { Type = "set_variable", Variable = "v", Value = 7 });
            var b = HostWithMatchStartAction(new TriggerAction { Type = "set_variable", Variable = "v", Value = 7 });
            Assert.Equal(ChecksumSequence(a, TICKS), ChecksumSequence(b, TICKS));
        }

        [Fact]
        public void DeclaredTimerCountdown_MovesTheLoopEmittedChecksum()
        {
            // A declared timer's remaining-ticks decrement each tick, so consecutive loop-emitted checksums must
            // differ even in an otherwise-inert world — timer state escaping the fold was the other half of the
            // silent-desync hole.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            host.ScenarioDirector.LoadScenario(new ScenarioData
            {
                Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(10) } },
                Triggers = new[]
                {
                    new TriggerDefinition // a trigger so the director tick runs its full path
                    {
                        Name = "noop",
                        Events  = new[] { new TriggerEvent { Type = "timer_expires", TimerName = "clock" } },
                        Actions = new[] { new TriggerAction { Type = "display_message", Text = "x" } },
                    },
                },
            });
            List<uint> seq = ChecksumSequence(host, 2);
            Assert.Equal(2, seq.Count);
            Assert.NotEqual(seq[0], seq[1]); // remaining-ticks (300→299, then 299→298) fold distinctly
        }

        [Fact]
        public void RunEffectApplyModifier_LandsThroughTheHostSinkWiring()
        {
            // A trigger-embedded apply_modifier needs the ModifierStore sink SetEffectRuntime injects — without the
            // host's wiring call this throws mid-tick (ApplyModifierEffect fails on a null store). Asserting the
            // modifier actually LANDED in host.Modifiers proves the production sink wiring end-to-end.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            int e = host.World.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            host.World.EffectiveMaxHealth[e] = Fixed.FromInt(100);

            var buff = new Modifier(9001, durationTicks: 150, StackRule.Refresh, maxStacks: 1,
                                    maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(5),
                                    moveSpeedDelta: Fixed.Zero, StatusFlags.None, periodEffect: null, periodTicks: 0);
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("buff", "match_start", new ApplyModifierEffect(buff));
            host.ScenarioDirector.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });

            host.StepOnce(); // match_start fires the run_effect; anchor = lowest-id alive entity = e
            Assert.Equal(1, host.Modifiers.CountAt(e)); // the modifier landed through the injected store
        }

        // ── Story 7.6 (review P1) — the SAME two wiring links for DslLoopState. Every 7.6 determinism test
        //    constructs ScenarioDirector directly and the golden harness runs an empty scenario, so deleting
        //    EITHER the `LoopState` ctor argument in SimulationHost's director construction OR the `LoopState`
        //    EnableChecksums argument compiled and passed everything (null ≡ empty folds). These tests assert at
        //    the LOOP-EMITTED checksum surface with a live loop state, so both one-argument regressions go RED. ──

        /// <summary>A host with <paramref name="units"/> P1 units and the given scenario, checksums every tick.</summary>
        private static SimulationHost HostWithUnitsAndScenario(int units, ScenarioData scenario)
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            for (int i = 0; i < units; i++)
                host.World.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                    Faction.Player1, Fixed.FromInt(100), Fixed.One);
            host.ScenarioDirector.LoadScenario(scenario);
            return host;
        }

        /// <summary>match_start → for_each_batched(P1, batch 1) [body: display_message] — checksum-neutral for
        /// world/vars (the message is presentation-only), so ONLY the DslLoopState fold (a configured row, the
        /// active drain, the consumed fuel) distinguishes it from an empty scenario.</summary>
        private static ScenarioData BatchedNeutralScenario()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "drip" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 2, Source = "faction_units", Faction = 0, BatchSize = 1 });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "x" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            return new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
        }

        [Fact]
        public void LoopStateDivergence_MovesTheLoopEmittedChecksum_ThroughBothWiringLinks()
        {
            // Two hosts with IDENTICAL worlds and (empty) variable tables; only DslLoopState differs — the
            // batched host carries a configured continuation row, an active drain, and consumed fuel; the empty
            // host carries nothing (its state folds byte-identically to a NULL fold). The loop-emitted sequences
            // must diverge. If the director-ctor link drops, the host's LoopState stays empty and folds like the
            // empty host's; if the EnableChecksums link drops, both fold null ≡ empty — either way the sequences
            // become EQUAL and this test goes red.
            SimulationHost batched = HostWithUnitsAndScenario(3, BatchedNeutralScenario());
            SimulationHost empty   = HostWithUnitsAndScenario(3, new ScenarioData());

            List<uint> withLoop = ChecksumSequence(batched, TICKS);
            List<uint> without  = ChecksumSequence(empty, TICKS);
            Assert.NotEqual(withLoop, without);
        }

        [Fact]
        public void LiveForEachAndBatchedDrain_TwoHostRuns_AreByteIdentical()
        {
            // The spec's host-altitude two-run determinism row for live loops: a scenario carrying BOTH a live
            // (re-firing) for_each AND a for_each_batched drain spanning ≥ 3 ticks, stepped twice through the
            // full SimulationHost/SimulationLoop stack with checksums enabled → byte-identical sequences.
            static ScenarioData LiveLoopScenario()
            {
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0, Name = "live" });
                g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_count_threshold", Faction = 0, Count = 1, Operator = ">=" });
                g.Nodes.Add(new ForEachNode { Id = 2, Source = "faction_units", Faction = 0, UpTo = 64 });
                g.Nodes.Add(new EffectActionNode { Id = 3, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
                g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));

                g.Nodes.Add(new TriggerNode { Id = 10, Name = "drip" });
                g.Nodes.Add(new EventNode { Id = 11, Kind = "match_start" });
                g.Nodes.Add(new ForEachBatchedNode { Id = 12, Source = "faction_units", Faction = 0, BatchSize = 1 });
                g.Nodes.Add(new EffectActionNode { Id = 13, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
                g.ExecEdges.Add(new ExecEdge(11, TriggerGraph.EventExecOutPort, 10, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(10, TriggerGraph.TriggerExecOutPort, 12, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(12, TriggerGraph.ForEachBodyOutPort, 13, TriggerGraph.ActionExecInPort));
                return new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            }

            static List<uint> Run()
            {
                // 4 units + batch 1 → the batched drain spans 4 ticks; the for_each re-fires every tick.
                SimulationHost host = HostWithUnitsAndScenario(4, LiveLoopScenario());
                return ChecksumSequence(host, 8);
            }

            Assert.Equal(Run(), Run());
        }
    }
}
