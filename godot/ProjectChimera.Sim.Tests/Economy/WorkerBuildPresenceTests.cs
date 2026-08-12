#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;  // FactionDefinition, BuildingDefinition
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-937 — the WC3 construction model. Before this, <c>TickConstruction</c> self-ticked every site and the
    /// worker's ONLY involvement was walking there to clear its own Build command — so a builder was free the
    /// moment it was ordered, and the building rose regardless (the 2026-08-12 field report). Now a WORKER-BUILT
    /// site (<see cref="BuildingStore.RequiresBuilder"/>, set by <c>QueueWorkerBuild</c>) advances only while an
    /// assigned builder stands at it: the timer waits during the walk, the builder is HELD in the Build command
    /// until completion, pulling the builder away PAUSES the site, and completion releases the builder back to the
    /// gather loop. Direct placements (scenario/editor/debug — no builder by design) keep the self-ticking timer.
    /// Godot-free, <see cref="Fixed"/>-only.
    /// </summary>
    public class WorkerBuildPresenceTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static FactionDefinition BuilderFaction()
        {
            var f = new FactionDefinition { Id = "dw937", DisplayName = "DW937" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 100,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee", Hp = 100f,
            });
            return f;
        }

        private static (EntityWorld world, BuildingStore buildings, ResourceStore resources, BuildingSystem sys)
            NewHarness()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            var sys = new BuildingSystem(buildings, resources, BuilderFaction());
            return (world, buildings, resources, sys);
        }

        private static int SpawnWorker(EntityWorld world, FixedVec3 pos)
        {
            int id = world.Create(pos, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id] = GatherState.Idle;
            return id;
        }

        [Fact]
        public void TimerWaits_WhileTheBuilderWalksToTheSite()
        {
            // Worker at the origin, site 30u away: with no arrival, 30 ticks must move the timer NOT AT ALL —
            // pre-DW-937 it self-ticked from the order (construction "started" while the peon was still walking).
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(0, 0));
            int b = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(30, 30),
                                         Faction.Player1, resources, world);
            Assert.True(b >= 0);
            Assert.True(buildings.RequiresBuilder[b]);

            Fixed timer0 = buildings.ConstructionTimer[b];
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);

            Assert.Equal(timer0.Raw, buildings.ConstructionTimer[b].Raw); // exact: nothing accrued
        }

        [Fact]
        public void BuilderHeldAtTheSite_UntilCompletion_ThenReleasedToGather()
        {
            // Worker spawned ON the site so arrival is immediate. It must stay in the Build command the whole
            // construction (pre-DW-937 it was freed on arrival) and return to the gather loop on completion.
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(10, 10),
                                         Faction.Player1, resources, world);
            Assert.True(b >= 0);

            for (int t = 0; t < 30; t++) sys.Tick(world, Dt); // one second of building
            Assert.Equal(UnitCommand.Build, world.CommandState[worker]); // still tied up
            Assert.True(buildings.ConstructionTimer[b] < buildings.ConstructionDuration[b]); // and it IS building

            buildings.ConstructionTimer[b] = Dt; // fast-forward to the final tick
            sys.Tick(world, Dt);

            Assert.False(buildings.IsUnderConstruction(b));
            Assert.Equal(UnitCommand.Idle, world.CommandState[worker]);   // released…
            Assert.Equal(-1, world.BuildTarget[worker]);
            Assert.Equal(GatherState.Idle, world.GatherState[worker]);    // …back to the gather loop
        }

        [Fact]
        public void PullingTheBuilderAway_PausesTheSite_ReturningItResumes()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(10, 10),
                                         Faction.Player1, resources, world);
            Assert.True(b >= 0);

            sys.Tick(world, Dt); // building (present)
            Fixed afterOneTick = buildings.ConstructionTimer[b];
            Assert.True(afterOneTick < buildings.ConstructionDuration[b]);

            // The player re-tasks the builder (any non-Build order): the site must PAUSE exactly where it was.
            world.CommandState[worker] = UnitCommand.Move;
            world.MoveTarget[worker]   = V(0, 0);
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);
            Assert.Equal(afterOneTick.Raw, buildings.ConstructionTimer[b].Raw); // exact pause, not slower

            // Restoring the assignment (the worker still holds BuildTarget) resumes the same timer.
            world.CommandState[worker] = UnitCommand.Build;
            sys.Tick(world, Dt);
            Assert.True(buildings.ConstructionTimer[b] < afterOneTick);
        }

        [Fact]
        public void DirectPlacement_NoBuilder_StillSelfTicks()
        {
            // The scenario/editor/debug path: PlaceBuildingDirect (preBuilt=false) has no builder by design and
            // must keep the pre-DW-937 self-ticking construction — a non-prebuilt authored building still finishes.
            var (world, buildings, _, sys) = NewHarness();
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 10), preBuilt: false);
            Assert.True(b >= 0);
            Assert.False(buildings.RequiresBuilder[b]);

            Fixed timer0 = buildings.ConstructionTimer[b];
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);

            Assert.True(buildings.ConstructionTimer[b] < timer0,
                        "a direct-placed site has no builder to wait for and must self-tick as before DW-937");
        }

        [Fact]
        public void BuilderDeath_PausesTheSite()
        {
            // No resume order exists yet (see the DW-937 ledger follow-ups), so a dead builder leaves the site
            // paused — pinned so the behavior is a recorded decision, not an accident.
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(10, 10),
                                         Faction.Player1, resources, world);
            Assert.True(b >= 0);

            sys.Tick(world, Dt);
            Fixed atDeath = buildings.ConstructionTimer[b];
            world.Destroy(worker);

            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);
            Assert.Equal(atDeath.Raw, buildings.ConstructionTimer[b].Raw);
            Assert.True(buildings.IsUnderConstruction(b)); // parked, not completed and not destroyed
        }
    }
}
