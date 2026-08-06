#nullable enable
using System;
using ProjectChimera.Core;              // BuildingType, TechTreeChecker, BuildingStore
using ProjectChimera.Core.Definitions;  // BuildingDefinition
using ProjectChimera.Navigation;        // DW-570: FlowField grid constants + FlowFieldSystem's extent-source delegate

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-169 — pure, Godot-free nav-footprint resolution policy for placed buildings. Extracted from the
    /// Godot-coupled <c>NavObstacleManager</c> (which owned a private copy of the built-in size table and returned a
    /// fixed 5×3×5 box for EVERY non-enum id) so the policy is unit-testable without running Godot, and so a large
    /// authored building no longer renders at mesh size while nav-blocking a fixed small box.
    ///
    /// Resolution order (first hit wins):
    ///   1. The def's authored <c>nav_footprint</c> (<see cref="BuildingDefinition.TryGetNavFootprint"/>) — the
    ///      explicit override, honored for built-ins and customs alike.
    ///   2. A built-in enum id (<see cref="TechTreeChecker.BuildingTypeFromId"/>) → its legacy
    ///      <see cref="BUILT_IN_FOOTPRINT"/> table entry — byte-identical to the pre-DW-169 behavior for every
    ///      un-authored built-in, so existing maps bake the exact navmesh they always did. The mesh source is
    ///      deliberately NOT consulted for built-ins (changing legacy nav behavior needs its own in-engine pass).
    ///   3. A Custom/authored id → the mesh-AABB-derived size supplied by <paramref name="meshSize"/> (already
    ///      MeshScale-scaled by the caller). The Func is invoked lazily — only on this branch — so built-ins never
    ///      pay a GLB load.
    ///   4. The guarded <see cref="CUSTOM_FOOTPRINT"/> default (no def / no mesh / degenerate AABB) — the same
    ///      5×3×5 the old code used unconditionally, so a mesh-less custom building behaves exactly as before.
    ///
    /// Lives under <c>src/UI</c> (NOT globbed by SimSources.props; single-file-included by
    /// ProjectChimera.Sim.Tests — the StartSlotMath/MapBoundsMath precedent). Presentation-side floats are fine
    /// here: the navmesh carve size is never folded into SimChecksum / CanonicalModelHash / StartStateHash.
    /// </summary>
    public static class BuildingNavFootprint
    {
        /// <summary>A plain 3-component size (world units) — deliberately not Godot's Vector3 so the policy and its
        /// tests stay Godot-free. The consuming presentation layer converts.</summary>
        public readonly struct Size3
        {
            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public Size3(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        /// <summary>
        /// The canonical built-in footprint table, indexed by <see cref="BuildingType"/> value — MOVED here from
        /// <c>NavObstacleManager.TYPE_SIZE</c> (values byte-identical) so the nav policy owns a single copy. Must
        /// match <c>BuildingBridge.TYPE_FALLBACK</c> exactly (the visual fallback boxes) — the long-standing
        /// invariant that the nav obstacle and the placeholder visual agree.
        /// </summary>
        public static readonly Size3[] BUILT_IN_FOOTPRINT =
        {
            new Size3(6f, 4f, 6f), // CommandCenter
            new Size3(5f, 3f, 5f), // Barracks
            new Size3(4f, 3f, 5f), // ArcheryRange
            new Size3(5f, 3f, 7f), // SiegeWorkshop
            new Size3(5f, 3f, 7f), // Aviary (Story 2.8)
        };

        /// <summary>Story 6.8 (kept as DW-169's last resort) — the guarded footprint for a Custom/authored building
        /// with no authored <c>nav_footprint</c> and no usable mesh AABB. Matches <c>BuildingBridge.CUSTOM_FALLBACK</c>
        /// (the missing-GLB placeholder box) so the visual and the nav obstacle agree in the fallback case too.</summary>
        public static readonly Size3 CUSTOM_FOOTPRINT = new Size3(5f, 3f, 5f);

        /// <summary>
        /// Resolve the nav-obstacle footprint for a placed building. <paramref name="definitionId"/> is the
        /// building's <c>BuildingStore.DefinitionId</c>; <paramref name="def"/> its resolved
        /// <see cref="BuildingDefinition"/> (null when no faction def carries the id); <paramref name="meshSize"/>
        /// lazily supplies the mesh-AABB-derived size (already MeshScale-scaled), invoked ONLY when a custom id has
        /// no authored footprint — return null for "no usable mesh" (missing GLB / placeholder box). See the class
        /// doc for the full resolution order.
        /// </summary>
        public static Size3 Resolve(string? definitionId, BuildingDefinition? def, Func<Size3?>? meshSize = null)
        {
            // 1. Authored override — built-ins and customs alike. TryGetNavFootprint is the shared validity rule
            //    (exactly 3 finite positive values), so a malformed authored value — already a located import-time
            //    error — falls through instead of producing a degenerate obstacle.
            if (def != null && def.TryGetNavFootprint(out float ax, out float ay, out float az))
                return new Size3(ax, ay, az);

            // 2. Built-in id → the legacy table, byte-identical to pre-DW-169 for un-authored built-ins.
            BuildingType? t = TechTreeChecker.BuildingTypeFromId(definitionId);
            if (t is BuildingType bt && (int)bt >= 0 && (int)bt < BUILT_IN_FOOTPRINT.Length)
                return BUILT_IN_FOOTPRINT[(int)bt];

            // 3. Custom id → mesh-AABB-derived size (lazy; a degenerate/non-finite size is rejected so a broken
            //    GLB can never bake a zero/NaN obstacle into the navmesh).
            Size3? m = meshSize?.Invoke();
            if (m is Size3 ms && IsUsable(in ms))
                return ms;

            // 4. Guarded default — exactly the old fixed CUSTOM_FOOTPRINT behavior.
            return CUSTOM_FOOTPRINT;
        }

        /// <summary>True when every component is finite and strictly positive — the gate a mesh-derived size must
        /// pass before it becomes a nav obstacle (a placeholder/degenerate AABB falls to <see cref="CUSTOM_FOOTPRINT"/>).</summary>
        public static bool IsUsable(in Size3 s) =>
            float.IsFinite(s.X) && s.X > 0f &&
            float.IsFinite(s.Y) && s.Y > 0f &&
            float.IsFinite(s.Z) && s.Z > 0f;

        // ── DW-570: the flow-field obstacle-stamp half-extents ────────────────────────────────────────────────

        /// <summary>
        /// DW-570 — convert a resolved world-unit footprint into the per-axis HALF-EXTENT IN CELLS a square-cell
        /// obstacle grid must stamp around the building's centre cell, so the deterministic flow-field obstacle map
        /// blocks the building's REAL extent instead of one fixed 3×3 block.
        ///
        /// <para><b>The rule:</b> <c>halfCells = ceil((size / 2) / cellSizeWorld)</c>, floored at
        /// <paramref name="minHalfCells"/> and capped at <paramref name="maxHalfCells"/>. The ceiling is the exact
        /// conservative bound, not a fudge: the building's centre may sit ANYWHERE inside its centre cell, so in the
        /// worst case (centre on the cell's near edge) the box reaches <c>size/2</c> world units past that edge —
        /// which is <c>ceil((size/2)/cellSize)</c> further cells. It reproduces the legacy stamp exactly for the 4×4
        /// building the old fixed <c>BUILDING_HALF_CELLS = 1</c> was sized for (ceil(2/2) = 1 → 3×3 cells), and grows
        /// from there: a 6×6 CommandCenter → 2 (5×5 cells), a 5×3×7 SiegeWorkshop → 2 cols × 2 rows.</para>
        ///
        /// <para>X drives the COLUMN half-extent and Z the ROW half-extent (Y — the box height — is irrelevant to a
        /// 2-D XZ obstacle grid), so a long-and-narrow footprint stamps a rectangle rather than a square.</para>
        ///
        /// <para>The float→int conversion lives HERE, in the presentation-side policy (src/UI is deliberately outside
        /// the determinism-analyzer source set), and it hands the sim nothing but <c>int</c>s — the obstacle grid
        /// itself stays pure integer state. Degenerate inputs (non-finite / non-positive size, non-positive cell size)
        /// fall back to <paramref name="minHalfCells"/> rather than producing a zero or overflowed stamp.</para>
        /// </summary>
        public static void ToHalfCells(in Size3 s, int cellSizeWorld, int minHalfCells, int maxHalfCells,
                                       out int halfCols, out int halfRows)
        {
            halfCols = HalfCellsFor(s.X, cellSizeWorld, minHalfCells, maxHalfCells);
            halfRows = HalfCellsFor(s.Z, cellSizeWorld, minHalfCells, maxHalfCells);
        }

        /// <summary>One axis of <see cref="ToHalfCells"/>. The cap is applied to the FLOAT before the int cast so a
        /// wildly oversized authored footprint can never overflow the conversion.</summary>
        private static int HalfCellsFor(float sizeWorld, int cellSizeWorld, int minHalfCells, int maxHalfCells)
        {
            if (maxHalfCells < minHalfCells) maxHalfCells = minHalfCells;
            if (cellSizeWorld <= 0 || !float.IsFinite(sizeWorld) || sizeWorld <= 0f)
                return minHalfCells;

            float cells = MathF.Ceiling(sizeWorld * 0.5f / cellSizeWorld);
            if (cells >= maxHalfCells) return maxHalfCells;   // also catches +inf; cap BEFORE the cast
            int n = (int)cells;
            return n < minHalfCells ? minHalfCells : n;
        }

        /// <summary>
        /// DW-570 — the full resolve-then-convert used by the flow-field obstacle stamp: run the definition through
        /// the SAME <see cref="Resolve"/> policy the navmesh carve uses, then express the result as obstacle-grid
        /// half-extents via <see cref="ToHalfCells"/>.
        ///
        /// <para><b>No mesh source is passed, deliberately.</b> The flow field steers units in the lockstep
        /// simulation, so its obstacle map must be a pure function of state both peers provably share. A GLB's AABB
        /// is not: it depends on the local asset files and on Godot resource loading, neither of which any handshake
        /// hash covers. An un-authored CUSTOM building therefore stamps the guarded <see cref="CUSTOM_FOOTPRINT"/>
        /// here while the (presentation-only) navmesh carve may use its mesh AABB — the authored
        /// <c>nav_footprint</c> is the escape hatch that makes the two agree exactly, and it is honoured by both.</para>
        /// </summary>
        public static void ResolveHalfCells(string? definitionId, BuildingDefinition? def,
                                            int cellSizeWorld, int minHalfCells, int maxHalfCells,
                                            out int halfCols, out int halfRows)
        {
            Size3 s = Resolve(definitionId, def, meshSize: null);
            ToHalfCells(in s, cellSizeWorld, minHalfCells, maxHalfCells, out halfCols, out halfRows);
        }

        /// <summary>
        /// DW-570 — build the per-slot obstacle-extent source <see cref="FlowFieldSystem.SetBuildingFootprintSource"/>
        /// consumes, closing over the live <paramref name="buildings"/> store and the same slot→
        /// <see cref="BuildingDefinition"/> resolver <c>NavObstacleManager</c> derives its navmesh footprints from.
        /// Both nav layers therefore read ONE policy: the navmesh no longer routes around a large building's true
        /// extent while flow-field-steered units only avoid a fixed 6×6 box.
        ///
        /// <para>Grid facts (cell size, the clearance floor, the stamp cap) come from the sim's own constants, so the
        /// wiring carries no magic numbers. A null store, an out-of-range slot, or a null resolver degrades to the
        /// legacy fixed square rather than throwing — the source is invoked from the obstacle rebuild, which must
        /// never be able to fault the frame.</para>
        /// </summary>
        public static FlowFieldSystem.BuildingObstacleExtents ObstacleExtentSource(
            BuildingStore? buildings, Func<int, BuildingDefinition?>? defOf)
        {
            return (int slot, out int halfCols, out int halfRows) =>
            {
                halfCols = FlowFieldSystem.BUILDING_HALF_CELLS;
                halfRows = FlowFieldSystem.BUILDING_HALF_CELLS;
                if (buildings == null || slot < 0 || slot >= BuildingStore.MAX_BUILDINGS) return;

                ResolveHalfCells(buildings.DefinitionId[slot], defOf?.Invoke(slot),
                                 FlowField.CELL_SIZE_WORLD,
                                 FlowFieldSystem.BUILDING_HALF_CELLS,
                                 FlowFieldSystem.MAX_BUILDING_HALF_CELLS,
                                 out halfCols, out halfRows);
            };
        }
    }
}
