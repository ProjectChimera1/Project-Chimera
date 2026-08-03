#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-148 (movement half) — a unit that starts INSIDE a blocked cell is CONFINED instead of exempt.
    ///
    /// <para>Pre-fix <c>MovementSystem</c> rejected only a CROSSING (<c>!IsBlocked(pos) &amp;&amp; IsBlocked(np)</c>), so
    /// any unit already standing in a blocked cell skipped blocking entirely and walked straight through an arbitrarily
    /// thick wall / cliff. Post-fix the reference is the unit's PRE-STEP CELL: it may shuffle inside its own cell and
    /// walk out into a CLEAR neighbour, but it may not traverse onward into a DIFFERENT blocked cell.</para>
    ///
    /// <para>The clear-cell path is untouched (a blocked destination is necessarily a different cell), which is why no
    /// golden moves — <c>MovementSystemBlockingTests</c> + the pathability-block golden pin that arm.</para>
    /// </summary>
    public class MovementSystemInBlockedConfinementTests
    {
        private static readonly Fixed Dt = Fixed.One / Fixed.FromInt(30);
        private const int GS = PathabilityGrid.GRID_SIZE;

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A full-height blocked BAND spanning flow columns 60..70 — world X ∈ [-8, 14).</summary>
        private static PathabilityGrid BandGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
                for (int col = 60; col <= 70; col++)
                    mask[row * GS + col] = true;
            return new PathabilityGrid(mask);
        }

        /// <summary>A single blocked column 64 — world X ∈ [0, 2) (the shared wall fixture).</summary>
        private static PathabilityGrid WallGrid()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            return new PathabilityGrid(mask);
        }

        private static int NewMover(EntityWorld w, FixedVec3 from, FixedVec3 to)
        {
            int u = w.Create(from, Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            w.MoveTarget[u] = to;
            w.Flags[u]     |= EntityFlags.Moving;
            return u;
        }

        [Fact]
        public void UnitSpawnedInsideABlockedBand_CannotTraverseOnward_ThroughIt()
        {
            // The unit starts inside the band (X=-5 ⇒ column 61, world X ∈ [-6, -4)) and is commanded east, straight
            // through 10 more blocked columns. Pre-fix it walked the whole band and reached the far side; post-fix it is
            // confined to its OWN cell and can never occupy a different blocked cell.
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid band = BandGrid();
            w.SetPathabilityGrid(band);
            int u = NewMover(w, V(-5, 0), V(30, 0));

            int startCell = PathabilityGrid.CellOf(w.Position[u].X, w.Position[u].Z);
            for (int t = 0; t < 600; t++)
            {
                move.Tick(w, Dt);
                FixedVec3 p = w.Position[u];
                Assert.False(band.IsBlockedOutside(startCell, p.X, p.Z),
                    $"the unit entered a FOREIGN blocked cell at tick {t + 1} (X={p.X.ToFloat()}).");
            }

            // It never escaped east: it is still west of column 62's near edge (world X = -4).
            Assert.True(w.Position[u].X < Fixed.FromInt(-4),
                $"the unit traversed the blocked band (X={w.Position[u].X.ToFloat()}).");
        }

        [Fact]
        public void UnitSpawnedInsideABlockedCell_CanStillEscape_IntoAClearNeighbour()
        {
            // The confinement must NOT freeze a badly-placed unit in place: with only ONE blocked column, a unit that
            // spawned inside it walks out into the clear cell east and reaches its target.
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid wall = WallGrid();
            w.SetPathabilityGrid(wall);
            int u = NewMover(w, V(1, 0), V(20, 0)); // X=1 is inside the blocked column 64

            Assert.True(wall.IsBlocked(w.Position[u].X, w.Position[u].Z)); // fixture: it really starts blocked
            for (int t = 0; t < 300; t++) move.Tick(w, Dt);

            Assert.True(w.Position[u].X > Fixed.FromInt(2),
                $"the unit failed to escape its blocked cell (X={w.Position[u].X.ToFloat()}).");
            Assert.False(wall.IsBlocked(w.Position[u].X, w.Position[u].Z));
        }

        [Fact]
        public void ClearCellUnit_BehavesExactlyAsBefore_AgainstTheSameBand()
        {
            // The unchanged arm, stated as an invariant: a unit approaching the band from a CLEAR cell stops at the
            // boundary and never enters ANY blocked cell (the pre-DW-148 guarantee, unchanged ⇒ no golden movement).
            var w = new EntityWorld();
            var move = new MovementSystem();
            PathabilityGrid band = BandGrid();
            w.SetPathabilityGrid(band);
            int u = NewMover(w, V(-20, 0), V(30, 0));

            for (int t = 0; t < 400; t++)
            {
                move.Tick(w, Dt);
                FixedVec3 p = w.Position[u];
                Assert.False(band.IsBlocked(p.X, p.Z), $"a clear-cell unit entered the band at tick {t + 1}.");
            }
            Assert.True(w.Position[u].X > Fixed.FromInt(-20)); // it advanced (not frozen at spawn)
            Assert.True(w.Position[u].X < Fixed.FromInt(-8));  // and stopped west of the band's near edge
        }

        [Fact]
        public void ConfinedUnit_IsDeterministic_AcrossTwoSameSeedReplays()
        {
            static EntityWorld Run()
            {
                var w = new EntityWorld();
                var move = new MovementSystem();
                w.SetPathabilityGrid(BandGrid());
                NewMover(w, V(-5, 0), V(30, 0));
                for (int t = 0; t < 300; t++) move.Tick(w, Dt);
                return w;
            }
            EntityWorld a = Run(), b = Run();
            Assert.Equal(a.Position[0].X.Raw, b.Position[0].X.Raw);
            Assert.Equal(a.Position[0].Z.Raw, b.Position[0].Z.Raw);
        }

        [Fact]
        public void IsBlockedOutside_EqualsIsBlocked_ForEveryClearOriginCell()
        {
            // The byte-identical-behavior proof for the untouched arm: whenever the origin cell is CLEAR, the new
            // cell-relative predicate agrees with the old absolute one at every probe — so no validated map (and no
            // golden) can move.
            PathabilityGrid band = BandGrid();
            for (int x = -30; x <= 30; x++)
            {
                Fixed px = Fixed.FromInt(x);
                int cell = PathabilityGrid.CellOf(px, Fixed.Zero);
                if (band.Blocked[cell]) continue; // clear-origin cells only
                for (int dx = -3; dx <= 3; dx++)
                {
                    Fixed qx = Fixed.FromInt(x + dx);
                    Assert.Equal(band.IsBlocked(qx, Fixed.Zero), band.IsBlockedOutside(cell, qx, Fixed.Zero));
                }
            }
        }
    }
}
