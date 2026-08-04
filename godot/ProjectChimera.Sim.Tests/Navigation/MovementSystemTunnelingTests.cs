#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-147 (movement half) — a FAST unit may not tunnel a thin wall.
    ///
    /// <para>Pre-fix <c>MovementSystem</c>'s blocked-cell rejection sampled only the pre-step and post-step CELLS, so a
    /// unit whose per-tick displacement reached the 2-world-unit cell size (move speed ≳ 60 u/s at 30 tps) stepped
    /// clean OVER a one-cell-thick wall: both endpoints clear, the wall never sampled. Post-fix the whole segment is
    /// swept and the step is rejected at the first foreign blocked cell it enters.</para>
    ///
    /// <para>The slow-mover arm (every shipping speed, well under one cell per tick) is unchanged — the last test here
    /// pins that a fast and a slow unit are stopped by the SAME wall edge, and <c>MovementSystemBlockingTests</c> plus
    /// the pathability-block golden pin the pre-existing behaviour byte-for-byte.</para>
    /// </summary>
    public class MovementSystemTunnelingTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private const int GS = PathabilityGrid.GRID_SIZE;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A full N-S wall exactly ONE cell thick at column 64 (world X ∈ [0, 2)).</summary>
        private static PathabilityGrid ThinWall()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>Spawn a mover at <paramref name="fromX"/> heading to <paramref name="toX"/> at
        /// <paramref name="speed"/> world-units/s. 150 u/s = 5 units/tick = 2.5 cells — a clean tunnelling step.</summary>
        private static int NewMover(EntityWorld w, int fromX, int toX, int speed)
        {
            int u = w.Create(V(fromX, 0), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(speed));
            w.MoveTarget[u] = V(toX, 0);
            w.Flags[u]     |= EntityFlags.Moving;
            return u;
        }

        [Fact]
        public void FastUnit_CannotTunnelAOneCellWall_InASingleTick()
        {
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = ThinWall();
            w.SetPathabilityGrid(wall);
            // X=-1 is in the clear column 63; one 5-unit tick would land at X=+4 (clear column 66), straight over the
            // wall at column 64. Pre-fix that step was accepted because both endpoints are clear.
            int u = NewMover(w, -1, 40, 150);

            for (int t = 0; t < 300; t++)
            {
                move.Tick(w, Dt);
                FixedVec3 p = w.Position[u];
                Assert.False(wall.IsBlocked(p.X, p.Z),
                    $"the fast unit occupied a blocked cell at tick {t + 1} (X={p.X.ToFloat()}).");
                Assert.True(p.X < Fixed.Zero,
                    $"the fast unit TUNNELLED the one-cell wall at tick {t + 1} (X={p.X.ToFloat()}).");
            }
        }

        [Fact]
        public void FastUnit_ApproachingFromFar_StopsWestOfTheWall_NeverBeyondIt()
        {
            // Starting far away the unit closes at 5 units/tick and must come to rest on the near side, never past it.
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = ThinWall();
            w.SetPathabilityGrid(wall);
            int u = NewMover(w, -40, 40, 150);

            for (int t = 0; t < 400; t++)
            {
                move.Tick(w, Dt);
                Assert.True(w.Position[u].X < Fixed.Zero,
                    $"the fast unit crossed the wall's near edge at tick {t + 1} (X={w.Position[u].X.ToFloat()}).");
            }
            Assert.True(w.Position[u].X > Fixed.FromInt(-40), "the fast unit never advanced at all.");
        }

        [Fact]
        public void FastUnit_WithAGapInTheWall_StillGetsThrough()
        {
            // The sweep must not become a blanket "fast units cannot move": with the wall opened at rows 60..68 the
            // unit still crosses (the flow field is not in this test, so it is steered straight through the gap).
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                if (row < 60 || row > 68) mask[row * GS + 64] = true;
            var holed = new PathabilityGrid(mask);

            var w = new EntityWorld();
            var move = new MovementSystem();
            w.SetPathabilityGrid(holed);
            int u = NewMover(w, -20, 20, 150);   // Z = 0 ⇒ row 64, inside the gap

            for (int t = 0; t < 60; t++) move.Tick(w, Dt);
            Assert.True(w.Position[u].X > Fixed.FromInt(2),
                $"the unit failed to pass through the gap (X={w.Position[u].X.ToFloat()}).");
        }

        [Fact]
        public void FastAndSlowUnits_AreStoppedByTheSameWallEdge()
        {
            // The behavioural statement of the fix: blocking is a property of the WALL, not of the mover's speed.
            static Fixed RestingX(int speed)
            {
                var w = new EntityWorld();
                var move = new MovementSystem();
                w.SetPathabilityGrid(ThinWall());
                int u = NewMover(w, -20, 40, speed);
                for (int t = 0; t < 600; t++) move.Tick(w, Dt);
                return w.Position[u].X;
            }
            Assert.True(RestingX(3).Raw   < 0, "the slow unit crossed the wall.");
            Assert.True(RestingX(150).Raw < 0, "the fast unit crossed the wall.");
        }

        [Fact]
        public void FastUnitAgainstTheWall_IsDeterministic_AcrossTwoSameSeedReplays()
        {
            static EntityWorld Run()
            {
                var w = new EntityWorld();
                var move = new MovementSystem();
                w.SetPathabilityGrid(ThinWall());
                NewMover(w, -20, 40, 150);
                for (int t = 0; t < 300; t++) move.Tick(w, Dt);
                return w;
            }
            EntityWorld a = Run(), b = Run();
            Assert.Equal(a.Position[0].X.Raw, b.Position[0].X.Raw);
            Assert.Equal(a.Position[0].Z.Raw, b.Position[0].Z.Raw);
        }

        [Fact]
        public void NullGrid_IsStillAnExactNoOp_ForAFastUnit()
        {
            // The flat/legacy path must remain untouched: with no pathability grid the fast unit crosses X=0 freely.
            var w = new EntityWorld();
            var move = new MovementSystem();
            int u = NewMover(w, -10, 40, 150);
            Assert.Null(w.Pathability);
            for (int t = 0; t < 10; t++) move.Tick(w, Dt);
            Assert.True(w.Position[u].X > Fixed.Zero);
        }
    }
}
