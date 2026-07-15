#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 6.5 — the pathability-block golden: pins the per-tick <see cref="SimChecksum"/> sequence of a unit
    /// commanded across a painted wall (<see cref="PathabilityBlockScenario"/>). Because <c>MovementSystem</c>'s
    /// deterministic blocked-cell rejection changes Position (which the checksum folds), this golden is the proof —
    /// through the pure-sim harness a Godot-free test CAN run — that blocking is deterministic across two same-seed
    /// replays. All folded state is int/Fixed.Raw ⇒ CROSS-PLATFORM SAFE (compared on both CI legs). A NEW golden;
    /// the 23 pre-existing per-tick goldens are NOT touched (blocking is captured transitively via Position, never
    /// folded into SimChecksum, so SimChecksum.AlgoVersion stays 15).
    /// </summary>
    public class PathabilityBlockGoldenTests
    {
        private const string GoldenFile = "pathability-block-scenario.golden.txt";

        private static readonly GoldenChecksumReplay.GoldenHeader Header = new(
            "pathability-block golden (Story 6.5)",
            "Pins the SimChecksum sequence for PathabilityBlockScenario.Build() (a unit stopped by a painted wall) " +
            "stepped via StepOnce at ChecksumInterval=1.",
            "set CHIMERA_GOLDEN_RECORD=1, run `dotnet test --filter FullyQualifiedName~PathabilityBlockGolden`, " +
            "then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        [Fact]
        public void ProducesIdenticalSequence_TwoRuns()
        {
            // The deterministic-across-two-same-seed-replays guarantee, in-process.
            var a = GoldenChecksumReplay.RunAndRecord(PathabilityBlockScenario.DefaultTicks, build: PathabilityBlockScenario.Build);
            var b = GoldenChecksumReplay.RunAndRecord(PathabilityBlockScenario.DefaultTicks, build: PathabilityBlockScenario.Build);
            Assert.Null(GoldenChecksumReplay.CompareSequences(a, b));
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            IReadOnlyList<GoldenChecksumReplay.Sample> actual =
                GoldenChecksumReplay.RunAndRecord(PathabilityBlockScenario.DefaultTicks, build: PathabilityBlockScenario.Build);

            if (GoldenChecksumReplay.MaybeRecord(actual, GoldenFile, Header)) return; // record mode: (re)write + skip

            IReadOnlyList<GoldenChecksumReplay.Sample> golden = GoldenChecksumReplay.LoadGolden(GoldenFile);
            GoldenChecksumReplay.Divergence? d = GoldenChecksumReplay.CompareSequences(golden, actual);
            Assert.True(d is null, d is null ? "" : GoldenChecksumReplay.DescribeDivergence(d.Value));
        }

        [Fact]
        public void Mover_NeverCrossesTheBlockedWall()
        {
            // Teeth: the golden is non-vacuous — the unit is genuinely stopped by the wall (it never reaches the
            // near edge of the blocked column at world X=0), and it does not simply sit still (it moves toward it).
            GoldenHarness h = PathabilityBlockScenario.Build();
            Fixed startX = h.World.Position[PathabilityBlockScenario.MoverId].X;
            for (int t = 0; t < PathabilityBlockScenario.DefaultTicks; t++)
            {
                h.Host.StepOnce();
                Fixed x = h.World.Position[PathabilityBlockScenario.MoverId].X;
                Assert.True(x < Fixed.FromInt(PathabilityBlockScenario.WallWorldX),
                    $"unit entered/passed the blocked wall: X={x.ToFloat()} at tick {t + 1} (wall at X=0).");
            }
            Fixed endX = h.World.Position[PathabilityBlockScenario.MoverId].X;
            Assert.True(endX > startX, "unit should have advanced toward the wall, not stayed put.");
        }
    }
}
