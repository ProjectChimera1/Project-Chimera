#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UGC
{
    /// <summary>
    /// Story 9.8 — per-scenario persistence for <see cref="ProofOfPlayToken"/>. One JSON file per scenario id under an
    /// INJECTED OS-absolute directory (the Godot layer globalizes <c>user://tokens</c> once and hands it in), mirroring
    /// <see cref="LocalProfileSource"/> / <see cref="FileSecretStore"/>: Godot-free, <c>System.IO</c> + fail-soft reads,
    /// directory created lazily on the first <see cref="Save"/>.
    ///
    /// <para>The scenario id is SANITIZED to the file-safe <c>^[a-z0-9_-]+$</c> rule before it becomes a filename, so a
    /// stray path separator / traversal segment can never escape the directory (matching the <see cref="FileSecretStore"/>
    /// key-id discipline). Distinct sanitized ids map to distinct files; an id that sanitizes empty falls back to a
    /// fixed safe stem.</para>
    /// </summary>
    public sealed class ProofOfPlayStore
    {
        /// <summary>Each token is stored as <c>&lt;sanitized-id&gt;.json</c>.</summary>
        public const string TokenFileExtension = ".json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented       = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly string _directory;

        /// <summary>Construct the store over <paramref name="directory"/> (an OS-absolute path). The directory need not
        /// exist yet — it is created lazily on the first <see cref="Save"/>.</summary>
        public ProofOfPlayStore(string directory) => _directory = directory ?? "";

        /// <summary>Persist <paramref name="token"/> for <paramref name="scenarioId"/>, creating the backing directory
        /// lazily. One file per (sanitized) scenario id; a re-save overwrites it.</summary>
        public void Save(string scenarioId, ProofOfPlayToken token)
        {
            if (token is null) return;
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(scenarioId), JsonSerializer.Serialize(token, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>Load the token for <paramref name="scenarioId"/>. Fail-soft: a missing / unreadable / unparseable
        /// file yields <c>false</c> with a null out-param (never a throw), mirroring the store family's contract.</summary>
        public bool TryLoad(string scenarioId, out ProofOfPlayToken? token)
        {
            token = null;
            string path = PathFor(scenarioId);
            if (!File.Exists(path)) return false;
            try
            {
                token = JsonSerializer.Deserialize<ProofOfPlayToken>(File.ReadAllText(path), JsonOptions);
                return token != null;
            }
            catch
            {
                // Fail-soft (mirrors LocalProfileSource.LoadAll / SettingsManager.Load): a corrupt file ⇒ "no token".
                token = null;
                return false;
            }
        }

        // Resolve the on-disk path for a scenario id. Sanitization (not rejection) so ANY scenario id maps to a stable
        // file-safe name — the traversal guard lives at the id→filename boundary, never at the raw path. Review P8: a
        // deterministic short hash of the RAW id is appended so two distinct ids that sanitize to the same stem (e.g.
        // "My-Map" and "My Map") never collide onto one file (which would let one scenario's token overwrite/cross-read
        // another's). The load side derives the identical name from the same raw id, so this stays a pure id→path map.
        private string PathFor(string scenarioId)
            => Path.Combine(_directory, FileStem(scenarioId) + TokenFileExtension);

        /// <summary>The full file stem for a raw scenario id: <c>{sanitized}_{shorthash-of-raw-id}</c>.</summary>
        internal static string FileStem(string? scenarioId)
            => $"{Sanitize(scenarioId)}_{ShortHash(scenarioId)}";

        /// <summary>Map an arbitrary scenario id to the file-safe <c>^[a-z0-9_-]+$</c> rule: lowercase, keep
        /// <c>[a-z0-9_-]</c>, replace every other char with <c>-</c>, collapse runs, trim. An id that sanitizes empty
        /// (e.g. <c>""</c> or all-punctuation) falls back to a fixed safe stem so a filename always exists.</summary>
        internal static string Sanitize(string? scenarioId)
        {
            if (string.IsNullOrEmpty(scenarioId)) return "_scenario";
            var sb = new StringBuilder(scenarioId.Length);
            foreach (char c in scenarioId.ToLowerInvariant())
                sb.Append((c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') ? c : '-');
            string result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
            return result.Length == 0 ? "_scenario" : result;
        }

        /// <summary>Deterministic 8-hex-char FNV-1a-32 digest of the RAW id — the collision-disambiguator appended to
        /// the sanitized stem. Derived from the id alone (the load side has only the id), so it is a pure function.</summary>
        private static string ShortHash(string? scenarioId)
        {
            const uint fnvOffset = 2166136261u, fnvPrime = 16777619u;
            uint h = fnvOffset;
            foreach (byte b in Encoding.UTF8.GetBytes(scenarioId ?? "")) { h ^= b; h *= fnvPrime; }
            return h.ToString("x8");
        }
    }
}
