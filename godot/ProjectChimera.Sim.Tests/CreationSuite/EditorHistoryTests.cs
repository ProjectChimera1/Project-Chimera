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

        private static int[] Sorted(HashSet<int> s)
        {
            var a = new List<int>(s);
            a.Sort();
            return a.ToArray();
        }
    }
}
