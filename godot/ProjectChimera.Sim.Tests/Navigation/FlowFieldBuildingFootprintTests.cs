#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-570 — the sim-side flow-field obstacle stamp must cover each building's REAL footprint.
    ///
    /// <para>Before this entry <c>FlowFieldSystem.MarkBuildingCells</c> stamped a fixed 3×3 cells
    /// (<c>BUILDING_HALF_CELLS = 1</c>, ~6×6 world units) for EVERY building, while the navmesh layer already derived
    /// footprints from the definition (DW-169). Built-in buildings run 4–7 world units and custom footprints are
    /// def-derived, so NavigationServer paths routed around a large building's true extent while flow-field-steered
    /// units only avoided 6×6 — they clipped or crowded large buildings.</para>
    ///
    /// <para>The fix derives per-building half-cell extents from the SAME <see cref="BuildingNavFootprint"/> policy the
    /// navmesh carve uses, injected into the sim as pure integers via
    /// <see cref="FlowFieldSystem.SetBuildingFootprintSource"/>. These tests pin both halves: the conversion policy
    /// (world size → half-cells) and the stamp it produces on the live obstacle grid.</para>
    ///
    /// <para>FALSIFICATION: <see cref="CommandCenter_BlocksItsFullSixUnitFootprint"/>,
    /// <see cref="AuthoredNavFootprint_StampsARectangle_NotASquare"/> and
    /// <see cref="EnlargedFootprintCells_AreExcludedFromTheComputedField"/> all assert cells the pre-fix fixed
    /// half-extent of 1 left CLEAR, so each one fails against the old <c>MarkBuildingCells</c>.
    /// <see cref="NoFootprintSource_KeepsTheLegacyThreeByThreeStamp"/> is the opposite pin — it holds on BOTH the old
    /// and new code, which is the point: an un-wired system must stay byte-identical to pre-DW-570.</para>
    /// </summary>
    public class FlowFieldBuildingFootprintTests
    {
        private const int CELL = FlowField.CELL_SIZE_WORLD;                    // 2 world units per cell
        private const int MIN  = FlowFieldSystem.BUILDING_HALF_CELLS;          // 1 — the clearance floor
        private const int MAX  = FlowFieldSystem.MAX_BUILDING_HALF_CELLS;      // 32 — the stamp cap

        /// <summary>World origin maps to cell (64, 64): (0 + 128) / 2.</summary>
        private const int ORIGIN_CELL = 64;

        private static FixedVec3 Origin => new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero);

        // ── The conversion policy: world footprint → obstacle half-extents in cells ────────────────────────────

        /// <summary>
        /// The legacy constant was sized for a 4×4-world-unit building, and the new rule reproduces it exactly:
        /// ceil((4/2) / 2) = 1 → the same 3×3-cell stamp. Anything larger grows, which is the whole entry.
        /// </summary>
        [Fact]
        public void FourByFourFootprint_ReproducesTheLegacyOneCellHalfExtent()
        {
            BuildingNavFootprint.ToHalfCells(new BuildingNavFootprint.Size3(4f, 3f, 4f), CELL, MIN, MAX,
                                             out int halfCols, out int halfRows);
            Assert.Equal(1, halfCols);
            Assert.Equal(1, halfRows);
        }

        /// <summary>
        /// Every built-in id resolves through the shared policy to the half-extents its real footprint needs.
        /// The ceiling is the exact conservative bound — a building centre may sit anywhere inside its centre cell,
        /// so a 6-unit span reaches 3 units past that cell's near edge = 2 further cells, not 1.
        /// </summary>
        [Theory]
        [InlineData("command_center", 2, 2)]  // 6 × 4 × 6
        [InlineData("barracks",       2, 2)]  // 5 × 3 × 5
        [InlineData("archery_range",  1, 2)]  // 4 × 3 × 5 — X fits in 1, Z needs 2
        [InlineData("siege_workshop", 2, 2)]  // 5 × 3 × 7
        [InlineData("aviary",         2, 2)]  // 5 × 3 × 7
        public void BuiltInIds_ResolveToTheirRealHalfExtents(string id, int expectCols, int expectRows)
        {
            BuildingNavFootprint.ResolveHalfCells(id, def: null, CELL, MIN, MAX,
                                                  out int halfCols, out int halfRows);
            Assert.Equal(expectCols, halfCols);
            Assert.Equal(expectRows, halfRows);
        }

        /// <summary>An un-authored custom id has no table entry and (in the sim) no mesh source, so it lands on the
        /// guarded 5×3×5 default — 2 cells each way, the same the navmesh uses in its own fallback case.</summary>
        [Fact]
        public void UnauthoredCustomId_UsesTheGuardedDefaultFootprint()
        {
            BuildingNavFootprint.ResolveHalfCells("watchtower", def: null, CELL, MIN, MAX,
                                                  out int halfCols, out int halfRows);
            Assert.Equal(2, halfCols);
            Assert.Equal(2, halfRows);
        }

        /// <summary>The authored <c>nav_footprint</c> override wins over the built-in table here exactly as it does
        /// for the navmesh — that shared override is what lets a creator make the two nav layers agree exactly.</summary>
        [Fact]
        public void AuthoredNavFootprint_OverridesTheBuiltInTable()
        {
            var def = new BuildingDefinition { NavFootprint = new[] { 16f, 3f, 4f } };
            BuildingNavFootprint.ResolveHalfCells("command_center", def, CELL, MIN, MAX,
                                                  out int halfCols, out int halfRows);
            Assert.Equal(4, halfCols);   // ceil((16/2)/2)
            Assert.Equal(1, halfRows);   // ceil((4/2)/2)
        }

        /// <summary>X drives COLUMNS and Z drives ROWS, so a long-and-narrow building stamps a rectangle. A square
        /// stamp would be exactly the bug this entry is about, at a different scale.</summary>
        [Fact]
        public void FootprintAxes_MapXToColumnsAndZToRows()
        {
            BuildingNavFootprint.ToHalfCells(new BuildingNavFootprint.Size3(20f, 3f, 4f), CELL, MIN, MAX,
                                             out int halfCols, out int halfRows);
            Assert.Equal(5, halfCols);
            Assert.Equal(1, halfRows);
            Assert.NotEqual(halfCols, halfRows);
        }

        /// <summary>A sub-cell footprint never shrinks the stamp below the legacy clearance floor.</summary>
        [Fact]
        public void TinyFootprint_NeverFallsBelowTheClearanceFloor()
        {
            BuildingNavFootprint.ToHalfCells(new BuildingNavFootprint.Size3(0.25f, 0.25f, 0.25f), CELL, MIN, MAX,
                                             out int halfCols, out int halfRows);
            Assert.Equal(MIN, halfCols);
            Assert.Equal(MIN, halfRows);
        }

        /// <summary><c>nav_footprint</c>'s only content validation is "3 finite positive values", so the conversion
        /// caps the result BEFORE the float→int cast: an absurd authored size bounds out instead of overflowing.</summary>
        [Fact]
        public void AbsurdFootprint_IsCappedAtTheStampBound()
        {
            BuildingNavFootprint.ToHalfCells(new BuildingNavFootprint.Size3(1e9f, 3f, 1e9f), CELL, MIN, MAX,
                                             out int halfCols, out int halfRows);
            Assert.Equal(MAX, halfCols);
            Assert.Equal(MAX, halfRows);
        }

        /// <summary>Defensive: a non-finite or non-positive size (which <see cref="BuildingNavFootprint.Resolve"/>
        /// never produces, but a future caller could hand in directly) degrades to the floor, never to a zero stamp
        /// or a garbage cast.</summary>
        [Theory]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NaN)]
        [InlineData(0f)]
        [InlineData(-8f)]
        public void DegenerateSize_DegradesToTheClearanceFloor(float bad)
        {
            BuildingNavFootprint.ToHalfCells(new BuildingNavFootprint.Size3(bad, bad, bad), CELL, MIN, MAX,
                                             out int halfCols, out int halfRows);
            Assert.Equal(MIN, halfCols);
            Assert.Equal(MIN, halfRows);
        }

        // ── The stamp on the live obstacle grid ───────────────────────────────────────────────────────────────

        /// <summary>A store holding one building of <paramref name="type"/>/<paramref name="id"/> at the world origin.</summary>
        private static BuildingStore StoreWithOneBuilding(BuildingType type, string? id = null)
        {
            var buildings = new BuildingStore();
            buildings.Create(Origin, Faction.Player1, type, buildingId: id!);
            return buildings;
        }

        /// <summary>A system wired exactly the way NavigationPhase wires the live one.</summary>
        private static FlowFieldSystem WiredSystem(BuildingStore buildings, Func<int, BuildingDefinition?>? defOf = null)
        {
            var sys = new FlowFieldSystem();
            sys.SetBuildingFootprintSource(BuildingNavFootprint.ObstacleExtentSource(buildings, defOf));
            sys.RebuildObstacles(buildings);
            return sys;
        }

        /// <summary>
        /// THE ENTRY, on the grid: a 6×6-world-unit CommandCenter blocks 2 cells each way, not 1. Column 66
        /// (= origin + 2 cells) is exactly the cell the pre-fix fixed 3×3 stamp left walkable underneath the
        /// building's real extent.
        /// </summary>
        [Fact]
        public void CommandCenter_BlocksItsFullSixUnitFootprint()
        {
            var sys = WiredSystem(StoreWithOneBuilding(BuildingType.CommandCenter));

            Assert.True(sys.GetObstacle(ORIGIN_CELL + 2, ORIGIN_CELL), "col +2 must be blocked (6-unit footprint)");
            Assert.True(sys.GetObstacle(ORIGIN_CELL - 2, ORIGIN_CELL), "col -2 must be blocked (6-unit footprint)");
            Assert.True(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL + 2), "row +2 must be blocked (6-unit footprint)");
            Assert.True(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL - 2), "row -2 must be blocked (6-unit footprint)");

            // ...and not one cell further: the stamp is the footprint, not a blanket enlargement.
            Assert.False(sys.GetObstacle(ORIGIN_CELL + 3, ORIGIN_CELL), "col +3 is outside the footprint");
            Assert.False(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL + 3), "row +3 is outside the footprint");
        }

        /// <summary>
        /// BACK-COMPAT PIN: with no footprint source wired (every existing caller and test) the stamp is the exact
        /// pre-DW-570 3×3 square. This one passes against the OLD code too — deliberately: it is the guard that the
        /// new seam is opt-in and changed nothing for un-wired callers.
        /// </summary>
        [Fact]
        public void NoFootprintSource_KeepsTheLegacyThreeByThreeStamp()
        {
            var buildings = StoreWithOneBuilding(BuildingType.CommandCenter);
            var sys = new FlowFieldSystem();
            sys.RebuildObstacles(buildings);            // no SetBuildingFootprintSource

            Assert.True(sys.GetObstacle(ORIGIN_CELL + 1, ORIGIN_CELL));
            Assert.True(sys.GetObstacle(ORIGIN_CELL - 1, ORIGIN_CELL));
            Assert.False(sys.GetObstacle(ORIGIN_CELL + 2, ORIGIN_CELL));
            Assert.False(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL + 2));
        }

        /// <summary>
        /// An authored <c>nav_footprint</c> reaches the sim stamp, and it stamps a RECTANGLE: 16 wide × 4 deep →
        /// 4 cells of columns, 1 cell of rows. The pre-fix code blocked a 1×1 square here regardless.
        /// </summary>
        [Fact]
        public void AuthoredNavFootprint_StampsARectangle_NotASquare()
        {
            var buildings = StoreWithOneBuilding(BuildingType.Custom, "watchtower");
            var def = new BuildingDefinition { NavFootprint = new[] { 16f, 3f, 4f } };
            var sys = WiredSystem(buildings, _ => def);

            Assert.True(sys.GetObstacle(ORIGIN_CELL + 4, ORIGIN_CELL), "col +4 is inside the 16-unit width");
            Assert.False(sys.GetObstacle(ORIGIN_CELL + 5, ORIGIN_CELL), "col +5 is outside it");
            Assert.True(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL + 1), "row +1 is inside the 4-unit depth");
            Assert.False(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL + 2), "row +2 is outside it");
        }

        /// <summary>The sim clamps whatever the policy hands back, so absurd authored content cannot stamp past the
        /// bound (and cannot make the marking loop unbounded).</summary>
        [Fact]
        public void AbsurdAuthoredFootprint_CannotStampBeyondTheCap()
        {
            var buildings = StoreWithOneBuilding(BuildingType.Custom, "colossus");
            var def = new BuildingDefinition { NavFootprint = new[] { 10000f, 3f, 10000f } };
            var sys = WiredSystem(buildings, _ => def);

            Assert.True(sys.GetObstacle(ORIGIN_CELL + MAX, ORIGIN_CELL));
            Assert.False(sys.GetObstacle(ORIGIN_CELL + MAX + 1, ORIGIN_CELL));
        }

        /// <summary>
        /// End-to-end: the enlarged stamp actually reaches the BFS, so units steer around the building's true extent.
        /// A cell inside the enlarged footprint is never assigned a steering direction — the pre-fix map left that
        /// same cell walkable, which is precisely how units clipped large buildings.
        /// </summary>
        [Fact]
        public void EnlargedFootprintCells_AreExcludedFromTheComputedField()
        {
            var sys = WiredSystem(StoreWithOneBuilding(BuildingType.CommandCenter));
            FlowField field = sys.GetOrCompute(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.Zero));

            int gs = FlowField.GRID_SIZE;
            Assert.Equal(FixedVec3.Zero, field.Directions[ORIGIN_CELL * gs + (ORIGIN_CELL + 2)]);
            // A cell just outside the footprint is still reachable — the building blocks its own extent, not the map.
            Assert.NotEqual(FixedVec3.Zero, field.Directions[ORIGIN_CELL * gs + (ORIGIN_CELL + 3)]);
        }

        /// <summary>A dead slot is not stamped, and the footprint source is never consulted for one — the alive
        /// filter still owns which buildings block.</summary>
        [Fact]
        public void DeadBuilding_IsNotStamped_AndItsSourceIsNotConsulted()
        {
            var buildings = StoreWithOneBuilding(BuildingType.CommandCenter);
            buildings.Destroy(0);

            int calls = 0;
            var sys = new FlowFieldSystem();
            sys.SetBuildingFootprintSource((int slot, out int halfCols, out int halfRows) =>
            {
                calls++;
                halfCols = 4; halfRows = 4;
            });
            sys.RebuildObstacles(buildings);

            Assert.Equal(0, calls);
            Assert.False(sys.GetObstacle(ORIGIN_CELL, ORIGIN_CELL));
        }

        /// <summary>Two identically-built systems produce byte-identical obstacle maps — the stamp stays a pure
        /// function of (definition, position), which is what keeps the flow field lockstep-safe.</summary>
        [Fact]
        public void IdenticalInputs_ProduceIdenticalObstacleMaps()
        {
            var a = WiredSystem(StoreWithOneBuilding(BuildingType.SiegeWorkshop));
            var b = WiredSystem(StoreWithOneBuilding(BuildingType.SiegeWorkshop));

            for (int row = 0; row < FlowField.GRID_SIZE; row++)
                for (int col = 0; col < FlowField.GRID_SIZE; col++)
                    Assert.Equal(a.GetObstacle(col, row), b.GetObstacle(col, row));
        }

        /// <summary>The slot-aware single-building entry point stamps the same cells a full rebuild does — the
        /// position-only overload cannot, because it has no slot to resolve a footprint from.</summary>
        [Fact]
        public void SlotAwareSetBuildingObstacle_MatchesTheRebuildStamp()
        {
            var buildings = StoreWithOneBuilding(BuildingType.CommandCenter);

            var incremental = new FlowFieldSystem();
            incremental.SetBuildingFootprintSource(BuildingNavFootprint.ObstacleExtentSource(buildings, null));
            incremental.SetBuildingObstacle(0, Origin, true);

            var rebuilt = WiredSystem(buildings);

            for (int row = 0; row < FlowField.GRID_SIZE; row++)
                for (int col = 0; col < FlowField.GRID_SIZE; col++)
                    Assert.Equal(rebuilt.GetObstacle(col, row), incremental.GetObstacle(col, row));
        }
    }
}
