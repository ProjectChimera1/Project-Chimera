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
    /// DW-686 — <c>ScenarioDirector.RequeueEdgeEventsFor</c> must check the enabled/run-once/cooldown gates ITSELF
    /// instead of trusting its callers to have checked them.
    ///
    /// <para><b>The rule it protects.</b> DW-349 persists one-shot EDGE occurrences that a fuel break or a batched
    /// drip prevented a trigger from evaluating, so they redeliver instead of vanishing. Its stated boundary is that
    /// a GATE-BLOCKED skip is authored semantics, not the loss class: a disabled / run-once-spent / cooling trigger
    /// is supposed to lose the occurrence, exactly as it would have on a normal sweep. Persisting a row for such a
    /// trigger breaks that polled-parity rule — and the row is FOLDED state (DslEventQueue, SimChecksum v18), so it
    /// is not a cosmetic difference.</para>
    ///
    /// <para><b>Why prose was not enough.</b> The arm documented the precondition as "gate state was already checked
    /// by the caller" and checked nothing. Two of its three call sites really are provably eligible (they sit
    /// directly under the sweep's enabled/fired/cooldown gate). The PER-OCCURRENCE pair is not: that loop re-checks
    /// only <c>_triggerFired</c>/<c>_triggerCooldown</c> per iteration, so a trigger whose own action ran
    /// <c>disable_trigger</c> ON ITSELF is still carrying an enabled-verdict from BEFORE it fired. The test below is
    /// that exact shape, and it is a live defect, not only a hardening exercise.</para>
    ///
    /// <para>Godot-free; ascending-id iteration; <see cref="Fixed"/> only.</para>
    /// </summary>
    public class RequeueGateGuardTests
    {
        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global };

        private static Dictionary<string, (DslValueType Type, VarScope Scope)> DeclMap(ScenarioVariable[] vars)
        {
            var map = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            foreach (ScenarioVariable v in vars) map[v.Name] = (v.Type, v.Scope);
            return map;
        }

        /// <summary>
        /// The DW-686 shape: ONE param-reading <c>unit_dies</c> trigger (the <c>event.victim</c> condition is what
        /// makes it dispatch per-occurrence) whose action chain optionally disables ITSELF and then enters a
        /// multi-tick <c>for_each_batched</c> drip. With two same-tick P2 deaths, occurrence 0 fires the trigger and
        /// activates the row; occurrence 1 then hits the batched-suppression arm — the call site whose enabled
        /// verdict is stale.
        /// </summary>
        private static TriggerGraph SelfDisablingBatchedTrigger(
            Dictionary<string, (DslValueType Type, VarScope Scope)> decls, bool selfDisable)
        {
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "T", "unit_dies", null, "event.victim >= 0", null, null, -1, false,
                "fires", 0, "fires + 1", decls, null);
            // The builder leaves EventNode.Faction at its 0 default; the victims below are Player2, so the
            // subscription must name slot 1 or nothing matches (the sibling requeue tests do the same).
            ((EventNode)g.Nodes.First(n => n is EventNode)).Faction = 1;

            int next = g.Nodes.Max(n => n.Id) + 1;
            var tail = (ActionNode)g.Nodes.First(n => n is ActionNode); // the set_variable the builder appended

            if (selfDisable)
            {
                int disableId = next++;
                g.Nodes.Add(new DisableTriggerNode { Id = disableId, TargetTriggerId = 0 });
                g.ExecEdges.Add(new ExecEdge(tail.Id, TriggerGraph.ActionExecOutPort, disableId, TriggerGraph.ActionExecInPort));
                // The batched node hangs off the disable so the drip still starts on the SAME dispatch.
                g.ExecEdges.Add(new ExecEdge(disableId, TriggerGraph.ActionExecOutPort, next, TriggerGraph.ActionExecInPort));
            }
            else
            {
                g.ExecEdges.Add(new ExecEdge(tail.Id, TriggerGraph.ActionExecOutPort, next, TriggerGraph.ActionExecInPort));
            }

            // 13 P1 units at batch 5 → a 3-tick drip, so the row is still ACTIVE when occurrence 1 is examined.
            g.Nodes.Add(new ForEachBatchedNode { Id = next, Source = "faction_units", Faction = 0, BatchSize = 5 });
            return g;
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslEventQueue Queue,
                        TriggerEnabledStore Enabled, EntityWorld World, int VictimA, int VictimB)
            Stage(bool selfDisable)
        {
            ScenarioVariable[] vars = { IntVar("fires") };
            TriggerGraph g = SelfDisablingBatchedTrigger(DeclMap(vars), selfDisable);

            var table   = new DslVarTable();
            var queue   = new DslEventQueue();
            var enabled = new TriggerEnabledStore();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero),
                                                table, new DslLoopState(), queue, enabled);
            director.LoadScenario(new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            for (int i = 0; i < 13; i++) // the drip population
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int a = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int b = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            // Seed the director's alive-snapshot: unit_dies is a DIFF against the previous tick, so a unit destroyed
            // before the first tick was never seen alive and raises nothing (the same warm-up every sibling
            // requeue/death test performs). The trigger has no other event source, so this tick is inert.
            director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("fires", 0));
            Assert.Equal(0, queue.Count);

            return (director, table, queue, enabled, world, a, b);
        }

        [Fact]
        public void ATriggerThatDisabledItselfMidDispatch_PersistsNoRequeueRow()
        {
            // RED pre-fix: queue.Count == 1. Occurrence 0 fires T, T disables itself and starts the drip; the loop
            // then examines occurrence 1, whose only surviving gate check is fired/cooldown — so the batched-
            // suppression arm persisted a redelivery row for a trigger that is no longer allowed to run at all.
            var (director, table, queue, enabled, world, a, b) = Stage(selfDisable: true);

            world.Destroy(a);
            world.Destroy(b); // two same-tick occurrences → occurrence 1 reaches the suppression arm

            director.Tick(world, Fixed.One);

            Assert.Equal(1, table.GetInt("fires", 0)); // fired exactly once, on occurrence 0
            Assert.False(enabled.IsEnabled(0));        // and disabled itself during that dispatch
            Assert.Equal(0, queue.Count);              // ← the fix: a gate-blocked trigger persists NOTHING

            // And it stays lost: the authored semantics of disable_trigger is that the occurrence is gone, not
            // parked. A later tick must not resurrect it.
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("fires", 0));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void TheSameShapeWithoutTheSelfDisable_StillPersistsItsRow()
        {
            // The positive control that makes the assertion above meaningful: identical graph, identical deaths,
            // only the disable_trigger removed. The suppression arm IS reached and DOES persist — so the zero above
            // is the gate check firing, not a staging that never got near the re-queue path (and not an
            // early-return that swallowed every caller).
            var (director, table, queue, enabled, world, a, b) = Stage(selfDisable: false);

            world.Destroy(a);
            world.Destroy(b);

            director.Tick(world, Fixed.One);

            Assert.Equal(1, table.GetInt("fires", 0));
            Assert.True(enabled.IsEnabled(0));
            Assert.Equal(1, queue.Count); // DW-349's arm, doing its job for an ELIGIBLE trigger
        }
    }
}
