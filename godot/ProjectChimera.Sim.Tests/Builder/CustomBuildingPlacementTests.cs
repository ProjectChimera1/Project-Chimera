#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Builder
{
    /// <summary>
    /// Story 6.8 — an authored (custom) building placed end-to-end: it survives the Tier-1 validator gate (as an
    /// owner-faction building-def id, not just a legacy enum name), applies as a <see cref="BuildingType.Custom"/>
    /// building carrying its <c>DefinitionId</c> + Health/SupplyBonus/ConstructionTime resolved from the
    /// <see cref="BuildingDefinition"/>, and NEVER collapses to <c>CommandCenter</c> (the retired
    /// <c>ParseBuildingType</c> default). A truly unknown id fails closed; a snake_case built-in id round-trips to
    /// its proper enum; and a custom producer's production routes through its authored <c>produces_category</c>.
    /// All Godot-free.
    /// </summary>
    public class CustomBuildingPlacementTests
    {
        // ── A faction that authors a custom "watchtower" + a custom Air producer "sky_forge", plus the built-in
        //    command_center/barracks (so a snake_case built-in id resolves), and Melee/Air units so the producer
        //    has a category to route to. ──
        private static FactionDefinition CustomFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition { Id = "worker",  Category = "Worker", Hp = 50f });   // 0
            f.Units.Add(new UnitDefinition { Id = "melee_a", Category = "Melee",  Hp = 100f, Supply = 1, CostOre = 50 }); // 1
            f.Units.Add(new UnitDefinition { Id = "air",     Category = "Air",    Hp = 200f, Supply = 1, CostOre = 50 }); // 2
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                Hp = 300f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "watchtower", DisplayName = "Watch Tower", Category = "Structure",
                Hp = 150f, ConstructionTime = 8f, SupplyBonus = 3, ProducesCategory = "Melee",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "sky_forge", DisplayName = "Sky Forge", Category = "Structure",
                Hp = 250f, ConstructionTime = 12f, SupplyBonus = 0, ProducesCategory = "Air",
            });
            // A pathological but valid ([a-z0-9_]) all-digit authored id — guards the applier's numeric-parse trap.
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "5", DisplayName = "Numeric Id Tower", Category = "Structure",
                Hp = 111f, ConstructionTime = 4f, SupplyBonus = 1, ProducesCategory = "Melee",
            });
            return f;
        }

        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;
            defs[(int)Faction.Player2] = faction;
            return defs;
        }

        private static ScenarioData ModelWithBuilding(string type) => new ScenarioData
        {
            Id = "custom_map", DisplayName = "Custom", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://x.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings = new[] { new ScenarioBuilding { Type = type, Slot = 0, X = -40f, Z = 5f, PreBuilt = true } },
            Units = System.Array.Empty<ScenarioUnit>(),
        };

        private static (SimulationHost host, ScenarioApplier applier, FactionDefinition?[] slotDefs) NewHost()
        {
            var faction = CustomFaction();
            var slotDefs = SlotDefs(faction);
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier, slotDefs);
        }

        private static int FindBuilding(BuildingStore b, string definitionId)
        {
            for (int i = 0; i < b.Count; i++)
                if (b.Alive[i] && b.DefinitionId[i] == definitionId) return i;
            return -1;
        }

        [Fact]
        public void CustomBuilding_ValidatesApplies_AsCustomTyped_WithResolvedStats_NeverCommandCenter()
        {
            var model = ModelWithBuilding("watchtower");
            var (host, applier, slotDefs) = NewHost();

            // Tier-1 gate: accepted BECAUSE the owner faction authors a "watchtower" building def.
            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);

            int id = FindBuilding(host.Buildings, "watchtower");
            Assert.True(id >= 0, "watchtower building was not placed");
            Assert.Equal(BuildingType.Custom, host.Buildings.Type[id]);
            Assert.NotEqual(BuildingType.CommandCenter, host.Buildings.Type[id]); // never collapsed to CC
            // Stats come from the resolved BuildingDefinition (not the per-type switch default of HP 200).
            Assert.Equal(Fixed.FromFloat(150f), host.Buildings.Health[id]);
            Assert.Equal(Fixed.FromFloat(150f), host.Buildings.MaxHealth[id]);
            Assert.Equal(3, host.Buildings.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(8f), host.Buildings.ConstructionDuration[id]);
            Assert.Equal(Fixed.Zero, host.Buildings.ConstructionTimer[id]); // pre_built
        }

        [Fact]
        public void UnknownBuildingId_FailsClosed_MessageNamesIt()
        {
            var model = ModelWithBuilding("nonexistent_bldg");
            var (_, _, slotDefs) = NewHost();

            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.False(r.Ok);
            Assert.Contains("nonexistent_bldg", r.Error);
        }

        [Fact]
        public void UnknownBuildingId_WithoutFactionDefs_AlsoFailsClosed_EnumNameOnly()
        {
            // No faction defs threaded (the legacy enum-only gate) — an authored id is not accepted either.
            var model = ModelWithBuilding("watchtower");
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok);
        }

        [Fact]
        public void SnakeCaseBuiltInId_RoundTripsToProperEnum_ViaBuildingTypeFromId()
        {
            var model = ModelWithBuilding("barracks"); // authored id, NOT "Barracks"
            var (host, applier, slotDefs) = NewHost();

            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);

            int id = FindBuilding(host.Buildings, "barracks");
            Assert.True(id >= 0);
            Assert.Equal(BuildingType.Barracks, host.Buildings.Type[id]); // placed as the proper enum, not Custom
        }

        [Fact]
        public void LegacyEnumName_StillTakesTheByteIdenticalDirectPath()
        {
            var model = ModelWithBuilding("CommandCenter"); // PascalCase enum name (legacy)
            var (host, applier, slotDefs) = NewHost();

            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);
            int id = FindBuilding(host.Buildings, "command_center");
            Assert.True(id >= 0);
            Assert.Equal(BuildingType.CommandCenter, host.Buildings.Type[id]);
        }

        // ── Custom producer: production routes through the authored produces_category, not the Melee default ──

        [Fact]
        public void CustomProducer_RoutesProduction_ThroughAuthoredProducesCategory_NotMeleeDefault()
        {
            var faction   = CustomFaction();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, faction);

            // sky_forge authors produces_category = "Air"; the Air unit is at Units index 2.
            int b = sys.PlaceBuildingDirectById("sky_forge", Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Assert.True(b >= 0);
            Assert.Equal(BuildingType.Custom, buildings.Type[b]);

            Assert.True(sys.TrainUnit(b, resources));
            // ProductionQueue is (Units index + 1). Air is index 2 → 3. Had it defaulted to Melee it would be melee_a
            // (index 1 → 2); had it found no unit it would be the fallback sentinel (255). Proves the Air routing.
            Assert.Equal((byte)(2 + 1), buildings.ProductionQueue[b]);
        }

        [Fact]
        public void CustomProducer_WithNoMatchingUnit_DoesNotTrainCrossCategory()
        {
            // watchtower authors produces_category = "Melee": a chosen AIR unit must be HARD-REJECTED (category guard).
            var faction   = CustomFaction();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, faction);

            int b = sys.PlaceBuildingDirectById("watchtower", Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Assert.True(b >= 0);

            // Choosing the Air unit (index 2) at a Melee producer is a cross-category request → rejected, nothing queued.
            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 2));
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        // ── Review-pass hardening: the bare "Custom" sentinel + numeric-parse edge cases (Story 6.8 review) ──

        [Fact]
        public void CustomSentinelName_FailsClosed_NotAnInvisibleGhost()
        {
            // "Custom" is a real BuildingType enum name but resolves NO def → a stat-less, unrendered ghost. The
            // validator must reject it closed (a custom building must name its authored id, not the sentinel).
            var model = ModelWithBuilding("Custom");
            var (_, _, slotDefs) = NewHost();

            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.False(r.Ok);
            Assert.Contains("Custom", r.Error);
        }

        [Fact]
        public void NumericAuthoredId_RoutesById_NotThroughEnumNumericParse()
        {
            // "5" parses as (int)BuildingType.Custom via Enum.TryParse's numeric path; the applier's name-round-trip
            // guard must reject that and route by-id so the authored def ("5") resolves with real stats + a stable id
            // — never the stat-less switch default a mis-routed PlaceBuildingDirect(Custom) would give.
            var model = ModelWithBuilding("5");
            var (host, applier, slotDefs) = NewHost();

            ValidationResult r = new ScenarioValidator().Validate(model, slotDefs);
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);

            int id = FindBuilding(host.Buildings, "5");
            Assert.True(id >= 0, "numeric-id building was not placed by-id");
            Assert.Equal(BuildingType.Custom, host.Buildings.Type[id]);
            Assert.Equal(Fixed.FromFloat(111f), host.Buildings.Health[id]); // real stats, not the statless default
        }
    }
}
