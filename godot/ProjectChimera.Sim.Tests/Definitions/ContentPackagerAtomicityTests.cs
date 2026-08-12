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
    /// DW-421 / DW-423 / DW-560 / DW-716 / DW-717 / DW-727 / DW-821 — the ContentPackager write-safety contracts:
    /// (1) RewriteManifest is ATOMIC: a failure anywhere in the rewrite (serialize, staging, or the zip commit)
    ///     leaves the shipped .chimera.zip byte-identical and readable — the old in-place delete-then-write left a
    ///     manifest-less, permanently unreadable package when a throw unwound the ZipArchiveMode.Update session.
    /// (2) Pack snapshots and writes ONE byte read per source: a source file mutated between enumeration and
    ///     archive writing can no longer change what gets packaged. DW-423 established this for the three
    ///     INTEGRITY-HASHED inputs (scenario/terrain/asset), where the failure was loud — the package failed its
    ///     OWN Unpack. DW-716 extends it to the non-hashed thumbnail/faction/screenshot families, where the same
    ///     TOCTOU is SILENT: no hash covers them, so a swapped source ships content the creator never saw.
    /// (3) Pack's OUTPUT is ATOMIC: a throw mid-write or at the commit boundary leaves any pre-existing export at
    ///     the output path byte-identical and readable, with no partial zip and no staging residue — the old shape
    ///     deleted the previous export up front and wrote straight into ZipArchiveMode.Create.
    /// (4) Staging residue is TOLERATED and COLLECTED, not fatal: a stale .pack.tmp FILE at the staging path no
    ///     longer takes the next export down with it (DW-727 — ZipArchiveMode.Create is FileMode.CreateNew, which
    ///     throws on an occupied path), and leaked staging files past an age gate are swept by the packages-dir
    ///     scan instead of accumulating forever (DW-717).
    /// (5) Bundled faction file NAMES are unique: two sources sharing a leaf both map to factions/{leaf}, and
    ///     Unpack's GetEntry returns only the first, so the collision silently loses one faction's bytes. Rejected
    ///     at Pack, mirroring the Story-9.9 asset-leaf guard (DW-821).
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
        public void Pack_ThrowAtTheCommitBoundary_LeavesPreviousExportIntact() // DW-560
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip  = Path.Combine(work, "map.chimera.zip");

                // The creator's PREVIOUS export — the thing the old delete-then-Create shape destroyed.
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Shipped v1" });
                byte[] originalZipBytes = File.ReadAllBytes(zip);

                // Fail at the LAST possible moment: the staged archive is fully written and closed, and the
                // publish rename is what throws (a full disk, a transient IO fault, an AV hold on the output).
                // This is the sharpest form of the DW-560 contract — everything succeeded except the commit, so
                // an implementation that wrote in place would already have destroyed "Shipped v1" by now.
                //
                // Vehicle note: this test used to inject the fault by deleting a screenshot inside
                // PackTestHookAfterHash, because the non-hashed sources were re-read inside the write loop.
                // DW-716 removed every write-loop re-read, so that deletion is now (correctly) harmless and the
                // commit boundary is the remaining injectable failure inside the staged-write region.
                ContentPackager.PackTestHookBeforeCommit = () => throw new IOException("commit boom");
                try
                {
                    Assert.Throws<IOException>(() => ContentPackager.Pack(scen, zip,
                        new ContentPackager.PackOptions { DisplayName = "Doomed v2" }));
                }
                finally { ContentPackager.PackTestHookBeforeCommit = null; }

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

        // ── DW-716: the NON-hashed sources are snapshotted too ───────────────────

        [Fact]
        public void NonHashedSourcesMutatedMidPack_PackageCarriesTheEnumerationTimeBytes() // DW-716
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);

                // The three families DW-423 did NOT cover: thumbnail, faction files, screenshots.
                string thumb = Path.Combine(work, "thumb.png");
                byte[] thumbOriginal = { 1, 2, 3 };
                File.WriteAllBytes(thumb, thumbOriginal);

                string faction = Path.Combine(work, "rebels.json");
                byte[] factionOriginal = { 4, 5, 6, 7 };
                File.WriteAllBytes(faction, factionOriginal);

                string shot = Path.Combine(work, "shot.png");
                byte[] shotOriginal = { 8, 9 };
                File.WriteAllBytes(shot, shotOriginal);

                string zip = Path.Combine(work, "map.chimera.zip");

                // SWAP all three in the window between enumeration and archive writing. Pre-fix each source was
                // File.Exists-checked at enumeration and File.ReadAllBytes-read inside the write loop, so the
                // package shipped the POST-swap bytes. Unlike the DW-423 families this failure is SILENT: no
                // integrity hash covers thumbnails/factions/screenshots, so the package still unpacks cleanly
                // while carrying content the creator never saw.
                ContentPackager.PackTestHookAfterHash = () =>
                {
                    File.WriteAllBytes(thumb,   new byte[] { 66, 66, 66 });
                    File.WriteAllBytes(faction, new byte[] { 77, 77, 77, 77 });
                    File.WriteAllBytes(shot,    new byte[] { 88, 88 });
                };
                try
                {
                    ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                    {
                        DisplayName     = "Toctou",
                        ThumbnailPath   = thumb,
                        FactionPaths    = new() { faction },
                        ScreenshotPaths = new() { shot },
                    });
                }
                finally { ContentPackager.PackTestHookAfterHash = null; }

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.NotNull(result.ThumbnailPath);
                Assert.Equal(thumbOriginal,   File.ReadAllBytes(result.ThumbnailPath!));
                Assert.Equal(factionOriginal, File.ReadAllBytes(result.FactionPaths.Single()));
                Assert.Equal(shotOriginal,    ReadZipEntry(zip, "screenshots/shot_00.png"));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void NonHashedSourceDeletedMidPack_NoLongerAbortsTheExport() // DW-716
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string shot = Path.Combine(work, "shot.png");
                byte[] shotOriginal = { 8, 9, 10 };
                File.WriteAllBytes(shot, shotOriginal);
                string zip = Path.Combine(work, "map.chimera.zip");

                // The other half of the read-once change: a source deleted after enumeration used to throw
                // FileNotFoundException from the write loop (the trigger DW-560's staging was built to survive).
                // With the bytes already in hand the export simply completes, carrying what enumeration saw.
                ContentPackager.PackTestHookAfterHash = () => File.Delete(shot);
                try
                {
                    ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                    {
                        DisplayName     = "Survivor",
                        ScreenshotPaths = new() { shot },
                    });
                }
                finally { ContentPackager.PackTestHookAfterHash = null; }

                var manifest = ContentPackager.ReadManifest(zip);
                Assert.NotNull(manifest);
                Assert.Equal(new[] { "screenshots/shot_00.png" }, manifest!.Screenshots.ToArray());
                Assert.Equal(shotOriginal, ReadZipEntry(zip, "screenshots/shot_00.png"));
                Assert.Empty(Directory.GetFiles(work, "*.pack.tmp"));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-727: a stale staging FILE must not take the next export down ──────

        [Fact]
        public void Pack_StaleStagingFileAtTheTempPath_NextExportStillSucceeds() // DW-727
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip  = Path.Combine(work, "map.chimera.zip");

                // Residue from a killed editor, or from the catch block's best-effort File.Delete losing to an
                // AV/indexer handle (it swallows its own failure by design). ZipArchiveMode.Create maps to
                // FileMode.CreateNew, so pre-fix this threw IOException("...already exists") — the creator ate a
                // confusing failure naming a temp file they never created, on an export whose inputs and
                // destination were both perfectly fine.
                string stale = zip + ".pack.tmp";
                File.WriteAllBytes(stale, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Fresh" });

                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.Equal("Fresh", reread!.DisplayName);
                Assert.False(File.Exists(stale));                     // residue cleared, not inherited
                Assert.Empty(Directory.GetFiles(work, "*.pack.tmp")); // and the fresh stage was renamed away
                Assert.True(File.Exists(ContentPackager.Unpack(zip, Path.Combine(work, "extract")).ScenarioPath));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        // ── DW-717: leaked staging files are collected, not accumulated ──────────

        [Fact]
        public void ScanDirectory_SweepsStaleStagingResidue_ButSparesFreshOnesAndPackages() // DW-717
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string packages = Path.Combine(work, "packages");
                Directory.CreateDirectory(packages);
                string zip = Path.Combine(packages, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Listed" });

                // Both leak shapes, aged past the gate. Pre-fix these lived forever: ScanDirectory's
                // "*.chimera.zip" glob does not match them, Unpack is never pointed at them, and both atomicity
                // paths deliberately swallow a failure to delete their own staging file.
                string stalePack    = Path.Combine(packages, "old.chimera.zip.pack.tmp");
                string staleRewrite = Path.Combine(packages, "old.chimera.zip.rewrite.tmp");
                foreach (string p in new[] { stalePack, staleRewrite })
                {
                    File.WriteAllBytes(p, new byte[] { 1 });
                    File.SetLastWriteTimeUtc(p,
                        DateTime.UtcNow.AddHours(-(ContentPackager.StaleStagingAgeHours + 1)));
                }

                // A staging file from a live/very recent export must SURVIVE — the age gate is exactly what keeps
                // the sweep from deleting another process's in-flight write out from under it.
                string live = Path.Combine(packages, "live.chimera.zip.pack.tmp");
                File.WriteAllBytes(live, new byte[] { 2 });

                var listed = ContentPackager.ScanDirectory(packages).ToList();

                Assert.Single(listed);
                Assert.Equal("Listed", listed[0].Manifest.DisplayName);
                Assert.False(File.Exists(stalePack));
                Assert.False(File.Exists(staleRewrite));
                Assert.True(File.Exists(live));  // young: never touched
                Assert.True(File.Exists(zip));   // real packages are never swept
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void SweepStaleStagingFiles_MissingDirectory_IsANoOp() // DW-717
        {
            // The sweep runs in front of a LISTING, so it must never be able to throw out of one — a packages
            // directory that does not exist yet (first run) is the ordinary case, not an error.
            Assert.Equal(0, ContentPackager.SweepStaleStagingFiles(
                Path.Combine(Path.GetTempPath(), "chimera_absent_" + Guid.NewGuid().ToString("N"))));
        }

        // ── DW-821: bundled faction file names must be unique ────────────────────

        [Fact]
        public void Pack_TwoFactionSourcesSharingAFileName_IsRejected() // DW-821
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);

                // The same LEAF from two different directories: both map to the single zip entry
                // factions/rebels.json. ZipArchive.CreateEntry permits duplicate entry names and Unpack's
                // GetEntry returns only the FIRST, so pre-fix the second faction's bytes were silently lost —
                // and unlike the asset twin there is no integrity hash over this family to make it self-evident.
                string dirA = Path.Combine(work, "a"); Directory.CreateDirectory(dirA);
                string dirB = Path.Combine(work, "b"); Directory.CreateDirectory(dirB);
                string a = Path.Combine(dirA, "rebels.json"); File.WriteAllBytes(a, new byte[] { 1 });
                string b = Path.Combine(dirB, "rebels.json"); File.WriteAllBytes(b, new byte[] { 2 });

                string zip = Path.Combine(work, "map.chimera.zip");
                var ex = Assert.Throws<ArgumentException>(() => ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Dupes", FactionPaths = new() { a, b } }));

                Assert.Contains("Duplicate faction file name 'rebels.json'", ex.Message, StringComparison.Ordinal);
                Assert.Contains("factions/rebels.json", ex.Message, StringComparison.Ordinal);

                // Rejected during enumeration, before anything is written: no export, no staging residue.
                Assert.False(File.Exists(zip));
                Assert.Empty(Directory.GetFiles(work, "*.pack.tmp"));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Pack_FactionSourcesWithDistinctFileNames_AreAllBundled() // DW-821 (negative control)
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string dirA = Path.Combine(work, "a"); Directory.CreateDirectory(dirA);
                string dirB = Path.Combine(work, "b"); Directory.CreateDirectory(dirB);
                string a = Path.Combine(dirA, "rebels.json");     File.WriteAllBytes(a, new byte[] { 1 });
                string b = Path.Combine(dirB, "homunculus.json"); File.WriteAllBytes(b, new byte[] { 2 });

                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Two", FactionPaths = new() { a, b } });

                // The guard rejects COLLISIONS only — a legitimate multi-faction export still round-trips whole.
                Assert.Equal(new[] { "factions/rebels.json", "factions/homunculus.json" },
                             manifest.FactionFiles.ToArray());
                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Equal(2, result.FactionPaths.Count);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        /// <summary>Read one zip entry's bytes — used where the packaged content is not surfaced by UnpackResult
        /// (screenshots are recorded on the manifest but not extracted to disk).</summary>
        private static byte[] ReadZipEntry(string zipPath, string entryName)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryName);
            Assert.NotNull(entry);
            using var s = entry!.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
