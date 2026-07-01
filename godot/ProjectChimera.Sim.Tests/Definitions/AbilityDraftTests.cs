#nullable enable
using System;
using System.Text.Json;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // AbilityDraft, DraftNode, DraftKind, DraftModifier, DraftVocabulary, AbilityDefinition, AbilityValidator, AbilityLoader, ContentJson
using ProjectChimera.Effects;           // the closed effect vocabulary + Modifier + enums
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.5b (AC2 round-trip, AC5-COMPOSER closed vocabulary, AC6 Godot-free testability) — the structured
    /// composer's authoring model <see cref="AbilityDraft"/>/<see cref="DraftNode"/> is pure C# and Tier-1-tested
    /// without Godot: a draft tree MATERIALISES to the immutable runtime graph, serialises through the canonical
    /// <see cref="ContentJson.Options"/>, re-loads via <see cref="AbilityLoader"/>, and round-trips on
    /// <c>Fixed.Raw</c> + structure (judged by the shared <see cref="EffectGraphAssert"/>). The closed authorable
    /// sets are asserted to exclude the reserved values (the load-bearing AC5 defense), and every gate ships with
    /// teeth (a 0-child sequence is rejected by the real validator; an incomplete Search Area refuses to materialise).
    /// </summary>
    public class AbilityDraftTests
    {
        // ── AC2 round-trip: draft → AbilityDefinition → serialise(canonical) → load → identical graph ──

        [Fact]
        public void AC2Example_ComposeSerializeReload_RoundTripsIdentical()
        {
            // Sequence[ DirectHpDelta(self-cost), SearchArea(filter=Ally, radius) → Heal(amount) ] — the AC2 demo,
            // built entirely from the authoring model (the same shape the composer assembles from controls).
            var seq = new DraftNode { Kind = DraftKind.Sequence };
            seq.Children.Add(new DraftNode { Kind = DraftKind.DirectHpDelta, Delta = Fixed.FromInt(-15) });
            var search = new DraftNode { Kind = DraftKind.SearchArea, Radius = Fixed.FromInt(5), Filter = TargetFilter.Ally };
            search.Child = new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(20) };
            seq.Children.Add(search);

            AssertDraftRoundTrips(Draft("ac2_demo", "Self", seq));
        }

        [Fact]
        public void DirectHpDelta_RoundTrips() =>
            AssertDraftRoundTrips(Draft("d_directhp", "Self",
                new DraftNode { Kind = DraftKind.DirectHpDelta, Delta = Fixed.FromInt(-25) }));

        [Fact]
        public void Heal_RoundTrips() =>
            AssertDraftRoundTrips(Draft("d_heal", "Self",
                new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(40) }));

        [Theory]
        [InlineData(0)]  // Normal
        [InlineData(4)]  // Hero — IS authorable (only COUNT is excluded), so confirm it round-trips
        public void Damage_RoundTrips_ForAuthorableTypes(int damageTypeId) =>
            AssertDraftRoundTrips(Draft("d_damage", "TargetUnit",
                new DraftNode { Kind = DraftKind.Damage, Amount = Fixed.FromInt(80), DamageType = (DamageType)damageTypeId }));

        [Fact]
        public void ApplyModifier_AllFieldsNonDefault_RoundTrips()
        {
            var node = new DraftNode { Kind = DraftKind.ApplyModifier };
            node.Modifier = new DraftModifier
            {
                Id = 7, DurationTicks = 200, Stacking = StackRule.Stack, MaxStacks = 3,
                MaxHealthDelta = Fixed.FromInt(50), AttackDamageDelta = Fixed.FromInt(8), MoveSpeedDelta = Fixed.FromInt(2),
                Status = StatusFlags.Stunned, PeriodTicks = 10,
                Period = new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(3) },
            };
            AssertDraftRoundTrips(Draft("d_modifier", "Self", node));
        }

        [Fact]
        public void SearchArea_RoundTrips()
        {
            var search = new DraftNode { Kind = DraftKind.SearchArea, Radius = Fixed.FromInt(6), Filter = TargetFilter.Enemy };
            search.Child = new DraftNode { Kind = DraftKind.Damage, Amount = Fixed.FromInt(30), DamageType = DamageType.Magic };
            AssertDraftRoundTrips(Draft("d_search", "GroundPoint", search));
        }

        [Fact]
        public void Persistent_WithPhases_RoundTrips()
        {
            var node = new DraftNode
            {
                Kind = DraftKind.Persistent, PeriodTicks = 30, PeriodCount = 4,
                Initial = new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(10) },
                Period  = new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(5) },
                // Expire left null — exercises the omit-when-null branch.
            };
            AssertDraftRoundTrips(Draft("d_persistent", "Self", node));
        }

        // ── FromDefinition is the exact inverse of ToDefinition (the parse-in / load path, Task 2.2) ──

        [Fact]
        public void FromDefinition_IsInverseOf_ToDefinition()
        {
            // The fireball shape: Sequence[ Damage, SearchArea → Damage ].
            var def1 = new AbilityDefinition
            {
                Id = "inv", DisplayName = "Inverse", Targeting = "TargetUnit",
                CostEnergy = Fixed.FromInt(50), Cooldown = Fixed.FromInt(6),
                EffectGraph = new SequenceEffect(
                    new DamageEffect(Fixed.FromInt(80), DamageType.Magic),
                    new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Enemy,
                        new DamageEffect(Fixed.FromInt(30), DamageType.Magic))),
            };

            AbilityDraft draft = AbilityDraft.FromDefinition(def1);

            // Header + costs survive the parse-in.
            Assert.Equal(def1.Id, draft.Id);
            Assert.Equal(def1.DisplayName, draft.DisplayName);
            Assert.Equal(def1.Targeting, draft.Targeting);
            Assert.Equal(def1.CostEnergy.Raw, draft.CostEnergy.Raw);
            Assert.Equal(def1.Cooldown.Raw, draft.Cooldown.Raw);

            // Materialise back and assert the graph is structurally identical (Fixed.Raw + node kinds + order).
            AbilityDefinition def2 = draft.ToDefinition();
            EffectGraphAssert.Equal(def1.EffectGraph, def2.EffectGraph);
        }

        // ── Caps metrics (power the in-UI guardrail; the validator remains the gate) ──

        [Fact]
        public void Metrics_CountDepthSearchArea_AreCorrect()
        {
            // Sequence[ DirectHpDelta, SearchArea → Heal ]
            var seq = new DraftNode { Kind = DraftKind.Sequence };
            seq.Children.Add(new DraftNode { Kind = DraftKind.DirectHpDelta });
            var search = new DraftNode { Kind = DraftKind.SearchArea };
            search.Child = new DraftNode { Kind = DraftKind.Heal };
            seq.Children.Add(search);

            Assert.Equal(4, seq.CountNodes());        // seq + direct + search + heal
            Assert.Equal(3, seq.Depth());             // seq(1) → search(2) → heal(3)
            Assert.Equal(1, seq.SearchAreaDepth());   // one SearchArea on the deepest path
        }

        // ── Teeth: every gate observably rejects (inject a violation → it fails) ──

        [Fact]
        public void Teeth_EmptySequence_RejectedByTheValidator()
        {
            AbilityDefinition def = Draft("empty_seq", "Self", new DraftNode { Kind = DraftKind.Sequence }).ToDefinition();
            AbilityValidationResult vr = new AbilityValidator().Validate(def);
            Assert.False(vr.Ok);                       // a 0-child Sequence is an AbilityValidator.WalkGraph reject
            Assert.False(string.IsNullOrEmpty(vr.Error));
        }

        [Fact]
        public void Teeth_SearchAreaWithoutChild_RefusesToMaterialize()
        {
            var search = new DraftNode { Kind = DraftKind.SearchArea, Radius = Fixed.FromInt(4), Filter = TargetFilter.Enemy };
            Assert.Throws<InvalidOperationException>(() => search.ToEffectNode());   // required child slot is empty
        }

        // ── AC5-COMPOSER: the closed authorable sets exclude the reserved values (the load-bearing defense) ──

        [Fact]
        public void Vocabulary_DamageTypes_ExcludeTheCountSentinel()
        {
            Assert.DoesNotContain(DamageType.COUNT, DraftVocabulary.DamageTypes);
            Assert.Equal(5, DraftVocabulary.DamageTypes.Length);   // Normal, Pierce, Siege, Magic, Hero
        }

        [Fact]
        public void Vocabulary_Filters_IncludeTheDomainBits()
        {
            // Story 2.9a: the Air/Ground/Structure domain bits are now offered (evaluated by TargetMatcher).
            Assert.Contains(TargetFilter.Air, DraftVocabulary.Filters);
            Assert.Contains(TargetFilter.Ground, DraftVocabulary.Filters);
            Assert.Contains(TargetFilter.Structure, DraftVocabulary.Filters);
            // Each offered filter is still a single power-of-two bit (or None), so the checkbox set can OR any subset.
            foreach (TargetFilter f in DraftVocabulary.Filters)
            {
                int bits = (int)f;
                Assert.True(bits == 0 || (bits & (bits - 1)) == 0, $"{f} is not a single bit — the checkbox set assumes single flags.");
            }
        }

        [Fact]
        public void Vocabulary_Kinds_AreExactlyTheClosedSeven()
        {
            Assert.Equal(7, DraftVocabulary.Kinds.Length);
            Assert.Contains(DraftKind.DirectHpDelta, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.Heal, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.Damage, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.ApplyModifier, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.Sequence, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.SearchArea, DraftVocabulary.Kinds);
            Assert.Contains(DraftKind.Persistent, DraftVocabulary.Kinds);
        }

        // ── Helpers ──

        private static AbilityDraft Draft(string id, string targeting, DraftNode effect) => new AbilityDraft
        {
            Id = id, DisplayName = id, Targeting = targeting,
            CostEnergy = Fixed.FromInt(10), CostOre = 0, CostCrystal = 0, Cooldown = Fixed.FromInt(2),
            Effect = effect,
        };

        /// <summary>Materialise → validate (Ok) → serialise(canonical) → load → assert the parsed graph + scalar
        /// fields are identical (Fixed pinned by <c>.Raw</c>); the re-load also re-validates, so a round-trip that
        /// produced an invalid file would surface here as a located Fail.</summary>
        private static void AssertDraftRoundTrips(AbilityDraft draft)
        {
            AbilityDefinition def = draft.ToDefinition();

            AbilityValidationResult vr = new AbilityValidator().Validate(def);
            Assert.True(vr.Ok, vr.Error);

            string json = JsonSerializer.Serialize(def, ContentJson.Options);
            AbilityValidationResult r = AbilityLoader.Load(json, def.Id);
            Assert.True(r.Ok, r.Error);
            AbilityDefinition rt = r.Value.Value;

            Assert.Equal(def.Id, rt.Id);
            Assert.Equal(def.DisplayName, rt.DisplayName);
            Assert.Equal(def.ParsedTargeting, rt.ParsedTargeting);
            Assert.Equal(def.CostEnergy.Raw, rt.CostEnergy.Raw);
            Assert.Equal(def.CostOre, rt.CostOre);
            Assert.Equal(def.CostCrystal, rt.CostCrystal);
            Assert.Equal(def.Cooldown.Raw, rt.Cooldown.Raw);

            EffectGraphAssert.Equal(def.EffectGraph, rt.EffectGraph);
        }
    }
}
