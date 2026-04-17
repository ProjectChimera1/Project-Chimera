namespace ProjectChimera.Core
{
    /// <summary>
    /// Fog of War simulation system.
    ///
    /// Maintains a 128×128 byte grid covering the playable area.
    /// Cell states:
    ///   0 = Unexplored  (black)
    ///   1 = Explored    (dark tint — seen before, not currently visible)
    ///   2 = Visible     (fully lit — at least one friendly unit has line of sight)
    ///
    /// Each tick:
    ///   1. Demote all Visible → Explored.
    ///   2. For each alive P1 unit, stamp a circle of radius VisionRange as Visible.
    ///
    /// The grid is exposed as a public byte array; FogOfWarBridge uploads it to GPU.
    ///
    /// World bounds: X ∈ [-128, +128], Z ∈ [-128, +128]  →  256×256 world units total.
    /// Each cell = 2 world units wide.
    /// </summary>
    public class FogOfWarSystem : ISimSystem
    {
        // ── Grid configuration ────────────────────────────────────────────────

        public const int   GRID_SIZE        = 128;
        public const float WORLD_HALF_EXTENT = 128f; // world coords: -128 … +128
        public const float CELL_SIZE         = WORLD_HALF_EXTENT * 2f / GRID_SIZE; // = 2.0

        public const byte UNEXPLORED = 0;
        public const byte EXPLORED   = 1;
        public const byte VISIBLE    = 2;

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Row-major [row * GRID_SIZE + col] byte array of cell states.
        /// Uploaded to GPU by FogOfWarBridge each frame.
        /// </summary>
        public readonly byte[] Grid = new byte[GRID_SIZE * GRID_SIZE];

        /// <summary>Which faction this fog instance reveals for.</summary>
        private readonly Faction _faction;

        public FogOfWarSystem(Faction faction = Faction.Player1)
        {
            _faction = faction;
        }

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            // Step 1: demote Visible → Explored
            for (int i = 0; i < Grid.Length; i++)
                if (Grid[i] == VISIBLE) Grid[i] = EXPLORED;

            // Step 2: stamp vision circles for each alive friendly unit
            int cap = world.HighWaterMark;
            for (int id = 0; id < cap; id++)
            {
                if ((world.Flags[id] & EntityFlags.Alive) == 0) continue;
                if (world.FactionOf[id] != _faction) continue;

                float wx = world.Position[id].X.ToFloat();
                float wz = world.Position[id].Z.ToFloat();
                float radius = world.VisionRange[id].ToFloat();

                StampCircle(wx, wz, radius);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void StampCircle(float worldX, float worldZ, float radius)
        {
            // Convert world-space centre to grid coords
            float cx = (worldX + WORLD_HALF_EXTENT) / CELL_SIZE;
            float cz = (worldZ + WORLD_HALF_EXTENT) / CELL_SIZE;

            int cellRadius = (int)(radius / CELL_SIZE) + 1;
            int minRow = System.Math.Max(0, (int)(cz - cellRadius));
            int maxRow = System.Math.Min(GRID_SIZE - 1, (int)(cz + cellRadius));
            int minCol = System.Math.Max(0, (int)(cx - cellRadius));
            int maxCol = System.Math.Min(GRID_SIZE - 1, (int)(cx + cellRadius));

            float radiusSqr = (radius / CELL_SIZE) * (radius / CELL_SIZE);

            for (int row = minRow; row <= maxRow; row++)
            {
                float dz = row + 0.5f - cz;
                float dzSqr = dz * dz;
                if (dzSqr > radiusSqr) continue;

                float dxMax = System.MathF.Sqrt(radiusSqr - dzSqr);
                int colStart = System.Math.Max(minCol, (int)(cx - dxMax));
                int colEnd   = System.Math.Min(maxCol, (int)(cx + dxMax));

                for (int col = colStart; col <= colEnd; col++)
                    Grid[row * GRID_SIZE + col] = VISIBLE;
            }
        }

        /// <summary>Returns the grid cell (col, row) for a given world position.</summary>
        public static (int col, int row) WorldToCell(float worldX, float worldZ)
        {
            int col = (int)((worldX + WORLD_HALF_EXTENT) / CELL_SIZE);
            int row = (int)((worldZ + WORLD_HALF_EXTENT) / CELL_SIZE);
            col = System.Math.Clamp(col, 0, GRID_SIZE - 1);
            row = System.Math.Clamp(row, 0, GRID_SIZE - 1);
            return (col, row);
        }

        /// <summary>Returns true if the given world position is currently visible.</summary>
        public bool IsVisible(float worldX, float worldZ)
        {
            var (col, row) = WorldToCell(worldX, worldZ);
            return Grid[row * GRID_SIZE + col] == VISIBLE;
        }

        /// <summary>Returns true if the cell has been explored (seen at some point).</summary>
        public bool IsExplored(float worldX, float worldZ)
        {
            var (col, row) = WorldToCell(worldX, worldZ);
            return Grid[row * GRID_SIZE + col] >= EXPLORED;
        }
    }
}
