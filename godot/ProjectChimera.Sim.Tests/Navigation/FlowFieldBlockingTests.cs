#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// Story 6.5 — the live-game "route around" nicety: <see cref="FlowFieldSystem.SetStaticBlocked"/> ORs the
    /// authored blocked mask into the obstacle map, so the BFS excludes blocked cells and steers units through gaps.
    /// The field is deterministic across two identical loads (the 6.2 identical-field pattern).
    /// </summary>
    public class FlowFieldBlockingTests
    {
        private const int GS = PathabilityGrid.GRID_SIZE;

        /// <summary>A N-S wall at column 64 with a gap at rows 60..67 (a chokepoint the BFS must route through).</summary>
        private static bool[] GappedWall()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++)
            {
                if (row >= 60 && row <= 67) continue; // the gap
                mask[row * GS + 64] = true;
            }
            return mask;
        }

        private static FlowFieldSystem NewSystem(bool[] staticMask)
        {
            var sys = new FlowFieldSystem();
            sys.SetStaticBlocked(staticMask);
            sys.RebuildObstacles(new BuildingStore()); // no buildings; ORs the static mask in
            return sys;
        }

        [Fact]
        public void BlockedCells_AreExcluded_FromTheField()
        {
            var sys = NewSystem(GappedWall());
            // Goal on the far (east) side of the wall.
            FlowField field = sys.GetOrCompute(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.Zero));
            // A blocked wall cell (column 64, a non-gap row) is never assigned a steering direction (stays Zero).
            int blockedRow = 10;
            Assert.Equal(FixedVec3.Zero, field.Directions[blockedRow * GS + 64]);
        }

        [Fact]
        public void NearSideCell_IsReachable_ByRoutingThroughTheGap()
        {
            var sys = NewSystem(GappedWall());
            FlowField field = sys.GetOrCompute(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.Zero));
            // A passable cell on the WEST side (col 40, row 30) must have a non-Zero direction — the BFS reached it
            // by routing around/through the gap, not straight through the wall.
            Assert.NotEqual(FixedVec3.Zero, field.Directions[30 * GS + 40]);
        }

        [Fact]
        public void IdenticalStaticMask_ProducesIdenticalField_AcrossTwoLoads()
        {
            var a = NewSystem(GappedWall());
            var b = NewSystem(GappedWall());
            var goal = new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(4));
            FlowField fa = a.GetOrCompute(goal);
            FlowField fb = b.GetOrCompute(goal);
            for (int i = 0; i < FlowField.CELL_COUNT; i++)
            {
                Assert.Equal(fa.Directions[i].X.Raw, fb.Directions[i].X.Raw);
                Assert.Equal(fa.Directions[i].Z.Raw, fb.Directions[i].Z.Raw);
            }
        }

        [Fact]
        public void FullWall_FullySeparatesTheMap_FarSideUnreachable()
        {
            // A gapless wall: a west cell cannot reach an east goal (the field never steers it — no path).
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            for (int row = 0; row < GS; row++) mask[row * GS + 64] = true;
            var sys = NewSystem(mask);
            FlowField field = sys.GetOrCompute(new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.Zero));
            Assert.Equal(FixedVec3.Zero, field.Directions[30 * GS + 10]); // deep west cell, no path east
        }
    }
}
