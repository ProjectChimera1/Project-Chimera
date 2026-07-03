#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.11 (AC4 / D-4) — tag-conditional counters expressed in the closed vocabulary, both consumption sites:
    ///   • AREA (SearchArea.RequireTag): "heal only Organic" and "+X bonus vs Mechanical";
    ///   • SINGLE-TARGET (LeafEffect.RequireTag): a require_tag heal/damage leaf that no-ops on a non-matching primary
    ///     target (the D-4 gate in EffectExecutor).
    /// All deltas are Fixed; the heal path is flat (matrix-free) so exact; the damage path asserts robust relative
    /// inequalities (bonus lands on Mechanical only, base on everyone) so the test does not hard-code the damage matrix.
    /// </summary>
    public class TagCounterTests
    {
        // ── AREA — "heal only Organic" (SearchArea filter Ally + require_tag Organic) ──────────────────────

        [Fact]
        public void Area_HealOnlyOrganic_HealsOrganicAlly_SparesNonOrganic()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int organic = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // ally
            w.TagsOf[organic] = UnitTag.Organic;
            w.Health[organic] = Fixed.FromInt(50);
            int mech = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // ally, wrong tag
            w.TagsOf[mech] = UnitTag.Mechanical;
            w.Health[mech] = Fixed.FromInt(50);
            var sh = new SpatialHash(); sh.Rebuild(w);

            // "Heal only Organic" = search_area{ filter: Ally, require_tag: Organic, child: heal }.
            var graph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Ally,
                new HealEffect(Fixed.FromInt(20)), UnitTag.Organic);
            Run(graph, w, caster, Faction.Player1, sh);

            Assert.Equal(Fixed.FromInt(70).Raw, w.Health[organic].Raw); // 50 + 20 healed
            Assert.Equal(Fixed.FromInt(50).Raw, w.Health[mech].Raw);    // teeth: non-Organic ally NOT healed
        }

        // ── AREA — "+X bonus vs Mechanical" (base search hits all enemies; a second require_tag search adds the bonus) ──

        [Fact]
        public void Area_BonusVsMechanical_AddsBonusToMechanicalOnly()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int mech = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(500), Fixed.FromInt(3));
            w.TagsOf[mech] = UnitTag.Mechanical; w.ArmorTypeOf[mech] = ArmorType.Unarmored;
            int plain = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(500), Fixed.FromInt(3)); // None
            w.ArmorTypeOf[plain] = ArmorType.Unarmored;
            var sh = new SpatialHash(); sh.Rebuild(w);

            // sequence[ search_area{Enemy, damage base}, search_area{Enemy, require_tag:Mechanical, damage bonus} ].
            var graph = new SequenceEffect(new EffectNode[]
            {
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy, new DamageEffect(Fixed.FromInt(20), DamageType.Magic)),
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy, new DamageEffect(Fixed.FromInt(30), DamageType.Magic), UnitTag.Mechanical),
            });
            Run(graph, w, caster, Faction.Player1, sh);

            Assert.True(w.Health[plain].Raw < Fixed.FromInt(500).Raw);          // base hit everyone
            Assert.True(w.Health[mech].Raw < w.Health[plain].Raw);             // teeth: bonus landed on Mechanical ONLY
        }

        // ── SINGLE-TARGET (D-4) — LeafEffect.RequireTag gate ───────────────────────────────────────────────

        [Fact]
        public void SingleTarget_HealRequireOrganic_NoOpsOnMechanical_HealsOrganic()
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int organic = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[organic] = UnitTag.Organic; w.Health[organic] = Fixed.FromInt(50);
            int mech = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[mech] = UnitTag.Mechanical; w.Health[mech] = Fixed.FromInt(50);

            var heal = new HealEffect(Fixed.FromInt(20), UnitTag.Organic); // "heal only Organic" as a single-target leaf gate

            RunSingle(heal, w, caster, organic, Faction.Player1);
            Assert.Equal(Fixed.FromInt(70).Raw, w.Health[organic].Raw); // Organic target healed

            RunSingle(heal, w, caster, mech, Faction.Player1);
            Assert.Equal(Fixed.FromInt(50).Raw, w.Health[mech].Raw);    // teeth: Mechanical target is a whole no-op
        }

        [Fact]
        public void SingleTarget_BonusVsMechanical_LeafGate_AppliesBonusToMechanicalOnly()
        {
            // sequence[ damage base, damage{require_tag:Mechanical, bonus} ] — base to any target; bonus only if Mechanical.
            var graph = new SequenceEffect(new EffectNode[]
            {
                new DamageEffect(Fixed.FromInt(20), DamageType.Magic),
                new DamageEffect(Fixed.FromInt(30), DamageType.Magic, UnitTag.Mechanical),
            });

            Fixed mechHp   = DamageOneTarget(graph, UnitTag.Mechanical);
            Fixed plainHp  = DamageOneTarget(graph, UnitTag.None);

            Assert.True(plainHp.Raw < Fixed.FromInt(500).Raw);  // base hit the non-Mechanical target
            Assert.True(mechHp.Raw < plainHp.Raw);              // teeth: bonus landed ONLY on the Mechanical target
        }

        // ── SINGLE-TARGET (D-4) — apply_modifier leaf gate (the SEPARATE EffectExecutor dispatch case, review C3) ──

        [Fact]
        public void SingleTarget_ApplyModifierRequireOrganic_NoOpsOnMechanical_InstallsOnOrganic()
        {
            var w = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(w, sys);
            sys.AttachStore(store);

            int caster  = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int organic = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[organic] = UnitTag.Organic;
            int mech    = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[mech] = UnitTag.Mechanical;

            // "buff only Organic" as a single-target apply_modifier leaf gate. apply_modifier is dispatched by its OWN
            // executor case (before the generic LeafEffect case), so this is the ONLY teeth on that gate site.
            var buff = new ApplyModifierEffect(
                new Modifier(1, 10, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero, StatusFlags.None, null, 0),
                UnitTag.Organic);

            RunSingleWithStore(buff, w, caster, organic, store);
            Assert.Equal(1, store.CountAt(organic)); // Organic target buffed

            RunSingleWithStore(buff, w, caster, mech, store);
            Assert.Equal(0, store.CountAt(mech));    // teeth: Mechanical target is a whole no-op (RED if EffectExecutor's apply_modifier gate is removed)
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────────────

        private static void Run(EffectNode graph, EntityWorld w, int caster, Faction f, SpatialHash sh)
        {
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, caster, f, DamageTable.Default, sh);
            ex.Run(graph, in ctx);
        }

        private static void RunSingle(EffectNode leafOrGraph, EntityWorld w, int caster, int target, Faction f)
        {
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, target, f, DamageTable.Default); // PrimaryTargetId = the single target
            ex.Run(leafOrGraph, in ctx);
        }

        // Single-target run WITH a ModifierStore in context (for the apply_modifier leaf gate, review C3).
        private static void RunSingleWithStore(EffectNode leaf, EntityWorld w, int caster, int target, ModifierStore store)
        {
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, casterId: caster, primaryTargetId: target, casterFaction: Faction.Player1,
                                        damageTable: DamageTable.Default, spatial: null, events: null, stats: null, modifierStore: store);
            ex.Run(leaf, in ctx);
        }

        /// <summary>Run <paramref name="graph"/> single-target against a fresh 500-HP Unarmored enemy of the given tag; return its Health.</summary>
        private static Fixed DamageOneTarget(EffectNode graph, UnitTag tag)
        {
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int target = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(500), Fixed.FromInt(3));
            w.TagsOf[target] = tag; w.ArmorTypeOf[target] = ArmorType.Unarmored;
            RunSingle(graph, w, caster, target, Faction.Player1);
            return w.Health[target];
        }
    }
}
