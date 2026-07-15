#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.5 — the deterministic sim TEETH: <c>MovementSystem</c>'s post-integration blocked-cell rejection.
    /// A live unit may not integrate its position INTO a blocked cell it is not already in; a null/all-clear grid is
    /// an exact no-op (byte-identical to pre-feature); and the behaviour is identical across two same-seed replays.
    /// Godot-free, Fixed-only.
    /// </summary>
    public class MovementSystemBlockingTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A full N-S wall at cell column 64 (world X ∈ [0, 2)).</summary>
        private static PathabilityGrid WallGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            const int GS = PathabilityGrid.GRID_SIZE;
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            return new PathabilityGrid(mask);
        }

        private static int NewMover(EntityWorld w, int fromX, int toX)
        {
            int u = w.Create(V(fromX, 0), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            w.MoveTarget[u] = V(toX, 0);
            w.Flags[u]     |= EntityFlags.Moving;
            return u;
        }

        [Fact]
        public void CommandedAcrossWall_StopsAtBoundary_NeverOccupiesBlockedCell()
        {
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = WallGrid();
            w.SetPathabilityGrid(wall);
            int u = NewMover(w, -10, 10);

            for (int t = 0; t < 300; t++)
            {
                move.Tick(w, Dt);
                FixedVec3 p = w.Position[u];
                Assert.False(wall.IsBlocked(p.X, p.Z), $"unit occupied a blocked cell at tick {t + 1} (X={p.X.ToFloat()}).");
                Assert.True(p.X < Fixed.Zero, $"unit crossed the wall near edge (X={p.X.ToFloat()}) at tick {t + 1}.");
            }
            // It advanced toward the wall from X=-10 (not frozen at spawn).
            Assert.True(w.Position[u].X > Fixed.FromInt(-10));
        }

        [Fact]
        public void NullGrid_IsByteIdenticalNoOp_UnitMovesFreely()
        {
            // With no grid the unit crosses X=0 freely — proving blocking is a pure no-op when unset.
            var w = new EntityWorld();
            var move = new MovementSystem();
            int u = NewMover(w, -10, 10);
            Assert.Null(w.Pathability);
            for (int t = 0; t < 200; t++) move.Tick(w, Dt);
            Assert.True(w.Position[u].X > Fixed.Zero, "with a null pathability grid the unit must move freely past X=0.");
        }

        [Fact]
        public void AllClearGrid_IsByteIdenticalTo_NullGrid()
        {
            var wNull = new EntityWorld();
            var wClear = new EntityWorld();
            var move = new MovementSystem();
            int a = NewMover(wNull, -10, 10);
            int b = NewMover(wClear, -10, 10);
            wClear.SetPathabilityGrid(new PathabilityGrid(new bool[PathabilityGrid.CELL_COUNT])); // AnyBlocked == false
            for (int t = 0; t < 200; t++) { move.Tick(wNull, Dt); move.Tick(wClear, Dt); }
            Assert.Equal(wNull.Position[a].X.Raw, wClear.Position[b].X.Raw);
            Assert.Equal(wNull.Position[a].Z.Raw, wClear.Position[b].Z.Raw);
        }

        [Fact]
        public void SeparationShoveTowardWall_NeverEntersBlockedCell()
        {
            // A crowd jammed against the wall: separation pushes units apart (some toward the wall). None may end up
            // in a blocked cell.
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = WallGrid();
            w.SetPathabilityGrid(wall);

            var units = new int[6];
            for (int k = 0; k < units.Length; k++)
            {
                // Cluster just west of the wall (X in [-2,-1)), varied Z, all pushing east into it.
                int u = w.Create(new FixedVec3(Fixed.FromFloat(-1.2f), Fixed.Zero, Fixed.FromInt(k - 3)),
                                 Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
                w.CollisionRadius[u] = Fixed.One;
                w.MoveTarget[u]      = V(10, k - 3);
                w.Flags[u]          |= EntityFlags.Moving;
                units[k] = u;
            }

            for (int t = 0; t < 300; t++)
            {
                move.Tick(w, Dt);
                foreach (int u in units)
                {
                    FixedVec3 p = w.Position[u];
                    Assert.False(wall.IsBlocked(p.X, p.Z),
                        $"a shoved unit entered a blocked cell (X={p.X.ToFloat()}, Z={p.Z.ToFloat()}) at tick {t + 1}.");
                }
            }
        }

        [Fact]
        public void TwoSameSeedReplays_ProduceIdenticalPositions()
        {
            static EntityWorld Run()
            {
                var w = new EntityWorld();
                var move = new MovementSystem();
                w.SetPathabilityGrid(WallGrid());
                int u = w.Create(V(-10, 0), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
                w.MoveTarget[u] = V(10, 0);
                w.Flags[u]     |= EntityFlags.Moving;
                for (int t = 0; t < 300; t++) move.Tick(w, Dt);
                return w;
            }
            EntityWorld a = Run(), b = Run();
            Assert.Equal(a.Position[0].X.Raw, b.Position[0].X.Raw);
            Assert.Equal(a.Position[0].Z.Raw, b.Position[0].Z.Raw);
        }
    }
}
