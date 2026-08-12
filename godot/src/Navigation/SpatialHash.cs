#nullable enable
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
        // Grid configuration. Public (read-only) so the Story 6.7 GridDimensionConsistencyTests guard can assert
        // this hash's world-space coverage encloses every supported MapSize extent (values unchanged — exposure only,
        // NOT a re-parameterization). Coverage on each axis is [ORIGIN_F, ORIGIN_F + GRID_DIM*CELL_SIZE_F) = [-160,160).
        public const int GRID_DIM = 32;          // cells per axis
        private const int TOTAL_CELLS = GRID_DIM * GRID_DIM;
        public const float CELL_SIZE_F = 10.0f;
        public const float ORIGIN_F = -160.0f;   // world-space grid origin (X and Z)

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
        /// <para>Story 9.14: an optional <paramref name="alliances"/> mask excludes ALLIED factions from acquisition
        /// (an ally is never auto-attacked). Null (bare combat tests) ⇒ only the same-faction skip applies, so the
        /// pre-9.14 behavior is byte-identical; in FFA no two distinct factions are allied, so the extra skip is a
        /// no-op there too. Neutral is never allied with a player (<c>AreAllied(Player,Neutral)==false</c>), so
        /// Neutral stays targetable.</para>
        /// </summary>
        public int FindNearestEnemy(EntityWorld world, int id, AllianceStore? alliances = null)
            => FindNearestEnemyWithin(world, id, world.AttackRange[id], alliances);

        /// <summary>
        /// DW-936 — the same nearest-enemy query at an EXPLICIT radius, for callers whose noticing distance is not
        /// their weapon range (AttackMove's WC3-style acquisition radius). <see cref="FindNearestEnemy"/> is now
        /// sugar over this at <c>AttackRange</c>, so the two can never drift. Same filters (self / same-faction /
        /// allied / domain), same strict-nearest + ascending-id tie-break.
        /// </summary>
        public int FindNearestEnemyWithin(EntityWorld world, int id, Fixed range, AllianceStore? alliances = null)
        {
            FixedVec3 pos = world.Position[id];
            Faction myFaction = world.FactionOf[id];
            Fixed sqrRange = range * range;

            int cx = WorldToCellAxis(pos.X);
            int cz = WorldToCellAxis(pos.Z);

            // Number of cells to search in each direction (range / cellSize, rounded up)
            int cellRadius = (range / CELL_SIZE).ToInt() + 1;

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
                        // Story 9.14: skip an ALLIED faction's unit (excluded from nearest-enemy acquisition). Null
                        // mask / FFA ⇒ no-op (see method doc); Neutral is never allied so it stays acquirable.
                        if (alliances != null && alliances.AreAllied(myFaction, world.FactionOf[j])) continue;
                        // Story 2.9a (AC1): honor the attacker's domain capability — prune targets it can never damage
                        // (e.g. an anti-air-only unit skips ground candidates) BEFORE the strict-nearest compare, so
                        // ascending-id tie-break stability is preserved. Integer bit-AND, no float.
                        if (!DomainClassifier.CanAttack(world.AttackDomainOf[id], world.CategoryOf[j])) continue;

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
        /// <para>Story 9.14: same optional <paramref name="alliances"/> allied-skip as <see cref="FindNearestEnemy"/>
        /// (an ally is never chased to contact); null / FFA ⇒ byte-identical to pre-9.14.</para>
        /// </summary>
        public int FindNearestEnemyGlobal(EntityWorld world, int id, AllianceStore? alliances = null)
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
                // Story 9.14: skip an ALLIED faction (never chase an ally to contact). Null / FFA ⇒ no-op.
                if (alliances != null && alliances.AreAllied(myFaction, world.FactionOf[j])) continue;
                // Story 2.9a (AC1.1): the SAME domain prune on the chase-to-contact path, so an anti-air-only unit
                // with no valid air target does NOT march toward a ground enemy it can never damage.
                if (!DomainClassifier.CanAttack(world.AttackDomainOf[id], world.CategoryOf[j])) continue;

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
        ///
        /// <para>UNORDERED and unfiltered: results come out in cell-scan order, and when more entities are in
        /// radius than the buffer holds this keeps the first buffer-full it encounters. That is deliberate for
        /// its one caller — <c>MovementSystem</c>'s separation pass, a symmetric "who is crowding me" sample.
        /// A caller that needs a PREDICATE and/or a well-defined over-cap selection (the effect-graph area
        /// search) must use <see cref="QueryRadiusLowestIds{TFilter}"/> instead.</para>
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

        /// <summary>
        /// FILTERED neighbour query that returns the globally LOWEST MATCHING entity ids inside
        /// <paramref name="radius"/> of <paramref name="pos"/>, already sorted ASCENDING. Returns the number of
        /// ids written; <c>resultBuffer[0..count)</c> is strictly ascending, so the caller needs no follow-up sort.
        ///
        /// <para>Two guarantees the unfiltered <see cref="QueryRadius"/> does not give:</para>
        /// <list type="bullet">
        ///   <item><paramref name="filter"/> is evaluated BEFORE a candidate may occupy a buffer slot, so a
        ///   rejected entity can never crowd a match out of the results. (Filtering a buffer that was already
        ///   truncated silently UNDER-selects: a 64-slot buffer that fills with the caster's own allies compacts
        ///   down to zero enemies even though enemies were standing in the radius.)</item>
        ///   <item>When more matches exist than <paramref name="resultBuffer"/> holds, the largest RETAINED id is
        ///   displaced rather than the late-scanned candidate dropped — so the kept set is the lowest-N over the
        ///   WHOLE radius, not the first-N in cell-scan order. Both peers therefore agree on the selection from
        ///   the ids alone, independent of grid geometry.</item>
        /// </list>
        ///
        /// <para>Deliberately a SEPARATE method from <see cref="QueryRadius"/> rather than a replacement:
        /// <c>MovementSystem</c>'s separation pass uses the unfiltered query and its neighbour set feeds
        /// <c>Position</c> — a <c>SimChecksum</c> input — so changing that query's truncation rule would move
        /// every movement golden for no behavioural gain.</para>
        ///
        /// <para>Allocation-free: <typeparamref name="TFilter"/> is struct-constrained, so
        /// <c>filter.Accepts</c> is a constrained (devirtualized) call — no delegate, no boxing — and the whole
        /// query stays inside the effect executor's zero-alloc contract. Deterministic: integer-id comparisons
        /// and Fixed distance only.</para>
        /// </summary>
        public int QueryRadiusLowestIds<TFilter>(EntityWorld world, FixedVec3 pos, Fixed radius, int excludeId,
                                                 int[] resultBuffer, TFilter filter)
            where TFilter : struct, IEntityQueryFilter
        {
            int maxResults = resultBuffer.Length;
            if (maxResults <= 0) return 0;

            int cx = WorldToCellAxis(pos.X);
            int cz = WorldToCellAxis(pos.Z);
            int cellRadius = (radius / CELL_SIZE).ToInt() + 1;
            Fixed sqrRadius = radius * radius;
            int count = 0;

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

                    // NOTE the missing `count < maxResults` loop bound (which QueryRadius has): a full buffer
                    // must NOT end the scan here, because a later candidate may still be lower-id than the
                    // largest id currently held. That is exactly what makes the selection global.
                    for (int k = 0; k < cellCount; k++)
                    {
                        int j = _sortedIds[start + k];
                        if (j == excludeId) continue;
                        if (FixedVec3.SqrDistance(pos, world.Position[j]) > sqrRadius) continue;
                        if (!filter.Accepts(world, j)) continue;

                        if (count == maxResults)
                        {
                            // Buffer full — resultBuffer[count-1] is the largest id held. Keep the lowest ids:
                            // ignore this candidate if it is not lower, else evict the largest (the shift below
                            // overwrites it) and insert.
                            if (j >= resultBuffer[count - 1]) continue;
                            count--;
                        }

                        // Insertion into the ascending prefix. Entity ids are unique, so > is a TOTAL order
                        // (no equal keys ⇒ nothing to reorder ⇒ deterministic, and CHM0003-free by construction:
                        // this is an explicit insertion, not an unstable library sort).
                        int p = count;
                        while (p > 0 && resultBuffer[p - 1] > j)
                        {
                            resultBuffer[p] = resultBuffer[p - 1];
                            p--;
                        }
                        resultBuffer[p] = j;
                        count++;
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
