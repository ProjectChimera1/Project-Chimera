#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-690 — <c>RallyMovePending</c> was the ONE input to its own gate that <see cref="SaveGameState"/> dropped, so
    /// a save/load taken mid-rally REVERTED the DW-634 fix.
    ///
    /// <para><b>The defect.</b> The save round-trips <c>Flags</c>, <c>CommandState</c>, <c>MoveTarget</c>,
    /// <c>CommandGoal</c>, <c>GatherState</c>, <c>GatherTarget</c>, <c>CarryAmount</c>, <c>CarryResType</c>,
    /// <c>CarryCapacity</c> and <c>BuildTarget</c> — but not <c>RallyMovePending</c>, which came back <c>false</c>
    /// from <c>EntityWorld.Create</c>. A worker autosaved mid-leg therefore reloaded looking in every respect like it
    /// was still walking its rally, while the gate that PROTECTS the walk was off: the first
    /// <see cref="GatheringSystem"/> tick ran <c>FindBestNode</c>/<c>AssignToNode</c> and overwrote <c>MoveTarget</c>
    /// with whatever node was nearest its MID-LEG position — the player's explicit rally silently discarded, which is
    /// the precise defect DW-634 was chartered to fix.</para>
    ///
    /// <para><b>The comment that justified the omission was factually wrong</b>, which is why this closed as a build
    /// rather than a doc fix: <c>EntityWorld</c> defended it as "the exact posture of every other field of this worker
    /// state machine (GatherState/GatherTarget/CarryAmount/GateClosedTicks are all unfolded)", conflating UNFOLDED
    /// with UNPERSISTED — three of those four ARE persisted.</para>
    ///
    /// <para>The exhaustive lane sweep in <c>EntityWorldSaveCompletenessTests</c> now proves the LANE round-trips
    /// (the field left its TransientLanes allowlist in the same commit). This file proves the BEHAVIOUR the lane
    /// exists for, end-to-end through a real capture → restore.</para>
    ///
    /// <para>Godot-free and <see cref="Fixed"/>-only. A save-format addition (v7 → v8, fail-closing older blobs); no
    /// fold changes, so no golden, <c>StartStateHash</c> or <c>CanonicalModelHash</c> moves.</para>
    /// </summary>
    public class RallyLegSaveRoundTripTests
    {
        private static readonly FixedVec3 HallPos  = V(0, 0);
        private static readonly FixedVec3 HomeNode = V(0, 8);    // nearest to the spawn — the pre-fix sweep's pick
        private static readonly FixedVec3 RallyPos = V(22, 0);
        private static readonly FixedVec3 FarNode  = V(24, 0);   // the mine BESIDE the rally — the player's intent

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A faction with a Worker unit and an authored Custom worker producer — the Story-6.8 shape that
        /// makes worker training (and therefore a rally first leg) reachable.</summary>
        private static FactionDefinition WorkerProducerFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition
            {
                Id = "worker", Category = "Worker", Hp = 50f, Speed = 30f, Supply = 1, CostOre = 50, TrainTime = 2f,
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "worker_hall", DisplayName = "Worker Hall", Category = "Structure",
                Hp = 400f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Worker",
            });
            return f;
        }

        private static SimulationHost NewHost(FactionDefinition faction)
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.Resources.Ore[(int)Faction.Player1]         = Fixed.FromInt(10000);
            host.Resources.SupplyCap[(int)Faction.Player1]   = 500;
            host.Resources.FactionBase[(int)Faction.Player1] = HallPos;
            return host;
        }

        /// <summary>Train one worker from a rallied hall on <paramref name="host"/> and return its entity id.</summary>
        private static int TrainRalliedWorker(SimulationHost host)
        {
            int b = host.BuildSys.PlaceBuildingDirectById("worker_hall", Faction.Player1, HallPos, preBuilt: true);
            Assert.True(host.BuildSys.SetRallyCommand(b, Faction.Player1, RallyPos.X, RallyPos.Z));
            Assert.True(host.BuildSys.TrainUnit(b, host.Resources));
            host.BuildSys.Tick(host.World, Fixed.FromInt(100));   // expire the train timer
            for (int i = 0; i < host.World.HighWaterMark; i++)
                if (host.World.IsAlive(i)) return i;
            return -1;
        }

        [Fact]
        public void AWorkerSavedMidRally_KeepsItsRallyLegAcrossTheLoad_AndIsNotReTargetedToTheNearestNode()
        {
            FactionDefinition faction = WorkerProducerFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            SimulationHost source = NewHost(faction);
            source.Nodes.Create(HomeNode, Fixed.FromInt(1000), Fixed.FromInt(100), maxGatherers: 4);
            source.Nodes.Create(FarNode,  Fixed.FromInt(1000), Fixed.FromInt(100), maxGatherers: 4);

            int w = TrainRalliedWorker(source);
            Assert.True(w >= 0);
            Assert.True(source.World.RallyMovePending[w]);                       // fixture: a real outstanding leg
            Assert.Equal(UnitCommand.Move, source.World.CommandState[w]);
            Assert.Equal(RallyPos.X.Raw, source.World.MoveTarget[w].X.Raw);

            // A real in-memory save: capture the source, then overlay onto an independently-constructed host — the
            // production sequence minus the disk framing.
            var table = CanonicalEffectDescriptorTable.Build(source.AbilityRegistry, source.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(source, table);
            state.Validate("dw-690 rally-leg round trip");

            SimulationHost dest = NewHost(faction);
            var destTable = CanonicalEffectDescriptorTable.Build(dest.AbilityRegistry, dest.ItemRegistry);
            state.RestoreInto(dest, destTable, slotDefs);

            // The gate itself survived (RED pre-fix: false, straight out of EntityWorld.Create).
            Assert.True(dest.World.RallyMovePending[w]);
            // …and so did every other input the restored worker looks "still walking" by.
            Assert.Equal(UnitCommand.Move, dest.World.CommandState[w]);
            Assert.Equal(RallyPos.X.Raw, dest.World.CommandGoal[w].X.Raw);
            Assert.Equal(RallyPos.X.Raw, dest.World.MoveTarget[w].X.Raw);
            Assert.NotEqual(EntityFlags.None, dest.World.Flags[w] & EntityFlags.Moving);

            // The BEHAVIOUR the lane exists for: the resumed match's idle-gather sweep must still stand down. PRE-FIX
            // this tick assigned the hall-side node and overwrote MoveTarget with it.
            dest.StepOnce();

            Assert.True(dest.World.RallyMovePending[w]);
            Assert.Equal(GatherState.Idle, dest.World.GatherState[w]);
            Assert.Equal(-1, dest.World.GatherTarget[w]);
            Assert.Equal(RallyPos.X.Raw, dest.World.MoveTarget[w].X.Raw);       // the player's rally SURVIVED the load
            Assert.Equal(0, dest.Nodes.AssignedGatherers[0]);
            Assert.Equal(0, dest.Nodes.AssignedGatherers[1]);
        }

        [Fact]
        public void TheStandDownBudget_IsDeliberatelyNotPersisted_AndReArmsFromTheRestoredPosition()
        {
            // DW-689's budget rides the GatherWalkStallTicks posture: unfolded AND unpersisted. A counter of 0 means
            // UNARMED, which is exactly what EntityWorld.Create leaves — so a resumed save simply restarts the window
            // from the worker's RESTORED position rather than from a stale mark taken elsewhere. The residual is
            // bounded at one further grace window of stand-down, and the LEG itself survives (the test above).
            FactionDefinition faction = WorkerProducerFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            SimulationHost source = NewHost(faction);
            source.Nodes.Create(HomeNode, Fixed.FromInt(1000), Fixed.FromInt(100), maxGatherers: 4);
            int w = TrainRalliedWorker(source);

            // Put the budget one tick from its cap. Written directly rather than accumulated through ticks: the
            // accumulation itself is pinned by TrainedWorkerRallyFirstLegTests, and what this test is about is
            // whether the VALUE crosses the save boundary.
            source.World.RallyStandDownTicks[w] = GatheringSystem.RALLY_STANDDOWN_GRACE_TICKS - 1;
            source.World.RallyGoalBestSqr[w]    = 777L << Fixed.FRACTIONAL_BITS; // DW-984 — raw 16.16 u², a long lane
            Assert.True(source.World.RallyMovePending[w]);

            var table = CanonicalEffectDescriptorTable.Build(source.AbilityRegistry, source.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(source, table);
            SimulationHost dest = NewHost(faction);
            state.RestoreInto(dest, CanonicalEffectDescriptorTable.Build(dest.AbilityRegistry, dest.ItemRegistry), slotDefs);

            Assert.True(dest.World.RallyMovePending[w]);                 // the leg travels…
            Assert.Equal(0, dest.World.RallyStandDownTicks[w]);          // …its budget does not (unarmed == fresh)
            Assert.Equal(0L, dest.World.RallyGoalBestSqr[w]);
        }
    }
}
