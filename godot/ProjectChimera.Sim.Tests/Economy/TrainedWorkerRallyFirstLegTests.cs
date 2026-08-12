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
        // DW-984 — a rally on the far side of an ordinary map (map_bounds 120-128 on every shipped scenario). ~283 u
        // from the hall, i.e. entirely PAST the ~181 u range where FixedVec3.SqrDistance saturates at Fixed.MaxValue.
        private static readonly FixedVec3 LongRally = V(200, 200);

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

        // ── Boundary 2: a rallied COMBAT unit WALKS, but carries no gather-side pending flag ───────────────

        /// <summary>
        /// DW-680 (Phase B re-baseline) — REPLACES <c>TrainedCombatUnit_WithARally_GetsNeitherThePendingFlagNorThe
        /// MovingFlag</c>, which pinned the defect as if it were the contract.
        ///
        /// <para><see cref="EntityFlags.Moving"/> is what actually makes MovementSystem walk a unit, and DW-634
        /// fenced it inside its worker-only branch to keep ShiftQueueScenario's recorded golden byte-identical. The
        /// consequence was that a rally point did nothing at all for combat units: they spawned, held a Move command
        /// they could never act on, and were skipped by CombatSystem because their CommandState was Move. DW-634's
        /// own comment recorded that the wider fix "belongs in a deliberate re-baseline" — this is it.</para>
        ///
        /// <para><c>RallyMovePending</c> stays worker-only by design: its only reader is GatheringSystem, which never
        /// ticks a non-gatherer, so on a combat unit it would be write-only state nothing ever clears.</para>
        /// </summary>
        [Fact]
        public void TrainedCombatUnit_WithARally_WalksToIt_ButCarriesNoPendingFlag()
        {
            var h = NewHarness();
            int b = h.BuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.SetRallyCommand(b, Faction.Player1, RallyPos.X, RallyPos.Z));
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int u = SpawnNext(h);

            Assert.Equal(UnitCategory.Melee, h.World.CategoryOf[u]);
            Assert.Equal(GatherState.Inactive, h.World.GatherState[u]);
            Assert.False(h.World.RallyMovePending[u]);                        // worker-only, unchanged
            Assert.Equal(EntityFlags.Moving, h.World.Flags[u] & EntityFlags.Moving); // DW-680: it now actually walks
            Assert.Equal(UnitCommand.Move, h.World.CommandState[u]);
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
            Assert.Equal(0, h.World.RallyStandDownTicks[w]);   // DW-689 — the budget is disarmed with the leg
        }

        // ── DW-689: the stand-down is BOUNDED — the "superseded" valve above cannot fire in the sim layer ──

        /// <summary>
        /// DW-689 — the defect the valve above could NOT reach. Nothing in the SIMULATION layer ever takes a rallied
        /// worker out of <see cref="UnitCommand.Move"/>: CombatSystem's gatherer normalization rewrites every command
        /// EXCEPT Move, OrderQueueSystem skips an empty queue, ClearWorkerBuild fires only from Build, and the only
        /// Move→Stop writers live in <c>src/UI</c> behind a radius tighter than the arrival test. So the test above
        /// pins an escape that only a HAND-WRITTEN CommandState can produce, and a worker whose rally it cannot reach
        /// stood down on every tick FOREVER — never gathering again, a silent permanent loss of its economic function.
        /// </summary>
        [Fact]
        public void RallyPendingWorker_MakingNoProgress_IsReleasedAfterTheGraceWindow_AndAutoGathers()
        {
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            int w = TrainRalliedWorker(h);
            Assert.True(h.World.RallyMovePending[w]);

            // No MovementSystem in this harness ⇒ the worker's position never changes ⇒ it never gets closer to its
            // rally goal. That is EXACTLY the observable state of a worker hard-stopped against a blocked region, and
            // the state the whole grace window is measured in.
            for (int t = 0; t < GatheringSystem.RALLY_STANDDOWN_GRACE_TICKS - 1; t++)
            {
                h.Gathering.Tick(h.World, Dt);
                Assert.True(h.World.RallyMovePending[w]);                     // still honouring the rally…
                Assert.Equal(GatherState.Idle, h.World.GatherState[w]);       // …so still standing down
                Assert.Equal(t + 1, h.World.RallyStandDownTicks[w]);          // …and the budget is counting
            }

            // The budget is spent on this tick: the leg is released and the SAME tick re-employs the worker (no extra
            // idle tick), through the unchanged nearest-node logic. PRE-FIX this loop could run forever.
            h.Gathering.Tick(h.World, Dt);

            Assert.False(h.World.RallyMovePending[w]);
            Assert.Equal(0, h.World.RallyStandDownTicks[w]);                  // disarmed with the leg
            Assert.Equal(0L, h.World.RallyGoalBestSqr[w]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(home, h.World.GatherTarget[w]);
            Assert.Equal(1, h.Nodes.AssignedGatherers[home]);
        }

        /// <summary>
        /// The teeth for the bound: a worker that is genuinely CLOSING on its rally must never be released, however
        /// long the walk takes. A flat timer would have re-introduced DW-634's defect on a delay (a big map, a slowed
        /// worker); the budget is a NO-PROGRESS test against the best approach reached, so ground gained anywhere in
        /// the window re-arms it.
        /// </summary>
        [Fact]
        public void RallyPendingWorker_StillClosingOnItsRally_IsNeverReleased_HoweverLongTheWalk()
        {
            var h = NewHarness();
            TwoNodes(h);
            int w = TrainRalliedWorker(h);

            // Ten grace windows' worth of ticks, creeping one raw Fixed tick closer per tick — the smallest possible
            // evidence of progress, and far slower than any real mover.
            FixedVec3 pos = h.World.Position[w];
            for (int t = 0; t < GatheringSystem.RALLY_STANDDOWN_GRACE_TICKS * 10; t++)
            {
                pos = new FixedVec3(Fixed.FromRaw(pos.X.Raw + 1), pos.Y, pos.Z); // toward RallyPos (+X of the spawn)
                h.World.Position[w] = pos;
                h.Gathering.Tick(h.World, Dt);
                Assert.True(h.World.RallyMovePending[w]);
                Assert.Equal(GatherState.Idle, h.World.GatherState[w]);
            }

            Assert.Equal(1, h.World.RallyStandDownTicks[w]); // re-armed every tick — never accumulated toward the cap
        }

        /// <summary>
        /// DW-689 END-TO-END, through the REAL mover and a REAL <see cref="PathabilityGrid"/>: the ledger's own
        /// trigger. A rally point behind a blocked region hard-stops the worker at the boundary, so
        /// <c>SqrDistance(Position, CommandGoal)</c> can never fall inside the arrival radius. Pre-fix the worker sat
        /// in Idle for the whole match; now the leg is released once it provably cannot complete and the worker
        /// rejoins the ordinary nearest-node sweep.
        /// </summary>
        [Fact]
        public void RalliedWorker_WhoseRallyIsBehindAWall_EventuallyGathersInsteadOfFreezingForever()
        {
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            var movement = new MovementSystem();

            // A full N–S wall one cell thick at column 69 — world X in [10, 12) (grid identity: column c spans
            // X in [2c-128, 2c-126)). The hall is at X=0 and the rally at X=22, so the leg is cut in two.
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < PathabilityGrid.GRID_SIZE; row++) mask[row * PathabilityGrid.GRID_SIZE + 69] = true;
            h.World.SetPathabilityGrid(new PathabilityGrid(mask));

            int w = TrainRalliedWorker(h);
            Assert.True(h.World.RallyMovePending[w]);

            for (int t = 0; t < 1200; t++)
            {
                h.Gathering.Tick(h.World, Dt);
                movement.Tick(h.World, Dt);
                if (h.World.GatherState[w] != GatherState.Idle) break;
            }

            // Never arrived (the wall is between it and the rally) — and yet it went back to work.
            Assert.True(h.World.Position[w].X.Raw < Fixed.FromInt(12).Raw,
                        "fixture assumption: the worker must still be west of the wall, i.e. it never reached the rally");
            Assert.True(FixedVec3.SqrDistance(h.World.Position[w], RallyPos) > ArrivalTuning.GoalArriveRadiusSqr);
            Assert.False(h.World.RallyMovePending[w]);
            Assert.NotEqual(GatherState.Idle, h.World.GatherState[w]);   // PRE-FIX: Idle forever
            Assert.Equal(home, h.World.GatherTarget[w]);
        }

        // ── DW-984: the bound must not fire on a LONG leg, where SqrDistance saturates ─────────────────────

        /// <summary>
        /// DW-984 — DW-689's no-progress budget discarded the rally of a worker that was walking perfectly well,
        /// on any leg longer than ~181 world units.
        ///
        /// <para><b>Mechanism.</b> The budget re-armed on <c>goalSqr &lt; RallyGoalBestSqr</c>, both read through
        /// <c>FixedVec3.SqrDistance</c>, which SATURATES at <see cref="Fixed.MaxValue"/> — raw 32767.99 u², i.e.
        /// sqrt(32768) = 181.019 units. Past that every separation is the same clamped value, so <c>&lt;</c> was
        /// false on every tick, <c>++RallyStandDownTicks</c> ran unopposed, and after the 90-tick window the worker
        /// was re-targeted to whatever node was nearest its MID-LEG position: exactly the DW-634 defect DW-689 was
        /// chartered to bound without re-introducing. A worker covers ~12 units inside the window, so it could never
        /// escape the clamp in time to save itself.</para>
        ///
        /// <para><b>Not a corner case.</b> <c>map_bounds</c> is 120–128 on every shipped scenario ⇒ 240–256-unit
        /// spans, and rallying the town hall at a far mine or a new expansion is standard macro.</para>
        /// </summary>
        [Fact]
        public void RalliedWorker_WalkingALongLeg_IsNeverReleased_WhereSqrDistanceWouldSaturate()
        {
            var h = NewHarness();
            var (home, far) = TwoNodes(h);
            int b = h.BuildSys.PlaceBuildingDirectById("worker_hall", Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.SetRallyCommand(b, Faction.Player1, LongRally.X, LongRally.Z));
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int w = SpawnNext(h);
            Assert.True(w >= 0);
            Assert.True(h.World.RallyMovePending[w]);

            // FIXTURE PREMISE: the whole leg lies INSIDE the saturated band, so the clamped Fixed cannot tell any
            // two ticks of it apart. Without this the test would pass on the pre-fix code for the wrong reason.
            Assert.Equal(Fixed.MaxValue.Raw, FixedVec3.SqrDistance(h.World.Position[w], LongRally).Raw);

            // Walk it at the shipped alpha worker's real pace (4 u/s at 30 tps), straight at the rally, for one and
            // a half grace windows. MovementSystem is deliberately not in this harness: the point is that PROGRESS
            // IS BEING MADE, not how the mover makes it.
            FixedVec3 pos = h.World.Position[w];
            Fixed step = Fixed.FromInt(4) / Fixed.FromInt(SimulationLoop.TICKS_PER_SECOND);
            for (int t = 0; t < GatheringSystem.RALLY_STANDDOWN_GRACE_TICKS * 3 / 2; t++)
            {
                pos = new FixedVec3(pos.X + step, pos.Y, pos.Z + step);
                h.World.Position[w] = pos;
                h.Gathering.Tick(h.World, Dt);
            }

            // Still deep inside the saturated band — ~18 units covered of a ~283-unit leg.
            Assert.Equal(Fixed.MaxValue.Raw, FixedVec3.SqrDistance(h.World.Position[w], LongRally).Raw);

            // PRE-FIX every assertion below fails: the budget hit 90 and the rally was thrown away.
            Assert.True(h.World.RallyMovePending[w]);
            Assert.Equal(1, h.World.RallyStandDownTicks[w]);   // re-armed by real progress on every single tick
            Assert.Equal(GatherState.Idle, h.World.GatherState[w]);
            Assert.Equal(-1, h.World.GatherTarget[w]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[home]);
            Assert.Equal(0, h.Nodes.AssignedGatherers[far]);
            Assert.Equal(LongRally.X.Raw, h.World.MoveTarget[w].X.Raw);   // the player's rally SURVIVES
        }

        /// <summary>
        /// The other half of DW-984: widening the metric must not blunt DW-689's teeth. A worker that is genuinely
        /// STUCK at long range — the far-away-rally-behind-a-wall case — must still be released after the window.
        /// </summary>
        [Fact]
        public void RalliedWorker_StuckAtLongRange_IsStillReleasedAfterTheGraceWindow()
        {
            var h = NewHarness();
            var (home, _) = TwoNodes(h);
            int b = h.BuildSys.PlaceBuildingDirectById("worker_hall", Faction.Player1, HallPos, preBuilt: true);
            Assert.True(h.BuildSys.SetRallyCommand(b, Faction.Player1, LongRally.X, LongRally.Z));
            Assert.True(h.BuildSys.TrainUnit(b, h.Resources));
            int w = SpawnNext(h);
            Assert.True(w >= 0);
            Assert.Equal(Fixed.MaxValue.Raw, FixedVec3.SqrDistance(h.World.Position[w], LongRally).Raw);

            // Position never changes ⇒ zero progress, exactly as when MovementSystem hard-stops it at a blocked cell.
            for (int t = 0; t < GatheringSystem.RALLY_STANDDOWN_GRACE_TICKS; t++) h.Gathering.Tick(h.World, Dt);

            Assert.False(h.World.RallyMovePending[w]);
            Assert.Equal(0, h.World.RallyStandDownTicks[w]);
            Assert.Equal(0L, h.World.RallyGoalBestSqr[w]);
            Assert.Equal(GatherState.MovingToResource, h.World.GatherState[w]);
            Assert.Equal(home, h.World.GatherTarget[w]);
        }
    }
}
