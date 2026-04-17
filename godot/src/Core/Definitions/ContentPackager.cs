#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
            /// <summary>Absolute path to a 256×256 PNG thumbnail. Null = no thumbnail.</summary>
            public string? ThumbnailPath { get; set; }
            /// <summary>Additional faction JSON files to bundle. Absolute paths.</summary>
            public List<string> FactionPaths { get; set; } = new();
        }

        /// <summary>
        /// Pack a scenario file (and optional extras) into a .chimera.zip.
        /// </summary>
        /// <param name="scenarioAbsPath">Absolute path to the scenario JSON to pack.</param>
        /// <param name="outputZipPath">Absolute path for the output .chimera.zip file.</param>
        /// <param name="options">Display metadata for the package.</param>
        /// <returns>The generated <see cref="ContentPackageManifest"/>.</returns>
        public static ContentPackageManifest Pack(string scenarioAbsPath, string outputZipPath,
                                                   PackOptions options)
        {
            if (!File.Exists(scenarioAbsPath))
                throw new FileNotFoundException("Scenario file not found.", scenarioAbsPath);

            // Generate a slug ID from the display name.
            string id = Slugify(options.DisplayName);

            // Hash the scenario bytes for integrity verification.
            uint scenarioHash = ScenarioSerializer.ComputeFileHash(scenarioAbsPath);

            // Build faction_files list (zip-relative paths).
            var factionEntries = new List<string>();
            foreach (var fp in options.FactionPaths)
                if (File.Exists(fp))
                    factionEntries.Add("factions/" + Path.GetFileName(fp));

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
                ThumbnailFile   = options.ThumbnailPath != null ? "thumbnail.png" : null,
                FactionFiles    = factionEntries,
                ScenarioHash    = scenarioHash,
                CreatedAt       = DateTime.UtcNow.ToString("o"),
            };

            // Delete existing output file if present.
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

            using var archive = ZipFile.Open(outputZipPath, ZipArchiveMode.Create);

            // manifest.json
            string manifestJson = JsonSerializer.Serialize(manifest, _jsonOpts);
            WriteEntry(archive, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));

            // scenario.json
            WriteEntry(archive, "scenario.json", File.ReadAllBytes(scenarioAbsPath));

            // thumbnail.png (optional)
            if (options.ThumbnailPath != null && File.Exists(options.ThumbnailPath))
                WriteEntry(archive, "thumbnail.png", File.ReadAllBytes(options.ThumbnailPath));

            // factions/ (optional)
            foreach (var fp in options.FactionPaths)
            {
                if (!File.Exists(fp)) continue;
                WriteEntry(archive, "factions/" + Path.GetFileName(fp), File.ReadAllBytes(fp));
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

            // 3. Extract thumbnail (optional).
            string? thumbOut = null;
            if (!string.IsNullOrEmpty(manifest.ThumbnailFile))
            {
                var thumbEntry = archive.GetEntry(manifest.ThumbnailFile);
                if (thumbEntry != null)
                {
                    thumbOut = Path.Combine(extractDir, "thumbnail.png");
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

            return new UnpackResult
            {
                Manifest     = manifest,
                ScenarioPath = scenarioOut,
                ThumbnailPath = thumbOut,
                FactionPaths  = factionOuts,
            };
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
