#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions; // ContentHash, FactionDefinition, UnitDefinition, AbilityRegistry, ItemRegistry, ...
using ProjectChimera.Combat;           // DamageTable, DamageType, ArmorType
using ProjectChimera.Effects;          // EffectNode, DirectHpDeltaEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.16 — <see cref="ContentHash"/> folds the loaded CONTENT definitions (factions/units/buildings/research,
    /// the full ability + item registries, and the damage table) so a content-byte mismatch rejects at the handshake
    /// instead of desyncing from the first combat tick. These tests walk every I/O-matrix row: each domain mutation
    /// moves the hash, presentation-only edits do NOT, logically-equal content folds identically, the fold is
    /// deterministic + registry-order-sensitive + never-0.
    /// </summary>
    public class ContentHashTests
    {
        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────────

        private static UnitDefinition Warrior(float attackDamage = 10f) => new UnitDefinition
        {
            Id = "warrior", DisplayName = "The Warrior", Category = "Melee",
            Hp = 120f, AttackDamage = attackDamage, AttackRange = 1.5f,
            MeshPath = "res://warrior.glb", MeshScale = 1.2f,
        };

        private static BuildingDefinition Barracks(int? supplyBonus = 0) => new BuildingDefinition
        {
            Id = "barracks", DisplayName = "Barracks", Category = "Structure",
            Hp = 800f, ConstructionTime = 30f, SupplyBonus = supplyBonus, ProducesCategory = "Melee",
        };

        private static ResearchDefinition Sharpen(int timeTicks = 300) => new ResearchDefinition
        {
            Id = "sharpen", DisplayName = "Sharpen Blades", CancelRefundFraction = 0.5f,
            Levels = new List<ResearchLevel>
            {
                new ResearchLevel
                {
                    Cost = new Dictionary<string, int> { { "ore", 100 } }, TimeTicks = timeTicks,
                    ModifierDelta = new ResearchModifierDelta { AttackDamageDelta = 2f },
                },
            },
        };

        private static List<FactionDefinition> Factions(float warriorDamage = 10f, int? supplyBonus = 0, int researchTicks = 300) => new()
        {
            new FactionDefinition
            {
                Id = "alpha", DisplayName = "Alpha", Color = new[] { 1f, 0f, 0f, 1f }, AiPreset = "balanced",
                Units = new List<UnitDefinition> { Warrior(warriorDamage) },
                Buildings = new List<BuildingDefinition> { Barracks(supplyBonus) },
                Research = new List<ResearchDefinition> { Sharpen(researchTicks) },
                StartingOre = 200f, StartingCrystal = 0f,
            },
        };

        private static AbilityRegistry Abilities(int hpDelta = -25) => new AbilityRegistry(new List<AbilityDefinition>
        {
            new AbilityDefinition
            {
                Id = "smite", DisplayName = "Smite", Targeting = "TargetUnit", Activation = "active",
                CostEnergy = Fixed.FromInt(10), Cooldown = Fixed.FromInt(5),
                EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(hpDelta)),
            },
        });

        private static ItemRegistry Items(int attackDelta = 5) => new ItemRegistry(new List<ItemDefinition>
        {
            new ItemDefinition
            {
                Id = "sword", DisplayName = "Sword", Icon = "res://sword.png",
                AttackDamageDelta = Fixed.FromInt(attackDelta),
            },
        });

        /// <summary>A full damage_table.json with one optionally-overridden cell (Normal vs Unarmored).</summary>
        private static DamageTable Damage(float normalUnarmored = 1.0f)
        {
            string nu = normalUnarmored.ToString(CultureInfo.InvariantCulture);
            string json =
                "{ \"multipliers\": {" +
                "\"Normal\": {\"Unarmored\":" + nu + ",\"Light\":1.0,\"Medium\":0.75,\"Heavy\":0.5,\"Fortified\":0.35,\"Hero\":1.0}," +
                "\"Pierce\": {\"Unarmored\":1.5,\"Light\":1.0,\"Medium\":0.75,\"Heavy\":0.35,\"Fortified\":0.25,\"Hero\":1.0}," +
                "\"Siege\": {\"Unarmored\":0.5,\"Light\":0.5,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":1.5,\"Hero\":1.0}," +
                "\"Magic\": {\"Unarmored\":1.0,\"Light\":1.0,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":0.5,\"Hero\":1.0}," +
                "\"Hero\": {\"Unarmored\":1.0,\"Light\":1.0,\"Medium\":1.0,\"Heavy\":1.0,\"Fortified\":1.0,\"Hero\":1.0}" +
                "}}";
            return DamageTable.FromJson(json);
        }

        private static ItemRegistry ItemsWithEffect(int hpDelta) => new ItemRegistry(new List<ItemDefinition>
        {
            new ItemDefinition { Id = "potion", Charges = 1, EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(hpDelta)) },
        });

        /// <summary>Wrap a single unit in a minimal faction so a per-unit fold can be Compute-tested in isolation.</summary>
        private static List<FactionDefinition> FactionWithUnit(UnitDefinition u) =>
            new() { new FactionDefinition { Id = "alpha", Units = new List<UnitDefinition> { u } } };

        private static ulong Base() => ContentHash.Compute(Factions(), Abilities(), Items(), Damage());

        // ── Two-run determinism + sentinel ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Compute_IsDeterministic()
        {
            Assert.Equal(Base(), Base());
        }

        [Fact]
        public void Compute_NeverZero_EvenForEmptyContent()
        {
            ulong empty = ContentHash.Compute(new List<FactionDefinition>(), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default);
            Assert.NotEqual(0UL, empty);
            Assert.NotEqual(0UL, ContentHash.Compute(null, null, null, null));
        }

        // ── Registry index-order invariant (the fold's cross-peer determinism rests on it) ───────────────────────
        //
        // ContentHash folds the ability/item registries in `.All` ENUMERATION order (not a re-sort) so an added/removed
        // file that reindexes the registry moves the hash (the id-reindex desync). That is only cross-peer
        // deterministic because `.All` is ascending-Id REGARDLESS of input/file-load order (the registry ctor sorts).
        // If a future refactor made `.All` echo input order, two peers loading identical content in different file
        // order would false-reject (or a genuine reindex would stop being caught). This pins that invariant next to the
        // fold that depends on it.

        [Fact]
        public void AbilityRegistry_All_IsAscendingIdOrder_RegardlessOfInputOrder()
        {
            AbilityDefinition A(string id) => new AbilityDefinition { Id = id, Targeting = "TargetUnit", Activation = "active" };
            var shuffled = new AbilityRegistry(new List<AbilityDefinition> { A("gamma"), A("alpha"), A("beta") });
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, shuffled.All.Select(a => a.Id));
        }

        [Fact]
        public void ItemRegistry_All_IsAscendingIdOrder_RegardlessOfInputOrder()
        {
            ItemDefinition I(string id) => new ItemDefinition { Id = id };
            var shuffled = new ItemRegistry(new List<ItemDefinition> { I("gamma"), I("alpha"), I("beta") });
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, shuffled.All.Select(i => i.Id));
        }

        // ── Per-domain mutation moves the hash (the fail-closed reject rows) ─────────────────────────────────────

        [Fact]
        public void UnitStatMutation_MovesTheHash()
        {
            Assert.NotEqual(
                ContentHash.Compute(Factions(warriorDamage: 10f), Abilities(), Items(), Damage()),
                ContentHash.Compute(Factions(warriorDamage: 11f), Abilities(), Items(), Damage()));
        }

        [Fact]
        public void BuildingStatMutation_MovesTheHash()
        {
            Assert.NotEqual(
                ContentHash.Compute(Factions(supplyBonus: 0), Abilities(), Items(), Damage()),
                ContentHash.Compute(Factions(supplyBonus: 10), Abilities(), Items(), Damage()));
        }

        [Fact]
        public void ResearchMutation_MovesTheHash()
        {
            Assert.NotEqual(
                ContentHash.Compute(Factions(researchTicks: 300), Abilities(), Items(), Damage()),
                ContentHash.Compute(Factions(researchTicks: 301), Abilities(), Items(), Damage()));
        }

        [Fact]
        public void DamageTableMutation_MovesTheHash()
        {
            Assert.NotEqual(
                ContentHash.Compute(Factions(), Abilities(), Items(), Damage(normalUnarmored: 1.0f)),
                ContentHash.Compute(Factions(), Abilities(), Items(), Damage(normalUnarmored: 1.25f)));
        }

        [Fact]
        public void AbilityEffectMutation_MovesTheHash_ViaTheTypedEffectWalk()
        {
            // The effect NODE VALUE differs (DirectHpDelta -25 vs -30) — folded through the shared typed effect walk,
            // so a modded effect is rejectable (the I/O-matrix "ability effect mutation" row).
            Assert.NotEqual(
                ContentHash.Compute(Factions(), Abilities(hpDelta: -25), Items(), Damage()),
                ContentHash.Compute(Factions(), Abilities(hpDelta: -30), Items(), Damage()));
        }

        [Fact]
        public void ItemMutation_MovesTheHash()
        {
            Assert.NotEqual(
                ContentHash.Compute(Factions(), Abilities(), Items(attackDelta: 5), Damage()),
                ContentHash.Compute(Factions(), Abilities(), Items(attackDelta: 6), Damage()));
        }

        [Fact]
        public void ItemEffectMutation_MovesTheHash_ViaTheTypedEffectWalk()
        {
            // P5: mirror the ability-effect case for items — the item EffectGraph value differs (charged consumable),
            // folded through the SAME shared typed effect walk, so a modded item effect is rejectable.
            Assert.NotEqual(
                ContentHash.Compute(Factions(), Abilities(), ItemsWithEffect(hpDelta: -40), Damage()),
                ContentHash.Compute(Factions(), Abilities(), ItemsWithEffect(hpDelta: -50), Damage()));
        }

        // ── P1: resolve-backed fields fold the value the SIM reads (no omit-vs-default false-positive) ────────────

        [Fact]
        public void OmittedXpBounty_FoldsIdenticallyToTheAuthoredResolvedDefault()
        {
            // The sim awards ResolveXpBounty() = authored value, else CostOre+CostCrystal. A unit that OMITS xp_bounty
            // (null → derived 60) and one that AUTHORS xp_bounty: 60 are sim-identical and MUST fold identically
            // (folding the raw nullable + presence bit would false-positive-reject them).
            var omitted  = new UnitDefinition { Id = "u", CostOre = 50, CostCrystal = 10, XpBounty = null };
            var authored = new UnitDefinition { Id = "u", CostOre = 50, CostCrystal = 10, XpBounty = 60 };
            Assert.Equal(60, omitted.ResolveXpBounty()); // guard the fixture's premise
            Assert.Equal(
                ContentHash.Compute(FactionWithUnit(omitted),  AbilityRegistry.Empty, ItemRegistry.Empty, Damage()),
                ContentHash.Compute(FactionWithUnit(authored), AbilityRegistry.Empty, ItemRegistry.Empty, Damage()));
        }

        [Fact]
        public void AuthoredCostMap_FoldsIdenticallyToLegacyCostFields()
        {
            // The sim trains with ResolvedCost. An authored cost:{ore:50,crystal:10} and the legacy
            // cost_ore:50/cost_crystal:10 resolve to the SAME sparse map and MUST fold identically. XpBounty is pinned
            // equal on both so the cost equivalence is isolated (ResolveXpBounty derives from CostOre+CostCrystal).
            var mapForm = new UnitDefinition
            {
                Id = "u", XpBounty = 100,
                Cost = new Dictionary<string, int> { { "ore", 50 }, { "crystal", 10 } },
            };
            var legacyForm = new UnitDefinition { Id = "u", XpBounty = 100, CostOre = 50, CostCrystal = 10, Cost = null };
            Assert.Equal(
                ContentHash.Compute(FactionWithUnit(mapForm),    AbilityRegistry.Empty, ItemRegistry.Empty, Damage()),
                ContentHash.Compute(FactionWithUnit(legacyForm), AbilityRegistry.Empty, ItemRegistry.Empty, Damage()));
        }

        // ── Registry-order sensitivity (catches the id-reindex desync) ──────────────────────────────────────────

        [Fact]
        public void ExtraAbilityFile_ShiftsIndices_MovesTheHash()
        {
            var withExtra = new AbilityRegistry(new List<AbilityDefinition>
            {
                new AbilityDefinition { Id = "smite",  Targeting = "TargetUnit", EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(-25)) },
                new AbilityDefinition { Id = "blessing", Targeting = "Self",      EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(15)) },
            });
            Assert.NotEqual(
                ContentHash.Compute(Factions(), Abilities(), Items(), Damage()),
                ContentHash.Compute(Factions(), withExtra, Items(), Damage()));
        }

        // ── Presentation-only edits do NOT move the hash (no false-positive reject) ──────────────────────────────

        [Fact]
        public void PresentationEdits_DoNotMoveTheHash()
        {
            // Faction/unit presentation fields.
            var a = Factions();
            var b = Factions();
            b[0].DisplayName = "COMPLETELY DIFFERENT";
            b[0].Color = new[] { 0f, 1f, 0f, 0.5f };
            b[0].AiPreset = "aggressive";
            b[0].SignatureMechanicDisplay = "flashy text";
            b[0].Units[0].DisplayName = "Renamed";
            b[0].Units[0].MeshPath = "res://other.glb";
            b[0].Units[0].MeshScale = 9.9f;
            b[0].Units[0].CombatFeedback = new CombatFeedbackProfile();
            // Research presentation (P6): a research display_name is excluded.
            b[0].Research[0].DisplayName = "Renamed Research";

            // Item presentation.
            var itemsA = Items();
            var itemsB = new ItemRegistry(new List<ItemDefinition>
            {
                new ItemDefinition { Id = "sword", DisplayName = "Renamed Sword", Icon = "res://different.png", AttackDamageDelta = Fixed.FromInt(5) },
            });

            // Ability presentation (P6): display_name + combat_feedback are excluded — same Id/effect/costs.
            var abilitiesA = Abilities();
            var abilitiesB = new AbilityRegistry(new List<AbilityDefinition>
            {
                new AbilityDefinition
                {
                    Id = "smite", DisplayName = "Renamed Smite", Targeting = "TargetUnit", Activation = "active",
                    CostEnergy = Fixed.FromInt(10), Cooldown = Fixed.FromInt(5),
                    EffectGraph = new DirectHpDeltaEffect(Fixed.FromInt(-25)),
                    CombatFeedback = new CombatFeedbackProfile(),
                },
            });

            Assert.Equal(
                ContentHash.Compute(a, abilitiesA, itemsA, Damage()),
                ContentHash.Compute(b, abilitiesB, itemsB, Damage()));
        }

        // ── Logically-equal content folds identically (omit-vs-default + array reorder) ─────────────────────────

        [Fact]
        public void RosterArrayReorder_FoldsIdentically()
        {
            // Two units in a faction, authored in different LIST order → same distinct roster (sorted by Id), so the
            // hash is identical (units resolve by id string, not list index — the I/O "JSON array reordered" row).
            var f1 = new List<FactionDefinition>
            {
                new FactionDefinition { Id = "alpha", Units = new List<UnitDefinition> { Warrior(), new UnitDefinition { Id = "archer", AttackDamage = 7f } } },
            };
            var f2 = new List<FactionDefinition>
            {
                new FactionDefinition { Id = "alpha", Units = new List<UnitDefinition> { new UnitDefinition { Id = "archer", AttackDamage = 7f }, Warrior() } },
            };
            Assert.Equal(
                ContentHash.Compute(f1, AbilityRegistry.Empty, ItemRegistry.Empty, Damage()),
                ContentHash.Compute(f2, AbilityRegistry.Empty, ItemRegistry.Empty, Damage()));
        }

        [Fact]
        public void DuplicateFactionDef_DedupsToOne()
        {
            // Two lobby slots referencing the SAME faction file (same Id, identical content) fold identically to one —
            // the distinct-set dedup, so a 2-slot-same-faction match doesn't hash differently from a 1-slot one.
            var one = Factions();
            var two = new List<FactionDefinition> { Factions()[0], Factions()[0] };
            Assert.Equal(
                ContentHash.Compute(one, Abilities(), Items(), Damage()),
                ContentHash.Compute(two, Abilities(), Items(), Damage()));
        }

        [Fact]
        public void DefaultTable_AndExplicitDefaultJson_FoldIdentically()
        {
            // The in-code DamageTable.Default and a JSON table with the same cells must fold identically (no
            // false-positive between the fallback table and an authored-identical one).
            Assert.Equal(
                ContentHash.Compute(Factions(), Abilities(), Items(), DamageTable.Default),
                ContentHash.Compute(Factions(), Abilities(), Items(), Damage(normalUnarmored: 1.0f)));
        }

        // ── Breakdown: per-domain isolation ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Describe_IsolatesTheDivergingDomain()
        {
            ContentHash.Breakdown b0 = ContentHash.Describe(Factions(), Abilities(), Items(), Damage());
            // Only the damage table differs → only the DamageTable sub-hash moves; the others stay put.
            ContentHash.Breakdown bDmg = ContentHash.Describe(Factions(), Abilities(), Items(), Damage(normalUnarmored: 1.5f));
            Assert.NotEqual(b0.DamageTable, bDmg.DamageTable);
            Assert.Equal(b0.Factions, bDmg.Factions);
            Assert.Equal(b0.Abilities, bDmg.Abilities);
            Assert.Equal(b0.Items, bDmg.Items);

            // Only a unit stat differs → only the Factions sub-hash moves.
            ContentHash.Breakdown bFac = ContentHash.Describe(Factions(warriorDamage: 99f), Abilities(), Items(), Damage());
            Assert.NotEqual(b0.Factions, bFac.Factions);
            Assert.Equal(b0.DamageTable, bFac.DamageTable);
            Assert.Equal(b0.Abilities, bFac.Abilities);
            Assert.Equal(b0.Items, bFac.Items);
        }

        [Fact]
        public void Describe_Combined_EqualsCompute()
        {
            Assert.Equal(
                ContentHash.Compute(Factions(), Abilities(), Items(), Damage()),
                ContentHash.Describe(Factions(), Abilities(), Items(), Damage()).Combined);
        }

        [Fact]
        public void Breakdown_ToString_NamesEveryDomain()
        {
            string s = ContentHash.Describe(Factions(), Abilities(), Items(), Damage()).ToString();
            Assert.Contains("ruleset-caps=", s);
            Assert.Contains("factions=", s);
            Assert.Contains("abilities=", s);
            Assert.Contains("items=", s);
            Assert.Contains("damage-table=", s);
        }
    }
}
