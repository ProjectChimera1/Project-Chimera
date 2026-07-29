#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 — the Godot-free disk rail for SP save slots (the <c>LocalProfileSource</c>/<c>ReplayBrowserPanel</c>
    /// pattern): slot enumeration + read/write/delete over an injected absolute directory. The Godot phase resolves
    /// <c>user://saves/</c> via <c>ProjectSettings.GlobalizePath</c> and constructs a <see cref="LocalSaveStore"/> over
    /// it; the sim/persistence core never touches Godot paths.
    /// </summary>
    public interface ISaveStore
    {
        /// <summary>The slot names that currently have a <c>.chsav</c> file (e.g. "0", "1", "autosave"), sorted
        /// deterministically. Never throws (a missing/unreadable directory yields an empty list).</summary>
        IReadOnlyList<string> List();

        /// <summary>True when <paramref name="slot"/> has a file.</summary>
        bool Exists(string slot);

        /// <summary>Read a slot's raw bytes, or null when it is absent/unreadable.</summary>
        byte[]? Read(string slot);

        /// <summary>Write (atomically overwrite) a slot's raw bytes. Creates the directory lazily.</summary>
        void Write(string slot, byte[] bytes);

        /// <summary>Delete a slot's file (a no-op if absent).</summary>
        void Delete(string slot);

        /// <summary>The absolute path a slot maps to (for a header-only metadata peek).</summary>
        string PathFor(string slot);
    }
}
