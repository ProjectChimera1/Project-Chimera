#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-425 / DW-426 — download-verify cleanup + manifest-driven ingest hygiene (the Godot-free cores).
    ///
    /// DW-426: the load-path asset ingest used to enumerate the extracted <c>imported_maps/&lt;id&gt;/assets/</c>
    /// directory instead of the integrity-verified manifest <c>AssetFiles</c> list, and the import dir was never
    /// cleared before extraction — so a stale/orphan .glb from a prior same-Id import could be ingested and rendered
    /// without having passed the current package's integrity check. Closure under test: <c>Unpack</c> materializes
    /// the validated <c>manifest.json</c> into the extract dir as its LAST step (the verified-extraction seal read
    /// back via <c>ReadExtractedManifest</c>), and <c>Unpack(..., cleanExtractDir: true)</c> clears the target first.
    ///
    /// DW-425: a download that failed integrity verification cleaned only the throwaway verify cache, leaving the
    /// rejected .chimera.zip in the <c>user://packages/</c> scan dir — so <c>RefreshLocal</c> (ScanDirectory) re-listed
    /// it as a playable local card on the next launch. Closure under test: <c>QuarantineRejectedPackage</c> moves the
    /// rejected file out of the scan dir (collision-safe, bytes preserved) so ScanDirectory can never re-offer it.
    /// </summary>
    public class ContentPackagerImportHygieneTests
    {
        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_hygiene_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string WriteScenario(string dir)
        {
            string p = Path.Combine(dir, "scenario.json");
            ScenarioSerializer.SaveToFile(
                new ScenarioData { Id = "a", DisplayName = "Hygiene Test", MapBounds = 120f }, p);
            return p;
        }

        private static string WriteAsset(string dir, string name, byte[] bytes)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        /// <summary>Pack a one-asset package whose manifest Id derives from <paramref name="displayName"/>.</summary>
        private static string PackWithAsset(string work, string zipName, string displayName,
                                            string assetName, byte[] assetBytes)
        {
            string scen = WriteScenario(work);
            string srcDir = Path.Combine(work, "src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(srcDir);
            string asset = WriteAsset(srcDir, assetName, assetBytes);

            string zip = Path.Combine(work, zipName);
            ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
            {
                DisplayName = displayName,
                AssetPaths  = new() { asset },
            });
            return zip;
        }

        // ── DW-426: verified-manifest seal ────────────────────────────────────────

        [Fact]
        public void Unpack_MaterializesVerifiedManifest_IntoExtractDir()
        {
            string work = NewTempDir();
            try
            {
                string zip = PackWithAsset(work, "map.chimera.zip", "Hygiene Test",
                                           "tank.glb", new byte[] { 1, 2, 3, 4 });
                string extract = Path.Combine(work, "extract");
                var result = ContentPackager.Unpack(zip, extract);

                // The seal exists and round-trips the SAME verified asset list the ingest must use.
                Assert.True(File.Exists(Path.Combine(extract, "manifest.json")));
                var sealedManifest = ContentPackager.ReadExtractedManifest(extract);
                Assert.NotNull(sealedManifest);
                Assert.Equal(result.Manifest.Id, sealedManifest!.Id);
                Assert.Equal(new[] { "assets/tank.glb" }, sealedManifest.AssetFiles);
                Assert.Equal(result.Manifest.AssetHash, sealedManifest.AssetHash);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Unpack_FailedIntegrity_LeavesNoManifestSeal()
        {
            string work = NewTempDir();
            try
            {
                string zip = PackWithAsset(work, "map.chimera.zip", "Hygiene Test",
                                           "tank.glb", new byte[] { 10, 20, 30 });

                // Tamper the asset bytes (valid extension + under-cap size, so the aggregate hash check is what
                // rejects) — a partial extraction lands on disk but must NOT be sealed as verified.
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    archive.GetEntry("assets/tank.glb")!.Delete();
                    var repl = archive.CreateEntry("assets/tank.glb");
                    using var s = repl.Open();
                    s.Write(new byte[] { 99, 99, 99 }, 0, 3);
                }

                string extract = Path.Combine(work, "extract");
                Assert.Throws<InvalidDataException>(() => ContentPackager.Unpack(zip, extract));

                Assert.False(File.Exists(Path.Combine(extract, "manifest.json")));
                Assert.Null(ContentPackager.ReadExtractedManifest(extract));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-426: clean-before-extract + orphan exclusion ───────────────────────

        [Fact]
        public void Unpack_CleanExtractDir_RemovesStaleOrphanAssets()
        {
            string work = NewTempDir();
            try
            {
                // Import #1: same display name ⇒ same manifest Id ⇒ same imported_maps/<id>/ target.
                string zipA = PackWithAsset(work, "a.chimera.zip", "Same Id Map",
                                            "old_tank.glb", new byte[] { 1, 1, 1 });
                string extract = Path.Combine(work, "extract");
                ContentPackager.Unpack(zipA, extract);
                Assert.True(File.Exists(Path.Combine(extract, "assets", "old_tank.glb")));

                // Import #2 (revised package, same Id, different asset set) with the DW-426 clean flag.
                string zipB = PackWithAsset(work, "b.chimera.zip", "Same Id Map",
                                            "new_tank.glb", new byte[] { 2, 2, 2 });
                ContentPackager.Unpack(zipB, extract, cleanExtractDir: true);

                // The prior import's orphan is GONE — a directory scan and the manifest now agree.
                Assert.False(File.Exists(Path.Combine(extract, "assets", "old_tank.glb")));
                Assert.True(File.Exists(Path.Combine(extract, "assets", "new_tank.glb")));
                var sealedManifest = ContentPackager.ReadExtractedManifest(extract);
                Assert.NotNull(sealedManifest);
                Assert.Equal(new[] { "assets/new_tank.glb" }, sealedManifest!.AssetFiles);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Unpack_DefaultDoesNotClean_ButManifestExcludesOrphan()
        {
            string work = NewTempDir();
            try
            {
                // Plant an orphan where a prior extraction would have left it.
                string extract = Path.Combine(work, "extract");
                Directory.CreateDirectory(Path.Combine(extract, "assets"));
                File.WriteAllBytes(Path.Combine(extract, "assets", "orphan.glb"), new byte[] { 9, 9 });

                string zip = PackWithAsset(work, "map.chimera.zip", "Hygiene Test",
                                           "tank.glb", new byte[] { 1, 2, 3 });
                ContentPackager.Unpack(zip, extract); // default: non-destructive (pins back-compat)

                // The orphan file survives on disk (default is opt-in clean), but the DW-426 ingest contract —
                // "ingest ONLY the manifest's verified AssetFiles" — excludes it: the seal never lists it.
                Assert.True(File.Exists(Path.Combine(extract, "assets", "orphan.glb")));
                var sealedManifest = ContentPackager.ReadExtractedManifest(extract);
                Assert.NotNull(sealedManifest);
                Assert.Equal(new[] { "assets/tank.glb" }, sealedManifest!.AssetFiles);
                Assert.DoesNotContain("assets/orphan.glb", sealedManifest.AssetFiles);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void ReadExtractedManifest_AbsentOrMalformed_ReturnsNull()
        {
            string work = NewTempDir();
            try
            {
                // Absent: a dir with no manifest.json (legacy pre-seal extraction).
                Assert.Null(ContentPackager.ReadExtractedManifest(work));

                // Malformed: unparseable JSON must read as "no verified seal", never throw.
                File.WriteAllText(Path.Combine(work, "manifest.json"), "{ not json !");
                Assert.Null(ContentPackager.ReadExtractedManifest(work));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-425: rejected-download quarantine ──────────────────────────────────

        [Fact]
        public void QuarantineRejectedPackage_RemovesRejectedZipFromScanDir()
        {
            string work = NewTempDir();
            try
            {
                // A real (manifest-bearing) package in the scan dir — exactly what RefreshLocal would re-list.
                string packagesDir = Path.Combine(work, "packages");
                Directory.CreateDirectory(packagesDir);
                string zip = PackWithAsset(packagesDir, "123.chimera.zip", "Rejected Map",
                                           "tank.glb", new byte[] { 1, 2, 3 });
                byte[] originalBytes = File.ReadAllBytes(zip);
                Assert.Single(ContentPackager.ScanDirectory(packagesDir)); // sanity: it WOULD be listed

                string quarantineDir = Path.Combine(work, "packages_quarantine");
                string? quarantined = ContentPackager.QuarantineRejectedPackage(zip, quarantineDir);

                // Moved out of the scan dir (the DW-425 defect: it used to stay and be re-listed) …
                Assert.False(File.Exists(zip));
                Assert.Empty(ContentPackager.ScanDirectory(packagesDir));
                // … into the quarantine dir, bytes preserved for diagnostics.
                Assert.NotNull(quarantined);
                Assert.Equal(Path.GetFullPath(quarantineDir),
                             Path.GetFullPath(Path.GetDirectoryName(quarantined!)!));
                Assert.Equal(originalBytes, File.ReadAllBytes(quarantined!));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void QuarantineRejectedPackage_CollisionKeepsBothFiles()
        {
            string work = NewTempDir();
            try
            {
                string quarantineDir = Path.Combine(work, "quarantine");
                Directory.CreateDirectory(quarantineDir);
                string existing = Path.Combine(quarantineDir, "123.chimera.zip");
                File.WriteAllBytes(existing, new byte[] { 0xAA });

                string src = Path.Combine(work, "123.chimera.zip");
                File.WriteAllBytes(src, new byte[] { 0xBB });

                string? quarantined = ContentPackager.QuarantineRejectedPackage(src, quarantineDir);

                Assert.NotNull(quarantined);
                Assert.NotEqual(Path.GetFullPath(existing), Path.GetFullPath(quarantined!));
                Assert.EndsWith(".chimera.zip", quarantined!); // double extension survives the rename
                Assert.Equal(new byte[] { 0xAA }, File.ReadAllBytes(existing));     // never overwritten
                Assert.Equal(new byte[] { 0xBB }, File.ReadAllBytes(quarantined)); // the new arrival's bytes
                Assert.False(File.Exists(src));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void QuarantineRejectedPackage_MissingSource_ReturnsNullWithoutThrowing()
        {
            string work = NewTempDir();
            try
            {
                string ghost = Path.Combine(work, "never_written.chimera.zip");
                Assert.Null(ContentPackager.QuarantineRejectedPackage(
                    ghost, Path.Combine(work, "quarantine")));
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }
}
