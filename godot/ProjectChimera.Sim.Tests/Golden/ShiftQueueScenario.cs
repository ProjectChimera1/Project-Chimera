using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.12 (AC2.4 / AC3.5) — the SHIFT-QUEUE golden scenario: the first golden whose recorded
    /// <see cref="SimChecksum"/> (v9) sequence exercises BOTH new surfaces of the story:
    ///   • a Shift-queued 3-waypoint Move chain on one stationary P1 unit (id 0), appended through the shared
    ///     <see cref="OrderApplier"/> with the queued flag at tick 1 and DRAINED by <c>OrderQueueSystem</c> as the
    ///     unit arrives at each waypoint (proves the v9 order-queue fold + the pure-sim completion signal), AND
    ///   • a <see cref="UnitCommand.SetRally"/> + <see cref="UnitCommand.Train"/> on a P1 Barracks at tick 1 — the
    ///     rally rides the wire (proves the v9 rally fold), then the trained grunt spawns (~tick 61) and walks to the
    ///     rally point (proves SpawnTrainedUnit reads the deterministically-written rally).
    ///
    /// Player2 is left EMPTY so the <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops — every hashed field is
    /// integer/<see cref="Fixed"/>, so this golden is CROSS-PLATFORM SAFE and compared on BOTH CI legs (NOT
    /// Windows-gated). The grunt is authored with Supply=0 + CostOre=0 so the Train gate passes at tick 1 regardless
    /// of the supply-cap recompute order; the queue/rally values are all integer coordinates or masked command bytes.
    /// </summary>
    public static class ShiftQueueScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        private const int QueueUnit    = 0; // the stationary P1 unit given the Shift-queued Move chain (created first → id 0)
        private const int IssueAtStep  = 0; // issue the whole schedule before the first StepOnce → reflected in tick 1

        /// <summary>Build a fresh, fully-wired sim: one P1 Move-chain unit + one P1 Barracks that will train a grunt.</summary>
        public static GoldenHarness Build()
        {
            FactionDefinition faction = BuildFaction();

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),        // P1 + P2 active (both 0 ore); P2 empty → AI no-ops
                faction,
                new FactionDefinition());
            host.ChecksumInterval = 1;         // checksum every tick

            EntityWorld w = host.World;

            // id 0 — the Shift-queue unit: stationary until its queued Moves pop. Speed 6 (=0.2/tick) so each 6-unit
            // leg drains in ~30 ticks, fully within the run. No enemies exist, so it walks its waypoints uninterrupted.
            w.Create(V(-20, 0, 8), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(6));

            // A pre-built P1 Barracks (operational immediately) that a Train order will produce a grunt from; the grunt
            // then walks to the rally set on this same building. PlaceBuildingDirect bypasses ore/construction.
            host.BuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 0, -10), preBuilt: true);

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, QueueUnit);
        }

        /// <summary>
        /// Issue the fixed schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>, so everything is
        /// reflected from tick 1). All through the SAME shared <see cref="OrderApplier"/> the live/replay/offline paths
        /// use: three queued (0x80-flagged) Moves append to id 0's ring; a SetRally + a Train act on building id 0.
        /// </summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i != IssueAtStep) return;
            EntityWorld w = host.World;

            // Shift-queued 3-waypoint Move chain on id 0 (the 0x80 high bit = append; OrderApplier masks it).
            Queued(w, QueueUnit, V(-14, 0, 8));
            Queued(w, QueueUnit, V(-8, 0, 8));
            Queued(w, QueueUnit, V(-8, 0, 14));

            // Rally the Barracks (building id 0) to a ground point, then Train the first-of-category grunt. Both name a
            // BUILDING (UnitId = 0 = the building), handled before the entity guard; buildings arg wires the exec-tick handlers.
            OrderApplier.Apply(w, new UnitOrder(0, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4)),
                Faction.Player1, buildings: host.BuildSys);
            OrderApplier.Apply(w, new UnitOrder(0, UnitCommand.Train, Fixed.FromRaw(-1), Fixed.Zero),
                Faction.Player1, buildings: host.BuildSys);
        }

        /// <summary>A grunt the Barracks can train: Melee category, Supply=0 + CostOre=0 so the Train gate always passes,
        /// TrainTime 2s (=60 ticks) so it spawns mid-run and walks to rally.</summary>
        private static FactionDefinition BuildFaction() => new FactionDefinition
        {
            Id = "sq", DisplayName = "ShiftQueue",
            Units = new List<UnitDefinition>
            {
                new UnitDefinition
                {
                    Id = "grunt", DisplayName = "Grunt", Category = "Melee",
                    Hp = 60f, Speed = 5f, AttackDamage = 8f, AttackRange = 2f, AttackSpeed = 1f,
                    CostOre = 0, TrainTime = 2f, Supply = 0,
                },
            },
        };

        /// <summary>Append a queued Move to <paramref name="id"/>'s ring via the shared applier (the 0x80 wire flag).</summary>
        private static void Queued(EntityWorld w, int id, FixedVec3 p)
        {
            var wireCmd = (UnitCommand)((byte)UnitCommand.Move | UnitOrderFlags.Queued);
            OrderApplier.Apply(w, new UnitOrder(id, wireCmd, p.X, p.Z), Faction.Player1);
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
