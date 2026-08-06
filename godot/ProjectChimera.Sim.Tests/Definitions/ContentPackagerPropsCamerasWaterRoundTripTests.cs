#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-156 — the Story 6.6 props/cameras/water round-trip at the <b>package/import</b> surface the acceptance
    /// criteria actually name, not one level lower.
    ///
    /// <para><see cref="ScenarioDataPropsCamerasWaterTests"/> proves the three collections survive
    /// <see cref="ScenarioSerializer"/> (a JSON save/load in one directory). The AC clause is "package/import": the
    /// creator exports a <c>.chimera.zip</c> via <see cref="ContentPackager.Pack"/> and a downloader recovers the map
    /// via <see cref="ContentPackager.Unpack"/> + <see cref="ScenarioSerializer.LoadFromFile"/> (the Godot-free half of
    /// <c>ContentBrowserPhase.DoImport</c> / <c>WinConditionPhase.HandleLoadMap</c>). Nothing pinned that composite:
    /// the packager writes <c>scenario.json</c> wholesale today, so props/cameras/water ride along <i>incidentally</i>
    /// — a future packager that filtered, re-serialized, or normalized the scenario payload on the way into the zip
    /// could drop a blocking prop or a water rect with the whole Tier-1 suite green.
    ///
    /// <para>The load-bearing assertion is <see cref="PackThenImport_PreservesCanonicalModelHash"/>: blocking props and
    /// water rects fold into <see cref="CanonicalModelHash"/> (via <c>BlockingFootprintDigest</c>), which is the
    /// lockstep start-state handshake. A package that silently lost one would not merely look wrong — the importer and
    /// the exporter would disagree at match start.</para>
    /// </summary>
    public class ContentPackagerPropsCamerasWaterRoundTripTests
    {
        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_pcw_pkg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>
        /// A VALIDATOR-CLEAN map carrying all three Story 6.6 collections: a blocking prop with non-default cosmetic
        /// fields, an all-defaults non-blocking prop, two named cameras, and a water rect. Everything sits clear of the
        /// slot-0 base at (-45, 0) so the blocked-cell union never covers a start position.
        /// </summary>
        private static ScenarioData AuthoredModel() => new ScenarioData
        {
            Id = "pcw_pkg", DisplayName = "Props Cameras Water", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://x.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            ResourceNodes = Array.Empty<ScenarioResourceNode>(),
            Buildings     = Array.Empty<ScenarioBuilding>(),
            Units         = Array.Empty<ScenarioUnit>(),
            Props = new[]
            {
                // Blocking + every cosmetic field non-default: the entry whose footprint cell folds into the hash.
                new ScenarioProp { PropId = "tree", X = 30f, Z = -20f, Rot = 1.5f, Scale = 2.25f, BlocksPathing = true },
                // All cosmetic defaults (rot/scale/blocks_pathing omitted from the bytes) — must come back defaulted,
                // not resurrected with the previous entry's values.
                new ScenarioProp { PropId = "rock", X = -3f, Z = 4f },
            },
            Cameras = new[]
            {
                new ScenarioCamera { Name = "intro", X = 1f, Y = 20f, Z = 3f, TargetX = 0f, TargetY = 0f, TargetZ = 0f, Fov = 55f },
                new ScenarioCamera { Name = "outro", X = -8f, Y = 35f, Z = 12f, TargetX = 4f, TargetY = 1f, TargetZ = -4f, Fov = 70f },
            },
            Water = new[] { new ScenarioWater { X = 40f, Z = 40f, W = 20f, H = 30f, Y = -1.5f } },
        };

        /// <summary>Save <paramref name="model"/>, Pack it into a .chimera.zip, Unpack it into a SEPARATE directory,
        /// and load the recovered scenario — i.e. the full export → import path a downloader walks.</summary>
        private static ScenarioData PackThenImport(string work, ScenarioData model, out ContentPackageManifest manifest)
        {
            string scen = Path.Combine(work, "scenario.json");
            ScenarioSerializer.SaveToFile(model, scen);

            string zip = Path.Combine(work, "map.chimera.zip");
            manifest = ContentPackager.Pack(scen, zip,
                new ContentPackager.PackOptions { DisplayName = model.DisplayName ?? "Map" });

            // Extract somewhere the authored file is NOT visible, so nothing can pass by reading the source map.
            var result = ContentPackager.Unpack(zip, Path.Combine(work, "import"));
            ScenarioData? back = ScenarioSerializer.LoadFromFile(result.ScenarioPath, out string? parseError);
            Assert.True(back != null, $"imported scenario failed to parse: {parseError}");
            return back!;
        }

        [Fact]
        public void PackThenImport_PreservesProps()
        {
            string work = NewTempDir();
            try
            {
                ScenarioData back = PackThenImport(work, AuthoredModel(), out _);

                Assert.NotNull(back.Props);
                Assert.Equal(2, back.Props!.Length);

                var tree = back.Props[0];
                Assert.Equal("tree", tree.PropId);
                Assert.Equal(30f, tree.X);
                Assert.Equal(-20f, tree.Z);
                Assert.Equal(1.5f, tree.Rot);
                Assert.Equal(2.25f, tree.Scale);
                Assert.True(tree.BlocksPathing);

                var rock = back.Props[1];
                Assert.Equal("rock", rock.PropId);
                Assert.Equal(-3f, rock.X);
                Assert.Equal(4f, rock.Z);
                Assert.Equal(0f, rock.Rot);
                Assert.Null(rock.Scale);
                Assert.False(rock.BlocksPathing);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void PackThenImport_PreservesCameras()
        {
            string work = NewTempDir();
            try
            {
                ScenarioData back = PackThenImport(work, AuthoredModel(), out _);

                Assert.NotNull(back.Cameras);
                Assert.Equal(2, back.Cameras!.Length);

                var intro = back.Cameras[0];
                Assert.Equal("intro", intro.Name);
                Assert.Equal(1f, intro.X);
                Assert.Equal(20f, intro.Y);
                Assert.Equal(3f, intro.Z);
                Assert.Equal(55f, intro.Fov);

                var outro = back.Cameras[1];
                Assert.Equal("outro", outro.Name);
                Assert.Equal(4f, outro.TargetX);
                Assert.Equal(1f, outro.TargetY);
                Assert.Equal(-4f, outro.TargetZ);
                Assert.Equal(70f, outro.Fov);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void PackThenImport_PreservesWater()
        {
            string work = NewTempDir();
            try
            {
                ScenarioData back = PackThenImport(work, AuthoredModel(), out _);

                Assert.NotNull(back.Water);
                Assert.Single(back.Water!);
                var w = back.Water![0];
                Assert.Equal(40f, w.X);
                Assert.Equal(40f, w.Z);
                Assert.Equal(20f, w.W);
                Assert.Equal(30f, w.H);
                Assert.Equal(-1.5f, w.Y);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        /// <summary>
        /// The lockstep-critical leg. Blocking props and water rects fold into <see cref="CanonicalModelHash"/> (the
        /// start-state handshake both peers compare), so an importer that recovered a map missing one would agree with
        /// nobody. Pinning hash EQUALITY across the package boundary catches a dropped/moved/unblocked footprint that
        /// a per-field assertion above could still miss (e.g. a packager that re-serialized through a lossy posture).
        /// </summary>
        [Fact]
        public void PackThenImport_PreservesCanonicalModelHash()
        {
            string work = NewTempDir();
            try
            {
                ScenarioData authored = AuthoredModel();
                ulong before = CanonicalModelHash.Compute(authored);
                ScenarioData back = PackThenImport(work, authored, out _);

                Assert.Equal(before, CanonicalModelHash.Compute(back));

                // Guard the guard: the fixture must actually EXERCISE the blocking fold, or the equality above would
                // hold vacuously for any map (a flat map with no footprint hashes the same with or without props).
                var noFootprint = AuthoredModel();
                noFootprint.Props![0].BlocksPathing = false;
                noFootprint.Water = null;
                Assert.NotEqual(before, CanonicalModelHash.Compute(noFootprint));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        /// <summary>
        /// An imported map is only usable if it still passes the load gate. Pin that the recovered model validates —
        /// including the blocked-cell union the validator rebuilds from the recovered props/water — so the package
        /// boundary cannot produce a parseable-but-unloadable map.
        /// </summary>
        [Fact]
        public void PackThenImport_ImportedModelStillValidates()
        {
            string work = NewTempDir();
            try
            {
                ScenarioData back = PackThenImport(work, AuthoredModel(), out _);
                ValidationResult r = new ScenarioValidator().Validate(back);
                Assert.True(r.Ok, r.Error);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        /// <summary>
        /// The absent case is half the Story 6.6 contract: a map with no props/cameras/water must come back with all
        /// three keys still absent (null), never resurrected as empty arrays by the package round-trip.
        /// </summary>
        [Fact]
        public void PackThenImport_MapWithoutPropsCamerasWater_KeepsThemAbsent()
        {
            string work = NewTempDir();
            try
            {
                var flat = AuthoredModel();
                flat.Props = null;
                flat.Cameras = null;
                flat.Water = null;

                ScenarioData back = PackThenImport(work, flat, out _);
                Assert.Null(back.Props);
                Assert.Null(back.Cameras);
                Assert.Null(back.Water);
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        /// <summary>
        /// Props/cameras/water ride INSIDE the integrity-hashed <c>scenario.json</c> payload (unlike terrain/assets,
        /// which carry their own aggregate hashes). Pin that a prop coordinate edited inside a shipped package is
        /// therefore rejected at import rather than silently loading a moved obstacle — a desync vector, since the
        /// blocking footprint folds into CanonicalModelHash.
        /// </summary>
        [Fact]
        public void TamperedPropCoordinateInsidePackage_FailsScenarioIntegrityCheck()
        {
            string work = NewTempDir();
            try
            {
                string scen = Path.Combine(work, "scenario.json");
                ScenarioSerializer.SaveToFile(AuthoredModel(), scen);
                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Tamper" });

                // Move the blocking prop by rewriting the packaged scenario.json (manifest.ScenarioHash untouched).
                string tampered;
                using (var archive = ZipFile.OpenRead(zip))
                using (var reader = new StreamReader(archive.GetEntry("scenario.json")!.Open(), Encoding.UTF8))
                    tampered = reader.ReadToEnd().Replace("\"x\": 30", "\"x\": 31");
                Assert.DoesNotContain("\"x\": 30", tampered); // the edit actually landed

                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                {
                    archive.GetEntry("scenario.json")!.Delete();
                    var repl = archive.CreateEntry("scenario.json");
                    using var s = repl.Open();
                    byte[] bytes = Encoding.UTF8.GetBytes(tampered);
                    s.Write(bytes, 0, bytes.Length);
                }

                Assert.Throws<InvalidDataException>(
                    () => ContentPackager.Unpack(zip, Path.Combine(work, "import")));
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }
}
