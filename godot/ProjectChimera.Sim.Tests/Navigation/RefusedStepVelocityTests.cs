#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-732 — a step rejected by the pathability guard used to leave a STALE non-zero <c>Velocity</c> on the mover.
    ///
    /// <para><b>The defect.</b> <c>MovementSystem</c> writes <c>world.Velocity[i] = velocity</c> from the DESIRED
    /// steering solution and only THEN resolves the step through <see cref="CheckedStep.Resolve"/>. A unit pressed
    /// against a wall therefore reported travelling at full speed forever while its <c>Position</c> never moved a raw
    /// tick. Velocity is not folded into <c>SimChecksum</c> and its only reader is the save serializer, so the sole
    /// consequence TODAY is a saved match recording a wall-stuck unit as moving — but it becomes a real defect the
    /// moment anything reads Velocity for presentation (walk-animation blending, a movement-based audio cue) or for
    /// AI/trigger conditions, which is why the entry was filed rather than closed as harmless.</para>
    ///
    /// <para><b>The bound.</b> Only the DID-NOT-MOVE outcome is corrected. A genuine wall SLIDE keeps its steering
    /// velocity verbatim (rewriting it as displacement/dt would need a Fixed division on the hot path and would round
    /// every flat-map velocity off its exact steering value), and a null/all-clear grid is an exact no-op — the two
    /// boundaries pinned below, so the fix cannot silently widen.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only. Nothing here is folded, so no golden moves.</para>
    /// </summary>
    public class RefusedStepVelocityTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A full N–S wall exactly one cell thick at <paramref name="col"/> (column 64 spans world X ∈ [0, 2)).</summary>
        private static PathabilityGrid ColumnWall(int col)
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        private static int Mover(EntityWorld w, FixedVec3 at, FixedVec3 target, int speed = 60)
        {
            int u = w.Create(at, Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(speed));
            w.MoveTarget[u] = target;
            w.Flags[u] |= EntityFlags.Moving;
            return u;
        }

        [Fact]
        public void AUnitPressedAgainstAWall_ReportsZeroVelocity_NotItsSeekVelocity()
        {
            var w = new EntityWorld();
            var move = new MovementSystem();
            w.SetPathabilityGrid(ColumnWall(64));                 // wall at X ∈ [0, 2)

            // Straight +X into the wall face at constant Z, so the full step, the X-slide and the (degenerate) Z-slide
            // are all refused and the helper hands back the pre-step position.
            int u = Mover(w, V(-6, 0), V(30, 0));

            FixedVec3 pressed = FixedVec3.Zero;
            bool everStuck = false;
            for (int t = 0; t < 120; t++)
            {
                FixedVec3 pre = w.Position[u];
                move.Tick(w, Dt);
                if (w.Position[u].X.Raw == pre.X.Raw && w.Position[u].Z.Raw == pre.Z.Raw) { everStuck = true; pressed = pre; break; }
            }

            Assert.True(everStuck, "fixture assumption: the mover must reach the wall and be refused a step");
            Assert.True(w.Position[u].X.Raw < Fixed.Zero.Raw, "fixture assumption: it is stopped WEST of the wall");

            // RED pre-fix: Velocity held the full +X seek solution while Position stood still.
            Assert.Equal(Fixed.Zero.Raw, w.Velocity[u].X.Raw);
            Assert.Equal(Fixed.Zero.Raw, w.Velocity[u].Z.Raw);

            // …and it STAYS honest while the unit keeps pressing: the flag/order are untouched (a wall is not a
            // cancel), so the mover re-seeks every tick and is refused every tick.
            for (int t = 0; t < 30; t++) move.Tick(w, Dt);
            Assert.Equal(pressed.X.Raw, w.Position[u].X.Raw);
            Assert.Equal(pressed.Z.Raw, w.Position[u].Z.Raw);
            Assert.NotEqual(EntityFlags.None, w.Flags[u] & EntityFlags.Moving);   // still ordered to move…
            Assert.Equal(Fixed.Zero.Raw, w.Velocity[u].X.Raw);                    // …but genuinely going nowhere
        }

        [Fact]
        public void AUnitOnClearGround_KeepsItsExactSeekVelocity_TheFlatMapNoOp()
        {
            // The boundary that keeps every legacy/flat map byte-identical: with no grid, Resolve returns the desired
            // position, which differs from the pre-step one for every step with length, so the DW-732 branch is never
            // taken. A unit on an ALL-CLEAR grid must behave the same.
            var free = new EntityWorld();
            var walled = new EntityWorld();
            walled.SetPathabilityGrid(PathabilityGrid.Empty);
            var moveA = new MovementSystem();
            var moveB = new MovementSystem();

            int a = Mover(free,   V(-6, 0), V(30, 0));
            int b = Mover(walled, V(-6, 0), V(30, 0));

            for (int t = 0; t < 20; t++) { moveA.Tick(free, Dt); moveB.Tick(walled, Dt); }

            Assert.NotEqual(Fixed.Zero.Raw, free.Velocity[a].X.Raw);
            Assert.Equal(free.Velocity[a].X.Raw, walled.Velocity[b].X.Raw);
            Assert.Equal(free.Position[a].X.Raw, walled.Position[b].X.Raw);
        }

        [Fact]
        public void AWallSlide_KeepsItsSteeringVelocity_BecauseTheUnitDidMove()
        {
            // The other boundary: the correction is DID-NOT-MOVE only. A diagonal approach whose X component the wall
            // refuses still slides along Z — a real displacement — so its velocity is left exactly as the integrator
            // computed it. Widening the fix to rewrite a slide as displacement/dt is deliberately out of scope.
            var w = new EntityWorld();
            var move = new MovementSystem();
            w.SetPathabilityGrid(ColumnWall(64));                 // wall at X ∈ [0, 2)

            int u = Mover(w, V(-6, -6), V(30, 30));                // heading +X and +Z

            bool slid = false;
            for (int t = 0; t < 200; t++)
            {
                FixedVec3 pre = w.Position[u];
                move.Tick(w, Dt);
                FixedVec3 post = w.Position[u];
                bool movedZ = post.Z.Raw != pre.Z.Raw;
                bool blockedX = post.X.Raw == pre.X.Raw;
                if (movedZ && blockedX)
                {
                    slid = true;
                    Assert.NotEqual(Fixed.Zero.Raw, w.Velocity[u].X.Raw);   // the refused axis is still in the solution
                    Assert.NotEqual(Fixed.Zero.Raw, w.Velocity[u].Z.Raw);
                    break;
                }
            }

            Assert.True(slid, "fixture assumption: the diagonal approach must produce at least one X-refused Z-slide");
        }
    }
}
