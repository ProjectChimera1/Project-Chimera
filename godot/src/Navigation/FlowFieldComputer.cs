using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Computes a FlowField for a given goal position using BFS from the goal outward.
    /// Pure C# — no Godot dependency. Deterministic.
    ///
    /// Algorithm:
    ///   1. Seed the BFS with the 3×3 area around the goal cell (all passable cells, cost 0).
    ///      This ensures units spawned adjacent to an obstacle can still reach a target there.
    ///   2. BFS outward in 8-connected order: each cell gets cost = parent cost + 1.
    ///   3. Each cell stores the direction pointing back toward its BFS parent (lower cost).
    ///   4. Obstacle cells and already-visited cells are skipped.
    ///
    /// Direction vectors for all 8 neighbors are precomputed FixedVec3 constants (no per-cell
    /// sqrt). Scratch arrays (_cost, _queue) are reused across calls to avoid GC pressure.
    ///
    /// Performance: 128×128 = 16 384 cells. A full BFS completes in ~0.1–0.5 ms.
    /// Fields are cached by FlowFieldSystem; recomputation only happens when a new destination
    /// or an obstacle change is encountered.
    /// </summary>
    public sealed class FlowFieldComputer
    {
        // ── Precomputed neighbor table ─────────────────────────────────────────
        // Each entry: (delta-col, delta-row, direction from parent→neighbor).
        // Stored direction is from PARENT toward NEIGHBOR; we negate it when writing
        // to the neighbor's Directions[] slot so the unit steers back toward the parent.

        private static readonly (int dc, int dr, FixedVec3 dir)[] s_neighbors;

        static FlowFieldComputer()
        {
            // 1 / sqrt(2) ≈ 0.7071068 — precomputed Fixed constant for diagonal normalization
            Fixed inv2 = Fixed.FromFloat(0.7071068f);
            Fixed pos  = Fixed.One;
            Fixed neg  = Fixed.NegOne;

            s_neighbors = new[]
            {
                // ── Cardinals ──────────────────────────────────────────────────
                //  dc   dr    direction (col axis = X, row axis = Z)
                (  0, -1, new FixedVec3(Fixed.Zero, Fixed.Zero, neg  )),  // North  (−Z)
                (  1,  0, new FixedVec3(pos,        Fixed.Zero, Fixed.Zero)), // East   (+X)
                (  0,  1, new FixedVec3(Fixed.Zero, Fixed.Zero, pos  )),  // South  (+Z)
                ( -1,  0, new FixedVec3(neg,        Fixed.Zero, Fixed.Zero)), // West   (−X)

                // ── Diagonals (pre-normalized) ─────────────────────────────────
                (  1, -1, new FixedVec3( inv2, Fixed.Zero, -inv2)),  // NE
                (  1,  1, new FixedVec3( inv2, Fixed.Zero,  inv2)),  // SE
                ( -1,  1, new FixedVec3(-inv2, Fixed.Zero,  inv2)),  // SW
                ( -1, -1, new FixedVec3(-inv2, Fixed.Zero, -inv2)),  // NW
            };
        }

        // ── Scratch arrays (reused per Compute() call — no per-call allocation) ──

        private readonly int[] _cost  = new int[FlowField.CELL_COUNT]; // BFS cost per cell
        private readonly int[] _queue = new int[FlowField.CELL_COUNT]; // BFS FIFO queue (max one entry per cell)

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Fill <paramref name="field"/> with directions that steer units toward
        /// <paramref name="goal"/>. <paramref name="obstacles"/> is indexed
        /// [row * GRID_SIZE + col]; true = impassable cell.
        ///
        /// After this call, <paramref name="field.Directions"/>[] and
        /// <paramref name="field.GoalWorld"/> are set; Cost[] is not exposed externally
        /// (it lives in this computer's scratch buffer for reuse).
        /// </summary>
        public void Compute(FlowField field, FixedVec3 goal, bool[] obstacles)
        {
            int gs   = FlowField.GRID_SIZE;
            int size = FlowField.CELL_COUNT;

            // ── Reset scratch and output ──────────────────────────────────────
            for (int i = 0; i < size; i++)
            {
                _cost[i]             = int.MaxValue; // unvisited
                field.Directions[i]  = FixedVec3.Zero;
            }

            field.GoalWorld = goal;

            // ── Seed BFS: 3×3 area around goal cell (cost = 0) ───────────────
            // Seeding a 3×3 region means that a unit right next to a walled-off
            // target can still find the field (adjacent passable cells are reachable).
            FlowField.WorldToCell(goal.X, goal.Z, out int gc, out int gr);

            int head = 0, tail = 0;

            for (int dc = -1; dc <= 1; dc++)
            {
                for (int dr = -1; dr <= 1; dr++)
                {
                    int sc = gc + dc;
                    int sr = gr + dr;
                    if ((uint)sc >= (uint)gs || (uint)sr >= (uint)gs) continue;

                    int idx = sr * gs + sc;
                    if (obstacles[idx] || _cost[idx] != int.MaxValue) continue;

                    _cost[idx]            = 0;
                    field.Directions[idx] = FixedVec3.Zero; // goal area — already arrived
                    _queue[tail++]        = idx;
                }
            }

            // ── BFS ───────────────────────────────────────────────────────────
            while (head < tail)
            {
                int  cellIdx = _queue[head++];
                int  col     = cellIdx % gs;
                int  row     = cellIdx / gs;
                int  pCost   = _cost[cellIdx];

                foreach (var (dc, dr, parentToNeighborDir) in s_neighbors)
                {
                    int nc = col + dc;
                    int nr = row + dr;
                    if ((uint)nc >= (uint)gs || (uint)nr >= (uint)gs) continue;

                    int nIdx = nr * gs + nc;
                    if (obstacles[nIdx] || _cost[nIdx] != int.MaxValue) continue;

                    _cost[nIdx] = pCost + 1;

                    // Direction from the neighbor BACK toward its parent (lower cost = toward goal).
                    // parentToNeighborDir is parent→neighbor, so negate it.
                    field.Directions[nIdx] = new FixedVec3(
                        -parentToNeighborDir.X,
                        Fixed.Zero,
                        -parentToNeighborDir.Z);

                    _queue[tail++] = nIdx;
                }
            }
        }
    }
}
