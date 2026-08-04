#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-205 — <c>BuildingSystem.SpawnTrainedUnit</c> must write the caller-owned worker gather residue
    /// (<see cref="EntityWorld.GatherState"/> / <see cref="EntityWorld.CarryCapacity"/>) that
    /// <c>ScenarioApplier.SpawnUnitAt</c> and <c>EntityPlacer.DoSpawnWorker</c> already write, so a TRAINED worker
    /// enters the world in the same state as a scenario-placed one.
    ///
    /// <para>TEETH — every assertion here fails on the pre-fix code, which ended at the rally/Stop branch leaving the
    /// residue at its <see cref="EntityWorld.Create"/> defaults (<c>Inactive</c> / capacity 0). That is a DOUBLE
    /// defect, and both halves are covered below:
    /// <list type="number">
    ///   <item><c>GatheringSystem.Tick</c> skips every <c>GatherState.Inactive</c> entity, so the trained worker can
    ///         never gather — no assignment, no carry, no deposit, forever.</item>
    ///   <item><c>CombatSystem</c>'s gatherer exemption is keyed on <c>GatherState != Inactive</c>, so the same worker
    ///         is treated as a combat unit and shoots anything that walks into its (authored, non-zero) attack
    ///         range.</item>
    /// </list>
    /// Latent while only a CommandCenter mapped to the "Worker" category — and <c>TrainUnit</c> hard-rejects a
    /// CommandCenter, so that mapping never reached the spawn path (pinned below); REACHABLE since Story 6.8 let an
    /// authored <see cref="BuildingType.Custom"/> building declare <c>produces_category: "Worker"</c>.</para>
    ///
    /// <para>Godot-free, Fixed-only. No SimChecksum input changes shape here — the fix writes two per-entity fields
    /// that are NOT folded (see DW-78, the still-open gather-state fold), on a path no golden scenario exercises (no
    /// golden trains a Worker-category unit; the AI's producer list holds only Barracks/ArcheryRange/SiegeWorkshop).
    /// </para>
    /// </summary>
    public class TrainedWorkerGatherInitTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>
        /// A faction with a Worker unit, a Melee unit (the negative control), a built-in <c>command_center</c> and an
        /// authored CUSTOM producer (<c>worker_hall</c>) that declares <c>produces_category: "Worker"</c> — the
        /// Story-6.8 shape that made DW-205 reachable. The worker is authored MELEE-RANGED (2u &lt; the 2.5u legacy
        /// delivery threshold) with <c>AttackSpeed 0</c> so the auto-combat arm below lands hitscan damage on the very
        /// first tick if the gatherer exemption is not in force.
        /// </summary>
        private static FactionDefinition WorkerProducerFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition                                        // 0
            {
                Id = "worker", Category = "Worker", Hp = 50f, Speed = 3f, Supply = 1, CostOre = 50,
                TrainTime = 2f, AttackDamage = 10f, AttackRange = 2f, AttackSpeed = 0f,
            });
            f.Units.Add(new UnitDefinition                                        // 1
            {
                Id = "melee_a", Category = "Melee", Hp = 100f, Supply = 1, CostOre = 50, TrainTime = 2f,
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "worker_hall", DisplayName = "Worker Hall", Category = "Structure",
                Hp = 400f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Worker",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                Hp = 300f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            return f;
        }

        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources, EntityWorld world)
            Harness(FactionDefinition faction)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, faction);
            return (sys, buildings, resources, world);
        }

        /// <summary>Expire the head train timer with one big-dt tick and return the spawned entity id (-1 if none).</summary>
        private static int SpawnNext(BuildingSystem sys, EntityWorld world)
        {
            sys.Tick(world, Fixed.FromInt(100));
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i)) return i;
            return -1;
        }

        // ── The residue itself: both arms that can reach a "Worker" produces_category ──────────────────────

        [Fact]
        public void TrainedWorker_FromCustomWorkerProducer_EntersGatherLoopWithCapacity()
        {
            var (sys, _, resources, world) = Harness(WorkerProducerFaction());
            // Story 6.8 path: an authored Custom building whose produces_category is "Worker".
            int b = sys.PlaceBuildingDirectById("worker_hall", Faction.Player1, V(0, 0), preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources));
            int w = SpawnNext(sys, world);

            Assert.True(w >= 0);
            Assert.Equal(UnitCategory.Worker, world.CategoryOf[w]);              // it really is the worker def
            Assert.Equal(GatherState.Idle, world.GatherState[w]);                // pre-fix: Inactive
            Assert.Equal(Fixed.FromFloat(20f).Raw, world.CarryCapacity[w].Raw);  // pre-fix: 0
        }

        [Fact]
        public void CommandCenter_CannotTrain_SoTheCustomProducerIsTheOnlyReachableWorkerPath()
        {
            // Pins WHY the fix's reachability story is what it is. CategoryForBuilding maps the built-in
            // BuildingType.CommandCenter to "Worker", but TrainUnit hard-rejects a CommandCenter outright, so that
            // mapping can never reach SpawnTrainedUnit. The ONLY live worker-training path is the Story-6.8 authored
            // Custom producer covered above — if this guard ever flips (a CommandCenter gains a train surface), the
            // residue write above is what keeps those workers gathering.
            var (sys, _, resources, world) = Harness(WorkerProducerFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, V(0, 0), preBuilt: true);

            Assert.False(sys.TrainUnit(b, resources));
            Assert.Equal(-1, SpawnNext(sys, world)); // nothing was queued, so nothing spawns
        }

        [Fact]
        public void TrainedWorker_WithRallyPoint_StillEntersGatherLoop()
        {
            // The rally branch is the one the pre-fix method ended at, so the residue write must sit BEFORE it and
            // apply to both branches — a rallied worker is still a worker.
            var (sys, _, resources, world) = Harness(WorkerProducerFaction());
            int b = sys.PlaceBuildingDirectById("worker_hall", Faction.Player1, V(0, 0), preBuilt: true);
            Assert.True(sys.SetRallyCommand(b, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(10)));

            Assert.True(sys.TrainUnit(b, resources));
            int w = SpawnNext(sys, world);

            Assert.Equal(UnitCommand.Move, world.CommandState[w]);               // rally still issued (unchanged)
            Assert.Equal(GatherState.Idle, world.GatherState[w]);                // pre-fix: Inactive
            Assert.Equal(Fixed.FromFloat(20f).Raw, world.CarryCapacity[w].Raw);
        }

        // ── Negative controls: the residue must NOT leak to non-workers ────────────────────────────────────

        [Fact]
        public void TrainedCombatUnit_StaysGatherInactive_WithNoCarryCapacity()
        {
            var (sys, _, resources, world) = Harness(WorkerProducerFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(0, 0), preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources)); // melee_a — first-of-Melee
            int u = SpawnNext(sys, world);

            Assert.Equal(UnitCategory.Melee, world.CategoryOf[u]);
            Assert.Equal(GatherState.Inactive, world.GatherState[u]); // a combat unit must never enter the gather loop
            Assert.Equal(Fixed.Zero.Raw, world.CarryCapacity[u].Raw);
        }

        [Fact]
        public void TrainedFallbackUnit_FromEmptyWorkerCategory_StaysGatherInactive()
        {
            // A "Worker" producer whose faction authors NO Worker unit still trains the graceful PRODUCTION_FALLBACK
            // unit (def == null). That fallback carries no def and no category, so it must keep the Create() defaults
            // — the residue write is def-gated, never applied blind to whatever the producer's category string says.
            var faction = WorkerProducerFaction();
            faction.Units.RemoveAt(0); // drop the only Worker unit
            var (sys, _, resources, world) = Harness(faction);
            int b = sys.PlaceBuildingDirectById("worker_hall", Faction.Player1, V(0, 0), preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources));
            int u = SpawnNext(sys, world);

            Assert.True(u >= 0);
            Assert.Equal(GatherState.Inactive, world.GatherState[u]);
            Assert.Equal(Fixed.Zero.Raw, world.CarryCapacity[u].Raw);
        }

        // ── Half 1 of the defect: the trained worker can actually gather ───────────────────────────────────

        [Fact]
        public void TrainedWorker_RunsTheFullGatherCycle_AndDepositsAtBase()
        {
            var (sys, buildings, resources, world) = Harness(WorkerProducerFaction());
            var nodes = new ResourceNodeStore();
            var gathering = new GatheringSystem(nodes, resources, buildings);
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(8, 0), Fixed.FromInt(100), Fixed.FromInt(1000), maxGatherers: 1);

            int b = sys.PlaceBuildingDirectById("worker_hall", Faction.Player1, V(0, 0), preBuilt: true);
            Assert.True(sys.TrainUnit(b, resources));
            int w = SpawnNext(sys, world);
            Fixed oreAfterTrain = resources.Ore[(int)Faction.Player1];

            // Idle → MovingToResource (pre-fix the worker is Inactive, so GatheringSystem.Tick skips it entirely and
            // every assertion from here down fails).
            gathering.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]);
            Assert.Equal(node, world.GatherTarget[w]);
            Assert.Equal(1, nodes.AssignedGatherers[node]);

            // Arrive (teleport — MovementSystem isn't in this isolated harness) → Gathering.
            world.Position[w] = nodes.Position[node];
            gathering.Tick(world, Dt);
            Assert.Equal(GatherState.Gathering, world.GatherState[w]);

            // Gather: capacity-capped at exactly the 20u a placed worker carries, then head home.
            gathering.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(20).Raw, world.CarryAmount[w].Raw);
            Assert.Equal(GatherState.MovingToBase, world.GatherState[w]);
            Assert.Equal(oreAfterTrain.Raw, resources.Ore[(int)Faction.Player1].Raw); // not credited until deposit

            // Arrive at base → deposit: the trained worker moved real income into the faction's balance.
            world.Position[w] = resources.FactionBase[(int)Faction.Player1];
            gathering.Tick(world, Dt);
            Assert.Equal((oreAfterTrain + Fixed.FromInt(20)).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.Zero.Raw, world.CarryAmount[w].Raw);
            Assert.Equal(GatherState.Idle, world.GatherState[w]);
        }

        // ── Half 2 of the defect: the trained worker is exempt from auto-combat ────────────────────────────

        [Fact]
        public void TrainedWorker_IsExemptFromAutoCombat_AndNeverShootsAnAdjacentEnemy()
        {
            var (sys, _, resources, world) = Harness(WorkerProducerFaction());
            var combat = new CombatSystem(new ProjectileStore());

            int b = sys.PlaceBuildingDirectById("worker_hall", Faction.Player1, V(0, 0), preBuilt: true);
            Assert.True(sys.TrainUnit(b, resources));
            int w = SpawnNext(sys, world);
            Assert.Equal(UnitCommand.Stop, world.CommandState[w]); // no rally → hold at the spawn point

            // An enemy parked 1u away — well inside the worker's authored 2u attack range.
            int enemy = world.Create(world.Position[w] + new FixedVec3(Fixed.One, Fixed.Zero, Fixed.Zero),
                                     Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            Fixed enemyHp0 = world.Health[enemy];

            for (int t = 0; t < 4; t++) combat.Tick(world, Dt);

            // The gatherer exemption normalizes Stop → Idle and skips the unit before any combat body runs.
            Assert.Equal(UnitCommand.Idle, world.CommandState[w]);
            Assert.Equal(EntityFlags.None, world.Flags[w] & EntityFlags.Attacking);
            Assert.Equal(-1, world.AttackTarget[w]);
            // Pre-fix (GatherState.Inactive + the authored 10 damage) this worker sat in TickStopCombat and hitscanned
            // the neighbour every tick.
            Assert.Equal(enemyHp0.Raw, world.Health[enemy].Raw);
        }
    }
}
