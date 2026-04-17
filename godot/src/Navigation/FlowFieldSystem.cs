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
    ///
    /// Determinism: the obstacle map is pure integer state and the BFS is deterministic,
    /// so both peers in a lockstep match will produce identical fields from identical inputs.
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

        // ── State ─────────────────────────────────────────────────────────────

        private readonly bool[]                      _obstacles = new bool[SIZE];
        private readonly FlowFieldComputer           _computer  = new FlowFieldComputer();
        private readonly Dictionary<int, FlowField>  _cache     = new Dictionary<int, FlowField>();

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
            _cache.Clear();

            for (int i = 0; i < buildings.Count; i++)
            {
                if (!buildings.Alive[i]) continue;
                MarkBuildingCells(buildings.Position[i], true);
            }
        }

        /// <summary>
        /// Mark or unmark obstacle cells for a single building centered at <paramref name="pos"/>.
        /// Pass <c>true</c> when a building is placed, <c>false</c> when it is destroyed.
        /// Automatically invalidates the field cache.
        /// </summary>
        public void SetBuildingObstacle(FixedVec3 pos, bool obstacle)
        {
            MarkBuildingCells(pos, obstacle);
            _cache.Clear();
        }

        // ── Field access ──────────────────────────────────────────────────────

        /// <summary>
        /// Return a flow field for <paramref name="goal"/>. Uses a cached field if one exists
        /// for the same goal cell, otherwise computes a new field via BFS and caches it.
        ///
        /// Multiple move commands to nearby positions sharing a cell return the same field.
        /// The cache is invalidated whenever the obstacle map changes.
        /// </summary>
        public FlowField GetOrCompute(FixedVec3 goal)
        {
            int key = FlowField.WorldToIndex(goal.X, goal.Z);

            if (!_cache.TryGetValue(key, out FlowField? field))
            {
                field = new FlowField();
                _computer.Compute(field, goal, _obstacles);
                _cache[key] = field;
            }

            return field;
        }

        /// <summary>
        /// Discard all cached flow fields without changing the obstacle map.
        /// Call this if you need to force recomputation without a building change
        /// (e.g. after terrain sculpting that affects passability).
        /// </summary>
        public void InvalidateCache() => _cache.Clear();

        /// <summary>Read-only access to the raw obstacle map (for debug visualization).</summary>
        public bool GetObstacle(int col, int row) => _obstacles[row * GS + col];

        // ── Private ───────────────────────────────────────────────────────────

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
