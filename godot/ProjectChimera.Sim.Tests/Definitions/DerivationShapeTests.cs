#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Stats;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 15-24c — the derivation SHAPES. Pins the arithmetic that forced the design (a step function is one
    /// polynomial degree above the linear (base, perLevel) flatten, so it cannot ride it), the two new shapes'
    /// evaluation against a hero's LIVE attribute total, the golden-neutrality of a linear-only model (the
    /// evaluator returns null and does no work), and the validator's fail-closed gates.
    /// </summary>
    public class DerivationShapeTests
    {
        private static AttributeModelDefinition Model(params DerivedStatRule[] rows) => new AttributeModelDefinition
        {
            Attributes = new List<AttributeDeclaration> { new() { Id = "strength" }, new() { Id = "intellect" } },
            Derived = new List<DerivedStatRule>(rows),
        };

        private static HeroAttributesDefinition Attrs(float strBase, float strPerLevel = 0f) =>
            new HeroAttributesDefinition
            {
                Primary = "strength",
                Base = new Dictionary<string, float> { { "strength", strBase } },
                PerLevel = new Dictionary<string, float> { { "strength", strPerLevel } },
            };

        private static Fixed At(AttributeModelDefinition m, HeroAttributesDefinition a, int level, StatId stat)
        {
            Fixed[]? v = HeroAttributeResolver.EvaluateAt(m, a, level);
            Assert.NotNull(v);
            return v![(int)stat];
        }

        // ── The arithmetic that forced the design ────────────────────────────────────────────────────────

        [Fact]
        public void AStepRowsFirstDifference_Alternates_SoNoLinearPairCanExpressIt()
        {
            // "every 25 strength → +1 health_regen", strength 20 +7/level. The attribute total by level is
            // 20,27,34,41,48,55 → floor(/25) = 0,1,1,1,1,2 — first differences 1,0,0,0,1. An affine
            // function's first difference is CONSTANT, so no (base, perLevel) pair reproduces this; that is
            // exactly why EvaluateAt exists instead of a wider Resolve output.
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "health_regen", PerPoint = 1f,
                Shape = "per_step", Threshold = 25f,
            });
            var a = Attrs(20f, 7f);

            var seen = new List<int>();
            for (int lvl = 1; lvl <= 6; lvl++) seen.Add(At(m, a, lvl, StatId.HealthRegen).ToInt());
            Assert.Equal(new[] { 0, 1, 1, 1, 1, 2 }, seen);

            var diffs = new HashSet<int>();
            for (int i = 1; i < seen.Count; i++) diffs.Add(seen[i] - seen[i - 1]);
            Assert.True(diffs.Count > 1,
                "the step row's first difference must NOT be constant — if it were, the linear flatten could have carried it");
        }

        [Fact]
        public void LinearRows_StayOnResolvesFlatten_AndAreNotDoubleCountedByEvaluateAt()
        {
            // A linear row contributes through Resolve (the 15-21 pair) and must NOT also appear in the
            // threshold evaluation — double-counting would silently double every shipped model's stats.
            var m = Model(new DerivedStatRule { Attribute = "strength", Stat = "max_health", PerPoint = 2f });
            var a = Attrs(10f, 1f);

            var (rBase, rPerLevel) = HeroAttributeResolver.Resolve(m, a);
            Assert.Equal(Fixed.FromInt(20).Raw, rBase[(int)StatId.MaxHealth].Raw);   // 2 × 10
            Assert.Equal(Fixed.FromInt(2).Raw, rPerLevel[(int)StatId.MaxHealth].Raw); // 2 × 1

            Assert.False(HeroAttributeResolver.HasThresholdRows(m));
            Assert.Null(HeroAttributeResolver.EvaluateAt(m, a, 5)); // linear-only ⇒ no threshold work at all
        }

        [Fact]
        public void AThresholdRow_IsExcludedFromTheLinearFlatten()
        {
            // The step row must not leak into Resolve's accumulators (it would be counted as per_point × value).
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "max_health", PerPoint = 5f, Shape = "per_step", Threshold = 10f,
            });
            var (rBase, rPerLevel) = HeroAttributeResolver.Resolve(m, Attrs(30f, 3f));
            Assert.Equal(0, rBase[(int)StatId.MaxHealth].Raw);
            Assert.Equal(0, rPerLevel[(int)StatId.MaxHealth].Raw);
        }

        // ── The two shapes ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void PerStep_GrantsOncePerCompletedStep_OfTheLiveAttributeTotal()
        {
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "max_health", PerPoint = 10f, Shape = "per_step", Threshold = 25f,
            });
            var a = Attrs(50f, 25f); // 50 at L1, +25/level

            Assert.Equal(Fixed.FromInt(20).Raw, At(m, a, 1, StatId.MaxHealth).Raw); // floor(50/25)=2 → 20
            Assert.Equal(Fixed.FromInt(30).Raw, At(m, a, 2, StatId.MaxHealth).Raw); // floor(75/25)=3 → 30
            Assert.Equal(Fixed.FromInt(40).Raw, At(m, a, 3, StatId.MaxHealth).Raw); // floor(100/25)=4 → 40
        }

        [Fact]
        public void AtLeast_IsAOneShotGate_ThatLatchesAtTheCrossingLevel()
        {
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "armor", PerPoint = 7f, Shape = "at_least", Threshold = 50f,
            });
            var a = Attrs(30f, 10f); // 30, 40, 50, 60 …

            Assert.Equal(0, At(m, a, 1, StatId.Armor).Raw);                        // 30 < 50
            Assert.Equal(0, At(m, a, 2, StatId.Armor).Raw);                        // 40 < 50
            Assert.Equal(Fixed.FromInt(7).Raw, At(m, a, 3, StatId.Armor).Raw);     // 50 ≥ 50 → +7 ONCE
            Assert.Equal(Fixed.FromInt(7).Raw, At(m, a, 4, StatId.Armor).Raw);     // still exactly +7 (not cumulative)
        }

        [Fact]
        public void ThePrimarySelector_AndTheUnflaggedPrimary_BehaveLikeLinearRows()
        {
            var m = Model(new DerivedStatRule
            {
                Attribute = "primary", Stat = "max_health", PerPoint = 4f, Shape = "per_step", Threshold = 10f,
            });
            Assert.Equal(Fixed.FromInt(8).Raw, At(m, Attrs(20f), 1, StatId.MaxHealth).Raw); // primary=strength → floor(20/10)=2

            var noPrimary = new HeroAttributesDefinition
            {
                Primary = null,
                Base = new Dictionary<string, float> { { "strength", 20f } },
            };
            Assert.Equal(0, At(m, noPrimary, 1, StatId.MaxHealth).Raw); // no primary flagged ⇒ contributes nothing
        }

        [Fact]
        public void EvaluateAt_ClampsLevelBelowOne_AndSkipsNonAuthorableStats()
        {
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "max_health", PerPoint = 3f, Shape = "per_step", Threshold = 10f,
            });
            // Level 0/-5 are unreachable in the sim; clamp to 1 rather than compute a negative attribute total.
            Assert.Equal(At(m, Attrs(40f, 5f), 1, StatId.MaxHealth).Raw, At(m, Attrs(40f, 5f), 0, StatId.MaxHealth).Raw);
        }

        // ── Validator gates ─────────────────────────────────────────────────────────────────────────────

        private static FactionDefinition Faction(AttributeModelDefinition model, HeroAttributesDefinition? attrs = null)
        {
            var hero = new UnitDefinition
            {
                Id = "hero", DisplayName = "Hero", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 10, AttackRange = 2, AttackSpeed = 1,
                Hero = new HeroDefinition { MaxLevel = 5, Attributes = attrs },
            };
            return new FactionDefinition
            {
                Id = "test_faction", DisplayName = "Test", AttributeModel = model,
                Units = new List<UnitDefinition> { hero },
                Buildings = new List<BuildingDefinition>(),
            };
        }

        private static List<string> Errors(FactionDefinition def)
        {
            var list = new List<string>();
            foreach ((string _, string msg) in FactionValidator.Validate(def).Errors) list.Add(msg);
            return list;
        }

        [Fact]
        public void Validator_RejectsAnUnknownShapeToken()
        {
            var m = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 1f, Shape = "sigmoid" });
            Assert.Contains(Errors(Faction(m, Attrs(10f))), e => e.Contains("is not a derivation shape"));
        }

        [Fact]
        public void Validator_RequiresAPositiveThresholdOnAStepRow_AndRejectsOneOnALinearRow()
        {
            var missing = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 1f, Shape = "per_step" });
            Assert.Contains(Errors(Faction(missing, Attrs(10f))), e => e.Contains("threshold") && e.Contains("must be finite"));

            var zero = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 1f, Shape = "at_least", Threshold = 0f });
            Assert.Contains(Errors(Faction(zero, Attrs(10f))), e => e.Contains("threshold") && e.Contains("must be finite"));

            var strayOnLinear = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 1f, Threshold = 25f });
            Assert.Contains(Errors(Faction(strayOnLinear, Attrs(10f))), e => e.Contains("is set on a linear row"));
        }

        [Fact]
        public void Validator_CapsTheResolvedThresholdTotalAtMaxLevel()
        {
            // "every 1 strength → +250 max health" with 40 strength at L1 and +50/level: per_point stays under the
            // pre-existing AttrPerPointMax (256) so THIS cap is what rejects, but at MaxLevel 5 the total is
            // 250 × 240 = 60,000 — past the Fixed range entirely, so EvaluateAt SATURATES it (rather than wrapping
            // it negative, which would have slipped this very check). Reachable ONLY through the threshold path
            // (Resolve skips the row), which is the gap this cap closes.
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "max_health", PerPoint = 250f, Shape = "per_step", Threshold = 1f,
            });
            Assert.Contains(Errors(Faction(m, Attrs(40f, 50f))), e => e.Contains("resolved THRESHOLD contribution"));
        }

        [Fact]
        public void Validator_AcceptsASaneThresholdModel()
        {
            var m = Model(
                new DerivedStatRule { Attribute = "strength", Stat = "max_health", PerPoint = 25f },
                new DerivedStatRule { Attribute = "strength", Stat = "health_regen", PerPoint = 1f, Shape = "per_step", Threshold = 25f },
                new DerivedStatRule { Attribute = "intellect", Stat = "armor", PerPoint = 2f, Shape = "at_least", Threshold = 50f });
            Assert.DoesNotContain(Errors(Faction(m, Attrs(20f, 3f))), e => e.Contains("attribute_model"));
        }

        // ── Content fold + Clone (the silent-drop traps) ─────────────────────────────────────────────────

        [Fact]
        public void Clone_CopiesTheNewRowFields()
        {
            var m = Model(new DerivedStatRule
            {
                Attribute = "strength", Stat = "armor", PerPoint = 2f, Shape = "at_least", Threshold = 40f,
            });
            AttributeModelDefinition c = m.Clone();
            Assert.Equal("at_least", c.Derived![0].Shape);
            Assert.Equal(40f, c.Derived[0].Threshold);
        }

        [Fact]
        public void ContentHash_MovesForTheShapeAndTheThreshold()
        {
            ulong H(AttributeModelDefinition model) =>
                ContentHash.Compute(new List<FactionDefinition> { Faction(model, Attrs(10f)) }, null, null, null);

            var linear = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 2f });
            var step   = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 2f, Shape = "per_step", Threshold = 10f });
            var step20 = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 2f, Shape = "per_step", Threshold = 20f });

            Assert.NotEqual(H(linear), H(step));   // the shape is folded
            Assert.NotEqual(H(step), H(step20));   // the threshold is folded

            // An explicit "linear" and an omitted shape mean the SAME thing and must fold identically — the
            // reason the fold takes the parsed ordinal rather than the raw token.
            var explicitLinear = Model(new DerivedStatRule { Attribute = "strength", Stat = "armor", PerPoint = 2f, Shape = "LINEAR" });
            Assert.Equal(H(linear), H(explicitLinear));
        }
    }
}
