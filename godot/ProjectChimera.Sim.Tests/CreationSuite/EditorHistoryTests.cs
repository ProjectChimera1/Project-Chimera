#nullable enable
using System.Collections.Generic;
using ProjectChimera.CreationSuite;
using Xunit;

namespace ProjectChimera.Sim.Tests.CreationSuite
{
    /// <summary>
    /// Story 6.2 — the shared <see cref="EditorHistory"/> is now driven by BOTH entity placement (EntityPlacer) and
    /// terrain sculpt/paint strokes (TerrainBrush). This pins the LIFO contract those two op kinds rely on to
    /// interleave without cross-corruption. (The Terrain3D-node snapshot/restore itself has no Godot-free surface and
    /// is covered by the live godot-mcp checks; here we prove the stack semantics the wiring depends on.)
    /// </summary>
    public class EditorHistoryTests
    {
        [Fact]
        public void InterleavedEntityAndTerrainOps_UndoRedoInStrictLifo()
        {
            var h   = new EditorHistory();
            var log = new List<string>();

            // Place a unit (entity op), then a terrain stroke (terrain op) — both push onto ONE stack.
            h.Push(redo: () => log.Add("entity:redo"),  undo: () => log.Add("entity:undo"));
            h.Push(redo: () => log.Add("terrain:redo"), undo: () => log.Add("terrain:undo"));

            h.Undo(); // terrain first (LIFO)
            h.Undo(); // then entity
            Assert.Equal(new[] { "terrain:undo", "entity:undo" }, log);

            log.Clear();
            h.Redo(); // entity comes back first
            h.Redo(); // then terrain
            Assert.Equal(new[] { "entity:redo", "terrain:redo" }, log);
        }

        [Fact]
        public void PushAfterUndo_ClearsRedoableFuture()
        {
            var h = new EditorHistory();
            h.Push(redo: () => { }, undo: () => { });
            h.Undo();
            Assert.True(h.CanRedo);

            // A new op (e.g. a terrain stroke after undoing an entity placement) discards the redoable future.
            h.Push(redo: () => { }, undo: () => { });
            Assert.False(h.CanRedo);
            Assert.True(h.CanUndo);
        }

        [Fact]
        public void UndoRedo_OnEmptyStack_AreNoOps()
        {
            var h = new EditorHistory();
            h.Undo();
            h.Redo();
            Assert.False(h.CanUndo);
            Assert.False(h.CanRedo);
        }

        /// <summary>
        /// Story 6.6 — a multi-select GROUP op (copy/paste, group-move, group-delete/duplicate spanning several
        /// placements across categories) is ONE (redo, undo) pair on the shared stack, so it undoes/redoes as a
        /// SINGLE step and interleaves LIFO with a single-entity op with no cross-corruption. Models the group op as
        /// a closure that mutates a list of "placed" ids (the Godot-free mutation surface the EntityPlacer group ops
        /// drive; the MultiMesh/scene side is covered by the live godot-mcp checks).
        /// </summary>
        [Fact]
        public void GroupOp_IsOneUndoStep_AndInterleavesWithSingleOp()
        {
            var h = new EditorHistory();
            var placed = new HashSet<int>();

            // A single-entity place (one prop).
            h.Push(redo: () => placed.Add(1), undo: () => placed.Remove(1));
            placed.Add(1);

            // A GROUP paste of three placements (2 units + 1 prop, say) — ONE pair, mutating all three.
            int[] group = { 10, 11, 12 };
            void GroupRedo() { foreach (int id in group) placed.Add(id); }
            void GroupUndo() { foreach (int id in group) placed.Remove(id); }
            h.Push(redo: GroupRedo, undo: GroupUndo);
            GroupRedo();

            Assert.Equal(new[] { 1, 10, 11, 12 }, Sorted(placed));

            // One undo removes the ENTIRE group as a single step (LIFO — the group was pushed last).
            h.Undo();
            Assert.Equal(new[] { 1 }, Sorted(placed));

            // The single-entity op undoes independently after it.
            h.Undo();
            Assert.Empty(placed);

            // Redo restores the single op first, then the whole group in one step.
            h.Redo();
            Assert.Equal(new[] { 1 }, Sorted(placed));
            h.Redo();
            Assert.Equal(new[] { 1, 10, 11, 12 }, Sorted(placed));
        }

        // ── DW-138: Clear() wipes both stacks ────────────────────────────────────────────────────────────────────
        /// <summary>Clear() empties BOTH stacks and resets the byte counter, and a subsequent Push starts fresh —
        /// the contract the match-reset / return-to-Edit seams rely on so no stale-id closure survives an F5.</summary>
        [Fact]
        public void Clear_WipesBothStacks_AndResetsByteTotal()
        {
            var h = new EditorHistory();
            // N undo entries (one carrying bytes so the byte total is non-zero) + M redo entries.
            h.Push(redo: () => { }, undo: () => { }, estimatedBytes: 1024);
            h.Push(redo: () => { }, undo: () => { });
            h.Push(redo: () => { }, undo: () => { });
            h.Undo(); // move one onto the redo stack so both stacks are populated
            Assert.True(h.CanUndo);
            Assert.True(h.CanRedo);

            h.Clear();
            Assert.False(h.CanUndo);
            Assert.False(h.CanRedo);

            // A subsequent Push starts from an empty history (fresh, single undoable entry, nothing redoable).
            h.Push(redo: () => { }, undo: () => { });
            Assert.True(h.CanUndo);
            Assert.False(h.CanRedo);
        }

        // ── DW-140: byte cap drops the oldest entries ────────────────────────────────────────────────────────────
        /// <summary>cap=100, three 60-byte entries: each new push that crosses the cap drops the oldest, so only the
        /// most-recent survives and undoing a dropped op is impossible.</summary>
        [Fact]
        public void ByteCap_DropsOldest_RetainedSumWithinCapPlusMostRecent()
        {
            var h       = new EditorHistory(maxUndoBytes: 100, maxUndoEntries: int.MaxValue);
            var undone  = new List<int>();

            h.Push(redo: () => { }, undo: () => undone.Add(1), estimatedBytes: 60); // A
            h.Push(redo: () => { }, undo: () => undone.Add(2), estimatedBytes: 60); // B → A dropped (120 > 100)
            h.Push(redo: () => { }, undo: () => undone.Add(3), estimatedBytes: 60); // C → B dropped (120 > 100)

            // Only C survives. Undoing runs C's undo once; the dropped A/B undos can never fire.
            h.Undo();
            Assert.Equal(new[] { 3 }, undone.ToArray());
            Assert.False(h.CanUndo); // A and B were dropped, not merely un-undone
        }

        /// <summary>cap=100, one 500-byte entry: the most-recent floor keeps a single oversized entry undoable.</summary>
        [Fact]
        public void SingleOversizedEntry_IsRetained()
        {
            var h = new EditorHistory(maxUndoBytes: 100, maxUndoEntries: int.MaxValue);
            h.Push(redo: () => { }, undo: () => { }, estimatedBytes: 500);
            Assert.True(h.CanUndo);
        }

        /// <summary>maxEntries=2, three zero-byte entity ops: the oldest is dropped so exactly 2 remain, newest-first.</summary>
        [Fact]
        public void EntryCountCap_DropsOldest_KeepsNewestFirst()
        {
            var h      = new EditorHistory(maxUndoBytes: long.MaxValue, maxUndoEntries: 2);
            var undone = new List<int>();

            h.Push(redo: () => { }, undo: () => undone.Add(1)); // dropped
            h.Push(redo: () => { }, undo: () => undone.Add(2));
            h.Push(redo: () => { }, undo: () => undone.Add(3));

            // Exactly two remain and they undo newest-first (3 then 2); op 1 was dropped and never fires.
            h.Undo();
            h.Undo();
            Assert.Equal(new[] { 3, 2 }, undone.ToArray());
            Assert.False(h.CanUndo);
        }

        /// <summary>A tiny byte cap plus many 0-byte entity ops: the byte total stays 0 so the byte cap never trims —
        /// only the entry-count cap can bound cheap ops.</summary>
        [Fact]
        public void ZeroByteOps_NeverTripByteCap()
        {
            var h = new EditorHistory(maxUndoBytes: 1, maxUndoEntries: int.MaxValue);
            for (int i = 0; i < 50; i++)
                h.Push(redo: () => { }, undo: () => { }); // all 0 bytes

            // All 50 survive — the byte cap (1) never trims because the running byte total stayed 0.
            int count = 0;
            while (h.CanUndo) { h.Undo(); count++; }
            Assert.Equal(50, count);
        }

        /// <summary>Push past the cap, undo the survivors, redo: redo replays only the surviving entries; the
        /// trimmed-away entries are gone from both stacks.</summary>
        [Fact]
        public void RedoAfterTrim_ReplaysOnlySurvivors()
        {
            var h    = new EditorHistory(maxUndoBytes: 100, maxUndoEntries: int.MaxValue);
            var redo = new List<int>();

            h.Push(redo: () => redo.Add(1), undo: () => { }, estimatedBytes: 60); // dropped by B
            h.Push(redo: () => redo.Add(2), undo: () => { }, estimatedBytes: 60); // dropped by C
            h.Push(redo: () => redo.Add(3), undo: () => { }, estimatedBytes: 60); // survivor

            h.Undo();               // undo the single survivor (op 3)
            Assert.True(h.CanRedo);
            h.Redo();               // replays only op 3
            Assert.Equal(new[] { 3 }, redo.ToArray());
            Assert.False(h.CanRedo); // nothing else to redo — ops 1 and 2 were trimmed, never redoable
        }

        // ── DW-140 (review gap 1): production default caps are actually wired ─────────────────────────────────────
        /// <summary>The parameterless ctor uses the production defaults (512 MiB / DefaultMaxEntries=1000). Every
        /// other cap test injects tiny caps, so this is the only fact that proves the shipped entry-count default is
        /// real: 1001 zero-byte pushes must leave exactly 1000 undoable (the 1001st trims the oldest).</summary>
        [Fact]
        public void DefaultCaps_BoundEntityOpCount()
        {
            var h = new EditorHistory(); // defaults: 512 MiB, 1000 entries
            for (int i = 0; i < EditorHistory.DefaultMaxEntries + 1; i++)
                h.Push(redo: () => { }, undo: () => { });

            int count = 0;
            while (h.CanUndo) { h.Undo(); count++; }
            Assert.Equal(EditorHistory.DefaultMaxEntries, count); // 1000 — the 1001st push trimmed the oldest
        }

        // ── DW-140 (review gap 2): Redo() re-accounts bytes so a LATER Trim() evicts correctly ────────────────────
        /// <summary>Redo() must add the restored entry's bytes back into the running total, or an undo→redo→keep-editing
        /// loop silently defeats the byte cap. Observed through a later Trim(): after B is undone then redone (restoring
        /// its 60 bytes), pushing C crosses the 150-byte cap and must evict the OLDEST (A). If Redo() failed to re-add
        /// the bytes the total would mis-count as 120, no trim would fire, and A's undo would wrongly still run.</summary>
        [Fact]
        public void Redo_RestoresByteTotal_SoLaterTrimEvictsCorrectly()
        {
            var h      = new EditorHistory(maxUndoBytes: 150, maxUndoEntries: int.MaxValue);
            var undone = new List<int>();

            h.Push(redo: () => { }, undo: () => undone.Add(1), estimatedBytes: 60); // A
            h.Push(redo: () => { }, undo: () => undone.Add(2), estimatedBytes: 60); // B — 120 ≤ 150, both retained

            h.Undo(); // pops B onto the redo stack (undo total → 60) — fires B's undo, so clear the log below
            h.Redo(); // restores B — the line under test re-adds 60, undo total → 120
            undone.Clear(); // discard the setup undo's B entry; we assert only on the final drain

            h.Push(redo: () => { }, undo: () => undone.Add(3), estimatedBytes: 60); // C — 180 > 150 → oldest A trimmed

            while (h.CanUndo) h.Undo();
            Assert.Equal(new[] { 3, 2 }, undone.ToArray()); // A was trimmed and never fires (buggy path would give {3,2,1})
        }

        private static int[] Sorted(HashSet<int> s)
        {
            var a = new List<int>(s);
            a.Sort();
            return a.ToArray();
        }
    }
}
