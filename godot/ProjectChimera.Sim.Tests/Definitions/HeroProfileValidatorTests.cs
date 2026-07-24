#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // PlayerProfile, HeroProfileValidator, ProfileInvalidReason
using ProjectChimera.Combat;             // HeroXpSystem.XpCeiling
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 9.12 — the canonical <see cref="HeroProfileValidator"/> rule set (the single source of truth the init-time
    /// apply gate, the client pre-flight, and the TS server RPC all obey). Every I/O-matrix row is driven off the SHARED
    /// fixture <c>docs/server-deploy/nakama-modules/test/fixtures/validation-cases.json</c> (embedded), the SAME oracle
    /// the TS <c>validation.test.ts</c> runs — so the C# and TS validators are proven in sync against one source of
    /// truth, not two hand-kept copies. Godot-free (Tier-1).
    /// </summary>
    public class HeroProfileValidatorTests
    {
        // ── Shared fixture (C#<->TS parity oracle) ────────────────────────────────────────

        private sealed class FixtureCase
        {
            public string Name { get; set; } = "";
            public bool ExpectValid { get; set; }
            public string ExpectReason { get; set; } = "none";
            public JsonElement Profile { get; set; }
        }

        private static List<FixtureCase> LoadCases(string arrayName)
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("validation-cases.json")
                ?? throw new InvalidOperationException("Embedded shared fixture 'validation-cases.json' not found.");
            using var reader = new StreamReader(s);
            using JsonDocument doc = JsonDocument.Parse(reader.ReadToEnd());
            var list = new List<FixtureCase>();
            foreach (JsonElement el in doc.RootElement.GetProperty(arrayName).EnumerateArray())
                list.Add(new FixtureCase
                {
                    Name         = el.GetProperty("name").GetString() ?? "",
                    ExpectValid  = el.GetProperty("expect_valid").GetBoolean(),
                    ExpectReason = el.GetProperty("expect_reason").GetString() ?? "none",
                    Profile      = el.GetProperty("profile").Clone(),
                });
            return list;
        }

        public static IEnumerable<object[]> SharedCases()
        {
            foreach (FixtureCase c in LoadCases("cases")) yield return new object[] { c.Name };
        }

        public static IEnumerable<object[]> TsOnlyCases()
        {
            foreach (FixtureCase c in LoadCases("ts_only_cases")) yield return new object[] { c.Name };
        }

        private static ProfileInvalidReason ReasonOf(string s) =>
            Enum.Parse<ProfileInvalidReason>(s, ignoreCase: true);

        /// <summary>Every row of the shared C#<->TS oracle: the C# validator must agree with the fixture's expected
        /// verdict + reason. If this drifts from the TS suite, the two validators disagree — the exact class the shared
        /// oracle exists to catch.</summary>
        [Theory]
        [MemberData(nameof(SharedCases))]
        public void SharedFixture_CSharpValidatorMatchesOracle(string caseName)
        {
            FixtureCase c = LoadCases("cases").Find(x => x.Name == caseName)
                ?? throw new InvalidOperationException($"Case '{caseName}' missing.");

            PlayerProfile? profile = JsonSerializer.Deserialize<PlayerProfile>(c.Profile.GetRawText());
            ProfileValidation v = HeroProfileValidator.Validate(profile);

            Assert.Equal(c.ExpectValid, v.IsValid);
            Assert.Equal(ReasonOf(c.ExpectReason), v.Reason);
        }

        /// <summary>P1: the TS-only boundary cases (out-of-Int32 raws / non-array containers) cannot round-trip through
        /// the C# <see cref="PlayerProfile"/> — System.Text.Json THROWS at the deserialization boundary (an out-of-range
        /// number overflows the <c>int</c> property; a non-array is a type mismatch). So the C# rail is fail-closed
        /// against them BY CONSTRUCTION at the boundary, and the TS validator (which parses lenient JSON) must reject
        /// them explicitly (vitest asserts that). This pins that C# never accepts a payload the TS side would need to
        /// guard — closing the C#<->TS parity break P1 fixes.</summary>
        [Theory]
        [MemberData(nameof(TsOnlyCases))]
        public void TsOnlyBoundaryCases_CSharpRejectsAtTheDeserializationBoundary(string caseName)
        {
            FixtureCase c = LoadCases("ts_only_cases").Find(x => x.Name == caseName)
                ?? throw new InvalidOperationException($"Case '{caseName}' missing.");

            bool rejected;
            try
            {
                PlayerProfile? p = JsonSerializer.Deserialize<PlayerProfile>(c.Profile.GetRawText());
                rejected = !HeroProfileValidator.Validate(p).IsValid; // if it somehow deserialized, it must still be invalid
            }
            catch
            {
                rejected = true; // threw at the deserialization boundary → fail-closed
            }
            Assert.True(rejected, $"C# must reject ts-only boundary case '{caseName}'");
        }

        [Fact]
        public void SharedFixture_CoversEveryInvalidReasonClass()
        {
            // Guards the oracle itself: it must exercise a valid case AND each invalid reason class, or a validator bug
            // in an uncovered class would pass silently.
            var reasons = new HashSet<string>();
            bool sawValid = false;
            foreach (FixtureCase c in LoadCases("cases"))
            {
                if (c.ExpectValid) sawValid = true; else reasons.Add(c.ExpectReason);
            }
            Assert.True(sawValid);
            Assert.Contains("identity", reasons);
            Assert.Contains("range", reasons);
            Assert.Contains("inventory", reasons);
            Assert.Contains("attributes", reasons);
        }

        // ── Direct assertions not expressible as a JSON-object fixture case ─────────────────

        [Fact]
        public void Validate_NullProfile_IsIdentity()
            => Assert.Equal(ProfileInvalidReason.Identity, HeroProfileValidator.Validate(null).Reason);

        // ── The range predicate is the exact former LoadInto gate (behaviour-neutral delegation) ──

        [Fact]
        public void IsLevelXpInRange_MatchesFormerInlinePredicate()
        {
            Assert.True(HeroProfileValidator.IsLevelXpInRange(0, 0));
            Assert.True(HeroProfileValidator.IsLevelXpInRange(3, HeroXpSystem.XpCeiling.Raw)); // inclusive ceiling
            Assert.False(HeroProfileValidator.IsLevelXpInRange(-1, 0));
            Assert.False(HeroProfileValidator.IsLevelXpInRange(1, -1));
            Assert.False(HeroProfileValidator.IsLevelXpInRange(1, HeroXpSystem.XpCeiling.Raw + 1));
        }

        [Fact]
        public void XpCeilingRaw_MatchesSharedFixtureConstant()
        {
            // The TS mirror hardcodes XP_CEILING_RAW; assert the C# ceiling equals the fixture's declared value so a
            // future XpCeiling change that forgets the TS side is caught here.
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("validation-cases.json")!;
            using var reader = new StreamReader(s);
            using JsonDocument doc = JsonDocument.Parse(reader.ReadToEnd());
            long fixtureCeiling = doc.RootElement.GetProperty("xp_ceiling_raw").GetInt64();
            Assert.Equal(HeroXpSystem.XpCeiling.Raw, fixtureCeiling);
        }
    }
}
