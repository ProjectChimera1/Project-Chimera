#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — the single construction site: correct adapter per provider id, base-URL override vs catalog
    /// default, key sourced from the secret store, non-allowlisted host → synchronous refusal (cloud → NoProvider;
    /// ollama non-loopback → HostRestricted per DW-370), and the no-fallback contract (a failing provider never
    /// yields another's success).
    /// </summary>
    public class LlmProviderFactoryTests
    {
        private static SettingsData Settings(string provider, string model = "m", string baseUrl = "")
            => new() { LlmProvider = provider, LlmModel = model, LlmBaseUrl = baseUrl };

        private static HttpClient AnyClient() => new(StubHttpMessageHandler.Ok("{}"));

        [Fact]
        public void Anthropic_WithKey_BuildsAnthropicProvider()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic"), new FakeSecretStore("sk-x"), AnyClient(), out var provider, out var failure);

            Assert.True(ok);
            Assert.Equal(AiAvailability.Healthy, failure);
            Assert.IsType<AnthropicProvider>(provider);
            Assert.Equal("anthropic", provider!.ProviderId);
        }

        [Fact]
        public void Ollama_NoKey_BuildsOllamaProvider()
        {
            // Local provider needs no key even with an empty store.
            bool ok = LlmProviderFactory.TryCreate(
                Settings("ollama"), new FakeSecretStore(), AnyClient(), out var provider, out var failure);

            Assert.True(ok);
            Assert.Equal(AiAvailability.Healthy, failure);
            Assert.IsType<OllamaProvider>(provider);
        }

        [Fact]
        public void OpenRouter_WithKey_BuildsOpenRouterProvider()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("openrouter"), new FakeSecretStore("or-x"), AnyClient(), out var provider, out var failure);

            Assert.True(ok);
            Assert.IsType<OpenRouterProvider>(provider);
        }

        [Fact]
        public void UnknownProvider_ReturnsNoProvider()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("does-not-exist"), new FakeSecretStore("k"), AnyClient(), out var provider, out var failure);

            Assert.False(ok);
            Assert.Null(provider);
            Assert.Equal(AiAvailability.NoProvider, failure);
        }

        [Fact]
        public void CloudProvider_NoKey_ReturnsNoKey()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic"), new FakeSecretStore(), AnyClient(), out var provider, out var failure);

            Assert.False(ok);
            Assert.Null(provider);
            Assert.Equal(AiAvailability.NoKey, failure);
        }

        [Fact]
        public void BaseUrlOverride_ToNonAllowlistedHost_ReturnsNoProvider()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic", baseUrl: "https://evil.example.com"),
                new FakeSecretStore("sk-x"), AnyClient(), out var provider, out var failure);

            Assert.False(ok);
            Assert.Null(provider);
            Assert.Equal(AiAvailability.NoProvider, failure);
        }

        [Fact]
        public void EmptyBaseUrl_UsesCatalogDefault_AndIsAllowed()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic", baseUrl: ""), new FakeSecretStore("sk-x"), AnyClient(), out var provider, out _);

            Assert.True(ok); // catalog default api.anthropic.com is allowlisted
            Assert.IsType<AnthropicProvider>(provider);
        }

        [Fact]
        public void MalformedBaseUrl_ReturnsNoProvider()
        {
            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic", baseUrl: "not a url"), new FakeSecretStore("sk-x"), AnyClient(), out _, out var failure);

            Assert.False(ok);
            Assert.Equal(AiAvailability.NoProvider, failure);
        }

        [Fact]
        public void Ollama_LanBaseUrl_ReturnsHostRestricted()
        {
            // DW-370 (recorded decision): a LAN-hosted Ollama (well-formed URL, non-loopback host) stays REJECTED —
            // the loopback-only policy is kept — but is classified HostRestricted, the state whose message names the
            // restriction, not the misleading NoProvider ("no AI provider is configured").
            bool ok = LlmProviderFactory.TryCreate(
                Settings("ollama", baseUrl: "http://192.168.1.5:11434"),
                new FakeSecretStore(), AnyClient(), out var provider, out var failure);

            Assert.False(ok);
            Assert.Null(provider);
            Assert.Equal(AiAvailability.HostRestricted, failure);
        }

        [Fact]
        public void Ollama_MalformedBaseUrl_ReturnsNoProvider_NotHostRestricted()
        {
            // The base-URL parse failure precedes the allowlist: a garbage ollama base URL is a config error, not a
            // host-policy rejection — HostRestricted is reserved for the loopback-only refusal it names.
            bool ok = LlmProviderFactory.TryCreate(
                Settings("ollama", baseUrl: "not a url"),
                new FakeSecretStore(), AnyClient(), out _, out var failure);

            Assert.False(ok);
            Assert.Equal(AiAvailability.NoProvider, failure);
        }

        [Fact]
        public void RequiresKey_TrueForCloud_FalseForOllama()
        {
            Assert.True(LlmProviderFactory.RequiresKey("anthropic"));
            Assert.True(LlmProviderFactory.RequiresKey("openrouter"));
            Assert.False(LlmProviderFactory.RequiresKey("ollama"));
        }

        // ── No-fallback: a failing selected provider never invokes another provider's adapter ─────

        [Fact]
        public async Task NoFallback_FailingProviderNeverYieldsAnotherSuccess()
        {
            // The selected provider (anthropic) fails with a 500. Assert the built adapter is anthropic's and its
            // failure is surfaced verbatim — there is no path to another provider by construction.
            var stub = StubHttpMessageHandler.Status(System.Net.HttpStatusCode.InternalServerError, "err");
            var http = new HttpClient(stub);

            bool ok = LlmProviderFactory.TryCreate(
                Settings("anthropic"), new FakeSecretStore("sk-x"), http, out var provider, out _);
            Assert.True(ok);
            Assert.Equal("anthropic", provider!.ProviderId);

            NormalizedResult r = await provider.GenerateAsync(new NormalizedRequest("s", "u"), CancellationToken.None);
            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.HttpError, r.Failure);
            Assert.Equal(1, stub.CallCount); // exactly one request — no second, different-provider attempt
        }
    }
}
