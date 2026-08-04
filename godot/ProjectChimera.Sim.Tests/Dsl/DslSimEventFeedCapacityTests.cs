#nullable enable
using ProjectChimera.Combat;            // DamageResolver / DamageContext / DamageTable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-192 — the sim-event feed's per-tick cap must cover a worst-case mass-combat AoE tick instead of
    /// saturating at 512 in the NORMAL case. <c>DamageResolver</c>/<c>ProjectileSystem</c> push one
    /// <c>unit_damaged</c> occurrence per hit AND per splash victim, so a big AoE engagement used to exceed 512
    /// pushes in one tick and silently stop firing <c>unit_damaged</c> triggers past the cap (deterministic, but
    /// a normal-case loss). Recorded decision (2026-07-25): raise the cap to cover worst-case AoE ticks, with a
    /// memory/cost note on the constant. Every test here FAILS at the old <c>Capacity = 512</c>.
    /// </summary>
    public class DslSimEventFeedCapacityTests
    {
        // ── Shared minimal harness (the SimEventRaiseSiteEndToEndTests pattern) ──────

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global };

        /// <summary>A single trigger subscribing to <paramref name="eventKind"/> for <paramref name="factionSlot"/>
        /// that READS <c>event.<paramref name="paramName"/></c> into the global "got" variable. Param-reading, so
        /// the base sweep dispatches it once per matching occurrence (Story 7.5 per-occurrence semantics).</summary>
        private static ScenarioData EventParamScenario(string eventKind, int factionSlot, string paramName)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind, Faction = factionSlot });
            g.Nodes.Add(new ExprEventParamNode { Id = 2, Name = paramName });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "got", Faction = 0 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));
            return new ScenarioData { Variables = new[] { IntVar("got") }, TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static (ScenarioDirector Director, DslVarTable Vars) BuildDirector(ScenarioData scenario, DslSimEventFeed feed)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, simEventFeed: feed);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        // ── The sizing rationale, pinned ─────────────────────────────────────────────

        /// <summary>The cap covers every entity on a maxed-out map being hit TWICE in the same tick (mass melee
        /// landing under a full-map splash volley, or two overlapping AoEs) — the worst realistic AoE tick. Also
        /// guards the coupling: a future <c>MAX_ENTITIES</c> bump must not silently re-open the DW-192 gap.</summary>
        [Fact]
        public void Capacity_CoversEveryEntityHitTwiceInOneTick()
        {
            Assert.True(DslSimEventFeed.Capacity >= 2 * EntityWorld.MAX_ENTITIES,
                $"DslSimEventFeed.Capacity ({DslSimEventFeed.Capacity}) must cover a full-map double-hit AoE tick " +
                $"(2 x MAX_ENTITIES = {2 * EntityWorld.MAX_ENTITIES}), or mass-combat unit_damaged raises drop in " +
                "the normal case (DW-192).");
        }

        // ── Feed-level: nothing drops up to the cap; past it the seatbelt stays deterministic ──

        [Fact]
        public void Push_HoldsEveryOccurrenceUpToCapacity_ThenDropsNewestWithoutCorruption()
        {
            var feed = new DslSimEventFeed();
            for (int i = 0; i < DslSimEventFeed.Capacity; i++)
                feed.Push(DslSimEventFeed.KindUnitDamaged, factionSlot: 0, p0: i, p1: -i, p2: i * 2);

            Assert.Equal(DslSimEventFeed.Capacity, feed.Count); // nothing dropped up to the cap
            int last = DslSimEventFeed.Capacity - 1;
            Assert.Equal(last, feed.P0At(last));                // lanes intact at the boundary slot
            Assert.Equal(-last, feed.P1At(last));
            Assert.Equal(last * 2, feed.P2At(last));

            // One past the cap: the documented deterministic drop-newest seatbelt — no wrap, no throw, no clobber.
            feed.Push(DslSimEventFeed.KindUnitDamaged, factionSlot: 0, p0: 999_999, p1: 999_999, p2: 999_999);
            Assert.Equal(DslSimEventFeed.Capacity, feed.Count);
            Assert.Equal(last, feed.P0At(last));

            feed.Clear();
            Assert.Equal(0, feed.Count);
        }

        // ── End-to-end: the exact DW-192 defect — an AoE tick past the OLD 512 cap ──────

        /// <summary>One mass-AoE tick damages 600 victims (&gt; the old 512 cap, well under the per-tick DSL fuel
        /// budget so every occurrence dispatches) through the REAL <c>DamageResolver.Apply</c> raise site — the
        /// same per-victim call <c>ProjectileSystem.ApplySplash</c> makes. Pre-DW-192 the feed kept only the first
        /// 512 raises, so the subscribed trigger never fired for the late victims and "got" held victim #512's id.</summary>
        [Fact]
        public void UnitDamaged_MassAoeTickPastOldCap_StillFiresTheTriggerForLateVictims()
        {
            const int Victims = 600;

            var world = new EntityWorld();
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            var victims = new int[Victims];
            for (int i = 0; i < Victims; i++) // ids 1..600 — the LAST id is the discriminator
                victims[i] = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            var feed = new DslSimEventFeed();
            for (int i = 0; i < Victims; i++)
            {
                var ctx = new DamageContext(world, victims[i], world.ArmorTypeOf[victims[i]], Faction.Player2,
                                            DamageTable.Default, null, null, null,
                                            attackerId: attacker, dslSimEvents: feed);
                DamageResolver.Apply(in ctx, Fixed.FromInt(10), DamageType.Normal); // non-lethal: 100 HP − 10
            }

            Assert.Equal(Victims, feed.Count); // pre-DW-192: 512 — pushes past the cap were silently dropped

            // The subscribed slot-0 trigger reads event.victim per occurrence; "got" ends on the LAST victim's id
            // only if the occurrences past the old cap actually reached the director and fired.
            (ScenarioDirector director, DslVarTable vt) = BuildDirector(EventParamScenario("unit_damaged", 0, "victim"), feed);
            director.Tick(world, Fixed.One);
            Assert.Equal(victims[Victims - 1], vt.GetInt("got", -1));
            Assert.Equal(0, feed.Count); // drained + cleared — still empty at the checksum boundary (NOT folded)
        }
    }
}
