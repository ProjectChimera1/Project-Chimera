#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;   // MovementSystem — the sim mover the rally first leg rides
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-634 — "honor the rally point first, THEN auto-gather" (recorded decision, 2026-08-05).
    ///
    /// <para>DEFECT: with the DW-205 residue in place, a worker trained from a building carrying a rally point got
    /// <c>CommandState=Move</c> + <c>MoveTarget=</c>rally, and then <c>GatheringSystem.TickIdle</c> overwrote
    /// <c>MoveTarget</c> with the nearest eligible node on the VERY NEXT TICK — so the rally was silently discarded
    /// for workers and a player could never direct new workers to a specific mine (rallying the town hall at a far
    /// mine / a new expansion is standard macro).</para>
    ///
    /// <para>FIX (the recorded MINIMAL shape — no new gather state, no rally-to-resource targeting): the spawn flags
    /// the worker <see cref="EntityWorld.RallyMovePending"/> (and finally sets <see cref="EntityFlags.Moving"/> so the
    /// sim actually walks the leg), and the idle-gather sweep stands down until the worker reaches its rally
    /// <see cref="EntityWorld.CommandGoal"/>. The desired behaviour then falls out of the UNCHANGED nearest-node
    /// logic: standing at the rally point, the nearest node IS the one the player rallied to.</para>
    ///
    /// <para>TEETH — <see cref="TrainedWorker_WithARally_IsNotReTargetedToTheNearestNodeOnTheNextTick"/>,
    /// <see cref="TrainedWorker_OnReachingTheRally_GathersFromTheNodeBesideIt_NotTheOneBesideTheHall"/> and
    /// <see cref="TrainedWorker_WalksTheWholeRallyLeg_ThenGathersAtTheRalliedMine"/> all FAIL on the pre-fix code
    /// (which assigned the hall-side node on tick 1). The negative controls pin the two boundaries the fix must not
    /// cross: a worker with NO rally is unchanged, and a rallied COMBAT unit is left completely untouched — that path
    /// is golden-covered (ShiftQueueScenario trains a Melee grunt from a rallied Barracks), so a byte of movement
    /// there would move a recorded checksum.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only. No SimChecksum input changes shape: <c>RallyMovePending</c> is
    /// unfolded (the <c>GatherState</c>/<c>GatherTarget</c>/<c>CarryAmount</c>/<c>GateClosedTicks</c> posture) and the
    /// only behaviour that moves is a TRAINED WORKER's, a path no golden exercises (no golden trains a
    /// Worker-category unit; the AI's producer list holds only Barracks/ArcheryRange/SiegeWorkshop).</para>
    /// </summary>
    public class TrainedWorkerRallyFirstLegTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        // The training hall sits at the origin; the FIRST unit it trains spawns at +3 lateral / -3 spread (see
        // BuildingSystem.SPAWN_OFFSET / SPAWN_SPREAD), i.e. ~(3, 0, -3).
        private static readonly FixedVec3 HallPos  = V(0, 0);
        private static readonly FixedVec3 HomeNode = V(0, 8);    // the node the pre-fix sweep grabbed (nearest to the spawn)
        private static readonly FixedVec3 RallyPos = V(22, 0);   // where the player rallied
        private static readonly FixedVec3 FarNode  = V(24, 0);   // the mine BESIDE the rally point — the intended target

        /// <summary>A faction with a Worker unit plus a Melee unit, an authored Custom worker producer
        /// (<c>worker_hall</c>) and a Barracks — the Story-6.8 shape that makes worker training reachable.</summary>
        private static FactionDefinition WorkerProducerFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition
            {
                Id = "worker", Category = "Worker", Hp = 50f, Speed = 30f, Supply = 1, CostOre = 50,
                TrainTime = 2f, AttackDamage = 10f, AttackRange = 2f, AttackSpeed = 0f,
            });
            f.Units.Add(new UnitDefinition
            {
                Id = "melee_a", Category = "Melee", Hp = 100f, Speed = 30f, Supply = 1, CostOre = 50, TrainTime = 2f,
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

        private sealed class Harness
        {
            public required EntityWorld       World     { get; init; }
            public required BuildingStore     Buildings { get; init; }
            public required ResourceStore     Resources { get; init; }
            public required ResourceNodeStore Nodes     { get; init; }
            public required BuildingSystem    BuildSys  { get; init; }
            public required GatheringSystem   Gathering { get; init; }
        }

        private static Harness NewHarness()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            var nodes     = new ResourceNodeStore();
            resources.Ore[(int)Faction.Player1]         = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1]   = 500;
            resources.FactionBase[(int)Faction.Player1] = HallPos;
            return new Harness
            {
                World     = world,
                Buildings = buildings,
                Resources = resources,
                Nodes     = nodes,
                BuildSys  = new BuildingSystem(buildings, resources, WorkerProducerFaction()),
                Gathering = new GatheringSystem(nodes, resources, buildings),
            };
        }

        /// <summary>Expire the head train timer with one big-dt tick and return the spawned entity id (-1 if none).</summary>
        private static int SpawnNext(Harness h)
        {
            h.BuildSys.Tick(h.World, Fixed.FromInt(100));
            for (int i = 0; i < h.World.HighWaterMark; i++)
                if (h.World.IsAlive(i)) return i;
            return -1;
        }

        /// <summary>Two GATHER nodes: one beside the hall (the pre-fix pick) and one beside the rally point.</summary>
        private static (int home, int far) TwoNodes(Harness h)
        {
            int home = h.Nodes.Create(HomeNode, Fixed.FromInt(1000), Fixed.FromInt(100), maxGatherers: 4);
            int far  = h.Nodes.Create(FarNode,  Fixed.FromInt(1000), Fixed.FromInt(100), maxGatherers: 4);
            return (home, far);
        }

        /// <summary>Place a rallied worker_hall, train one worker and return its entity id.</summary>
        private static int TrainRalliedWorker(Harness h)
        {
            int b = h.BuildSys.PlaceBuildingDirectById("worker_hall", Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.SetRallyCommand(b, Faction.Player1, RallyPos.X, RallyPos.Z));
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int w = SpawnNext(h);
            Assert.True(w >= 0);
            return w;
        }

        // ── The defect itself ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TrainedWorker_WithARally_IsNotReTargetedToTheNearestNodeOnTheNextTick()
        {
            var h = NewHarness();
            var (home, far) = TwoNodes(h);
            int w = TrainRalliedWorker(h);

            // The spawn hands the worker a real, walkable rally leg.
            Assert.True(h.World.RallyMovePending[w]);
            Assert.Equal(UnitCommand.Move, h.World.CommandState[w]);
            Assert.Equal(RallyPos.X.Raw, h.World.MoveTarget[w].X.Raw);
            Assert.Equal(RallyPos.Z.Raw, h.World.MoveTarget[w].Z.Raw);
            Assert.Equal(RallyPos.X.Raw, h.World.CommandGoal[w].X.Raw);
            Assert.NotEqual(EntityFlags.None, h.World.Flags[w] & EntityFlags.Moving); // pre-fix: never set → the leg could not be walked

            // …and the idle-gather sweep leaves it alone. PRE-FIX every assertion below fails: TickIdle assigned the
            // hall-side node on this very tick, overwriting MoveTarget and flipping the state to MovingToResource.
            for (int t = 0; t < 5; t++) h.Gathering.Tick(h.World, Dt);

            Assert.Equal(GatherState.Idle, h.World.GatherState[w]);
            Assert.Equal(-1, h.World.GatherTarget[w]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[home]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[far]);
            Assert.Equal(RallyPos.X.Raw, h.World.MoveTarget[w].X.Raw); // the rally target SURVIVES
            Assert.Equal(RallyPos.Z.Raw, h.World.MoveTarget[w].Z.Raw);
        }

        [Fact]
        public void TrainedWorker_OnReachingTheRally_GathersFromTheNodeBesideIt_NotTheOneBesideTheHall()
        {
            var h = NewHarness();
            var (home, far) = TwoNodes(h);
            int w = TrainRalliedWorker(h);

            // Teleport to the rally point (MovementSystem is not in this isolated harness — the walk itself is covered
            // by WalksTheWholeRallyLeg below). The sweep must now release the one-shot flag and resume normally.
            h.World.Position[w] = RallyPos;
            h.Gathering.Tick(h.World, Dt);

            Assert.False(h.World.RallyMovePending[w]);                       // one-shot consumed
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(far, h.World.GatherTarget[w]);                      // the mine the player rallied to…
            Assert.Equal(1, h.Nodes.AssignedGatherers[far]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[home]);                // …NOT the hall-side one the pre-fix sweep grabbed
            Assert.Equal(FarNode.X.Raw, h.World.MoveTarget[w].X.Raw);
        }

        [Fact]
        public void TrainedWorker_WalksTheWholeRallyLeg_ThenGathersAtTheRalliedMine()
        {
            // End-to-end through the REAL sim movement, in the host's registration order (GatheringSystem [2] then
            // MovementSystem [3]): spawn → walk the rally leg → arrive → auto-gather at the rallied mine.
            var h = NewHarness();
            var (home, far) = TwoNodes(h);
            var movement = new MovementSystem();
            int w = TrainRalliedWorker(h);

            FixedVec3 spawnPos = h.World.Position[w];
            Fixed startToRally = FixedVec3.SqrDistance(spawnPos, RallyPos);

            for (int t = 0; t < 400; t++)
            {
                h.Gathering.Tick(h.World, Dt);
                movement.Tick(h.World, Dt);
                if (h.World.GatherState[w] == GatherState.Gathering) break;
            }

            // It really travelled (pre-fix it walked to the HALL-side node instead, ending up nowhere near the rally).
            Assert.True(FixedVec3.SqrDistance(h.World.Position[w], RallyPos) < startToRally);
            Assert.False(h.World.RallyMovePending[w]);
            Assert.Equal(GatherState.Gathering, h.World.GatherState[w]);
            Assert.Equal(far, h.World.GatherTarget[w]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[home]);
        }

        // ── Boundary 1: no rally ⇒ byte-for-byte the old behaviour ─────────────────────────────────────────

        [Fact]
        public void TrainedWorker_WithNoRally_StillGathersOnTheVeryFirstTick()
        {
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            int b = h.BuildSys.PlaceBuildingDirectById("worker_hall", Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int w = SpawnNext(h);

            Assert.False(h.World.RallyMovePending[w]);                       // nothing to honour
            Assert.Equal(UnitCommand.Stop, h.World.CommandState[w]);

            h.Gathering.Tick(h.World, Dt);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(home, h.World.GatherTarget[w]);                     // nearest node, exactly as before
        }

        [Fact]
        public void ScenarioPlacedWorker_IsNeverGatedByTheRallyLeg()
        {
            // A hand-placed worker (the ScenarioApplier / EntityPlacer shape: GatherState written directly, no
            // production path involved) must be untouched by the fix — it carries no pending rally.
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            int w = h.World.Create(V(3, -3), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(30));
            h.World.GatherState[w]   = GatherState.Idle;
            h.World.CarryCapacity[w] = Fixed.FromInt(20);

            Assert.False(h.World.RallyMovePending[w]);
            h.Gathering.Tick(h.World, Dt);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(home, h.World.GatherTarget[w]);
        }

        // ── Boundary 2: a rallied COMBAT unit is untouched (the golden-safety edge) ────────────────────────

        [Fact]
        public void TrainedCombatUnit_WithARally_GetsNeitherThePendingFlagNorTheMovingFlag()
        {
            // ShiftQueueScenario's recorded golden trains a Melee grunt from a rallied Barracks. The fix is
            // WORKER-ONLY precisely so that path stays byte-identical: no RallyMovePending (GatheringSystem never
            // ticks a non-gatherer, so nothing would ever clear it) and no Moving flag (which would make
            // MovementSystem walk the grunt and move the recorded checksum — a deliberate re-baseline, not this fix).
            var h = NewHarness();
            int b = h.BuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.SetRallyCommand(b, Faction.Player1, RallyPos.X, RallyPos.Z));
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int u = SpawnNext(h);

            Assert.Equal(UnitCategory.Melee, h.World.CategoryOf[u]);
            Assert.Equal(GatherState.Inactive, h.World.GatherState[u]);
            Assert.False(h.World.RallyMovePending[u]);
            Assert.Equal(EntityFlags.None, h.World.Flags[u] & EntityFlags.Moving);
            Assert.Equal(UnitCommand.Move, h.World.CommandState[u]);          // the rally command itself is unchanged
            Assert.Equal(RallyPos.X.Raw, h.World.MoveTarget[u].X.Raw);
        }

        // ── The no-permanent-freeze valve ─────────────────────────────────────────────────────────────────

        [Fact]
        public void RallyPendingWorker_WhoseMoveIsSuperseded_ResumesAutoGatherWithoutReachingTheRally()
        {
            // A pending rally must never park a worker in Idle forever. Any command state other than the rally's own
            // Move (CombatSystem's gatherer normalization, BuildingSystem.ClearWorkerBuild's Idle after a build, a
            // fresh player order) ends the leg — even far from the rally point.
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            int w = TrainRalliedWorker(h);
            Assert.True(h.World.RallyMovePending[w]);

            h.World.CommandState[w] = UnitCommand.Idle;    // e.g. ClearWorkerBuild after a construction order
            h.Gathering.Tick(h.World, Dt);

            Assert.False(h.World.RallyMovePending[w]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(home, h.World.GatherTarget[w]);
        }
    }
}
