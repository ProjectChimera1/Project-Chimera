#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-191 — both load-time cycle-DFS sites (<c>DslLoopGate.RunCycleDfs</c> and
    /// <c>EventDispatchPlan.CycleDfs</c>) are EXPLICIT-STACK iterative walks. The recursive forms burned one stack
    /// frame per chain link, and the run_trigger target graph has NO structural cap on trigger count — so a very
    /// long *acyclic* run_trigger chain (pathological but valid creator content) stack-overflowed the load gate
    /// before it could say "valid". Pre-fix, the deep-chain tests here do not fail an assert — they KILL the test
    /// host with an uncatchable StackOverflow; post-fix they complete in milliseconds. The event-side tests pin
    /// the mirrored walk at the deepest depth reachable through the closed registry (its DFS was already bounded
    /// by <see cref="EventBounds.MaxCustomEvents"/> — they are order/message parity pins, not overflow repros).
    /// </summary>
    public class DslCycleDfsIterativeTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> NoArrays =
            new(StringComparer.Ordinal);

        // ── DslLoopGate.RunCycleDfs — the genuinely unbounded site ──────────────

        /// <summary>Far past any 1 MB thread stack budget: the recursive RunCycleDfs burned one frame per chain
        /// link (~100+ bytes each), so this depth dies pre-fix as a testhost crash, not an assert failure.</summary>
        private const int DeepChain = 60_000;

        /// <summary>Build a run_trigger chain T0→T1→…→T(n-1) as a graph plus a hand-built exec view (the exact
        /// shape <c>TriggerGraph.BuildExecutionOrder</c> emits for it — built directly because that method's
        /// per-trigger edge scans are O(triggers·edges), needlessly slow at this n; <c>DslLoopGate.CheckGraph</c>
        /// is the REAL shared load-gate entry either way). When <paramref name="tailSelfCycle"/> is set, T(n-1)
        /// additionally runs ITSELF, so the only cycle sits at the far end of the deep walk.</summary>
        private static (TriggerGraph Graph, List<TriggerGraph.TriggerExec> Execs) BuildRunChain(int n, bool tailSelfCycle)
        {
            var g = new TriggerGraph();
            var execs = new List<TriggerGraph.TriggerExec>(n);
            var triggers = new TriggerNode[n];
            for (int i = 0; i < n; i++)
            {
                triggers[i] = new TriggerNode { Id = i, Name = "T" + i };
                g.Nodes.Add(triggers[i]);
            }
            for (int i = 0; i < n; i++)
            {
                TriggerGraph.ExecItem[] items;
                if (i < n - 1 || tailSelfCycle)
                {
                    int target = i < n - 1 ? i + 1 : n - 1; // the tail (when cycling) targets itself
                    var run = new RunTriggerNode { Id = n + i, TargetTriggerId = target };
                    g.Nodes.Add(run);
                    items = new[] { new TriggerGraph.ExecItem { Node = run } };
                }
                else
                {
                    items = Array.Empty<TriggerGraph.ExecItem>();
                }
                execs.Add(new TriggerGraph.TriggerExec { Trigger = triggers[i], Items = items });
            }
            return (g, execs);
        }

        private static string? CheckRunChain(int n, bool tailSelfCycle)
        {
            (TriggerGraph g, List<TriggerGraph.TriggerExec> execs) = BuildRunChain(n, tailSelfCycle);
            return DslLoopGate.CheckGraph(g, execs, NoVars, NoArrays, _ => false);
        }

        [Fact]
        public void RunTriggerGate_DeepAcyclicChain_ValidatesWithoutStackOverflow()
        {
            // A long acyclic chain is VALID content (the runtime seatbelt MaxRunTriggerDepth caps execution, not
            // authoring) — the load gate must return "no error", not blow the stack proving it.
            Assert.Null(CheckRunChain(DeepChain, tailSelfCycle: false));
        }

        [Fact]
        public void RunTriggerGate_CycleAtTheEndOfADeepChain_IsStillLocatedAndNamed()
        {
            string? err = CheckRunChain(DeepChain, tailSelfCycle: true);
            Assert.NotNull(err);
            Assert.Contains("run_trigger cycle", err!);
            Assert.Contains($"'T{DeepChain - 1}'", err!); // the tail self-cycle is named — the walk reached full depth
        }

        [Fact]
        public void RunTriggerGate_TailSelfCycle_KeepsTheRecursiveFormsExactMessage()
        {
            // Message/order-parity pin: the iterative walk must keep the recursive form's child order and cycle
            // naming byte-for-byte. A short chain keeps the expected literal readable.
            string? err = CheckRunChain(3, tailSelfCycle: true);
            Assert.NotNull(err);
            Assert.Contains("run_trigger cycle: 'T2' (node 2)→'T2' (node 2)", err!);
        }

        // ── EventDispatchPlan.CycleDfs — the mirrored (registry-bounded) site ───

        private static ScenarioCustomEvent Ev(string name) => new() { Name = name };

        /// <summary>Add a trigger subscribed to custom event <paramref name="subscribeTo"/> whose action chain
        /// same-tick-raises <paramref name="raises"/> (mirrors the EventDispatchPlanTests helper).</summary>
        private static void AddTrigger(TriggerGraph g, ref int id, string subscribeTo, params string[] raises)
        {
            int t = id++;
            g.Nodes.Add(new TriggerNode { Id = t, Name = $"t{t}" });
            int e = id++;
            g.Nodes.Add(new EventNode { Id = e, Kind = "custom_event", EventName = subscribeTo });
            g.ExecEdges.Add(new ExecEdge(e, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
            int prev = t, prevPort = TriggerGraph.TriggerExecOutPort;
            foreach (string target in raises)
            {
                int r = id++;
                g.Nodes.Add(new RaiseEventNode { Id = r, Name = target, NextTick = false });
                g.ExecEdges.Add(new ExecEdge(prev, prevPort, r, TriggerGraph.ActionExecInPort));
                prev = r;
                prevPort = TriggerGraph.ActionExecOutPort;
            }
        }

        /// <summary>A same-tick chain e0→e1→…→e(n-1) across the FULL closed registry; when
        /// <paramref name="tailSelfCycle"/> is set the deepest event's subscriber also re-raises its own event.</summary>
        private static (bool Ok, string? Error) BuildEventChain(int n, bool tailSelfCycle)
        {
            var g = new TriggerGraph();
            int id = 0;
            var events = new List<ScenarioCustomEvent>(n);
            for (int i = 0; i < n; i++) events.Add(Ev("e" + i));
            for (int i = 0; i < n - 1; i++)
                AddTrigger(g, ref id, "e" + i, "e" + (i + 1));
            if (tailSelfCycle)
                AddTrigger(g, ref id, "e" + (n - 1), "e" + (n - 1));
            else
                AddTrigger(g, ref id, "e" + (n - 1));
            bool ok = EventDispatchPlan.TryBuild(events, g, g.BuildExecutionOrder(), NoVars,
                arrayDecls: null, maxRaiserSlotExclusive: 4, out _, out string? error);
            return (ok, error);
        }

        [Fact]
        public void EventGate_MaxRegistryDepthChain_WalksFullyAndRejectsOnTheDepthCapNotTheWalk()
        {
            // The deepest same-tick chain the closed registry admits (MaxCustomEvents events). The cycle DFS must
            // walk all of it and find NO cycle; the reject must then be the CASCADE-DEPTH cap — proving the
            // iterative walk completes at the maximum reachable depth and the failure is the intended dial.
            (bool ok, string? error) = BuildEventChain(EventBounds.MaxCustomEvents, tailSelfCycle: false);
            Assert.False(ok);
            Assert.Contains("MaxEventCascadeDepth", error!);
            Assert.DoesNotContain("cycle:", error!);
        }

        [Fact]
        public void EventGate_SelfCycleAtMaxRegistryDepth_IsStillNamed()
        {
            // A self-edge on the DEEPEST event: the walk must reach it through the whole chain and name it exactly
            // as the recursive form did (the cycle check runs before the depth cap, so this is the surfaced error).
            int n = EventBounds.MaxCustomEvents;
            (bool ok, string? error) = BuildEventChain(n, tailSelfCycle: true);
            Assert.False(ok);
            Assert.Contains("same-tick event dispatch cycle", error!);
            Assert.Contains($"e{n - 1}→e{n - 1}", error!);
        }
    }
}
