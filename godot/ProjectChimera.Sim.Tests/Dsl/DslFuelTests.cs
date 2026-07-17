#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;   // SequenceEffect / DirectHpDeltaEffect (drain-phase exhaustion fixture)
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.6 — the Layer-3 fuel seatbelt (the I/O-matrix row): many INDIVIDUALLY-LEGAL loop triggers whose
    /// same-tick sum exceeds <see cref="DslBounds.MaxDslOpsPerTick"/> halt the sweep deterministically at a
    /// WHOLE-TRIGGER boundary (the in-flight trigger completes — never torn state), skipped triggers simply
    /// re-evaluate next tick, the consumed-fuel value folds into <c>SimChecksum</c>, and two headless runs are
    /// byte-identical.
    /// </summary>
    public class DslFuelTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Decls =
            new(StringComparer.Ordinal) { ["x"] = (DslValueType.Int, VarScope.Global) };

        /// <summary>
        /// Build N identical triggers, each: unit_count_threshold(P1 ≥ 1) → for_each faction_units(P1, up_to 64)
        /// [body: x = x + 1]. Per-fire runtime cost with 64 alive P1 units: 1 (loop) + 64 × (1 action + 3 expr
        /// ops) = 257 — individually legal (≤ MaxDslOpsPerTrigger), but 80 of them sum past MaxDslOpsPerTick.
        /// </summary>
        private static ScenarioData Scenario(int triggerCount)
        {
            var g = new TriggerGraph();
            int id = 0;
            var actionIds = new List<int>(triggerCount);
            for (int t = 0; t < triggerCount; t++)
            {
                int trig = id++, ev = id++, loop = id++, act = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"burn{t}" });
                g.Nodes.Add(new EventNode { Id = ev, Kind = "unit_count_threshold", Faction = 0, Count = 1, Operator = ">=" });
                g.Nodes.Add(new ForEachNode { Id = loop, Source = "faction_units", Faction = 0, UpTo = 64 });
                g.Nodes.Add(new ActionNode { Id = act, Kind = "set_variable", Variable = "x" });
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(trig, TriggerGraph.TriggerExecOutPort, loop, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(loop, TriggerGraph.ForEachBodyOutPort, act, TriggerGraph.ActionExecInPort));
                actionIds.Add(act);
            }
            foreach (int act in actionIds)
            {
                (int root, _) = ExprParser.Parse("x + 1", g, Decls);
                g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionValueInPort, DataWireType.Int));
            }

            return new ScenarioData
            {
                Variables = new[] { new ScenarioVariable { Name = "x", Type = DslValueType.Int, Scope = VarScope.Global } },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslLoopState Loop, EntityWorld World) Build(int triggerCount)
        {
            var world = new EntityWorld();
            for (int i = 0; i < 64; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
            director.LoadScenario(Scenario(triggerCount));
            return (director, vars, loop, world);
        }

        [Fact]
        public void Exhaustion_HaltsAtAWholeTriggerBoundary_AndSkippedTriggersReEvaluateNextTick()
        {
            // 80 triggers × 257 ops each: the check runs BEFORE each trigger, so triggers run until consumed ≥
            // 16384 — exactly 64 of them (64 × 257 = 16448) — and the remaining 16 skip this tick.
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world) = Build(80);

            director.Tick(world, Fixed.One);
            int x = vars.GetInt("x", 0);
            Assert.Equal(64 * 64, x);                 // exactly 64 whole triggers ran
            Assert.Equal(0, x % 64);                  // never torn mid-trigger (each fire adds a whole 64)
            Assert.True(loop.FuelConsumed >= DslBounds.MaxDslOpsPerTick,
                $"fuel should be exhausted (consumed {loop.FuelConsumed})");

            // Next tick: the budget resets and the skipped triggers re-evaluate (the polled event re-fires all).
            director.Tick(world, Fixed.One);
            Assert.Equal(2 * 64 * 64, vars.GetInt("x", 0));
        }

        [Fact]
        public void UnderBudgetWork_IsUntouchedByTheSeatbelt()
        {
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world) = Build(10);
            director.Tick(world, Fixed.One);
            Assert.Equal(10 * 64, vars.GetInt("x", 0)); // all 10 triggers ran
            Assert.False(loop.FuelExhausted);
            Assert.True(loop.FuelConsumed > 0); // work was charged (and folds via SimChecksum)
        }

        [Fact]
        public void ConsumedFuel_FoldsIntoTheChecksum()
        {
            (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world) = Build(10);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry  = new FactionRegistry(2);

            director.Tick(world, Fixed.One);
            Assert.True(loop.FuelConsumed > 0);
            uint withFuel = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars, loop);
            uint without  = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars, null);
            Assert.NotEqual(without, withFuel); // the consumed-this-tick value is checksummed state
        }

        // ── Drain-phase exhaustion (review P3): the whole-ROW boundary halt in DrainBatchedRows ──

        /// <summary>
        /// 5 match_start triggers, each one for_each_batched(P1, batch 63) whose body costs exactly 65 ops per
        /// entity (a run_effect embedding a 64-node Sequence + one display_message action), i.e. a per-tick
        /// drain cost of 1 + 63 × 65 = 4096 per row — the MaxDslOpsPerTrigger static maximum, so this is the
        /// densest legal fixture. NOTE: the review sketch asked for TWO batched triggers, but the static
        /// per-trigger cap makes that unreachable (2 × 4096 &lt; MaxDslOpsPerTick = 16384): a row can never
        /// exhaust the budget alone, so FIVE rows are needed — rows 0..3 consume exactly 16384 and row 4 must
        /// be skipped by the drain-phase FuelExhausted break.
        /// </summary>
        private static ScenarioData BatchedExhaustionScenario()
        {
            var g = new TriggerGraph();
            int id = 0;
            for (int t = 0; t < 5; t++)
            {
                int trig = id++, ev = id++, batched = id++, eff = id++, act = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"drip{t}" });
                g.Nodes.Add(new EventNode { Id = ev, Kind = "match_start" });
                g.Nodes.Add(new ForEachBatchedNode { Id = batched, Source = "faction_units", Faction = 0, BatchSize = 63 });
                var children = new EffectNode[63];
                for (int k = 0; k < children.Length; k++) children[k] = new DirectHpDeltaEffect(Fixed.Zero);
                g.Nodes.Add(new EffectActionNode { Id = eff, Effect = new SequenceEffect(children) }); // 64 nodes
                g.Nodes.Add(new ActionNode { Id = act, Kind = "display_message", Text = "x" });        // +1 op
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(trig, TriggerGraph.TriggerExecOutPort, batched, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(batched, TriggerGraph.ForEachBodyOutPort, eff, TriggerGraph.ActionExecInPort));
                g.ExecEdges.Add(new ExecEdge(eff, TriggerGraph.ActionExecOutPort, act, TriggerGraph.ActionExecInPort));
            }
            return new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
        }

        [Fact]
        public void DrainPhaseExhaustion_HaltsAtAWholeRowBoundary_AndTheSkippedRowResumesNextTick()
        {
            var world = new EntityWorld();
            for (int i = 0; i < 63; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop);
            director.LoadScenario(BatchedExhaustionScenario());

            // Fire tick: all 5 triggers snapshot their 63-id rows (5 ops — nowhere near the budget).
            director.Tick(world, Fixed.One);
            for (int r = 0; r < 5; r++)
            {
                Assert.True(loop.RowActive(r));
                Assert.Equal(63, loop.RowLength(r));
            }

            // Drain tick: rows 0..3 drain whole (4 × 4096 = 16384 = the budget) and COMPLETE; the check before
            // row 4 sees FuelExhausted → row 4 is skipped untouched. Deleting the `if (FuelExhausted) break;`
            // in DrainBatchedRows drains row 4 this tick too → these assertions go red.
            director.Tick(world, Fixed.One);
            for (int r = 0; r < 4; r++)
            {
                Assert.Equal(63, loop.RowCursor(r));
                Assert.False(loop.RowActive(r)); // completed (whole rows — never torn)
            }
            Assert.True(loop.RowActive(4));
            Assert.Equal(0, loop.RowCursor(4)); // the skipped row's cursor did NOT advance
            Assert.True(loop.FuelExhausted);
            Assert.Equal(4 * 4096, loop.FuelConsumed);

            // Next tick: the budget resets and the skipped row drains to completion.
            director.Tick(world, Fixed.One);
            Assert.False(loop.RowActive(4));
            Assert.Equal(63, loop.RowCursor(4));
            Assert.False(loop.FuelExhausted); // 4096 < the budget
        }

        [Fact]
        public void FuelExhaustionScenario_TwoHeadlessRuns_AreByteIdentical()
        {
            static List<uint> Run()
            {
                (ScenarioDirector director, DslVarTable vars, DslLoopState loop, EntityWorld world) = Build(80);
                var buildings = new BuildingStore();
                var resources = new ResourceStore(Fixed.Zero);
                var registry  = new FactionRegistry(2);
                var seq = new List<uint>();
                for (int t = 0; t < 6; t++)
                {
                    director.Tick(world, Fixed.One);
                    seq.Add(SimChecksum.Compute(world, buildings, resources, registry,
                        null, null, null, null, null, vars, loop));
                }
                return seq;
            }

            Assert.Equal(Run(), Run());
        }
    }
}
