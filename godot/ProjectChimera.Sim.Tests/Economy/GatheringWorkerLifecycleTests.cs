#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// The gathering worker LIFECYCLE edges — every way a worker can stop occupying a resource node without walking
    /// the happy-path state machine to its end.
    ///
    /// <para><b>DW-207</b> (<c>AssignedGatherers</c> leaks): a node's <c>AssignedGatherers</c> counter is a real
    /// capacity reservation — <c>FindBestNode</c> skips a node whose count has reached <c>MaxGatherers</c> — but two of
    /// the four ways a worker stops occupying one never gave the slot back. (a) DEATH: the tick loop skips dead
    /// entities and nothing subscribed <see cref="EntityWorld.OnDestroy"/> for gathering, so a node where workers died
    /// permanently lost that much capacity and eventually became un-gatherable while looking perfectly full.
    /// (b) BUILD INTERRUPT: <c>QueueWorkerBuild</c> cleared <c>GatherTarget</c> and left a comment promising the count
    /// would "reconcile next gather tick" — nothing ever reconciled it. Both now route through the single
    /// <see cref="GatheringSystem.ReleaseGatherSlot"/> path, which deliberately does NOT release from
    /// <see cref="GatherState.MovingToBase"/> (already released at that transition) — the double-decrement tests below
    /// pin that mirror-image defect closed.</para>
    ///
    /// <para><b>DW-80</b> (Streaming gate parked forever): a Streaming worker at a node whose <c>requires_structure</c>
    /// gate closed PERMANENTLY sat in <see cref="GatherState.Gathering"/> producing nothing, forever. Per the recorded
    /// 2026-07-30 decision it now re-idles after <see cref="GatheringSystem.STREAMING_GATE_GRACE_TICKS"/> CONSECUTIVE
    /// closed ticks and seeks a different eligible node, while a closure SHORTER than the window still behaves exactly
    /// as Story 4.7's AC4 requires (withhold, then resume for the same worker at the same node).</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, isolated stores (the <c>GatheringSystemTests</c> pattern) — except
    /// that the harness passes the <see cref="EntityWorld"/> to <see cref="GatheringSystem"/> and the node store to
    /// <see cref="BuildingSystem"/>, which is exactly what <c>SimulationHost</c> does and what the two DW-207 seams
    /// need.</para>
    /// </summary>
    public class GatheringWorkerLifecycleTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private sealed class Harness
        {
            public EntityWorld       World     = null!;
            public ResourceNodeStore Nodes     = null!;
            public ResourceStore     Resources = null!;
            public BuildingStore     Buildings = null!;
            public GatheringSystem   Gather    = null!;
        }

        /// <summary>Isolated stores wired the way <c>SimulationHost</c> wires them: the gathering system holds the world
        /// (so its <see cref="EntityWorld.OnDestroy"/> release is live).</summary>
        private static Harness NewHarness()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            return new Harness
            {
                World = world, Nodes = nodes, Resources = resources, Buildings = buildings,
                Gather = new GatheringSystem(nodes, resources, buildings, null, world),
            };
        }

        private static int SpawnWorker(EntityWorld world, Faction faction, FixedVec3 pos)
        {
            int id = world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id]   = GatherState.Idle;
            world.CarryCapacity[id] = Fixed.FromInt(20);
            return id;
        }

        // BuildingStore.Create defaults an unresolved-stats (Custom, no def) building to UNDER CONSTRUCTION, which the
        // requires_structure gate correctly treats as absent — so a "qualifying structure" fixture must pass all three
        // resolved stats to land already-complete (the GatheringSystemTests convention).
        private static int CreateCompletedStructure(BuildingStore buildings, FixedVec3 pos, Faction faction, string buildingId) =>
            buildings.Create(pos, faction, BuildingType.Custom, buildingId: buildingId,
                health: Fixed.FromInt(100), supplyBonus: 0, constructionDuration: Fixed.Zero);

        /// <summary>Drive a worker Idle → MovingToResource → (teleport) → Gathering on the given node.</summary>
        private static void WalkToGathering(Harness h, int worker, int node)
        {
            h.Gather.Tick(h.World, Dt);                       // Idle -> MovingToResource (claims the slot)
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[worker]);
            h.World.Position[worker] = h.Nodes.Position[node]; // MovementSystem isn't running in an isolated harness
            h.Gather.Tick(h.World, Dt);                       // -> Gathering
            Assert.Equal(GatherState.Gathering, h.World.GatherState[worker]);
        }

        // ── DW-207: the slot must come back when the worker DIES ────────────────────────────────────────────

        [Fact]
        public void WorkerDeath_WhileGathering_ReleasesTheSlot_SoAnotherWorkerCanClaimTheNode()
        {
            var h = NewHarness();
            // maxGatherers: 1 makes the leak observable as a permanently un-gatherable node.
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, a, node);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.World.Destroy(a); // killed at the node (combat, an ability, an editor delete — the path is irrelevant)

            // Pre-DW-207 this stayed at 1 forever: the tick loop skips dead entities and nothing else ever decremented.
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            // …and the capacity is genuinely usable again, not merely a corrected number.
            int b = SpawnWorker(h.World, Faction.Player1, V(0, 1));
            h.Gather.Tick(h.World, Dt);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[b]);
            Assert.Equal(node, h.World.GatherTarget[b]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void WorkerDeath_WhileEnRouteToTheNode_ReleasesTheReservedSlot()
        {
            var h = NewHarness();
            int node = h.Nodes.Create(V(20, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            h.Gather.Tick(h.World, Dt); // Idle -> MovingToResource: the slot is reserved BEFORE arrival
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[a]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.World.Destroy(a); // intercepted mid-walk

            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void WorkerDeath_WhileCarryingHome_DoesNotDoubleDecrement_AndCannotStealALivingWorkersSlot()
        {
            // The mirror-image defect a naive "release on every death" fix introduces: TickGathering ALREADY gives the
            // slot back at the Gathering -> MovingToBase transition, yet it leaves GatherTarget pointing at the node,
            // so a target-only release would decrement a second time and evict a live worker from its own slot.
            var h = NewHarness();
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(1000), maxGatherers: 2); // huge rate -> carry fills in one tick
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            int b = SpawnWorker(h.World, Faction.Player1, V(0, 1));

            h.Gather.Tick(h.World, Dt); // both claim a slot (ascending id)
            Assert.Equal(2, h.Nodes.AssignedGatherers[node]);
            h.World.Position[a] = h.Nodes.Position[node];
            h.World.Position[b] = h.Nodes.Position[node];
            h.Gather.Tick(h.World, Dt); // both -> Gathering
            h.Gather.Tick(h.World, Dt); // both fill their carry -> MovingToBase, both release
            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[a]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            // Re-occupy with one worker so a spurious decrement would be VISIBLE (a 0 counter is floor-guarded).
            h.Nodes.AssignedGatherers[node] = 1;
            Assert.True(h.World.GatherTarget[a] >= 0, "fixture assumption: MovingToBase still points at the node it left");

            h.World.Destroy(a);

            Assert.Equal(1, h.Nodes.AssignedGatherers[node]); // untouched — a carrying worker held no reservation
        }

        [Fact]
        public void NonGathererDeath_TouchesNoNodeCounter()
        {
            var h = NewHarness();
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
            int worker  = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            int soldier = h.World.Create(V(5, 5), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // GatherState.Inactive
            WalkToGathering(h, worker, node);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.World.Destroy(soldier); // the OnDestroy hook fires for EVERY entity, not just workers

            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(GatherState.Gathering, h.World.GatherState[worker]);
        }

        // ── DW-207: the slot must come back when a BUILD command interrupts the worker ───────────────────────

        private static FactionDefinition BuilderFaction()
        {
            var f = new FactionDefinition { Id = "builder", DisplayName = "Builder" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 10, ConstructionTime = 10f,
                SupplyBonus = 0, ProducesCategory = "Melee", Hp = 100f,
            });
            return f;
        }

        /// <summary>A BuildingSystem wired to the node store the way <c>SimulationHost</c> wires it (DW-207).</summary>
        private static BuildingSystem NewBuildSystem(Harness h)
        {
            var sys = new BuildingSystem(h.Buildings, h.Resources, BuilderFaction());
            sys.SetResourceNodes(h.Nodes);
            return sys;
        }

        [Fact]
        public void BuildCommand_InterruptingAGatheringWorker_ReleasesTheSlot_SoAnotherWorkerCanClaimTheNode()
        {
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, a, node);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            int bId = buildSys.QueueWorkerBuild(a, BuildingType.Barracks, V(30, 30),
                                                Faction.Player1, h.Resources, h.World);
            Assert.True(bId >= 0, "fixture assumption: the build order was accepted");

            // Pre-DW-207 this stayed at 1 forever — every build order permanently burned one of the node's slots.
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
            Assert.Equal(-1, h.World.GatherTarget[a]); // unchanged contract: the target is still forgotten

            int b = SpawnWorker(h.World, Faction.Player1, V(0, 1));
            h.Gather.Tick(h.World, Dt);
            Assert.Equal(node, h.World.GatherTarget[b]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void BuildCommand_InterruptingACarryingWorker_DoesNotDoubleDecrement()
        {
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(1000), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, a, node);
            h.Gather.Tick(h.World, Dt); // carry fills -> MovingToBase (the slot is already handed back)
            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[a]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            h.Nodes.AssignedGatherers[node] = 1; // a live worker occupies the node, so a spurious decrement shows
            int bId = buildSys.QueueWorkerBuild(a, BuildingType.Barracks, V(30, 30),
                                                Faction.Player1, h.Resources, h.World);
            Assert.True(bId >= 0);

            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        // ── DW-520: a build interrupt must not throw away the delivery that was in flight ────────────────────

        /// <summary>Drive a worker all the way to a FULL carry on <paramref name="node"/> (which must have a huge rate),
        /// leaving it in <see cref="GatherState.MovingToBase"/> with its slot already handed back.</summary>
        private static void FillCarryAndHeadHome(Harness h, int worker, int node)
        {
            WalkToGathering(h, worker, node);
            h.Gather.Tick(h.World, Dt); // one huge-rate tick fills the carry -> MovingToBase
            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[worker]);
            Assert.True(h.World.CarryAmount[worker] >= h.World.CarryCapacity[worker],
                "fixture assumption: the worker is walking home with a FULL load");
        }

        /// <summary>Issue a build order, teleport the worker onto the site, and drive construction to COMPLETION so
        /// the worker is released the way it is in a live match. DW-937: arrival no longer clears the command — the
        /// builder is HELD at the site until the building finishes (TickConstruction's completion release) — so this
        /// helper fast-forwards the timer to its last tick instead of asserting an arrival release.</summary>
        private static void BuildAndArrive(Harness h, BuildingSystem buildSys, int worker, FixedVec3 site)
        {
            int bId = buildSys.QueueWorkerBuild(worker, BuildingType.Barracks, site,
                                                Faction.Player1, h.Resources, h.World);
            Assert.True(bId >= 0, "fixture assumption: the build order was accepted");
            Assert.Equal(UnitCommand.Build, h.World.CommandState[worker]);

            h.World.Position[worker] = site;      // MovementSystem isn't running in an isolated harness
            buildSys.Tick(h.World, Dt);           // arrival: the builder anchors at the site and is HELD (DW-937)
            Assert.Equal(UnitCommand.Build, h.World.CommandState[worker]);

            h.Buildings.ConstructionTimer[bId] = Dt; // fast-forward to the final construction tick
            buildSys.Tick(h.World, Dt);              // completion -> TickConstruction releases via ClearWorkerBuild
            Assert.Equal(UnitCommand.Idle, h.World.CommandState[worker]);
        }

        [Fact]
        public void BuildCompletion_WithALoadStillCarried_ResumesTheDelivery_InsteadOfReSeekingANode()
        {
            // Pre-DW-520 ClearWorkerBuild forced GatherState.Idle regardless of CarryAmount, so a worker interrupted
            // while walking a full load home was sent back OUT to a node: TickIdle re-reserves one of that node's
            // MaxGatherers slots, the worker walks the whole way, gathers NOTHING (a full carry leaves canCarry == 0)
            // and only then turns round to bank the load it had all along.
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(1000), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            FillCarryAndHeadHome(h, a, node);
            Fixed carried = h.World.CarryAmount[a];

            BuildAndArrive(h, buildSys, a, V(30, 30));

            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[a]);
            Assert.Equal(carried, h.World.CarryAmount[a]);                                       // nothing dropped
            Assert.Equal(h.Resources.FactionBase[(int)Faction.Player1], h.World.MoveTarget[a]);  // aimed at the base…
            Assert.True((h.World.Flags[a] & EntityFlags.Moving) != 0, "…and actually walking there");
        }

        [Fact]
        public void BuildCompletion_WithALoadStillCarried_BanksThatLoad_WithoutAWastedTripToANode()
        {
            // The end-to-end consequence: the interrupted delivery completes on the NEXT arrival at the base, and the
            // credited amount is the load the worker was already carrying — no node round trip in between.
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(1000), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            FillCarryAndHeadHome(h, a, node);
            Fixed carried = h.World.CarryAmount[a];

            BuildAndArrive(h, buildSys, a, V(30, 30));

            // AFTER the order: QueueWorkerBuild already debited the barracks cost, and this assertion is about the
            // DELIVERY, not about build accounting.
            Fixed oreBefore = h.Resources.Ore[(int)Faction.Player1];
            h.World.Position[a] = h.Resources.FactionBase[(int)Faction.Player1]; // walks home (movement isn't ticked here)
            h.Gather.Tick(h.World, Dt);

            Assert.Equal(oreBefore + carried, h.Resources.Ore[(int)Faction.Player1]);
            Assert.Equal(Fixed.Zero, h.World.CarryAmount[a]);
            Assert.Equal(GatherState.Idle, h.World.GatherState[a]); // deposited, now free to seek a node again
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);       // and it never re-reserved a slot to do it
        }

        [Fact]
        public void BuildCompletion_WithAPartialLoad_AlsoResumesTheDelivery()
        {
            // A worker interrupted mid-GATHER (still at the node, partly loaded) is the same case: the load rides on
            // the worker and CarryResourceType makes the deposit route correctly, so there is nothing to gain from
            // walking back out for a top-up first.
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2); // modest rate
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            WalkToGathering(h, a, node);
            h.Gather.Tick(h.World, Dt); // partial load
            Assert.True(h.World.CarryAmount[a] > Fixed.Zero && h.World.CarryAmount[a] < h.World.CarryCapacity[a],
                "fixture assumption: the worker holds a PARTIAL load");
            Fixed carried = h.World.CarryAmount[a];

            BuildAndArrive(h, buildSys, a, V(30, 30));

            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[a]);
            Assert.Equal(carried, h.World.CarryAmount[a]);
        }

        [Fact]
        public void BuildCompletion_EmptyHanded_StillReturnsToIdle_ToSeekANode()
        {
            // The unchanged contract for the common case — the fix must be scoped to a non-empty carry, or a worker
            // that built with nothing in hand would walk to the base for no reason.
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            Assert.Equal(Fixed.Zero, h.World.CarryAmount[a]);

            BuildAndArrive(h, buildSys, a, V(30, 30));

            Assert.Equal(GatherState.Idle, h.World.GatherState[a]);
        }

        [Fact]
        public void BuildCancelledByADestroyedSite_WithALoadStillCarried_AlsoResumesTheDelivery()
        {
            // ClearWorkerBuild's OTHER call site: the build target was razed (or its slot recycled) while the worker
            // was walking to it. The order is cancelled silently — but the load it is carrying is just as real.
            var h = NewHarness();
            h.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            var buildSys = NewBuildSystem(h);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(1000), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            FillCarryAndHeadHome(h, a, node);
            Fixed carried = h.World.CarryAmount[a];

            int bId = buildSys.QueueWorkerBuild(a, BuildingType.Barracks, V(30, 30),
                                                Faction.Player1, h.Resources, h.World);
            Assert.True(bId >= 0);
            h.Buildings.Alive[bId] = false; // razed mid-walk -> TryResolveRef fails -> silent cancel
            buildSys.Tick(h.World, Dt);

            Assert.Equal(UnitCommand.Idle, h.World.CommandState[a]);
            Assert.Equal(GatherState.MovingToBase, h.World.GatherState[a]);
            Assert.Equal(carried, h.World.CarryAmount[a]);
        }

        // ── DW-80: a permanently closed Streaming gate must eventually free the worker ───────────────────────

        /// <summary>A gated Streaming node plus the structure that opens it. Returns (node, structure).</summary>
        private static (int node, int structure) GatedStreamingNode(Harness h, FixedVec3 at)
        {
            int node = h.Nodes.Create(at, Fixed.FromInt(2000), Fixed.FromInt(5), maxGatherers: 1,
                collectionModel: ResourceCollectionModel.Streaming,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            int b = CreateCompletedStructure(h.Buildings, at, Faction.Player1, "watchtower");
            return (node, b);
        }

        [Fact]
        public void StreamingGate_ClosedPermanently_ReIdlesAfterTheGraceWindow_AndHandsTheSlotBack()
        {
            var h = NewHarness();
            (int node, int structure) = GatedStreamingNode(h, V(2, 0));
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, w, node);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            h.Buildings.Alive[structure] = false; // the gating structure is destroyed and never rebuilt
            Fixed creditedWhileOpen = h.Resources.Ore[(int)Faction.Player1];

            // Every tick up to (but not including) the threshold is still a pure withhold — the AC4 contract.
            for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS - 1; i++)
            {
                h.Gather.Tick(h.World, Dt);
                Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
                Assert.Equal(creditedWhileOpen, h.Resources.Ore[(int)Faction.Player1]);
                Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            }

            h.Gather.Tick(h.World, Dt); // the threshold tick — pre-DW-80 the worker parked here forever

            Assert.Equal(GatherState.Idle, h.World.GatherState[w]);
            Assert.Equal(-1, h.World.GatherTarget[w]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]); // capacity returned, not leaked
            Assert.Equal(0, h.World.GateClosedTicks[w]);
            Assert.Equal(creditedWhileOpen, h.Resources.Ore[(int)Faction.Player1]); // still zero credit from the closed gate
        }

        [Fact]
        public void StreamingGate_ClosedPermanently_RelocatesTheWorkerToADifferentEligibleNode()
        {
            // The point of the decision: "free the worker to Idle to seek ANOTHER eligible node, matching GATHER".
            var h = NewHarness();
            (int gated, int structure) = GatedStreamingNode(h, V(2, 0));
            int open = h.Nodes.Create(V(6, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1); // ungated GATHER node
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));

            // The gated node is nearer, so FindBestNode picks it first while the gate is open.
            WalkToGathering(h, w, gated);
            Assert.Equal(gated, h.World.GatherTarget[w]);

            h.Buildings.Alive[structure] = false;
            for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS; i++) h.Gather.Tick(h.World, Dt);
            Assert.Equal(GatherState.Idle, h.World.GatherState[w]);

            h.Gather.Tick(h.World, Dt); // Idle re-seeks — and the gated node is ineligible for this faction now

            Assert.Equal(open, h.World.GatherTarget[w]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[open]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[gated]);
        }

        [Fact]
        public void StreamingGate_ReopeningInsideTheGraceWindow_KeepsTheWorkerParked_AndResumesCredit()
        {
            // Story 4.7 AC4 must survive DW-80 unchanged for any closure shorter than the window: same worker, same
            // node, credit withheld then resumed — never a reassignment.
            var h = NewHarness();
            (int node, int structure) = GatedStreamingNode(h, V(2, 0));
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, w, node);
            h.Gather.Tick(h.World, Dt); // credits while the gate is open
            Fixed creditedWhileOpen = h.Resources.Ore[(int)Faction.Player1];
            Assert.True(creditedWhileOpen > Fixed.Zero);

            h.Buildings.Alive[structure] = false;
            for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS - 1; i++) h.Gather.Tick(h.World, Dt);
            Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
            Assert.Equal(creditedWhileOpen, h.Resources.Ore[(int)Faction.Player1]);

            h.Buildings.Alive[structure] = true; // reopens with one tick of grace left
            h.Gather.Tick(h.World, Dt);

            Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
            Assert.Equal(node, h.World.GatherTarget[w]);
            Assert.True(h.Resources.Ore[(int)Faction.Player1] > creditedWhileOpen);
            Assert.Equal(0, h.World.GateClosedTicks[w]); // the streak is CONSECUTIVE — a reopen resets it
        }

        [Fact]
        public void StreamingGate_IntermittentClosures_NeverEvictAProductiveWorker()
        {
            // Teeth on the reset: without it, N separate sub-window closures would accumulate and evict a worker that
            // is producing perfectly well between them.
            var h = NewHarness();
            (int node, int structure) = GatedStreamingNode(h, V(2, 0));
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, w, node);

            for (int cycle = 0; cycle < 3; cycle++)
            {
                h.Buildings.Alive[structure] = false;
                for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS - 1; i++) h.Gather.Tick(h.World, Dt);
                h.Buildings.Alive[structure] = true;
                Fixed before = h.Resources.Ore[(int)Faction.Player1];
                h.Gather.Tick(h.World, Dt); // one productive tick resets the streak
                Assert.True(h.Resources.Ore[(int)Faction.Player1] > before, $"cycle {cycle}: the reopened gate did not credit");
                Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
                Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
            }
        }

        [Fact]
        public void StreamingGate_ClosedForAnotherFactionOnly_DoesNotEvictTheFactionWhoseGateIsOpen()
        {
            // The gate is evaluated per WORKER FACTION, so the streak has to be per worker, not per node.
            var h = NewHarness();
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(2000), Fixed.FromInt(5), maxGatherers: 2,
                collectionModel: ResourceCollectionModel.Streaming,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            CreateCompletedStructure(h.Buildings, V(2, 0), Faction.Player1, "watchtower"); // P1 only
            int p1 = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            int p2 = SpawnWorker(h.World, Faction.Player2, V(0, 1));

            h.Gather.Tick(h.World, Dt);                  // only P1 can be assigned — P2's gate is shut at FindBestNode
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[p1]);
            Assert.Equal(GatherState.Idle, h.World.GatherState[p2]);
            h.World.Position[p1] = h.Nodes.Position[node];
            h.Gather.Tick(h.World, Dt);
            Assert.Equal(GatherState.Gathering, h.World.GatherState[p1]);

            Fixed before = h.Resources.Ore[(int)Faction.Player1];
            for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS * 2; i++) h.Gather.Tick(h.World, Dt);

            Assert.Equal(GatherState.Gathering, h.World.GatherState[p1]); // never evicted — ITS gate never closed
            Assert.Equal(0, h.World.GateClosedTicks[p1]);
            Assert.True(h.Resources.Ore[(int)Faction.Player1] > before);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);
        }

        [Fact]
        public void GatherNode_IsNeverLiveGated_SoTheGraceWindowCannotEvictAGatherWorker()
        {
            // requires_structure on a GATHER node is checked ONCE, at assignment. DW-80 must not leak the live re-check
            // (or the streak) onto that path — the Always/Never contract Story 4.7 pinned.
            var h = NewHarness();
            h.Resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(2000), Fixed.FromInt(1), maxGatherers: 1,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5)); // GATHER + gate
            int b = CreateCompletedStructure(h.Buildings, V(2, 0), Faction.Player1, "watchtower");
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, w, node);

            h.Buildings.Alive[b] = false; // gate closes; a GATHER worker must not care

            Fixed carriedBefore = h.World.CarryAmount[w];
            for (int i = 0; i < GatheringSystem.STREAMING_GATE_GRACE_TICKS + 5; i++) h.Gather.Tick(h.World, Dt);

            Assert.Equal(0, h.World.GateClosedTicks[w]);
            Assert.True(h.World.CarryAmount[w] > carriedBefore || h.World.GatherState[w] == GatherState.MovingToBase,
                "a GATHER worker kept extracting (or filled up and left) despite the closed gate");
            Assert.NotEqual(GatherState.Idle, h.World.GatherState[w]);
        }

        // ── Cross-cutting: the release path must survive the Edit↔Play reset ─────────────────────────────────

        [Fact]
        public void WorldClear_ResetsTheClosedGateStreak_SoARePlayStartsLikeAFreshBoot()
        {
            var h = NewHarness();
            (int node, int structure) = GatedStreamingNode(h, V(2, 0));
            int w = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, w, node);
            h.Buildings.Alive[structure] = false;
            h.Gather.Tick(h.World, Dt);
            h.Gather.Tick(h.World, Dt);
            Assert.Equal(2, h.World.GateClosedTicks[w]);

            h.World.Clear();

            Assert.Equal(0, h.World.GateClosedTicks[w]);
        }

        [Fact]
        public void ReleaseGatherSlot_IsIdempotent_SoTwoReleasePathsCannotCompound()
        {
            // Death and a build interrupt can both fire for the same worker (a worker killed while walking to a build
            // site). The second release must be a no-op, which the GatherTarget = -1 half guarantees.
            var h = NewHarness();
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 2);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, a, node);
            h.Nodes.AssignedGatherers[node] = 2; // pretend a second worker also occupies it

            GatheringSystem.ReleaseGatherSlot(h.World, h.Nodes, a);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]);

            GatheringSystem.ReleaseGatherSlot(h.World, h.Nodes, a);
            GatheringSystem.ReleaseGatherSlot(h.World, h.Nodes, a);
            Assert.Equal(1, h.Nodes.AssignedGatherers[node]); // the other worker's slot is untouched
        }

        [Fact]
        public void DepletedNode_DeathRelease_DoesNotUnderflowTheZeroedCounter()
        {
            // Depletion force-zeroes AssignedGatherers; a death release afterwards must not push it negative (which
            // would let MaxGatherers + 1 workers onto the next node the counter is reused for).
            var h = NewHarness();
            int node = h.Nodes.Create(V(2, 0), Fixed.FromInt(1), Fixed.FromInt(1000), maxGatherers: 1, // depletes on the first gather tick
                collectionModel: ResourceCollectionModel.Streaming);
            int a = SpawnWorker(h.World, Faction.Player1, V(0, 0));
            WalkToGathering(h, a, node);
            h.Gather.Tick(h.World, Dt);
            Assert.False(h.Nodes.Active[node]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);

            h.World.GatherTarget[a]  = node;             // re-point at the dead node, as the en-route branch can leave it
            h.World.GatherState[a]   = GatherState.Gathering;
            h.World.Destroy(a);

            Assert.Equal(0, h.Nodes.AssignedGatherers[node]);
        }
    }
}
