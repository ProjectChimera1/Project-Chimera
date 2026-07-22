#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction
using ProjectChimera.Core.Definitions;  // UnitDefinition
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.5 — proves <see cref="BalanceSuggestionApplier.TryApply"/> gates a proposed value through the SAME
    /// <see cref="UnitDefinitionValidator"/> hand-authored unit edits use, on a CLONE (the original is never touched on a
    /// reject), and that an applied in-range value quantizes IDENTICALLY to a hand-authored one at the existing
    /// <c>EntityWorld.ApplyUnitDefinition</c> boundary — no second float→Fixed path.
    /// </summary>
    public class BalanceAnalysisApplyTests
    {
        private static UnitDefinition Grunt() => new()
        {
            Id = "grunt", DisplayName = "Grunt", Category = "Melee",
            Hp = 120f, Speed = 4f, AttackDamage = 10f, AttackRange = 1.5f, AttackSpeed = 1f,
            CostOre = 50, Supply = 2,
        };

        [Fact]
        public void TryApply_InRange_ReturnsCandidateWithFieldSet()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 14, null);

            Assert.Null(err);
            Assert.NotNull(candidate);
            Assert.Equal(14f, candidate!.AttackDamage);
            Assert.Equal("grunt", candidate.Id);
            // The original is untouched (apply is on a clone).
            Assert.Equal(10f, target.AttackDamage);
            Assert.NotSame(target, candidate);
        }

        [Fact]
        public void TryApply_IntField_RoundsAndApplies()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "cost_ore", 65, null);

            Assert.Null(err);
            Assert.NotNull(candidate);
            Assert.Equal(65, candidate!.CostOre);
            Assert.Equal(50, target.CostOre);   // original unchanged
        }

        [Fact]
        public void TryApply_OutOfFixedRange_LocatedReject_OriginalUnchanged()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 40000, null);

            Assert.Null(candidate);
            Assert.NotNull(err);
            Assert.Contains("attack_damage", err);   // the located path
            Assert.Contains("40000", err);           // the offending value
            Assert.Equal(10f, target.AttackDamage);  // target untouched
        }

        [Fact]
        public void TryApply_UnknownField_LocatedReject()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "wingspan", 3, null);

            Assert.Null(candidate);
            Assert.NotNull(err);
            Assert.Contains("wingspan", err);
        }

        [Fact]
        public void TryApply_HeroFieldOnNonHero_LocatedReject()
        {
            var target = Grunt();   // no hero block
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "hero.damage_per_level", 4, null);

            Assert.Null(candidate);
            Assert.NotNull(err);
            Assert.Contains("hero", err);
        }

        [Fact]
        public void TryApply_HeroFieldOnHero_Applies()
        {
            var target = new UnitDefinition
            {
                Id = "champion", Category = "Melee", Hp = 300f, AttackDamage = 25f,
                IsHero = true,
                Hero = new HeroDefinition { MaxLevel = 5, BaseXp = 100f, XpGrowth = 1.4f },
            };
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "hero.health_per_level", 30, null);

            Assert.Null(err);
            Assert.NotNull(candidate);
            Assert.NotNull(candidate!.Hero);
            Assert.Equal(30f, candidate.Hero!.HealthPerLevel);
            Assert.Equal(0f, target.Hero!.HealthPerLevel);   // original untouched
        }

        [Fact]
        public void TryApply_EditedValue_GatesTheEditedValue()
        {
            var target = Grunt();

            // Creator edits the proposed value in-range → applies.
            var (inRange, e1) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 22, null);
            Assert.Null(e1);
            Assert.NotNull(inRange);
            Assert.Equal(22f, inRange!.AttackDamage);

            // Creator edits it out-of-range → located reject (the edited value is what is gated).
            var (outOfRange, e2) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 50000, null);
            Assert.Null(outOfRange);
            Assert.NotNull(e2);
            Assert.Contains("attack_damage", e2);
        }

        [Fact]
        public void TryApply_WithRosterSiblingsIncludingTarget_DoesNotFalselyFlagDuplicateId()
        {
            var target = Grunt();
            var roster = new List<UnitDefinition> { target, new() { Id = "worker", Category = "Worker" } };

            // The clone shares the target's id, and the roster CONTAINS the target — keeping an existing id must not be
            // flagged as a new duplicate (the raw-JSON-pane save precedent).
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 14, roster);

            Assert.Null(err);
            Assert.NotNull(candidate);
        }

        private static UnitDefinition HeroChampion() => new()
        {
            Id = "champion", Category = "Melee", Hp = 300f, AttackDamage = 25f,
            IsHero = true,
            Hero = new HeroDefinition { MaxLevel = 5, BaseXp = 100f, XpGrowth = 1.4f },
        };

        // Each row: the tunable field name + a DIRECT property reader (independent of SetField's switch, so a
        // wrong-property mapping — e.g. vision_range→AttackRange — makes the named-property read miss 7 and FAILS).
        public static IEnumerable<object[]> UnitFieldCases() => new[]
        {
            Row("attack_damage",    (Func<UnitDefinition, double>)(u => u.AttackDamage)),
            Row("hp",               u => u.Hp),
            Row("armor",            u => u.Armor),
            Row("attack_range",     u => u.AttackRange),
            Row("attack_speed",     u => u.AttackSpeed),
            Row("splash_radius",    u => u.SplashRadius),
            Row("vision_range",     u => u.VisionRange),
            Row("train_time",       u => u.TrainTime),
            Row("max_energy",       u => u.MaxEnergy),
            Row("collision_radius", u => u.CollisionRadius),
            Row("mesh_scale",       u => u.MeshScale),
            Row("projectile_speed", u => u.ProjectileSpeed),
            Row("cost_ore",         u => u.CostOre),
            Row("cost_crystal",     u => u.CostCrystal),
            Row("supply",           u => u.Supply),
        };

        public static IEnumerable<object[]> HeroFieldCases() => new[]
        {
            Row("hero.max_level",        (Func<UnitDefinition, double>)(u => u.Hero!.MaxLevel)),
            Row("hero.base_xp",          u => u.Hero!.BaseXp),
            Row("hero.xp_growth",        u => u.Hero!.XpGrowth),
            Row("hero.xp_per_kill",      u => u.Hero!.XpPerKill),
            Row("hero.xp_share_radius",  u => u.Hero!.XpShareRadius),
            Row("hero.health_per_level", u => u.Hero!.HealthPerLevel),
            Row("hero.damage_per_level", u => u.Hero!.DamagePerLevel),
            Row("hero.armor_per_level",  u => u.Hero!.ArmorPerLevel),
        };

        private static object[] Row(string field, Func<UnitDefinition, double> reader) => new object[] { field, reader };

        [Theory]
        [MemberData(nameof(UnitFieldCases))]
        public void TryApply_UnitField_AppliesToItsOwnProperty(string field, Func<UnitDefinition, double> read)
        {
            // 7 is in the Fixed-safe range for every listed field; each field's default differs from 7, so reading the
            // NAMED property and asserting ==7 catches any wrong-property mapping in SetField's switch.
            var (candidate, err) = BalanceSuggestionApplier.TryApply(Grunt(), field, 7, null);
            Assert.Null(err);
            Assert.NotNull(candidate);
            Assert.Equal(7d, read(candidate!), 3);
        }

        [Theory]
        [MemberData(nameof(HeroFieldCases))]
        public void TryApply_HeroField_AppliesToItsOwnProperty(string field, Func<UnitDefinition, double> read)
        {
            var (candidate, err) = BalanceSuggestionApplier.TryApply(HeroChampion(), field, 7, null);
            Assert.Null(err);
            Assert.NotNull(candidate);
            Assert.NotNull(candidate!.Hero);
            Assert.Equal(7d, read(candidate), 3);
        }

        [Fact]
        public void TryApply_EveryTunableField_IsHandledBySetField_NotDefaultRejected()
        {
            // The coverage guard the class comment promises: every member of the single-source TunableFields set must be
            // APPLIABLE (handled by SetField), not fall through to the "is not a tunable balance field" default. A hero
            // target keeps hero.* fields out of the non-hero reject; 5 is valid for every field, so each yields a
            // non-null candidate. Adding a field to TunableFields (+ the prompt) but forgetting SetField fails HERE.
            foreach (string field in BalanceSuggestionApplier.TunableFields)
            {
                var (candidate, err) = BalanceSuggestionApplier.TryApply(HeroChampion(), field, 5, null);
                Assert.False(err != null && err.Contains("is not a tunable balance field"),
                    $"TunableFields member '{field}' is not handled by SetField (default-rejected).");
                Assert.NotNull(candidate);
            }
        }

        [Fact]
        public void TryApply_IntField_NaN_LocatedReject_OriginalUnchanged()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "cost_ore", double.NaN, null);

            Assert.Null(candidate);               // NaN→-1 coercion makes the validator's negative-cost gate fire
            Assert.NotNull(err);
            Assert.Contains("cost_ore", err);     // located, not a silent NaN→0 apply
            Assert.Equal(50, target.CostOre);     // original untouched
        }

        [Fact]
        public void TryApply_IntField_Infinity_LocatedReject()
        {
            var (candidate, err) = BalanceSuggestionApplier.TryApply(Grunt(), "supply", double.PositiveInfinity, null);

            Assert.Null(candidate);               // +Inf→int.MaxValue → out of [0,32768) → located reject
            Assert.NotNull(err);
            Assert.Contains("supply", err);
        }

        [Fact]
        public void AppliedInRangeValue_QuantizesToSameSoAFixedAsHandAuthored()
        {
            var target = Grunt();
            var (candidate, err) = BalanceSuggestionApplier.TryApply(target, "attack_damage", 17.5, null);
            Assert.Null(err);
            Assert.NotNull(candidate);

            // An equivalent HAND-AUTHORED def with the identical stat.
            var hand = new UnitDefinition { Id = "grunt", Category = "Melee", AttackDamage = 17.5f };

            Assert.Equal(ApplyAndReadAttackDamage(hand).Raw, ApplyAndReadAttackDamage(candidate!).Raw);
            // And the absolute quantization matches Fixed.FromFloat at the single ApplyUnitDefinition boundary.
            Assert.Equal(Fixed.FromFloat(17.5f).Raw, ApplyAndReadAttackDamage(candidate!).Raw);
        }

        /// <summary>Apply <paramref name="def"/> through the single def→SoA mapper and read the quantized BaseAttackDamage.</summary>
        private static Fixed ApplyAndReadAttackDamage(UnitDefinition def)
        {
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);
            return w.BaseAttackDamage[id];
        }
    }
}
