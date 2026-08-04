#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-368 — LLM API keys are stored PER PROVIDER (<see cref="SecretIds.ForLlmProvider"/>:
    /// <c>llm_anthropic</c> / <c>llm_openrouter</c> / …), never under the one legacy shared <c>llm</c> id. The
    /// defect this pins down: with a single shared id, a key stored while Anthropic was selected would be sent
    /// verbatim to the OpenRouter endpoint after a provider switch. Covers: the factory never consuming the legacy
    /// shared id, each adapter receiving exactly its own provider's key, per-provider NoKey isolation, the id
    /// mapping being store-valid and collision-free, and the one-time
    /// <see cref="LlmProviderFactory.MigrateLegacySharedKey"/> migration of an existing shared key.
    /// </summary>
    public class PerProviderSecretIdTests
    {
        private static SettingsData Settings(string provider, string model = "m", string baseUrl = "")
            => new() { LlmProvider = provider, LlmModel = model, LlmBaseUrl = baseUrl };

        // ── The DW-368 regression: the legacy shared "llm" id feeds NO provider ───────────────

        [Fact]
        public void SharedLegacyKey_IsNeverConsumedByAnyCloudProvider()
        {
            // A store holding ONLY the pre-fix shared id. Under the shared-id defect BOTH cloud providers would
            // build successfully carrying this key (sending it to whichever endpoint was selected); per-provider
            // keying must instead report NoKey for both, because neither provider OWNS a key.
            var store = new FakeSecretStore();
            store.Set(SecretIds.Llm, "sk-shared-legacy");

            foreach (string providerId in new[] { "anthropic", "openrouter" })
            {
                bool ok = LlmProviderFactory.TryCreate(
                    Settings(providerId), store, new HttpClient(StubHttpMessageHandler.Ok("{}")),
                    out ILLMProvider? provider, out AiAvailability failure);

                Assert.False(ok);
                Assert.Null(provider);
                Assert.Equal(AiAvailability.NoKey, failure);
            }
        }

        // ── The ledger's exact scenario: an Anthropic key must not reach OpenRouter ───────────

        [Fact]
        public void AnthropicOnlyKey_AnthropicHealthy_OpenRouterNoKey()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ForLlmProvider("anthropic"), "sk-ant-only");

            Assert.True(LlmProviderFactory.TryCreate(
                Settings("anthropic"), store, new HttpClient(StubHttpMessageHandler.Ok("{}")),
                out ILLMProvider? anthropic, out AiAvailability aFail));
            Assert.Equal(AiAvailability.Healthy, aFail);
            Assert.IsType<AnthropicProvider>(anthropic);

            Assert.False(LlmProviderFactory.TryCreate(
                Settings("openrouter"), store, new HttpClient(StubHttpMessageHandler.Ok("{}")),
                out ILLMProvider? openRouter, out AiAvailability oFail));
            Assert.Null(openRouter);
            Assert.Equal(AiAvailability.NoKey, oFail);
        }

        // ── Each adapter gets exactly ITS provider's key (asserted at the wire) ───────────────

        [Fact]
        public async Task PerProviderKeys_EachAdapterSendsItsOwnKey()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ForLlmProvider("anthropic"),  "sk-ant");
            store.Set(SecretIds.ForLlmProvider("openrouter"), "sk-or");

            // Anthropic adapter → x-api-key header must carry the ANTHROPIC key.
            var antStub = StubHttpMessageHandler.Ok("{}");
            Assert.True(LlmProviderFactory.TryCreate(
                Settings("anthropic"), store, new HttpClient(antStub), out ILLMProvider? ant, out _));
            await ant!.GenerateAsync(new NormalizedRequest("s", "u"), CancellationToken.None);
            Assert.True(antStub.LastHeaders.TryGetValue("x-api-key", out string? antSent));
            Assert.Equal("sk-ant", antSent);

            // OpenRouter adapter → Authorization header must carry the OPENROUTER key, not the Anthropic one.
            var orStub = StubHttpMessageHandler.Ok("{}");
            Assert.True(LlmProviderFactory.TryCreate(
                Settings("openrouter"), store, new HttpClient(orStub), out ILLMProvider? or, out _));
            await or!.GenerateAsync(new NormalizedRequest("s", "u"), CancellationToken.None);
            Assert.True(orStub.LastHeaders.TryGetValue("Authorization", out string? orSent));
            Assert.Equal("Bearer sk-or", orSent);
        }

        // ── The synchronous four-state gate reflects per-provider key presence ────────────────

        [Fact]
        public void EvaluateConfig_ReflectsPerProviderKeyPresence()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ForLlmProvider("anthropic"), "sk-ant-only");
            var eval = new AiAvailabilityEvaluator(new HttpClient(StubHttpMessageHandler.Ok("{}")));

            Assert.Equal(AiAvailability.Healthy, eval.EvaluateConfig(Settings("anthropic"),  store));
            Assert.Equal(AiAvailability.NoKey,   eval.EvaluateConfig(Settings("openrouter"), store));
        }

        // ── Id mapping: pinned literals, collision-free, valid store key ids ──────────────────

        [Fact]
        public void ForLlmProvider_PinnedLiterals()
        {
            // Pin the produced ids so the Godot-coupled read/write sites (SettingsPanel / SettingsPhase /
            // TriggerEditorPhase) can never drift from the on-disk file names (llm_<provider>.key) or each other —
            // the same guard FileSecretStoreTests pins for the other canonical ids.
            Assert.Equal("llm_anthropic",  SecretIds.ForLlmProvider("anthropic"));
            Assert.Equal("llm_ollama",     SecretIds.ForLlmProvider("ollama"));
            Assert.Equal("llm_openrouter", SecretIds.ForLlmProvider("openrouter"));
        }

        [Fact]
        public void ForLlmProvider_IdsAreDistinctAndStoreValid()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                // Must never collide with the other canonical ids (or the legacy shared one).
                SecretIds.Llm, SecretIds.ModIo, SecretIds.ProofOfPlay,
            };

            foreach (LlmProviderCatalog.ProviderInfo p in LlmProviderCatalog.Providers)
            {
                string id = SecretIds.ForLlmProvider(p.Id);
                Assert.True(seen.Add(id), $"secret id '{id}' collides with another canonical id");

                // Round-trips through the REAL file store — proves the id satisfies its ^[a-z0-9_-]+$ rule.
                store.Set(id, "v-" + p.Id);
                Assert.Equal("v-" + p.Id, store.Get(id));
                Assert.True(File.Exists(Path.Combine(dir.Path, id + ".key")));
            }
        }

        // ── One-time migration of the legacy shared key ───────────────────────────────────────

        [Fact]
        public void Migrate_MovesSharedKeyToSelectedCloudProvider()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.Llm, "sk-migrate-me");

            bool migrated = LlmProviderFactory.MigrateLegacySharedKey(store, "openrouter");

            Assert.True(migrated);
            Assert.Equal("sk-migrate-me", store.Get(SecretIds.ForLlmProvider("openrouter")));
            Assert.False(store.Has(SecretIds.Llm)); // MOVED, not copied — no shared id left to re-migrate elsewhere

            // End-to-end: the provider it was stored for keeps working; no OTHER provider inherits it.
            Assert.True(LlmProviderFactory.TryCreate(
                Settings("openrouter"), store, new HttpClient(StubHttpMessageHandler.Ok("{}")), out _, out _));
            Assert.False(LlmProviderFactory.TryCreate(
                Settings("anthropic"), store, new HttpClient(StubHttpMessageHandler.Ok("{}")),
                out _, out AiAvailability antFail));
            Assert.Equal(AiAvailability.NoKey, antFail);
        }

        [Theory]
        [InlineData("ollama")]         // selected provider needs no key → key can't be its
        [InlineData("does-not-exist")] // unknown provider id
        [InlineData(null)]             // no selection available
        public void Migrate_NonCloudOrUnknownSelection_FallsBackToAnthropic(string? selected)
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.Llm, "sk-orphan");

            Assert.True(LlmProviderFactory.MigrateLegacySharedKey(store, selected));

            // Anthropic is the documented historical owner of the shared "llm" id.
            Assert.Equal("sk-orphan", store.Get(SecretIds.ForLlmProvider(LlmProviderCatalog.DefaultProviderId)));
            Assert.False(store.Has(SecretIds.Llm));
        }

        [Fact]
        public void Migrate_NoSharedKey_IsNoOp()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ForLlmProvider("anthropic"), "sk-keep");

            Assert.False(LlmProviderFactory.MigrateLegacySharedKey(store, "anthropic"));
            Assert.Equal("sk-keep", store.Get(SecretIds.ForLlmProvider("anthropic"))); // untouched
        }

        [Fact]
        public void Migrate_ExistingPerProviderKey_IsNotOverwritten_SharedDiscarded()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ForLlmProvider("openrouter"), "sk-newer");
            store.Set(SecretIds.Llm, "sk-stale-duplicate");

            Assert.True(LlmProviderFactory.MigrateLegacySharedKey(store, "openrouter"));

            Assert.Equal("sk-newer", store.Get(SecretIds.ForLlmProvider("openrouter"))); // kept
            Assert.False(store.Has(SecretIds.Llm)); // the stale shared duplicate is discarded, not left to linger
        }

        [Fact]
        public void Migrate_IsIdempotent()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.Llm, "sk-once");

            Assert.True(LlmProviderFactory.MigrateLegacySharedKey(store, "anthropic"));
            Assert.False(LlmProviderFactory.MigrateLegacySharedKey(store, "anthropic")); // second boot: no-op
            Assert.Equal("sk-once", store.Get(SecretIds.ForLlmProvider("anthropic")));
        }

        [Fact]
        public void Migrate_NullStore_ReturnsFalse()
            => Assert.False(LlmProviderFactory.MigrateLegacySharedKey(null, "anthropic"));

        // ── Temp-dir rail (mirrors FileSecretStoreTests) ──────────────────────────────────────

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "chimera_perprov_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort */ }
            }
        }
    }
}
