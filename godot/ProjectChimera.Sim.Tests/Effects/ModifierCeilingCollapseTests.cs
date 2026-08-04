#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-491 — the DW-325 ceiling-collapse death is a downward TRANSITION, not an absolute
    /// <c>EffectiveMaxHealth == 0</c> reading.
    ///
    /// <para>The original gate was <c>IsAlive(id) &amp;&amp; EffectiveMaxHealth[id] == Fixed.Zero</c> with no sign test
    /// and no "was it above zero before?" test, so two NON-collapses were lethal and contradicted the gate's own
    /// "net-negative-MaxHealth" comment:</para>
    /// <list type="bullet">
    /// <item>a host that legitimately SITS at ceiling 0 (a base-0 / item-sustained unit) was killed by ANY
    ///   MaxHealth-touching modifier, because <c>0 → 0</c> satisfies an absolute test; and</item>
    /// <item>even a POSITIVE +MaxHealth grant on such a host was lethal — a heal that kills its target.</item>
    /// </list>
    /// <para>Each test below is RED against the pre-DW-491 gate (the host dies) and the two teeth tests are RED against
    /// a gate that simply stopped killing (a real collapse must still kill). The negative-sign conjunct additionally
    /// restores DW-488's accumulator-wrap outcome to the benign 0-ceiling zombie it was before DW-325 rather than an
    /// outright kill — pinned by <see cref="AccumulatorWrapFromAPositiveGrant_IsNotLethal"/>.</para>
    ///
    /// Bare worlds via <see cref="EntityWorld.Create"/>; <see cref="Fixed.FromInt"/>/<see cref="Fixed.FromRaw"/> only.
    /// </summary>
    public class ModifierCeilingCollapseTests
    {
        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        /// <summary>A pure MaxHealth modifier (no period, no status).</summary>
        private static Modifier MaxHpMod(int id, Fixed maxHealthDelta, int duration = 5) =>
            new Modifier(id, duration, StackRule.Refresh, 1, maxHealthDelta, Fixed.Zero, Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        // ── The two non-collapses that used to be lethal ──

        [Fact]
        public void HostAlreadyAtCeilingZero_NegativeMaxHealthModifier_DoesNotKill()
        {
            // A live entity whose ceiling is ALREADY 0 (base max health 0 — the "item-sustained host" shape). A −10
            // debuff leaves the ceiling floored at 0, i.e. 0 → 0: nothing collapsed, so nothing may die. Under the old
            // absolute gate the post-apply `EffectiveMaxHealth == 0` read was satisfied and the host was killed (RED).
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.Zero, Fixed.FromInt(4));
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw); // the precondition this test is about

            store.Apply(id, MaxHpMod(1, Fixed.FromInt(-10)), id, Faction.Player1);

            Assert.True(world.IsAlive(id));                                 // 0 → 0 is not a collapse
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw); // Zero-floored, unchanged
            Assert.Equal(1, store.CountAt(id));                             // the modifier really installed (non-vacuous)
        }

        [Fact]
        public void HostAlreadyAtCeilingZero_PositiveMaxHealthGrant_DoesNotKill()
        {
            // The sharper half of DW-491: a POSITIVE +MaxHealth grant killing its target. Set the host up with a
            // net-negative MaxHealth bonus through the 2.2a accumulator seam directly (NOT the store — the store's own
            // apply path is what we are about to exercise), so its ceiling is a legitimate, floored 0 while alive.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.Zero, Fixed.FromInt(4));
            sys.AccumulateBonus(id, Fixed.Zero, Fixed.FromInt(-50), Fixed.Zero);
            sys.RecomputeEntity(world, id);
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw); // max(0, 0 − 50) == 0, alive

            // +30 is a HEAL: net bonus −20 → the ceiling stays floored at 0. The old gate read only the absolute 0 and
            // killed the host on a positive grant (RED); a buff must never be lethal.
            store.Apply(id, MaxHpMod(2, Fixed.FromInt(30)), id, Faction.Player1);

            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(1, store.CountAt(id));
        }

        [Fact]
        public void AccumulatorWrapFromAPositiveGrant_IsNotLethal()
        {
            // DW-488's runtime outcome, seen from DW-491's side. ModifierSystem.AccumulateBonus sums with the WRAPPING
            // int `+=`, so two huge POSITIVE +MaxHealth contributions wrap the accumulator negative; DW-28's
            // AddSaturating then saturates `Base + wrappedNegative` to MinValue and the Zero-floor collapses the ceiling
            // from 100 to 0. Under the old sign-less gate the over-buffed host was KILLED (DW-488: "a strictly worse
            // divergent outcome" than the benign 0-ceiling zombie it used to be). A positive change is never lethal now.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            sys.AccumulateBonus(id, Fixed.Zero, Fixed.FromRaw(2_000_000_000), Fixed.Zero);
            sys.RecomputeEntity(world, id);
            Assert.True(world.EffectiveMaxHealth[id].Raw > 0); // still a real, positive ceiling before the wrap

            store.Apply(id, MaxHpMod(3, Fixed.FromRaw(2_000_000_000)), id, Faction.Player1);

            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw); // the accumulator really wrapped (non-vacuous)
            Assert.True(world.IsAlive(id));                                 // …but a +MaxHealth grant did not kill it
        }

        // ── Teeth: a GENUINE downward collapse must still kill (or the gate is just disabled) ──

        [Fact]
        public void GenuineCollapse_OnApply_StillKills()
        {
            // The DW-325 contract, re-pinned locally so this file cannot go vacuous: ceiling 100 → 0 driven by a
            // net-negative change is a real collapse and still raises the single combat death.
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            store.Apply(id, MaxHpMod(4, Fixed.FromInt(-100)), id, Faction.Player1);

            Assert.False(world.IsAlive(id));
            Assert.Equal(0, store.CountAt(id)); // OnDestroy→ClearEntity wiped the just-installed slot
        }

        [Fact]
        public void GenuineCollapse_OnExpiryRevert_StillKills()
        {
            // The removal arm of the same teeth: the prior-ceiling snapshot is taken inside ApplyStatDeltas, so it is
            // just as correct on the RemoveSlot path (revert −100 → ceiling 100 → 0) as on an apply.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // A +100 buff that expires on the first Advance, plus a longer-lived −100 debuff: net 0 while both are live.
            store.Apply(id, MaxHpMod(5, Fixed.FromInt(100), duration: 1), id, Faction.Player1);
            store.Apply(id, MaxHpMod(6, Fixed.FromInt(-100), duration: 5), id, Faction.Player1);
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);

            sys.Tick(world, Fixed.Zero); // the buff expires → revert −100 → ceiling collapses to 0 during the removal

            Assert.False(world.IsAlive(id));
            Assert.Equal(0, store.CountAt(id));
        }
    }
}
