#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 8.1 — the file-backed <see cref="ISecretStore"/>. One bare secret per file (<c>&lt;id&gt;.key</c>)
    /// inside an INJECTED absolute directory. Mirrors <see cref="LocalProfileSource"/> exactly: a Godot-free class
    /// over an OS-absolute path (the Godot layer globalizes <c>user://secrets</c> once and hands it in), using only
    /// <c>System.IO</c> + fail-soft reads. Because the file is a bare secret and not JSON, <see cref="Set"/> writes
    /// UTF-8 text with no <c>WriteIndented</c> / serializer.
    ///
    /// <para>Contract (I/O matrix): <see cref="Get"/> on an absent secret returns <c>""</c> and writes NOTHING —
    /// the directory is created lazily only on the first <see cref="Set"/>. A missing/unreadable file reads as
    /// <c>""</c> (fail-soft, mirroring <c>SettingsManager.Load</c>). Key ids are validated <c>^[a-z0-9_-]+$</c> so a
    /// <c>"../evil"</c> or empty id can never escape the directory — an invalid id throws
    /// <see cref="ArgumentException"/> before any path is touched.</para>
    /// </summary>
    public sealed class FileSecretStore : ISecretStore
    {
        /// <summary>Every secret is stored as <c>&lt;id&gt;.key</c>; the extension is gitignored (<c>*.key</c>).</summary>
        public const string KeyFileExtension = ".key";

        // Anchored allow-list: lower-case ASCII letters, digits, underscore, hyphen. Rejects '/', '\', '.', '..',
        // and empty — closing the path-traversal row of the I/O matrix at the id boundary (never at the path).
        // Uses \A…\z (absolute string anchors), NOT ^…$: in .NET, $ matches before a trailing '\n', so "^…$" would
        // accept an id like "llm\n" and map it to a stray "llm\n.key" file — \z requires the true end of string.
        private static readonly Regex KeyIdPattern = new(@"\A[a-z0-9_-]+\z", RegexOptions.Compiled);

        private readonly string _directory;

        /// <summary>Construct the store over <paramref name="directory"/> (an OS-absolute path; the Godot layer
        /// resolves <c>user://secrets</c> via <c>ProjectSettings.GlobalizePath</c> before passing it). The directory
        /// need not exist yet — it is created lazily on the first <see cref="Set"/>.</summary>
        public FileSecretStore(string directory) => _directory = directory ?? "";

        /// <inheritdoc/>
        public string Get(string id)
        {
            string path = PathFor(id);   // validates id (throws on invalid) — but touches nothing on disk
            if (!File.Exists(path)) return "";   // absent secret ⇒ "", nothing created
            try
            {
                // Trim so an editor-appended trailing newline never leaks into the key; a Set-written value has none.
                return File.ReadAllText(path).Trim();
            }
            catch
            {
                // Fail-soft (mirrors SettingsManager.Load): an unreadable/corrupt key file must not crash — treat as "".
                return "";
            }
        }

        /// <inheritdoc/>
        public void Set(string id, string value)
        {
            string path = PathFor(id);           // validates id first
            Directory.CreateDirectory(_directory); // lazy-create only on write
            File.WriteAllText(path, (value ?? "").Trim(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <inheritdoc/>
        public bool Has(string id) => !string.IsNullOrEmpty(Get(id));

        /// <inheritdoc/>
        public void Clear(string id)
        {
            string path = PathFor(id);   // validates id first
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort: an undeletable file must not crash the caller */ }
        }

        // Resolve the on-disk path for a validated key id. Validation happens HERE so every public method rejects a
        // bad id before any File.* call — the path-traversal guard is at the id boundary, not the filesystem.
        private string PathFor(string id)
        {
            if (id == null || !KeyIdPattern.IsMatch(id))
                throw new ArgumentException($"Invalid secret key id '{id}'. Must match ^[a-z0-9_-]+$.", nameof(id));
            return Path.Combine(_directory, id + KeyFileExtension);
        }
    }
}
