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

        /// <summary>DW-423 regression seam — invoked by <see cref="Pack"/> after all integrity hashes
        /// (scenario/terrain/asset) are computed and before any archive entry is written: exactly the window the
        /// old hash-then-re-read TOCTOU lived in. Tests assign a delegate that mutates the on-disk source files
        /// to prove the packaged bytes are the hashed snapshots; production never sets it (null ⇒ no-op).
        /// ThreadStatic so a parallel test class packing concurrently can never observe another test's hook.</summary>
#pragma warning disable CS0649 // assigned only by the Tier-1 test assembly (which compiles this file directly)
        [ThreadStatic] internal static Action? PackTestHookAfterHash;
#pragma warning restore CS0649

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
            var factionEntries = new List<string>();
            foreach (var fp in options.FactionPaths)
                if (File.Exists(fp))
                    factionEntries.Add("factions/" + Path.GetFileName(fp));

            // Story 9.8: enumerate the on-disk screenshots into canonical zip-relative paths (screenshots/shot_NN.png).
            // Only existing files contribute; the index is assigned in list order so the manifest and the written
            // entries stay in lock-step. Recorded on the manifest below and counted by the publish gate.
            var screenshotEntries = new List<string>();
            var screenshotSources = new List<string>();
            foreach (var sp in options.ScreenshotPaths ?? new List<string>())
            {
                if (!File.Exists(sp)) continue;
                screenshotEntries.Add($"screenshots/shot_{screenshotEntries.Count:D2}.png");
                screenshotSources.Add(sp);
            }

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

            // DW-423 regression seam — fires between integrity-hash computation and archive writing, the window
            // the old hash-then-re-read TOCTOU lived in. Tests mutate the on-disk sources here to prove the
            // packaged bytes are the hashed snapshots, not a re-read. Null (no-op) in production.
            PackTestHookAfterHash?.Invoke();

            // Delete existing output file if present.
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

            // manifest.json
            string manifestJson = JsonSerializer.Serialize(manifest, _jsonOpts);
            WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));

            // scenario.json — the SAME byte snapshot ScenarioHash covered (DW-423), never a re-read.
            WriteEntry(archive, "scenario.json", scenarioBytes);

            // preview/preview.png (Story 6.7, optional) — the auto-generated top-down minimap preview. Takes
            // precedence over the legacy on-disk thumbnail so a package never carries two conflicting preview slots.
            if (options.PreviewPngBytes != null && options.PreviewPngBytes.Length > 0)
                WriteEntry(archive, "preview/preview.png", options.PreviewPngBytes);
            // thumbnail.png (legacy, optional) — only when no generated preview was supplied.
            else if (options.ThumbnailPath != null && File.Exists(options.ThumbnailPath))
                WriteEntry(archive, "thumbnail.png", File.ReadAllBytes(options.ThumbnailPath));

            // factions/ (optional)
            foreach (var fp in options.FactionPaths)
            {
                if (!File.Exists(fp)) continue;
                WriteEntry(archive, "factions/" + Path.GetFileName(fp), File.ReadAllBytes(fp));
            }

            // map/terrain/ (Story 6.2, optional) — the same ordinal-sorted snapshots TerrainHash folded (DW-423).
            foreach (var (leaf, bytes) in terrainSnapshots)
                WriteEntry(archive, "map/terrain/" + leaf, bytes);

            // screenshots/ (Story 9.8, optional) — the same list order the manifest recorded, so entry names match.
            // (Screenshots are NOT integrity-hashed, so a write-time re-read carries no TOCTOU hash risk.)
            for (int i = 0; i < screenshotEntries.Count; i++)
                WriteEntry(archive, screenshotEntries[i], File.ReadAllBytes(screenshotSources[i]));

            // assets/ (Story 9.9, optional) — the same list order the manifest recorded, so entry names match;
            // the bytes are the same snapshots AssetHash folded (DW-423), never a re-read.
            for (int i = 0; i < assetEntries.Count; i++)
                WriteEntry(archive, assetEntries[i], assetSnapshots[i].Bytes);

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
        /// </summary>
        /// <param name="zipPath">Absolute path to the .chimera.zip file.</param>
        /// <param name="extractDir">Absolute path to the output directory.</param>
        /// <exception cref="InvalidDataException">
        /// If manifest.json is missing, malformed, or the scenario hash doesn't match.
        /// </exception>
        public static UnpackResult Unpack(string zipPath, string extractDir)
        {
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("Package file not found.", zipPath);

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
            string tmpPath = zipPath + ".rewrite.tmp";
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
        /// </summary>
        public static IEnumerable<(string ZipPath, ContentPackageManifest Manifest)>
            ScanDirectory(string directory)
        {
            if (!Directory.Exists(directory)) yield break;
            foreach (var file in Directory.EnumerateFiles(directory, "*.chimera.zip",
                                                           SearchOption.TopDirectoryOnly))
            {
                var m = ReadManifest(file);
                if (m != null) yield return (file, m);
            }
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
