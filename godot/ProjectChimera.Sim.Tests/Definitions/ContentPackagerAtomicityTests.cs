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
    /// DW-421 / DW-423 / DW-560 — the ContentPackager write-safety contracts:
    /// (1) RewriteManifest is ATOMIC: a failure anywhere in the rewrite (serialize, staging, or the zip commit)
    ///     leaves the shipped .chimera.zip byte-identical and readable — the old in-place delete-then-write left a
    ///     manifest-less, permanently unreadable package when a throw unwound the ZipArchiveMode.Update session.
    /// (2) Pack hashes and writes ONE byte snapshot per integrity-hashed source (scenario/terrain/asset): a source
    ///     file mutated between hash computation and archive writing can no longer produce a package whose recorded
    ///     hashes disagree with its own bytes (which failed the package's OWN Unpack).
    /// (3) Pack's OUTPUT is ATOMIC: a throw mid-write leaves any pre-existing export at the output path
    ///     byte-identical and readable, with no partial zip and no staging residue — the old shape deleted the
    ///     previous export up front and wrote straight into ZipArchiveMode.Create.
    /// </summary>
    public class ContentPackagerAtomicityTests
    {
        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_atomic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string WriteScenario(string dir)
        {
            string p = Path.Combine(dir, "scenario.json");
            ScenarioSerializer.SaveToFile(
                new ScenarioData { Id = "a", DisplayName = "Atomic Test", MapBounds = 120f }, p);
            return p;
        }

        // ── DW-421: RewriteManifest atomicity ────────────────────────────────────

        [Fact]
        public void RewriteManifest_SerializerThrow_LeavesShippedZipIntact() // DW-421
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Before" });
                byte[] originalZipBytes = File.ReadAllBytes(zip);

                // Fault-inject the serialize step through the internal seam. The old implementation deleted
                // manifest.json BEFORE serializing inside one Update session, so this throw flushed a package
                // with no manifest; the atomic implementation must leave the shipped zip untouched.
                Assert.Throws<InvalidOperationException>(() =>
                    ContentPackager.RewriteManifest(zip, () => throw new InvalidOperationException("boom")));

                Assert.Equal(originalZipBytes, File.ReadAllBytes(zip)); // bit-identical original
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread); // still readable by every future ReadManifest/Unpack
                Assert.Equal("Before", reread!.DisplayName);
                Assert.Empty(Directory.GetFiles(work, "*.rewrite.tmp")); // no staging residue
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void RewriteManifest_StagingFailure_LeavesShippedZipIntact() // DW-421
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Before" });
                byte[] originalZipBytes = File.ReadAllBytes(zip);

                // Squat a DIRECTORY on the temp-sibling name so the staged copy fails mid-rewrite. This pins the
                // staging mechanism itself: the rewrite must never open the shipped zip for writing, so a failure
                // after serialization still leaves it byte-identical. (An in-place rewrite would succeed here and
                // fail this test — deliberately: in-place is exactly the corruption DW-421 closed.)
                Directory.CreateDirectory(zip + ".rewrite.tmp");

                manifest.IpConsent = true;
                Assert.ThrowsAny<Exception>(() => ContentPackager.RewriteManifest(zip, manifest));

                Assert.Equal(originalZipBytes, File.ReadAllBytes(zip));
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.False(reread!.IpConsent); // the failed rewrite must not have half-applied
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void RewriteManifest_Success_SwapsManifestAndLeavesNoResidue() // DW-421
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Before" });

                manifest.IpConsent = true;
                ContentPackager.RewriteManifest(zip, manifest);

                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.True(reread!.IpConsent);
                Assert.Empty(Directory.GetFiles(work, "*.rewrite.tmp")); // staged copy swapped, not leaked

                // Every other entry survived the staged swap: the rewritten package still unpacks
                // (scenario entry present, its recorded integrity hash still matching).
                var unpacked = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.True(File.Exists(unpacked.ScenarioPath));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-423: Pack hash-then-write TOCTOU ──────────────────────────────────

        [Fact]
        public void SourceMutatedMidPack_PackageStillPassesItsOwnUnpack() // DW-423
        {
            string work = NewTempDir();
            try
            {
                // All three integrity-hashed inputs: scenario, one terrain region, one custom asset.
                string scen = WriteScenario(work);
                byte[] scenOriginal = File.ReadAllBytes(scen);

                string terrainDir = Path.Combine(work, "terrain_src");
                Directory.CreateDirectory(terrainDir);
                string region = Path.Combine(terrainDir, "terrain3d_00_00.res");
                byte[] regionOriginal = { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(region, regionOriginal);

                string assetDir = Path.Combine(work, "asset_src");
                Directory.CreateDirectory(assetDir);
                string asset = Path.Combine(assetDir, "tank.glb");
                byte[] assetOriginal = { 10, 20, 30, 40 };
                File.WriteAllBytes(asset, assetOriginal);

                string zip = Path.Combine(work, "map.chimera.zip");

                // Mutate ALL three sources in the window between hash computation and archive writing — the
                // exact TOCTOU the old hash-then-re-read Pack had, where the packaged bytes then disagreed
                // with the recorded hashes and the package failed its OWN Unpack integrity check.
                ContentPackager.PackTestHookAfterHash = () =>
                {
                    File.WriteAllText(scen, "{}");
                    File.WriteAllBytes(region, new byte[] { 99, 99 });
                    File.WriteAllBytes(asset, new byte[] { 77 });
                };
                try
                {
                    ContentPackager.Pack(scen, zip,
                        new ContentPackager.PackOptions { DisplayName = "Toctou", AssetPaths = new() { asset } },
                        terrainDir);
                }
                finally { ContentPackager.PackTestHookAfterHash = null; }

                // Self-consistent package: recorded hashes match the packaged bytes (pre-fix this THROWS on the
                // scenario integrity check — the packaged bytes were re-read after the mutation)...
                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));

                // ...and the packaged bytes are the PRE-mutation snapshots the hashes actually covered.
                Assert.Equal(scenOriginal,   File.ReadAllBytes(result.ScenarioPath));
                Assert.Equal(regionOriginal, File.ReadAllBytes(result.TerrainFiles.Single()));
                Assert.Equal(assetOriginal,  File.ReadAllBytes(result.AssetFiles.Single()));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-560: Pack output atomicity ────────────────────────────────────────

        [Fact]
        public void Pack_ThrowMidWrite_LeavesPreviousExportIntact() // DW-560
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip  = Path.Combine(work, "map.chimera.zip");

                // The creator's PREVIOUS export — the thing the old delete-then-Create shape destroyed.
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Shipped v1" });
                byte[] originalZipBytes = File.ReadAllBytes(zip);

                // A NON-hashed source (screenshot) that is still re-read inside the write loop. Deleting it in the
                // post-hash window is the real-world failure DW-560 names: enumeration saw the file, the write loop
                // no longer finds it, and the throw lands long after the old export would already have been deleted.
                string shot = Path.Combine(work, "shot.png");
                File.WriteAllBytes(shot, new byte[] { 8, 9, 10 });

                ContentPackager.PackTestHookAfterHash = () => File.Delete(shot);
                try
                {
                    Assert.Throws<FileNotFoundException>(() => ContentPackager.Pack(scen, zip,
                        new ContentPackager.PackOptions
                        {
                            DisplayName = "Doomed v2",
                            ScreenshotPaths = new() { shot },
                        }));
                }
                finally { ContentPackager.PackTestHookAfterHash = null; }

                // Pre-fix: the previous export is GONE (or a partial zip). Post-fix: byte-identical and readable.
                Assert.True(File.Exists(zip));
                Assert.Equal(originalZipBytes, File.ReadAllBytes(zip));
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.Equal("Shipped v1", reread!.DisplayName);

                // The failed export left no staging residue beside the output.
                Assert.Empty(Directory.GetFiles(work, "*.pack.tmp"));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Pack_StagingFailure_LeavesPreviousExportIntact() // DW-560
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip  = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Shipped v1" });
                byte[] originalZipBytes = File.ReadAllBytes(zip);

                // Squat a DIRECTORY on the temp-sibling name so the staged write fails. This pins the staging
                // MECHANISM itself: Pack must never open the output path for writing, so a write-side failure
                // still leaves the previous export byte-identical. (An in-place Pack succeeds here and fails this
                // test — deliberately: writing in place is exactly what DW-560 closed.)
                Directory.CreateDirectory(zip + ".pack.tmp");

                Assert.ThrowsAny<Exception>(() => ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Doomed v2" }));

                Assert.Equal(originalZipBytes, File.ReadAllBytes(zip));
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.Equal("Shipped v1", reread!.DisplayName); // no half-applied re-export
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Pack_Success_ReplacesPreviousExportWholesaleAndLeavesNoResidue() // DW-560
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip  = Path.Combine(work, "map.chimera.zip");

                // v1 carries a faction file; v2 does not. Staging must publish a FRESH archive (Create semantics),
                // never an Update over the previous export — a stale entry surviving the re-export would be a
                // package shipping content its own manifest does not list.
                string faction = Path.Combine(work, "rebels.json");
                File.WriteAllText(faction, "{\"id\":\"rebels\"}");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                {
                    DisplayName  = "Shipped v1",
                    FactionPaths = new() { faction },
                });
                Assert.Single(ContentPackager.ReadManifest(zip)!.FactionFiles);

                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Shipped v2" });

                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.Equal("Shipped v2", reread!.DisplayName);
                Assert.Empty(reread.FactionFiles);
                using (var archive = ZipFile.OpenRead(zip))
                    Assert.Null(archive.GetEntry("factions/rebels.json")); // no stale entry carried over

                Assert.Empty(Directory.GetFiles(work, "*.pack.tmp")); // staged copy renamed, not leaked
                Assert.True(File.Exists(ContentPackager.Unpack(zip, Path.Combine(work, "extract")).ScenarioPath));
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }
}
