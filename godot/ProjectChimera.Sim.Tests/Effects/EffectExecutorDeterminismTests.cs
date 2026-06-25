#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.1 AC3 — deterministic, ascending-id execution. Two identical runs hash byte-identically via the
    /// real <see cref="SimChecksum"/>, and a SearchArea over several entities applies in ASCENDING entity-id
    /// order (proven by the kill-event sequence, which is order-observable — a forward-push would reverse it,
    /// so this test is the teeth behind the executor's reverse-push of SearchArea children).
    /// </summary>
    public class EffectExecutorDeterminismTests
    {
        private static readonly Fixed Zero = Fixed.Zero;

        private static uint Hash(EntityWorld w) =>
            SimChecksum.Compute(w, new BuildingStore(), new ResourceStore(Fixed.Zero), new FactionRegistry(2));

        private static FixedVec3 At(int x) => new FixedVec3(Fixed.FromInt(x), Zero, Zero);

        // ── Byte-identical checksum across two identical runs ─────────────────────────────────────────

        [Fact]
        public void IdenticalGraphAndWorld_ProduceByteIdenticalChecksum()
        {
            uint a = RunAreaDamageScenario();
            uint b = RunAreaDamageScenario();
            Assert.Equal(a, b);
        }

        private static uint RunAreaDamageScenario()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Create(At(1), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.Create(At(2), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.Create(At(3), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DamageEffect(Fixed.FromInt(10), DamageType.Normal));
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
            ex.Run(graph, in ctx);
            return Hash(w);
        }

        // ── SearchArea applies in ascending entity-id order (kill-event sequence proves it) ───────────

        [Fact]
        public void SearchArea_AppliesToTargets_InAscendingIdOrder()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            int e1 = w.Create(At(1), Faction.Player2, Fixed.FromInt(5), Fixed.FromInt(3));                 // id 1
            int e2 = w.Create(At(2), Faction.Player2, Fixed.FromInt(5), Fixed.FromInt(3));                 // id 2
            int e3 = w.Create(At(3), Faction.Player2, Fixed.FromInt(5), Fixed.FromInt(3));                 // id 3
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var events = new CombatEventQueue();
            // 10 dmg vs 5 hp (Normal vs Unarmored = 1.0) → each hit is lethal, so each produces a UnitKilled
            // event AT THAT ENTITY'S POSITION, in the order the effect is applied.
            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DamageEffect(Fixed.FromInt(10), DamageType.Normal));
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh, events, null);
            ex.Run(graph, in ctx);

            // Ascending id ⇒ e1 (id 1) killed first, e3 (id 3) last. A forward-push would reverse this.
            Assert.Equal(3, events.Count);
            Assert.Equal(At(1), events.Get(0).Position);
            Assert.Equal(At(2), events.Get(1).Position);
            Assert.Equal(At(3), events.Get(2).Position);

            Assert.False(w.IsAlive(e1));
            Assert.False(w.IsAlive(e2));
            Assert.False(w.IsAlive(e3));
            Assert.True(w.IsAlive(caster)); // Enemy filter excludes the same-faction caster
        }

        // ── FindTargets returns ascending ids regardless of creation/spatial order ────────────────────

        [Fact]
        public void FindTargets_ReturnsMatchesInAscendingIdOrder()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Spread enemies so they land in different spatial-hash cells (QueryRadius is unordered across cells).
            w.Create(At(5), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.Create(At(15), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.Create(At(25), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            w.Create(At(35), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(100), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            int n = search.FindTargets(in ctx, buffer);

            Assert.Equal(4, n);
            for (int i = 1; i < n; i++)
                Assert.True(buffer[i - 1] < buffer[i], "FindTargets must return strictly ascending ids (AC3).");
        }

        // ── Filter correctness: Enemy selects only the other faction (not self/ally/neutral) ─────────

        [Fact]
        public void EnemyFilter_SelectsOnlyOtherFaction()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Create(At(1), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));  // ally
            w.Create(At(2), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));  // enemy
            w.Create(At(3), Faction.Neutral, Fixed.FromInt(50), Fixed.FromInt(3));  // neutral
            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            int n = search.FindTargets(in ctx, buffer);

            Assert.Equal(1, n); // only the Player2 entity
            Assert.Equal(Faction.Player2, w.FactionOf[buffer[0]]);
        }
    }
}
