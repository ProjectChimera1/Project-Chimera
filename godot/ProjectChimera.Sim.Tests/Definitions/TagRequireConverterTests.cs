#nullable enable
using System.Text.Json;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.11 (AC3.4 / AC4.2) — the converter learns <c>require_tag</c> for <c>search_area</c> AND every leaf kind:
    /// it reads a single <see cref="UnitTag"/> enum string, round-trips (Read→Write→Read identity), OMITS the key when
    /// None (so pre-2.11 nodes are byte-identical), and rejects an unknown token as a LOCATED reject (fail-closed via the
    /// name-only enum converter). Godot-free.
    /// </summary>
    public class TagRequireConverterTests
    {
        // Deserialize an effect node directly (isolating the CONVERTER), mirroring EffectNodeConverterTests.Compile.
        private static EffectNode? Compile(string effectJson)
        {
            string json = $$"""{ "id": "t", "targeting": "Self", "effect": {{effectJson}} }""";
            return JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options)!.EffectGraph;
        }

        // Serialize AS EffectNode (typeof forces the closed-registry converter's Write) then re-Read — a true round-trip.
        private static EffectNode RoundTrip(EffectNode node) =>
            JsonSerializer.Deserialize<EffectNode>(
                JsonSerializer.Serialize(node, typeof(EffectNode), ContentJson.Options), ContentJson.Options)!;

        [Fact]
        public void SearchArea_RequireTag_ReadsByName()
        {
            var s = Assert.IsType<SearchAreaEffect>(Compile(
                """{ "kind": "search_area", "radius": 4, "filter": "Enemy", "require_tag": "Mechanical", "child": { "kind": "heal", "amount": 1 } }"""));
            Assert.Equal(UnitTag.Mechanical, s.RequireTag);
        }

        [Fact]
        public void SearchArea_RequireTag_RoundTripsIdentity()
        {
            var original = new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Enemy,
                new DamageEffect(Fixed.FromInt(10), DamageType.Magic), UnitTag.Mechanical);
            var rt = Assert.IsType<SearchAreaEffect>(RoundTrip(original));
            Assert.Equal(UnitTag.Mechanical, rt.RequireTag);
            Assert.Equal(TargetFilter.Enemy, rt.Filter);
            Assert.Equal(original.Radius.Raw, rt.Radius.Raw);
        }

        [Fact]
        public void Leaf_RequireTag_ReadsByName_AndRoundTrips()   // AC4.2 — the single-target leaf gate key
        {
            var d = Assert.IsType<DamageEffect>(Compile(
                """{ "kind": "damage", "amount": 20, "damage_type": "Magic", "require_tag": "Mechanical" }"""));
            Assert.Equal(UnitTag.Mechanical, d.RequireTag);

            var rt = Assert.IsType<DamageEffect>(RoundTrip(d));
            Assert.Equal(UnitTag.Mechanical, rt.RequireTag);
            Assert.Equal(DamageType.Magic, rt.Type);

            var h = Assert.IsType<HealEffect>(Compile("""{ "kind": "heal", "amount": 5, "require_tag": "Organic" }"""));
            Assert.Equal(UnitTag.Organic, h.RequireTag);
            Assert.Equal(UnitTag.Organic, Assert.IsType<HealEffect>(RoundTrip(h)).RequireTag);
        }

        [Fact]
        public void NoRequireTag_DefaultsToNone_AndIsOmittedOnWrite()   // AC3.3 / back-compat — omit-when-None
        {
            var s = Assert.IsType<SearchAreaEffect>(Compile(
                """{ "kind": "search_area", "radius": 4, "filter": "Enemy", "child": { "kind": "heal", "amount": 1 } }"""));
            Assert.Equal(UnitTag.None, s.RequireTag); // back-compat: no key → None

            string j = JsonSerializer.Serialize(s, typeof(EffectNode), ContentJson.Options);
            Assert.DoesNotContain("require_tag", j);  // omitted when None → existing search_area abilities byte-identical
        }

        [Fact]
        public void UnknownRequireTagToken_OnSearchArea_IsLocatedReject()   // fail-closed via the name-only enum converter
        {
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "bt", "targeting": "Self", "effect": { "kind": "search_area", "radius": 4, "filter": "Enemy", "require_tag": "Undead", "child": { "kind": "heal", "amount": 1 } } }""", "bt");
            Assert.False(r.Ok);
            Assert.Contains("require_tag", r.Error!);
        }

        [Fact]
        public void UnknownRequireTagToken_OnLeaf_IsLocatedReject()
        {
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "bt", "targeting": "Self", "effect": { "kind": "damage", "amount": 1, "damage_type": "Normal", "require_tag": "Bogus" } }""", "bt");
            Assert.False(r.Ok);
            Assert.Contains("require_tag", r.Error!);
        }

        [Fact]
        public void ApplyModifier_RequireTag_RoundTripsIdentity()   // review C4 — apply_modifier's OWN converter branch (Write + Read allow-list)
        {
            // apply_modifier is a DISTINCT converter Write/Read case (not shared with the other leaves), so its
            // require_tag wiring needs its own round-trip: Write emits require_tag; Read allow-lists + parses it.
            var original = new ApplyModifierEffect(
                new Modifier(7, 90, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero, StatusFlags.None, null, 0),
                UnitTag.Organic);
            var rt = Assert.IsType<ApplyModifierEffect>(RoundTrip(original));
            Assert.Equal(UnitTag.Organic, rt.RequireTag);   // RED if WriteRequireTag is dropped from the apply_modifier Write, or require_tag from its Read allow-list
        }
    }
}
