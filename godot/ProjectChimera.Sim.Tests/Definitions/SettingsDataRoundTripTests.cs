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

        // ── DW-441 — the shared-team-vision toggle (Story 9.14 follow-up) ──────

        [Fact]
        public void SharedTeamVision_DefaultsTrue()
        {
            // Default ON = the pre-DW-441 hardwired behavior (FogOfWarSystem.SharedTeamVision also defaults true),
            // so adding the user-facing control changes nothing until a player deliberately opts out.
            Assert.True(new SettingsData().SharedTeamVision);
        }

        [Fact]
        public void SharedTeamVision_SurvivesSerializeRoundTrip_WhenFalse()
        {
            var original = new SettingsData { SharedTeamVision = false };

            string json = JsonSerializer.Serialize(original, Opts);
            var reloaded = SettingsData.FromJson(json, Opts);

            Assert.False(reloaded.SharedTeamVision); // an explicit opt-out persists across sessions
        }

        [Fact]
        public void SharedTeamVision_AbsentFromOldSaveFile_DefaultsTrue()
        {
            // A pre-DW-441 settings.json lacking the key must land on the safe default (shared vision ON —
            // exactly what every teamed match did before the toggle existed), not throw or flip behavior.
            const string legacyJson = "{ \"camera_speed\": 1.0, \"master_volume\": 1.0 }";

            var reloaded = SettingsData.FromJson(legacyJson, Opts);

            Assert.True(reloaded.SharedTeamVision);
        }

        [Fact]
        public void SharedTeamVision_JsonKey_IsSnakeCase()
        {
            string json = JsonSerializer.Serialize(new SettingsData(), Opts);
            Assert.Contains("\"shared_team_vision\"", json);
        }

        [Fact]
        public void SharedTeamVision_False_IsPreservedByMigration()
        {
            // MigrateForward normalizes enum-shaped/corrupt fields; it must never reset a player's explicit
            // shared-vision opt-out back to the default.
            var s = new SettingsData { SharedTeamVision = false }.MigrateForward();
            Assert.False(s.SharedTeamVision);
        }

        // ── Story 11.7 — the six video fields ──────────────────────────────────

        [Fact]
        public void Video_DefaultsAreSafe()
        {
            var s = new SettingsData();
            Assert.Equal(1920, s.ResolutionWidth);
            Assert.Equal(1080, s.ResolutionHeight);
            Assert.Equal("windowed", s.WindowMode);
            Assert.True(s.Vsync);
            Assert.Equal("medium", s.QualityPreset);
            Assert.Equal(1.0f, s.UiScale);
        }

        [Fact]
        public void Video_SurvivesSerializeRoundTrip()
        {
            var original = new SettingsData
            {
                ResolutionWidth  = 2560,
                ResolutionHeight = 1440,
                WindowMode       = "fullscreen",
                Vsync            = false,
                QualityPreset    = "high",
                UiScale          = 1.25f,
            };

            string json = JsonSerializer.Serialize(original, Opts);
            var reloaded = SettingsData.FromJson(json, Opts);

            Assert.Equal(2560, reloaded.ResolutionWidth);
            Assert.Equal(1440, reloaded.ResolutionHeight);
            Assert.Equal("fullscreen", reloaded.WindowMode);
            Assert.False(reloaded.Vsync);
            Assert.Equal("high", reloaded.QualityPreset);
            Assert.Equal(1.25f, reloaded.UiScale);
            Assert.Equal(SettingsData.CurrentSchemaVersion, reloaded.SchemaVersion);
        }

        [Fact]
        public void Video_JsonKeys_AreSnakeCase()
        {
            var json = JsonSerializer.Serialize(new SettingsData(), Opts);
            Assert.Contains("\"resolution_width\"", json);
            Assert.Contains("\"resolution_height\"", json);
            Assert.Contains("\"window_mode\"", json);
            Assert.Contains("\"vsync\"", json);
            Assert.Contains("\"quality_preset\"", json);
            Assert.Contains("\"ui_scale\"", json);
        }

        [Fact]
        public void Video_AbsentFromOldSaveFile_DefaultsToSafeValues()
        {
            // A pre-11.7 settings.json lacking the six video keys — must land on the defaults, not throw.
            const string legacyJson = "{ \"camera_speed\": 1.0, \"master_volume\": 1.0 }";

            var reloaded = SettingsData.FromJson(legacyJson, Opts);

            Assert.Equal(1920, reloaded.ResolutionWidth);
            Assert.Equal(1080, reloaded.ResolutionHeight);
            Assert.Equal("windowed", reloaded.WindowMode);
            Assert.True(reloaded.Vsync);
            Assert.Equal("medium", reloaded.QualityPreset);
            Assert.Equal(1.0f, reloaded.UiScale);
        }

        [Fact]
        public void Video_UnknownWindowMode_ResetsToDefault()
        {
            const string json = "{ \"window_mode\": \"cinema\" }";
            var reloaded = SettingsData.FromJson(json, Opts);
            Assert.Equal("windowed", reloaded.WindowMode);
        }

        [Fact]
        public void Video_UnknownQualityPreset_ResetsToDefault()
        {
            const string json = "{ \"quality_preset\": \"ultra\" }";
            var reloaded = SettingsData.FromJson(json, Opts);
            Assert.Equal("medium", reloaded.QualityPreset);
        }

        [Theory]
        [InlineData(5.0f, 1.5f)]   // above the band → clamps down
        [InlineData(0.1f, 0.75f)]  // below the band → clamps up
        [InlineData(1.25f, 1.25f)] // in-band → preserved
        public void Video_UiScale_IsClampedToBand(float input, float expected)
        {
            string json = $"{{ \"ui_scale\": {input.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}";
            var reloaded = SettingsData.FromJson(json, Opts);
            Assert.Equal(expected, reloaded.UiScale);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Video_UiScale_NonFinite_FallsBackToOne(float bad)
        {
            // Math.Clamp passes NaN through, so a hand-corrupted non-finite ui_scale must be caught BEFORE the clamp
            // or it becomes a NaN ContentScaleFactor → blank UI. (JSON literals can't carry NaN/Inf, so drive
            // MigrateForward directly — the same normalization SettingsManager.Load runs.)
            var s = new SettingsData { UiScale = bad }.MigrateForward();
            Assert.Equal(1.0f, s.UiScale);
        }

        [Fact]
        public void Video_ValidEnums_ArePreservedByMigration()
        {
            var s = new SettingsData { WindowMode = "borderless", QualityPreset = "low" }.MigrateForward();
            Assert.Equal("borderless", s.WindowMode);
            Assert.Equal("low", s.QualityPreset);
        }

        [Fact]
        public void SchemaVersion_IsStampedTo3_OnLoad()
        {
            var reloaded = SettingsData.FromJson("{ }", Opts);
            Assert.Equal(3, reloaded.SchemaVersion);
            Assert.Equal(3, SettingsData.CurrentSchemaVersion);
        }

        [Theory]
        [InlineData(1, 1)]         // sub-pixel → reset (would restore a 1×1 window on boot)
        [InlineData(1920, 100)]    // height below the floor → reset
        [InlineData(100000, 1080)] // absurd width above the cap → reset
        public void Video_CorruptResolution_ResetsToDefault(int w, int h)
        {
            // A hand-corrupted/legacy resolution outside the sane band must fall back to 1080p — ApplyVideo has no
            // floor, so a sub-pixel size would boot an unusable window where safe-revert never arms.
            var s = new SettingsData { ResolutionWidth = w, ResolutionHeight = h }.MigrateForward();
            Assert.Equal(1920, s.ResolutionWidth);
            Assert.Equal(1080, s.ResolutionHeight);
        }

        [Fact]
        public void Video_ValidResolution_IsPreservedByMigration()
        {
            var s = new SettingsData { ResolutionWidth = 2560, ResolutionHeight = 1440 }.MigrateForward();
            Assert.Equal(2560, s.ResolutionWidth);
            Assert.Equal(1440, s.ResolutionHeight);
        }

        // ── DW-482 — newer-schema files bail out of MigrateForward (no downgrade-stamp) ──────────────
        //
        // A settings.json written by a NEWER build carries a higher schema_version. This build's
        // MigrateForward encodes CURRENT rules only, so it must return the instance untouched — stamping
        // CurrentSchemaVersion (or clamping/resetting values legal under the newer schema) would persist
        // a silent downgrade on the next Save.

        [Fact]
        public void NewerSchema_MigrateForward_DoesNotDowngradeStamp()
        {
            int future = SettingsData.CurrentSchemaVersion + 1;
            var s = new SettingsData { SchemaVersion = future }.MigrateForward();
            Assert.Equal(future, s.SchemaVersion);
        }

        [Fact]
        public void NewerSchema_FromJson_PreservesVersionAndOutOfBandValues()
        {
            // ui_scale 2.5 and window_mode "ultrawide" are illegal under THIS build's schema but may be
            // legal under the newer one that wrote the file — the bail must leave both verbatim rather
            // than clamp/reset them to current-build rules.
            int future = SettingsData.CurrentSchemaVersion + 1;
            string json = $"{{ \"schema_version\": {future}, \"ui_scale\": 2.5, \"window_mode\": \"ultrawide\" }}";

            var s = SettingsData.FromJson(json, Opts);

            Assert.Equal(future, s.SchemaVersion);
            Assert.Equal(2.5f, s.UiScale);
            Assert.Equal("ultrawide", s.WindowMode);
        }

        [Fact]
        public void NewerSchema_SurvivesLoadThenSaveRoundTrip_WithoutVersionDowngrade()
        {
            // The exact defect from the ledger: load a newer file, then Save — the persisted JSON must
            // still carry the FUTURE schema_version, not this build's, so the newer build re-normalizes
            // under its own rules on the next upgrade instead of trusting a downgraded stamp.
            int future = SettingsData.CurrentSchemaVersion + 2;
            string json = $"{{ \"schema_version\": {future}, \"quality_preset\": \"cinematic\" }}";

            var loaded = SettingsData.FromJson(json, Opts);
            string saved = JsonSerializer.Serialize(loaded, Opts);
            var reloaded = JsonSerializer.Deserialize<SettingsData>(saved, Opts);

            Assert.NotNull(reloaded);
            Assert.Equal(future, reloaded!.SchemaVersion);
            Assert.Equal("cinematic", reloaded.QualityPreset);
        }

        [Fact]
        public void CurrentSchema_StillNormalizesAndStamps()
        {
            // Guard against overshooting the bail to >=: a file AT the current version still gets the
            // full normalization pass (it may be hand-edited), and the stamp is a no-op.
            string json = $"{{ \"schema_version\": {SettingsData.CurrentSchemaVersion}, \"window_mode\": \"cinema\", \"ui_scale\": 9.0 }}";

            var s = SettingsData.FromJson(json, Opts);

            Assert.Equal(SettingsData.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("windowed", s.WindowMode); // unknown mode reset under current rules
            Assert.Equal(1.5f, s.UiScale);          // clamped under current rules
        }

        [Fact]
        public void OlderSchema_StillMigratesAndStamps()
        {
            // The pre-existing upgrade path must be unaffected by the bail: an older file (v2, explicit
            // null endpoint string) is normalized and stamped up to the current version.
            const string json = "{ \"schema_version\": 2, \"nakama_host\": null, \"window_mode\": \"cinema\" }";

            var s = SettingsData.FromJson(json, Opts);

            Assert.Equal(SettingsData.CurrentSchemaVersion, s.SchemaVersion);
            Assert.Equal("", s.NakamaHost);
            Assert.Equal("windowed", s.WindowMode);
        }
    }
}
