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
    /// Architecture: Pure C# — no Godot types.
    /// </summary>
    public class EditorHistory
    {
        private readonly Stack<(System.Action redo, System.Action undo)> _undoStack = new();
        private readonly Stack<(System.Action redo, System.Action undo)> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// Register a command that has already been executed.
        /// Clears the redo stack — pushing after an undo discards the redoable future.
        /// </summary>
        public void Push(System.Action redo, System.Action undo)
        {
            _undoStack.Push((redo, undo));
            _redoStack.Clear();
        }

        /// <summary>Undo the most recent command and make it redoable.</summary>
        public void Undo()
        {
            if (!CanUndo) return;
            var cmd = _undoStack.Pop();
            cmd.undo();
            _redoStack.Push(cmd);
        }

        /// <summary>Re-apply the most recently undone command.</summary>
        public void Redo()
        {
            if (!CanRedo) return;
            var cmd = _redoStack.Pop();
            cmd.redo();
            _undoStack.Push(cmd);
        }
    }
}
