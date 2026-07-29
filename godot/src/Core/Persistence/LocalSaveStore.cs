#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 — a filesystem <see cref="ISaveStore"/> over an injected absolute directory (the
    /// <c>LocalProfileSource</c> template): pure <c>System.IO</c>, no <c>using Godot</c>, fail-soft reads, lazy
    /// directory creation, deterministic slot ordering. One file per slot: <c>{slot}.chsav</c>. Writes go through a
    /// temp file + atomic replace so a crash mid-write never corrupts an existing slot.
    /// </summary>
    public sealed class LocalSaveStore : ISaveStore
    {
        /// <summary>The dedicated periodic-autosave slot name.</summary>
        public const string AutosaveSlot = "autosave";

        /// <summary>The <c>.chsav</c> file extension (with dot).</summary>
        public const string Extension = ".chsav";

        private readonly string _directory;

        public LocalSaveStore(string directory) => _directory = directory ?? "";

        public string PathFor(string slot) => Path.Combine(_directory, Sanitize(slot) + Extension);

        public IReadOnlyList<string> List()
        {
            var slots = new List<string>();
            try
            {
                if (!Directory.Exists(_directory)) return slots;
                foreach (string file in Directory.GetFiles(_directory, "*" + Extension))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    // #11 — only list names that ROUND-TRIP through PathFor (Sanitize is idempotent for them), so a
                    // listed slot name always resolves back to the SAME file. A name that would sanitize differently is
                    // skipped rather than listed under a string that Read/Write can't reopen.
                    if (Sanitize(name) == name) slots.Add(name);
                }
            }
            catch { return new List<string>(); } // fail-soft: an unreadable directory lists as empty
            slots.Sort(StringComparer.Ordinal); // deterministic order
            return slots;
        }

        public bool Exists(string slot)
        {
            try { return File.Exists(PathFor(slot)); } catch { return false; }
        }

        public byte[]? Read(string slot)
        {
            try
            {
                string path = PathFor(slot);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch { return null; } // fail-soft: a locked/corrupt file reads as absent (the loader fail-closes on parse)
        }

        public void Write(string slot, byte[] bytes)
        {
            Directory.CreateDirectory(_directory);
            string path = PathFor(slot);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            // Atomic replace: never leave a half-written slot if the process dies mid-write.
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public void Delete(string slot)
        {
            try { string path = PathFor(slot); if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }

        // Strip path separators / invalid chars so a slot name can never escape the directory.
        private static string Sanitize(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return "slot";
            var chars = slot.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
