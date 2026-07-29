#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 4.7 — per-resource collection models (GATHER/Income/Streaming), the requires_structure proximity
    /// gate, and Crystal production. Exercises <see cref="GatheringSystem"/> directly against fresh
    /// <see cref="ResourceNodeStore"/>/<see cref="ResourceStore"/>/<see cref="BuildingStore"/>/<see cref="EntityWorld"/>
    /// instances (no <c>SimulationHost</c>) — the isolated-store pattern <c>BuildingAutoAcquireTests</c>/
    /// <c>SimChecksumCoverageGuardTest</c> use, mirrored here for a system with no dedicated test file yet.
    /// </summary>
    public class GatheringSystemTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt; // one real sim tick (1/30s)

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static (EntityWorld world, ResourceNodeStore nodes, ResourceStore resources, BuildingStore buildings, GatheringSystem sys) NewHarness()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var sys       = new GatheringSystem(nodes, resources, buildings);
            return (world, nodes, resources, buildings, sys);
        }

        private static int SpawnWorker(EntityWorld world, Faction faction, FixedVec3 pos)
        {
            int id = world.Create(pos, faction, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id]   = GatherState.Idle;
            world.CarryCapacity[id] = Fixed.FromInt(20);
            return id;
        }

        // BuildingStore.Create defaults an unresolved-stats (Custom, no def) building to under-construction
        // (ConstructionTimer > 0, via the type-switch fallback) — the requires_structure gate is now (review patch)
        // correctly closed to an under-construction structure, so every "qualifying structure" fixture in this file
        // must pass all three of health/supplyBonus/constructionDuration to hit BuildingStore's resolved-stats
        // short-circuit and land already-complete (ConstructionTimer = Fixed.Zero).
        private static int CreateCompletedStructure(BuildingStore buildings, FixedVec3 pos, Faction faction, string buildingId) =>
            buildings.Create(pos, faction, BuildingType.Custom, buildingId: buildingId,
                health: Fixed.FromInt(100), supplyBonus: 0, constructionDuration: Fixed.Zero);

        // ── GATHER: byte-identical worker cycle (regression) ───────────────────────────────────────────────

        [Fact]
        public void Gather_DefaultCollectionModel_FullCycle_DepositsOnBaseArrival_Only()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(1000), maxGatherers: 1); // default Gather/Ore
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            // Idle -> MovingToResource
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]);
            Assert.Equal(1, nodes.AssignedGatherers[node]);

            // Arrive at node (teleport — MovementSystem isn't running in this isolated harness)
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Gathering, world.GatherState[w]);

            // Gather until carry-capacity full (rate is huge, so one tick fills it)
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToBase, world.GatherState[w]);
            Assert.Equal(Fixed.FromInt(20), world.CarryAmount[w]); // carry-capped, not the full canGather amount
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]); // NOT credited yet — still carrying

            // Arrive at base -> deposit
            world.Position[w] = resources.FactionBase[(int)Faction.Player1];
            sys.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(20), resources.Ore[(int)Faction.Player1]);
            Assert.Equal(Fixed.Zero, world.CarryAmount[w]);
            Assert.Equal(GatherState.Idle, world.GatherState[w]);
        }

        [Fact]
        public void Gather_FindBestNode_SkipsSaturatedNode()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1);
            int w1 = SpawnWorker(world, Faction.Player1, V(0, 0));
            int w2 = SpawnWorker(world, Faction.Player1, V(0, 1));

            sys.Tick(world, Dt); // w1 (ascending id) claims the only slot
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w1]);
            Assert.Equal(1, nodes.AssignedGatherers[node]);

            sys.Tick(world, Dt); // w2 still Idle — the node is saturated
            Assert.Equal(GatherState.Idle, world.GatherState[w2]);
        }

        // ── Streaming: credit-in-place, no carry, no base trip ─────────────────────────────────────────────

        [Fact]
        public void Streaming_CreditsInPlace_NoCarryAmount_NoMovingToBase_RegardlessOfBaseDistance()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            // Base is far away — Streaming must never route there, so distance is irrelevant to total mined.
            resources.FactionBase[(int)Faction.Player1] = V(500, 500);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1,
                collectionModel: ResourceCollectionModel.Streaming);
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt); // Idle -> MovingToResource
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // MovingToResource -> Gathering

            Fixed before = resources.Ore[(int)Faction.Player1];
            for (int i = 0; i < 5; i++)
            {
                sys.Tick(world, Dt);
                Assert.Equal(GatherState.Gathering, world.GatherState[w]); // never leaves Gathering
                Assert.Equal(Fixed.Zero, world.CarryAmount[w]);            // never carries
            }
            Fixed after = resources.Ore[(int)Faction.Player1];
            Assert.True(after > before, "Streaming node did not credit Ore over 5 gathering ticks.");

            // The credited total must equal exactly what left SupplyRemaining (the AC's distance-independence:
            // total mined does not depend on how far the base is, since Streaming never makes the trip).
            Fixed mined = Fixed.FromInt(100) - nodes.SupplyRemaining[node];
            Assert.Equal(mined, after - before);
            Assert.True(mined > Fixed.Zero);
        }

        [Fact]
        public void Streaming_NodeDepletion_ReturnsWorkerToIdle_NeverMovingToBase()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            int node = nodes.Create(V(2, 0), Fixed.FromInt(1), Fixed.FromInt(1000), maxGatherers: 1, // tiny supply, huge rate -> depletes tick 1
                collectionModel: ResourceCollectionModel.Streaming);
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // -> Gathering

            sys.Tick(world, Dt); // depletes this tick
            Assert.False(nodes.Active[node]);
            Assert.Equal(GatherState.Idle, world.GatherState[w]);
            Assert.Equal(Fixed.Zero, world.CarryAmount[w]);
        }

        // ── Income: periodic flat credit, zero workers ever, depletion ─────────────────────────────────────

        [Fact]
        public void Income_CreditsExactlyRatePerPeriod_ZeroWorkersEverAssigned_AndDepletesOnAPeriodCredit()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(20), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income,
                ownerFaction: Faction.Player1,
                incomePeriodTicks: 3);
            // An idle worker sitting right on the node must NEVER be assigned to it.
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            for (int i = 0; i < 2; i++)
            {
                sys.Tick(world, Dt);
                Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]); // no credit before the period elapses
                Assert.Equal(GatherState.Idle, world.GatherState[w]);          // never assigned
                Assert.Equal(0, nodes.AssignedGatherers[node]);
            }

            sys.Tick(world, Dt); // 3rd tick — period elapses
            Assert.Equal(Fixed.FromInt(10), resources.Ore[(int)Faction.Player1]);
            Assert.True(nodes.Active[node]);
            Assert.Equal(GatherState.Idle, world.GatherState[w]); // still never assigned

            for (int i = 0; i < 2; i++) sys.Tick(world, Dt);
            sys.Tick(world, Dt); // 6th tick — 2nd period; supply exactly exhausted on this credit
            Assert.Equal(Fixed.FromInt(20), resources.Ore[(int)Faction.Player1]);
            Assert.False(nodes.Active[node]); // depleted exactly on a period credit

            // No further credit once depleted.
            for (int i = 0; i < 6; i++) sys.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(20), resources.Ore[(int)Faction.Player1]);
        }

        // ── requires_structure gate ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void RequiresStructure_FindBestNode_ExcludesUngatedFaction_ThenEligibleOnceStructureBuilt()
        {
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 4,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Idle, world.GatherState[w]); // gate closed — no qualifying structure yet

            CreateCompletedStructure(buildings, V(2, 0), Faction.Player1, "watchtower");
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]); // gate now open
        }

        [Fact]
        public void RequiresStructure_WrongFactionOwnedStructure_GateStaysClosed()
        {
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 4,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            CreateCompletedStructure(buildings, V(2, 0), Faction.Player2, "watchtower"); // wrong faction
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Idle, world.GatherState[w]); // Player2's structure never satisfies Player1's gate
        }

        [Fact]
        public void RequiresStructure_UnderConstructionStructure_DoesNotSatisfyGate_ThenOpensOnCompletion()
        {
            // Review patch (Edge Case Hunter): a PLACED-but-not-yet-finished structure must not satisfy the gate —
            // matches the codebase-wide IsUnderConstruction precedent (TechTreeChecker/BuildingSystem: "not functional
            // yet"). This test would fail before that patch (Create's default fallback stats leave ConstructionTimer
            // > 0, so an under-construction "watchtower" would have opened the gate immediately).
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 4,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            int b = buildings.Create(V(2, 0), Faction.Player1, BuildingType.Custom, buildingId: "watchtower"); // default stats -> under construction
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Idle, world.GatherState[w]); // under construction — gate stays closed

            buildings.ConstructionTimer[b] = Fixed.Zero; // completes
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]); // gate opens the instant it completes
        }

        [Fact]
        public void RequiresStructure_IncomeCredit_WithheldThenGranted_AfterStructureBuilt()
        {
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, ownerFaction: Faction.Player1, incomePeriodTicks: 1,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));

            sys.Tick(world, Dt);
            sys.Tick(world, Dt);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]); // withheld — no structure, no error

            CreateCompletedStructure(buildings, V(1, 0), Faction.Player1, "watchtower");
            sys.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(10), resources.Ore[(int)Faction.Player1]); // eligible now
        }

        [Fact]
        public void RequiresStructure_GateClosingMidCycle_DoesNotInterruptAnAlreadyAssignedGatherWorker()
        {
            // Verification-Gap patch: requires_structure is checked only at FindBestNode assignment time for
            // GATHER (never re-checked live, unlike Streaming's per-tick check) — proves that documented-only
            // contract instead of leaving it asserted purely in comments.
            var (world, nodes, resources, buildings, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(1000), maxGatherers: 1,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            int b = CreateCompletedStructure(buildings, V(2, 0), Faction.Player1, "watchtower");
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt); // gate open at assignment time -> MovingToResource
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]);

            buildings.Alive[b] = false; // destroy the gating structure AFTER assignment

            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // -> Gathering, despite the gate now being closed
            Assert.Equal(GatherState.Gathering, world.GatherState[w]);

            sys.Tick(world, Dt); // carry fills -> MovingToBase
            Assert.Equal(GatherState.MovingToBase, world.GatherState[w]);

            world.Position[w] = resources.FactionBase[(int)Faction.Player1];
            sys.Tick(world, Dt); // deposit proceeds normally — GATHER never re-checked the gate mid-cycle
            Assert.Equal(Fixed.FromInt(20), resources.Ore[(int)Faction.Player1]);
            Assert.Equal(GatherState.Idle, world.GatherState[w]);
        }

        [Fact]
        public void RequiresStructure_StreamingGate_ClosesThenReopensMidGather_WithholdsThenResumesCredit()
        {
            // Verification-Gap patch: proves the Streaming gate's live per-tick re-check round-trips (withhold while
            // closed, resume the instant it reopens) instead of leaving it asserted purely in comments.
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1,
                collectionModel: ResourceCollectionModel.Streaming,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            int b = CreateCompletedStructure(buildings, V(2, 0), Faction.Player1, "watchtower");
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt); // -> MovingToResource (gate open)
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // -> Gathering
            sys.Tick(world, Dt); // credits while gate is open
            Fixed creditedWhileOpen = resources.Ore[(int)Faction.Player1];
            Assert.True(creditedWhileOpen > Fixed.Zero);

            buildings.Alive[b] = false; // gate closes
            for (int i = 0; i < 3; i++)
            {
                sys.Tick(world, Dt);
                Assert.Equal(GatherState.Gathering, world.GatherState[w]); // parked, not reassigned
                Assert.Equal(creditedWhileOpen, resources.Ore[(int)Faction.Player1]); // withheld — no further credit
            }

            buildings.Alive[b] = true; // gate reopens
            sys.Tick(world, Dt);
            Assert.True(resources.Ore[(int)Faction.Player1] > creditedWhileOpen); // resumes the instant it reopens
        }

        // ── Crystal production (closing the dead path) ─────────────────────────────────────────────────────

        [Fact]
        public void Gather_CrystalNode_DepositsViaAddCrystal_NotAddOre()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(1000), maxGatherers: 1,
                resourceType: ResourceKind.Crystal);
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt); // -> MovingToResource
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // -> Gathering
            sys.Tick(world, Dt); // carry fills -> MovingToBase
            world.Position[w] = resources.FactionBase[(int)Faction.Player1];
            sys.Tick(world, Dt); // deposit

            Assert.Equal(Fixed.FromInt(20), resources.Crystal[(int)Faction.Player1]);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]); // the Ore balance is untouched
        }

        [Fact]
        public void Streaming_CrystalNode_CreditsAddCrystal_InPlace()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 1,
                collectionModel: ResourceCollectionModel.Streaming, resourceType: ResourceKind.Crystal);
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt);
            sys.Tick(world, Dt);

            Assert.True(resources.Crystal[(int)Faction.Player1] > Fixed.Zero);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
        }

        [Fact]
        public void Income_CrystalNode_CreditsAddCrystal()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, resourceType: ResourceKind.Crystal,
                ownerFaction: Faction.Player1, incomePeriodTicks: 1);

            sys.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(10), resources.Crystal[(int)Faction.Player1]);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
        }

        [Fact]
        public void CrystalCredit_ThenSpendCrystal_IsAtomic_ClosingTheDeadPath()
        {
            var (world, nodes, resources, _, sys) = NewHarness();
            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, resourceType: ResourceKind.Crystal,
                ownerFaction: Faction.Player1, incomePeriodTicks: 1);

            sys.Tick(world, Dt); // Crystal[P1] = 10 — the previously-dead AddCrystal path now fires
            Assert.True(resources.CanAffordCrystal(Faction.Player1, Fixed.FromInt(10)));

            Assert.False(resources.SpendCrystal(Faction.Player1, Fixed.FromInt(11))); // atomic refuse — no partial spend
            Assert.Equal(Fixed.FromInt(10), resources.Crystal[(int)Faction.Player1]);

            Assert.True(resources.SpendCrystal(Faction.Player1, Fixed.FromInt(10)));
            Assert.Equal(Fixed.Zero, resources.Crystal[(int)Faction.Player1]);
        }

        // ── Follow-up review patches ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Gather_CrystalNode_DepositsAsCrystal_EvenWhenGatherTargetClearedMidReturn()
        {
            // Follow-up review regression (Blind Hunter F1): BuildingSystem clears world.GatherTarget (=-1) when a
            // Build command interrupts a returning worker, WITHOUT touching CarryAmount/GatherState. The deposit
            // must still credit the CARRIED kind (Crystal), not fall back to Ore. The carried kind is snapshotted
            // at gather time onto CarryResourceType (independent of GatherTarget), so routing survives the clear.
            // Before the fix, the old node<0 fallback unconditionally called AddOre — mis-crediting Crystal as Ore.
            var (world, nodes, resources, _, sys) = NewHarness();
            resources.FactionBase[(int)Faction.Player1] = V(0, 0);
            int node = nodes.Create(V(2, 0), Fixed.FromInt(100), Fixed.FromInt(1000), maxGatherers: 1,
                resourceType: ResourceKind.Crystal);
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt); // -> MovingToResource
            world.Position[w] = nodes.Position[node];
            sys.Tick(world, Dt); // -> Gathering
            sys.Tick(world, Dt); // carry fills -> MovingToBase
            Assert.Equal(GatherState.MovingToBase, world.GatherState[w]);
            Assert.Equal(ResourceKind.Crystal, world.CarryResourceType[w]); // snapshotted at gather time

            world.GatherTarget[w] = -1; // exactly what BuildingSystem.QueueWorkerBuild does on a Build command

            world.Position[w] = resources.FactionBase[(int)Faction.Player1];
            sys.Tick(world, Dt); // deposit
            Assert.Equal(Fixed.FromInt(20), resources.Crystal[(int)Faction.Player1]); // credited as Crystal, NOT Ore
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
            Assert.Equal(ResourceKind.Ore, world.CarryResourceType[w]); // marker reset for the next trip
        }

        [Fact]
        public void RequiresStructure_IncomeGate_CounterFrozenWhileClosed_NoBacklogBurstOnReopen()
        {
            // Verification-gap patch: the "counter frozen while gated" contract (TickIncomeNodes withholds credit
            // WITHOUT advancing IncomeTicksElapsed) is only observable with period>1 — a period-1 backlog collapses
            // to a single credit. Close the gate across several ticks, reopen, then assert the first credit lands a
            // FULL period of OPEN ticks later, not immediately (no backlog burst). Determinism-critical:
            // IncomeTicksElapsed is folded into SimChecksum (v13).
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(100), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, ownerFaction: Faction.Player1, incomePeriodTicks: 3,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));

            for (int i = 0; i < 5; i++) // gate closed 5 ticks — no credit AND no counter accrual
            {
                sys.Tick(world, Dt);
                Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
            }

            CreateCompletedStructure(buildings, V(1, 0), Faction.Player1, "watchtower"); // gate opens
            sys.Tick(world, Dt); // open tick 1 — counter 0->1
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
            sys.Tick(world, Dt); // open tick 2 — counter 1->2
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]);
            sys.Tick(world, Dt); // open tick 3 — period elapses, first credit lands here (NOT on open tick 1)
            Assert.Equal(Fixed.FromInt(10), resources.Ore[(int)Faction.Player1]);
        }

        [Fact]
        public void RequiresStructure_QualifyingStructureBeyondRadius_GateStaysClosed_ThenOpensWhenOneIsInRange()
        {
            // Verification-gap patch: every other gate test places the structure well within radius, so a gate that
            // ignored distance entirely would pass them all. Prove the Fixed squared-distance cull actually EXCLUDES:
            // a same-faction, same-id, COMPLETED structure outside requires_structure_radius must not open the gate.
            var (world, nodes, resources, buildings, sys) = NewHarness();
            int node = nodes.Create(V(0, 0), Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 4,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(5));
            CreateCompletedStructure(buildings, V(20, 0), Faction.Player1, "watchtower"); // 20 units away, radius 5
            int w = SpawnWorker(world, Faction.Player1, V(0, 0));

            sys.Tick(world, Dt);
            Assert.Equal(GatherState.Idle, world.GatherState[w]); // out of range — gate stays closed

            CreateCompletedStructure(buildings, V(3, 0), Faction.Player1, "watchtower"); // now one within radius 5
            sys.Tick(world, Dt);
            Assert.Equal(GatherState.MovingToResource, world.GatherState[w]); // gate opens once a structure is in range
        }

        [Fact]
        public void Income_NeutralOwner_CreditsNothing_DefensiveGuard()
        {
            // Follow-up review patch (F5): an Income node whose owner degraded to Neutral (out-of-range owner_slot,
            // only reachable when the validator is bypassed) must NOT credit faction index 0 a phantom balance.
            var (world, nodes, resources, _, sys) = NewHarness();
            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, ownerFaction: Faction.Neutral, incomePeriodTicks: 1);

            for (int i = 0; i < 5; i++) sys.Tick(world, Dt);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Neutral]); // no phantom credit to index 0
        }

        // ── Story 11.2 — MatchStats crystal counter fires through the real GatheringSystem credit seam ─────

        [Fact]
        public void Income_CrystalNode_WithWiredStats_RecordsCrystalMined_NotOreMined()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var stats     = new MatchStats();
            var sys       = new GatheringSystem(nodes, resources, buildings, stats); // stats WIRED

            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, resourceType: ResourceKind.Crystal,
                ownerFaction: Faction.Player1, incomePeriodTicks: 1);

            sys.Tick(world, Dt); // period elapses → 10 crystal credited

            Assert.Equal(Fixed.FromInt(10), resources.Crystal[(int)Faction.Player1]);
            Assert.Equal(10, stats.CrystalMined(Faction.Player1)); // the observational counter fired
            Assert.Equal(0,  stats.OreMined(Faction.Player1));     // the Crystal branch does NOT bump Ore stats
        }

        [Fact]
        public void Income_OreNode_WithWiredStats_DoesNotBumpCrystalMined()
        {
            var world     = new EntityWorld();
            var nodes     = new ResourceNodeStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var stats     = new MatchStats();
            var sys       = new GatheringSystem(nodes, resources, buildings, stats);

            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, resourceType: ResourceKind.Ore,
                ownerFaction: Faction.Player1, incomePeriodTicks: 1);

            sys.Tick(world, Dt); // 10 ore credited

            Assert.Equal(10, stats.OreMined(Faction.Player1));
            Assert.Equal(0,  stats.CrystalMined(Faction.Player1)); // Ore branch never touches the Crystal counter
        }

        [Fact]
        public void Income_NonPositivePeriod_CreditsNothing_DefensiveGuard()
        {
            // Follow-up review patch (EC2): an Income node with a non-positive period — the ResourceNodeStore.Create
            // default is 0 — must be skipped, not credit every tick. The validator forbids period<=0 for Income;
            // this is the system-level backstop for a direct/internal Create that bypasses validation.
            var (world, nodes, resources, _, sys) = NewHarness();
            nodes.Create(V(0, 0), Fixed.FromInt(50), Fixed.FromInt(10), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, ownerFaction: Faction.Player1, incomePeriodTicks: 0);

            for (int i = 0; i < 5; i++) sys.Tick(world, Dt);
            Assert.Equal(Fixed.Zero, resources.Ore[(int)Faction.Player1]); // skipped entirely — no credit-every-tick
        }
    }
}
