#nullable enable
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Loads and saves scenario JSON files.
    ///
    /// Pass absolute OS paths (not res:// paths) — resolve with
    /// <c>ProjectSettings.GlobalizePath()</c> in the presentation layer before calling.
    ///
    /// <para>
    /// The scenario JSON encodes the complete map setup: terrain, faction assignments,
    /// resource nodes, pre-placed buildings + units, and win condition.
    /// This is the foundation for editor save/load (Phase 2) and deterministic
    /// multiplayer map loading (Phase 3).
    /// </para>
    /// </summary>
    public static class ScenarioSerializer
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented       = true,
            Converters          = { new JsonStringEnumConverter(), new FixedJsonConverter() },
        };

        /// <summary>
        /// Load a <see cref="ScenarioData"/> from a JSON file on disk.
        /// Returns null if the file does not exist or fails to parse.
        /// </summary>
        public static ScenarioData? LoadFromFile(string absolutePath)
        {
            if (!File.Exists(absolutePath)) return null;
            string json = File.ReadAllText(absolutePath);
            return JsonSerializer.Deserialize<ScenarioData>(json, _options);
        }

        /// <summary>
        /// Serialize a <see cref="ScenarioData"/> to its canonical JSON string (the exact form
        /// <see cref="SaveToFile"/> writes). Deterministic — stable property order and culture-invariant number
        /// formatting — so two calls on equal models are byte-identical. Story 1.11 (AC4) uses this as the
        /// byte-identical artifact the procedural-map-generator golden-hash check pins.
        /// </summary>
        public static string Serialize(ScenarioData scenario) => JsonSerializer.Serialize(scenario, _options);

        /// <summary>
        /// Serialize a <see cref="ScenarioData"/> to a JSON file on disk.
        /// Creates or overwrites the file at <paramref name="absolutePath"/>.
        /// Used by the Creation Suite editor (Phase 2).
        /// </summary>
        public static void SaveToFile(ScenarioData scenario, string absolutePath) =>
            File.WriteAllText(absolutePath, Serialize(scenario));

        // FNV-1a 32-bit constants — shared by ComputeHash (in-memory) and ComputeFileHash (streamed) so a
        // serialized scenario and its on-disk form hash identically.
        private const uint FNV_PRIME  = 16777619u;
        private const uint FNV_OFFSET = 2166136261u;

        /// <summary>
        /// Compute a 32-bit FNV-1a hash of an in-memory byte buffer (e.g. UTF-8 of <see cref="Serialize"/>).
        /// Same algorithm as <see cref="ComputeFileHash"/>. Story 1.11 (AC4) pins the procedural generator's
        /// serialized bytes against a golden value computed this way, so a silent JSON-format drift fails the test.
        /// </summary>
        public static uint ComputeHash(byte[] bytes)
        {
            uint hash = FNV_OFFSET;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= FNV_PRIME;
            }
            return hash;
        }

        /// <summary>
        /// Compute a 32-bit FNV-1a hash of a scenario file's raw bytes.
        /// Used for pre-match content verification: if both peers compute different hashes,
        /// their scenario files differ and the match would immediately desync.
        /// Returns 0 if the file does not exist.
        /// </summary>
        public static uint ComputeFileHash(string absolutePath)
        {
            if (!File.Exists(absolutePath)) return 0u;

            uint hash = FNV_OFFSET;
            // Read in chunks to avoid loading huge files entirely into memory.
            using var stream = File.OpenRead(absolutePath);
            byte[] chunk = new byte[4096];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    hash ^= chunk[i];
                    hash *= FNV_PRIME;
                }
            }
            return hash;
        }
    }
}
