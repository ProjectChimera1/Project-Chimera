#nullable enable
using System;
using System.Reflection;                // DW-297 — the deleted-metric guard reads DraftNode's public surface
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
    /// sets are asserted to be the closed authorable vocabulary — still excluding the internal <c>DamageType.COUNT</c>
    /// sentinel, and (Story 2.9a) now INCLUDING the Air/Ground/Structure domain bits (the load-bearing AC5 defense), and every gate ships with
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

        // ── Caps metric (powers the in-UI guardrail; the validator remains the gate) ──

        [Fact]
        public void Metrics_CountNodes_CountsEverySlot()
        {
            // Sequence[ DirectHpDelta, SearchArea → Heal ]
            var seq = new DraftNode { Kind = DraftKind.Sequence };
            seq.Children.Add(new DraftNode { Kind = DraftKind.DirectHpDelta });
            var search = new DraftNode { Kind = DraftKind.SearchArea };
            search.Child = new DraftNode { Kind = DraftKind.Heal };
            seq.Children.Add(search);

            Assert.Equal(4, seq.CountNodes());        // seq + direct + search + heal
        }

        // ── DW-297: the surviving metric is PINNED against the authoritative gate, not hand-counted literals ──

        /// <summary>
        /// DW-297 — <see cref="DraftNode.CountNodes"/> is the number the composer spends its
        /// <see cref="EffectCaps.MaxTotalEffectNodes"/> budget against, so it must mean exactly what
        /// <c>AbilityValidator.WalkGraph</c>'s tally means. Pinned AT THE BOUNDARY from both sides with the one shape
        /// the two walks could plausibly disagree on: an <c>ApplyModifier</c>'s period subtree (a structural leaf to
        /// <c>EffectBounds</c>, but descended by the validator's node tally — and therefore by CountNodes too).
        /// Teeth: make CountNodes stop descending <c>Modifier.Period</c> and BOTH drafts collapse to 56, so both
        /// equality assertions go RED — and the panel would silently offer adds past a budget the save-time gate has
        /// already spent.
        /// </summary>
        [Fact]
        public void CountNodes_AgreesWithTheValidatorTally_AtTheMaxTotalEffectNodesBoundary()
        {
            // Exactly at the cap: root Sequence(1) + 6 × Sequence-of-8-Heals(6 × 9 = 54)
            //                     + ApplyModifier(1) whose period subtree is Sequence-of-7-Heals(1 + 7 = 8) → 64.
            DraftNode atCap = SequenceOfSequences(sequenceChildren: 6, healsPerSequence: 8, modifierPeriodHeals: 7);
            Assert.Equal(EffectCaps.MaxTotalEffectNodes, atCap.CountNodes());

            AbilityValidationResult okr = new AbilityValidator().Validate(Draft("caps_at", "Self", atCap).ToDefinition());
            Assert.True(okr.Ok, okr.Error);                       // the validator's own tally agrees: 64 is not over

            // One node over: the SAME shape with one extra Heal in the modifier's period subtree.
            DraftNode overCap = SequenceOfSequences(sequenceChildren: 6, healsPerSequence: 8, modifierPeriodHeals: 8);
            Assert.Equal(EffectCaps.MaxTotalEffectNodes + 1, overCap.CountNodes());

            AbilityValidationResult bad = new AbilityValidator().Validate(Draft("caps_over", "Self", overCap).ToDefinition());
            Assert.False(bad.Ok);                                 // …and 65 IS over — the extra node was counted by both
            Assert.Contains("MaxTotalEffectNodes", bad.Error!);
        }

        /// <summary>
        /// DW-297 — the two dead metrics (<c>DraftNode.Depth()</c> / <c>DraftNode.SearchAreaDepth()</c>) are gone and
        /// must not creep back. They were referenced by nothing but their own assertions, and <c>Depth()</c> did not
        /// even agree with the cap it named: it counted every node on a path including the terminal leaf, while
        /// <see cref="EffectCaps.MaxEffectDepth"/> counts only COMPOSITION nodes (<c>EffectBounds</c>: root = 0, leaves
        /// contribute nothing), so a legal <c>MaxEffectDepth</c>-deep chain measured <c>MaxEffectDepth + 1</c>.
        /// The panel derives both depths TOP-DOWN via its own <c>TreeCtx</c>, which is the only context an add-affordance
        /// has. Re-adding a subtree depth metric is allowed — but only pinned against <c>EffectBounds</c>, which is why
        /// this guard names the semantics rather than just the member.
        /// </summary>
        [Fact]
        public void DeadDepthMetrics_AreNotReintroducedUnpinned()
        {
            foreach (string dead in new[] { "Depth", "SearchAreaDepth" })
                Assert.Null(typeof(DraftNode).GetMethod(dead, BindingFlags.Public | BindingFlags.Instance));
        }

        /// <summary>
        /// The composition-depth semantics the panel's guardrail is written against, asserted on the AUTHORITATIVE
        /// enforcer so the comment in <c>AbilityDraft</c> can never quietly go stale: a chain of
        /// <see cref="EffectCaps.MaxEffectDepth"/> nested compositions ending in a leaf LOADS (the leaf costs no depth),
        /// and one more composition does not. This is the fact the deleted <c>Depth()</c> got wrong.
        /// </summary>
        [Fact]
        public void MaxEffectDepth_CountsCompositionsOnly_NotTheTerminalLeaf()
        {
            Assert.True(EffectBounds.Validate(NestedSequences(EffectCaps.MaxEffectDepth)).IsValid);

            EffectBoundsResult over = EffectBounds.Validate(NestedSequences(EffectCaps.MaxEffectDepth + 1));
            Assert.False(over.IsValid);
            Assert.Contains("MaxEffectDepth", over.Error!);
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

        /// <summary>
        /// DW-297 — a draft whose node count is dialled to a precise total, built ONLY from shapes both the composer
        /// and <c>AbilityValidator</c> accept: a root Sequence holding <paramref name="sequenceChildren"/> Sequences of
        /// <paramref name="healsPerSequence"/> Heals, plus one ApplyModifier whose period effect is a Sequence of
        /// <paramref name="modifierPeriodHeals"/> Heals (the subtree only the node tally descends).
        /// Total = 1 + sequenceChildren × (1 + healsPerSequence) + 1 + (1 + modifierPeriodHeals).
        /// </summary>
        private static DraftNode SequenceOfSequences(int sequenceChildren, int healsPerSequence, int modifierPeriodHeals)
        {
            var root = new DraftNode { Kind = DraftKind.Sequence };
            for (int i = 0; i < sequenceChildren; i++)
            {
                var inner = new DraftNode { Kind = DraftKind.Sequence };
                for (int k = 0; k < healsPerSequence; k++)
                    inner.Children.Add(new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(1) });
                root.Children.Add(inner);
            }

            var period = new DraftNode { Kind = DraftKind.Sequence };
            for (int k = 0; k < modifierPeriodHeals; k++)
                period.Children.Add(new DraftNode { Kind = DraftKind.Heal, Amount = Fixed.FromInt(1) });

            var mod = new DraftNode { Kind = DraftKind.ApplyModifier };
            mod.Modifier = new DraftModifier
            {
                // duration != 0 and period_ticks > 0 keep the DW-278 warning and the DW-504 period-shape REJECT out of
                // the picture, so the only thing under test here is the node tally.
                Id = 1, DurationTicks = 100, Stacking = StackRule.Refresh, MaxStacks = 1,
                AttackDamageDelta = Fixed.FromInt(1), PeriodTicks = 10, Period = period,
            };
            root.Children.Add(mod);
            return root;
        }

        /// <summary>A chain of <paramref name="compositions"/> nested <c>SequenceEffect</c>s terminating in a single
        /// leaf — the shape <see cref="EffectCaps.MaxEffectDepth"/> is defined against.</summary>
        private static EffectNode NestedSequences(int compositions)
        {
            EffectNode node = new DirectHpDeltaEffect(Fixed.FromInt(-1));
            for (int i = 0; i < compositions; i++)
                node = new SequenceEffect(node);
            return node;
        }

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
