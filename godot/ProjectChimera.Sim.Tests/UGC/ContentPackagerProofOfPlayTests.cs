#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.UGC
{
    /// <summary>
    /// Story 9.8 — the .chimera.zip proof-of-play round-trip (Godot-free half): Pack a scenario WITH a token,
    /// screenshots, and IP consent, then confirm the manifest carries them, the screenshot entries are written, and a
    /// re-read manifest passes <see cref="PublishGate"/> for the current model hash.
    /// </summary>
    public class ContentPackagerProofOfPlayTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("packager-test-key-0123456789");

        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_pop_pack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        private static string WriteScenario(string dir, ScenarioData scenario)
        {
            string p = Path.Combine(dir, "scenario.json");
            ScenarioSerializer.SaveToFile(scenario, p);
            return p;
        }

        [Fact]
        public void Pack_WritesToken_Screenshots_Consent_AndGatePassesOnReadBack()
        {
            string work = NewTempDir();
            try
            {
                var scenario = new ScenarioData { Id = "gate-map", DisplayName = "Gate Map", MapBounds = 120f };
                string scen = WriteScenario(work, scenario);

                // Two screenshots on disk.
                string shot0 = Path.Combine(work, "a.png");
                string shot1 = Path.Combine(work, "b.png");
                File.WriteAllBytes(shot0, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(shot1, new byte[] { 4, 5, 6, 7 });

                // Token minted for the CURRENT canonical hash of the model.
                ulong hash = CanonicalModelHash.Compute(scenario);
                var token = ProofOfPlaySigner.Create(hash, "win", "2026-07-24T00:00:00Z", "gate-map", Key);

                string zip = Path.Combine(work, "map.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                {
                    DisplayName     = "Gate Map",
                    Description     = new string('d', 120),          // ≥100 chars
                    PreviewPngBytes = new byte[] { 9, 9, 9 },        // ⇒ ThumbnailFile set
                    Token           = token,
                    ScreenshotPaths = new List<string> { shot0, shot1 },
                    IpConsent       = true,
                });

                // The returned manifest carries the new fields.
                Assert.NotNull(manifest.ProofOfPlay);
                Assert.Equal("win", manifest.ProofOfPlay!.Outcome);
                Assert.True(manifest.IpConsent);
                Assert.Equal(2, manifest.Screenshots.Count);
                Assert.Equal("screenshots/shot_00.png", manifest.Screenshots[0]);
                Assert.Equal("screenshots/shot_01.png", manifest.Screenshots[1]);

                // The zip actually contains the screenshot entries.
                using (var archive = ZipFile.OpenRead(zip))
                {
                    Assert.NotNull(archive.GetEntry("screenshots/shot_00.png"));
                    Assert.NotNull(archive.GetEntry("screenshots/shot_01.png"));
                }

                // Re-read the manifest (as the content browser would) and gate it against the current model hash.
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                reread!.IpConsent = true; // consent is a live choice stamped at publish
                var result = PublishGate.Check(reread, reread.ProofOfPlay, hash, Key);
                Assert.True(result.Passed);
                Assert.True(ProofOfPlaySigner.Verify(reread.ProofOfPlay, Key));
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void TokenHash_SurvivesSerializeLoadRecompute_NotFalselyStale()
        {
            // Review P4: the REAL gate re-derives the hash from the UNPACKED + reloaded scenario. A serialization
            // round-trip that perturbed any folded field would make every genuine publish read "token stale". Assert
            // the serialize→Pack→Unpack→load→Compute identity holds.
            string work = NewTempDir();
            try
            {
                var scenario = new ScenarioData { Id = "stale-map", DisplayName = "Stale Map", MapBounds = 96f };
                string scen = WriteScenario(work, scenario);

                ulong mintedHash = CanonicalModelHash.Compute(scenario);
                var token = ProofOfPlaySigner.Create(mintedHash, "win", "t", "stale-map", Key);

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                {
                    DisplayName = "Stale Map",
                    Token       = token,
                });

                var result   = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                var reloaded  = ScenarioSerializer.LoadFromFile(result.ScenarioPath);
                Assert.NotNull(reloaded);
                ulong reloadedHash = CanonicalModelHash.Compute(reloaded!);

                Assert.Equal(mintedHash, reloadedHash);
                Assert.True(ProofOfPlaySigner.MatchesScenario(token, reloadedHash)); // NOT falsely stale
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void TokenHash_SurvivesRoundTripForPopulatedModel_NotFalselyStale()
        {
            // Follow-up review: the sibling test above round-trips a bare Id/DisplayName/MapBounds model — NONE of the
            // collections CanonicalModelHash.Compute actually folds (PlayerSlots/ResourceNodes/Buildings/Units/Regions/
            // Triggers/Variables/TriggerGraph). If any of those folded fields failed to survive the ScenarioSerializer
            // save/load byte-identically, EVERY real creator's token would read "token stale" and the publish gate would
            // reject all populated maps — while the empty-model test still passed. Exercise the identity on a model
            // carrying at least one entry in each folded collection the real export path writes.
            string work = NewTempDir();
            try
            {
                var scenario = new ScenarioData
                {
                    Id = "populated-map", DisplayName = "Populated Map", MapBounds = 120f,
                    WinCondition = WinCondition.DestroyAllBuildings,
                    PlayerSlots = new[]
                    {
                        new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, StartCrystal = 50f, BaseX = -45f, BaseZ = 3f },
                        new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX = 45f, BaseZ = -3f },
                    },
                    ResourceNodes = new[]
                    {
                        new ScenarioResourceNode { X = 12.5f, Z = -7.25f, Supply = 1500f, Rate = 8f, MaxGatherers = 5, ResourceType = "ore", OwnerSlot = -1 },
                    },
                    Buildings = new[]
                    {
                        new ScenarioBuilding { Type = "town_hall", Slot = 0, X = -45f, Z = 3f, PreBuilt = true },
                    },
                    Units = new[]
                    {
                        new ScenarioUnit { UnitId = "worker", Slot = 0, X = -40.5f, Z = 1.25f },
                        new ScenarioUnit { UnitId = "soldier", Slot = 1, X = 40.5f, Z = -1.25f },
                    },
                    Regions = new[]
                    {
                        new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
                    },
                    Triggers = new[]
                    {
                        new TriggerDefinition
                        {
                            Name = "koth",
                            Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                            Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "hill" } },
                            Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                        },
                    },
                };
                string scen = WriteScenario(work, scenario);

                ulong mintedHash = CanonicalModelHash.Compute(scenario);
                var token = ProofOfPlaySigner.Create(mintedHash, "win", "t", "populated-map", Key);

                string zip = Path.Combine(work, "map.chimera.zip");
                ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions
                {
                    DisplayName = "Populated Map",
                    Token       = token,
                });

                var result   = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                var reloaded  = ScenarioSerializer.LoadFromFile(result.ScenarioPath);
                Assert.NotNull(reloaded);
                ulong reloadedHash = CanonicalModelHash.Compute(reloaded!);

                Assert.Equal(mintedHash, reloadedHash);
                Assert.True(ProofOfPlaySigner.MatchesScenario(token, reloadedHash)); // NOT falsely stale on a real map
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void RewriteManifest_PersistsConsentIntoShippedZip()
        {
            // Review P2: consent chosen at publish must be written INTO the on-disk zip, not just the in-memory copy.
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work, new ScenarioData { Id = "c", DisplayName = "C", MapBounds = 120f });
                string zip = Path.Combine(work, "c.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "C" });
                Assert.False(manifest.IpConsent); // export-time default

                // Stamp consent + rewrite the manifest entry in place.
                manifest.IpConsent = true;
                ContentPackager.RewriteManifest(zip, manifest);

                // Re-reading the SHIPPED zip now reflects the recorded consent (and left other entries intact).
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.True(reread!.IpConsent);

                var unpacked = ContentPackager.Unpack(zip, Path.Combine(work, "extract"));
                Assert.True(File.Exists(unpacked.ScenarioPath)); // scenario entry survived the rewrite
            }
            finally { Directory.Delete(work, recursive: true); }
        }

        [Fact]
        public void Pack_WithoutProofFields_RoundTripsAsPre98Package()
        {
            string work = NewTempDir();
            try
            {
                string scen = WriteScenario(work, new ScenarioData { Id = "plain", DisplayName = "Plain", MapBounds = 120f });
                string zip = Path.Combine(work, "plain.chimera.zip");
                var manifest = ContentPackager.Pack(scen, zip, new ContentPackager.PackOptions { DisplayName = "Plain" });

                Assert.Null(manifest.ProofOfPlay);
                Assert.False(manifest.IpConsent);
                Assert.Empty(manifest.Screenshots);

                // proof_of_play is omitted from the JSON when null (byte-parity with a pre-9.8 manifest).
                var reread = ContentPackager.ReadManifest(zip);
                Assert.NotNull(reread);
                Assert.Null(reread!.ProofOfPlay);
                Assert.Empty(reread.Screenshots);
            }
            finally { Directory.Delete(work, recursive: true); }
        }
    }
}
