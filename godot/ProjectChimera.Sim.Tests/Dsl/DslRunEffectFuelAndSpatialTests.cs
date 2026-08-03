#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-347 / DW-339 / DW-352 / DW-340 — the run_effect fuel + spatial-index hardening bundle:
    ///
    /// <para>DW-347 — <c>DslLoopGate.CountEffectNodes</c> (the ONE function behind both the static load-gate
    /// product check and the runtime fuel charge) weights a <c>SearchAreaEffect</c> child subtree by
    /// <c>EffectCaps.MaxSearchTargets</c> — the executor fans the child out once per matched target, so the old
    /// flat node count undercounted by up to 64× (worst inside entity loops).</para>
    ///
    /// <para>DW-339/DW-352 — <c>ScenarioDirector.RunEffect</c> rebuilds its SearchArea <c>SpatialHash</c>
    /// LAZILY at most once per tick, dirty-flagged: a kill-capable embedded graph or a spawn_unit leaf re-marks
    /// the index stale so mid-tick kills/spawns stay visible to later run_effects — byte-identical results to
    /// the historical rebuild-per-invocation, minus the redundant O(world) passes.</para>
    ///
    /// <para>DW-340 — a director with NO ModifierStore wired (no <c>SetEffectRuntime</c>) now rejects
    /// modifier-bearing run_effect content AT LOAD with a located error instead of throwing
    /// <c>NotSupportedException</c> mid-tick on first fire.</para>
    /// </summary>
    public class DslRunEffectFuelAndSpatialTests
    {
        // ── DW-347: the static cost model ───────────────────────────────────────

        [Fact]
        public void CountEffectNodes_WeightsSearchAreaChildByMaxSearchTargets()
        {
            var leaf = new DirectHpDeltaEffect(Fixed.FromInt(-1));

            // A bare leaf and a Sequence keep their flat node counts (no fan-out — unchanged).
            Assert.Equal(1, DslLoopGate.CountEffectNodes(leaf));
            Assert.Equal(3, DslLoopGate.CountEffectNodes(
                new SequenceEffect(new EffectNode[] { new DirectHpDeltaEffect(Fixed.Zero), new HealEffect(Fixed.One) })));

            // SearchArea: 1 (the node) + MaxSearchTargets × the child subtree (executed once per matched target).
            var search = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy | TargetFilter.Alive, leaf);
            Assert.Equal(1 + EffectCaps.MaxSearchTargets, DslLoopGate.CountEffectNodes(search));

            // Nested searches multiply (the 64² chain-lightning worst case the executor can actually run).
            var nested = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy | TargetFilter.Alive, search);
            int inner = 1 + EffectCaps.MaxSearchTargets;                       // the inner search subtree
            Assert.Equal(1 + EffectCaps.MaxSearchTargets * inner, DslLoopGate.CountEffectNodes(nested));

            // A search child under a Sequence weights only the search's subtree.
            var seq = new SequenceEffect(new EffectNode[] { new DirectHpDeltaEffect(Fixed.Zero), search });
            Assert.Equal(1 + 1 + (1 + EffectCaps.MaxSearchTargets), DslLoopGate.CountEffectNodes(seq));
        }

        [Fact]
        public void RuntimeFuelCharge_UsesTheWeightedSearchAreaCost()
        {
            // One trigger: match_start → run_effect(SearchArea(hp delta)). The ONLY fuel charge of the tick is the
            // run_effect item, so FuelConsumed == the weighted static cost. Pre-DW-347 this charged a flat 2.
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start",
                new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy | TargetFilter.Alive,
                    new DirectHpDeltaEffect(Fixed.FromInt(-1))));

            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(), loop);
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });

            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(1 + EffectCaps.MaxSearchTargets, loop.FuelConsumed);
        }

        [Fact]
        public void LoopedSearchAreaRunEffect_NowExceedsThePerTriggerCostGate()
        {
            // for_each(up_to 64) { run_effect(SearchArea(leaf)) }: 1 + 64 × (1 + 64) = 4161 > MaxDslOpsPerTrigger
            // (4096) → a located load reject naming the constant. Pre-DW-347 the same trigger cost a flat
            // 1 + 64 × 2 = 129 and loaded silently — exactly the uncharged 64× fan-out the ledger names.
            TriggerGraph g = TriggerGraph.BuildForEachTrigger("t", "match_start",
                "faction_units", null, 0, null, upTo: 64, loopVar: null,
                new NodeBase[]
                {
                    new EffectActionNode
                    {
                        Effect = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy | TargetFilter.Alive,
                            new DirectHpDeltaEffect(Fixed.FromInt(-1))),
                    },
                });

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }));
            Assert.Contains("MaxDslOpsPerTrigger", ex.Message);
        }

        // ── DW-339/DW-352: the dirty-flagged once-per-tick spatial rebuild ─────

        private static ScenarioDirector BuildDirector(TriggerGraph g)
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });
            return director;
        }

        [Fact]
        public void NonMutatingRunEffects_ShareOneSpatialRebuildPerTick()
        {
            // Two heal-only run_effect triggers in one tick: heal cannot change the alive set or any position, so
            // the second run_effect reuses the first's rebuild. Pre-DW-339 every invocation rebuilt (count 2).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("a", "match_start", new HealEffect(Fixed.One));
            g.Merge(TriggerGraph.BuildRunEffectTrigger("b", "match_start", new HealEffect(Fixed.One)));
            ScenarioDirector director = BuildDirector(g);

            var world = new EntityWorld();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);
            Assert.Equal(1, director.EffectSpatialRebuildCount);

            // Next tick: no run_effect fires (match_start is gone) → no further rebuild.
            director.Tick(world, Fixed.One);
            Assert.Equal(1, director.EffectSpatialRebuildCount);
        }

        [Fact]
        public void KillCapableRunEffect_DirtiesTheIndex_SoTheNextRunEffectRebuilds()
        {
            // Trigger a: a DAMAGE-class graph (can kill → conservative mutating classification); trigger b: a
            // heal. The mutating run completes and re-dirties the index, so b rebuilds — mid-tick kills stay
            // visible to later searches (the DW-352 constraint the once-per-tick naive fix would violate).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("a", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            g.Merge(TriggerGraph.BuildRunEffectTrigger("b", "match_start", new HealEffect(Fixed.One)));
            ScenarioDirector director = BuildDirector(g);

            var world = new EntityWorld();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);
            Assert.Equal(2, director.EffectSpatialRebuildCount);
        }

        [Fact]
        public void SpawnThenSearch_SameTick_SeesTheSpawnedUnit()
        {
            // a: a heal run_effect BUILDS the index early (before the spawn exists); b: spawn_unit creates a unit
            // synchronously (the production OnSpawnUnit wiring — sim→sim); c: a SearchArea run_effect must HIT the
            // spawned unit. The spawn dirties the index so c rebuilds — a naive "once per tick" rebuild would
            // serve c the pre-spawn index and silently miss the unit (the exact staleness DW-352 forbids).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("a", "match_start", new HealEffect(Fixed.One));

            var spawn = new TriggerGraph();
            spawn.Nodes.Add(new TriggerNode { Id = 0, Name = "b" });
            spawn.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            spawn.Nodes.Add(new ActionNode
            {
                Id = 2, Kind = "spawn_unit", UnitId = "grunt", Faction = 0,
                X = Fixed.FromInt(3), Z = Fixed.Zero, Count = 1,
            });
            spawn.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            spawn.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.Merge(spawn);

            g.Merge(TriggerGraph.BuildRunEffectTrigger("c", "match_start",
                new SearchAreaEffect(Fixed.FromInt(20), TargetFilter.Ally | TargetFilter.Alive,
                    new DirectHpDeltaEffect(Fixed.FromInt(-1)))));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });

            var world = new EntityWorld();
            int anchor = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);
            world.EffectiveMaxHealth[anchor] = Fixed.FromInt(10);

            int spawned = -1;
            director.OnSpawnUnit = (unitId, slot, x, z, count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    spawned = world.Create(new FixedVec3(x, Fixed.Zero, z), (Faction)(slot + 1), Fixed.FromInt(10), Fixed.One);
                    world.EffectiveMaxHealth[spawned] = Fixed.FromInt(10);
                }
            };

            director.Tick(world, Fixed.One);

            Assert.True(spawned >= 0, "the spawn_unit leaf should have spawned");
            // c's SearchArea (centered on the anchor, radius 20, Ally) must have hit the just-spawned unit.
            Assert.Equal(Fixed.FromInt(9).Raw, world.Health[spawned].Raw);
            // And the spawn forced a second rebuild after a's early build (a=1, c-after-dirty=2).
            Assert.Equal(2, director.EffectSpatialRebuildCount);
        }

        // ── DW-340: the ModifierStore wiring gate ──────────────────────────────

        private static Modifier TestModifier() => new Modifier(
            id: 1, durationTicks: 30, stacking: StackRule.Refresh, maxStacks: 1,
            maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.One, moveSpeedDelta: Fixed.Zero,
            status: default, periodEffect: null, periodTicks: 0);

        [Fact]
        public void LoadScenario_WithoutModifierStore_RejectsApplyModifierRunEffect()
        {
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start",
                new ApplyModifierEffect(TestModifier()));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }));
            Assert.Contains("ModifierStore", ex.Message);
        }

        [Fact]
        public void LoadScenario_WithoutModifierStore_RejectsNestedPersistentEffect()
        {
            // The modifier-bearing node hides under a SearchArea child — the walk must find it at any depth.
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start",
                new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy | TargetFilter.Alive,
                    new PersistentEffect(new DirectHpDeltaEffect(Fixed.FromInt(-1)), null, null,
                        periodTicks: 0, periodCount: 0)));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }));
            Assert.Contains("ModifierStore", ex.Message);
        }

        [Fact]
        public void LoadScenario_WithModifierStoreWired_AcceptsModifierBearingRunEffect_AndTicksWithoutThrowing()
        {
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start",
                new ApplyModifierEffect(TestModifier()));

            var world = new EntityWorld();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.SetEffectRuntime(null, new ModifierStore(world, new ModifierSystem()), null, null, null);
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }); // must not throw

            director.Tick(world, Fixed.One); // the effect fires against the wired store — no mid-tick throw
        }

        [Fact]
        public void LoadScenario_WithoutModifierStore_StillAcceptsModifierFreeRunEffect()
        {
            // The gate is scoped to modifier-BEARING graphs: plain damage/heal run_effects keep loading exactly
            // as before on a store-less director (every existing 7.x fixture).
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start",
                new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }); // must not throw
        }
    }
}
