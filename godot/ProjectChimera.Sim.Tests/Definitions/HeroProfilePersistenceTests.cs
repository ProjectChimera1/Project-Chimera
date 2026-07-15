#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core;               // Fixed, HeroStore, HeroId
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.9 — the offline hero-persistence rail + init-time apply. Covers the value-bearing
    /// <see cref="PlayerProfile"/> (xp as <see cref="Fixed.Raw"/>, never a float), the <see cref="LocalProfileSource"/>
    /// disk rail (save/load/replace/delete + fail-soft), and the <see cref="HeroProfileLoader"/> apply core
    /// (<see cref="HeroProfileLoader.MintId"/> determinism, <see cref="HeroProfileLoader.LoadInto"/> → byte-identical
    /// <see cref="StartStateHash"/> on re-load, the incompatible/null empty-store equality, the <c>-1</c> deterministic
    /// skip, and the shape-driven <see cref="HeroProfileLoader.BuildProfile"/>). All Godot-free (Tier-1), mirroring
    /// <see cref="PersistenceManifestTests"/>. Asserts the 3.2/3.8 hash versions are UNBUMPED (3.9 is init-time only).
    /// </summary>
    public class HeroProfilePersistenceTests
    {
        private static readonly Fixed SampleXp = Fixed.FromRaw(786432); // 12.0 in 16.16

        // ── PlayerProfile JSON round-trip (xp as Fixed.Raw int, no float) ──────────────────

        [Fact]
        public void Profile_JsonRoundTrip_XpIsIntegerRaw_NoFloat()
        {
            PlayerProfile p = HeroProfileLoader.BuildProfile(
                "grommash#1", "grommash", "orc_clans", "Grommash", "chaos_strike",
                level: 3, xp: SampleXp, ShapeOf("hero.level", "hero.xp"));

            string json = JsonSerializer.Serialize(p);
            Assert.Contains("\"profile_id\":\"grommash#1\"", json);
            Assert.Contains("786432", json);           // xp stored as its raw int
            Assert.DoesNotContain("786432.", json);     // never a float literal

            PlayerProfile? back = JsonSerializer.Deserialize<PlayerProfile>(json);
            Assert.NotNull(back);
            Assert.Equal(3, back!.Level);
            Assert.Equal(SampleXp.Raw, back.Xp.Raw);    // reconstructed EXACTLY from the raw
            Assert.Equal("grommash", back.HeroDefId);
            Assert.Equal("chaos_strike", back.SignatureAbility);
        }

        [Fact]
        public void Profile_Clone_IsIndependentDeepCopy()
        {
            PlayerProfile p = HeroProfileLoader.BuildProfile(
                "h#1", "h", "f", "H", null, 2, Fixed.FromRaw(10), ShapeOf("hero.level", "hero.xp"));
            PlayerProfile c = p.Clone();
            c.Values.Add(new ProfileAttributeValue("hero.level", 99));
            Assert.Equal(2, p.Values.Count);   // source list not shared
            Assert.Equal(3, c.Values.Count);
        }

        // ── LocalProfileSource — save / load / replace / delete + fail-soft ────────────────

        [Fact]
        public void LocalProfileSource_SaveLoadReplaceDelete_RoundTrips()
        {
            using var dir = new TempDir();
            var source = new LocalProfileSource(dir.Path);
            Assert.Empty(source.LoadAll());   // nothing yet

            PlayerProfile a = HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "Grommash", null, 1, Fixed.Zero, ShapeOf("hero.level"));
            PlayerProfile b = HeroProfileLoader.BuildProfile("valla#1", "valla", "elf", "Valla", null, 5, SampleXp, ShapeOf("hero.level", "hero.xp"));
            source.Save(a);
            source.Save(b);
            Assert.Equal(2, source.LoadAll().Count);

            // Replace-by-id (Overwrite): same ProfileId, new level.
            PlayerProfile a2 = HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "Grommash", null, 7, Fixed.Zero, ShapeOf("hero.level"));
            source.Save(a2);
            IReadOnlyList<PlayerProfile> after = source.LoadAll();
            Assert.Equal(2, after.Count);           // replaced, not appended
            Assert.Equal(7, FindById(after, "grommash#1").Level);

            source.Delete("grommash#1");
            IReadOnlyList<PlayerProfile> afterDel = source.LoadAll();
            Assert.Single(afterDel);
            Assert.Equal("valla#1", afterDel[0].ProfileId);
        }

        [Fact]
        public void LocalProfileSource_Missing_ReturnsEmpty_NoThrow()
        {
            var source = new LocalProfileSource(Path.Combine(Path.GetTempPath(), "chimera_no_such_dir_" + System.Guid.NewGuid().ToString("N")));
            Assert.Empty(source.LoadAll());   // absent directory ⇒ empty, no throw
        }

        [Fact]
        public void LocalProfileSource_Corrupt_ReturnsEmpty_NoThrow()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, LocalProfileSource.ProfilesFileName), "{ this is not valid json ]");
            var source = new LocalProfileSource(dir.Path);
            Assert.Empty(source.LoadAll());   // unparseable ⇒ empty, no throw
        }

        [Fact]
        public void LocalProfileSource_NextProfileId_IsDeterministicFromStoreState()
        {
            using var dir = new TempDir();
            var source = new LocalProfileSource(dir.Path);
            Assert.Equal("grommash#1", source.NextProfileId("grommash"));   // empty store

            source.Save(HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "G", null, 1, Fixed.Zero, ShapeOf("hero.level")));
            Assert.Equal("grommash#2", source.NextProfileId("grommash"));   // one existing
            Assert.Equal("valla#1", source.NextProfileId("valla"));         // per-hero counter
        }

        // ── MintId determinism ────────────────────────────────────────────────────────────

        [Fact]
        public void MintId_SameProfileId_SameHeroId_DistinctIdsDiffer()
        {
            HeroId a  = HeroProfileLoader.MintId(ProfileWithId("grommash#1"));
            HeroId a2 = HeroProfileLoader.MintId(ProfileWithId("grommash#1"));
            HeroId b  = HeroProfileLoader.MintId(ProfileWithId("grommash#2"));
            Assert.Equal(a.Value, a2.Value);   // reproducible
            Assert.NotEqual(a.Value, b.Value); // unique per ProfileId
        }

        // ── LoadInto → StartStateHash ───────────────────────────────────────────────────────

        [Fact]
        public void LoadInto_SameProfileTwice_YieldsByteIdenticalStartStateHash()
        {
            var model = new ScenarioData { Id = "s" };
            var placed = new[] { new HeroProfileLoader.PlacedHero(0, "grommash") };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "G", null, 3, SampleXp, ShapeOf("hero.level", "hero.xp"));

            var storeA = new HeroStore();
            Assert.Equal(1, HeroProfileLoader.LoadInto(storeA, placed, profile));
            ulong hashA = StartStateHash.Compute(model, storeA);

            var storeB = new HeroStore();
            Assert.Equal(1, HeroProfileLoader.LoadInto(storeB, placed, profile));
            ulong hashB = StartStateHash.Compute(model, storeB);

            Assert.Equal(hashA, hashB);
        }

        [Fact]
        public void LoadInto_LevelOrXpChange_ChangesStartStateHash()
        {
            var model = new ScenarioData { Id = "s" };
            var placed = new[] { new HeroProfileLoader.PlacedHero(0, "grommash") };

            ulong Hash(int level, Fixed xp)
            {
                var store = new HeroStore();
                HeroProfileLoader.LoadInto(store, placed,
                    HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "G", null, level, xp, ShapeOf("hero.level", "hero.xp")));
                return StartStateHash.Compute(model, store);
            }

            ulong baseline = Hash(3, SampleXp);
            Assert.NotEqual(baseline, Hash(4, SampleXp));                    // level differs
            Assert.NotEqual(baseline, Hash(3, Fixed.FromRaw(SampleXp.Raw + 1))); // xp differs by one raw
        }

        [Fact]
        public void LoadInto_IncompatibleOrNull_EqualsEmptyStoreHash()
        {
            var model = new ScenarioData { Id = "s" };
            var placed = new[] { new HeroProfileLoader.PlacedHero(0, "grommash") };
            ulong emptyHash = StartStateHash.Compute(model, new HeroStore());

            // Incompatible: the placed hero's unit id does not match the profile's HeroDefId → nothing minted.
            var incompat = new HeroStore();
            Assert.Equal(0, HeroProfileLoader.LoadInto(incompat, placed,
                HeroProfileLoader.BuildProfile("x#1", "some_other_hero", "orc", "X", null, 9, SampleXp, ShapeOf("hero.level", "hero.xp"))));
            Assert.Equal(emptyHash, StartStateHash.Compute(model, incompat));

            // Null profile → nothing minted.
            var nullStore = new HeroStore();
            Assert.Equal(0, HeroProfileLoader.LoadInto(nullStore, placed, null));
            Assert.Equal(emptyHash, StartStateHash.Compute(model, nullStore));
        }

        [Fact]
        public void LoadInto_DuplicateLiveId_SkipsDeterministically()
        {
            var model = new ScenarioData { Id = "s" };
            // Two placed heroes share the deployed hero's unit id → the same deterministic MintId → the second Mint
            // returns -1 (duplicate live id) and is skipped. Only one row is minted, deterministically, on every peer.
            var placed = new[]
            {
                new HeroProfileLoader.PlacedHero(0, "grommash"),
                new HeroProfileLoader.PlacedHero(1, "grommash"),
            };
            PlayerProfile profile = HeroProfileLoader.BuildProfile("grommash#1", "grommash", "orc", "G", null, 3, SampleXp, ShapeOf("hero.level", "hero.xp"));

            var storeA = new HeroStore();
            var storeB = new HeroStore();
            Assert.Equal(1, HeroProfileLoader.LoadInto(storeA, placed, profile));  // one minted, one skipped
            Assert.Equal(1, HeroProfileLoader.LoadInto(storeB, placed, profile));
            Assert.Equal(StartStateHash.Compute(model, storeA), StartStateHash.Compute(model, storeB));
        }

        // ── BuildProfile captures exactly the manifest-shape keys ──────────────────────────

        [Fact]
        public void BuildProfile_CapturesExactlyTheManifestShapeKeys()
        {
            PlayerProfile both = HeroProfileLoader.BuildProfile("g#1", "g", "f", "G", null, 3, SampleXp, ShapeOf("hero.level", "hero.xp"));
            Assert.Equal(2, both.Values.Count);
            Assert.Equal(3, both.Level);
            Assert.Equal(SampleXp.Raw, both.Xp.Raw);

            PlayerProfile levelOnly = HeroProfileLoader.BuildProfile("g#1", "g", "f", "G", null, 3, SampleXp, ShapeOf("hero.level"));
            Assert.Single(levelOnly.Values);
            Assert.Equal("hero.level", levelOnly.Values[0].Key);
            Assert.Equal(0, levelOnly.Xp.Raw);   // xp not captured (not in the shape) → default 0
        }

        // ── 3.9 is init-time only: the 3.2/3.8 hash versions are UNBUMPED ──────────────────

        [Fact]
        public void HashAlgoVersions_AreUnchanged()
        {
            Assert.Equal(15, SimChecksum.AlgoVersion); // Story 6.3: per-entity Elevation fold (14→15)
            Assert.Equal(6, CanonicalModelHash.AlgoVersion); // Story 6.5: pathability layer + slope config folded (5→6)
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        private static PlayerProfileShape ShapeOf(params string[] keys)
        {
            var m = new PersistenceManifest { Enabled = true };
            foreach (string k in keys) m.Attributes.Add(k);
            return m.DeriveProfileShape();
        }

        private static PlayerProfile ProfileWithId(string id) =>
            HeroProfileLoader.BuildProfile(id, "h", "f", "H", null, 1, Fixed.Zero, ShapeOf("hero.level"));

        private static PlayerProfile FindById(IReadOnlyList<PlayerProfile> list, string id)
        {
            foreach (PlayerProfile p in list)
                if (p.ProfileId == id) return p;
            Assert.Fail($"profile '{id}' not found");
            return null!;
        }

        private sealed class TempDir : System.IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_hero_profiles_" + System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
