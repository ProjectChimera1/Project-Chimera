#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-134 — <c>SettingsManager</c>'s serializer options and the two Tier-1 settings round-trip suites used to be
    /// THREE hand-maintained copies of one persistence-critical setting.
    ///
    /// <para><b>Why that was a real gap, not tidiness.</b> <c>SettingsManager</c> is a Godot <c>Node</c>, so it cannot
    /// be constructed in this Godot-free assembly; <c>SettingsDataRoundTripTests</c> and
    /// <c>SettingsProviderConfigTests</c> therefore each declared a local <see cref="JsonSerializerOptions"/> that
    /// "matched" it. They were byte-for-byte identical by coincidence and nothing enforced they stayed so — if the
    /// real options had later gained a naming policy or a converter, <c>HasSeenOnboarding</c> (or any field's)
    /// persistence could regress in the shipped game while both "round-trip" suites stayed green, because they would
    /// be validating the DTO against a serializer shape the game does not use.</para>
    ///
    /// <para>The closure removes the drift surface instead of guarding it: one Godot-free
    /// <see cref="SettingsJson.Options"/> that the Node and both suites reference. The source pins below are what
    /// enforce that — a runtime posture assertion alone would still pass against a re-introduced replica, since a
    /// replica is equal TODAY; only a source scan can tell "the same instance" from "the same values".</para>
    /// </summary>
    public class SettingsJsonSingleSourceTests
    {
        // ── The shared posture (the values the shipped settings file is written with) ────────────────────

        [Fact]
        public void SharedOptions_CarryTheDocumentedPosture()
        {
            JsonSerializerOptions o = SettingsJson.Options;

            Assert.True(o.WriteIndented);                                   // settings.json is hand-editable
            Assert.Equal(JsonCommentHandling.Skip, o.ReadCommentHandling);  // a hand-added note must not blank settings
            Assert.True(o.AllowTrailingCommas);
            Assert.Null(o.PropertyNamingPolicy);                            // names come from [JsonPropertyName]
            Assert.Empty(o.Converters);
            Assert.Equal(JsonUnmappedMemberHandling.Skip, o.UnmappedMemberHandling); // forward-compat: unknown key ⇒ no-op
        }

        [Fact]
        public void SharedOptions_RoundTripASettingsFile_WithCommentsAndATrailingComma()
        {
            // The posture's whole point: a hand-edited file still loads rather than silently reverting to defaults.
            const string handEdited = """
            {
              // the creator turned onboarding off by hand
              "has_seen_onboarding": true,
            }
            """;

            var loaded = JsonSerializer.Deserialize<SettingsData>(handEdited, SettingsJson.Options);
            Assert.NotNull(loaded);
            Assert.True(loaded!.HasSeenOnboarding);

            string written = JsonSerializer.Serialize(loaded, SettingsJson.Options);
            Assert.Contains("\n", written);   // WriteIndented survived the round trip
            Assert.True(JsonSerializer.Deserialize<SettingsData>(written, SettingsJson.Options)!.HasSeenOnboarding);
        }

        // ── The pins: no consumer may re-declare its own copy ───────────────────────────────────────────

        [Fact]
        public void SettingsManager_UsesTheSharedOptions_NotAPrivateReplica()
        {
            string path = RepoFile("godot", "src", "UI", "SettingsManager.cs");
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");

            string blob = StripCommentsAndNormalize(File.ReadAllText(path));

            // Vacuous-pass guard: the Node must still own a serializer options handle at all.
            Assert.Matches(@"JsonSerializerOptions\s+_jsonOpts\b", blob);

            Assert.Matches(@"_jsonOpts\s*=\s*SettingsJson\.Options\b", blob);
            Assert.False(Regex.IsMatch(blob, @"ReadCommentHandling\s*="),
                "SettingsManager re-declares its own JsonSerializerOptions body. DW-134: reference " +
                "SettingsJson.Options so the Tier-1 round-trip suites exercise the REAL serializer shape instead of " +
                "a replica that can silently drift from it.");
        }

        [Theory]
        [InlineData("SettingsDataRoundTripTests.cs")]
        [InlineData("SettingsProviderConfigTests.cs")]
        public void TheTier1SettingsSuites_ReferenceTheSharedOptions(string fileName)
        {
            string path = TestFile(fileName);
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");

            string blob = StripCommentsAndNormalize(File.ReadAllText(path));

            Assert.Matches(@"JsonSerializerOptions\s+Opts\s*=\s*SettingsJson\.Options\b", blob);
            Assert.False(Regex.IsMatch(blob, @"ReadCommentHandling\s*="),
                $"{fileName} hand-rolls a JsonSerializerOptions replica of SettingsManager's. DW-134: that is the " +
                "exact drift surface this entry closed — reference SettingsJson.Options instead.");
        }

        // ── Source-scan plumbing (mirrors CommandApplyParityTests) ──────────────────────────────────────

        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }

        /// <summary>Resolve a repo-relative path — this file lives in
        /// godot/ProjectChimera.Sim.Tests/Definitions/, so <c>../../..</c> is the repo root.</summary>
        private static string RepoFile(string a, string b, string c, string d)
        {
            var segments = new System.Collections.Generic.List<string> { HereDir(), "..", "..", "..", a, b, c, d };
            return Path.GetFullPath(Path.Combine(segments.ToArray()));
        }

        /// <summary>A sibling test file in this same directory.</summary>
        private static string TestFile(string fileName) => Path.GetFullPath(Path.Combine(HereDir(), fileName));

        private static string HereDir([CallerFilePath] string thisFilePath = "")
            => Path.GetDirectoryName(thisFilePath)
               ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
    }
}
