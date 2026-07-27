using System.Collections.Generic;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Linear undo/redo stack for editor placement actions.
    ///
    /// Each command is a (redo, undo) delegate pair. The redo delegate is the
    /// original "do" action and is re-run when the user presses Ctrl+Y. The undo
    /// delegate reverts the action. Pushing a new command clears the redo stack
    /// (standard non-linear history model).
    ///
    /// DW-138: <see cref="Clear"/> wipes BOTH stacks so no undo/redo closure can
    /// outlive a match reset that made its captured ids stale (see the reset seams
    /// in MainScene). DW-140: the undo store is a bounded deque — each entry carries
    /// an estimated byte cost, a running total is kept, and the OLDEST entries are
    /// dropped when a byte cap or entry-count cap is exceeded (the single most-recent
    /// entry is always retained). Cheap ops (entity place/delete) report 0 bytes and
    /// are never penalized; heavy terrain snapshots report their real image cost.
    ///
    /// Architecture: Pure C# — no Godot types.
    /// </summary>
    public class EditorHistory
    {
        /// <summary>Default undo-memory byte cap (512 MiB) — bounds heavy terrain snapshots.</summary>
        public const long DefaultMaxBytes = 512L * 1024 * 1024;

        /// <summary>Default undo entry-count cap — bounds cheap (0-byte) ops that never trip the byte cap.</summary>
        public const int DefaultMaxEntries = 1000;

        private readonly struct Entry
        {
            public readonly System.Action Redo;
            public readonly System.Action Undo;
            public readonly long Bytes;
            public Entry(System.Action redo, System.Action undo, long bytes)
            {
                Redo = redo; Undo = undo; Bytes = bytes;
            }
        }

        // Undo store is a deque (LinkedList) so Trim() can drop from the OLDEST end (First);
        // a Stack<T> can only drop the newest. AddLast = push, Last/RemoveLast = pop.
        private readonly LinkedList<Entry> _undoStack = new();
        // Redo stays a Stack — it is transitively bounded (entries only ever come from
        // undone undo-stack entries, and Push clears redo), so it never needs trimming.
        private readonly Stack<Entry> _redoStack = new();

        private readonly long _maxUndoBytes;
        private readonly int  _maxUndoEntries;

        // Running sum of the estimated bytes of every entry currently on the undo stack.
        private long _undoBytes;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Construct a history with optional undo caps. Defaults bound undo memory to
        /// <see cref="DefaultMaxBytes"/> and the undo entry count to <see cref="DefaultMaxEntries"/>.
        /// </summary>
        public EditorHistory(long maxUndoBytes = DefaultMaxBytes, int maxUndoEntries = DefaultMaxEntries)
        {
            _maxUndoBytes   = maxUndoBytes;
            _maxUndoEntries = maxUndoEntries;
        }

        /// <summary>
        /// Register a command that has already been executed.
        /// Clears the redo stack — pushing after an undo discards the redoable future.
        /// <paramref name="estimatedBytes"/> is the entry's memory cost for the byte cap
        /// (0 for cheap entity ops; the summed snapshot size for terrain strokes).
        /// </summary>
        public void Push(System.Action redo, System.Action undo, long estimatedBytes = 0)
        {
            _undoStack.AddLast(new Entry(redo, undo, estimatedBytes));
            _undoBytes += estimatedBytes;
            _redoStack.Clear();
            Trim();
        }

        /// <summary>Undo the most recent command and make it redoable.</summary>
        public void Undo()
        {
            if (!CanUndo) return;
            var node = _undoStack.Last!;
            var cmd  = node.Value;
            _undoStack.RemoveLast();
            _undoBytes -= cmd.Bytes;
            cmd.Undo();
            _redoStack.Push(cmd);
        }

        /// <summary>Re-apply the most recently undone command.</summary>
        public void Redo()
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            cmd.Redo();
            // Moving an already-capped entry back onto the undo stack cannot exceed the cap it
            // was under, so no Trim() here (transitive bound — see class remarks).
            _undoStack.AddLast(cmd);
            _undoBytes += cmd.Bytes;
        }

        /// <summary>
        /// DW-138: wipe BOTH stacks and reset the byte counter to 0. Invoked on the match-reset /
        /// return-to-Edit seams so no undo/redo closure can outlive the reset that made its ids stale.
        /// </summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _undoBytes = 0;
        }

        /// <summary>
        /// DW-140: drop the OLDEST undo entries while the byte cap or entry-count cap is exceeded,
        /// always retaining at least the single most-recent entry (a giant stroke must stay undoable once).
        /// </summary>
        private void Trim()
        {
            while ((_undoBytes > _maxUndoBytes || _undoStack.Count > _maxUndoEntries)
                   && _undoStack.Count > 1)
            {
                var oldest = _undoStack.First!;
                _undoBytes -= oldest.Value.Bytes;
                _undoStack.RemoveFirst();
            }
        }
    }
}
