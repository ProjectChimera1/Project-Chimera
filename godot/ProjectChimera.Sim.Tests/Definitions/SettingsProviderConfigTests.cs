#nullable enable
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 8.1 — the versioned provider config on <see cref="SettingsData"/>: new fields round-trip through the
    /// SAME serializer shape <c>SettingsManager.Load/Save</c> use; an old file lacking the fields loads to safe
    /// defaults (provider <c>anthropic</c>, model <c>claude-sonnet-5</c>, schema version stamped by
    /// <see cref="SettingsData.MigrateForward"/>) with no error; a free-text model override persists verbatim; the
    /// catalog still exposes the provider's curated model list; and the API key is NEVER written into the JSON.
    /// Godot-free / Tier-1, mirroring <c>SettingsDataRoundTripTests</c>.
    /// </summary>
    public class SettingsProviderConfigTests
    {
        // DW-134: the REAL options SettingsManager.Load/Save use (the same instance), not a hand-rolled replica that
        // could silently drift from them — see SettingsJson / SettingsDataRoundTripTests.
        private static readonly JsonSerializerOptions Opts = SettingsJson.Options;

        [Fact]
        public void Defaults_AreSafe()
        {
            var s = new SettingsData();
            Assert.Equal("anthropic", s.LlmProvider);
            Assert.Equal("claude-sonnet-5", s.LlmModel);
            Assert.Equal("", s.LlmBaseUrl);
        }

        [Fact]
        public void NewFields_RoundTrip_ThroughSerializerShape()
        {
            var original = new SettingsData
            {
                LlmProvider = "ollama",
                LlmModel    = "mistral",
                LlmBaseUrl  = "http://localhost:11434",
            }.MigrateForward();

            string json = JsonSerializer.Serialize(original, Opts);
            var back = JsonSerializer.Deserialize<SettingsData>(json, Opts);

            Assert.NotNull(back);
            Assert.Equal("ollama", back!.LlmProvider);
            Assert.Equal("mistral", back.LlmModel);
            Assert.Equal("http://localhost:11434", back.LlmBaseUrl);
            Assert.Equal(SettingsData.CurrentSchemaVersion, back.SchemaVersion);
        }

        [Fact]
        public void JsonKeys_AreSnakeCase()
        {
            string json = JsonSerializer.Serialize(new SettingsData().MigrateForward(), Opts);
            Assert.Contains("\"schema_version\"", json);
            Assert.Contains("\"llm_provider\"", json);
            Assert.Contains("\"llm_model\"", json);
            Assert.Contains("\"llm_base_url\"", json);
        }

        [Fact]
        public void OldFile_WithoutProviderFields_MigratesToDefaults_NoError()
        {
            // A pre-8.1 settings.json that predates provider fields + schema version entirely.
            const string legacyJson = "{ \"camera_speed\": 1.0, \"master_volume\": 1.0 }";

            var loaded = JsonSerializer.Deserialize<SettingsData>(legacyJson, Opts);
            Assert.NotNull(loaded);
            Assert.Equal(0, loaded!.SchemaVersion); // absent field ⇒ 0 before migration

            loaded.MigrateForward();

            Assert.Equal("anthropic", loaded.LlmProvider);
            Assert.Equal("claude-sonnet-5", loaded.LlmModel);
            Assert.Equal(SettingsData.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void MigrateForward_UnknownProvider_ResetToDefault()
        {
            var s = new SettingsData { LlmProvider = "totally-unknown" }.MigrateForward();
            Assert.Equal("anthropic", s.LlmProvider);
        }

        [Fact]
        public void MigrateForward_EmptyModel_ResetToDefault()
        {
            var s = new SettingsData { LlmModel = "" }.MigrateForward();
            Assert.Equal("claude-sonnet-5", s.LlmModel);
        }

        [Fact]
        public void MigrateForward_ExplicitNullBaseUrl_NormalizedToEmpty()
        {
            // A settings.json with `"llm_base_url": null` deserializes the property to null; MigrateForward must
            // normalize it to "" so Story 8.2's base-URL resolution never sees a null.
            const string json = "{ \"llm_base_url\": null }";
            var loaded = JsonSerializer.Deserialize<SettingsData>(json, Opts)!.MigrateForward();
            Assert.Equal("", loaded.LlmBaseUrl);
        }

        [Fact]
        public void MigrateForward_ExplicitNullEndpointFields_NormalizedToEmpty_AndSchemaBumped()
        {
            // Story 9.7: a settings.json written before the multiplayer endpoint fields existed (or with any of
            // them explicitly `null`) deserializes those properties to null. MigrateForward must normalize the
            // three string endpoint fields to "" — the schema-v2 migration contract — so lobby composition's
            // null/empty fallback (MatchLifecycleController) never has to distinguish null from "". It must also
            // stamp SchemaVersion to the current version so a subsequent Save persists the bump.
            const string json =
                "{ \"game_server_ip\": null, \"nakama_host\": null, \"nakama_key\": null }";
            var loaded = JsonSerializer.Deserialize<SettingsData>(json, Opts)!.MigrateForward();

            Assert.Equal("", loaded.GameServerIp);
            Assert.Equal("", loaded.NakamaHost);
            Assert.Equal("", loaded.NakamaKey);
            Assert.Equal(SettingsData.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void FromJson_LoadSeam_DeserializesAndMigrates()
        {
            // Pins the SettingsManager.Load seam contract (the Node itself is Godot-coupled / un-unit-testable):
            // FromJson must deserialize AND forward-migrate — an unknown provider / empty model in the file comes
            // back normalized with the schema version stamped.
            const string legacyJson =
                "{ \"llm_provider\": \"totally-unknown\", \"llm_model\": \"\", \"camera_speed\": 1.0 }";

            var loaded = SettingsData.FromJson(legacyJson, Opts);

            Assert.Equal("anthropic", loaded.LlmProvider);
            Assert.Equal("claude-sonnet-5", loaded.LlmModel);
            Assert.Equal("", loaded.LlmBaseUrl);
            Assert.Equal(SettingsData.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void FromJson_NullOrEmptyDeserialization_FallsBackToDefaults()
        {
            // The JSON literal `null` deserializes to a null SettingsData; FromJson must fall back to a fresh,
            // migrated instance rather than returning null.
            var loaded = SettingsData.FromJson("null", Opts);
            Assert.Equal("anthropic", loaded.LlmProvider);
            Assert.Equal(SettingsData.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void FreeTextModelOverride_Persists_AndCatalogStillExposesCuratedList()
        {
            var s = new SettingsData
            {
                LlmProvider = "anthropic",
                LlmModel    = "some-custom:tag", // not in the curated list — a free-text override
            }.MigrateForward();

            string json = JsonSerializer.Serialize(s, Opts);
            var back = JsonSerializer.Deserialize<SettingsData>(json, Opts);

            Assert.Equal("some-custom:tag", back!.LlmModel); // free-text value round-trips unchanged
            Assert.Equal("anthropic", back.LlmProvider);     // provider stays valid (still known)

            // The catalog still exposes that provider's curated list independent of the free-text pick.
            Assert.True(LlmProviderCatalog.TryGet("anthropic", out var info));
            Assert.NotEmpty(info!.Models);
        }

        [Fact]
        public void SelectedProvider_ExposesCuratedModelList()
        {
            var s = new SettingsData { LlmProvider = "openrouter" }.MigrateForward();
            Assert.True(LlmProviderCatalog.TryGet(s.LlmProvider, out var info));
            Assert.NotEmpty(info!.Models);
        }

        [Fact]
        public void ApiKey_IsNeverPresentInSettingsJson()
        {
            // SettingsData has no key field at all — serialized JSON must never contain a key-shaped value.
            string json = JsonSerializer.Serialize(new SettingsData
            {
                LlmProvider = "anthropic",
                LlmModel    = "claude-sonnet-5",
            }.MigrateForward(), Opts);

            Assert.DoesNotContain("api_key", json);
            Assert.DoesNotContain("apikey", json.ToLowerInvariant());
            Assert.DoesNotContain("sk-", json);
        }
    }
}
