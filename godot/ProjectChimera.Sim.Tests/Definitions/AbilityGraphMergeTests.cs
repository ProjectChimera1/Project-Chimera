#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-900 — the APPEND core behind the ability editor's "Add more ✦" action.
    ///
    /// <para>The behaviour worth pinning is not "it concatenates". It is that appending is INCAPABLE of altering what
    /// the author already wrote — the property that distinguishes this from the destructive Generate it sits beside.
    /// The merge deliberately runs on the immutable <see cref="EffectNode"/> side rather than through
    /// <c>AbilityDraft</c>, because the draft layer does not carry <c>RequireTag</c> and would silently strip tag
    /// gates off the author's existing nodes.</para>
    /// </summary>
    public class AbilityGraphMergeTests
    {
        private static AbilityDefinition Ability(EffectNode? graph, string activation = "active") =>
            new AbilityDefinition
            {
                Id = "test_ability", DisplayName = "Test", Targeting = "TargetUnit", Activation = activation,
                CostEnergy = Fixed.FromInt(10), CostOre = 1, CostCrystal = 2, CostHealth = 3, Cooldown = 5,
                EffectGraph = graph,
            };

        [Fact]
        public void AppendingToASequenceExtendsItInPlaceRatherThanNesting()
        {
            var current = Ability(new SequenceEffect(new HealEffect(Fixed.FromInt(5)), new DamageEffect(Fixed.FromInt(3), DamageType.Magic)));

            AbilityDefinition merged = AbilityGraphMerge.Append(current, new EffectNode[] { new HealEffect(Fixed.FromInt(7)) });

            var seq = Assert.IsType<SequenceEffect>(merged.EffectGraph);
            Assert.Equal(3, seq.Children.Length);                 // extended, NOT wrapped in another sequence
            Assert.IsType<HealEffect>(seq.Children[0]);
            Assert.IsType<DamageEffect>(seq.Children[1]);
            Assert.Equal(Fixed.FromInt(7), Assert.IsType<HealEffect>(seq.Children[2]).Amount);
        }

        [Fact]
        public void AppendingToANonSequenceRootWrapsBothInOneSequence()
        {
            var current = Ability(new HealEffect(Fixed.FromInt(5)));

            AbilityDefinition merged = AbilityGraphMerge.Append(current, new EffectNode[] { new DamageEffect(Fixed.FromInt(9), DamageType.Pierce) });

            var seq = Assert.IsType<SequenceEffect>(merged.EffectGraph);
            Assert.Equal(2, seq.Children.Length);
            Assert.Equal(Fixed.FromInt(5), Assert.IsType<HealEffect>(seq.Children[0]).Amount);   // the ORIGINAL root, first
            Assert.Equal(Fixed.FromInt(9), Assert.IsType<DamageEffect>(seq.Children[1]).Amount);
        }

        [Fact]
        public void TheAuthorsExistingNodesSurviveByReference_IncludingRequireTag()
        {
            // The teeth: RequireTag has no representation in the draft layer, so a merge routed through AbilityDraft
            // would drop it here and the author would see the AI "change" an effect it was never asked to touch.
            var tagged = new DamageEffect(Fixed.FromInt(4), DamageType.Hero, UnitTag.Magical);
            var current = Ability(new SequenceEffect(tagged));

            AbilityDefinition merged = AbilityGraphMerge.Append(current, new EffectNode[] { new HealEffect(Fixed.FromInt(1)) });

            var seq = Assert.IsType<SequenceEffect>(merged.EffectGraph);
            Assert.Same(tagged, seq.Children[0]);                          // the very same instance, not a rebuild
            Assert.Equal(UnitTag.Magical, Assert.IsType<DamageEffect>(seq.Children[0]).RequireTag);
        }

        [Fact]
        public void EveryNonGraphFieldIsCarriedAcross()
        {
            var current = Ability(new HealEffect(Fixed.FromInt(5)));
            current.TargetAffinity = "ally";
            current.AllowSelfLethal = true;

            AbilityDefinition merged = AbilityGraphMerge.Append(current, new EffectNode[] { new HealEffect(Fixed.One) });

            Assert.Equal("test_ability", merged.Id);
            Assert.Equal("Test", merged.DisplayName);
            Assert.Equal("TargetUnit", merged.Targeting);
            Assert.Equal("ally", merged.TargetAffinity);
            Assert.Equal(Fixed.FromInt(10), merged.CostEnergy);
            Assert.Equal(1, merged.CostOre);
            Assert.Equal(2, merged.CostCrystal);
            Assert.Equal(3, merged.CostHealth);      // the two the draft layer drops
            Assert.True(merged.AllowSelfLethal);     //
            Assert.Equal(5, merged.Cooldown);
        }

        [Fact]
        public void TheSourceAbilityIsNeverMutated()
        {
            var original = new SequenceEffect(new HealEffect(Fixed.FromInt(5)));
            var current = Ability(original);

            AbilityGraphMerge.Append(current, new EffectNode[] { new HealEffect(Fixed.One) });

            Assert.Same(original, current.EffectGraph);                                 // still the old graph
            Assert.Single(Assert.IsType<SequenceEffect>(current.EffectGraph).Children); // still one child
        }

        // ── CanAppend: the pre-flight that must run BEFORE an LLM call is spent ──

        [Fact]
        public void CanAppend_RefusesAnEmptyGraph()
        {
            Assert.False(AbilityGraphMerge.CanAppend(Ability(null), out string reason));
            Assert.Contains("Generate", reason);   // points the author at the right button
        }

        [Theory]
        [InlineData("aura")]
        [InlineData("while_alive")]
        public void CanAppend_RefusesAPassiveWhoseRootShapeIsPinned(string activation)
        {
            // The validator's passive-shape rule requires an aura to be a SearchArea root and a while_alive to be a
            // Persistent root, so wrapping either in a Sequence would turn a valid ability invalid.
            var current = Ability(new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Ally, new HealEffect(Fixed.One)), activation);

            Assert.False(AbilityGraphMerge.CanAppend(current, out string reason));
            Assert.Contains("fixed root shape", reason);
        }

        [Fact]
        public void CanAppend_RefusesAFullSequence()
        {
            var children = new EffectNode[EffectCaps.MaxSequenceChildren];
            for (int i = 0; i < children.Length; i++) children[i] = new HealEffect(Fixed.One);

            Assert.False(AbilityGraphMerge.CanAppend(Ability(new SequenceEffect(children)), out string reason));
            Assert.Contains(EffectCaps.MaxSequenceChildren.ToString(), reason);
        }

        [Fact]
        public void CanAppend_AllowsAnOrdinaryActiveAbility()
        {
            Assert.True(AbilityGraphMerge.CanAppend(Ability(new HealEffect(Fixed.One)), out string reason));
            Assert.Equal("", reason);
        }

        [Fact]
        public void CountNodes_WalksEveryCompositionArm()
        {
            // sequence(1) + heal(1) + search_area(1) + its child damage(1) = 4
            var graph = new SequenceEffect(
                new HealEffect(Fixed.One),
                new SearchAreaEffect(Fixed.FromInt(3), TargetFilter.Enemy, new DamageEffect(Fixed.One, DamageType.Normal)));

            Assert.Equal(4, AbilityGraphMerge.CountNodes(graph));
        }

        [Fact]
        public void AMergedAbilityStillPassesTheRealValidator()
        {
            // The end-to-end property the panel depends on: a well-formed append produces content the authoritative
            // gate accepts, so the happy path does not surface a false rejection to the author.
            var current = Ability(new SequenceEffect(new HealEffect(Fixed.FromInt(5))));
            AbilityDefinition merged = AbilityGraphMerge.Append(current, new EffectNode[] { new DamageEffect(Fixed.FromInt(6), DamageType.Magic) });

            AbilityValidationResult r = new AbilityValidator().Validate(merged);
            Assert.True(r.Ok, r.Error);
        }
    }
}
