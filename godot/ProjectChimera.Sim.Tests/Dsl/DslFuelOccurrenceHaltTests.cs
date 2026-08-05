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
    /// DW-543 + DW-545 — the two surviving fuel-halt loss classes the DW-349 re-queue rail did not close, each
    /// closed under its own recorded owner decision:
    ///
    ///   • DW-543 ("halt at occurrence granularity") — the per-occurrence base sweep checked
    ///     <c>FuelExhausted</c> only at the per-TRIGGER boundary, so a param-reading trigger that exhausted the
    ///     budget mid-loop kept dispatching its remaining matching occurrences that tick (each a full
    ///     FireTrigger), stretching the documented whole-trigger boundary across N occurrences. The check now
    ///     lives inside the occurrence loop and the unconsumed EDGE occurrences persist on the rail.
    ///
    ///   • DW-545 ("extend the re-queue rail to custom occurrences") — the custom-event drain ABANDONED every
    ///     remaining same-tick work item on exhaustion. They now persist too: the occurrence caught
    ///     mid-dispatch carries a RESUME exec so already-served subscribers cannot double-fire, and the items
    ///     the halt never started ride their plain registry index.
    ///
    /// Both fixtures pre-burn the tick budget with 63 whole-trigger burns (63 × 257 = 16191 of
    /// <see cref="DslBounds.MaxDslOpsPerTick"/> = 16384 — the DslFuelTests cost model), so the halt lands on a
    /// deterministic, hand-computable dispatch rather than on a timing coincidence.
    /// </summary>
    public class DslFuelOccurrenceHaltTests
    {
        // ── Fixture helpers ─────────────────────────────────────────────────────

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global };

        private static Dictionary<string, (DslValueType Type, VarScope Scope)> DeclMap(ScenarioVariable[] vars)
        {
            var map = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            foreach (ScenarioVariable v in vars) map[v.Name] = (v.Type, v.Scope);
            return map;
        }

        /// <summary>
        /// <paramref name="count"/> burn triggers keyed on <c>unit_count_threshold(P2 ≥ 1)</c> — a switch the test
        /// turns OFF by killing the last Player2 unit — each running <c>for_each faction_units(P1, up_to 64)</c>
        /// with a one-action body: 1 + 64 × (1 action + 3 expr ops) = 257 ops per fire (the DslFuelTests model).
        /// </summary>
        private static TriggerGraph BurnTriggers(int count,
            Dictionary<string, (DslValueType Type, VarScope Scope)> decls)
        {
            var g = new TriggerGraph();
            int id = 0;
            var actionIds = new List<int>(count);
            for (int t = 0; t < count; t++)
            {
                int trig = id++, ev = id++, loop = id++, act = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"burn{t}" });
                g.Nodes.Add(new EventNode { Id = ev, Kind = "unit_count_threshold", Faction = 1, Count = 1, Operator = ">=" });
                g.Nodes.Add(new ForEachNode { Id = loop, Source = "faction_units", Faction = 0, UpTo = 64 });
                g.Nodes.Add(new ActionNode { Id = act, Kind = "set_variable", Variable = "x" });
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(trig, TriggerGraph.TriggerExecOutPort, loop, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(loop, TriggerGraph.ForEachBodyOutPort, act, TriggerGraph.ActionExecInPort));
                actionIds.Add(act);
            }
            foreach (int act in actionIds)
            {
                (int root, _) = ExprParser.Parse("x + 1", g, decls);
                g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionValueInPort, DataWireType.Int));
            }
            return g;
        }

        /// <summary>One trigger costing 257 ops per dispatch (the burn body) on the given event, optionally
        /// param-reading via a <c>event.victim</c> condition — the shape that opts a trigger into the base
        /// sweep's PER-OCCURRENCE dispatch loop.</summary>
        private static TriggerGraph ExpensiveTrigger(string name, string eventKind, string? customEventName,
            string counterVar, bool paramReadingCondition,
            Dictionary<string, (DslValueType Type, VarScope Scope)> decls)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = name });
            g.Nodes.Add(customEventName != null
                ? new EventNode { Id = 1, Kind = NodeKinds.CustomEvent, EventName = customEventName }
                : new EventNode { Id = 1, Kind = eventKind, Faction = 1 });
            g.Nodes.Add(new ForEachNode { Id = 2, Source = "faction_units", Faction = 0, UpTo = 64 });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = counterVar });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            (int root, _) = ExprParser.Parse($"{counterVar} + 1", g, decls);
            g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));

            if (paramReadingCondition)
            {
                (int cRoot, DataWireType wire) = ExprParser.Parse("event.victim >= 0", g, decls,
                    eventParams: EventDispatchPlan.UnitDiesParams);
                Assert.Equal(DataWireType.Boolean, wire);
                g.DataEdges.Add(new DataEdge(cRoot, TriggerGraph.ExprDataOutPort, 0,
                    TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            }
            return g;
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslEventQueue Queue, DslLoopState Loop)
            Build(ScenarioData scenario)
        {
            var vars  = new DslVarTable();
            var queue = new DslEventQueue();
            var loop  = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop, queue);
            director.LoadScenario(scenario);
            return (director, vars, queue, loop);
        }

        /// <summary>64 Player1 units (the for_each source, so every dispatch costs the same 257 ops) plus
        /// <paramref name="p2Count"/> Player2 units (the burn switch / the death occurrences).</summary>
        private static (EntityWorld World, int[] P2) MakeWorld(int p2Count)
        {
            var world = new EntityWorld();
            for (int i = 0; i < 64; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            var p2 = new int[p2Count];
            for (int i = 0; i < p2Count; i++)
                p2[i] = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            return (world, p2);
        }

        // ── DW-543 — the per-occurrence loop halts on exhaustion and persists the remainder ─────────────────

        [Fact]
        public void ParamReadingTrigger_StopsDispatchingOccurrencesOnceTheBudgetIsSpent_AndTheRemainderPersists()
        {
            // 63 burns spend 16191 of the 16384-op budget, then P (unit_dies, param-reading, 257 ops per
            // dispatch) meets FIVE death occurrences. Occurrence 1 dispatches and tips the budget; pre-DW-543 the
            // remaining FOUR still ran (5 × 257 = 1285 ops past a ceiling documented as one whole trigger), now
            // they halt and persist on the DW-349 rail — so the tick's work is bounded AND nothing is lost.
            ScenarioVariable[] vars = { IntVar("x"), IntVar("hits") };
            var decls = DeclMap(vars);

            TriggerGraph g = BurnTriggers(63, decls);
            g.Merge(ExpensiveTrigger("P", "unit_dies", null, "hits", paramReadingCondition: true, decls));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, DslLoopState loop) =
                Build(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            (EntityWorld world, int[] p2) = MakeWorld(p2Count: 6);

            director.Tick(world, Fixed.One); // baseline: burns fire, alive-flags snapshot taken, no deaths yet
            Assert.False(loop.FuelExhausted); // 63 × 257 = 16191 < 16384 — the sweep completes
            Assert.Equal(0, table.GetInt("hits", 0));
            Assert.Equal(0, queue.Count);

            for (int i = 0; i < 5; i++) world.Destroy(p2[i]); // 5 deaths; one P2 unit stays alive → burns re-fire

            director.Tick(world, Fixed.One);
            Assert.True(loop.FuelExhausted);
            Assert.Equal(64, table.GetInt("hits", 0)); // EXACTLY ONE occurrence dispatched — pre-DW-543 all 5 ran (320)
            Assert.Equal(4, queue.Count);              // the four unconsumed deaths persisted, targeted at P

            world.Destroy(p2[5]); // the burn switch goes off → next tick has a full budget

            director.Tick(world, Fixed.One);
            Assert.False(loop.FuelExhausted);
            Assert.Equal(6 * 64, table.GetInt("hits", 0)); // 4 redelivered + the 6th (fresh) death — each exactly once
            Assert.Equal(0, queue.Count);                  // rail consumed, nothing pending
        }

        // ── DW-545 — the custom drain persists what the halt abandoned, resuming mid-occurrence ─────────────

        [Fact]
        public void CustomDrain_PersistsTheAbandonedOccurrences_AndResumesAtTheFirstUnservedSubscriber()
        {
            // 63 burns (16191) + two match_start raises (2 ops) leave 191 ops. The drain then dispatches
            // occurrence 1 to S1 (257 ops → exhausted) and halts BEFORE S2. Pre-DW-545 S2/S3 and the whole of
            // occurrence 2 were dropped forever; now occurrence 1 persists with a RESUME exec (so S1 is not
            // re-served) and occurrence 2 persists untargeted, giving every (occurrence × subscriber) dispatch
            // exactly once across the two ticks: 2 × 3 × 64 = 384.
            ScenarioVariable[] vars = { IntVar("x"), IntVar("y") };
            var decls = DeclMap(vars);
            var events = new[] { new ScenarioCustomEvent { Name = "e0" } };

            TriggerGraph g = BurnTriggers(63, decls);
            for (int r = 1; r <= 2; r++)
                g.Merge(TriggerGraph.BuildCustomEventTrigger(
                    $"R{r}", "match_start", null, null,
                    "e0", null, raiser: -1, raiseNextTick: false,
                    null, 0, null, decls, events));
            for (int s = 1; s <= 3; s++)
                g.Merge(ExpensiveTrigger($"S{s}", NodeKinds.CustomEvent, "e0", "y", paramReadingCondition: false, decls));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, DslLoopState loop) =
                Build(new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson() });

            (EntityWorld world, int[] p2) = MakeWorld(p2Count: 1);

            director.Tick(world, Fixed.One);
            Assert.True(loop.FuelExhausted);
            Assert.Equal(64, table.GetInt("y", 0)); // only S1 of occurrence 1 ran
            Assert.Equal(2, queue.Count);           // occurrence 1 (resume @ S2) + occurrence 2 (unstarted)

            world.Destroy(p2[0]); // the burn switch goes off → next tick has a full budget

            director.Tick(world, Fixed.One);
            Assert.False(loop.FuelExhausted);
            // 6 dispatches total, never 7+ (an untargeted redelivery would re-serve S1 → 448) and never < 6.
            Assert.Equal(2 * 3 * 64, table.GetInt("y", 0));
            Assert.Equal(0, queue.Count);
        }

        // ── The persisted rows survive a starved tick rather than being dropped a second time ───────────────

        [Fact]
        public void PersistedCustomOccurrences_SurviveAConsecutivelyStarvedTick()
        {
            // Same fixture, but the burn switch stays ON for a second tick: the redelivered occurrences meet an
            // already-spent budget and must be RE-persisted (the DW-349 "persistence across consecutive
            // exhausted ticks" property, now holding for custom occurrences), then drain in full once the burns
            // go quiet — still exactly 2 × 3 dispatches in total.
            ScenarioVariable[] vars = { IntVar("x"), IntVar("y") };
            var decls = DeclMap(vars);
            var events = new[] { new ScenarioCustomEvent { Name = "e0" } };

            TriggerGraph g = BurnTriggers(63, decls);
            for (int r = 1; r <= 2; r++)
                g.Merge(TriggerGraph.BuildCustomEventTrigger(
                    $"R{r}", "match_start", null, null,
                    "e0", null, raiser: -1, raiseNextTick: false,
                    null, 0, null, decls, events));
            for (int s = 1; s <= 3; s++)
                g.Merge(ExpensiveTrigger($"S{s}", NodeKinds.CustomEvent, "e0", "y", paramReadingCondition: false, decls));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, _) =
                Build(new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson() });

            (EntityWorld world, int[] p2) = MakeWorld(p2Count: 1);

            director.Tick(world, Fixed.One);
            Assert.Equal(64, table.GetInt("y", 0));
            Assert.Equal(2, queue.Count);

            director.Tick(world, Fixed.One); // still starved — the rail must not lose the work a second time
            Assert.True(queue.Count > 0);
            Assert.True(table.GetInt("y", 0) < 2 * 3 * 64);

            world.Destroy(p2[0]);

            director.Tick(world, Fixed.One);
            Assert.Equal(2 * 3 * 64, table.GetInt("y", 0)); // still exactly six dispatches — no loss, no double-fire
            Assert.Equal(0, queue.Count);
        }
    }
}
