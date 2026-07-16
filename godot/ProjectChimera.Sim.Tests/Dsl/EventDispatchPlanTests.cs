#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.5 — the SHARED load-time dispatch analysis (<see cref="EventDispatchPlan"/>, run at both the
    /// validator gate and the LoadScenario backstop): the same-tick DAG proof (cycles located and NAMED; diamonds
    /// legal), the EventBounds cap rejects (each naming its constant), the closed-registry rules, the raise-arg
    /// edge shape, and the corpus-validation fixture proving a WC3-class scenario (a Glut-shaped on-death cascade
    /// + a deep-but-legal module chain) loads under the SHIPPED cap values (caps are corpus-validated dials).
    /// </summary>
    public class EventDispatchPlanTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(StringComparer.Ordinal);

        private static ScenarioCustomEvent Ev(string name, ScenarioEventParam[]? ps = null, int[]? raisers = null) =>
            new() { Name = name, Params = ps, AllowedRaisers = raisers };

        private static ScenarioEventParam P(string name, DslValueType type) => new() { Name = name, Type = type };

        /// <summary>Add a trigger subscribed to <paramref name="subscribeTo"/> (a declared custom event, or a
        /// built-in kind when <paramref name="isCustom"/> is false) whose action chain raises the given targets.</summary>
        private static void AddTrigger(TriggerGraph g, ref int id, string subscribeTo, bool isCustom,
            params (string Target, bool NextTick)[] raises)
        {
            int t = id++;
            g.Nodes.Add(new TriggerNode { Id = t, Name = $"t{t}" });
            int e = id++;
            g.Nodes.Add(isCustom
                ? new EventNode { Id = e, Kind = "custom_event", EventName = subscribeTo }
                : new EventNode { Id = e, Kind = subscribeTo });
            g.ExecEdges.Add(new ExecEdge(e, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
            int prev = t, prevPort = TriggerGraph.TriggerExecOutPort;
            foreach ((string target, bool nextTick) in raises)
            {
                int r = id++;
                g.Nodes.Add(new RaiseEventNode { Id = r, Name = target, NextTick = nextTick });
                g.ExecEdges.Add(new ExecEdge(prev, prevPort, r, TriggerGraph.ActionExecInPort));
                prev = r;
                prevPort = TriggerGraph.ActionExecOutPort;
            }
        }

        private static (bool Ok, string? Error, EventDispatchPlan? Plan) Build(
            TriggerGraph g, params ScenarioCustomEvent[] events)
        {
            bool ok = EventDispatchPlan.TryBuild(events, g, g.BuildExecutionOrder(), NoVars,
                maxRaiserSlotExclusive: 4, out EventDispatchPlan? plan, out string? error);
            return (ok, error, plan);
        }

        // ── DAG proof ────────────────────────────────────────────────────────────

        [Fact]
        public void SameTickCycle_IsRejected_NamingTheCyclePath()
        {
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "e1", isCustom: true, ("e2", false));
            AddTrigger(g, ref id, "e2", isCustom: true, ("e1", false));
            (bool ok, string? error, _) = Build(g, Ev("e1"), Ev("e2"));
            Assert.False(ok);
            Assert.Contains("cycle", error!);
            Assert.Contains("e1→e2→e1", error!);
        }

        [Fact]
        public void NextTickEdge_BreaksTheCycle_AndIsAccepted()
        {
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "e1", isCustom: true, ("e2", false));
            AddTrigger(g, ref id, "e2", isCustom: true, ("e1", true)); // the sanctioned feedback channel
            (bool ok, string? error, _) = Build(g, Ev("e1"), Ev("e2"));
            Assert.True(ok, error);
        }

        [Fact]
        public void Diamond_IsLegal()
        {
            // e1 fans to e2 AND e3 (two handlers), both re-converge on e4 — shared descent, no cycle.
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "e1", isCustom: true, ("e2", false));
            AddTrigger(g, ref id, "e1", isCustom: true, ("e3", false));
            AddTrigger(g, ref id, "e2", isCustom: true, ("e4", false));
            AddTrigger(g, ref id, "e3", isCustom: true, ("e4", false));
            AddTrigger(g, ref id, "e4", isCustom: true);
            (bool ok, string? error, _) = Build(g, Ev("e1"), Ev("e2"), Ev("e3"), Ev("e4"));
            Assert.True(ok, error);
        }

        [Fact]
        public void SelfCycle_IsRejected()
        {
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "e1", isCustom: true, ("e1", false));
            (bool ok, string? error, _) = Build(g, Ev("e1"));
            Assert.False(ok);
            Assert.Contains("e1→e1", error!);
        }

        // ── Cap rejects (each names its EventBounds constant) ───────────────────

        [Fact]
        public void FanOutOverCap_IsRejected_NamingMaxEventFanOut()
        {
            var g = new TriggerGraph();
            int id = 0;
            for (int i = 0; i <= EventBounds.MaxEventFanOut; i++) // cap + 1 subscribers
                AddTrigger(g, ref id, "hub", isCustom: true);
            (bool ok, string? error, _) = Build(g, Ev("hub"));
            Assert.False(ok);
            Assert.Contains("MaxEventFanOut", error!);
        }

        [Fact]
        public void DepthOverCap_IsRejected_NamingMaxEventCascadeDepth()
        {
            var g = new TriggerGraph();
            int id = 0;
            int n = EventBounds.MaxEventCascadeDepth + 1; // a 9-event same-tick chain
            var events = new List<ScenarioCustomEvent>();
            for (int i = 0; i < n; i++) events.Add(Ev($"m{i}"));
            for (int i = 0; i < n - 1; i++)
                AddTrigger(g, ref id, $"m{i}", isCustom: true, ($"m{i + 1}", false));
            AddTrigger(g, ref id, $"m{n - 1}", isCustom: true);
            (bool ok, string? error, _) = Build(g, events.ToArray());
            Assert.False(ok);
            Assert.Contains("MaxEventCascadeDepth", error!);
        }

        [Fact]
        public void TransitiveOpsOverCap_IsRejected_NamingMaxCascadeOps()
        {
            // Two subscribers per level, each raising the next level: depth 8 (legal) but ops double per level —
            // ops(l0) = 2·(1 + ops(l1)) … = 510 > MaxCascadeOps=256. Fan-out 2 and depth 8 both pass; only the
            // memoized transitive-cost estimator catches the exponential web.
            var g = new TriggerGraph();
            int id = 0;
            int n = EventBounds.MaxEventCascadeDepth; // 8 levels
            var events = new List<ScenarioCustomEvent>();
            for (int i = 0; i < n; i++) events.Add(Ev($"l{i}"));
            for (int i = 0; i < n - 1; i++)
            {
                AddTrigger(g, ref id, $"l{i}", isCustom: true, ($"l{i + 1}", false));
                AddTrigger(g, ref id, $"l{i}", isCustom: true, ($"l{i + 1}", false));
            }
            AddTrigger(g, ref id, $"l{n - 1}", isCustom: true);
            AddTrigger(g, ref id, $"l{n - 1}", isCustom: true);
            (bool ok, string? error, _) = Build(g, events.ToArray());
            Assert.False(ok);
            Assert.Contains("MaxCascadeOps", error!);
        }

        // ── Corpus validation (the caps-are-corpus-validated gate) ──────────────

        [Fact]
        public void Wc3ClassFixture_GlutCascadePlusDeepLegalChain_LoadsUnderShippedCaps()
        {
            // The Sanguine Court "Glut" seam shape: an on-death (built-in) trigger raises glut_stack; a handler
            // gates/accumulates and raises glut_bonus; a bonus handler applies it — a 3-deep on-death cascade.
            // PLUS a deep-but-legal module chain at exactly MaxEventCascadeDepth, single subscriber per link.
            var g = new TriggerGraph();
            int id = 0;
            var events = new List<ScenarioCustomEvent>
            {
                Ev("glut_stack", new[] { P("victim_ref", DslValueType.Int), P("amount", DslValueType.Int) }),
                Ev("glut_bonus", new[] { P("amount", DslValueType.Int) }),
            };

            // unit_dies → raise glut_stack(event.victim, 1)
            {
                int t = id++;
                g.Nodes.Add(new TriggerNode { Id = t, Name = "onDeath" });
                int e = id++;
                g.Nodes.Add(new EventNode { Id = e, Kind = "unit_dies", Faction = 0 });
                g.ExecEdges.Add(new ExecEdge(e, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
                int r = id++;
                g.Nodes.Add(new RaiseEventNode { Id = r, Name = "glut_stack" });
                g.ExecEdges.Add(new ExecEdge(t, TriggerGraph.TriggerExecOutPort, r, TriggerGraph.ActionExecInPort));
                (int a0, _) = ExprParser.Parse("event.victim", g, NoVars, EventDispatchPlan.UnitDiesParams);
                g.DataEdges.Add(new DataEdge(a0, TriggerGraph.ExprDataOutPort, r, TriggerGraph.RaiseArgInPort0, DataWireType.Int));
                (int a1, _) = ExprParser.Parse("1", g, NoVars);
                g.DataEdges.Add(new DataEdge(a1, TriggerGraph.ExprDataOutPort, r, TriggerGraph.RaiseArgInPort1, DataWireType.Int));
                id = NextId(g);
            }
            // glut_stack handler → raise glut_bonus(event.amount * 2)
            {
                int t = id++;
                g.Nodes.Add(new TriggerNode { Id = t, Name = "onStack" });
                int e = id++;
                g.Nodes.Add(new EventNode { Id = e, Kind = "custom_event", EventName = "glut_stack" });
                g.ExecEdges.Add(new ExecEdge(e, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
                int r = id++;
                g.Nodes.Add(new RaiseEventNode { Id = r, Name = "glut_bonus" });
                g.ExecEdges.Add(new ExecEdge(t, TriggerGraph.TriggerExecOutPort, r, TriggerGraph.ActionExecInPort));
                var stackParams = EventDispatchPlan.ParamMapOf(events[0]);
                (int a0, _) = ExprParser.Parse("event.amount * 2", g, NoVars, stackParams);
                g.DataEdges.Add(new DataEdge(a0, TriggerGraph.ExprDataOutPort, r, TriggerGraph.RaiseArgInPort0, DataWireType.Int));
                id = NextId(g);
            }
            // glut_bonus handler (terminal).
            AddTrigger(g, ref id, "glut_bonus", isCustom: true);

            // The deep-but-legal module chain: exactly MaxEventCascadeDepth events, one subscriber per link.
            for (int i = 0; i < EventBounds.MaxEventCascadeDepth; i++) events.Add(Ev($"mod{i}"));
            for (int i = 0; i < EventBounds.MaxEventCascadeDepth - 1; i++)
                AddTrigger(g, ref id, $"mod{i}", isCustom: true, ($"mod{i + 1}", false));
            AddTrigger(g, ref id, $"mod{EventBounds.MaxEventCascadeDepth - 1}", isCustom: true);

            (bool ok, string? error, EventDispatchPlan? plan) = Build(g, events.ToArray());
            Assert.True(ok, error);
            Assert.NotNull(plan);
        }

        private static int NextId(TriggerGraph g)
        {
            int next = 0;
            foreach (NodeBase n in g.Nodes)
                if (n.Id + 1 > next) next = n.Id + 1;
            return next;
        }

        // ── Registry rejects ─────────────────────────────────────────────────────

        [Fact]
        public void RegistryOverMaxCustomEvents_IsRejected_NamingTheConstant()
        {
            var events = new List<ScenarioCustomEvent>();
            for (int i = 0; i <= EventBounds.MaxCustomEvents; i++) events.Add(Ev($"e{i}"));
            string? err = EventDispatchPlan.ValidateRegistry(events, 4);
            Assert.NotNull(err);
            Assert.Contains("MaxCustomEvents", err!);
        }

        [Fact]
        public void RegistryOverMaxEventParams_IsRejected_NamingTheConstant()
        {
            var ps = new List<ScenarioEventParam>();
            for (int i = 0; i <= EventBounds.MaxEventParams; i++) ps.Add(P($"p{i}", DslValueType.Int));
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev("e", ps.ToArray()) }, 4);
            Assert.NotNull(err);
            Assert.Contains("MaxEventParams", err!);
        }

        [Theory]
        [InlineData("", "non-empty")]                    // blank name
        [InlineData("unit_dies", "shadows")]             // built-in shadow
        [InlineData("custom_event", "shadows")]          // the graph kind itself
        public void RegistryBadName_IsRejected(string name, string expect)
        {
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev(name) }, 4);
            Assert.NotNull(err);
            Assert.Contains(expect, err!);
        }

        [Fact]
        public void RegistryDuplicateName_IsRejected()
        {
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev("dup"), Ev("dup") }, 4);
            Assert.NotNull(err);
            Assert.Contains("duplicate", err!);
        }

        [Theory]
        [InlineData(DslValueType.EntityRef)]
        [InlineData(DslValueType.Point)]
        [InlineData(DslValueType.Array)]
        public void RegistryNonScalarParamType_IsRejected(DslValueType bad)
        {
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev("e", new[] { P("p", bad) }) }, 4);
            Assert.NotNull(err);
            Assert.Contains("Int/Fixed/Bool", err!);
        }

        [Fact]
        public void RegistryNonIdentifierParamName_IsRejected()
        {
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev("e", new[] { P("1bad", DslValueType.Int) }) }, 4);
            Assert.NotNull(err);
            Assert.Contains("identifier", err!);
        }

        [Theory]
        [InlineData(new[] { 4 })]     // over the engine ceiling (exclusive bound 4)
        [InlineData(new[] { -1 })]    // negative
        [InlineData(new[] { 1, 1 })]  // duplicate
        public void RegistryBadAllowedRaisers_AreRejected(int[] raisers)
        {
            string? err = EventDispatchPlan.ValidateRegistry(new[] { Ev("e", null, raisers) }, 4);
            Assert.NotNull(err);
        }

        // ── Graph-usage rejects ──────────────────────────────────────────────────

        [Fact]
        public void RaiseOfUndeclaredEvent_IsRejected()
        {
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "match_start", isCustom: false, ("ghost", false));
            (bool ok, string? error, _) = Build(g /* no declared events */);
            Assert.False(ok);
            Assert.Contains("ghost", error!);
            Assert.Contains("not a declared custom event", error!);
        }

        [Fact]
        public void SubscriptionToUndeclaredEvent_IsRejected()
        {
            var g = new TriggerGraph();
            int id = 0;
            AddTrigger(g, ref id, "ghost", isCustom: true);
            (bool ok, string? error, _) = Build(g);
            Assert.False(ok);
            Assert.Contains("ghost", error!);
        }

        [Fact]
        public void RaiserOutsideAllowedRaisers_IsRejected_AndSystemIsAlwaysLegal()
        {
            var g = new TriggerGraph();
            int id = 0;
            int t = id++;
            g.Nodes.Add(new TriggerNode { Id = t, Name = "t" });
            int e = id++;
            g.Nodes.Add(new EventNode { Id = e, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(e, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
            int r = id++;
            g.Nodes.Add(new RaiseEventNode { Id = r, Name = "ev", Raiser = 2 }); // 2 ∉ {0, 1}
            g.ExecEdges.Add(new ExecEdge(t, TriggerGraph.TriggerExecOutPort, r, TriggerGraph.ActionExecInPort));

            (bool ok, string? error, _) = Build(g, Ev("ev", null, new[] { 0, 1 }));
            Assert.False(ok);
            Assert.Contains("allowed_raisers", error!);

            // -1 (system) is always legal.
            ((RaiseEventNode)g.Nodes[2]).Raiser = -1;
            (ok, error, _) = Build(g, Ev("ev", null, new[] { 0, 1 }));
            Assert.True(ok, error);
        }

        [Fact]
        public void CustomSubscriberWithASecondEventNode_IsRejected_SingleSubscriptionRule()
        {
            var g = new TriggerGraph();
            int id = 0;
            int t = id++;
            g.Nodes.Add(new TriggerNode { Id = t, Name = "multi" });
            int e1 = id++;
            g.Nodes.Add(new EventNode { Id = e1, Kind = "custom_event", EventName = "ev" });
            g.ExecEdges.Add(new ExecEdge(e1, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
            int e2 = id++;
            g.Nodes.Add(new EventNode { Id = e2, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(e2, TriggerGraph.EventExecOutPort, t, TriggerGraph.TriggerEventInPort));
            (bool ok, string? error, _) = Build(g, Ev("ev"));
            Assert.False(ok);
            Assert.Contains("exactly one event node", error!);
        }

        [Fact]
        public void RaiseArgEdgeShape_MissingForkedExtraAndMistyped_AreRejected()
        {
            ScenarioCustomEvent ev = Ev("typed", new[] { P("count", DslValueType.Int) });

            // Missing arg edge.
            {
                var g = new TriggerGraph();
                int id = 0;
                AddTrigger(g, ref id, "match_start", isCustom: false, ("typed", false));
                (bool ok, string? error, _) = Build(g, ev);
                Assert.False(ok);
                Assert.Contains("no argument edge", error!);
            }
            // Extra port (an edge into a port ≥ declared count).
            {
                var g = new TriggerGraph();
                int id = 0;
                AddTrigger(g, ref id, "match_start", isCustom: false, ("typed", false));
                int raiseId = 2; // trigger 0, event 1, raise 2 (AddTrigger id layout)
                (int a0, _) = ExprParser.Parse("1", g, NoVars);
                g.DataEdges.Add(new DataEdge(a0, TriggerGraph.ExprDataOutPort, raiseId, TriggerGraph.RaiseArgInPort0, DataWireType.Int));
                (int a1, _) = ExprParser.Parse("2", g, NoVars);
                g.DataEdges.Add(new DataEdge(a1, TriggerGraph.ExprDataOutPort, raiseId, TriggerGraph.RaiseArgInPort1, DataWireType.Int));
                (bool ok, string? error, _) = Build(g, ev);
                Assert.False(ok);
                Assert.Contains("port 1", error!);
            }
            // Forked (two edges into the same declared port).
            {
                var g = new TriggerGraph();
                int id = 0;
                AddTrigger(g, ref id, "match_start", isCustom: false, ("typed", false));
                int raiseId = 2;
                (int a0, _) = ExprParser.Parse("1", g, NoVars);
                g.DataEdges.Add(new DataEdge(a0, TriggerGraph.ExprDataOutPort, raiseId, TriggerGraph.RaiseArgInPort0, DataWireType.Int));
                (int a1, _) = ExprParser.Parse("2", g, NoVars);
                g.DataEdges.Add(new DataEdge(a1, TriggerGraph.ExprDataOutPort, raiseId, TriggerGraph.RaiseArgInPort0, DataWireType.Int));
                (bool ok, string? error, _) = Build(g, ev);
                Assert.False(ok);
                Assert.Contains("forked", error!);
            }
            // Mistyped (a Bool expression into an Int-declared param — the wire mismatch rejects located).
            {
                var g = new TriggerGraph();
                int id = 0;
                AddTrigger(g, ref id, "match_start", isCustom: false, ("typed", false));
                int raiseId = 2;
                (int a0, _) = ExprParser.Parse("true", g, NoVars);
                g.DataEdges.Add(new DataEdge(a0, TriggerGraph.ExprDataOutPort, raiseId, TriggerGraph.RaiseArgInPort0, DataWireType.Boolean));
                (bool ok, string? error, _) = Build(g, ev);
                Assert.False(ok);
                Assert.Contains("wire", error!);
            }
        }
    }
}
