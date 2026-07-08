#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 4.3 — <see cref="ResourceStore"/>'s generic sparse cost-map API (<c>CanAfford</c>/<c>Spend</c>/<c>Add</c>)
    /// and its wiring into <see cref="BuildingSystem"/>: <c>TrainUnit</c>'s sparse-map training path, and the
    /// "buildings now charge crystal" gap fix in <c>QueueWorkerBuild</c>/<c>GetBuildingCost</c> (previously
    /// ore-only — a nonzero <c>cost_crystal</c> on a building was silently never charged).
    /// </summary>
    public class SparseCostMapTests
    {
        // ── ResourceStore.CanAfford/Spend/Add — the generic sparse-map API ──────────────────────────────────────

        [Fact]
        public void CanAfford_EmptyMap_VacuouslyTrue()
        {
            var resources = new ResourceStore(Fixed.Zero);
            Assert.True(resources.CanAfford(Faction.Player1, new Dictionary<string, int>()));
        }

        [Fact]
        public void CanAfford_UnknownResourceId_FailsClosed()
        {
            var resources = new ResourceStore(Fixed.FromInt(1000));
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(1000);
            Assert.False(resources.CanAfford(Faction.Player1, new Dictionary<string, int> { { "gems", 1 } }));
        }

        [Fact]
        public void Spend_AtomicCheckAllThenSpendAll_OrePassesCrystalFails_SpendsNeither()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(100);
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(10);

            var cost = new Dictionary<string, int> { { "ore", 50 }, { "crystal", 30 } };
            Assert.False(resources.Spend(Faction.Player1, cost));
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, resources.Crystal[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void Spend_BothAfford_SpendsBothExactlyOnce()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(100);
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(100);

            var cost = new Dictionary<string, int> { { "ore", 50 }, { "crystal", 30 } };
            Assert.True(resources.Spend(Faction.Player1, cost));
            Assert.Equal(Fixed.FromInt(50).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(70).Raw, resources.Crystal[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void Add_CreditsEachKnownResource_IgnoresUnknownKeys()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.Add(Faction.Player1, new Dictionary<string, int> { { "ore", 40 }, { "crystal", 20 }, { "gems", 5 } });
            Assert.Equal(Fixed.FromInt(40).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(20).Raw, resources.Crystal[(int)Faction.Player1].Raw);
        }

        // ── BuildingSystem.TrainUnit — sparse-map training path (ore-omitted, crystal-only cost) ────────────────

        private static FactionDefinition SparseCostFaction()
        {
            var f = new FactionDefinition { Id = "sparse", DisplayName = "Sparse" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            // Authored sparse cost: crystal only (ore key omitted entirely) — distinct from cost_ore:0/cost_crystal:N.
            f.Units.Add(new UnitDefinition
            {
                Id = "crystal_only", Category = "Melee", Hp = 100f,
                Cost = new Dictionary<string, int> { { "crystal", 30 } },
            });
            return f;
        }

        [Fact]
        public void TrainUnit_SparseCostMap_CrystalOnly_ChecksAndSpendsCrystalOnly_OreUntouched()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(500); // plenty — must stay untouched
            resources.Crystal[(int)Faction.Player1]   = Fixed.FromInt(100);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, SparseCostFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // crystal_only
            Assert.Equal(Fixed.FromInt(500).Raw, resources.Ore[(int)Faction.Player1].Raw);     // untouched
            Assert.Equal(Fixed.FromInt(70).Raw, resources.Crystal[(int)Faction.Player1].Raw);  // 100 - 30
        }

        [Fact]
        public void TrainUnit_SparseCostMap_InsufficientCrystal_Refuses_SpendsNothing()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(500);
            resources.Crystal[(int)Faction.Player1]   = Fixed.FromInt(10); // < 30 required
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, SparseCostFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(Fixed.FromInt(500).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, resources.Crystal[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        // ── BuildingSystem.QueueWorkerBuild / GetBuildingCost — the crystal-on-buildings gap fix ────────────────

        private static FactionDefinition CrystalBuildingFaction()
        {
            var f = new FactionDefinition { Id = "crystal_building", DisplayName = "CB" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 100, CostCrystal = 50,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            return f;
        }

        [Fact]
        public void GetBuildingCost_ReturnsSparseMap_IncludingCrystal()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var sys = new BuildingSystem(buildings, resources, CrystalBuildingFaction());

            var cost = sys.GetBuildingCost(BuildingType.Barracks, Faction.Player1);
            Assert.Equal(100, cost["ore"]);
            Assert.Equal(50, cost["crystal"]);
        }

        [Fact]
        public void QueueWorkerBuild_ChecksAndSpendsBothOreAndCrystal_PreviouslySilentlyFreeCrystal()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(200);
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(100);
            var sys = new BuildingSystem(buildings, resources, CrystalBuildingFaction());

            int worker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker] = GatherState.Idle;

            int bId = sys.QueueWorkerBuild(worker, BuildingType.Barracks,
                new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10)), Faction.Player1, resources, world);

            Assert.True(bId >= 0);
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Ore[(int)Faction.Player1].Raw);     // 200 - 100
            Assert.Equal(Fixed.FromInt(50).Raw, resources.Crystal[(int)Faction.Player1].Raw);  // 100 - 50 (was NEVER charged pre-4.3)
        }

        [Fact]
        public void QueueWorkerBuild_InsufficientCrystal_Refuses_SpendsNeitherOreNorCrystal()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(200); // affords ore
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(5);   // does NOT afford the 50 crystal
            var sys = new BuildingSystem(buildings, resources, CrystalBuildingFaction());

            int worker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker] = GatherState.Idle;

            int bId = sys.QueueWorkerBuild(worker, BuildingType.Barracks,
                new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10)), Faction.Player1, resources, world);

            Assert.Equal(-1, bId); // rejected — ore-only afford is no longer sufficient
            Assert.Equal(Fixed.FromInt(200).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(5).Raw, resources.Crystal[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void QueueWorkerBuild_PlacementFailure_RefundsBothOreAndCrystal()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]     = Fixed.FromInt(200);
            resources.Crystal[(int)Faction.Player1] = Fixed.FromInt(100);
            var sys = new BuildingSystem(buildings, resources, CrystalBuildingFaction());

            int worker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker] = GatherState.Idle;

            // Fill the BuildingStore to force Create() to fail (id < 0), triggering the refund branch.
            while (buildings.Count < BuildingStore.MAX_BUILDINGS)
                buildings.Create(FixedVec3.Zero, Faction.Player2, BuildingType.Barracks);

            int bId = sys.QueueWorkerBuild(worker, BuildingType.Barracks,
                new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10)), Faction.Player1, resources, world);

            Assert.Equal(-1, bId);
            Assert.Equal(Fixed.FromInt(200).Raw, resources.Ore[(int)Faction.Player1].Raw);     // refunded
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Crystal[(int)Faction.Player1].Raw); // refunded
        }
    }
}
