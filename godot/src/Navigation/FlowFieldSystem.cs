using System.Collections.Generic;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Manages the obstacle map used for flow field pathfinding and caches computed fields.
    ///
    /// Obstacle map: a bool[] (128×128) where true = impassable cell.
    /// Building footprint: each 4×4-world-unit building marks a 3×3-cell area as blocked,
    /// providing clearance so units path comfortably around structures.
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
        /// Half-extent (in cells) of the obstacle footprint for each building.
        /// 1 → 3×3 cells = 6×6 world units; provides clearance around 4×4 buildings.
        /// </summary>
        private const int BUILDING_HALF_CELLS = 1;

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

            for (int i = 0; i < buildings.Count; i++)
            {
                if (!buildings.Alive[i]) continue;
                MarkBuildingCells(buildings.Position[i], true);
            }
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
        /// </summary>
        public void SetBuildingObstacle(FixedVec3 pos, bool obstacle)
        {
            MarkBuildingCells(pos, obstacle);
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
        /// Mark a BUILDING_HALF_CELLS × 2 + 1 square of cells around the building center.
        /// Does NOT invalidate the cache — callers are responsible for that.
        /// </summary>
        private void MarkBuildingCells(FixedVec3 pos, bool obstacle)
        {
            FlowField.WorldToCell(pos.X, pos.Z, out int cc, out int cr);

            for (int dc = -BUILDING_HALF_CELLS; dc <= BUILDING_HALF_CELLS; dc++)
            {
                for (int dr = -BUILDING_HALF_CELLS; dr <= BUILDING_HALF_CELLS; dr++)
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
