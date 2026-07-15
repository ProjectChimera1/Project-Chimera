#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 6.5 — the fixed, deterministic in-code scenario the pathability-block golden pins: a single Player1
    /// unit commanded straight across a painted wall of blocked cells. The <see cref="PathabilityGrid"/> is injected
    /// into the world BEFORE stepping, so <c>MovementSystem</c>'s post-integration rejection keeps the unit on the
    /// near side of the wall — the deterministic "units never path into blocked cells" guarantee, expressed through
    /// the same per-tick <see cref="SimChecksum"/> harness the other goldens use. All state is authored in
    /// <see cref="Fixed"/> (no <c>Fixed.FromFloat</c>) so the sequence is byte-identical on every run and platform.
    /// </summary>
    public static class PathabilityBlockScenario
    {
        /// <summary>Default tick count (10s at 30 tps) — enough for the unit to reach the wall and settle against it.</summary>
        public const int DefaultTicks = 120;

        /// <summary>The moving unit's entity id (created first ⇒ id 0), for the "never crosses the wall" assertion.</summary>
        public const int MoverId = 0;

        /// <summary>World X of the painted wall (cell column 64 ⇒ world X ∈ [0, 2)). The mover starts at X=-10 and is
        /// commanded to X=+10; it must never reach X ≥ 0 (the near edge of the blocked column).</summary>
        public const int WallWorldX = 0;

        /// <summary>Build a fresh, fully-wired sim with the mover + the injected blocked-wall grid.</summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            // Mover (id 0): Player1 unit at X=-10 commanded to X=+10 — straight through the wall at X≈0.
            int mover = host.World.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                                          Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            host.World.MoveTarget[mover] = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            host.World.Flags[mover]     |= EntityFlags.Moving;
            if (mover != MoverId)
                throw new InvalidOperationException(
                    $"PathabilityBlockScenario invariant broken: mover id was {mover}, expected {MoverId}.");

            host.World.SetPathabilityGrid(BuildWallGrid());
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, mover);
        }

        /// <summary>Build a grid whose entire column 64 (world X ∈ [0, 2)) is blocked — an infinite N-S wall the mover
        /// cannot cross.</summary>
        public static PathabilityGrid BuildWallGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            const int GS = PathabilityGrid.GRID_SIZE;
            // Column for world X=0 mirrors FlowField.WorldToCell: (floor(0)+128)/2 = 64.
            const int wallCol = 64;
            for (int row = 0; row < GS; row++) mask[row * GS + wallCol] = true;
            return new PathabilityGrid(mask);
        }
    }
}
