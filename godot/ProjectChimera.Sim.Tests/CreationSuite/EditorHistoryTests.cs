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
    }
}
