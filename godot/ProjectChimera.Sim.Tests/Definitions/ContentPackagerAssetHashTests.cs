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
    /// Story 9.9 — the .chimera.zip custom-asset integrity round-trip (Godot-free half): Pack a set of fake asset
    /// files, Unpack them, confirm recovery + list-order preservation, and confirm the aggregate AssetHash catches a
    /// tampered byte and a missing listed entry (mirroring the shipped terrain-hash check). Also pins the
    /// "no assets ⇒ AssetHash==0, no assets/ entries" byte-compat sentinel.
    /// </summary>
    public class ContentPackagerAssetHashTests
    {
        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_asset_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string WriteScenario(string dir)
        {
            string p = Path.Combine(dir, "scenario.json");
            ScenarioSerializer.SaveToFile(
                new ScenarioData { Id = "a", DisplayName = "Asset Test", MapBounds = 120f }, p);
            return p;
        }

        private static string WriteAsset(string dir, string name, byte[] bytes)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        [Fact]
        public void PackThenUnpack_RecoversAssetFiles_InListOrder()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                // Pass b before a so we can assert list order is preserved (NOT ordinal-sorted like the hash input).
                string b = WriteAsset(srcDir, "b_tank.glb", new byte[] { 5, 6, 7, 8, 9 });
                string a = WriteAsset(srcDir, "a_tank.glb", new byte[] { 1, 2, 3, 4 });

                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions
                    {
                        DisplayName = "Asset Test",
                        AssetPaths  = new() { b, a },
                    });

                Assert.Equal(2, manifest.AssetFiles.Count);
                Assert.NotEqual(0u, manifest.AssetHash);
                Assert.Equal("assets/b_tank.glb", manifest.AssetFiles[0]); // list order preserved
                Assert.Equal("assets/a_tank.glb", manifest.AssetFiles[1]);

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Equal(2, result.AssetFiles.Count);
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 },
                    File.ReadAllBytes(result.AssetFiles.First(f => f.EndsWith("b_tank.glb"))));
                Assert.Equal(new byte[] { 1, 2, 3, 4 },
                    File.ReadAllBytes(result.AssetFiles.First(f => f.EndsWith("a_tank.glb"))));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void AssetHash_IsInputOrderIndependent()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                string a = WriteAsset(srcDir, "a_tank.glb", new byte[] { 1, 2, 3, 4 });
                string b = WriteAsset(srcDir, "b_tank.glb", new byte[] { 5, 6, 7, 8, 9 });

                var m1 = ContentPackager.Pack(scen, Path.Combine(work, "m1.chimera.zip"),
                    new ContentPackager.PackOptions { DisplayName = "T", AssetPaths = new() { a, b } });
                var m2 = ContentPackager.Pack(scen, Path.Combine(work, "m2.chimera.zip"),
                    new ContentPackager.PackOptions { DisplayName = "T", AssetPaths = new() { b, a } });

                // Different list order → same ordinal-sorted aggregate hash.
                Assert.Equal(m1.AssetHash, m2.AssetHash);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void TamperedAssetByte_FailsIntegrityCheck()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                string a = WriteAsset(srcDir, "tank.glb", new byte[] { 10, 20, 30 });

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "T", AssetPaths = new() { a } });

                // Tamper: rewrite the asset entry's bytes inside the zip (scenario hash still valid).
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    archive.GetEntry("assets/tank.glb")!.Delete();
                    var repl = archive.CreateEntry("assets/tank.glb");
                    using var s = repl.Open();
                    s.Write(new byte[] { 99, 99, 99 }, 0, 3);
                }

                var ex = Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "extract")));
                Assert.Contains("Package may be corrupt", ex.Message);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void MissingListedAsset_FailsWithLocatedError()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                string a = WriteAsset(srcDir, "tank.glb", new byte[] { 10, 20, 30 });

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "T", AssetPaths = new() { a } });

                // Delete the asset entry but leave the manifest still listing it.
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                    archive.GetEntry("assets/tank.glb")!.Delete();

                var ex = Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "extract")));
                Assert.Contains("assets/tank.glb", ex.Message); // located: names the missing entry
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void PackWithoutAssets_HashZero_NoAssetEntries()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "NoAssets" });

                Assert.Empty(manifest.AssetFiles);
                Assert.Equal(0u, manifest.AssetHash);

                // No assets/ entries written (byte-compat with a pre-9.9 package structure).
                using (var archive = ZipFile.OpenRead(zip))
                    Assert.DoesNotContain(archive.Entries, e => e.FullName.StartsWith("assets/"));

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Empty(result.AssetFiles);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void NonExistentAssetPath_IsSkipped()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                string a = WriteAsset(srcDir, "real.glb", new byte[] { 1, 2, 3 });
                string ghost = Path.Combine(srcDir, "ghost.glb"); // never written

                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "T", AssetPaths = new() { a, ghost } });

                // Only the existing file contributes.
                Assert.Single(manifest.AssetFiles);
                Assert.Equal("assets/real.glb", manifest.AssetFiles[0]);

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Single(result.AssetFiles);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void DuplicateAssetLeafName_RejectedAtPack() // review P3
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                // Two distinct sources (different dirs) that share the same leaf name → would collide on assets/tank.glb.
                string d1 = Path.Combine(work, "a"); Directory.CreateDirectory(d1);
                string d2 = Path.Combine(work, "b"); Directory.CreateDirectory(d2);
                string a = WriteAsset(d1, "tank.glb", new byte[] { 1, 2, 3 });
                string b = WriteAsset(d2, "tank.glb", new byte[] { 4, 5, 6 });

                string zip = Path.Combine(work, "map.chimera.zip");
                Assert.Throws<ArgumentException>(() => ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Dup", AssetPaths = new() { a, b } }));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void DisallowedAssetExtension_RejectedAtUnpack() // review P4
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                // Pack does not gate extension; a non-.glb (here .gltf) is bundled + hashed, then rejected at Unpack.
                string bad = WriteAsset(srcDir, "model.gltf", new byte[] { 1, 2, 3, 4 });

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Bad", AssetPaths = new() { bad } });

                var ex = Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "extract")));
                Assert.Contains("disallowed extension", ex.Message);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void OversizedAssetEntry_RejectedAtUnpack() // review P4
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string srcDir = Path.Combine(work, "src_assets");
                Directory.CreateDirectory(srcDir);
                string a = WriteAsset(srcDir, "tank.glb", new byte[] { 1, 2, 3 });

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip,
                    new ContentPackager.PackOptions { DisplayName = "Big", AssetPaths = new() { a } });

                // Replace the entry with an over-cap uncompressed payload (zeros compress tiny on disk, but the
                // per-entry size gate reads the uncompressed Length). The size check runs before the hash, so this
                // rejects on size regardless of the (now-stale) hash.
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    archive.GetEntry("assets/tank.glb")!.Delete();
                    var repl = archive.CreateEntry("assets/tank.glb");
                    using var s = repl.Open();
                    byte[] big = new byte[AssetValidator.MaxAssetBytes + 1];
                    s.Write(big, 0, big.Length);
                }

                var ex = Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "extract")));
                Assert.Contains("over the", ex.Message);
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }
}
