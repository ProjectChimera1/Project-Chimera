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
    /// Story 2.11 (AC3) — the SearchArea <c>require_tag</c> predicate. A tag-filtered SearchArea collects ONLY entities
    /// whose <c>TagsOf</c> INTERSECTS the required set (match if ANY required bit is set), in ascending-id order;
    /// <c>require_tag == None</c> imposes no constraint (byte-identical to a pre-2.11 SearchArea). Verified through the
    /// public <see cref="SearchAreaEffect.FindTargets"/> fan-out (mirrors <c>AbilityDomainFilterTests</c>). Godot-free,
    /// integer-only.
    /// </summary>
    public class TagFilterSearchAreaTests
    {
        private static EffectContext Setup(out EntityWorld w, out int mech, out int organic, out int none)
        {
            w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            mech = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[mech] = UnitTag.Mechanical;
            organic = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            w.TagsOf[organic] = UnitTag.Organic;
            none = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // TagsOf defaults to None
            var sh = new SpatialHash();
            sh.Rebuild(w);
            return new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);
        }

        private static int[] Hits(TargetFilter filter, UnitTag requireTag, in EffectContext ctx)
        {
            var probe = new int[EffectCaps.MaxHitsPerSearch];
            var search = new SearchAreaEffect(Fixed.FromInt(10), filter, new DirectHpDeltaEffect(Fixed.FromInt(-1)), requireTag);
            int n = search.FindTargets(in ctx, probe);
            return probe.Take(n).ToArray();
        }

        [Fact]
        public void RequireTag_Mechanical_CollectsOnlyMechanical()   // AC3 — exact count + membership; teeth
        {
            EffectContext ctx = Setup(out _, out int mech, out int organic, out int none);
            int[] hits = Hits(TargetFilter.Enemy, UnitTag.Mechanical, in ctx);
            Assert.Equal(new[] { mech }, hits);   // EXACTLY the Mechanical id — RED if the TargetMatcher tag check is removed
            Assert.DoesNotContain(organic, hits); // teeth: Organic excluded
            Assert.DoesNotContain(none, hits);    // teeth: untagged excluded
        }

        [Fact]
        public void RequireTag_None_CollectsEveryone_ByteIdenticalToBefore()   // AC3.3 back-compat
        {
            EffectContext ctx = Setup(out _, out int mech, out int organic, out int none);
            int[] hits = Hits(TargetFilter.Enemy, UnitTag.None, in ctx);
            Assert.Contains(mech, hits);
            Assert.Contains(organic, hits);
            Assert.Contains(none, hits); // untagged INCLUDED when no tag required (pre-2.11 behaviour)
        }

        [Fact]
        public void RequireTag_Intersects_MatchesAnyRequiredBit_AscendingId()   // AC3.2 intersection + AC3.3 ordering
        {
            EffectContext ctx = Setup(out _, out int mech, out int organic, out int none);
            // Require Mechanical|Organic: both units match (any bit intersects); the untagged one is excluded.
            int[] hits = Hits(TargetFilter.Enemy, UnitTag.Mechanical | UnitTag.Organic, in ctx);
            Assert.Equal(new[] { mech, organic }, hits); // ascending-id order preserved (mech < organic)
            Assert.DoesNotContain(none, hits);
        }

        [Fact]
        public void MultiTaggedUnit_MatchesEitherRequiredTag()   // AC1.2 + AC3.2 — a unit carrying several tags
        {
            EffectContext ctx = Setup(out EntityWorld w, out int mech, out _, out _);
            w.TagsOf[mech] = UnitTag.Mechanical | UnitTag.Magical; // a Mechanical+Magical unit
            Assert.Contains(mech, Hits(TargetFilter.Enemy, UnitTag.Magical, in ctx));    // matches via the Magical bit
            Assert.Contains(mech, Hits(TargetFilter.Enemy, UnitTag.Mechanical, in ctx)); // and the Mechanical bit
        }
    }
}
