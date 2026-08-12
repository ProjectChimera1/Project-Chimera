#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;        // EntityWorld, Fixed, FixedVec3, Faction, EntityFlags, SimulationLoop
using ProjectChimera.Navigation;  // CheckedStep, PathabilityGrid, MovementSystem
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-805 — <c>GatheringSystem.TickWalkStall</c>'s probe is NOT the step <c>MovementSystem</c> integrates, and the
    /// two-clause "conservatism" argument that used to justify that in the method doc was BACKWARDS IN BOTH CLAUSES.
    /// The defect is a wrong LOAD-BEARING INVARIANT (the doc is what a maintainer widening the probe would reason
    /// from), so the closure is the corrected doc — and these tests are what stops the retired claims from coming back
    /// as "obviously true" a year from now. Each one falsifies a claim directly, against the real helper.
    ///
    /// <list type="number">
    ///   <item><b>"A step hard-stopped at this length is hard-stopped at every length in that direction."</b> Runs the
    ///         WRONG WAY: <c>PathabilityGrid</c>'s sweep rejects at the FIRST foreign blocked cell, so a shorter step is
    ///         a strict PREFIX of the longer one and can resolve CLEAR exactly where the full-speed step is refused.</item>
    ///   <item><b>"The arrive-slowdown / separation terms this probe omits can only make the real step SHORTER."</b>
    ///         False for separation, which is an ADDED VECTOR: it changes the integrated step's DIRECTION, so the real
    ///         step can point somewhere the probe never tested at all.</item>
    /// </list>
    ///
    /// <para>The third test is a doc-rot guard in the <c>EffectCapsDocHygieneTests</c> / <c>ModifierStoreReentrancyDocTests</c>
    /// shape: the corrected passage must keep NAMING what the probe does not model, or the next reader inherits the same
    /// false invariant with none of the evidence.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only. Nothing here changes behaviour or folds into <c>SimChecksum</c>.</para>
    /// </summary>
    public class WalkStallProbeContractTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;

        /// <summary>The shared full-height blocked BAND spanning flow columns 60..70 — world X ∈ [-8, 14).</summary>
        private static PathabilityGrid BandGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 70; col++)
                    mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        private static FixedVec3 X(float x) => new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.Zero);

        // ── (1) The prefix counterexample ────────────────────────────────────────────────────────────────────

        [Fact]
        public void AShorterStepInTheSameDirection_ResolvesCLEAR_WhereTheFullSpeedStepIsRefused()
        {
            PathabilityGrid grid = BandGrid();
            FixedVec3 from = X(-9.0f);
            FixedVec3 full = X(-7.5f); // 1.5 units of +X — crosses into the band's first column
            FixedVec3 half = X(-8.5f); // 0.5 units of +X — a strict PREFIX of the same segment, still outside it

            // Fixture assumptions, asserted through the grid's OWN api so a cell-mapping change fails HERE with a
            // readable reason instead of silently making the counterexample vacuous.
            Assert.False(grid.IsBlocked(from.X, from.Z), "the origin must be on clear ground");
            Assert.False(grid.IsBlocked(half.X, half.Z), "the short step must land on clear ground");
            Assert.True(grid.IsBlocked(full.X, full.Z),  "the full step must land inside the band");

            // The full-speed step comes back as the ORIGIN — which is exactly what TickWalkStall reads as "this leg
            // cannot advance".
            Assert.Equal(from, CheckedStep.Resolve(grid, from, full));

            // …and yet a SHORTER step in the SAME direction is accepted in full. The retired claim asserted this pair
            // could not exist.
            Assert.Equal(half, CheckedStep.Resolve(grid, from, half));
        }

        [Fact]
        public void EveryDampedFractionOfTheRefusedStep_IsAcceptedInFull()
        {
            // Claim (1) is not a knife-edge coincidence at one length. MovementSystem damps speed by dist/SLOW_RADIUS
            // inside 4.0 world units and TickWalkStall runs across the whole 1.8-to-4.0 approach band, so the step a
            // damped worker really takes is an arbitrary FRACTION of the probe's — and the grid answers the entire
            // family of prefixes differently from the full step that was refused.
            PathabilityGrid grid = BandGrid();
            FixedVec3 from = X(-9.0f);

            Assert.Equal(from, CheckedStep.Resolve(grid, from, X(-7.5f))); // the full-speed step: refused

            foreach (float shorter in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f })
            {
                FixedVec3 desired = X(-9.0f + shorter);
                Assert.False(grid.IsBlocked(desired.X, desired.Z), $"fixture: {shorter} must still land outside the band");
                Assert.Equal(desired, CheckedStep.Resolve(grid, from, desired));
            }
        }

        // ── (2) Separation is an ADDED vector, so it changes DIRECTION, not just length ───────────────────────

        [Fact]
        public void SeparationRedirectsTheIntegratedStep_SoItIsNotMerelyAShorterVersionOfTheProbe()
        {
            var world = new EntityWorld();
            var move  = new MovementSystem();

            // A walker seeking straight down +X — the probe's direction has Z exactly zero.
            int walker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.MoveTarget[walker] = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
            world.Flags[walker] |= EntityFlags.Moving;

            // One neighbour in CONTACT on the +Z side. Nothing about the seek changes; separation is summed on top.
            int neighbour = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.FromFloat(0.5f)),
                                         Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));

            Fixed contact = world.CollisionRadius[walker] + world.CollisionRadius[neighbour];
            Assert.True(FixedVec3.SqrDistance(world.Position[walker], world.Position[neighbour]) < contact * contact,
                        "fixture assumption broken: the pair is not in contact, so no separation term exists");

            move.Tick(world, SimulationLoop.FixedDt);

            // The probe's step is pure +X. The step the integrator actually took is NOT — it acquired a -Z component
            // pushing away from the neighbour. A term that can only shorten a step could never do this.
            Assert.True(world.Velocity[walker].Z < Fixed.Zero,
                        $"separation must deflect the integrated step off the seek axis (Z={world.Velocity[walker].Z});" +
                        " the retired doc claimed the omitted terms could only make the real step SHORTER");
            Assert.True(world.Velocity[walker].X > Fixed.Zero, "the seek half must still be pointing at the target");
        }

        // ── (3) Doc-rot guard: the correction must keep naming what the probe does not model ──────────────────

        [Fact]
        public void TheWalkStallDoc_StillNamesTheTermsTheProbeDoesNotModel()
        {
            string path = Path.Combine(SrcRoot(), "Economy", "GatheringSystem.cs");
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");
            string text = File.ReadAllText(path);

            foreach (string required in new[] { "DW-805", "SLOW_RADIUS", "separation", "HoldPosition" })
                Assert.True(text.Contains(required, StringComparison.Ordinal),
                    $"GatheringSystem.cs no longer mentions '{required}'. DW-805's closure is the CORRECTED doc: the " +
                    "walk-stall probe is a full-speed, separation-free step, and the passage must keep enumerating " +
                    "what it omits (the arrive damping inside SLOW_RADIUS, the added separation vector, and the " +
                    "Hold/Phased anchors that stop integration for reasons the grid never caused). If the doc was " +
                    "restructured on purpose, move this guard with it — do not delete it, or the false 'a hard stop " +
                    "at this length is a hard stop at every length' invariant simply grows back.");
        }

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Economy/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"DW-805 doc guard could not locate the shipping source tree. Resolved path: '{root}'.");
            return root;
        }
    }
}
