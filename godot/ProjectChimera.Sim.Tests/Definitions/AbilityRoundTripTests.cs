#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // AbilityLoader, AbilityDefinition, ContentJson, AbilityValidationResult
using ProjectChimera.Effects;           // the closed effect vocabulary
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.5a (AC2-RAWJSON, AC6a) — the authoring serialize path round-trips: an in-memory
    /// <see cref="AbilityDefinition"/> serialized through the canonical <see cref="ContentJson.Options"/> (which
    /// now routes the effect graph through the completed <see cref="EffectNodeJsonConverter"/>.<c>Write</c>) re-loads
    /// via <see cref="AbilityLoader"/> into a STRUCTURALLY IDENTICAL graph — same node kinds, same <c>Fixed.Raw</c>
    /// values, same children/order. The 3 shipped samples + the two hand-built cases (<c>direct_hp_delta</c> and
    /// <c>persistent</c>, the kinds no sample or preset reaches) exercise ALL 7 <c>Write</c> branches behaviorally.
    /// </summary>
    public class AbilityRoundTripTests
    {
        // ── The 3 shipped samples: Load → serialize(canonical) → Load → identical graph. ──
        [Theory]
        [InlineData("fireball.json")]     // sequence → [ damage, search_area → damage ]
        [InlineData("minor_heal.json")]   // single heal leaf (omits optional costs → defaults)
        [InlineData("battle_fury.json")]  // apply_modifier with stat deltas
        [InlineData("aura_guard.json")]      // Story 2.6 aura: search_area(Ally) → apply_modifier(+armor)
        [InlineData("onhit_searing.json")]   // Story 2.6 on_hit: a single damage rider
        [InlineData("furnace_trickle.json")] // Story 2.6 while_alive: persistent HoT
        public void SampleAbility_SurvivesSerializeRoundTrip_WithIdenticalGraph(string fileName)
        {
            string path = Path.Combine(AbilitiesResourceDir(), fileName);

            AbilityValidationResult first = AbilityLoader.LoadFromFile(path);
            Assert.True(first.Ok, first.Error);
            AssertSerializeRoundTrips(first.Value.Value, fileName);
        }

        // ── Hand-built direct_hp_delta (the equal-exchange self-cost shape; no sample/preset reaches it). ──
        [Fact]
        public void DirectHpDelta_SurvivesSerializeRoundTrip()
        {
            var original = new AbilityDefinition
            {
                Id          = "test_direct_hp_delta",
                DisplayName = "Test Direct HP Delta",
                Targeting   = "Self",
                CostEnergy  = Fixed.FromInt(10),
                Cooldown    = Fixed.FromInt(2),
                EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(-25)), // flat, armor-independent self-cost
            };
            AssertSerializeRoundTrips(original, original.Id);
        }

        // ── Hand-built persistent (nested phase children + a null phase → exercises the omit-when-null Write path). ──
        [Fact]
        public void Persistent_SurvivesSerializeRoundTrip()
        {
            var original = new AbilityDefinition
            {
                Id          = "test_persistent",
                DisplayName = "Test Persistent",
                Targeting   = "Self",
                CostEnergy  = Fixed.FromInt(15),
                Cooldown    = Fixed.FromInt(5),
                EffectGraph = new PersistentEffect(
                    initialEffect: new HealEffect(Fixed.FromInt(10)),
                    periodEffect:  new HealEffect(Fixed.FromInt(5)),
                    expireEffect:  null,           // exercises the omit-when-null nullable-child branch
                    periodTicks:   30,
                    periodCount:   4),
            };
            AssertSerializeRoundTrips(original, original.Id);
        }

        // ── Hand-built apply_modifier with EVERY Modifier field at a NON-default value. Closes the coverage gap where ──
        // ── across all other inputs 6 of the 10 modifier fields sat at their Read-fallback (max_stacks=1,           ──
        // ── max_health_delta=0, stacking=Refresh, status=None, period_ticks=0, period_effect=null), so a WriteModifier ──
        // ── regression that omitted any of those would still round-trip green. Here each is distinct + non-fallback. ──
        [Fact]
        public void ApplyModifier_AllFieldsNonDefault_SurvivesSerializeRoundTrip()
        {
            var original = new AbilityDefinition
            {
                Id          = "test_apply_modifier_full",
                DisplayName = "Test Apply Modifier Full",
                Targeting   = "Self",
                CostEnergy  = Fixed.FromInt(20),
                Cooldown    = Fixed.FromInt(8),
                EffectGraph = new ApplyModifierEffect(new Modifier(
                    id:                7,
                    durationTicks:     200,
                    stacking:          StackRule.Stack,                       // ≠ Refresh
                    maxStacks:         3,                                      // ≠ 1
                    maxHealthDelta:    Fixed.FromInt(50),                      // ≠ 0
                    attackDamageDelta: Fixed.FromInt(8),
                    moveSpeedDelta:    Fixed.FromInt(2),                       // ≠ 0
                    status:            StatusFlags.Stunned | StatusFlags.Rooted, // ≠ None (OR-combo)
                    periodEffect:      new HealEffect(Fixed.FromInt(3)),       // ≠ null (store-run HoT)
                    periodTicks:       10)),                                   // ≠ 0
            };
            AssertSerializeRoundTrips(original, original.Id);
        }

        /// <summary>
        /// Serialize through the canonical options, re-load through the fail-closed loader, and assert the parsed
        /// graph + scalar fields are identical (Fixed pinned by .Raw). The re-load also re-validates, so a round-trip
        /// that produced an invalid file would surface here as a located Fail.
        /// </summary>
        private static void AssertSerializeRoundTrips(AbilityDefinition original, string label)
        {
            string json = JsonSerializer.Serialize(original, ContentJson.Options);

            AbilityValidationResult r = AbilityLoader.Load(json, label);
            Assert.True(r.Ok, r.Error);
            AbilityDefinition rt = r.Value.Value;

            Assert.Equal(original.Id, rt.Id);
            Assert.Equal(original.DisplayName, rt.DisplayName);
            Assert.Equal(original.ParsedTargeting, rt.ParsedTargeting);
            Assert.Equal(original.ParsedActivation, rt.ParsedActivation);   // Story 2.6
            Assert.Equal(original.CostEnergy.Raw, rt.CostEnergy.Raw);
            Assert.Equal(original.CostOre, rt.CostOre);
            Assert.Equal(original.CostCrystal, rt.CostCrystal);
            Assert.Equal(original.Cooldown.Raw, rt.Cooldown.Raw);

            EffectGraphAssert.Equal(original.EffectGraph, rt.EffectGraph);
        }

        // <repo>/godot/ProjectChimera.Sim.Tests/Definitions/THIS.cs → <repo>/godot/resources/data/abilities
        private static string AbilitiesResourceDir([CallerFilePath] string thisFile = "")
        {
            string defsDir  = Path.GetDirectoryName(thisFile)!;
            string testProj = Path.GetDirectoryName(defsDir)!;
            string godot    = Path.GetDirectoryName(testProj)!;
            return Path.Combine(godot, "resources", "data", "abilities");
        }
    }
}
