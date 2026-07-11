#nullable enable
using System.Text.Json;
using ProjectChimera.Core.Definitions;   // SettingsData
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 5.9 (NFR-2) — <see cref="SettingsData.HasSeenOnboarding"/> round-trips through the SAME
    /// <see cref="JsonSerializerOptions"/> shape <c>SettingsManager.Load</c>/<c>Save</c> use (WriteIndented,
    /// comments skipped, trailing commas allowed) — <see cref="SettingsData"/> itself is Godot-free (a plain DTO
    /// under <c>src/Core/Definitions</c>), so this exercises the exact serialize/deserialize behavior headlessly
    /// without needing the Godot <c>SettingsManager</c> Node or a live <c>user://settings.json</c> file.
    /// </summary>
    public class SettingsDataRoundTripTests
    {
        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented        = true,
            ReadCommentHandling  = JsonCommentHandling.Skip,
            AllowTrailingCommas  = true,
        };

        [Fact]
        public void HasSeenOnboarding_DefaultsFalse()
        {
            var settings = new SettingsData();
            Assert.False(settings.HasSeenOnboarding);
        }

        [Fact]
        public void HasSeenOnboarding_SurvivesSerializeRoundTrip_WhenTrue()
        {
            var original = new SettingsData { HasSeenOnboarding = true };

            string json = JsonSerializer.Serialize(original, Opts);
            var reloaded = JsonSerializer.Deserialize<SettingsData>(json, Opts);

            Assert.NotNull(reloaded);
            Assert.True(reloaded!.HasSeenOnboarding);
        }

        [Fact]
        public void HasSeenOnboarding_SurvivesSerializeRoundTrip_WhenFalse()
        {
            var original = new SettingsData { HasSeenOnboarding = false };

            string json = JsonSerializer.Serialize(original, Opts);
            var reloaded = JsonSerializer.Deserialize<SettingsData>(json, Opts);

            Assert.NotNull(reloaded);
            Assert.False(reloaded!.HasSeenOnboarding);
        }

        [Fact]
        public void HasSeenOnboarding_AbsentFromOldSaveFile_DefaultsFalse()
        {
            // Simulates a pre-5.9 settings.json that predates this field entirely — must deserialize to the
            // safe default rather than throwing or leaving the field uninitialized (spec Boundaries: "defaulting
            // false so old save files are unaffected").
            const string legacyJson = "{ \"camera_speed\": 1.0, \"master_volume\": 1.0 }";

            var reloaded = JsonSerializer.Deserialize<SettingsData>(legacyJson, Opts);

            Assert.NotNull(reloaded);
            Assert.False(reloaded!.HasSeenOnboarding);
        }

        [Fact]
        public void JsonKey_IsSnakeCase_HasSeenOnboarding()
        {
            var original = new SettingsData { HasSeenOnboarding = true };
            string json = JsonSerializer.Serialize(original, Opts);
            Assert.Contains("\"has_seen_onboarding\"", json);
        }
    }
}
