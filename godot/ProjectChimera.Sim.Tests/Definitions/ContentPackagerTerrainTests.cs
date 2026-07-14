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
    /// Story 6.2 — the .chimera.zip terrain round-trip (Godot-free half): Pack a folder of fake terrain .res files,
    /// Unpack them, confirm recovery, and confirm the terrain integrity hash catches a tampered byte (mirroring the
    /// existing scenario-hash check). Also pins the TerrainFolderName derivation helper.
    /// </summary>
    public class ContentPackagerTerrainTests
    {
        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_terrain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string WriteScenario(string dir)
        {
            string p = Path.Combine(dir, "scenario.json");
            ScenarioSerializer.SaveToFile(
                new ScenarioData { Id = "t", DisplayName = "Terrain Test", MapBounds = 120f }, p);
            return p;
        }

        [Fact]
        public void PackThenUnpack_RecoversTerrainFiles()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string terrainDir = Path.Combine(work, "src_terrain");
                Directory.CreateDirectory(terrainDir);
                File.WriteAllBytes(Path.Combine(terrainDir, "terrain3d_00_00.res"), new byte[] { 1, 2, 3, 4 });
                File.WriteAllBytes(Path.Combine(terrainDir, "terrain3d_00_01.res"), new byte[] { 5, 6, 7, 8, 9 });

                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(
                    scen, zip, new ContentPackager.PackOptions { DisplayName = "Terrain Test" }, terrainDir);

                Assert.Equal(2, manifest.TerrainFiles.Count);
                Assert.NotEqual(0u, manifest.TerrainHash);
                Assert.Contains("map/terrain/terrain3d_00_00.res", manifest.TerrainFiles);

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Equal(2, result.TerrainFiles.Count);
                Assert.Equal(new byte[] { 1, 2, 3, 4 },
                    File.ReadAllBytes(result.TerrainFiles.First(f => f.EndsWith("terrain3d_00_00.res"))));
                Assert.Equal(new byte[] { 5, 6, 7, 8, 9 },
                    File.ReadAllBytes(result.TerrainFiles.First(f => f.EndsWith("terrain3d_00_01.res"))));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void TamperedTerrainByte_FailsIntegrityCheck()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string terrainDir = Path.Combine(work, "src_terrain");
                Directory.CreateDirectory(terrainDir);
                File.WriteAllBytes(Path.Combine(terrainDir, "terrain3d_00_00.res"), new byte[] { 10, 20, 30 });

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "T" }, terrainDir);

                // Tamper: rewrite the terrain entry's bytes inside the zip (scenario hash still valid).
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    archive.GetEntry("map/terrain/terrain3d_00_00.res")!.Delete();
                    var repl = archive.CreateEntry("map/terrain/terrain3d_00_00.res");
                    using var s = repl.Open();
                    s.Write(new byte[] { 99, 99, 99 }, 0, 3);
                }

                Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "extract")));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void PackWithoutTerrain_UnpacksGracefully()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work);
                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(
                    scen, zip, new ContentPackager.PackOptions { DisplayName = "NoTerrain" });

                Assert.Empty(manifest.TerrainFiles);
                Assert.Equal(0u, manifest.TerrainHash);

                var result = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.Empty(result.TerrainFiles);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void TerrainFolderName_AppendsSuffix()
            => Assert.Equal("alpha_map_01_terrain", ContentPackager.TerrainFolderName("alpha_map_01"));

        // Review pass 2 (VG2): pin the load-bearing negative-coordinate region-file recognition. Terrain3D encodes a
        // negative region location with a HYPHEN (the default flat region at (-1,-1) → "terrain3d-01-01.res"), so the
        // load-side "does this folder hold regions?" predicate MUST match hyphenated names — an underscore-anchored
        // rule would make essentially every map covering the origin load flat. If someone later "cleans up"
        // IsTerrainRegionFile / the ScenarioLoadPhase check to require an underscore, this test goes red.
        [Theory]
        [InlineData("terrain3d_00_00.res", true)]   // origin region (0,0) — underscore
        [InlineData("terrain3d-01-01.res", true)]   // default flat region (-1,-1) — HYPHEN (the regression trap)
        [InlineData("terrain3d_00-01.res", true)]   // mixed sign (0,-1)
        [InlineData("terrain3d-05_03.res", true)]   // mixed sign (-5,3)
        [InlineData("thumbnail.png", false)]
        [InlineData("scenario.json", false)]
        [InlineData("terrain3d_00_00.tres", false)] // wrong extension
        [InlineData("notterrain3d_00_00.res", false)]
        public void IsTerrainRegionFile_RecognizesRegionFilesIncludingNegativeCoords(string fileName, bool expected)
            => Assert.Equal(expected, ContentPackager.IsTerrainRegionFile(fileName));
    }
}
