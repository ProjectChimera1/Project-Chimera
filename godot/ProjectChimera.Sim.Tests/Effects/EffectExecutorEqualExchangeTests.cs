#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.1 AC4 — the Equal-Exchange-shaped non-matrix primitive, plus the leaf edge cases the Edge Case
    /// Hunter will walk (clamp at zero / MaxHealth, dead-target no-op, the deferred Persistent/ApplyModifier
    /// guards). All post-states are asserted against INDEPENDENTLY-computed Fixed raws (never by re-running the
    /// method), mirroring the DamageResolverTests discipline.
    /// </summary>
    public class EffectExecutorEqualExchangeTests
    {
        private static EffectContext SelfCtx(EntityWorld w, int id) =>
            new EffectContext(w, id, id, w.FactionOf[id], DamageTable.Default);

        // ── {DirectHpDelta -10, Heal +25} in order, flat and armor-independent ────────────────────────

        [Fact]
        public void Sequence_DirectHpDeltaThenHeal_AppliesFlatInOrder()
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(3)); // MaxHealth 200
            w.Health[id] = Fixed.FromInt(50);          // headroom so the heal does not clamp and mask the order
            w.ArmorTypeOf[id] = ArmorType.Unarmored;

            var graph = new SequenceEffect(
                new DirectHpDeltaEffect(Fixed.FromInt(-10)),
                new HealEffect(Fixed.FromInt(25)));
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);
            ex.Run(graph, in ctx);

            // Independently computed: 50 - 10 + 25 = 65. (raw 65*65536 = 4_259_840)
            Assert.Equal(Fixed.FromInt(65).Raw, w.Health[id].Raw);
            Assert.Equal(4_259_840, w.Health[id].Raw);
        }

        [Fact]
        public void DirectHpDelta_IsArmorIndependent_UnlikeMatrixDamage()
        {
            // DirectHpDelta: flat -10 regardless of armor → 50 - 10 = 40 for BOTH Heavy and Unarmored.
            Fixed directHeavy = AfterDirectDelta(ArmorType.Heavy, -10);
            Fixed directUnarmored = AfterDirectDelta(ArmorType.Unarmored, -10);
            Assert.Equal(directUnarmored.Raw, directHeavy.Raw);            // armor-independent
            Assert.Equal(Fixed.FromInt(40).Raw, directHeavy.Raw);         // flat 50 - 10

            // Contrast: a matrix DamageEffect WOULD scale by armor (Normal vs Heavy = 0.5) → 50 - 5 = 45,
            // proving DirectHpDelta (flat 40) does NOT route through DamageMatrix/DamageResolver (AC4).
            Fixed matrixHeavy = AfterMatrixDamage(ArmorType.Heavy, 10);
            Assert.Equal(Fixed.FromInt(45).Raw, matrixHeavy.Raw);
            Assert.NotEqual(directHeavy.Raw, matrixHeavy.Raw);
        }

        private static Fixed AfterDirectDelta(ArmorType armor, int delta)
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(3));
            w.Health[id] = Fixed.FromInt(50);
            w.ArmorTypeOf[id] = armor;
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);
            ex.Run(new DirectHpDeltaEffect(Fixed.FromInt(delta)), in ctx);
            return w.Health[id];
        }

        private static Fixed AfterMatrixDamage(ArmorType armor, int amount)
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(3));
            w.Health[id] = Fixed.FromInt(50);
            w.ArmorTypeOf[id] = armor;
            var ex = new EffectExecutor();
            // Caster faction differs from target so this is a clean enemy hit (not that DamageResolver cares).
            var ctx = new EffectContext(w, id, id, Faction.Player2, DamageTable.Default);
            ex.Run(new DamageEffect(Fixed.FromInt(amount), DamageType.Normal), in ctx);
            return w.Health[id];
        }

        // ── Clamp boundaries: DirectHpDelta floors at 0 (no death); Heal caps at MaxHealth (no overheal) ─

        [Fact]
        public void DirectHpDelta_OverkillClampsAtZero_WithoutFiringDeath()
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);

            ex.Run(new DirectHpDeltaEffect(Fixed.FromInt(-500)), in ctx); // overkill

            Assert.Equal(Fixed.Zero.Raw, w.Health[id].Raw);
            // By design, the flat pool adjustment does NOT fire the death sequence — that is DamageEffect's job.
            Assert.True(w.IsAlive(id));
        }

        [Fact]
        public void Heal_OverhealClampsAtMaxHealth()
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Health[id] = Fixed.FromInt(40);
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);

            ex.Run(new HealEffect(Fixed.FromInt(500)), in ctx);

            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[id].Raw); // capped at MaxHealth
        }

        // ── Dead/recycled target: every leaf no-ops, never throws ─────────────────────────────────────

        [Fact]
        public void Leaves_OnDeadTarget_AreNoOp()
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Destroy(id);
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);

            Exception? a = Record.Exception(() => ex.Run(new HealEffect(Fixed.FromInt(10)), in ctx));
            Exception? b = Record.Exception(() => ex.Run(new DirectHpDeltaEffect(Fixed.FromInt(-10)), in ctx));
            Exception? c = Record.Exception(() => ex.Run(new DamageEffect(Fixed.FromInt(10), DamageType.Normal), in ctx));

            Assert.Null(a);
            Assert.Null(b);
            Assert.Null(c);
            Assert.False(w.IsAlive(id));
        }

        // ── SearchArea with zero hits is a clean no-op ────────────────────────────────────────────────

        [Fact]
        public void SearchArea_WithNoMatches_IsNoOp()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var sh = new SpatialHash();
            sh.Rebuild(w); // only the caster exists; no enemies

            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                             new DamageEffect(Fixed.FromInt(10), DamageType.Normal));
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
            ex.Run(graph, in ctx);

            Assert.True(w.IsAlive(caster));
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[caster].Raw);
        }

        // ── Deferred nodes fail closed (loud) until Story 2.2b ────────────────────────────────────────

        [Fact]
        public void PersistentAndApplyModifier_FailClosed_Until22b()
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            var ex = new EffectExecutor();
            EffectContext ctx = SelfCtx(w, id);

            var persistent = new PersistentEffect(new HealEffect(Fixed.One), null, null, 1, 1);
            Assert.Throws<NotSupportedException>(() => ex.Run(persistent, in ctx));

            var modifier = new Modifier(1, 30, StackRule.Refresh, 1,
                Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0);
            Assert.Throws<NotSupportedException>(() => ex.Run(new ApplyModifierEffect(modifier), in ctx));
        }
    }
}
