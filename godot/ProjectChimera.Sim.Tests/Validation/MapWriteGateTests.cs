#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 14.7 (DW-164) — teeth for <see cref="MapWriteGate.Check"/>, the single HARD pre-write gate the editor's
    /// map Export and New-Map paths MUST consult before any disk write. The gate wraps the same
    /// <see cref="ScenarioValidator.Validate"/> the load path uses, so a rejected write is exactly a scenario the
    /// validator deems invalid (the class that hard-fails <c>CheckCoord</c> / slot checks on reload).
    ///
    /// RED-teeth proof: stub <see cref="MapWriteGate.Check"/> to <c>return null;</c> unconditionally (the pre-fix
    /// ungated write path) and the stranded-content + slot-overflow tests below turn RED; restoring the real gate
    /// returns them GREEN.
    ///
    /// Reuses the <see cref="NegativeValidationTests"/> fixture shape (a minimal valid model), since the gate's
    /// verdict is defined entirely by the validator it consumes.
    /// </summary>
    public class MapWriteGateTests
    {
        /// <summary>A minimal VALID model: two declared slots, an in-bounds node, building, and unit.</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
        };

        [Fact]
        public void ValidScenario_PassesGate_ReturnsNull()
        {
            // A scenario the validator accepts must produce a null gate verdict — the happy path both write paths take.
            string? verdict = MapWriteGate.Check(ValidModel());
            Assert.Null(verdict);
        }

        [Fact]
        public void StrandedContentPastMapBounds_IsBlocked_LocatingTheField()
        {
            // A resource node stranded past MapBounds (e.g. after a map-size shrink) is the exact class that
            // hard-fails CheckCoord on reload. The gate must block it with a located error naming the field + bounds.
            var m = ValidModel();
            m.ResourceNodes[0].X = 200f; // inside the Fixed range but outside map_bounds 120
            string? verdict = MapWriteGate.Check(m);
            Assert.NotNull(verdict);
            Assert.Contains("map_bounds", verdict!);
            Assert.Contains("resource_nodes[0].x", verdict!);
        }

        [Fact]
        public void SlotOverflow_IsBlocked_LocatingTheSlot()
        {
            // A player_slot above the engine Faction ceiling (Story 9.2: Player8 → max slot 7) hard-fails on reload.
            // The gate must block it with a located slot error.
            var m = ValidModel();
            m.PlayerSlots[1].Slot = 8; // == PLAYER_COUNT — the first slot beyond the [0,8) valid range
            string? verdict = MapWriteGate.Check(m);
            Assert.NotNull(verdict);
            Assert.Contains("slot", verdict!);
            Assert.Contains("player_slots[1].slot", verdict!);
        }

        // ── Review pass 1 (patch): the two-arg slotFactionDefs pass-through — the one behavior unique to the gate's
        //    signature (the Export path forwards _ctx.SlotFactionDefs so a pre-placed CUSTOM building's authored id
        //    resolves as reload would). Proves Check() actually threads the defs through: the SAME scenario is
        //    REJECTED with null defs (custom Type unresolvable → enum-name-only) yet ACCEPTED once the owner faction
        //    declares it. Mirrors the CustomFaction/SlotDefs setup in Builder/CustomBuildingPlacementTests.

        /// <summary>A faction that authors a custom building id "watchtower" (not a BuildingType enum name).</summary>
        private static FactionDefinition CustomFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "watchtower", DisplayName = "Watch Tower", Category = "Structure",
                Hp = 150f, ConstructionTime = 8f, SupplyBonus = 3, ProducesCategory = "Melee",
            });
            return f;
        }

        /// <summary>Per-slot defs with slot 0's owner faction (Player1) assigned the custom faction.</summary>
        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;
            return defs;
        }

        /// <summary>A minimal model with one pre-placed building of the given authored <paramref name="type"/> on slot 0.</summary>
        private static ScenarioData ModelWithCustomBuilding(string type) => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings = new[] { new ScenarioBuilding { Type = type, Slot = 0, X = -40f, Z = 5f, PreBuilt = true } },
            Units = System.Array.Empty<ScenarioUnit>(),
        };

        [Fact]
        public void CustomBuilding_WithNullFactionDefs_IsBlocked()
        {
            // Null defs → the custom "watchtower" id resolves enum-name-only → not a known BuildingType → blocked.
            string? verdict = MapWriteGate.Check(ModelWithCustomBuilding("watchtower"), slotFactionDefs: null);
            Assert.NotNull(verdict);
        }

        [Fact]
        public void CustomBuilding_WithResolvingFactionDefs_PassesGate()
        {
            // The SAME model passes once the owner faction's defs are threaded through — proving Check() forwards them.
            string? verdict = MapWriteGate.Check(ModelWithCustomBuilding("watchtower"), SlotDefs(CustomFaction()));
            Assert.Null(verdict);
        }
    }
}
