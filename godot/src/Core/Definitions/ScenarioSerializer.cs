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
        public static string Serialize(ScenarioData scenario)
        {
            // Review patch (Story 6.4): Regions uses JsonIgnore(WhenWritingNull), which omits null but NOT an empty
            // array — `[]` would emit `"regions":[]` and drift the pinned scenario bytes. Normalize empty→null for
            // THIS serialization only, WITHOUT mutating the caller's model: Serialize is a pure, deterministic
            // byte-source (golden-hash checks pin its output) and may be handed a live ScenarioData the editor still
            // holds, so a permanent `scenario.Regions = null` side effect would silently surprise any other holder of
            // that instance. Swap-to-null under try/finally and restore the original reference afterwards — the JSON
            // bytes are identical to the null/absent case, and the caller's object is observably unchanged.
            // Story 14.5: the persistence_manifest follows the same absent-stays-absent contract but needs no swap here —
            // a null PersistenceManifest is omitted by [JsonIgnore(WhenWritingNull)] on ScenarioData, so a manifest-less
            // map serializes with no key and an authored manifest round-trips unchanged. Pinned by the Tier-1
            // PersistenceManifestTests all-shipped absolute-absence guard + editor-save round-trip test.
            ScenarioRegion[]? savedRegions = scenario.Regions;
            if (savedRegions is { Length: 0 }) scenario.Regions = null;

            // Story 6.5: normalize an ALL-CLEAR painted pathability layer to null for THIS serialization so a map the
            // author painted then fully erased serializes byte-identically to a flat/legacy map (the key is omitted
            // rather than emitting a 2048-byte all-zero bitset). Same swap-under-try/finally, restore-after discipline
            // as Regions above — Serialize is a pure byte-source and must not mutate the caller's live model. A base64
            // that decodes to any blocked cell is kept verbatim; only the all-zero case normalizes to null.
            string? savedPathability = scenario.PathabilityBlocked;
            if (savedPathability != null
                && ProjectChimera.Navigation.PathabilityGrid.DigestOfBase64(savedPathability) == 0u)
                scenario.PathabilityBlocked = null;

            // Story 6.6: normalize empty Props/Cameras/Water → null for THIS serialization (same swap-under-try/finally,
            // restore-after discipline as Regions above — Serialize is a pure byte-source and must not mutate the
            // caller's live model). An absent/empty collection emits no key, byte-identical to a pre-feature map.
            ScenarioProp[]?   savedProps   = scenario.Props;
            ScenarioCamera[]? savedCameras = scenario.Cameras;
            ScenarioWater[]?  savedWater   = scenario.Water;
            if (savedProps   is { Length: 0 }) scenario.Props   = null;
            if (savedCameras is { Length: 0 }) scenario.Cameras = null;
            if (savedWater   is { Length: 0 }) scenario.Water   = null;

            // Story 6.7: normalize empty authoring metadata → null for THIS serialization so a map whose author left
            // Author/Description blank serializes byte-identically to a pre-6.7 map (the key is omitted rather than
            // emitting "author":""). Same swap-under-try/finally, restore-after discipline — Serialize is a pure
            // byte-source and must not mutate the caller's live model.
            string? savedAuthor      = scenario.Author;
            string? savedDescription = scenario.Description;
            if (string.IsNullOrEmpty(savedAuthor))      scenario.Author      = null;
            if (string.IsNullOrEmpty(savedDescription)) scenario.Description = null;

            // Story 7.3: normalize empty Variables/Timers arrays and empty/whitespace TriggerGraphJson → null for THIS
            // serialization (same swap-under-try/finally, restore-after discipline — Serialize is a pure byte-source
            // and must not mutate the caller's live model). An absent-declaration scenario then serializes BYTE-
            // IDENTICALLY to pre-7.3 (no key emitted), so no scenario-bytes / CanonicalModelHash / StartStateHash move.
            ScenarioVariable[]? savedVariables = scenario.Variables;
            ScenarioTimer[]?    savedTimers    = scenario.Timers;
            string?             savedTriggerGraph = scenario.TriggerGraphJson;
            if (savedVariables is { Length: 0 }) scenario.Variables = null;
            if (savedTimers    is { Length: 0 }) scenario.Timers    = null;
            if (string.IsNullOrWhiteSpace(savedTriggerGraph)) scenario.TriggerGraphJson = null;

            // Story 7.5: normalize an empty CustomEvents array → null for THIS serialization (the Variables
            // pattern — same swap-under-try/finally, restore-after discipline), so an event-less scenario
            // serializes byte-identically to pre-7.5 (no key emitted, no hash/golden movement).
            ScenarioCustomEvent[]? savedCustomEvents = scenario.CustomEvents;
            if (savedCustomEvents is { Length: 0 }) scenario.CustomEvents = null;
            try
            {
                return JsonSerializer.Serialize(scenario, _options);
            }
            finally
            {
                scenario.Regions = savedRegions;
                scenario.PathabilityBlocked = savedPathability;
                scenario.Props   = savedProps;
                scenario.Cameras = savedCameras;
                scenario.Water   = savedWater;
                scenario.Author      = savedAuthor;
                scenario.Description = savedDescription;
                scenario.Variables       = savedVariables;
                scenario.Timers          = savedTimers;
                scenario.TriggerGraphJson = savedTriggerGraph;
                scenario.CustomEvents    = savedCustomEvents;
            }
        }

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
