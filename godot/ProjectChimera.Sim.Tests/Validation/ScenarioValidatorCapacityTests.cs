#nullable enable
using ProjectChimera.Core;              // ResourceNodeStore, BuildingStore
using ProjectChimera.Core.Definitions;  // ScenarioData & co, ScenarioValidator, ValidationResult
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-230 — fail-closed store-capacity caps. The applier writes resource nodes into a fixed-size
    /// <see cref="ResourceNodeStore"/> (<see cref="ResourceNodeStore.MAX_NODES"/>) and buildings into a fixed-size
    /// <see cref="BuildingStore"/> (<see cref="BuildingStore.MAX_BUILDINGS"/>); past the cap the store's Create
    /// returns -1 and the overflow entry silently vanished (pre-fix, no diagnostic and no gate rejection). These
    /// tests pin that an over-capacity scenario is REJECTED at the validator gate — located, naming the collection,
    /// its count, and the cap — while an exactly-at-capacity scenario still passes.
    /// </summary>
    public class ScenarioValidatorCapacityTests
    {
        private static readonly ScenarioValidator Validator = new ScenarioValidator();

        /// <summary>A 2-player blank scenario (bounds/slots valid) to hang the collection under test off of.</summary>
        private static ScenarioData Blank() => ScenarioData.CreateBlank("capacity", suggestedPlayers: 2);

        /// <summary><paramref name="count"/> distinct, individually-valid resource nodes on a small grid near origin
        /// (comfortably inside the blank map's bounds), so only the COUNT can trip the validator.</summary>
        private static ScenarioResourceNode[] Nodes(int count)
        {
            var nodes = new ScenarioResourceNode[count];
            for (int i = 0; i < count; i++)
                nodes[i] = new ScenarioResourceNode
                {
                    X = -20f + (i % 8) * 4f, Z = -20f + (i / 8) * 4f,
                    Supply = 100f, Rate = 5f, MaxGatherers = 4,
                };
            return nodes;
        }

        /// <summary><paramref name="count"/> distinct, individually-valid pre-built CommandCenters on a small grid,
        /// each on a declared slot (0/1), so only the COUNT can trip the validator.</summary>
        private static ScenarioBuilding[] Buildings(int count)
        {
            var buildings = new ScenarioBuilding[count];
            for (int i = 0; i < count; i++)
                buildings[i] = new ScenarioBuilding
                {
                    Type = "CommandCenter", Slot = i % 2,
                    X = -20f + (i % 8) * 4f, Z = -20f + (i / 8) * 4f, PreBuilt = true,
                };
            return buildings;
        }

        [Fact]
        public void ResourceNodes_OverCap_RejectedLocated_NamingCountAndCap()
        {
            ScenarioData m = Blank();
            m.ResourceNodes = Nodes(ResourceNodeStore.MAX_NODES + 1); // 65

            ValidationResult r = Validator.Validate(m);

            Assert.False(r.Ok);
            Assert.Contains("resource_nodes", r.Error);
            Assert.Contains((ResourceNodeStore.MAX_NODES + 1).ToString(), r.Error); // the offending count (65)
            Assert.Contains(ResourceNodeStore.MAX_NODES.ToString(), r.Error);        // the cap (64)
        }

        [Fact]
        public void ResourceNodes_AtCap_Passes()
        {
            ScenarioData m = Blank();
            m.ResourceNodes = Nodes(ResourceNodeStore.MAX_NODES); // 64

            ValidationResult r = Validator.Validate(m);

            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void Buildings_OverCap_RejectedLocated_NamingCountAndCap()
        {
            ScenarioData m = Blank();
            m.Buildings = Buildings(BuildingStore.MAX_BUILDINGS + 1); // 65

            ValidationResult r = Validator.Validate(m);

            Assert.False(r.Ok);
            Assert.Contains("buildings", r.Error);
            Assert.Contains((BuildingStore.MAX_BUILDINGS + 1).ToString(), r.Error); // the offending count (65)
            Assert.Contains(BuildingStore.MAX_BUILDINGS.ToString(), r.Error);        // the cap (64)
        }

        [Fact]
        public void Buildings_AtCap_Passes()
        {
            ScenarioData m = Blank();
            m.Buildings = Buildings(BuildingStore.MAX_BUILDINGS); // 64

            ValidationResult r = Validator.Validate(m);

            Assert.True(r.Ok, r.Error);
        }
    }
}
