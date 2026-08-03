#nullable enable
using System;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.6 — the checksummed Layer-3 runtime state: the per-tick DSL fuel counter plus the
    /// <c>for_each_batched</c> continuation rows. Pure sim-layer C# (Godot-free, float-free, int state only).
    /// Owned by <c>SimulationHost</c> (a sibling of <see cref="DslVarTable"/>), driven by <c>ScenarioDirector</c>,
    /// folded into <c>SimChecksum</c> (v17) AFTER the variable table and BEFORE the SimRng fold.
    ///
    /// Continuation rows are ALLOCATED AT LOAD (<see cref="ConfigureRows"/> — one row per for_each_batched node,
    /// ascending node id, each with a preallocated <c>DslBounds.MaxBatchSnapshot</c>-sized id buffer) so the drain
    /// path performs zero per-tick heap allocation. Rows are kept ONE FLAT ROW per node — batched loops are
    /// top-level-only / one-per-trigger by the load gate, so no nested-resume machinery exists.
    ///
    /// Fuel: <see cref="ResetFuel"/> runs at the START of every director tick; <see cref="Charge"/> mirrors the
    /// static load-gate cost model (action = 1, expression = op count, run_effect = embedded node count with
    /// SearchArea child subtrees weighted by <c>EffectCaps.MaxSearchTargets</c> — DW-347, loop / branch entry
    /// = 1). <see cref="FuelExhausted"/> (consumed ≥ <see cref="DslBounds.MaxDslOpsPerTick"/>) is
    /// checked only at whole-trigger / whole-drain-row boundaries, so exhaustion never tears a trigger.
    /// </summary>
    public sealed class DslLoopState
    {
        // ── Batched continuation rows (SoA, ascending for_each_batched node id) ──
        private int    _rowCount;
        private int[]  _rowNodeId  = Array.Empty<int>();  // the for_each_batched node's persistent graph id
        private bool[] _rowActive  = Array.Empty<bool>(); // a fired snapshot is draining
        private int[]  _rowCursor  = Array.Empty<int>();  // next snapshot index to drain
        private int[]  _rowLen     = Array.Empty<int>();  // live snapshot length
        private int[]  _rowIds     = Array.Empty<int>();  // flat [row * MaxBatchSnapshot + k] snapshot entity ids

        /// <summary>Ops consumed THIS director tick (reset at tick start; folded into <c>SimChecksum</c>).</summary>
        public int FuelConsumed { get; private set; }

        /// <summary>Number of configured continuation rows (one per for_each_batched node).</summary>
        public int RowCount => _rowCount;

        /// <summary>
        /// (Re)allocate the continuation rows for a freshly-loaded scenario — one per <c>for_each_batched</c>
        /// node, in ASCENDING node-id order (<paramref name="nodeIdsAscending"/> must already be sorted; the
        /// drain phase walks rows in this order). Load-time only; clears all prior state.
        /// </summary>
        public void ConfigureRows(int[] nodeIdsAscending)
        {
            _rowCount  = nodeIdsAscending.Length;
            _rowNodeId = new int[_rowCount];
            _rowActive = new bool[_rowCount];
            _rowCursor = new int[_rowCount];
            _rowLen    = new int[_rowCount];
            _rowIds    = new int[_rowCount * DslBounds.MaxBatchSnapshot];
            Array.Copy(nodeIdsAscending, _rowNodeId, _rowCount);
            FuelConsumed = 0;
        }

        /// <summary>Reset to the empty (no rows, zero fuel) state — the Edit↔Play / host reset path.</summary>
        public void Clear()
        {
            _rowCount = 0;
            _rowNodeId = Array.Empty<int>();
            _rowActive = Array.Empty<bool>();
            _rowCursor = Array.Empty<int>();
            _rowLen    = Array.Empty<int>();
            _rowIds    = Array.Empty<int>();
            FuelConsumed = 0;
        }

        // ── Fuel ──────────────────────────────────────────────────────────────

        /// <summary>Reset the per-tick fuel counter (called at the start of every director tick).</summary>
        public void ResetFuel() => FuelConsumed = 0;

        /// <summary>Charge <paramref name="ops"/> ops against this tick's budget (saturating — never wraps).</summary>
        public void Charge(int ops)
        {
            long next = (long)FuelConsumed + ops;
            FuelConsumed = next > int.MaxValue ? int.MaxValue : (int)next;
        }

        /// <summary>True once this tick's consumption reaches <see cref="DslBounds.MaxDslOpsPerTick"/> — checked
        /// only at whole-trigger / whole-drain-row boundaries (never mid-trigger).</summary>
        public bool FuelExhausted => FuelConsumed >= DslBounds.MaxDslOpsPerTick;

        // ── Continuation-row access (director-driven) ─────────────────────────

        /// <summary>True while row <paramref name="row"/> is draining a fired snapshot.</summary>
        public bool RowActive(int row) => _rowActive[row];

        /// <summary>Row <paramref name="row"/>'s next-to-drain snapshot index.</summary>
        public int RowCursor(int row) => _rowCursor[row];

        /// <summary>Row <paramref name="row"/>'s live snapshot length.</summary>
        public int RowLength(int row) => _rowLen[row];

        /// <summary>The snapshot entity id at (<paramref name="row"/>, <paramref name="index"/>).</summary>
        public int RowId(int row, int index) => _rowIds[row * DslBounds.MaxBatchSnapshot + index];

        /// <summary>Begin a fresh snapshot on <paramref name="row"/> (called when its trigger fires): activates
        /// the row with cursor 0 and length 0; the caller appends ids via <see cref="SnapshotAppend"/> in
        /// ascending entity-id order.</summary>
        public void BeginSnapshot(int row)
        {
            _rowActive[row] = true;
            _rowCursor[row] = 0;
            _rowLen[row]    = 0;
        }

        /// <summary>Append one entity id to <paramref name="row"/>'s snapshot. Returns false (deterministic
        /// truncation to the LOWEST ids — the caller scans ascending) once <see cref="DslBounds.MaxBatchSnapshot"/>
        /// is reached.</summary>
        public bool SnapshotAppend(int row, int entityId)
        {
            if (_rowLen[row] >= DslBounds.MaxBatchSnapshot) return false;
            _rowIds[row * DslBounds.MaxBatchSnapshot + _rowLen[row]] = entityId;
            _rowLen[row]++;
            return true;
        }

        /// <summary>Advance <paramref name="row"/>'s cursor after draining up to it (clamped to [0, length] —
        /// the low clamp is a deterministic defensive floor; the drain path never passes a negative).</summary>
        public void SetCursor(int row, int cursor) =>
            _rowCursor[row] = cursor < 0 ? 0 : (cursor > _rowLen[row] ? _rowLen[row] : cursor);

        /// <summary>Deactivate <paramref name="row"/> (its drain completed and the continuation ran).</summary>
        public void CompleteRow(int row) => _rowActive[row] = false;

        // ── Checksum fold ─────────────────────────────────────────────────────

        /// <summary>
        /// Fold the loop-layer state into the running FNV-1a <paramref name="hash"/>: a leading row count; per
        /// row (ascending node-id order) active flag, cursor, length, then every live snapshot id in ascending
        /// index; then the fuel consumed this tick. Uses the caller's <paramref name="mix"/> primitive so the
        /// fold shares <c>SimChecksum.Mix</c>.
        /// </summary>
        public void FoldInto(ref uint hash, Func<uint, int, uint> mix)
        {
            hash = mix(hash, _rowCount);
            for (int r = 0; r < _rowCount; r++)
            {
                hash = mix(hash, _rowActive[r] ? 1 : 0);
                hash = mix(hash, _rowCursor[r]);
                hash = mix(hash, _rowLen[r]);
                int rowBase = r * DslBounds.MaxBatchSnapshot;
                for (int k = 0; k < _rowLen[r]; k++)
                    hash = mix(hash, _rowIds[rowBase + k]);
            }
            hash = mix(hash, FuelConsumed);
        }

        /// <summary>Fold BYTE-IDENTICALLY to an empty state's <see cref="FoldInto"/> (a 0 row count + a 0 fuel
        /// value) so a NULL store and a non-null EMPTY store are interchangeable in <c>SimChecksum</c>.</summary>
        public static void FoldEmpty(ref uint hash, Func<uint, int, uint> mix)
        {
            hash = mix(hash, 0); // 0 rows (matches FoldInto's leading row count on an empty state)
            hash = mix(hash, 0); // 0 fuel consumed
        }
    }
}
