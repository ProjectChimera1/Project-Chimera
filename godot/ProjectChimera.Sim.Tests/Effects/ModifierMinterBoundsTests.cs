#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-650 — DW-488's authoring bound (<see cref="Modifier.CheckAuthoringBounds"/> /
    /// <see cref="Modifier.MaxStatDeltaTotalRaw"/>) adopted at the THREE non-ability <see cref="Modifier"/> minters.
    ///
    /// <para>DW-488 closed the <c>ModifierSystem.AccumulateBonus</c> wrap — up to
    /// <see cref="EffectCaps.MaxModifiersPerEntity"/> live modifier contributions summed into ONE wrapping int
    /// accumulator, which DW-28's saturating <c>Base + Σbonus</c> read cannot recover once wrapped — by bounding each
    /// modifier's worst-case contribution. But it enforced that bound in <c>AbilityValidator</c> only, and three
    /// minters build a <see cref="Modifier"/> directly and never go near an ability:
    /// <list type="bullet">
    ///   <item><c>ItemSystem.ApplyItemStatModifier</c> — gated by <see cref="ItemDefinitionValidator"/>.</item>
    ///   <item><c>HeroXpSystem.ReconcileGrowth</c> — gated by <see cref="UnitDefinitionValidator"/>'s hero block.</item>
    ///   <item><c>ResearchSystem.BuildCumulativeModifier</c> — NOT gatable at load time (its magnitude is a running sum
    ///         accumulated across a repeatable ladder), so the bound is applied where the modifier is built.</item>
    /// </list></para>
    /// </summary>
    public class ModifierMinterBoundsTests
    {
        // ─────────────────────────── Minter 1: items (ItemDefinitionValidator) ───────────────────────────

        [Fact]
        public void ItemCaps_ImplyTheAccumulatorBound_ForEveryDelta()
        {
            // The item path is bounded TODAY only because two independently-owned numbers happen to be ordered:
            // MAX_ITEM_STAT_DELTA (1000) / MAX_MOVE_SPEED_DELTA (50) both sit under MaxStatDeltaTotalRaw (~4096 stat
            // units). That ordering is the whole reason DW-650 is latent rather than open on items — so pin it. If a
            // balance pass ever raises a per-stat cap past the accumulator bound this test goes RED, instead of the
            // regression showing up as a wrapped accumulator in a match.
            var worstCase = new ItemDefinition
            {
                Id = "worst_case",
                Charges = 0,
                MaxHealthDelta    = ItemDefinitionValidator.MAX_ITEM_STAT_DELTA,
                AttackDamageDelta = ItemDefinitionValidator.MAX_ITEM_STAT_DELTA,
                ArmorDelta        = ItemDefinitionValidator.MAX_ITEM_STAT_DELTA,
                MoveSpeedDelta    = ItemDefinitionValidator.MAX_MOVE_SPEED_DELTA,
            };

            ItemValidationResult r = new ItemDefinitionValidator().Validate(worstCase);
            Assert.True(r.Ok, r.Error);

            // …and the descriptor ItemSystem actually mints from that definition is inside the bound.
            Assert.Null(MintedItemModifier(worstCase).CheckAuthoringBounds());
        }

        [Fact]
        public void ItemValidator_RejectsADescriptorOverTheAccumulatorBound()
        {
            // The adoption itself, exercised directly on the descriptor shape the validator now checks: an item whose
            // deltas are inside the 16.16 representable range but past the accumulator bound must not be admissible.
            // (Unreachable through Validate while MAX_ITEM_STAT_DELTA < MaxStatDeltaTotalRaw — the tighter per-stat cap
            // reports first, deliberately, so its more actionable message keeps precedence. This asserts the rule the
            // validator now applies, which is what survives a cap change.)
            var over = new ItemDefinition
            {
                Id = "over_bound",
                Charges = 0,
                MaxHealthDelta = Fixed.FromRaw(Modifier.MaxStatDeltaTotalRaw + 1),
            };

            (string Field, string Reason)? rejected = MintedItemModifier(over).CheckAuthoringBounds();
            Assert.NotNull(rejected);
            Assert.Equal("max_health_delta", rejected!.Value.Field);
            Assert.Contains("MaxStatDeltaTotalRaw", rejected.Value.Reason);
        }

        /// <summary>The descriptor <c>ItemSystem.ApplyItemStatModifier</c> mints for a carried stat item — permanent,
        /// <see cref="StackRule.Ignore"/>, one stack, the four authored deltas.</summary>
        private static Modifier MintedItemModifier(ItemDefinition def) =>
            new Modifier(0, -1, StackRule.Ignore, 1,
                         maxHealthDelta:    def.MaxHealthDelta,
                         attackDamageDelta: def.AttackDamageDelta,
                         moveSpeedDelta:    def.MoveSpeedDelta,
                         status: StatusFlags.None, periodEffect: null, periodTicks: 0,
                         armorDelta:        def.ArmorDelta);

        // ──────────────────── Minter 2: hero level growth (UnitDefinitionValidator) ────────────────────

        private static readonly UnitDefinitionValidator UnitV = new();

        /// <summary>A minimal valid hero unit; <paramref name="maxLevel"/> and the three growth deltas are the axes
        /// under test.</summary>
        private static UnitDefinition Hero(int maxLevel, float healthPerLevel = 0f, float damagePerLevel = 0f,
                                           float armorPerLevel = 0f) => new UnitDefinition
        {
            Id = "champion", DisplayName = "Champion", Category = "Melee",
            Hp = 100f, Speed = 4f, AttackDamage = 10f, AttackRange = 5f, AttackSpeed = 1f,
            DamageType = "Normal", ArmorType = "Unarmored",
            CostOre = 50, CostCrystal = 0, Supply = 1, VisionRange = 8f,
            Armor = 0f, TrainTime = 8f, SplashRadius = 0f, CollisionRadius = 1f, MeshScale = 1f, MaxEnergy = 0f,
            SeparationPriority = "Normal",
            IsHero = true,
            Hero = new HeroDefinition
            {
                MaxLevel = maxLevel, BaseXp = 100f, XpGrowth = 1.15f, XpPerKill = 100f, XpShareRadius = 12f,
                HealthPerLevel = healthPerLevel, DamagePerLevel = damagePerLevel, ArmorPerLevel = armorPerLevel,
            },
        };

        private static string? ErrorOn(UnitValidationResult r, string fieldPath) =>
            r.Errors.FirstOrDefault(e => e.FieldPath == fieldPath).Message;

        [Theory]
        [InlineData("hero.health_per_level")]
        [InlineData("hero.damage_per_level")]
        [InlineData("hero.armor_per_level")]
        public void HeroGrowth_OverTheAccumulatorBound_IsRejected_OnEveryGrowthField(string field)
        {
            // 200/level is INSIDE the pre-existing coarse cap (HeroStatGrowthMax = 256) and was fully valid content, but
            // a max-level-100 hero holds 99 stacks of it — 19800 stat units (~1.3e9 raw) from a single modifier id,
            // over half of int.MaxValue, so two ordinary neighbours in the same 8-slot accumulator wrap it negative.
            // HeroXpSystem mints that growth modifier directly, so nothing used to check it. RED before DW-650.
            UnitDefinition def = field switch
            {
                "hero.health_per_level" => Hero(maxLevel: 100, healthPerLevel: 200f),
                "hero.damage_per_level" => Hero(maxLevel: 100, damagePerLevel: 200f),
                _                       => Hero(maxLevel: 100, armorPerLevel: 200f),
            };

            UnitValidationResult r = UnitV.Validate(def, null, null);
            string? msg = ErrorOn(r, field);
            Assert.True(msg != null,
                $"expected a '{field}' error, got: {string.Join(" | ", r.Errors.Select(e => e.FieldPath))}");
            Assert.Contains("MaxStatDeltaTotalRaw", msg!);
            Assert.Contains("champion", msg!);
        }

        [Fact]
        public void HeroGrowth_AtTheAccumulatorBound_IsAccepted_OneStepPastIsNot()
        {
            // Boundary teeth in both directions, so the rule is neither an off-by-one that silently narrows authorable
            // content nor a bound that admits the value past it. Derived, never a literal: the largest whole-number
            // per-level growth a max-level hero (99 stacks) can carry inside MaxStatDeltaTotalRaw — 41 today.
            float atBound = (float)System.Math.Floor(
                Modifier.MaxStatDeltaTotalRaw / (double)HeroXpSystem.MaxGrowthStacks / Fixed.ONE);

            UnitValidationResult ok = UnitV.Validate(Hero(maxLevel: 100, healthPerLevel: atBound), null, null);
            Assert.True(ok.Ok, string.Join(" | ", ok.Errors.Select(e => e.Message)));

            UnitValidationResult over = UnitV.Validate(Hero(maxLevel: 100, healthPerLevel: atBound + 1f), null, null);
            Assert.Contains("MaxStatDeltaTotalRaw", ErrorOn(over, "hero.health_per_level") ?? "");
        }

        [Fact]
        public void HeroGrowth_IsBoundedByTheHerosOwnLevelCeiling_NotTheStoreStackCap()
        {
            // No false positives: ReconcileGrowth applies at most (max_level - 1) stacks, so the SAME 200/level that a
            // max-level-100 hero cannot carry is perfectly safe on a low-level hero. Bounding every hero against the
            // descriptor's MaxStacks (99) instead would reject this legal content.
            Assert.True(UnitV.Validate(Hero(maxLevel: 10, healthPerLevel: 200f), null, null).Ok);
            Assert.True(UnitV.Validate(Hero(maxLevel: 2,  healthPerLevel: 200f), null, null).Ok);

            // …and the descriptor HeroXpSystem mints for the ACCEPTED hero really is inside the bound at its own
            // worst case (9 stacks for max_level 10).
            Assert.Null(GrowthModifier(stacks: 9, healthPerLevel: Fixed.FromInt(200)).CheckAuthoringBounds());
        }

        [Fact]
        public void HeroGrowth_CoarseCapStillReportsFirst_OneBadgePerField()
        {
            // The D-9 per-field-badge contract: a value over BOTH the coarse HeroStatGrowthMax and the accumulator
            // bound produces exactly ONE error on that field, and it is the tighter, more actionable cap message.
            UnitValidationResult r = UnitV.Validate(Hero(maxLevel: 100, healthPerLevel: 1000f), null, null);
            Assert.Equal(1, r.Errors.Count(e => e.FieldPath == "hero.health_per_level"));
            Assert.DoesNotContain("MaxStatDeltaTotalRaw", ErrorOn(r, "hero.health_per_level")!);
        }

        /// <summary>The descriptor <c>HeroXpSystem.ReconcileGrowth</c> mints — permanent, <see cref="StackRule.Stack"/>,
        /// the hero's per-level deltas, no move-speed channel.</summary>
        private static Modifier GrowthModifier(int stacks, Fixed healthPerLevel) =>
            new Modifier(HeroXpSystem.HeroGrowthModifierId, -1, StackRule.Stack, stacks,
                         maxHealthDelta: healthPerLevel, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: null, periodTicks: 0, armorDelta: Fixed.Zero);

        // ──────────────────── Minter 3: research cumulative (ResearchSystem) ────────────────────

        [Fact]
        public void ResearchCumulative_AtTheFixedCeiling_IsBoundedWhenTheModifierIsBuilt()
        {
            // ResearchValidator range-checks each INDIVIDUAL level's delta against ±32768, and CompleteResearch's
            // SaturatingAdd lets a repeatable ladder's RUNNING total saturate at the full Fixed ceiling — eight times
            // the accumulator bound. One such research on a unit must contribute the bound, not the ceiling.
            var h = Wire(researchCount: 1);
            Bank(h, researchIndex: 0, cumulativeArmor: Fixed.MaxValue);

            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            h.Sys.ApplyCompletedResearch(h.World, unit);

            Assert.Equal(Modifier.MaxStatDeltaTotalRaw, h.World.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void ResearchCumulative_AFullRingOfSaturatedResearches_DoesNotWrapTheAccumulator()
        {
            // DW-488's defect shape, reached entirely through the research path: EffectCaps.MaxModifiersPerEntity
            // researches, each with a cumulative saturated at the Fixed ceiling, each its own modifier id — exactly one
            // per ring slot. Unbounded, the wrapping `_flatArmorBonus +=` sums 8 × int.MaxValue to −8 raw and the
            // Zero-floor collapses EffectiveArmor to 0: the unit ends up with LESS armor than it started with after
            // eight completed, paid-for upgrades. Bounded, the same eight sum to exactly
            // MaxModifiersPerEntity × MaxStatDeltaTotalRaw ≤ int.MaxValue and nothing wraps. RED before DW-650.
            var h = Wire(researchCount: EffectCaps.MaxModifiersPerEntity);
            for (int ri = 0; ri < EffectCaps.MaxModifiersPerEntity; ri++)
                Bank(h, ri, Fixed.MaxValue);

            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            h.Sys.ApplyCompletedResearch(h.World, unit);

            Assert.Equal(EffectCaps.MaxModifiersPerEntity, h.Modifiers.CountAt(unit)); // all eight really installed
            Assert.Equal(EffectCaps.MaxModifiersPerEntity * Modifier.MaxStatDeltaTotalRaw,
                         h.World.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void ResearchCumulative_OrdinaryTotals_AreUntouchedByTheBound()
        {
            // Non-vacuity: the clamp must be invisible to anything a designer would ship. A +5 armor ladder lands on
            // +5, not on the ceiling and not on the bound.
            var h = Wire(researchCount: 1);
            Bank(h, researchIndex: 0, cumulativeArmor: Fixed.FromInt(5));

            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            h.Sys.ApplyCompletedResearch(h.World, unit);

            Assert.Equal(Fixed.FromInt(5), h.World.EffectiveArmor[unit]);
        }

        [Fact]
        public void ResearchCumulative_BankedTotalKeepsItsFullFixedRange()
        {
            // The clamp is on the CONTRIBUTION, not on the banked total: DW-623's void/rollback snapshots the stored
            // cumulative and SaturatingAdd's Fixed-ceiling semantics (persisted + folded into SimChecksum) must be
            // untouched, or an unrelated behaviour moves under this fix.
            var h = Wire(researchCount: 1);
            Bank(h, researchIndex: 0, cumulativeArmor: Fixed.MaxValue);

            Assert.Equal(Fixed.MaxValue, h.Research.CumulativeArmorDelta[(int)Faction.Player1][0]);
        }

        private sealed class ResearchHarness
        {
            public EntityWorld World = new EntityWorld();
            public ResearchStore Research = new ResearchStore();
            public ModifierStore Modifiers = null!;
            public ResearchSystem Sys = null!;
        }

        /// <summary>A Player1 faction with <paramref name="researchCount"/> single-level armor researches, wired to a
        /// live <see cref="ModifierStore"/>/<see cref="ModifierSystem"/> pair.</summary>
        private static ResearchHarness Wire(int researchCount)
        {
            var h = new ResearchHarness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);

            var research = new List<ResearchDefinition>();
            for (int i = 0; i < researchCount; i++)
                research.Add(new ResearchDefinition
                {
                    Id = $"armor_ladder_{i}",
                    Prerequisites = System.Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 1 } }, TimeTicks = 1,
                                            ModifierDelta = new ResearchModifierDelta { ArmorDelta = 1f } },
                    },
                });

            var faction = new FactionDefinition { Id = "p1", Buildings = new List<BuildingDefinition>(), Research = research };
            h.Sys = new ResearchSystem(new BuildingStore(), new ResourceStore(Fixed.Zero), h.Research, h.Modifiers,
                                       events: null, p1Faction: faction);
            return h;
        }

        /// <summary>Bank one completed level of <paramref name="researchIndex"/> with an already-accumulated cumulative
        /// armor total — the state a repeatable ladder reaches after enough completions, set directly so the test does
        /// not depend on the (separately covered) start/tick/complete order path.</summary>
        private static void Bank(ResearchHarness h, int researchIndex, Fixed cumulativeArmor)
        {
            int f = (int)Faction.Player1;
            h.Research.CompletedLevels[f][researchIndex]      = 1;
            h.Research.CumulativeArmorDelta[f][researchIndex] = cumulativeArmor;
        }
    }
}
