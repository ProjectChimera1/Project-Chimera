#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 6.3 — a Godot-free, <see cref="Fixed"/>-only terrain elevation grid: the deterministic sim-side
    /// projection of the authored Terrain3D heightmap. Built ONCE at load time by a Godot-side phase (one
    /// <see cref="Fixed.FromFloat"/> per cell — the sanctioned float→Fixed boundary), then injected into
    /// <see cref="EntityWorld"/> so every spawn path samples the SAME immutable grid.
    ///
    /// <para><b>Determinism contract.</b> <see cref="Sample"/> does a CLAMPED INTEGER CELL LOOKUP over the flat
    /// <see cref="Heights"/> array — never a floating-point / Godot <c>Image</c> interpolation, never an
    /// out-of-bounds read. All arithmetic is integer/<see cref="Fixed"/>, so the result is byte-identical across
    /// platforms. An XZ mapping to a cell outside the grid clamps to the nearest valid cell (no NaN, no exception,
    /// no desync). This is the Tier-1-testable core of the elevation feature.</para>
    ///
    /// <para>The grid carries its own world extent (<see cref="WorldMinX"/>/<see cref="WorldMinZ"/> +
    /// <see cref="CellSize"/>) so it is general over resolution and testable with tiny hand-built grids — it does
    /// NOT assume the default map's ±128 / 256×256 layout.</para>
    /// </summary>
    public sealed class ElevationGrid
    {
        /// <summary>Row-major <c>[row * Width + col]</c> per-cell terrain heights (world Y), in <see cref="Fixed"/>.</summary>
        public readonly Fixed[] Heights;

        /// <summary>Number of cells along world X (columns).</summary>
        public readonly int Width;

        /// <summary>Number of cells along world Z (rows).</summary>
        public readonly int Height;

        /// <summary>World X of the LOW edge of column 0.</summary>
        public readonly Fixed WorldMinX;

        /// <summary>World Z of the LOW edge of row 0.</summary>
        public readonly Fixed WorldMinZ;

        /// <summary>World-unit width of one cell (columns and rows share the square cell size).</summary>
        public readonly Fixed CellSize;

        /// <summary>
        /// Construct an elevation grid from a pre-baked <see cref="Fixed"/> height array and its world extent.
        /// <paramref name="heights"/> must be <c>width * height</c> long, row-major.
        /// </summary>
        public ElevationGrid(Fixed[] heights, int width, int height, Fixed worldMinX, Fixed worldMinZ, Fixed cellSize)
        {
            Heights   = heights;
            Width     = width;
            Height    = height;
            WorldMinX = worldMinX;
            WorldMinZ = worldMinZ;
            CellSize  = cellSize;
        }

        /// <summary>
        /// Deterministically sample the terrain height at a world XZ via clamped integer cell lookup. An XZ at or
        /// outside the grid edge clamps to the nearest valid cell and returns a finite <see cref="Fixed"/> — never an
        /// OOB read / NaN / exception. A degenerate (empty or bad-size) grid returns <see cref="Fixed.Zero"/>.
        /// </summary>
        public Fixed Sample(Fixed worldX, Fixed worldZ)
        {
            // Degenerate guard: an unsized grid (or a length/dims mismatch) reads as flat, never throws.
            if (Width <= 0 || Height <= 0 || CellSize.Raw <= 0 || Heights.Length < Width * Height)
                return Fixed.Zero;

            // Cell index = floor((world - worldMin) / cellSize). All Fixed/int → cross-platform deterministic.
            int col = ((worldX - WorldMinX) / CellSize).ToInt(); // ToInt() = arithmetic shift = floor
            int row = ((worldZ - WorldMinZ) / CellSize).ToInt();

            // Clamp to the nearest valid cell (the OOB/edge contract — no wraparound, no negative index).
            if (col < 0) col = 0; else if (col >= Width)  col = Width  - 1;
            if (row < 0) row = 0; else if (row >= Height) row = Height - 1;

            return Heights[row * Width + col];
        }
    }
}
