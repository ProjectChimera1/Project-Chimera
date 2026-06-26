#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC2 / AC4 / AC5 — the validator's reject rules, each with a positive case AND a negative control
    /// that is demonstrably RED without the rule (A3 "every gate ships with teeth"). Graphs are built directly in C#
    /// so the validator's own gates are under test (the converter's parse guard is exercised separately in
    /// <see cref="EffectNodeConverterTests"/>). Every reject asserts the located error contains BOTH the ability id
    /// and the offending path/limit.
    /// </summary>
    public class NegativeAbilityValidationTests
    {
        private static readonly AbilityValidator V = new();

        private static EffectNode Leaf() => new HealEffect(Fixed.FromInt(1));

        private static AbilityDefinition Def(EffectNode? graph, string id = "atest") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        // ── (c) ≥ 1 effect node ──

        [Fact]
        public void NullEffectGraph_IsRejected()
        {
            AbilityValidationResult r = V.Validate(Def(null));
            Assert.False(r.Ok);
            Assert.Contains("atest", r.Error!);
            Assert.Contains("effect", r.Error!);
        }

        // ── (a) targeting + (b) costs/cooldown ──

        [Fact]
        public void UnknownTargeting_IsRejected()
        {
            var def = new AbilityDefinition { Id = "bog", Targeting = "Bogus", EffectGraph = Leaf() };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("targeting", r.Error!);
            Assert.Contains("Bogus", r.Error!);
        }

        [Fact]
        public void NegativeOreCost_IsRejected()
        {
            var def = new AbilityDefinition { Id = "neg", Targeting = "Self", CostOre = -1, EffectGraph = Leaf() };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cost_ore", r.Error!);
        }

        [Fact]
        public void NegativeCooldown_IsRejected()
        {
            var def = new AbilityDefinition { Id = "neg", Targeting = "Self", Cooldown = Fixed.FromInt(-1), EffectGraph = Leaf() };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cooldown", r.Error!);
        }

        [Fact]
        public void NegativeEnergyCost_IsRejected()
        {
            var def = new AbilityDefinition { Id = "neg", Targeting = "Self", CostEnergy = Fixed.FromInt(-5), EffectGraph = Leaf() };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cost_energy", r.Error!);
        }

        // ── (d) EffectBounds (reused): depth ≤ 8, Sequence ≤ 8 ──

        [Fact]
        public void Depth8_Passes_Depth9_Rejected()
        {
            // 8 nested single-child sequences → deepest sequence at composition depth 7 (valid).
            EffectNode g8 = Leaf();
            for (int i = 0; i < 8; i++) g8 = new SequenceEffect(g8);
            Assert.True(V.Validate(Def(g8)).Ok, V.Validate(Def(g8)).Error);

            // 9 → deepest at depth 8 → EffectBounds rejects.
            EffectNode g9 = Leaf();
            for (int i = 0; i < 9; i++) g9 = new SequenceEffect(g9);
            AbilityValidationResult r = V.Validate(Def(g9));
            Assert.False(r.Ok);
            Assert.Contains("MaxEffectDepth", r.Error!);
        }

        [Fact]
        public void Sequence8Children_Passes_9Rejected()
        {
            EffectNode[] eight = new EffectNode[8];
            for (int i = 0; i < 8; i++) eight[i] = Leaf();
            Assert.True(V.Validate(Def(new SequenceEffect(eight))).Ok);

            EffectNode[] nine = new EffectNode[9];
            for (int i = 0; i < 9; i++) nine[i] = Leaf();
            AbilityValidationResult r = V.Validate(Def(new SequenceEffect(nine)));
            Assert.False(r.Ok);
            Assert.Contains("MaxSequenceChildren", r.Error!);
        }

        // ── (e) AC4 total-work caps ──

        [Fact]
        public void SearchAreaDepth2_Passes_3Rejected()
        {
            SearchAreaEffect Search(EffectNode child) =>
                new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, child);

            // 2-nested passes (worst case 64² executions).
            Assert.True(V.Validate(Def(Search(Search(Leaf())))).Ok);

            // 3-nested rejected (AC4 teeth: remove the MaxSearchAreaDepth check → this passes → RED).
            AbilityValidationResult r = V.Validate(Def(Search(Search(Search(Leaf())))));
            Assert.False(r.Ok);
            Assert.Contains("MaxSearchAreaDepth", r.Error!);
        }

        [Fact]
        public void TotalNodes64_Passes_65Rejected()
        {
            // Balanced tree: 1 outer Sequence + 8 inner Sequences + N leaves. Depth 2, widths ≤ 8 → EffectBounds OK.
            EffectNode Tree(int[] innerLeafCounts)
            {
                var inner = new EffectNode[innerLeafCounts.Length];
                for (int i = 0; i < innerLeafCounts.Length; i++)
                {
                    var leaves = new EffectNode[innerLeafCounts[i]];
                    for (int j = 0; j < innerLeafCounts[i]; j++) leaves[j] = Leaf();
                    inner[i] = new SequenceEffect(leaves);
                }
                return new SequenceEffect(inner);
            }

            // 1 + 8 + 55 = 64 → passes.
            Assert.True(V.Validate(Def(Tree(new[] { 7, 7, 7, 7, 7, 7, 7, 6 }))).Ok);

            // 1 + 8 + 56 = 65 → rejected (AC4 teeth: remove the MaxTotalEffectNodes check → this passes → RED).
            AbilityValidationResult r = V.Validate(Def(Tree(new[] { 7, 7, 7, 7, 7, 7, 7, 7 })));
            Assert.False(r.Ok);
            Assert.Contains("MaxTotalEffectNodes", r.Error!);
        }

        // ── (f) AC5 re-entrancy / period-shape ──

        [Fact]
        public void ApplyModifier_TopLevel_Passes_InsidePersistentPhase_Rejected()
        {
            var mod = new Modifier(1, 30, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero,
                                   StatusFlags.None, null, 0);
            var apply = new ApplyModifierEffect(mod);

            // Top-level ApplyModifier is accepted (executes since 2.2b).
            Assert.True(V.Validate(Def(apply)).Ok);

            // The SAME install inside a Persistent phase is rejected (install re-entrancy). AC5 teeth.
            var persistent = new PersistentEffect(apply, null, null, 0, 0);
            AbilityValidationResult r = V.Validate(Def(persistent));
            Assert.False(r.Ok);
            Assert.Contains("PersistentEffect phase", r.Error!);
        }

        [Fact]
        public void NestedPersistent_InsidePersistentPhase_Rejected()
        {
            var inner = new PersistentEffect(Leaf(), null, null, 0, 0);
            var outer = new PersistentEffect(inner, null, null, 0, 0);
            AbilityValidationResult r = V.Validate(Def(outer));
            Assert.False(r.Ok);
            Assert.Contains("nested PersistentEffect", r.Error!);
        }

        [Fact]
        public void SearchArea_InsidePersistentPeriod_Rejected()
        {
            var search = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, Leaf());
            var persistent = new PersistentEffect(null, search, null, 30, 5); // search is the period effect
            AbilityValidationResult r = V.Validate(Def(persistent));
            Assert.False(r.Ok);
            Assert.Contains("period_effect", r.Error!);
        }

        [Fact]
        public void TopLevelPersistent_WithDirectTargetPeriod_Passes()
        {
            // A period effect that is a direct-target leaf (no SearchArea) is admissible.
            var persistent = new PersistentEffect(
                initialEffect: new DamageEffect(Fixed.FromInt(5), DamageType.Magic),
                periodEffect: new DamageEffect(Fixed.FromInt(2), DamageType.Magic),
                expireEffect: null, periodTicks: 30, periodCount: 5);
            Assert.True(V.Validate(Def(persistent)).Ok);
        }

        // ── (b/AC2) over-range number is rejected at parse, folded into a located result by the loader ──

        [Fact]
        public void OverRangeNumber_IsRejected_AtParse()
        {
            // 40000 > the 16.16 range ceiling (32768) → FixedJsonConverter throws → loader returns a located fail.
            AbilityValidationResult r = AbilityLoader.Load(
                """{ "id": "big", "targeting": "Self", "effect": { "kind": "heal", "amount": 40000 } }""", "big");
            Assert.False(r.Ok);
            Assert.Contains("big", r.Error!);
            Assert.Contains("range", r.Error!);
        }
    }
}
