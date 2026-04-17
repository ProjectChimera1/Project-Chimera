using System;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// A computed flow field for a single destination.
    /// Each grid cell stores a pre-normalized direction vector pointing toward the goal.
    ///
    /// Grid specs (mirror FogOfWarSystem exactly):
    ///   - 128×128 cells, 2 world units per cell
    ///   - Coverage: X ∈ [-128, +128], Z ∈ [-128, +128]
    ///
    /// Sampling is deterministic: WorldToCell uses only integer arithmetic.
    /// All direction vectors are precomputed Fixed constants (no per-sample sqrt).
    ///
    /// Usage:
    ///   int  idx = FlowField.WorldToIndex(wx, wz);
    ///   var  dir = field.Directions[idx];          // steering direction (or Zero if at goal)
    ///   bool done = field.HasArrived(wx, wz);      // true when within arrival radius of goal
    /// </summary>
    public sealed class FlowField
    {
        // ── Grid constants (match FogOfWarSystem) ─────────────────────────────

        /// <summary>Grid cells along each axis (128×128 total).</summary>
        public const int GRID_SIZE        = 128;

        /// <summary>World units per cell (2).</summary>
        public const int CELL_SIZE_WORLD  = 2;

        /// <summary>World half-extent in integer units (±128).</summary>
        public const int WORLD_HALF_INT   = 128;

        /// <summary>Total cell count.</summary>
        public const int CELL_COUNT       = GRID_SIZE * GRID_SIZE;

        // Squared arrival threshold: 1.5 world units, matching PathRequestSystem.WAYPOINT_REACH_SQR.
        private static readonly Fixed ARRIVE_SQR =
            Fixed.FromFloat(1.5f) * Fixed.FromFloat(1.5f);

        // ── Per-cell data ──────────────────────────────────────────────────────

        /// <summary>
        /// Normalized direction vectors, indexed [row * GRID_SIZE + col].
        /// FixedVec3.Zero = goal area (unit has arrived) or unreachable cell.
        /// Direction always points XZ-only (Y component is always zero).
        /// </summary>
        public readonly FixedVec3[] Directions = new FixedVec3[CELL_COUNT];

        /// <summary>Goal position in world space (XZ plane).</summary>
        public FixedVec3 GoalWorld;

        // ── Grid coordinate math ───────────────────────────────────────────────

        /// <summary>
        /// Convert world (wx, wz) to grid (col, row), clamped to [0, GRID_SIZE-1].
        /// Uses integer-only arithmetic: deterministic across all platforms.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WorldToCell(Fixed wx, Fixed wz, out int col, out int row)
        {
            // ToInt() = Raw >> 16 (truncate toward zero — deterministic)
            int ix = wx.ToInt() + WORLD_HALF_INT;
            int iz = wz.ToInt() + WORLD_HALF_INT;
            col = Math.Clamp(ix / CELL_SIZE_WORLD, 0, GRID_SIZE - 1);
            row = Math.Clamp(iz / CELL_SIZE_WORLD, 0, GRID_SIZE - 1);
        }

        /// <summary>Returns the flat [row * GRID_SIZE + col] index for a world position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WorldToIndex(Fixed wx, Fixed wz)
        {
            WorldToCell(wx, wz, out int col, out int row);
            return row * GRID_SIZE + col;
        }

        /// <summary>Returns the world-space center of cell (col, row) on the XZ plane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FixedVec3 CellCenter(int col, int row)
        {
            // center = (col * 2 + 1) - 128, 0, (row * 2 + 1) - 128
            return new FixedVec3(
                Fixed.FromInt(col * CELL_SIZE_WORLD + 1 - WORLD_HALF_INT),
                Fixed.Zero,
                Fixed.FromInt(row * CELL_SIZE_WORLD + 1 - WORLD_HALF_INT));
        }

        // ── Sampling ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the steering direction for a unit at world position (wx, wz).
        /// Zero = unit is in the goal area or the cell is unreachable.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FixedVec3 Sample(Fixed wx, Fixed wz)
            => Directions[WorldToIndex(wx, wz)];

        /// <summary>
        /// True when the unit at (wx, wz) is within 1.5 world units of the goal.
        /// Same threshold as PathRequestSystem.WAYPOINT_REACH_SQR.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasArrived(Fixed wx, Fixed wz)
        {
            Fixed dx = wx - GoalWorld.X;
            Fixed dz = wz - GoalWorld.Z;
            return dx * dx + dz * dz <= ARRIVE_SQR;
        }
    }
}
