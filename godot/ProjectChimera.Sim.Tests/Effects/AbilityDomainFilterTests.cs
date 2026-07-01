#nullable enable
using System.Linq;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.9a (AC6 / D-1) — the ability-side <c>SearchArea</c> domain filter. The reserved
    /// <see cref="TargetFilter.Air"/>/<see cref="TargetFilter.Ground"/>/<see cref="TargetFilter.Structure"/> bits are
    /// now EVALUATED by <c>TargetMatcher</c> via the SAME <see cref="DomainClassifier"/> the combat AC1 filter uses.
    /// Verified through the public <see cref="SearchAreaEffect.FindTargets"/> fan-out. "No domain bit = all domains",
    /// so a filter with domain=0 is byte-identical to pre-2.9a (AC6.1). Godot-free, Fixed-only.
    /// </summary>
    public class AbilityDomainFilterTests
    {
        private static EffectContext Setup(out EntityWorld w, out int air, out int ground)
        {
            w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            air = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.CategoryOf[air] = UnitCategory.Air;
            ground = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // CategoryOf defaults to Melee (ground)
            var sh = new SpatialHash();
            sh.Rebuild(w);
            return new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
        }

        private static int[] Hits(TargetFilter filter, in EffectContext ctx)
        {
            var probe = new int[EffectCaps.MaxHitsPerSearch];
            var search = new SearchAreaEffect(Fixed.FromInt(10), filter, new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            int n = search.FindTargets(in ctx, probe);
            return probe.Take(n).ToArray();
        }

        [Fact]
        public void SearchArea_AirFilter_SelectsOnlyFliers()
        {
            EffectContext ctx = Setup(out _, out int air, out int ground);
            int[] hits = Hits(TargetFilter.Enemy | TargetFilter.Air, in ctx);
            Assert.Contains(air, hits);
            Assert.DoesNotContain(ground, hits); // teeth: a Ground candidate is excluded from an Air filter
        }

        [Fact]
        public void SearchArea_GroundFilter_SparesFliers()
        {
            EffectContext ctx = Setup(out _, out int air, out int ground);
            int[] hits = Hits(TargetFilter.Enemy | TargetFilter.Ground, in ctx);
            Assert.Contains(ground, hits);
            Assert.DoesNotContain(air, hits); // teeth: an Air candidate is spared by a Ground filter
        }

        [Fact]
        public void SearchArea_NoDomainBit_SelectsAllDomains_ByteIdenticalToBefore()
        {
            EffectContext ctx = Setup(out _, out int air, out int ground);
            int[] hits = Hits(TargetFilter.Enemy, in ctx); // domain=0 → every existing SearchArea behaves as before
            Assert.Contains(air, hits);
            Assert.Contains(ground, hits);
        }
    }
}
