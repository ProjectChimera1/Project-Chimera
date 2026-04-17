using System;
using ProjectChimera.Core;

namespace ProjectChimera.Navigation
{
    /// <summary>
    /// Allocation-free fixed-grid spatial hash for fast neighbor queries.
    /// Rebuilt each simulation tick via a two-pass counting sort — no heap allocations.
    ///
    /// Grid: 32×32 cells, 10 units/cell → covers ±160 units from origin.
    /// Query cost: O(k) where k = entities in neighboring cells (typically &lt;&lt; n).
    /// </summary>
    public class SpatialHash
    {
        // Grid configuration
        private const int GRID_DIM = 32;          // cells per axis
        private const int TOTAL_CELLS = GRID_DIM * GRID_DIM;
        private const float CELL_SIZE_F = 10.0f;
        private const float ORIGIN_F = -160.0f;   // world-space grid origin (X and Z)

        private static readonly Fixed CELL_SIZE = Fixed.FromFloat(CELL_SIZE_F);
        private static readonly Fixed ORIGIN = Fixed.FromFloat(ORIGIN_F);

        // Pre-allocated buffers — zero heap per tick
        private readonly int[] _cellCount = new int[TOTAL_CELLS];
        private readonly int[] _cellStart = new int[TOTAL_CELLS];
        private readonly int[] _sortedIds = new int[EntityWorld.MAX_ENTITIES];
        private readonly int[] _entityCell = new int[EntityWorld.MAX_ENTITIES];

        /// <summary>
        /// Rebuild the spatial hash from current entity positions.
        /// Call once at the start of each simulation tick before any queries.
        /// </summary>
        public void Rebuild(EntityWorld world)
        {
            Array.Clear(_cellCount, 0, TOTAL_CELLS);

            int cap = world.HighWaterMark;

            // Pass 1: assign each entity to a cell, accumulate counts
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) { _entityCell[i] = -1; continue; }
                int cell = WorldToCell(world.Position[i]);
                _entityCell[i] = cell;
                if (cell >= 0) _cellCount[cell]++;
            }

            // Pass 2: prefix sum → start indices
            int running = 0;
            for (int c = 0; c < TOTAL_CELLS; c++)
            {
                _cellStart[c] = running;
                running += _cellCount[c];
            }

            // Reset counts for insertion pass
            Array.Clear(_cellCount, 0, TOTAL_CELLS);

            // Pass 3: fill sorted array
            for (int i = 0; i < cap; i++)
            {
                int cell = _entityCell[i];
                if (cell < 0) continue;
                _sortedIds[_cellStart[cell] + _cellCount[cell]] = i;
                _cellCount[cell]++;
            }
        }

        /// <summary>
        /// Find the nearest alive enemy to entity <paramref name="id"/> within its attack range.
        /// Returns the enemy entity ID, or -1 if none found.
        /// </summary>
        public int FindNearestEnemy(EntityWorld world, int id)
        {
            FixedVec3 pos = world.Position[id];
            Faction myFaction = world.FactionOf[id];
            Fixed sqrRange = world.AttackRange[id] * world.AttackRange[id];

            int cx = WorldToCellAxis(pos.X);
            int cz = WorldToCellAxis(pos.Z);

            // Number of cells to search in each direction (range / cellSize, rounded up)
            int cellRadius = (world.AttackRange[id] / CELL_SIZE).ToInt() + 1;

            Fixed bestSqrDist = Fixed.MaxValue;
            int bestId = -1;

            for (int dz = -cellRadius; dz <= cellRadius; dz++)
            {
                int ncz = cz + dz;
                if (ncz < 0 || ncz >= GRID_DIM) continue;

                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int ncx = cx + dx;
                    if (ncx < 0 || ncx >= GRID_DIM) continue;

                    int cell = ncz * GRID_DIM + ncx;
                    int start = _cellStart[cell];
                    int count = _cellCount[cell];

                    for (int k = 0; k < count; k++)
                    {
                        int j = _sortedIds[start + k];
                        if (j == id) continue;
                        if (world.FactionOf[j] == myFaction) continue;

                        Fixed sqrDist = FixedVec3.SqrDistance(pos, world.Position[j]);
                        if (sqrDist <= sqrRange && sqrDist < bestSqrDist)
                        {
                            bestSqrDist = sqrDist;
                            bestId = j;
                        }
                    }
                }
            }

            return bestId;
        }

        /// <summary>
        /// Find the nearest alive enemy to entity <paramref name="id"/> across the entire grid,
        /// ignoring attack range. Used for "advance to contact" when no enemy is in range.
        /// Returns the enemy entity ID, or -1 if no enemies exist.
        /// </summary>
        public int FindNearestEnemyGlobal(EntityWorld world, int id)
        {
            FixedVec3 pos = world.Position[id];
            Faction myFaction = world.FactionOf[id];
            Fixed bestSqrDist = Fixed.MaxValue;
            int bestId = -1;
            int cap = world.HighWaterMark;

            for (int j = 0; j < cap; j++)
            {
                if (j == id) continue;
                if (!world.IsAlive(j)) continue;
                if (world.FactionOf[j] == myFaction) continue;

                Fixed sqrDist = FixedVec3.SqrDistance(pos, world.Position[j]);
                if (sqrDist < bestSqrDist)
                {
                    bestSqrDist = sqrDist;
                    bestId = j;
                }
            }

            return bestId;
        }

        /// <summary>
        /// Fill <paramref name="resultBuffer"/> with IDs of alive entities within
        /// <paramref name="radius"/> of <paramref name="pos"/>, excluding <paramref name="excludeId"/>.
        /// Returns the number of results written (capped at resultBuffer.Length).
        /// </summary>
        public int QueryRadius(EntityWorld world, FixedVec3 pos, Fixed radius, int excludeId, int[] resultBuffer)
        {
            int cx = WorldToCellAxis(pos.X);
            int cz = WorldToCellAxis(pos.Z);
            int cellRadius = (radius / CELL_SIZE).ToInt() + 1;
            Fixed sqrRadius = radius * radius;
            int count = 0;
            int maxResults = resultBuffer.Length;

            for (int dz = -cellRadius; dz <= cellRadius; dz++)
            {
                int ncz = cz + dz;
                if (ncz < 0 || ncz >= GRID_DIM) continue;

                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int ncx = cx + dx;
                    if (ncx < 0 || ncx >= GRID_DIM) continue;

                    int cell = ncz * GRID_DIM + ncx;
                    int start = _cellStart[cell];
                    int cellCount = _cellCount[cell];

                    for (int k = 0; k < cellCount && count < maxResults; k++)
                    {
                        int j = _sortedIds[start + k];
                        if (j == excludeId) continue;
                        Fixed sqrDist = FixedVec3.SqrDistance(pos, world.Position[j]);
                        if (sqrDist <= sqrRadius)
                            resultBuffer[count++] = j;
                    }
                }
            }

            return count;
        }

        private static int WorldToCell(FixedVec3 pos)
        {
            int cx = WorldToCellAxis(pos.X);
            int cz = WorldToCellAxis(pos.Z);
            if (cx < 0 || cx >= GRID_DIM || cz < 0 || cz >= GRID_DIM) return -1;
            return cz * GRID_DIM + cx;
        }

        private static int WorldToCellAxis(Fixed coord)
        {
            return ((coord - ORIGIN) / CELL_SIZE).ToInt();
        }
    }
}
