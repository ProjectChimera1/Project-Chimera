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
        /// <summary>
        /// Story 7.7 (D3 versioning) — the CURRENT scenario-JSON format version <see cref="Serialize"/> stamps
        /// into <see cref="ScenarioData.SchemaVersion"/> on every save. An absent stamp reads as v1 (legacy
        /// amnesty); a file stamped NEWER than this rejects at <see cref="ScenarioValidator"/>. Bump only on a
        /// real format change, together with the <c>VersionStampConsistencyTests</c> pin (same commit).
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        private static readonly JsonSerializerOptions _options = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented       = true,
            Converters          = { new JsonStringEnumConverter(), new FixedJsonConverter(), new WidgetBaseJsonConverter() },
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
            // Story 7.7 review — all normalizations/stamps below apply to a SHALLOW COPY, never the caller's model:
            // Serialize is a pure, deterministic byte-source (golden-hash checks pin its output) and may be handed
            // a live ScenarioData the editor still holds. The former swap-mutate-restore discipline left the model
            // transiently stamped (observable by concurrent readers, and permanently if a pre-try step threw); the
            // copy makes non-mutation structural. Shallow is sufficient — only top-level references/stamps are
            // reassigned, element contents are never touched — and the emitted bytes are identical
            // (SchemaVersionTests.Serialize_DoesNotMutateTheCallersModel pins the contract).
            ScenarioData copy = scenario.ShallowClone();

            // Review patch (Story 6.4): Regions uses JsonIgnore(WhenWritingNull), which omits null but NOT an empty
            // array — `[]` would emit `"regions":[]` and drift the pinned scenario bytes. Normalize empty→null.
            // Story 14.5: the persistence_manifest follows the same absent-stays-absent contract but needs no
            // normalization here — a null PersistenceManifest is omitted by [JsonIgnore(WhenWritingNull)], so a
            // manifest-less map serializes with no key and an authored manifest round-trips unchanged. Pinned by the
            // Tier-1 PersistenceManifestTests all-shipped absolute-absence guard + editor-save round-trip test.
            if (copy.Regions is { Length: 0 }) copy.Regions = null;

            // Story 6.5: normalize an ALL-CLEAR painted pathability layer to null so a map the author painted then
            // fully erased serializes byte-identically to a flat/legacy map (the key is omitted rather than emitting
            // a 2048-byte all-zero bitset). A base64 that decodes to any blocked cell is kept verbatim.
            if (copy.PathabilityBlocked != null
                && ProjectChimera.Navigation.PathabilityGrid.DigestOfBase64(copy.PathabilityBlocked) == 0u)
                copy.PathabilityBlocked = null;

            // Story 6.6: normalize empty Props/Cameras/Water → null — an absent/empty collection emits no key,
            // byte-identical to a pre-feature map.
            if (copy.Props   is { Length: 0 }) copy.Props   = null;
            if (copy.Cameras is { Length: 0 }) copy.Cameras = null;
            if (copy.Water   is { Length: 0 }) copy.Water   = null;

            // Story 6.7: normalize empty authoring metadata → null so a map whose author left Author/Description
            // blank serializes byte-identically to a pre-6.7 map (no "author":"" key).
            if (string.IsNullOrEmpty(copy.Author))      copy.Author      = null;
            if (string.IsNullOrEmpty(copy.Description)) copy.Description = null;

            // Story 7.3: normalize empty Variables/Timers arrays and empty/whitespace TriggerGraphJson → null. An
            // absent-declaration scenario then serializes BYTE-IDENTICALLY to pre-7.3 (no key emitted), so no
            // scenario-bytes / CanonicalModelHash / StartStateHash move.
            if (copy.Variables is { Length: 0 }) copy.Variables = null;
            if (copy.Timers    is { Length: 0 }) copy.Timers    = null;
            if (string.IsNullOrWhiteSpace(copy.TriggerGraphJson)) copy.TriggerGraphJson = null;

            // Story 7.8: normalize an EMPTY custom-UI tree (no widgets) → null so a scenario without custom UI
            // serializes BYTE-IDENTICALLY to pre-7.8 (no "custom_ui" key), round-trips absent, and moves no golden.
            if (copy.CustomUi != null && (copy.CustomUi.Widgets == null || copy.CustomUi.Widgets.Length == 0))
                copy.CustomUi = null;

            // Story 7.7 (D3 versioning): STAMP the current schema/checksum-algo versions. Every save carries the
            // stamps; the caller's in-memory model (possibly null stamps) is untouched. Both stamps are EXCLUDED
            // from CanonicalModelHash, so this re-save-adds-stamps behavior never moves the handshake hash of a
            // legacy file.
            copy.SchemaVersion       = CurrentSchemaVersion;
            copy.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion;

            return JsonSerializer.Serialize(copy, _options);
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
