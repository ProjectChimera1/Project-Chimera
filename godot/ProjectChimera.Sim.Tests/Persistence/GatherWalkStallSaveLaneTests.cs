#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario (the trigger-less applied fixture)
using Xunit;

namespace ProjectChimera.Sim.Tests.Persistence
{
    /// <summary>
    /// DW-804 — <see cref="EntityWorld.GatherWalkStallTicks"/> was deliberately absent from the save while the
    /// node-side <c>ResourceNodeStore.AssignedGatherers</c> counter it PAIRS with was captured, restored AND folded
    /// into <c>SimChecksum</c>. The two halves describe ONE reservation, so persisting one without the other is not a
    /// dropped nicety: 0 is the value that MEANS "this worker holds a slot", so a save taken while a worker carried
    /// <see cref="GatheringSystem.SLOT_YIELDED"/> restored it as a HOLDER.
    ///
    /// <para><b>The route the original residual note did not cover.</b> If the restored worker CAN move (the blocked
    /// region was rebuilt away by a scenario re-apply, or the save is loaded onto a flat grid, in which case
    /// <c>TickWalkStall</c> returns immediately and the counter stays 0) it simply walks to the node and the whole
    /// arrival RE-CLAIM branch is skipped — which skips BOTH the <c>AssignedGatherers &gt;= MaxGatherers</c> capacity
    /// check the DW-532 branch exists to enforce AND the matching increment. The worker then gathers with NO
    /// reservation at all, and <c>TickGathering</c>'s unconditional carry-full decrement drives the folded counter one
    /// BELOW the number of workers genuinely holding slots — permanently, and cumulatively across save/loads.</para>
    ///
    /// <para>The lane is now <c>SaveGameState.EA.GatherWalkStall</c> (save format v8, the DW-581 shape:
    /// capture + restore + a fail-closed <c>Validate</c> domain check). It is UNFOLDED, so this moves no golden — the
    /// determinism-relevant half is the folded counter it stops corrupting.</para>
    ///
    /// <para>Godot-free and Tier-1: the applier fixture, real capture/restore, real framing.</para>
    /// </summary>
    public class GatherWalkStallSaveLaneTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private sealed class Applied
        {
            public SimulationHost Host = null!;
            public FactionDefinition?[] SlotDefs = null!;
            public EntityWorld World => Host.World;
        }

        /// <summary>The trigger-less applier golden scenario (4 workers, 8 nodes, 2 pre-built command centers) applied
        /// onto a fresh host — the same fixture <c>SaveLoadTests</c>/<c>EntityWorldSaveCompletenessTests</c> use.</summary>
        private static Applied BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return new Applied { Host = host, SlotDefs = slotDefs };
        }

        private const int Node = 0;    // the applier's first resource node
        private const int Holder = 0;  // the applier's first spawned unit (a Player1 worker)
        private const int Yielder = 1; // the second

        /// <summary>Stage the exact posture DW-804 describes on <paramref name="a"/>: a 1-slot-capacity node whose only
        /// slot is held by <see cref="Holder"/>, and a <see cref="Yielder"/> still EN ROUTE to the same node that has
        /// already handed its reservation back (<see cref="GatheringSystem.SLOT_YIELDED"/>). Both workers are parked
        /// ON the node so the first post-restore tick resolves the arrival with no movement system in the loop.</summary>
        private static void StageYieldedWorker(Applied a, int maxGatherers)
        {
            EntityWorld w = a.World;
            Assert.True(w.IsAlive(Holder) && w.IsAlive(Yielder), "fixture assumption broken: the applier spawned no workers");
            Assert.Equal(Faction.Player1, w.FactionOf[Holder]);
            Assert.Equal(Faction.Player1, w.FactionOf[Yielder]);

            a.Host.Nodes.MaxGatherers[Node] = maxGatherers;
            a.Host.Nodes.AssignedGatherers[Node] = 1;   // exactly one real holder

            FixedVec3 at = a.Host.Nodes.Position[Node];

            w.Position[Holder]             = at;
            w.GatherState[Holder]          = GatherState.Gathering;
            w.GatherTarget[Holder]         = Node;
            w.GatherWalkStallTicks[Holder] = 0;         // a genuine holder
            w.CarryCapacity[Holder]        = Fixed.FromInt(20);
            w.CarryAmount[Holder]          = Fixed.Zero;

            w.Position[Yielder]             = at;
            w.GatherState[Yielder]          = GatherState.MovingToResource;
            w.GatherTarget[Yielder]         = Node;
            w.GatherWalkStallTicks[Yielder] = GatheringSystem.SLOT_YIELDED; // holds NOTHING
            w.CarryCapacity[Yielder]        = Fixed.FromInt(20);
            w.CarryAmount[Yielder]          = Fixed.Zero;
        }

        /// <summary>A real in-memory save: capture the source host (Validate in between, exactly as the disk path
        /// does) and overlay it onto an independently scenario-applied destination host.</summary>
        private static Applied CaptureThenRestore(Applied from)
        {
            Applied into = BuildApplied();
            var fromTable = CanonicalEffectDescriptorTable.Build(from.Host.AbilityRegistry, from.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(from.Host, fromTable);
            state.Validate("dw-804 walk-stall lane");
            var intoTable = CanonicalEffectDescriptorTable.Build(into.Host.AbilityRegistry, into.Host.ItemRegistry);
            state.RestoreInto(into.Host, intoTable, into.SlotDefs);
            return into;
        }

        /// <summary>A GatheringSystem over the RESTORED stores, ticked directly — no movement/AI/combat noise, so the
        /// arrival branch under test is the only thing that can move the counter.</summary>
        private static GatheringSystem GatherOver(Applied a)
            => new GatheringSystem(a.Host.Nodes, a.Host.Resources, a.Host.Buildings);

        // ══════════════════════ (1) The lane itself round-trips ═══════════════════════════════════════════════

        [Fact]
        public void SavedYieldedSentinel_SurvivesCaptureAndRestore()
        {
            Applied source = BuildApplied();
            StageYieldedWorker(source, maxGatherers: 1);

            Applied dest = CaptureThenRestore(source);

            // Pre-DW-804 this came back as 0 — the value that MEANS "holds a slot".
            Assert.Equal(GatheringSystem.SLOT_YIELDED, dest.World.GatherWalkStallTicks[Yielder]);
            Assert.Equal(0, dest.World.GatherWalkStallTicks[Holder]);
            Assert.Equal(1, dest.Host.Nodes.AssignedGatherers[Node]);
        }

        [Fact]
        public void SavedYieldedSentinel_SurvivesTheFullDiskFraming()
        {
            // The in-memory path and the framed path are different code (WriteBody/ReadBody), and a lane can round-trip
            // in memory while breaking the per-lane length contract on disk — so prove the file, not just the object.
            Applied source = BuildApplied();
            StageYieldedWorker(source, maxGatherers: 1);

            var table = CanonicalEffectDescriptorTable.Build(source.Host.AbilityRegistry, source.Host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(source.Host, table);
            var header = new SaveGameHeaderData
            {
                CanonicalModelHash = 0, ContentHash = 0, Tick = source.Host.CurrentTick, MapId = "alpha_map_01",
                Slots = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
            };

            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            using var read = new MemoryStream(ms.ToArray());
            (SaveGameHeaderData _, SaveGameState back) = SaveGameFile.Read(read);
            back.Validate("dw-804 framed");

            Applied dest = BuildApplied();
            var destTable = CanonicalEffectDescriptorTable.Build(dest.Host.AbilityRegistry, dest.Host.ItemRegistry);
            back.RestoreInto(dest.Host, destTable, dest.SlotDefs);

            Assert.Equal(GatheringSystem.SLOT_YIELDED, dest.World.GatherWalkStallTicks[Yielder]);
        }

        // ══════════════════════ (2) The behaviour the lane exists for ════════════════════════════════════════

        [Fact]
        public void RestoredYielder_ArrivingAtASATURATEDNode_TakesNoSlot_AndDoesNotGatherOffTheBooks()
        {
            Applied source = BuildApplied();
            StageYieldedWorker(source, maxGatherers: 1);
            Applied dest = CaptureThenRestore(source);

            GatheringSystem gather = GatherOver(dest);
            gather.Tick(dest.World, Dt);

            // It re-seeks like any worker whose node filled while it was stuck — it does NOT become a second gatherer
            // on a 1-capacity node. Pre-DW-804 it walked straight into Gathering with the counter still reading 1.
            Assert.Equal(GatherState.Idle, dest.World.GatherState[Yielder]);
            Assert.Equal(-1, dest.World.GatherTarget[Yielder]);
            Assert.Equal(1, dest.Host.Nodes.AssignedGatherers[Node]);
            Assert.Equal(GatherState.Gathering, dest.World.GatherState[Holder]); // the real holder is untouched

            // …and the counter does not drift below the real holder count either, which is the permanent half of the
            // defect (TickGathering's carry-full decrement is unconditional, so an off-the-books gatherer eventually
            // decrements a slot it never took).
            for (int t = 0; t < 5 * SimulationLoop.TICKS_PER_SECOND; t++) gather.Tick(dest.World, Dt);
            Assert.True(dest.Host.Nodes.AssignedGatherers[Node] >= 0);
            Assert.True(dest.Host.Nodes.AssignedGatherers[Node] <= dest.Host.Nodes.MaxGatherers[Node],
                        "a restored save must never leave more reservations recorded than the node's capacity");
        }

        [Fact]
        public void RestoredYielder_ArrivingWhereThereIsRoom_RECLAIMSItsSlot()
        {
            // The other half of the sentinel's meaning, so the fix cannot be "always refuse the arrival": the restored
            // yielder holds NOTHING, so on a node with spare capacity its arrival must INCREMENT the counter.
            Applied source = BuildApplied();
            StageYieldedWorker(source, maxGatherers: 2);
            Applied dest = CaptureThenRestore(source);

            Assert.Equal(1, dest.Host.Nodes.AssignedGatherers[Node]);

            GatheringSystem gather = GatherOver(dest);
            gather.Tick(dest.World, Dt);

            Assert.Equal(GatherState.Gathering, dest.World.GatherState[Yielder]);
            Assert.Equal(2, dest.Host.Nodes.AssignedGatherers[Node]); // pre-DW-804 this stayed at 1 with two gatherers
            Assert.Equal(0, dest.World.GatherWalkStallTicks[Yielder]); // the sentinel is consumed by the arrival
        }

        // ══════════════════════ (3) The fail-closed domain check ═════════════════════════════════════════════

        private static SaveGameState CaptureValid()
        {
            Applied a = BuildApplied();
            var table = CanonicalEffectDescriptorTable.Build(a.Host.AbilityRegistry, a.Host.ItemRegistry);
            SaveGameState s = SaveGameState.CaptureFrom(a.Host, table);
            s.Validate("baseline");   // the untampered capture must be clean, or the tamper cases prove nothing
            return s;
        }

        [Fact]
        public void Validate_WalkStallBelowTheYieldedSentinel_ThrowsWithMessage()
        {
            // The dangerous corruption: TickWalkStall reads anything that is not exactly SLOT_YIELDED as a STREAK and
            // increments it, so a value below −1 climbs to −1 and the worker becomes a "yielder" that never yielded —
            // its slot is decremented by nobody and both release paths then skip it forever (a permanent capacity loss).
            SaveGameState state = CaptureValid();
            state.Ent[(int)SaveGameState.EA.GatherWalkStall][0] = GatheringSystem.SLOT_YIELDED - 1;
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("walk-stall", ex.Message);
        }

        [Fact]
        public void Validate_WalkStallAtOrAboveTheGraceWindow_ThrowsWithMessage()
        {
            // Unreachable in the sim: the increment that reaches the grace window replaces itself with the sentinel in
            // the same statement, so the window value is never observable at a tick boundary.
            SaveGameState state = CaptureValid();
            state.Ent[(int)SaveGameState.EA.GatherWalkStall][0] = GatheringSystem.WALK_STALL_GRACE_TICKS;
            var ex = Assert.Throws<InvalidDataException>(() => state.Validate("t"));
            Assert.Contains("walk-stall", ex.Message);
        }

        [Fact]
        public void Validate_BothEndsOfTheReachableDomain_Pass()
        {
            // The bound must not fail-close on a LEGITIMATE save: the sentinel and the longest streak the sim can
            // actually store are both valid (an off-by-one here would reject real saves).
            SaveGameState state = CaptureValid();
            int[] lane = state.Ent[(int)SaveGameState.EA.GatherWalkStall];
            Assert.True(lane.Length >= 2, "fixture assumption broken: the applier spawned fewer than two entities");
            lane[0] = GatheringSystem.SLOT_YIELDED;
            lane[1] = GatheringSystem.WALK_STALL_GRACE_TICKS - 1;
            state.Validate("t"); // must not throw
        }
    }
}
