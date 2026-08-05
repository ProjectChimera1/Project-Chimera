#nullable enable
using System.IO;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Loads and saves scenario JSON files.
    ///
    /// Pass absolute OS paths (not res:// paths) — resolve with
    /// <c>ProjectSettings.GlobalizePath()</c> in the presentation layer before calling.
    ///
    /// <para>
    /// DW-274: both directions go through <see cref="ContentJson.ScenarioOptions"/> — this class carries NO private
    /// <see cref="JsonSerializerOptions"/> of its own, so the scenario format's posture cannot drift from the rest of
    /// the content pipeline. Read <see cref="ContentJson.ScenarioOptions"/>'s docs before changing anything about it:
    /// its <c>WriteIndented</c> + converter ORDER are what every scenario golden hash is pinned on.
    /// </para>
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

        /// <summary>
        /// DW-366 — the maximum byte size of UNTRUSTED scenario input (a .json file on disk, or an AI-generated
        /// string) accepted for deserialization. Enforced BEFORE any read/parse by
        /// <see cref="GuardScenarioInputSize"/> at both untrusted entries (<see cref="LoadFromFile"/> and
        /// <c>LLMService.ValidateScenario</c>), closing the parse-then-gate gap: every per-collection cap
        /// (<c>DslBounds.MaxWidgetCount</c>, the trigger/region/unit gates) runs only AFTER System.Text.Json has
        /// already materialized the full collection, so a pathological flat array of millions of elements used to
        /// allocate before rejection. One uniform upstream byte cap bounds that allocation for EVERY scenario
        /// collection at once (the recorded 2026-07-30 decision: upstream size guard — cheapest, uniform). 8 MiB
        /// is orders of magnitude above any legitimate scenario (the largest shipped map is ~4 KB; a maxed-out
        /// authored file — full widget tree with per-widget editor bags, a large trigger corpus, painted
        /// pathability — stays well under ~2 MiB) while still bounding hostile/degenerate input.
        /// </summary>
        public const long MaxScenarioFileBytes = 8L * 1024 * 1024;

        /// <summary>
        /// DW-366 — reject over-cap untrusted scenario input BEFORE it is read or parsed. Throws a located
        /// <see cref="JsonException"/> naming <see cref="MaxScenarioFileBytes"/> (the fail-closed load posture: an
        /// unloadable scenario must never be mistaken for "no scenario here"). Call with the input's byte count
        /// (on-disk file length / UTF-8 byte count of a string) before deserializing scenario JSON.
        /// </summary>
        public static void GuardScenarioInputSize(long byteCount, string source)
        {
            if (byteCount > MaxScenarioFileBytes)
                throw new JsonException(
                    $"{source}: scenario input is {byteCount} bytes, exceeding ScenarioSerializer.MaxScenarioFileBytes={MaxScenarioFileBytes} — refusing to parse (DW-366 upstream size guard).");
        }

        /// <summary>
        /// Load a <see cref="ScenarioData"/> from a JSON file on disk.
        ///
        /// <para>
        /// Returns null whenever NO scenario model can be produced: the file does not exist, its JSON is the literal
        /// <c>null</c>, or its content is MALFORMED — bad syntax, a numeric enum (<c>"win_condition": 0</c>) now that
        /// the scenario posture is <c>allowIntegerValues:false</c>, a non-finite/over-range 16.16 number, an unknown
        /// widget <c>kind</c>. DW-533: that null-on-parse-failure contract is exactly what both production callers
        /// already code to — <c>ScenarioLoadPhase.LoadAndApplyScenario</c> ("Scenario not found or failed to parse …
        /// using defaults" → the VALIDATED fallback mirror) and <c>MainScene.BuildHeadlessServerSimHost</c>
        /// ("missing/parse-failed" → relay + quorum only). DW-274 tightened the enum boundary WITHOUT updating those
        /// callers, so a hand-edited or externally generated <c>user://</c> map used to throw past
        /// <c>ScenePhaseRunner.Run</c> and abort <c>_Ready</c> mid-phase with a half-initialised scene. It now
        /// degrades to the fallback path instead.
        /// </para>
        ///
        /// <para>
        /// This is still FAIL-CLOSED where fail-closed matters: a partially-parsed or numerically-miscoded model is
        /// never returned and never reaches the sim — the caller substitutes validated fallback content. What changed
        /// is only the CHANNEL (null instead of an escaping exception). The located reason is not lost: use the
        /// <c>LoadFromFile(path, out string? parseError)</c> overload to recover it (file path + the parser's property
        /// path / line); the one-argument form discards it.
        /// </para>
        ///
        /// <para>
        /// An OVER-CAP file (&gt; <see cref="MaxScenarioFileBytes"/>) still THROWS via
        /// <see cref="GuardScenarioInputSize"/>, checked against the on-disk length BEFORE the file is even read
        /// (DW-366). That guard bounds HOSTILE input rather than reporting authoring breakage, it runs before any
        /// allocation, and it is deliberately outside the parse-failure contract below.
        /// </para>
        /// </summary>
        public static ScenarioData? LoadFromFile(string absolutePath) => LoadFromFile(absolutePath, out _);

        /// <summary>
        /// <see cref="LoadFromFile(string)"/> with the LOCATED parse-failure reason surfaced (DW-533).
        /// <paramref name="parseError"/> is non-null ONLY when the file existed and its content failed to parse — null
        /// both on success and on a missing file — so <c>result == null &amp;&amp; parseError != null</c> is precisely
        /// the "this file is broken" case worth logging, distinct from "no scenario here".
        /// </summary>
        public static ScenarioData? LoadFromFile(string absolutePath, out string? parseError)
        {
            parseError = null;
            if (!File.Exists(absolutePath)) return null;
            // DW-366 — upstream size guard: check the on-disk length BEFORE reading, so an over-cap file never
            // allocates its string, let alone its parsed collections. Deliberately OUTSIDE the try below (see the
            // XML doc): an over-cap file keeps throwing.
            GuardScenarioInputSize(new FileInfo(absolutePath).Length, absolutePath);
            string json = File.ReadAllText(absolutePath);
            try
            {
                return JsonSerializer.Deserialize<ScenarioData>(json, ContentJson.ScenarioOptions);
            }
            catch (JsonException e)
            {
                // DW-533 — map a parse failure onto the null/fallback contract both production callers code to.
                // EVERY scenario-posture rejection reports as a JsonException (System.Text.Json's own syntax/enum
                // errors, FixedJsonConverter's finite/range checks, WidgetBaseJsonConverter's closed-kind and
                // field-shape gates), so this single catch covers the whole malformed-CONTENT surface while a
                // code-level fault (e.g. a NotSupportedException from a broken type model) still propagates loudly.
                parseError = $"{absolutePath}: {e.Message}";
                return null;
            }
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
            // Story 7.14: normalize an empty Objectives array → null so an objective-less scenario serializes
            // BYTE-IDENTICALLY to pre-7.14 (no "objectives" key) and round-trips absent. Hash-excluded either way.
            if (copy.Objectives is { Length: 0 }) copy.Objectives = null;
            if (string.IsNullOrWhiteSpace(copy.TriggerGraphJson)) copy.TriggerGraphJson = null;

            // Story 7.5: normalize an empty CustomEvents array → null so an event-less scenario serializes
            // BYTE-IDENTICALLY to pre-7.5 (no "custom_events" key emitted) and round-trips absent.
            if (copy.CustomEvents is { Length: 0 }) copy.CustomEvents = null;

            // Story 7.8: normalize an EMPTY custom-UI tree (no widgets) → null so a scenario without custom UI
            // serializes BYTE-IDENTICALLY to pre-7.8 (no "custom_ui" key), round-trips absent, and moves no golden.
            if (copy.CustomUi != null && (copy.CustomUi.Widgets == null || copy.CustomUi.Widgets.Length == 0))
                copy.CustomUi = null;

            // Story 7.11: normalize a None (no-preset) WinConditionSpec → null so a scenario using only the built-in
            // WinCondition enum serializes BYTE-IDENTICALLY to pre-7.11 (no "win_condition_spec" key), round-trips
            // absent, and — with the None spec folding nothing extra in CanonicalModelHash — moves no golden apart
            // from the AlgoVersion bump.
            if (copy.WinConditionSpec != null && copy.WinConditionSpec.Preset == WinPresetKind.None)
                copy.WinConditionSpec = null;

            // Story 7.11 review (P5): the editor reuses one WinConditionSpec across preset switches, so a spec can
            // carry stale cross-preset params (e.g. a leftover LeaderUnitIndex on a KotH spec). [JsonIgnore(
            // WhenWritingDefault)] would then serialize them, making two semantically-identical scenarios serialize
            // DIFFERENTLY while CanonicalModelHash (which folds only the active preset's params) hashes them
            // IDENTICALLY — breaking "same serialize ⇄ same hash". Zero every field NOT owned by the active preset.
            // ShallowClone shares the spec reference, so build a fresh instance — the live in-editor spec is untouched.
            if (copy.WinConditionSpec is { Preset: not WinPresetKind.None } src)
            {
                WinConditionSpec norm = new() { Preset = src.Preset };
                switch (src.Preset)
                {
                    case WinPresetKind.KingOfTheHill:
                        norm.RegionId  = src.RegionId;
                        norm.HoldTicks = src.HoldTicks;
                        break;
                    case WinPresetKind.TimedSurvival:
                        norm.FactionSlot  = src.FactionSlot;
                        norm.SurviveTicks = src.SurviveTicks;
                        break;
                    case WinPresetKind.Assassination:
                        norm.LeaderUnitIndex = src.LeaderUnitIndex;
                        break;
                    case WinPresetKind.LandmarkDestruction:
                        norm.StructureIndex = src.StructureIndex;
                        break;
                }
                copy.WinConditionSpec = norm;
            }

            // Story 7.7 (D3 versioning): STAMP the current schema/checksum-algo versions. Every save carries the
            // stamps; the caller's in-memory model (possibly null stamps) is untouched. Both stamps are EXCLUDED
            // from CanonicalModelHash, so this re-save-adds-stamps behavior never moves the handshake hash of a
            // legacy file.
            copy.SchemaVersion       = CurrentSchemaVersion;
            copy.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion;

            // A4-E3: normalize to LF. .NET 8's indented Utf8JsonWriter emits Environment.NewLine
            // (\r\n on Windows, \n on Linux), which made this method's bytes — and every golden hash
            // pinned on them — platform-dependent. Linux output is already LF, so this is a no-op
            // there and no goldens move. Switch to JsonSerializerOptions.NewLine if/when on .NET 9.
            return JsonSerializer.Serialize(copy, ContentJson.ScenarioOptions).Replace("\r\n", "\n");
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
