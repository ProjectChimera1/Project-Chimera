#nullable enable
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// spec-command-card-producer-surfaces — the single active command-card producer surface (DW-31 / DW-90 / DW-168).
    ///
    /// Proves the Godot-free sim core: <see cref="BuildingSystem.ResolveCommandCardSurface"/> resolves EXACTLY one
    /// surface per building (authored <c>command_card_producer</c> authoritative, else the Train → Research → Shop →
    /// Revive → None derivation that preserves today's single-capability behaviour), the def-aware
    /// <see cref="BuildingSystem.GetProductionUnits"/> lists a Custom producer's authored-category roster (not Melee),
    /// and <see cref="BuildingDefinitionValidator"/> rejects a bogus surface value. Mirrors the existing test culture
    /// (<see cref="ProductionSelectionTests"/>, <c>CustomBuildingPlacementTests</c>). No Godot, no goldens.
    /// </summary>
    public class CommandCardSurfaceTests
    {
        // A faction authoring every building shape the surface matrix needs: the built-in producers + command_center,
        // a Custom Air producer (sky_forge), a Custom Melee producer that also revives (temple_forge), plus revive-only
        // / shop-only / research-only / dual (revive+shop) Custom buildings, and a "none"-declaring producer. Units
        // give the Custom producer a category to route to for the def-aware GetProductionUnits arm.
        private static FactionDefinition TestFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker",  Category = "Worker", Hp = 50f });   // 0
            f.Units.Add(new UnitDefinition { Id = "melee_a", Category = "Melee",  Hp = 100f });  // 1
            f.Units.Add(new UnitDefinition { Id = "melee_b", Category = "Melee",  Hp = 110f });  // 2
            f.Units.Add(new UnitDefinition { Id = "air",     Category = "Air",    Hp = 200f });  // 3

            f.Buildings.Add(B("command_center", produces: "Worker"));
            f.Buildings.Add(B("barracks",       produces: "Melee"));
            // A Custom Air producer, no declaration → derives Train (Air roster).
            f.Buildings.Add(B("sky_forge", produces: "Air"));
            // A producer (Melee) that ALSO revives, declaring "revive" → Revive wins over the Train priority (DW-31).
            f.Buildings.Add(B("temple_forge", produces: "Melee", revives: true, declare: "revive"));
            // A revive-only building (non-producer): produces_category "None" → derives Revive.
            f.Buildings.Add(B("shrine", produces: "None", revives: true));
            // A shop-only building.
            f.Buildings.Add(B("bazaar", produces: "None", sells: true));
            // A research-only building.
            f.Buildings.Add(B("library", produces: "None", research: new[] { "upgrade_x" }));
            // Overlapping revive + shop, NO declaration → Shop wins by priority, single surface (DW-90).
            f.Buildings.Add(B("emporium", produces: "None", revives: true, sells: true));
            // A producer declaring "none" → surface None despite train eligibility.
            f.Buildings.Add(B("silent_forge", produces: "Melee", declare: "none"));

            // ── Eligible authoritative declarations (train/research/shop branches) ──
            f.Buildings.Add(B("declared_train_forge", produces: "Melee", declare: "train"));
            f.Buildings.Add(B("declared_research_hall", produces: "None", research: new[] { "upgrade_x" }, declare: "research"));
            f.Buildings.Add(B("declared_shop_stall", produces: "None", sells: true, declare: "shop"));

            // ── INELIGIBLE declarations must fall through to derivation, never blank the card ──
            // A Custom Melee producer declaring "revive" but with no revive flag → not revive-eligible → derives Train.
            f.Buildings.Add(B("misdeclared_reviver", produces: "Melee", declare: "revive"));
            // A research-only building declaring "shop" but selling nothing → not shop-eligible → derives Research.
            f.Buildings.Add(B("misdeclared_shopper", produces: "None", research: new[] { "upgrade_x" }, declare: "shop"));
            return f;
        }

        private static BuildingDefinition B(string id, string produces, bool revives = false,
                                            bool sells = false, string[]? research = null, string? declare = null)
            => new BuildingDefinition
            {
                Id = id, DisplayName = id, Category = "Structure",
                Hp = 200f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = produces,
                RevivesHeroes = revives, SellsItems = sells,
                AvailableResearch = research ?? System.Array.Empty<string>(),
                CommandCardProducer = declare,
            };

        private static (BuildingSystem sys, BuildingStore buildings) Harness(FactionDefinition faction)
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            var sys = new BuildingSystem(buildings, resources, faction);
            return (sys, buildings);
        }

        private static CommandCardSurface SurfaceOfBuiltIn(BuildingType type)
        {
            var (sys, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(type, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            return sys.ResolveCommandCardSurface(b);
        }

        private static CommandCardSurface SurfaceOfById(string buildingId)
        {
            var (sys, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirectById(buildingId, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            return sys.ResolveCommandCardSurface(b);
        }

        // ── Derivation for every built-in producer (unchanged behaviour) ───────────────────────────────

        [Theory]
        [InlineData(BuildingType.Barracks)]
        [InlineData(BuildingType.ArcheryRange)]
        [InlineData(BuildingType.SiegeWorkshop)]
        [InlineData(BuildingType.Aviary)]
        public void BuiltInProducer_DerivesTrain(BuildingType type)
            => Assert.Equal(CommandCardSurface.Train, SurfaceOfBuiltIn(type));

        [Fact]
        public void CommandCenter_DerivesNone()
            => Assert.Equal(CommandCardSurface.None, SurfaceOfBuiltIn(BuildingType.CommandCenter));

        // ── Custom producer + single-capability derivations ────────────────────────────────────────────

        [Fact]
        public void CustomAirProducer_NoDeclaration_DerivesTrain()
            => Assert.Equal(CommandCardSurface.Train, SurfaceOfById("sky_forge"));

        [Fact]
        public void ReviveOnly_DerivesRevive()
            => Assert.Equal(CommandCardSurface.Revive, SurfaceOfById("shrine"));

        [Fact]
        public void ShopOnly_DerivesShop()
            => Assert.Equal(CommandCardSurface.Shop, SurfaceOfById("bazaar"));

        [Fact]
        public void ResearchOnly_DerivesResearch()
            => Assert.Equal(CommandCardSurface.Research, SurfaceOfById("library"));

        // ── Authored declaration is authoritative ───────────────────────────────────────────────────────

        [Fact]
        public void ProduceAndRevive_DeclaringRevive_ResolvesRevive_NotTrain() // DW-31
            => Assert.Equal(CommandCardSurface.Revive, SurfaceOfById("temple_forge"));

        [Fact]
        public void DeclaredNone_ResolvesNone_DespiteTrainEligibility()
            => Assert.Equal(CommandCardSurface.None, SurfaceOfById("silent_forge"));

        [Fact]
        public void OverlappingReviveAndShop_NoDeclaration_ResolvesExactlyOneSurface_ShopByPriority() // DW-90
            => Assert.Equal(CommandCardSurface.Shop, SurfaceOfById("emporium"));

        // ── Eligible authoritative declarations: train / research / shop branches ────────────────────────

        [Fact]
        public void DeclaredTrain_OnEligibleProducer_ResolvesTrain()
            => Assert.Equal(CommandCardSurface.Train, SurfaceOfById("declared_train_forge"));

        [Fact]
        public void DeclaredResearch_OnEligibleResearchBuilding_ResolvesResearch()
            => Assert.Equal(CommandCardSurface.Research, SurfaceOfById("declared_research_hall"));

        [Fact]
        public void DeclaredShop_OnEligibleSeller_ResolvesShop()
            => Assert.Equal(CommandCardSurface.Shop, SurfaceOfById("declared_shop_stall"));

        // ── Ineligible declarations fall through to derivation — NEVER the ineligible surface, NEVER None ──

        [Fact]
        public void DeclaredRevive_OnPlainBuiltInBarracks_FallsThroughToTrain()
        {
            // A built-in Barracks whose def declares "revive" but carries no revive flag: revive is ineligible, so the
            // declaration is ignored and the surface derives Train (a plain producer) — the card is never blanked.
            var faction = TestFaction();
            faction.GetBuilding("barracks")!.CommandCardProducer = "revive";
            var (sys, _) = Harness(faction);
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            CommandCardSurface s = sys.ResolveCommandCardSurface(b);
            Assert.Equal(CommandCardSurface.Train, s);
            Assert.NotEqual(CommandCardSurface.Revive, s);
            Assert.NotEqual(CommandCardSurface.None, s);
        }

        [Fact]
        public void DeclaredRevive_OnCustomProducerWithoutReviveFlag_FallsThroughToTrain()
        {
            CommandCardSurface s = SurfaceOfById("misdeclared_reviver");
            Assert.Equal(CommandCardSurface.Train, s);
            Assert.NotEqual(CommandCardSurface.Revive, s);
            Assert.NotEqual(CommandCardSurface.None, s);
        }

        [Fact]
        public void DeclaredShop_OnResearchOnlyBuilding_FallsThroughToResearch()
        {
            CommandCardSurface s = SurfaceOfById("misdeclared_shopper");
            Assert.Equal(CommandCardSurface.Research, s);
            Assert.NotEqual(CommandCardSurface.Shop, s);
            Assert.NotEqual(CommandCardSurface.None, s);
        }

        // ── Bounds / alive guard ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void OutOfRangeBuildingId_ResolvesNone()
        {
            var (sys, _) = Harness(TestFaction());
            Assert.Equal(CommandCardSurface.None, sys.ResolveCommandCardSurface(999));
            Assert.Equal(CommandCardSurface.None, sys.ResolveCommandCardSurface(-1));
        }

        // ── Def-aware GetProductionUnits (DW-168) ───────────────────────────────────────────────────────

        [Fact]
        public void GetProductionUnits_CustomProducer_ListsAuthoredCategoryRoster_NotMelee()
        {
            var (sys, _) = Harness(TestFaction());
            // sky_forge authors produces_category "Air" → the Air roster (air, index 3), never the Melee roster.
            var air = sys.GetProductionUnits(BuildingType.Custom, Faction.Player1, "sky_forge");
            Assert.Single(air);
            Assert.Equal("air", air[0].Def.Id);
            Assert.Equal(3, air[0].Index);
        }

        [Fact]
        public void GetProductionUnits_BuiltInTwoArgCall_StaysByteIdentical()
        {
            var (sys, _) = Harness(TestFaction());
            // The 2-arg (no definitionId) call is unchanged: a Barracks lists the Melee roster (melee_a, melee_b).
            var melee = sys.GetProductionUnits(BuildingType.Barracks, Faction.Player1);
            Assert.Equal(2, melee.Count);
            Assert.Equal(new[] { "melee_a", "melee_b" }, melee.Select(m => m.Def.Id).ToArray());
        }

        [Fact]
        public void GetProductionUnit_Singular_CustomProducer_ResolvesAuthoredCategoryFirstUnit_NotMelee()
        {
            var (sys, _) = Harness(TestFaction());
            // sky_forge authors produces_category "Air" → the singular overload returns the Air unit, never melee_a.
            UnitDefinition? air = sys.GetProductionUnit(BuildingType.Custom, Faction.Player1, "sky_forge");
            Assert.NotNull(air);
            Assert.Equal("air", air!.Id);
        }

        [Fact]
        public void GetProductionUnit_Singular_NullAndTwoArg_StayByteIdentical()
        {
            var (sys, _) = Harness(TestFaction());
            // The null definitionId path is the byte-identical enum-only resolution: a Barracks → first Melee (melee_a).
            Assert.Equal("melee_a", sys.GetProductionUnit(BuildingType.Barracks, Faction.Player1, null)?.Id);
            Assert.Equal("melee_a", sys.GetProductionUnit(BuildingType.Barracks, Faction.Player1)?.Id);
        }

        // ── Validator: the new command_card_producer field ──────────────────────────────────────────────

        [Fact]
        public void Validator_RejectsBogusCommandCardProducer_LocatedError()
        {
            var def = B("weird", produces: "Melee", declare: "bogus");
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.FieldPath == "command_card_producer"
                                        && e.Message.Contains("command_card_producer"));
        }

        [Theory]
        [InlineData("train")]
        [InlineData("Revive")]   // case-insensitive
        [InlineData("NONE")]
        [InlineData(null)]        // absent = derive → always accepted
        public void Validator_AcceptsValidOrAbsentCommandCardProducer(string? value)
        {
            var def = B("ok", produces: "Melee", declare: value);
            BuildingValidationResult r = BuildingDefinitionValidator.Validate(def);
            // The field under test never contributes an error for a valid/absent value (other fields are all authored).
            Assert.DoesNotContain(r.Errors, e => e.FieldPath == "command_card_producer");
        }
    }
}
