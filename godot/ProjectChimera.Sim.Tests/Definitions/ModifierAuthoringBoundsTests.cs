#nullable enable
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // AbilityDefinition / AbilityValidator / AbilityValidationResult
using ProjectChimera.Effects;            // Modifier / EffectCaps / ModifierStore / ModifierSystem
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-488 — the content-authoring cap on <c>|delta| × MaxStacks</c> that keeps <c>ModifierSystem</c>'s stat
    /// accumulator un-wrappable.
    ///
    /// <para><c>AccumulateBonus</c> sums a host's live modifier contributions with the WRAPPING int <c>+=</c>. DW-28's
    /// <c>Fixed.AddSaturating</c> saturates only the later <c>Base + Σbonus</c> READ and (per its own docstring) cannot
    /// recover a value that has already wrapped, and per-step saturation inside the accumulator would break
    /// <c>AccumulateBonus</c>'s order-independence invariant — so the closure is DW-28's other option, an authoring
    /// bound. <see cref="Modifier.MaxStatDeltaTotalRaw"/> is that bound, and <see cref="AbilityValidator"/> enforces it
    /// fail-closed.</para>
    ///
    /// <para>The bound is NOT an arbitrary number, and
    /// <see cref="TheBoundIsExactlyTheAccumulatorWrapThreshold_AtBoundSaturates_OverBoundWraps"/> is the test that says
    /// so: <see cref="EffectCaps.MaxModifiersPerEntity"/> modifiers AT the bound drive the real store without wrapping,
    /// and the same count ONE RAW UNIT over it wraps the accumulator negative and collapses the ceiling to zero — the
    /// exact state DW-488 describes, and exactly what the validator now rejects.</para>
    /// </summary>
    public class ModifierAuthoringBoundsTests
    {
        private static readonly AbilityValidator V = new();

        private static AbilityDefinition Def(EffectNode graph, string id = "bound_test") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        /// <summary>A stat-only modifier with one delta placed on the requested field.</summary>
        private static Modifier Mod(StackRule rule, int maxStacks, Fixed delta, string field = "max_health_delta",
                                    int id = 41) =>
            new Modifier(id, 90, rule, maxStacks,
                         maxHealthDelta:    field == "max_health_delta"    ? delta : Fixed.Zero,
                         attackDamageDelta: field == "attack_damage_delta" ? delta : Fixed.Zero,
                         moveSpeedDelta:    field == "move_speed_delta"    ? delta : Fixed.Zero,
                         status: StatusFlags.None, periodEffect: null, periodTicks: 0,
                         armorDelta:        field == "armor_delta"         ? delta : Fixed.Zero);

        // ── The validator rule ──

        [Theory]
        [InlineData("max_health_delta")]
        [InlineData("attack_damage_delta")]
        [InlineData("move_speed_delta")]
        [InlineData("armor_delta")]
        public void StackedDeltaOverTheBound_IsRejected_OnEveryStatField(string field)
        {
            // MaxStacks 8 × a raw delta of (bound/8 + 1) is one raw unit past the per-modifier ceiling. All four
            // accumulators use the same wrapping `+=`, so all four must be gated — a per-field loop, not one arm.
            Fixed delta = Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw / 8 + 1);
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, maxStacks: 8, delta, field))));

            Assert.False(r.Ok);
            Assert.Contains($"effect.modifier.{field}", r.Error!);          // located at the offending field
            Assert.Contains("MaxStatDeltaTotalRaw", r.Error!);              // …and names the bound it broke
        }

        [Fact]
        public void StackedDeltaExactlyAtTheBound_IsAccepted()
        {
            // Boundary teeth in the other direction: an off-by-one that rejected the bound itself would be a silent
            // content-breaking change. 5 stacks × (bound/5) lands exactly on the ceiling and must PASS.
            Fixed delta = Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw / 5);
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, maxStacks: 5, delta))));

            Assert.True(r.Ok, r.Error);
            Assert.NotNull(r.Value.Value);
        }

        [Fact]
        public void NonStackingRule_IsNotMultipliedByMaxStacks()
        {
            // Only StackRule.Stack re-adds the deltas per stack; Refresh/Ignore hold exactly ONE instance whatever
            // max_stacks says. A bound that multiplied regardless would reject this legal (if odd) content.
            Fixed delta = Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw);
            Assert.True(V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Refresh, maxStacks: 1000, delta)))).Ok);
            Assert.True(V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Ignore, maxStacks: 1000, delta)))).Ok);

            // …but the SAME magnitude under Stack (which really does multiply) is rejected.
            Assert.False(V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, maxStacks: 1000, delta)))).Ok);
        }

        [Fact]
        public void StackingRuleWithNonPositiveMaxStacks_IsRejected()
        {
            // A `stacking: Stack` modifier that can hold no stack is inert content; it is also the shape that makes the
            // product bound meaningless, so it is rejected rather than warned.
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, maxStacks: 0, Fixed.FromInt(3)))));

            Assert.False(r.Ok);
            Assert.Contains("effect.modifier.max_stacks", r.Error!);
        }

        [Fact]
        public void MaxStacksBeyondTheFixedRepresentableLimit_IsRejected()
        {
            // ModifierStore.RemoveSlot reverts a slot as `delta × Fixed.FromInt(stackCount)`, and Fixed.FromInt shifts
            // left by FRACTIONAL_BITS — past MaxAuthorableStacks the stack count is not representable in 16.16 and the
            // revert would subtract the wrong amount, permanently poisoning the accumulator.
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, Modifier.MaxAuthorableStacks + 1, Fixed.FromRaw(1)))));

            Assert.False(r.Ok);
            Assert.Contains("MaxAuthorableStacks", r.Error!);
            // Teeth: the limit itself is fine (this is a bound, not an off-by-one reject).
            Assert.True(V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, Modifier.MaxAuthorableStacks, Fixed.FromRaw(1))))).Ok);
        }

        [Fact]
        public void ShippedSizedDeltas_AreUntouchedByTheBound()
        {
            // Non-vacuity in the direction that matters most: the bound must not be a breaking change. The largest
            // MaxHealth delta in shipped content is ring_of_vigor's +50, three orders of magnitude below the ceiling.
            Assert.Null(Mod(StackRule.Stack, maxStacks: 5, Fixed.FromInt(50)).CheckAuthoringBounds());
            Assert.True(V.Validate(Def(new ApplyModifierEffect(
                Mod(StackRule.Stack, maxStacks: 5, Fixed.FromInt(50))))).Ok);
        }

        // ── Why the bound has THAT value: the accumulator wrap threshold, driven through the real store ──

        [Fact]
        public void TheBoundIsExactlyTheAccumulatorWrapThreshold_AtBoundSaturates_OverBoundWraps()
        {
            // AT the bound: MaxModifiersPerEntity instances each contributing MaxStatDeltaTotalRaw sum to
            // MaxModifiersPerEntity × bound ≤ int.MaxValue, so the accumulator never wraps and the ceiling saturates
            // high. (Positive deltas, so the DW-491 collapse gate is out of the picture either way.)
            var atBound = Wire();
            int ok = atBound.world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            for (int i = 0; i < EffectCaps.MaxModifiersPerEntity; i++)
                atBound.store.Apply(ok, Mod(StackRule.Refresh, 1, Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw), id: 100 + i),
                                    ok, Faction.Player1);

            Assert.Equal(EffectCaps.MaxModifiersPerEntity, atBound.store.CountAt(ok)); // all eight really installed
            Assert.True(atBound.world.IsAlive(ok));
            Assert.Equal(int.MaxValue, atBound.world.EffectiveMaxHealth[ok].Raw);      // saturated, NOT wrapped to 0

            // ONE RAW UNIT over the bound: the same count wraps the int accumulator negative, AddSaturating saturates
            // Base + wrappedNegative to MinValue, and the Zero-floor collapses the ceiling to 0 — DW-488's defect.
            var overBound = Wire();
            int bad = overBound.world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            for (int i = 0; i < EffectCaps.MaxModifiersPerEntity; i++)
                overBound.store.Apply(bad, Mod(StackRule.Refresh, 1, Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw + 1), id: 200 + i),
                                      bad, Faction.Player1);

            Assert.Equal(Fixed.Zero.Raw, overBound.world.EffectiveMaxHealth[bad].Raw); // the accumulator wrapped

            // …and that is precisely the modifier the authoring bound rejects, so the wrap is unreachable from content.
            (string Field, string Reason)? rejected =
                Mod(StackRule.Refresh, 1, Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw + 1)).CheckAuthoringBounds();
            Assert.NotNull(rejected);
            Assert.Equal("max_health_delta", rejected!.Value.Field);
        }

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }
    }
}
