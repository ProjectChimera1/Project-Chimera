#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Creates and reads .chimera.zip content packages.
    ///
    /// A .chimera.zip is a standard ZIP archive with a required manifest.json entry
    /// at the root, a scenario.json, and optional thumbnail + faction files.
    ///
    /// Packaging flow (Phase 4 editor "Export Map" button):
    ///   var opts = new PackOptions { DisplayName = "My Map", Author = "Alec", ... };
    ///   ContentPackager.Pack(scenarioAbsPath, outputZipPath, opts);
    ///
    /// Loading flow (in-game content browser or "Import Map"):
    ///   var result = ContentPackager.Unpack(zipPath, extractDir);
    ///   var scenario = ScenarioSerializer.LoadFromFile(result.ScenarioPath);
    ///
    /// All methods take absolute OS paths (use ProjectSettings.GlobalizePath for res:// paths).
    /// </summary>
    public static class ContentPackager
    {
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // ── Pack ─────────────────────────────────────────────────────────────────

        public class PackOptions
        {
            public string DisplayName    { get; set; } = "Unnamed Map";
            public string Description    { get; set; } = "";
            public string Author         { get; set; } = "Unknown";
            public string Version        { get; set; } = "1.0.0";
            public string MinGameVersion { get; set; } = "0.1";
            public List<string> Tags     { get; set; } = new();
            public int PlayerCount       { get; set; } = 2;
            /// <summary>Absolute path to a 256×256 PNG thumbnail. Null = no thumbnail. Legacy input, still honored;
            /// <see cref="PreviewPngBytes"/> (the Story 6.7 auto-generated minimap preview) takes precedence when both
            /// are supplied.</summary>
            public string? ThumbnailPath { get; set; }
            /// <summary>Story 6.7 — the auto-generated top-down minimap preview as PNG bytes. When non-null, written
            /// into the package at <c>preview/preview.png</c> and referenced by the manifest's
            /// <see cref="ContentPackageManifest.ThumbnailFile"/> (wiring the previously-dead thumbnail slot). Null =
            /// no preview generated ⇒ the slot is omitted, byte-identical to a pre-6.7 package.</summary>
            public byte[]? PreviewPngBytes { get; set; }
            /// <summary>Additional faction JSON files to bundle. Absolute paths.</summary>
            public List<string> FactionPaths { get; set; } = new();

            /// <summary>Story 9.8 — the signed proof-of-play token to embed in the manifest. Null = none embedded (the
            /// publish gate will refuse the upload). Written verbatim into <see cref="ContentPackageManifest.ProofOfPlay"/>.</summary>
            public ProofOfPlayToken? Token { get; set; }

            /// <summary>Story 9.8 — absolute on-disk paths to screenshot PNGs to bundle. Each is copied into the package
            /// at <c>screenshots/shot_NN.png</c> and recorded in <see cref="ContentPackageManifest.Screenshots"/>.
            /// Empty (the default) = no screenshots (the publish gate requires ≥1).</summary>
            public List<string> ScreenshotPaths { get; set; } = new();

            /// <summary>Story 9.8 — explicit IP-ownership consent, written into
            /// <see cref="ContentPackageManifest.IpConsent"/>. Defaults false (the publish gate refuses without it).</summary>
            public bool IpConsent { get; set; }

            /// <summary>Story 9.9 — absolute on-disk paths to custom binary assets (GLB meshes) to bundle. Each existing
            /// file is copied into the package at <c>assets/{name}</c> (recorded in list order in
            /// <see cref="ContentPackageManifest.AssetFiles"/>) and its filename+bytes folded (ordinal-sorted) into
            /// <see cref="ContentPackageManifest.AssetHash"/>. Empty (the default) = no assets bundled.</summary>
            public List<string> AssetPaths { get; set; } = new();
        }

        /// <summary>DW-423 regression seam — invoked by <see cref="Pack"/> after every source has been
        /// enumerated, snapshotted and hashed and before any archive entry is written: exactly the window the
        /// old hash-then-re-read TOCTOU lived in. Tests assign a delegate that mutates the on-disk source files
        /// to prove the packaged bytes are the enumeration-time snapshots; production never sets it (null ⇒ no-op).
        /// DW-716 widened what this proves from the three integrity-hashed inputs (scenario/terrain/asset) to
        /// EVERY packaged source, including the non-hashed thumbnail/faction/screenshot families.
        /// ThreadStatic so a parallel test class packing concurrently can never observe another test's hook.</summary>
#pragma warning disable CS0649 // assigned only by the Tier-1 test assembly (which compiles this file directly)
        [ThreadStatic] internal static Action? PackTestHookAfterHash;

        /// <summary>DW-560 regression seam — invoked by <see cref="Pack"/> after the staged archive has fully
        /// committed on Dispose and immediately BEFORE the <see cref="File.Move(string,string,bool)"/> that
        /// publishes it over the output path. Tests throw from here to prove that a failure at the commit
        /// boundary still leaves a pre-existing export byte-identical and leaves no staging residue behind.
        /// Production never sets it (null ⇒ no-op); ThreadStatic for the same reason as
        /// <see cref="PackTestHookAfterHash"/>. It exists because DW-716 removed the last write-loop re-read
        /// (every source is now snapshotted at enumeration), which also removed the only source-deletion
        /// fault-injection point inside the staged-write region.</summary>
        [ThreadStatic] internal static Action? PackTestHookBeforeCommit;
#pragma warning restore CS0649

        /// <summary>DW-560 — the suffix <see cref="Pack"/> stages its export on, beside the output file.</summary>
        internal const string PackStagingSuffix = ".pack.tmp";

        /// <summary>DW-421 — the suffix <see cref="RewriteManifest(string,Func{byte[]})"/> stages on, beside the
        /// shipped package.</summary>
        internal const string RewriteStagingSuffix = ".rewrite.tmp";

        /// <summary>
        /// Pack a scenario file (and optional extras) into a .chimera.zip.
        /// </summary>
        /// <param name="scenarioAbsPath">Absolute path to the scenario JSON to pack.</param>
        /// <param name="outputZipPath">Absolute path for the output .chimera.zip file.</param>
        /// <param name="options">Display metadata for the package.</param>
        /// <param name="terrainDir">Story 6.2 — optional absolute path to a folder of Terrain3D region .res files
        /// to bundle under map/terrain/. Null/missing/empty ⇒ no terrain bundled (a terrainless map).</param>
        /// <returns>The generated <see cref="ContentPackageManifest"/>.</returns>
        public static ContentPackageManifest Pack(string scenarioAbsPath, string outputZipPath,
                                                   PackOptions options, string? terrainDir = null)
        {
            if (!File.Exists(scenarioAbsPath))
                throw new FileNotFoundException("Scenario file not found.", scenarioAbsPath);

            // Generate a slug ID from the display name.
            string id = Slugify(options.DisplayName);

            // Hash the scenario bytes for integrity verification. DW-423: read the bytes ONCE — the exact
            // buffer hashed here is the buffer written into the archive below, so a source file mutated
            // mid-Pack can no longer produce a package whose recorded hash disagrees with its own packaged
            // bytes (the hash-then-rewrite TOCTOU that made a package fail its own Unpack).
            byte[] scenarioBytes = File.ReadAllBytes(scenarioAbsPath);
            uint scenarioHash = ScenarioSerializer.ComputeHash(scenarioBytes);

            // Build faction_files list (zip-relative paths).
            // DW-716: snapshot the bytes HERE, at enumeration, exactly like the integrity-hashed inputs (DW-423).
            // The write loop below must never re-read a source: a faction file swapped between the File.Exists
            // check and the write would package content the creator never saw, and no integrity hash covers this
            // family, so nothing downstream could ever notice.
            // DW-821: two sources from different directories that share a file NAME both map to the single zip
            // entry factions/{leaf}. ZipArchive.CreateEntry permits duplicate entry names, manifest.FactionFiles
            // would list the same path twice, and Unpack's GetEntry returns only the FIRST — so the second
            // faction's bytes are silently lost. Reject at Pack, mirroring the Story-9.9 asset-leaf guard below
            // (which rejects for the same reason; assets merely fail their own integrity check instead of
            // vanishing quietly).
            var factionEntries   = new List<string>();
            var factionSnapshots = new List<(string Leaf, byte[] Bytes)>();
            var factionLeaves    = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fp in options.FactionPaths)
            {
                if (!File.Exists(fp)) continue;
                string factionLeaf = Path.GetFileName(fp);
                if (!factionLeaves.Add(factionLeaf))
                    throw new ArgumentException(
                        $"Duplicate faction file name '{factionLeaf}': bundled faction names must be unique " +
                        $"(each maps to factions/{factionLeaf}).", nameof(options));
                factionEntries.Add("factions/" + factionLeaf);
                factionSnapshots.Add((factionLeaf, File.ReadAllBytes(fp)));
            }

            // Story 9.8: enumerate the on-disk screenshots into canonical zip-relative paths (screenshots/shot_NN.png).
            // Only existing files contribute; the index is assigned in list order so the manifest and the written
            // entries stay in lock-step. Recorded on the manifest below and counted by the publish gate.
            // DW-716: read-once, like every other source — the bytes written are the bytes enumeration saw.
            var screenshotEntries   = new List<string>();
            var screenshotSnapshots = new List<byte[]>();
            foreach (var sp in options.ScreenshotPaths ?? new List<string>())
            {
                if (!File.Exists(sp)) continue;
                screenshotEntries.Add($"screenshots/shot_{screenshotEntries.Count:D2}.png");
                screenshotSnapshots.Add(File.ReadAllBytes(sp));
            }

            // Story 6.7 / legacy thumbnail (optional) — DW-716 read-once snapshot. The manifest's ThumbnailFile
            // slot below stays keyed on ThumbnailPath being NON-NULL rather than on the file existing (Unpack
            // treats a listed-but-missing image as "no image", pre-6.7 parity), so a path that does not exist
            // still records the slot and writes no entry — byte-identical to the previous behaviour.
            byte[]? thumbnailBytes = options.ThumbnailPath != null && File.Exists(options.ThumbnailPath)
                ? File.ReadAllBytes(options.ThumbnailPath)
                : null;

            // Story 6.2: enumerate the terrain region files (ordinal-sorted by name so the aggregate integrity hash
            // is order-independent), record them zip-relative under map/terrain/, and fold their filename+bytes.
            // DW-423: each region is read ONCE into a snapshot — the same bytes are hashed here and written below.
            var terrainEntries = new List<string>();
            var terrainSnapshots = new List<(string Leaf, byte[] Bytes)>();
            uint terrainHash = 0u;
            if (!string.IsNullOrEmpty(terrainDir) && Directory.Exists(terrainDir))
            {
                string[] terrainFiles = Directory.EnumerateFiles(terrainDir, "*.res", SearchOption.TopDirectoryOnly)
                                                 .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                                                 .ToArray();
                foreach (var f in terrainFiles)
                {
                    string leaf = Path.GetFileName(f);
                    terrainEntries.Add("map/terrain/" + leaf);
                    terrainSnapshots.Add((leaf, File.ReadAllBytes(f)));
                }
                // Review pass 2 (EC10): keep TerrainHash==0 the unambiguous "no terrain bundled" sentinel — an empty
                // terrain dir must not stamp a non-zero FNV-of-nothing that contradicts an empty TerrainFiles list.
                terrainHash = terrainSnapshots.Count == 0 ? 0u : HashNamedBytes(terrainSnapshots);
            }

            // Story 9.9: enumerate the bundled custom assets (GLB meshes) into canonical zip-relative paths
            // (assets/{name}). Only existing files contribute. AssetFiles preserves the caller's list order (so the
            // manifest and the written entries stay in lock-step, mirroring screenshots); the AssetHash folds the same
            // set ordinal-sorted by filename (so the aggregate integrity hash is input-order-independent, mirroring
            // terrain). AssetHash==0 is the unambiguous "no assets bundled" sentinel.
            // DW-423: each asset is read ONCE into a snapshot — the same bytes are hashed here and written below.
            var assetEntries = new List<string>();
            var assetSnapshots = new List<(string Leaf, byte[] Bytes)>();
            var assetLeaves  = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ap in options.AssetPaths ?? new List<string>())
            {
                if (!File.Exists(ap)) continue;
                string leaf = Path.GetFileName(ap);
                // Review pass (P3): two sources that share a leaf name would both map to assets/{leaf}. The hash folds
                // both distinct files but Unpack's GetEntry returns the single surviving entry twice, so the package
                // would fail its OWN integrity check on download. Reject at Pack with a clear error instead of shipping
                // a self-invalidating package.
                if (!assetLeaves.Add(leaf))
                    throw new ArgumentException(
                        $"Duplicate asset file name '{leaf}': bundled asset names must be unique " +
                        $"(each maps to assets/{leaf}).", nameof(options));

                // DW-424: mirror Unpack's extension + size rejects at Pack (fail fast at export) through the SAME
                // AssetValidator core the runtime ingest gate uses — previously a non-.glb or over-cap source packed
                // cleanly and every downloader's Unpack rejected it, a self-invalidating package discovered only
                // after publish. Stat-based pre-gate so an over-cap source is rejected without ever loading it.
                GateAsset(ap, leaf, new FileInfo(ap).Length);

                // DW-423: the single byte snapshot folded into AssetHash below AND written into the archive.
                byte[] bytes = File.ReadAllBytes(ap);

                // DW-424 (snapshot re-gate): the snapshot's length is what Unpack's per-entry size check will
                // measure; if the source grew between the stat and the read, reject the snapshot too.
                GateAsset(ap, leaf, bytes.LongLength);

                assetEntries.Add("assets/" + leaf);
                assetSnapshots.Add((leaf, bytes));
            }
            uint assetHash = assetSnapshots.Count == 0
                ? 0u
                : HashNamedBytes(assetSnapshots.OrderBy(s => s.Leaf, StringComparer.Ordinal));

            // DW-424 — the Pack-side twin of Unpack's per-entry extension/size rejects (kept locally so the two
            // pack/unpack gates read side by side in one file). Static local ⇒ no capture; nameof is compile-time.
            static void GateAsset(string sourcePath, string leaf, long byteLength)
            {
                var gate = AssetValidator.Validate(leaf, byteLength);
                if (!gate.Ok)
                    throw new ArgumentException(
                        $"Cannot pack asset '{sourcePath}': {gate.Reason} A package bundling it would fail " +
                        $"its own integrity check at every downloader's Unpack.", nameof(options));
            }

            var manifest = new ContentPackageManifest
            {
                Id              = id,
                DisplayName     = options.DisplayName,
                Description     = options.Description,
                Author          = options.Author,
                Version         = options.Version,
                MinGameVersion  = options.MinGameVersion,
                Tags            = options.Tags,
                PlayerCount     = options.PlayerCount,
                ScenarioFile    = "scenario.json",
                // Story 6.7: the auto-generated minimap preview wires the previously-dead ThumbnailFile slot. Preview
                // bytes take precedence over the legacy on-disk ThumbnailPath; neither ⇒ null (omitted, pre-6.7 parity).
                ThumbnailFile   = options.PreviewPngBytes != null && options.PreviewPngBytes.Length > 0 ? "preview/preview.png"
                                : options.ThumbnailPath != null   ? "thumbnail.png"
                                : null,
                FactionFiles    = factionEntries,
                ScenarioHash    = scenarioHash,
                TerrainFiles    = terrainEntries,
                TerrainHash     = terrainHash,
                // Story 9.9: bundled custom assets (GLB) + their aggregate integrity hash (the terrain-hash sibling).
                AssetFiles      = assetEntries,
                AssetHash       = assetHash,
                // Story 9.8: proof-of-play token + screenshots + IP-ownership consent (the pre-publish quality/IP gate
                // fields). The gate at upload verifies the token, re-derives the canonical hash for staleness, and
                // enforces thumbnail/description/screenshots/consent before ModIoService.UploadModAsync.
                ProofOfPlay     = options.Token,
                Screenshots     = screenshotEntries,
                IpConsent       = options.IpConsent,
                // AR-36 allow-list (Story 1.10b): packaging-time wall-clock stamped when EXPORTING a
                // .chimera.zip — never tick-reachable and never folded into the sim/start-state hash, so it is
                // an explicit RS0030 exemption (keeps the banned-API release gate at a clean zero baseline). A
                // new DateTime in tick code is NOT exempted and still fails the release gate.
#pragma warning disable RS0030
                CreatedAt       = DateTime.UtcNow.ToString("o"),
#pragma warning restore RS0030
            };

            // DW-423 / DW-716 regression seam — fires between source snapshotting (+ integrity hashing) and
            // archive writing, the window the old enumerate-then-re-read TOCTOU lived in. Tests mutate the
            // on-disk sources here to prove the packaged bytes are the snapshots, not a re-read. Null in production.
            PackTestHookAfterHash?.Invoke();

            // DW-560 — stage the export on a temp SIBLING of the output and only rename it over
            // outputZipPath once the archive has fully committed on Dispose. The previous shape deleted an
            // existing export up front and then wrote straight into ZipArchiveMode.Create, so a throw mid-write
            // destroyed the creator's previous export AND left a partial zip behind. Same
            // stage-to-temp-then-atomic-File.Move pattern RewriteManifest uses (DW-421): a sibling path shares
            // the volume, so the Move is an atomic rename.
            // (DW-716 narrowed the throw-mid-write window considerably — every source is now snapshotted at
            // enumeration, so a source deleted or swapped after enumeration can no longer throw from the write
            // loop. The staging remains load-bearing for everything else that can fail here: a full disk, a
            // transient IO fault, or a failure at the commit boundary itself.)
            string stagePath = outputZipPath + PackStagingSuffix;
            try
            {
                // DW-727: ZipArchiveMode.Create maps to FileMode.CreateNew, which THROWS when anything already
                // occupies the path — so a staging FILE left behind by a killed editor, or by the catch block
                // below (whose best-effort delete deliberately swallows its own failure so as not to mask the
                // real error), takes the NEXT export down with it. Clear the residue first, symmetric with
                // RewriteManifest's File.Copy(..., overwrite: true) staging. File.Exists-guarded on purpose: a
                // DIRECTORY squatting the staging name is an environment fault, not residue this method left,
                // and must keep surfacing as the UnauthorizedAccessException it is rather than as a confusing
                // "access denied" from File.Delete.
                if (File.Exists(stagePath)) File.Delete(stagePath);

                using (var archive = ZipFile.Open(stagePath, ZipArchiveMode.Create))
                {
                    // manifest.json
                    string manifestJson = JsonSerializer.Serialize(manifest, _jsonOpts);
                    WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));

                    // scenario.json — the SAME byte snapshot ScenarioHash covered (DW-423), never a re-read.
                    WriteEntry(archive, "scenario.json", scenarioBytes);

                    // preview/preview.png (Story 6.7, optional) — the auto-generated top-down minimap preview. Takes
                    // precedence over the legacy on-disk thumbnail so a package never carries two conflicting preview slots.
                    if (options.PreviewPngBytes != null && options.PreviewPngBytes.Length > 0)
                        WriteEntry(archive, "preview/preview.png", options.PreviewPngBytes);
                    // thumbnail.png (legacy, optional) — only when no generated preview was supplied; the
                    // enumeration-time snapshot (DW-716), never a re-read.
                    else if (thumbnailBytes != null)
                        WriteEntry(archive, "thumbnail.png", thumbnailBytes);

                    // factions/ (optional) — the enumeration-time snapshots (DW-716), leaf-unique (DW-821).
                    foreach (var (leaf, bytes) in factionSnapshots)
                        WriteEntry(archive, "factions/" + leaf, bytes);

                    // map/terrain/ (Story 6.2, optional) — the same ordinal-sorted snapshots TerrainHash folded (DW-423).
                    foreach (var (leaf, bytes) in terrainSnapshots)
                        WriteEntry(archive, "map/terrain/" + leaf, bytes);

                    // screenshots/ (Story 9.8, optional) — the same list order the manifest recorded, so entry names
                    // match, and the same enumeration-time snapshots (DW-716).
                    for (int i = 0; i < screenshotEntries.Count; i++)
                        WriteEntry(archive, screenshotEntries[i], screenshotSnapshots[i]);

                    // assets/ (Story 9.9, optional) — the same list order the manifest recorded, so entry names match;
                    // the bytes are the same snapshots AssetHash folded (DW-423), never a re-read.
                    for (int i = 0; i < assetEntries.Count; i++)
                        WriteEntry(archive, assetEntries[i], assetSnapshots[i].Bytes);
                }

                // DW-560 regression seam — the commit boundary. Null (no-op) in production.
                PackTestHookBeforeCommit?.Invoke();

                // Commit: the staged archive is complete and closed, so this rename either publishes a whole,
                // readable export or (on failure) leaves the previous one exactly as it was.
                File.Move(stagePath, outputZipPath, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the staged partial; any pre-existing export at outputZipPath was never
                // touched, so it stays byte-identical and readable either way.
                try { if (File.Exists(stagePath)) File.Delete(stagePath); }
                catch { /* leave the temp file rather than mask the real error */ }
                throw;
            }

            return manifest;
        }

        // ── Unpack ────────────────────────────────────────────────────────────────

        public class UnpackResult
        {
            /// <summary>The parsed manifest.</summary>
            public ContentPackageManifest Manifest { get; init; } = null!;
            /// <summary>Absolute path to the extracted scenario.json.</summary>
            public string ScenarioPath { get; init; } = "";
            /// <summary>Absolute path to the extracted thumbnail.png, or null.</summary>
            public string? ThumbnailPath { get; init; }
            /// <summary>Absolute paths to extracted faction JSON files.</summary>
            public List<string> FactionPaths { get; init; } = new();
            /// <summary>Story 6.2 — absolute paths to extracted Terrain3D region .res files (empty if none bundled).</summary>
            public List<string> TerrainFiles { get; init; } = new();
            /// <summary>Story 9.9 — absolute paths to extracted custom asset (GLB) files (empty if none bundled). Each
            /// lives under an <c>assets/</c> subdir of the extract directory; its manifest zip-relative logical id
            /// (e.g. "assets/heavy_tank.glb") is the AssetRegistry key a custom unit's MeshPath references.</summary>
            public List<string> AssetFiles { get; init; } = new();
        }

        /// <summary>
        /// Extract a .chimera.zip package to a directory.
        /// The directory is created if it does not exist.
        ///
        /// <para>DW-426 — on success the validated <c>manifest.json</c> is materialized into
        /// <paramref name="extractDir"/> as the LAST step, only after every integrity check passed. Its presence is
        /// therefore the "extraction completed + verified" seal: the load-path asset ingest
        /// (<c>FactionVisualsPhase.IngestImportedAssets</c>) reads it via <see cref="ReadExtractedManifest"/> and
        /// ingests ONLY its <c>AssetFiles</c> logical ids, so a partial extraction (throw mid-unpack ⇒ no seal) or an
        /// orphan file a raw directory scan would have picked up can never be ingested unverified.</para>
        /// </summary>
        /// <param name="zipPath">Absolute path to the .chimera.zip file.</param>
        /// <param name="extractDir">Absolute path to the output directory.</param>
        /// <param name="cleanExtractDir">DW-426 — when true, <paramref name="extractDir"/> is deleted (recursively)
        /// before extraction so stale/orphan files from a prior extraction of a same-Id package cannot survive into
        /// the fresh materialization. Opt-in: default false preserves the historical non-destructive behavior for
        /// callers extracting into throwaway dirs they manage themselves.</param>
        /// <exception cref="InvalidDataException">
        /// If manifest.json is missing, malformed, or the scenario hash doesn't match.
        /// </exception>
        public static UnpackResult Unpack(string zipPath, string extractDir, bool cleanExtractDir = false)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("Package file not found.", zipPath);

            // DW-426: clear the target first (only once the source zip is known to exist) so a re-import of a
            // same-Id package can never inherit the previous import's leftover files.
            if (cleanExtractDir && Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            Directory.CreateDirectory(extractDir);

            using var archive = ZipFile.OpenRead(zipPath);

            // 1. Read and validate manifest.
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("Package is missing manifest.json.");

            ContentPackageManifest manifest;
            using (var ms = new MemoryStream())
            {
                manifestEntry.Open().CopyTo(ms);
                manifest = JsonSerializer.Deserialize<ContentPackageManifest>(
                    Encoding.UTF8.GetString(ms.ToArray()), _jsonOpts)
                    ?? throw new InvalidDataException("Failed to parse manifest.json.");
            }

            // 2. Extract scenario.json and verify hash.
            string scenarioEntry = manifest.ScenarioFile ?? "scenario.json";
            string scenarioOut   = Path.Combine(extractDir, "scenario.json");

            var scenEntry = archive.GetEntry(scenarioEntry)
                ?? throw new InvalidDataException($"Package is missing '{scenarioEntry}'.");
            scenEntry.ExtractToFile(scenarioOut, overwrite: true);

            // Verify integrity if a hash was recorded.
            if (manifest.ScenarioHash != 0)
            {
                uint actualHash = ScenarioSerializer.ComputeFileHash(scenarioOut);
                if (actualHash != manifest.ScenarioHash)
                    throw new InvalidDataException(
                        $"Scenario integrity check failed: expected 0x{manifest.ScenarioHash:X8}, " +
                        $"got 0x{actualHash:X8}. Package may be corrupt.");
            }

            // 3. Extract the preview/thumbnail image (optional). Story 6.7: ThumbnailFile may now be either the
            //    legacy "thumbnail.png" or the generated "preview/preview.png" — extract to the manifest-named entry's
            //    basename so both round-trip (a listed-but-missing image is treated as "no image", pre-6.7 parity).
            string? thumbOut = null;
            if (!string.IsNullOrEmpty(manifest.ThumbnailFile))
            {
                var thumbEntry = archive.GetEntry(manifest.ThumbnailFile);
                if (thumbEntry != null)
                {
                    thumbOut = Path.Combine(extractDir, Path.GetFileName(manifest.ThumbnailFile));
                    thumbEntry.ExtractToFile(thumbOut, overwrite: true);
                }
            }

            // 4. Extract faction files (optional).
            var factionOuts = new List<string>();
            foreach (var factionZipPath in manifest.FactionFiles)
            {
                var entry = archive.GetEntry(factionZipPath);
                if (entry == null) continue;
                string dest = Path.Combine(extractDir, Path.GetFileName(factionZipPath));
                entry.ExtractToFile(dest, overwrite: true);
                factionOuts.Add(dest);
            }

            // 5. Extract terrain region files (Story 6.2, optional) + verify aggregate integrity hash.
            var terrainOuts = new List<string>();
            if (manifest.TerrainFiles != null && manifest.TerrainFiles.Count > 0)
            {
                string terrainOutDir = Path.Combine(extractDir, "terrain");
                Directory.CreateDirectory(terrainOutDir);
                foreach (var terrainZipPath in manifest.TerrainFiles)
                {
                    // Review pass 2 (EC8): a listed terrain file absent from the archive is a corrupt/incomplete
                    // package, not a silent skip — dropping it would restore partial terrain unnoticed.
                    var entry = archive.GetEntry(terrainZipPath)
                        ?? throw new InvalidDataException(
                            $"Terrain integrity check failed: manifest lists {terrainZipPath} but it is " +
                            $"missing from the package.");
                    string dest = Path.Combine(terrainOutDir, Path.GetFileName(terrainZipPath));
                    entry.ExtractToFile(dest, overwrite: true);
                    terrainOuts.Add(dest);
                }

                // Verify integrity whenever the manifest lists terrain files (review pass 2, F8/EC7): gating on
                // TerrainHash!=0 both overloaded 0 as a legitimate-but-unverified hash AND let a tampered manifest
                // skip the check by zeroing the field. Every terrain-bearing package is produced by Pack above, which
                // always records a hash, so an unconditional verify here has no false positives.
                string[] sorted = terrainOuts
                    .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
                uint actualHash = HashFiles(sorted);
                if (actualHash != manifest.TerrainHash)
                    throw new InvalidDataException(
                        $"Terrain integrity check failed: expected 0x{manifest.TerrainHash:X8}, " +
                        $"got 0x{actualHash:X8}. Package may be corrupt.");
            }

            // 6. Extract custom asset files (Story 9.9, optional) + verify aggregate integrity hash. Mirrors the terrain
            //    path exactly: a listed-but-absent asset is a corrupt/incomplete package (a located throw, never a
            //    silent skip), and the recomputed hash is verified unconditionally whenever the manifest lists assets so
            //    an asset byte damaged in transit is caught. Like ScenarioHash/TerrainHash this is an UNKEYED FNV
            //    corruption check, not tamper-proofing — clearing AssetFiles skips the check and the hash is
            //    recomputable; a re-packaged archive is out of scope (server attestation is the later online rail).
            var assetOuts = new List<string>();
            if (manifest.AssetFiles != null && manifest.AssetFiles.Count > 0)
            {
                string assetOutDir = Path.Combine(extractDir, "assets");
                Directory.CreateDirectory(assetOutDir);
                foreach (var assetZipPath in manifest.AssetFiles)
                {
                    var entry = archive.GetEntry(assetZipPath)
                        ?? throw new InvalidDataException(
                            $"Asset integrity check failed: manifest lists {assetZipPath} but it is " +
                            $"missing from the package.");

                    // Review pass (P4): gate the extension + per-entry uncompressed size at extraction — not only at
                    // the render-path ingest — so a hostile listed entry (disallowed type or a decompression bomb)
                    // never lands on disk. Located throws mirror the missing-entry reject above.
                    if (!AssetValidator.IsAllowedExtension(Path.GetExtension(assetZipPath)))
                        throw new InvalidDataException(
                            $"Asset integrity check failed: '{assetZipPath}' has a disallowed extension " +
                            $"(only {string.Join(", ", AssetValidator.AllowedExtensions)} allowed).");
                    if (entry.Length > AssetValidator.MaxAssetBytes)
                        throw new InvalidDataException(
                            $"Asset integrity check failed: '{assetZipPath}' is {entry.Length} bytes, over the " +
                            $"{AssetValidator.MaxAssetBytes}-byte cap.");

                    string dest = Path.Combine(assetOutDir, Path.GetFileName(assetZipPath));
                    entry.ExtractToFile(dest, overwrite: true);
                    assetOuts.Add(dest);
                }

                string[] sorted = assetOuts
                    .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToArray();
                uint actualHash = HashFiles(sorted);
                if (actualHash != manifest.AssetHash)
                    throw new InvalidDataException(
                        $"Asset integrity check failed: expected 0x{manifest.AssetHash:X8}, " +
                        $"got 0x{actualHash:X8}. Package may be corrupt.");
            }

            // 7. DW-426 — materialize the manifest into the extract dir LAST, after every integrity check above has
            //    passed, so its presence on disk is the unambiguous "extraction completed + verified" seal. A throw
            //    anywhere above leaves the partial extraction UNsealed and the manifest-driven load-path ingest
            //    (ReadExtractedManifest ⇒ null) refuses to touch it.
            manifestEntry.ExtractToFile(Path.Combine(extractDir, "manifest.json"), overwrite: true);

            return new UnpackResult
            {
                Manifest     = manifest,
                ScenarioPath = scenarioOut,
                ThumbnailPath = thumbOut,
                FactionPaths  = factionOuts,
                TerrainFiles  = terrainOuts,
                AssetFiles    = assetOuts,
            };
        }

        /// <summary>
        /// DW-426 — read the <c>manifest.json</c> a successful <see cref="Unpack"/> materialized into
        /// <paramref name="extractDir"/> (its verified-extraction seal). Returns null when the file is absent
        /// (legacy/pre-seal or partial extraction) or malformed — the load-path ingest treats null as "nothing
        /// verifiable to ingest" rather than falling back to a raw directory scan.
        /// </summary>
        /// <param name="extractDir">Absolute path to a directory a package was extracted into.</param>
        public static ContentPackageManifest? ReadExtractedManifest(string extractDir)
        {
            try
            {
                string manifestPath = Path.Combine(extractDir, "manifest.json");
                if (!File.Exists(manifestPath)) return null;
                return JsonSerializer.Deserialize<ContentPackageManifest>(
                    File.ReadAllText(manifestPath), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        // ── Quarantine a rejected download (DW-425) ──────────────────────────────

        /// <summary>
        /// DW-425 — move a rejected (failed-integrity / failed-verify) downloaded package OUT of the local-packages
        /// scan directory into <paramref name="quarantineDir"/>, so <see cref="ScanDirectory"/> (the content
        /// browser's local-tab listing) can never re-offer it as a playable local card. The move is collision-safe
        /// (an already-quarantined file of the same name is never overwritten — the new file gets a numbered prefix)
        /// and preserves the bytes for diagnostics. If the move itself fails, the file is deleted instead —
        /// deletion also satisfies the invariant; the one unacceptable outcome is leaving the rejected package
        /// in the scan directory.
        /// </summary>
        /// <param name="zipPath">Absolute path to the rejected package file.</param>
        /// <param name="quarantineDir">Absolute path to the quarantine directory (created if absent).</param>
        /// <returns>The quarantined file's absolute path, or null when nothing was moved (source absent, or the
        /// move failed and the fallback delete ran).</returns>
        public static string? QuarantineRejectedPackage(string zipPath, string quarantineDir)
        {
            if (!File.Exists(zipPath)) return null;
            try
            {
                Directory.CreateDirectory(quarantineDir);

                // Collision-safe destination: prefix (not suffix) a counter so the ".chimera.zip" double extension
                // survives intact; past a sanity cap fall back to a GUID prefix which cannot collide.
                string leaf = Path.GetFileName(zipPath);
                string dest = Path.Combine(quarantineDir, leaf);
                for (int n = 2; File.Exists(dest); n++)
                {
                    if (n > 99)
                    {
                        // RS0030 suppression (15-24a release-gate sweep): the ban exists for SIM determinism;
                        // this is a local-FILESYSTEM collision-escape name for a quarantine move — authoring-side,
                        // never sim state, never folded, never on the wire. A seeded scheme would defeat the
                        // purpose (the whole point is a name no prior run could have produced).
#pragma warning disable RS0030
                        dest = Path.Combine(quarantineDir, Guid.NewGuid().ToString("N") + "_" + leaf);
#pragma warning restore RS0030
                        break;
                    }
                    dest = Path.Combine(quarantineDir, $"{n}_{leaf}");
                }

                File.Move(zipPath, dest);
                return dest;
            }
            catch
            {
                // Fallback: deleting still keeps the rejected package out of the scan dir. Best-effort — if even
                // this fails (e.g. the file is transiently locked), the caller's next verify pass rejects it again.
                try { File.Delete(zipPath); } catch { /* best-effort */ }
                return null;
            }
        }

        // ── Rewrite manifest in place (Story 9.8, publish-time consent) ──────────

        /// <summary>
        /// Story 9.8 (review P2) — overwrite the <c>manifest.json</c> entry of an existing .chimera.zip with
        /// <paramref name="manifest"/>, leaving every other entry untouched (ZipArchiveMode.Update). Used at publish
        /// time to record the creator's live IP-ownership consent (and any gate-evaluated fields) INTO the shipped
        /// package before upload, so the on-disk zip reflects the consent the gate approved — not the export-time
        /// default. Godot-free (System.IO.Compression) so it is Tier-1 testable.
        /// </summary>
        /// <param name="zipPath">Absolute path to the .chimera.zip to update in place.</param>
        /// <param name="manifest">The manifest to serialize over the existing <c>manifest.json</c>.</param>
        public static void RewriteManifest(string zipPath, ContentPackageManifest manifest) =>
            RewriteManifest(zipPath, () => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, _jsonOpts)));

        /// <summary>
        /// DW-421 core (internal so the atomicity contract is fault-injection testable) — the previous
        /// implementation deleted <c>manifest.json</c> and re-wrote it inside one <c>ZipArchiveMode.Update</c>
        /// session on the SHIPPED zip, so a throw between the delete and the commit-on-Dispose flushed a package
        /// whose only manifest had already been deleted: permanently unreadable by every future
        /// <see cref="ReadManifest"/>/<see cref="Unpack"/>. Now atomic: (1) serialize BEFORE touching any file, so
        /// a serializer throw leaves the package untouched; (2) stage the rewrite on a temp copy beside the
        /// original (same directory ⇒ same volume ⇒ <see cref="File.Move(string,string,bool)"/> is an atomic
        /// rename) and only swap it over the shipped zip after the updated archive fully committed on Dispose.
        /// A throw anywhere — serialize, entry write, or the Update-mode commit itself — unwinds with the
        /// original .chimera.zip intact.
        /// </summary>
        internal static void RewriteManifest(string zipPath, Func<byte[]> serializeManifest)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("Package file not found.", zipPath);

            // (1) Serialize first — before the original is copied or opened.
            byte[] data = serializeManifest();

            // (2) Stage on a temp sibling, then atomically replace the original on success.
            string tmpPath = zipPath + RewriteStagingSuffix;
            try
            {
                File.Copy(zipPath, tmpPath, overwrite: true);
                using (var archive = ZipFile.Open(tmpPath, ZipArchiveMode.Update))
                {
                    archive.GetEntry("manifest.json")?.Delete();
                    var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(data, 0, data.Length);
                }
                File.Move(tmpPath, zipPath, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the staged copy; the shipped original is untouched either way.
                try { File.Delete(tmpPath); } catch { /* leave the temp file rather than mask the real error */ }
                throw;
            }
        }

        // ── Read manifest only (for content browser preview) ─────────────────────

        /// <summary>
        /// Read only the manifest from a .chimera.zip without extracting anything else.
        /// Used by the content browser to display package info without full extraction.
        /// Returns null if the package is invalid.
        /// </summary>
        public static ContentPackageManifest? ReadManifest(string zipPath)
        {
            if (!File.Exists(zipPath)) return null;
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry("manifest.json");
                if (entry == null) return null;
                using var ms = new MemoryStream();
                entry.Open().CopyTo(ms);
                return JsonSerializer.Deserialize<ContentPackageManifest>(
                    Encoding.UTF8.GetString(ms.ToArray()), _jsonOpts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Read manifests from all .chimera.zip files in a directory.
        /// Used by the local content browser to enumerate installed packages.
        ///
        /// <para>DW-717 — also sweeps stale staging residue out of <paramref name="directory"/> on entry (see
        /// <see cref="SweepStaleStagingFiles"/>). This is the one code path that already walks the user's
        /// packages directory, and nothing else ever looked at the leaked temp files.</para>
        /// </summary>
        public static IEnumerable<(string ZipPath, ContentPackageManifest Manifest)>
            ScanDirectory(string directory)
        {
            // Deliberately a thin non-iterator wrapper: an iterator body does not run until the caller
            // enumerates, so a sweep placed inside ScanDirectoryCore would silently never run for a caller
            // that only counts or short-circuits. Doing it here makes the sweep happen at CALL time.
            SweepStaleStagingFiles(directory);
            return ScanDirectoryCore(directory);
        }

        private static IEnumerable<(string ZipPath, ContentPackageManifest Manifest)>
            ScanDirectoryCore(string directory)
        {
            if (!Directory.Exists(directory)) yield break;
            foreach (var file in Directory.EnumerateFiles(directory, "*.chimera.zip",
                                                           SearchOption.TopDirectoryOnly))
            {
                var m = ReadManifest(file);
                if (m != null) yield return (file, m);
            }
        }

        // ── Stale staging-file sweep (DW-717) ────────────────────────────────────

        /// <summary>DW-717 — how old (in hours) a leaked staging file must be before the sweep removes it.
        /// Deliberately generous: a CONCURRENT export or manifest rewrite (this process or another) writes its
        /// staging file with a fresh timestamp, so a gate this wide can never race a live write, while still
        /// bounding the residue to "gone by tomorrow's browse".</summary>
        internal const int StaleStagingAgeHours = 24;

        /// <summary>
        /// DW-717 — delete leaked <c>*.pack.tmp</c> / <c>*.rewrite.tmp</c> staging files older than
        /// <paramref name="minAgeHours"/> from the TOP LEVEL of <paramref name="directory"/>.
        ///
        /// <para>Both atomicity paths (<see cref="Pack"/> and <see cref="RewriteManifest(string,Func{byte[]})"/>)
        /// best-effort-delete their own staging file and deliberately swallow a failure to do so, so as not to
        /// mask the real error. If that delete loses — a transient AV/indexer lock, or the editor being killed
        /// mid-write — the temp file sits in the user's packages directory forever: <see cref="ScanDirectory"/>'s
        /// <c>*.chimera.zip</c> pattern does not match it and nothing else ever looks. Worse, per DW-727 a stale
        /// <c>.pack.tmp</c> is not inert clutter but a tripwire under the next export to the same path.</para>
        ///
        /// <para>Best-effort PER FILE: a delete that still fails (locked, permissions) is skipped rather than
        /// thrown — a housekeeping sweep must never be able to break the listing it runs in front of.</para>
        /// </summary>
        /// <param name="directory">Absolute path to the packages directory to sweep. Missing ⇒ no-op.</param>
        /// <param name="minAgeHours">Minimum age, in hours, before a staging file is considered abandoned.</param>
        /// <returns>The number of stale staging files actually deleted.</returns>
        internal static int SweepStaleStagingFiles(string directory, int minAgeHours = StaleStagingAgeHours)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

            // AR-36 allow-list (Story 1.10b): off-tick FILE-SYSTEM housekeeping wall-clock — never tick-reachable
            // and never folded into the sim/start-state hash, so it is an explicit RS0030 exemption exactly like
            // Pack's CreatedAt export stamp. A new DateTime in tick code is NOT exempted and still fails the gate.
#pragma warning disable RS0030
            DateTime cutoffUtc = DateTime.UtcNow.AddHours(-Math.Abs((double)minAgeHours));
#pragma warning restore RS0030

            int deleted = 0;
            foreach (string suffix in new[] { PackStagingSuffix, RewriteStagingSuffix })
            {
                string[] candidates;
                try { candidates = Directory.GetFiles(directory, "*" + suffix, SearchOption.TopDirectoryOnly); }
                catch { continue; }   // unreadable directory: nothing to sweep, never a throw out of a listing

                foreach (string f in candidates)
                {
                    // Belt to the glob's braces: Windows' legacy 8.3 short-name matching can widen a
                    // "*.pack.tmp" pattern, so re-check the suffix explicitly. A sweep that DELETES must only
                    // ever match names it is certain about.
                    if (!f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
#pragma warning disable RS0030 // AR-36 allow-list: see the cutoff comment above.
                        if (File.GetLastWriteTimeUtc(f) > cutoffUtc) continue;   // young ⇒ possibly a live write
#pragma warning restore RS0030
                        File.Delete(f);
                        deleted++;
                    }
                    catch { /* still locked / no permission — leave it; the next scan retries */ }
                }
            }
            return deleted;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static void WriteEntry(ZipArchive archive, string entryName, byte[] data)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(data, 0, data.Length);
        }

        // FNV-1a 32-bit constants — same primitive as ScenarioSerializer's scenario integrity hash, so the terrain
        // integrity check reads as the sibling of the scenario one.
        private const uint FNV_PRIME  = 16777619u;
        private const uint FNV_OFFSET = 2166136261u;

        /// <summary>
        /// Story 6.2 (generalized Story 9.9) — aggregate FNV-1a hash over a set of files: for each file (in the given
        /// order — callers pass an ordinal-sort-by-filename set so the result is input-order-independent) fold its
        /// filename bytes then its content bytes. Filename inclusion catches a rename; content bytes catch a
        /// corrupt-in-transit file. The single folding shape shared by the terrain integrity hash and the Story 9.9
        /// asset integrity hash. Mirrors <see cref="ScenarioSerializer.ComputeFileHash"/> so all three integrity
        /// checks share one algorithm family.
        /// </summary>
        private static uint HashFiles(IEnumerable<string> absFiles) =>
            HashNamedBytes(absFiles.Select(f => (Path.GetFileName(f), File.ReadAllBytes(f))));

        /// <summary>
        /// DW-423 — the byte-snapshot core of <see cref="HashFiles"/>: folds each (Name, Bytes) pair exactly as
        /// HashFiles folds filename+content (HashFiles now delegates here, so the two can never drift). Pack
        /// hashes the in-memory snapshots it is about to write (read-once, closing the hash-then-re-read TOCTOU);
        /// Unpack keeps hashing the files it just extracted via the path-based wrapper.
        /// </summary>
        private static uint HashNamedBytes(IEnumerable<(string Name, byte[] Bytes)> files)
        {
            uint hash = FNV_OFFSET;
            foreach (var (name, bytes) in files)
            {
                foreach (byte b in Encoding.UTF8.GetBytes(name)) { hash ^= b; hash *= FNV_PRIME; }
                foreach (byte b in bytes) { hash ^= b; hash *= FNV_PRIME; }
            }
            return hash;
        }

        /// <summary>Story 6.2 — the terrain-folder name for a scenario stem ("{stem}_terrain"). The single naming
        /// convention shared by the export-time save-beside-scenario path and the import-time TerrainRef rewrite, so
        /// the two sites can never drift.</summary>
        internal static string TerrainFolderName(string stem) => $"{stem}_terrain";

        /// <summary>
        /// Story 6.2 — is <paramref name="fileName"/> a Terrain3D region file? A region is written as
        /// "terrain3d_XX_YY.res", but Terrain3DUtil.location_to_filename encodes a NEGATIVE region coordinate with a
        /// HYPHEN separator — e.g. the default flat region at location (-1,-1) is "terrain3d-01-01.res", (0,0) is
        /// "terrain3d_00_00.res". The load-side "does this folder hold regions?" check therefore MUST match a
        /// "terrain3d" prefix that is NOT anchored on an underscore, or every map whose regions sit at a negative
        /// location (i.e. essentially every map covering the origin) would be seen as having no regions and load flat.
        /// Extracted as a Godot-free predicate (review pass 2, VG2) so this load-bearing rule is Tier-1 unit-testable
        /// and a later "cleanup" to an underscore-anchored glob is caught by a red test rather than silently shipping.
        /// </summary>
        internal static bool IsTerrainRegionFile(string fileName) =>
            fileName.StartsWith("terrain3d", StringComparison.Ordinal) &&
            fileName.EndsWith(".res", StringComparison.Ordinal);

        /// <summary>Convert a display name to a slug: lowercase, spaces→hyphens, strip non-alnum.</summary>
        internal static string Slugify(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            // Collapse consecutive hyphens
            string result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-");
            return result.Trim('-');
        }
    }
}
