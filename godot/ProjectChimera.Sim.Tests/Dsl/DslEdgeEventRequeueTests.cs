#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-349 (recorded owner decision: "Re-queue via DslEventQueue") — one-shot EDGE events (match_start,
    /// unit_dies, …) that a fuel-exhausted sweep break or a batched-drain suppression prevented a trigger from
    /// evaluating are PERSISTED onto the checksummed <see cref="DslEventQueue"/> re-queue rail and redelivered
    /// next tick to THAT trigger alone:
    ///   • the fuel-break tail gets its missed occurrence back (previously gone forever — "re-evaluate next
    ///     tick" held only for polled events);
    ///   • a batched-suppressed trigger's occurrence persists across the whole drip and dispatches when the
    ///     row completes;
    ///   • already-served triggers can never double-fire (redelivery is exec-targeted);
    ///   • polled threshold events never ride the rail (they re-emit every tick by construction).
    /// </summary>
    public class DslEdgeEventRequeueTests
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

        /// <summary>The DslFuelTests burn shape: <paramref name="count"/> triggers, each
        /// unit_count_threshold(faction <paramref name="thresholdFaction"/> ≥ <paramref name="thresholdCount"/>)
        /// → for_each faction_units(P1, up_to 64) [x = x + 1] — 257 ops per fire with 64 alive P1 units, so 64
        /// firing burns exhaust <c>MaxDslOpsPerTick</c>.</summary>
        private static TriggerGraph BurnTriggers(int count, int thresholdFaction, int thresholdCount,
            Dictionary<string, (DslValueType Type, VarScope Scope)> decls)
        {
            var g = new TriggerGraph();
            int id = 0;
            var actionIds = new List<int>(count);
            for (int t = 0; t < count; t++)
            {
                int trig = id++, ev = id++, loop = id++, act = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"burn{t}" });
                g.Nodes.Add(new EventNode { Id = ev, Kind = "unit_count_threshold", Faction = thresholdFaction, Count = thresholdCount, Operator = ">=" });
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

        private static void SetEventFaction(TriggerGraph g, int faction) =>
            ((EventNode)g.Nodes.First(n => n is EventNode)).Faction = faction;

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

        // ── The match_start loss class (the ledger's canonical one-shot) ────────

        [Fact]
        public void MatchStart_SkippedByFuelBreak_PersistsAndDispatchesWhenTheBudgetRecovers()
        {
            // 70 burns (keyed on P2 count ≥ 1) exhaust tick 1's budget before the sweep reaches E
            // (match_start → hit = 1). match_start fires exactly ONCE — pre-DW-349 the skipped E lost it
            // forever; now it persists on the queue rail and dispatches on tick 2 once the burns go quiet.
            ScenarioVariable[] vars = { IntVar("x"), IntVar("hit") };
            var decls = DeclMap(vars);

            TriggerGraph g = BurnTriggers(70, thresholdFaction: 1, thresholdCount: 1, decls);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "E", "match_start", null, null, null, null, -1, false,
                "hit", 0, null, decls, null, setVarValue: 1));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, DslLoopState loop) =
                Build(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            for (int i = 0; i < 64; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int p2 = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One); // burns exhaust; E skipped; match_start PERSISTED (targeted at E)
            Assert.True(loop.FuelExhausted);
            Assert.Equal(0, table.GetInt("hit", 0));
            Assert.Equal(1, queue.Count); // exactly one persistence row — deleting the requeue arms zeroes this

            world.Destroy(p2); // burns' threshold collapses → next tick has budget for E

            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("hit", 0)); // the redelivered match_start reached E
            Assert.Equal(0, queue.Count);            // consumed — nothing left pending
        }

        // ── Fuel-break: targeted redelivery, no double-fire, per-occurrence payloads ──

        [Fact]
        public void FuelBreak_PersistsTheDeathForTheSkippedTrigger_AndNeverRefiresServedTriggers()
        {
            // Sweep order (merge order): C — a param-reading any-death counter served BEFORE the break — then
            // 70 burns (P2 count ≥ 2), then E — param-reading, gated on the FIRST victim's id. The tick-2 death
            // coincides with exhaustion, so E misses it; tick 3's fresh death has a different victim, so ONLY
            // the persisted, exec-targeted redelivery can satisfy E (hit == 1 discriminates the fix), while C
            // must count exactly its two live dispatches (c == 2 — an untargeted requeue would make it 3).
            ScenarioVariable[] vars = { IntVar("x"), IntVar("c"), IntVar("hit") };
            var decls = DeclMap(vars);

            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "C", "unit_dies", null, "event.victim >= 0", null, null, -1, false,
                "c", 0, "c + 1", decls, null);
            SetEventFaction(g, 1); // deaths below are Player2 units → occurrence slot 1

            g.Merge(BurnTriggers(70, thresholdFaction: 1, thresholdCount: 2, decls));

            TriggerGraph e = TriggerGraph.BuildCustomEventTrigger(
                "E", "unit_dies", null, "event.victim == 64", null, null, -1, false,
                "hit", 0, "hit + 1", decls, null);
            SetEventFaction(e, 1);
            g.Merge(e);

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, _) =
                Build(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            for (int i = 0; i < 64; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int v2 = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 64
            int v3 = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 65
            world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);          // id 66

            director.Tick(world, Fixed.One); // burns exhaust (P2 = 3 ≥ 2); no deaths yet → nothing to persist
            Assert.Equal(0, queue.Count);

            world.Destroy(v2);
            director.Tick(world, Fixed.One); // C serves the death; burns exhaust; E skipped → death PERSISTED
            Assert.Equal(1, table.GetInt("c", 0));
            Assert.Equal(0, table.GetInt("hit", 0)); // E never saw it this tick
            Assert.Equal(1, queue.Count);            // one row, targeted at E

            world.Destroy(v3);
            director.Tick(world, Fixed.One); // burns quiet (P2 = 1 < 2): E gets the REDELIVERED victim-64 death
            Assert.Equal(1, table.GetInt("hit", 0)); // pre-DW-349 this stayed 0 forever
            Assert.Equal(2, table.GetInt("c", 0));   // fresh death only — the targeted redelivery skipped C
            Assert.Equal(0, queue.Count);
        }

        // ── Batched-drain suppression: persistence across the whole drip ────────

        [Fact]
        public void BatchedSuppression_PersistsTheDeathAcrossTheDrip_AndDispatchesOnCompletion()
        {
            // T: unit_dies(P2) → fires = fires + 1, then for_each_batched(P1, batch 5) over 13 units — a
            // 3-tick drip. The first death fires T and starts the drip; the SECOND death lands mid-drip, where
            // the sweep suppresses T: pre-DW-349 it was gone forever; now it persists (re-queued each
            // suppressed tick) and dispatches on the completion tick.
            ScenarioVariable[] vars = { IntVar("fires") };
            var decls = DeclMap(vars);

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_dies", Faction = 1 });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "fires" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 3, Source = "faction_units", Faction = 0, BatchSize = 5 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            (int root, _) = ExprParser.Parse("fires + 1", g, decls);
            g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, DslLoopState loop) =
                Build(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            for (int i = 0; i < 13; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int a = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int b = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One); // snapshot baseline

            world.Destroy(a);
            director.Tick(world, Fixed.One); // T fires (fires = 1), row activates: 13 ids, batch 5
            Assert.Equal(1, table.GetInt("fires", 0));
            Assert.True(loop.RowActive(0));

            world.Destroy(b);
            director.Tick(world, Fixed.One); // drip 5/13; T suppressed → the death PERSISTS (targeted at T)
            Assert.Equal(1, table.GetInt("fires", 0));
            Assert.Equal(1, queue.Count);

            director.Tick(world, Fixed.One); // drip 10/13; redelivered → still suppressed → re-persisted
            Assert.Equal(1, table.GetInt("fires", 0));
            Assert.Equal(1, queue.Count);

            director.Tick(world, Fixed.One); // drip completes at tick start → the redelivered death fires T
            Assert.Equal(2, table.GetInt("fires", 0)); // pre-DW-349 this stayed 1 forever
        }

        // ── Polled events never ride the rail ───────────────────────────────────

        [Fact]
        public void PolledThresholds_AreNeverPersisted_AndFuelSkipBehaviorIsUnchanged()
        {
            // The exact DslFuelTests exhaustion shape (80 burns on unit_count_threshold P1 ≥ 1): thresholds
            // re-emit every tick, so the skipped tail needs no persistence — the rail must stay EMPTY (this is
            // also the goldens-safety proof: legacy fuel-exhaustion content adds zero queue rows, so its
            // SimChecksum fold is untouched) and the pre-349 skip/recover behavior stays byte-identical.
            ScenarioVariable[] vars = { IntVar("x") };
            var decls = DeclMap(vars);
            TriggerGraph g = BurnTriggers(80, thresholdFaction: 0, thresholdCount: 1, decls);

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue, DslLoopState loop) =
                Build(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            for (int i = 0; i < 64; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);
            Assert.Equal(64 * 64, table.GetInt("x", 0)); // exactly 64 whole triggers ran (unchanged)
            Assert.True(loop.FuelExhausted);
            Assert.Equal(0, queue.Count); // nothing persisted — thresholds are polled

            director.Tick(world, Fixed.One);
            Assert.Equal(2 * 64 * 64, table.GetInt("x", 0)); // skipped tail re-evaluated by the re-poll
            Assert.Equal(0, queue.Count);
        }
    }
}
