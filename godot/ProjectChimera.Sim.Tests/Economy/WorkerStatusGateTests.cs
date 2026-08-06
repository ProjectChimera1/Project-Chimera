#nullable enable
using ProjectChimera.Combat;            // CombatEventQueue, CombatEventType, DenialReason
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, ResourceStore, SimulationLoop
using ProjectChimera.Core.Definitions;  // FactionDefinition, BuildingDefinition
using ProjectChimera.Economy;           // GatheringSystem, BuildingSystem, ResourceNodeStore
using ProjectChimera.Effects;           // StatusFlags, Modifier, StackRule, ModifierStore/System
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-619 — the ECONOMY half of the DW-266 status pass. <see cref="EntityWorld.StatusFlagsOf"/> reached
    /// MovementSystem / CombatSystem / AbilityCastSystem but never <see cref="GatheringSystem"/> or
    /// <see cref="BuildingSystem"/>, so a stun anchored a worker standing at its node and then let it keep mining that
    /// node out at full rate, and a stunned worker could still pay for and start a whole building. These tests are the
    /// behavioural oracle for both halves:
    ///   • <see cref="GatheringSystem"/> — a Stunned/Rooted worker drains no node supply, accrues no
    ///     <see cref="EntityWorld.CarryAmount"/> and credits no Streaming income, while keeping its node reservation
    ///     and resuming the same cycle the tick the flag clears (PAUSE, not cancel).
    ///   • <see cref="BuildingSystem"/> — construction has NO per-tick worker input to gate (the
    ///     <see cref="BuildingStore.ConstructionTimer"/> ticks itself, unlinked to any builder), so the one moment a
    ///     worker's status can reach it is the ORDER: <c>QueueWorkerBuild</c> refuses atomically, spending nothing and
    ///     placing nothing.
    /// Also pins the gate as an EXACT no-op for the non-blocking flags, which is what keeps every recorded golden still.
    /// Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg including the WSL cross-platform gate.
    /// </summary>
    public class WorkerStatusGateTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static (EntityWorld world, ResourceNodeStore nodes, ResourceStore resources, GatheringSystem sys) NewHarness()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var sys       = new GatheringSystem(nodes, resources, new BuildingStore());
            return (world, nodes, resources, sys);
        }

        private static int SpawnWorker(EntityWorld world, FixedVec3 pos)
        {
            int id = world.Create(pos, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id]   = GatherState.Idle;
            world.CarryCapacity[id] = Fixed.FromInt(20);
            return id;
        }

        /// <summary>Idle → MovingToResource → Gathering. The worker is spawned ON its node, so the walk leg is one
        /// tick and neither MovementSystem nor a teleport is needed.</summary>
        private static void DriveToGathering(GatheringSystem sys, EntityWorld world, int worker)
        {
            sys.Tick(world, Dt);
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Gathering, world.GatherState[worker]);
        }

        // ── GatheringSystem: the accrual gate ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData((byte)StatusFlags.Stunned)]
        [InlineData((byte)StatusFlags.Rooted)]
        public void GatherBlockingStatus_MinesNothing_WhileAnIdenticalTwinKeepsFillingItsCarry(byte statusRaw)
        {
            var status = (StatusFlags)statusRaw;
            var (world, nodes, resources, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);

            // Two independent nodes, far apart, so each worker's own node supply is separately observable and
            // FindBestNode can only pick the one it is standing on. Rate 5 ⇒ ~0.167/tick: 30 ticks stays well under
            // the 20 carry capacity, so the held worker's twin never leaves Gathering either (like-for-like).
            int nodeHeld = nodes.Create(V(2, 0),  Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1);
            int nodeFree = nodes.Create(V(2, 60), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1);
            int held = SpawnWorker(world, nodes.Position[nodeHeld]);
            int free = SpawnWorker(world, nodes.Position[nodeFree]);

            DriveToGathering(sys, world, held);
            Assert.Equal(GatherState.Gathering, world.GatherState[free]);
            Assert.Equal(nodeHeld, world.GatherTarget[held]);
            Assert.Equal(1, nodes.AssignedGatherers[nodeHeld]);

            world.StatusFlagsOf[held] = status;
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);

            // NOTHING moved for the held worker — exact Raw equality, not "mined less".
            Assert.Equal(Fixed.Zero.Raw, world.CarryAmount[held].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, nodes.SupplyRemaining[nodeHeld].Raw);
            // The identical twin proves the scenario really does mine for a healthy worker.
            Assert.True(world.CarryAmount[free] > Fixed.Zero, "the un-statused twin must have accrued a carry");
            Assert.True(nodes.SupplyRemaining[nodeFree] < Fixed.FromInt(100));

            // PAUSE, NOT CANCEL: the node, the reservation and the state all survived the status…
            Assert.Equal(GatherState.Gathering, world.GatherState[held]);
            Assert.Equal(nodeHeld, world.GatherTarget[held]);
            Assert.Equal(1, nodes.AssignedGatherers[nodeHeld]);
            // …so clearing it resumes the very same cycle on the very next tick.
            world.StatusFlagsOf[held] = StatusFlags.None;
            sys.Tick(world, Dt);
            Assert.True(world.CarryAmount[held] > Fixed.Zero, "clearing the status must let mining resume immediately");
        }

        [Theory]
        [InlineData((byte)StatusFlags.Stunned)]
        [InlineData((byte)StatusFlags.Rooted)]
        public void GatherBlockingStatus_CreditsNoStreamingIncome(byte statusRaw)
        {
            // Streaming is the one collection model that pays the faction WITHOUT a delivery leg, so it is the only
            // way a held worker could still feed its economy every tick — the DW-266 movement anchor cannot stop it.
            var (world, nodes, resources, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1,
                                    collectionModel: ResourceCollectionModel.Streaming);
            int worker = SpawnWorker(world, nodes.Position[node]);

            DriveToGathering(sys, world, worker);
            world.StatusFlagsOf[worker] = (StatusFlags)statusRaw;

            Fixed ore0 = resources.Ore[(int)Faction.Player1];
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);

            Assert.Equal(ore0.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, nodes.SupplyRemaining[node].Raw);
            Assert.Equal(Fixed.Zero.Raw, world.CarryAmount[worker].Raw); // Streaming never carries, either way

            world.StatusFlagsOf[worker] = StatusFlags.None;
            sys.Tick(world, Dt);
            Assert.True(resources.Ore[(int)Faction.Player1] > ore0, "clearing the status must resume the in-place credit");
        }

        [Fact]
        public void NonBlockingStatuses_LeaveTheGatherTickBitIdentical()
        {
            // The gate must be an EXACT no-op for every flag outside the mask — this is what keeps every recorded
            // golden still (they all leave StatusFlagsOf at None) and stops a buff from taxing production.
            static (Fixed carry, Fixed supply, Fixed ore) Run(StatusFlags s)
            {
                var (world, nodes, resources, sys) = NewHarness();
                resources.FactionBase[(int)Faction.Player1] = V(0, 0);
                int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1);
                int worker = SpawnWorker(world, nodes.Position[node]);
                DriveToGathering(sys, world, worker);
                world.StatusFlagsOf[worker] = s;
                for (int t = 0; t < 30; t++) sys.Tick(world, Dt);
                return (world.CarryAmount[worker], nodes.SupplyRemaining[node], resources.Ore[(int)Faction.Player1]);
            }

            var baseline = Run(StatusFlags.None);
            var buffed   = Run(StatusFlags.Invulnerable | StatusFlags.Silenced | StatusFlags.Disarmed);

            Assert.Equal(baseline.carry.Raw,  buffed.carry.Raw);
            Assert.Equal(baseline.supply.Raw, buffed.supply.Raw);
            Assert.Equal(baseline.ore.Raw,    buffed.ore.Raw);
            Assert.True(baseline.carry > Fixed.Zero, "the control run must actually mine, or this proves nothing");
        }

        [Fact]
        public void AuthoredStunModifier_SuspendsMiningWhileLive_AndReleasesItOnExpiry()
        {
            // The real path an authored ability takes: ApplyModifier(status: Stunned) → ModifierStore writes
            // StatusFlagsOf → GatheringSystem honours it → the modifier expires → the worker mines again. No test
            // shortcut writes StatusFlagsOf here.
            var (world, nodes, resources, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var modSys    = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);

            int node   = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1);
            int worker = SpawnWorker(world, nodes.Position[node]);
            DriveToGathering(sys, world, worker);

            Assert.True(modifiers.Apply(worker, new Modifier(7101, 5, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero,
                                                             Fixed.Zero, StatusFlags.Stunned, null, 0),
                                        casterId: worker, casterFaction: Faction.Player1));
            Assert.Equal(StatusFlags.Stunned, world.StatusFlagsOf[worker]);

            for (int t = 0; t < 5; t++) { sys.Tick(world, Dt); modSys.Tick(world, Dt); }
            Assert.Equal(Fixed.Zero.Raw, world.CarryAmount[worker].Raw);          // held for the whole authored duration
            Assert.Equal(Fixed.FromInt(100).Raw, nodes.SupplyRemaining[node].Raw);

            // Drain any remaining duration, then confirm the store cleared the flag and production resumes.
            for (int t = 0; t < 5; t++) modSys.Tick(world, Dt);
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[worker]);
            sys.Tick(world, Dt);
            Assert.True(world.CarryAmount[worker] > Fixed.Zero, "an expired stun must release the worker back to mining");
        }

        // ── BuildingSystem: the construction ORDER gate ───────────────────────────────────────────────

        private static FactionDefinition BuildFaction()
        {
            var f = new FactionDefinition { Id = "dw619", DisplayName = "DW619" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 100,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            return f;
        }

        [Theory]
        [InlineData((byte)StatusFlags.Stunned, (int)DenialReason.Stunned)]
        [InlineData((byte)StatusFlags.Rooted,  (int)DenialReason.None)]   // no Rooted member in the denial vocabulary
        public void BuildBlockingStatus_RefusesTheConstructionAtomically_ThenBuildsOnceItClears(byte statusRaw, int expectedReason)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            var events = new CombatEventQueue();
            var sys    = new BuildingSystem(buildings, resources, BuildFaction());

            int worker = world.Create(V(0, 0), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;
            world.StatusFlagsOf[worker] = (StatusFlags)statusRaw;

            int bId = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(10, 10),
                                           Faction.Player1, resources, world, events);

            // ATOMIC refusal: nothing spent, nothing placed, no Build command written.
            Assert.Equal(-1, bId);
            Assert.Equal(Fixed.FromInt(500).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.Count);
            Assert.NotEqual(UnitCommand.Build, world.CommandState[worker]);
            // …and the player is told (the reason the guard authored, single-truth).
            Assert.Equal(1, events.Count);
            Assert.Equal(CombatEventType.OrderDenied, events.Get(0).Type);
            Assert.Equal((DenialReason)expectedReason, events.Get(0).Reason);

            // PAUSE, NOT CANCEL: with the status cleared the identical order goes through.
            world.StatusFlagsOf[worker] = StatusFlags.None;
            int okId = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(10, 10),
                                            Faction.Player1, resources, world, events);
            Assert.True(okId >= 0);
            Assert.Equal(Fixed.FromInt(400).Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(UnitCommand.Build, world.CommandState[worker]);
            Assert.True(buildings.IsUnderConstruction(okId));
        }

        [Fact]
        public void StunnedWorker_StartsNoSelfTickingConstruction_WhileAHealthyTwinsBuildRises()
        {
            // The consequence the refusal actually prevents: TickConstruction needs no builder, so a building a
            // stunned worker was allowed to START would finish entirely on its own while the worker stood held.
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            var sys = new BuildingSystem(buildings, resources, BuildFaction());

            int stunned = world.Create(V(0, 0), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[stunned]   = GatherState.Idle;
            world.StatusFlagsOf[stunned] = StatusFlags.Stunned;
            int healthy = world.Create(V(1, 0), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[healthy]   = GatherState.Idle;

            Assert.Equal(-1, sys.QueueWorkerBuild(stunned, BuildingType.Barracks, V(10, 10),
                                                  Faction.Player1, resources, world));
            int twinBuild = sys.QueueWorkerBuild(healthy, BuildingType.Barracks, V(20, 20),
                                                 Faction.Player1, resources, world);
            Assert.True(twinBuild >= 0);
            Assert.Equal(1, buildings.Count); // the stunned worker's order left no slot behind

            Fixed timer0 = buildings.ConstructionTimer[twinBuild];
            for (int t = 0; t < 30; t++) sys.Tick(world, Dt);
            Assert.True(buildings.ConstructionTimer[twinBuild] < timer0,
                        "the healthy worker's building must still self-tick — the gate is on the ORDER, not on TickConstruction");
        }
    }
}
