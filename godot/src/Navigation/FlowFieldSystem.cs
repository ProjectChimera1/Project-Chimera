#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Manages the obstacle map used for flow field pathfinding and caches computed fields.
    ///
    /// Obstacle map: a bool[] (128×128) where true = impassable cell.
    /// Building footprint (DW-570): each building marks the cell rectangle its OWN footprint covers, derived
    /// per-slot from the same nav-footprint policy the navmesh carve uses and injected as integer half-extents
    /// via <see cref="SetBuildingFootprintSource"/>. <see cref="BUILDING_HALF_CELLS"/> is the clearance FLOOR
    /// (a 3×3-cell stamp, right for the 4×4-world-unit building the constant was originally sized for) — with no
    /// source wired every building stamps exactly that floor, byte-identical to the pre-DW-570 behaviour.
    ///
    /// Field cache: Dictionary keyed by goal cell index. Multiple units moving to the same
    /// destination share one field — the key advantage of flow fields over per-unit queries.
    /// The cache is BOUNDED (DW-485): at most <see cref="MAX_CACHED_FIELDS"/> fields are retained,
    /// evicting the least-recently-used entry once the cap is exceeded. Each field is ~192 KB
    /// (16 384 cells × 12-byte FixedVec3), so an unbounded cache accumulated ~200 KB per distinct
    /// destination cell until the next obstacle change (~20 MB after ~100 distinct move orders).
    ///
    /// Eviction only removes the dictionary entry — FlowField instances are never pooled or
    /// mutated after compute, so a unit (FlowFieldBridge) still holding an evicted field keeps
    /// steering correctly with it; a recompute only happens if a NEW order targets that cell.
    ///
    /// Determinism: the obstacle map is pure integer state and the BFS is deterministic,
    /// so both peers in a lockstep match will produce identical fields from identical inputs.
    /// LRU eviction preserves this: it is a pure function of the GetOrCompute call sequence
    /// (identical on every peer under lockstep), and an evicted field recomputes byte-identical,
    /// so bounding the cache is invisible to the simulation.
    ///
    /// Call order (from MainScene / scenario loading):
    ///   1. RebuildObstacles(buildings)   — once at scenario load, once after any building change
    ///   2. GetOrCompute(goal)             — on each move/attack-move command
    /// </summary>
    public sealed class FlowFieldSystem
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const int GS   = FlowField.GRID_SIZE;
        private const int SIZE = FlowField.CELL_COUNT;

        /// <summary>
        /// MINIMUM half-extent (in cells) of the obstacle footprint for a building — the clearance floor.
        /// 1 → 3×3 cells = 6×6 world units, the right stamp for a 4×4-world-unit building (and, before DW-570,
        /// the stamp EVERY building got regardless of size). A per-building extent from
        /// <see cref="SetBuildingFootprintSource"/> may only grow the stamp past this floor, never shrink it.
        /// </summary>
        public const int BUILDING_HALF_CELLS = 1;

        /// <summary>
        /// DW-570: hard cap on a single building's obstacle half-extent, in cells (32 → a 65×65-cell / 130×130-world-unit
        /// stamp, half the grid). Content authors a raw <c>nav_footprint</c> whose only validation is "3 finite positive
        /// values", so this bounds both the marking loop's cost and the blast radius of an absurd authored size. No real
        /// building comes close — the largest built-in is 7 world units (half-extent 2 cells).
        /// </summary>
        public const int MAX_BUILDING_HALF_CELLS = 32;

        /// <summary>
        /// DW-570: per-slot obstacle half-extents, in CELLS, for the building occupying <paramref name="slot"/>.
        /// Deliberately integer-only and slot-keyed: the resolution policy (authored <c>nav_footprint</c> > built-in
        /// table > guarded default) lives with the presentation-side <c>BuildingNavFootprint</c> that also feeds the
        /// navmesh carve, so both nav layers share ONE footprint source while the sim layer stays Godot-free,
        /// float-free and deterministic.
        /// </summary>
        public delegate void BuildingObstacleExtents(int slot, out int halfCols, out int halfRows);

        /// <summary>
        /// DW-485: upper bound on the number of cached flow fields. Each field is ~192 KB
        /// (CELL_COUNT × 12-byte FixedVec3), so 32 caps retained memory at ~6.3 MB on the
        /// current 128² grid (~25 MB at the map-size Route B 256² grid). Far more than the
        /// distinct destinations plausibly in flight at once — units keep steering with a
        /// field they already hold even after it is evicted from the cache.
        /// </summary>
        public const int MAX_CACHED_FIELDS = 32;

        // ── State ─────────────────────────────────────────────────────────────

        private readonly bool[]            _obstacles = new bool[SIZE];
        private readonly FlowFieldComputer _computer  = new FlowFieldComputer();

        /// <summary>
        /// Bounded LRU cache, keyed by goal cell index. LastUse is a unique monotonic access
        /// stamp (updated on every hit and insert); eviction removes the minimum-stamp entry.
        /// Stamps are unique, so the LRU choice never depends on dictionary enumeration order —
        /// eviction is a deterministic function of the GetOrCompute call sequence.
        /// </summary>
        private readonly Dictionary<int, (FlowField Field, long LastUse)> _cache =
            new Dictionary<int, (FlowField, long)>();

        /// <summary>Monotonic access counter backing the LRU stamps. Reset on every cache clear.</summary>
        private long _accessStamp;

        /// <summary>
        /// Story 6.5: the static authored blocked mask (painted ∪ slope-derived cells, same 128²/2-unit/±128 cell
        /// identity as the obstacle map) injected once at load via <see cref="SetStaticBlocked"/>. OR'd into
        /// <see cref="_obstacles"/> on every <see cref="RebuildObstacles"/> so the BFS routes AROUND impassable
        /// terrain in the live game. Null ⇒ no static blocking (byte-identical to pre-feature). Held by reference —
        /// the load-time seam builds it once and never mutates it thereafter.
        /// </summary>
        private bool[]? _staticBlocked;

        /// <summary>
        /// DW-570: the injected per-slot footprint source (see <see cref="SetBuildingFootprintSource"/>). Null ⇒ every
        /// building stamps the fixed <see cref="BUILDING_HALF_CELLS"/> square, byte-identical to pre-DW-570.
        /// </summary>
        private BuildingObstacleExtents? _extentsOf;

        // ── Obstacle map ──────────────────────────────────────────────────────

        /// <summary>
        /// Rebuild the obstacle map from scratch using all alive buildings.
        /// Clears the field cache (all cached fields become stale after a rebuild).
        ///
        /// Call this once at scenario load and after any building placement / destruction.
        /// </summary>
        public void RebuildObstacles(BuildingStore buildings)
        {
            System.Array.Clear(_obstacles, 0, SIZE);
            ClearCache();

            // Story 6.5: OR the static authored blocked mask (painted ∪ slope-derived) in FIRST, so the BFS treats
            // impassable terrain as obstacles and steers units around it. Same cell identity as the building marks
            // below (both go through FlowField.WorldToCell). Null ⇒ nothing added (byte-identical to pre-feature).
            if (_staticBlocked != null)
                for (int c = 0; c < SIZE; c++)
                    if (_staticBlocked[c]) _obstacles[c] = true;

            // DW-570: ascending slot order, and each slot stamps ITS OWN footprint rectangle rather than one fixed
            // 3×3 block. Order is irrelevant to the result here (marking is idempotent OR-ing of `true`), but it is
            // kept ascending to match the house rule and so the source is invoked in a deterministic sequence.
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!buildings.Alive[i]) continue;
                ResolveHalfCells(i, out int halfCols, out int halfRows);
                MarkBuildingCells(buildings.Position[i], halfCols, halfRows, true);
            }
        }

        /// <summary>
        /// DW-570: inject the per-slot obstacle footprint source — the seam that lets the deterministic flow-field
        /// obstacle map block each building's REAL extent instead of one fixed 3×3-cell block. Wired once at
        /// scenario setup (NavigationPhase) with a closure over the same definition resolver the navmesh carve uses;
        /// null restores the pre-DW-570 fixed-square stamp exactly.
        ///
        /// <para>Clears the field cache (every cached field was computed against the old stamps) but does NOT rebuild
        /// the obstacle map: the caller owns that ordering, and the live wiring runs before the first
        /// <see cref="RebuildObstacles"/>. Call it BEFORE the rebuild, or rebuild yourself after changing it.</para>
        /// </summary>
        public void SetBuildingFootprintSource(BuildingObstacleExtents? source)
        {
            _extentsOf = source;
            ClearCache();
        }

        /// <summary>
        /// Story 6.5: inject the static authored blocked mask (painted ∪ slope-derived cells; length
        /// <see cref="FlowField.CELL_COUNT"/>). Held by reference and OR'd into the obstacle map on the next (and
        /// every subsequent) <see cref="RebuildObstacles"/>; the field cache is cleared so any already-computed field
        /// recomputes against the new blocking. Null / wrong-length ⇒ no static blocking. Call once at scenario load.
        /// </summary>
        public void SetStaticBlocked(bool[]? mask)
        {
            _staticBlocked = (mask != null && mask.Length == SIZE) ? mask : null;
            ClearCache();
        }

        /// <summary>
        /// Mark or unmark obstacle cells for a single building centered at <paramref name="pos"/>.
        /// Pass <c>true</c> when a building is placed, <c>false</c> when it is destroyed.
        /// Automatically invalidates the field cache.
        ///
        /// <para>DW-570: this position-only entry point has no slot, so it cannot consult the per-slot footprint
        /// source and stamps the <see cref="BUILDING_HALF_CELLS"/> floor. Use
        /// <see cref="SetBuildingObstacle(int, FixedVec3, bool)"/> — or a full <see cref="RebuildObstacles"/>, which
        /// is what the live game actually does on every building change — to get the real footprint.</para>
        /// </summary>
        public void SetBuildingObstacle(FixedVec3 pos, bool obstacle)
        {
            MarkBuildingCells(pos, BUILDING_HALF_CELLS, BUILDING_HALF_CELLS, obstacle);
            ClearCache();
        }

        /// <summary>
        /// DW-570: the slot-aware form of <see cref="SetBuildingObstacle(FixedVec3, bool)"/> — stamps the footprint
        /// the injected source reports for <paramref name="slot"/>, so a single-building update blocks the same cells
        /// a full <see cref="RebuildObstacles"/> would.
        ///
        /// <para>Unmarking (<paramref name="obstacle"/> = <c>false</c>) clears the whole rectangle unconditionally, so
        /// it erases any overlap with a neighbouring building or the static blocked mask — the same caveat the
        /// position-only overload always carried. Prefer <see cref="RebuildObstacles"/> for removals.</para>
        /// </summary>
        public void SetBuildingObstacle(int slot, FixedVec3 pos, bool obstacle)
        {
            ResolveHalfCells(slot, out int halfCols, out int halfRows);
            MarkBuildingCells(pos, halfCols, halfRows, obstacle);
            ClearCache();
        }

        // ── Field access ──────────────────────────────────────────────────────

        /// <summary>
        /// Return a flow field for <paramref name="goal"/>. Uses a cached field if one exists
        /// for the same goal cell, otherwise computes a new field via BFS and caches it.
        ///
        /// Multiple move commands to nearby positions sharing a cell return the same field.
        /// The cache is invalidated whenever the obstacle map changes, and is bounded at
        /// <see cref="MAX_CACHED_FIELDS"/> entries (DW-485): computing a new field past the cap
        /// evicts the least-recently-used entry. Evicted fields already held by callers stay
        /// valid — instances are never pooled or mutated after compute.
        /// </summary>
        public FlowField GetOrCompute(FixedVec3 goal)
        {
            int key = FlowField.WorldToIndex(goal.X, goal.Z);

            if (_cache.TryGetValue(key, out (FlowField Field, long LastUse) hit))
            {
                _cache[key] = (hit.Field, ++_accessStamp);
                return hit.Field;
            }

            var field = new FlowField();
            _computer.Compute(field, goal, _obstacles);
            _cache[key] = (field, ++_accessStamp);

            if (_cache.Count > MAX_CACHED_FIELDS)
                EvictLeastRecentlyUsed();

            return field;
        }

        /// <summary>
        /// Discard all cached flow fields without changing the obstacle map.
        /// Call this if you need to force recomputation without a building change
        /// (e.g. after terrain sculpting that affects passability).
        /// </summary>
        public void InvalidateCache() => ClearCache();

        /// <summary>Number of flow fields currently retained in the cache (≤ <see cref="MAX_CACHED_FIELDS"/>).</summary>
        public int CachedFieldCount => _cache.Count;

        /// <summary>Read-only access to the raw obstacle map (for debug visualization).</summary>
        public bool GetObstacle(int col, int row) => _obstacles[row * GS + col];

        // ── Private ───────────────────────────────────────────────────────────

        /// <summary>
        /// Drop every cached field and reset the LRU access counter. All cache-clearing paths
        /// (obstacle rebuild, static-mask injection, single-building change, explicit
        /// invalidation) route through here so the stamp state can never desync from the cache.
        /// </summary>
        private void ClearCache()
        {
            _cache.Clear();
            _accessStamp = 0;
        }

        /// <summary>
        /// DW-485: remove the least-recently-used cache entry (minimum access stamp).
        /// Stamps are unique, so the minimum is unique and the scan result does not depend on
        /// dictionary enumeration order — eviction stays deterministic across peers. The O(cap)
        /// scan runs at most once per newly computed field, which is noise next to the 16 384-cell
        /// BFS that preceded it. Only the dictionary entry is removed; the FlowField instance
        /// itself stays valid for any unit still steering with it.
        /// </summary>
        private void EvictLeastRecentlyUsed()
        {
            int  lruKey   = -1;
            long lruStamp = long.MaxValue;

            foreach (KeyValuePair<int, (FlowField Field, long LastUse)> kv in _cache)
            {
                if (kv.Value.LastUse < lruStamp)
                {
                    lruStamp = kv.Value.LastUse;
                    lruKey   = kv.Key;
                }
            }

            if (lruKey >= 0)
                _cache.Remove(lruKey);
        }

        /// <summary>
        /// DW-570: resolve building <paramref name="slot"/>'s obstacle half-extents in cells. Falls back to the
        /// <see cref="BUILDING_HALF_CELLS"/> floor when no source is wired, and clamps whatever a wired source
        /// returns into [<see cref="BUILDING_HALF_CELLS"/>, <see cref="MAX_BUILDING_HALF_CELLS"/>] — the sim owns
        /// that bound, so a policy bug or absurd authored footprint can neither shrink the stamp below the legacy
        /// clearance nor blow up the marking loop.
        /// </summary>
        private void ResolveHalfCells(int slot, out int halfCols, out int halfRows)
        {
            halfCols = BUILDING_HALF_CELLS;
            halfRows = BUILDING_HALF_CELLS;
            if (_extentsOf == null) return;

            _extentsOf(slot, out int hc, out int hr);
            halfCols = System.Math.Clamp(hc, BUILDING_HALF_CELLS, MAX_BUILDING_HALF_CELLS);
            halfRows = System.Math.Clamp(hr, BUILDING_HALF_CELLS, MAX_BUILDING_HALF_CELLS);
        }

        /// <summary>
        /// Mark a (2·<paramref name="halfCols"/> + 1) × (2·<paramref name="halfRows"/> + 1) rectangle of cells around
        /// the building center. DW-570: the extents are per-building (column half-extent from the footprint's X,
        /// row half-extent from its Z) rather than one fixed square, so a long-and-narrow building stamps a
        /// rectangle. Does NOT invalidate the cache — callers are responsible for that.
        /// </summary>
        private void MarkBuildingCells(FixedVec3 pos, int halfCols, int halfRows, bool obstacle)
        {
            FlowField.WorldToCell(pos.X, pos.Z, out int cc, out int cr);

            for (int dc = -halfCols; dc <= halfCols; dc++)
            {
                for (int dr = -halfRows; dr <= halfRows; dr++)
                {
                    int nc = cc + dc;
                    int nr = cr + dr;
                    if ((uint)nc >= (uint)GS || (uint)nr >= (uint)GS) continue;
                    _obstacles[nr * GS + nc] = obstacle;
                }
            }
        }
    }
}
