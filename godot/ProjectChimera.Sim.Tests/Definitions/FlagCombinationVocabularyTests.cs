#nullable enable
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // DraftVocabulary, AbilityDefinition, AbilityLoader, ContentJson, AbilityValidationResult
using ProjectChimera.Effects;           // TargetFilter, StatusFlags, SearchAreaEffect, ApplyModifierEffect, Modifier, StackRule
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.6 (Task 8 / AC5) — the closed-vocabulary + flag-combination teeth for the editor's new multi-select
    /// checkbox UI (<c>AddFlagChecks</c>). The UI itself is Godot and verified via <c>/godot-verify</c> (Task 9), but
    /// the two GUARANTEES it rests on are Godot-free and pinned here:
    ///   1. the OFFERED filter/status bits are the CLOSED sets — since Story 2.9a the filter set INCLUDES the
    ///      <see cref="TargetFilter.Air"/>/<see cref="TargetFilter.Ground"/>/<see cref="TargetFilter.Structure"/> domain bits;
    ///   2. a composed <c>[Flags]</c> COMBINATION round-trips through <see cref="ContentJson.Options"/> +
    ///      <see cref="AbilityLoader"/> with both bits intact (the checkbox set ORs bits together — the file must keep them).
    /// Because the composer can only build a value from <see cref="DraftVocabulary"/>, (1) is the load-bearing AC5
    /// defense and (2) proves the on-disk format carries combinations (no silent collapse to a single bit).
    /// </summary>
    public class FlagCombinationVocabularyTests
    {
        // ── (1) Closed-set teeth — the offered filter set IS the closed allegiance+Alive+domain set (AC5). Story
        //        2.9a: the Air/Ground/Structure bits are now OFFERED (evaluated by TargetMatcher), no longer reserved. ──

        [Fact]
        public void DraftVocabulary_FilterSet_IsTheClosedAllegiancePlusDomainSet()
        {
            // Story 2.9a: the domain bits ARE now offered to the creator (evaluated by TargetMatcher).
            Assert.Contains(TargetFilter.Air,       DraftVocabulary.Filters);
            Assert.Contains(TargetFilter.Ground,    DraftVocabulary.Filters);
            Assert.Contains(TargetFilter.Structure, DraftVocabulary.Filters);

            // Exactly the allegiance + Alive + domain set is offered (closed vocabulary).
            Assert.Equal(
                new[] { TargetFilter.None, TargetFilter.Self, TargetFilter.Ally, TargetFilter.Enemy,
                        TargetFilter.Neutral, TargetFilter.Alive,
                        TargetFilter.Air, TargetFilter.Ground, TargetFilter.Structure },
                DraftVocabulary.Filters);
        }

        [Fact]
        public void DraftVocabulary_StatusSet_IsTheClosedStatusSet()
        {
            Assert.Equal(
                new[] { StatusFlags.None, StatusFlags.Stunned, StatusFlags.Rooted,
                        StatusFlags.Silenced, StatusFlags.Disarmed, StatusFlags.Invulnerable },
                DraftVocabulary.Statuses);

            // The checkbox UI skips the zero/None value (it is "no boxes checked"); every OTHER offered bit is a real
            // single power-of-two flag, so OR-ing any subset is a valid closed combination.
            foreach (StatusFlags s in DraftVocabulary.Statuses.Where(s => s != StatusFlags.None))
            {
                int bits = (int)s;
                Assert.True(bits != 0 && (bits & (bits - 1)) == 0, $"{s} is not a single bit — the checkbox set assumes single flags.");
            }
        }

        // ── (2) A composed [Flags] combination round-trips through the canonical converter + loader. ──

        [Fact]
        public void MultiBitTargetFilter_RoundTripsThroughConverter_PreservingBothBits()
        {
            // An aura whose Search Area filter is a TWO-BIT combination (Ally + Alive) — exactly what the checkbox set
            // produces. No existing test round-trips a multi-bit TargetFilter, so this is the new teeth.
            var def = new AbilityDefinition
            {
                Id = "test_filter_combo", DisplayName = "Filter Combo", Targeting = "None", Activation = "aura",
                EffectGraph = new SearchAreaEffect(
                    Fixed.FromInt(5), TargetFilter.Ally | TargetFilter.Alive,
                    new ApplyModifierEffect(new Modifier(
                        1, 2, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                        StatusFlags.None, null, 0, Fixed.FromInt(5)))),
            };

            AbilityDefinition rt = RoundTrip(def);
            var sa = Assert.IsType<SearchAreaEffect>(rt.EffectGraph);
            Assert.Equal(TargetFilter.Ally | TargetFilter.Alive, sa.Filter); // both bits survived
        }

        [Fact]
        public void MultiBitStatusFlags_RoundTripsThroughConverter_PreservingBothBits()
        {
            var def = new AbilityDefinition
            {
                Id = "test_status_combo", DisplayName = "Status Combo", Targeting = "Self", Activation = "active",
                CostEnergy = Fixed.FromInt(10), Cooldown = Fixed.FromInt(2),
                EffectGraph = new ApplyModifierEffect(new Modifier(
                    1, 100, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                    StatusFlags.Stunned | StatusFlags.Silenced, null, 0)),
            };

            AbilityDefinition rt = RoundTrip(def);
            var am = Assert.IsType<ApplyModifierEffect>(rt.EffectGraph);
            Assert.Equal(StatusFlags.Stunned | StatusFlags.Silenced, am.Modifier.Status); // both bits survived
        }

        /// <summary>Serialize through the canonical options, re-load through the fail-closed loader, return the parsed def.</summary>
        private static AbilityDefinition RoundTrip(AbilityDefinition def)
        {
            string json = JsonSerializer.Serialize(def, ContentJson.Options);
            AbilityValidationResult r = AbilityLoader.Load(json, def.Id);
            Assert.True(r.Ok, r.Error);
            return r.Value.Value;
        }
    }
}
