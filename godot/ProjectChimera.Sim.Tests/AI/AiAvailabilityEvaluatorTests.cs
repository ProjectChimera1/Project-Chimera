#nullable enable
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — drives all five states through the stub handler + fake secret store: NoProvider, NoKey, Healthy,
    /// Unreachable, FailedValidation. The synchronous split (config-derived vs round-trip) and the no-fallback
    /// contract are exercised here.
    /// </summary>
    public class AiAvailabilityEvaluatorTests
    {
        private static SettingsData Settings(string provider, string baseUrl = "")
            => new() { LlmProvider = provider, LlmModel = "m", LlmBaseUrl = baseUrl };

        private static AiAvailabilityEvaluator Eval(StubHttpMessageHandler stub) => new(new HttpClient(stub));

        // ── EvaluateConfig (synchronous, no network) ──────────────────────────────

        [Fact]
        public void EvaluateConfig_UnknownProvider_NoProvider()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.NoProvider,
                eval.EvaluateConfig(Settings("nope"), new FakeSecretStore()));
        }

        [Fact]
        public void EvaluateConfig_CloudNoKey_NoKey()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.NoKey,
                eval.EvaluateConfig(Settings("anthropic"), new FakeSecretStore()));
        }

        [Fact]
        public void EvaluateConfig_OllamaNoKey_HealthyCandidate()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.Healthy,
                eval.EvaluateConfig(Settings("ollama"), new FakeSecretStore()));
        }

        [Fact]
        public void EvaluateConfig_CloudWithKey_HealthyCandidate()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.Healthy,
                eval.EvaluateConfig(Settings("anthropic"), new FakeSecretStore("sk-x")));
        }

        [Fact]
        public void EvaluateConfig_NonAllowlistedBaseUrl_NoProvider()
        {
            // A saved base-URL override to a non-allowlisted host must NOT present as Healthy (the panels enable
            // Generate on Healthy) — the sync state must agree with what the factory / round-trip will actually accept.
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.NoProvider,
                eval.EvaluateConfig(Settings("anthropic", "https://evil.example.com"), new FakeSecretStore("sk-x")));
        }

        [Fact]
        public void EvaluateConfig_MalformedBaseUrl_NoProvider()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.NoProvider,
                eval.EvaluateConfig(Settings("anthropic", "not a url"), new FakeSecretStore("sk-x")));
        }

        // ── TestConnectionAsync (round-trip) ──────────────────────────────────────

        [Fact]
        public async Task TestConnection_NoProvider_Synchronous()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            var state = await eval.TestConnectionAsync(Settings("nope"), new FakeSecretStore(), CancellationToken.None);
            Assert.Equal(AiAvailability.NoProvider, state);
        }

        [Fact]
        public async Task TestConnection_NoKey_Synchronous()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            var state = await eval.TestConnectionAsync(Settings("anthropic"), new FakeSecretStore(), CancellationToken.None);
            Assert.Equal(AiAvailability.NoKey, state);
        }

        [Fact]
        public async Task TestConnection_ReachableParseable_Healthy()
        {
            var stub = StubHttpMessageHandler.Ok("{\"content\":[{\"type\":\"text\",\"text\":\"pong\"}]}");
            var eval = Eval(stub);
            var state = await eval.TestConnectionAsync(Settings("anthropic"), new FakeSecretStore("sk-x"), CancellationToken.None);
            Assert.Equal(AiAvailability.Healthy, state);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public async Task TestConnection_UnreachableHost_Unreachable()
        {
            var eval = Eval(StubHttpMessageHandler.Unreachable());
            var state = await eval.TestConnectionAsync(Settings("anthropic"), new FakeSecretStore("sk-x"), CancellationToken.None);
            Assert.Equal(AiAvailability.Unreachable, state);
        }

        [Fact]
        public async Task TestConnection_ReturnedButUnparseable_FailedValidation()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("not json at all"));
            var state = await eval.TestConnectionAsync(Settings("anthropic"), new FakeSecretStore("sk-x"), CancellationToken.None);
            Assert.Equal(AiAvailability.FailedValidation, state);
        }

        [Fact]
        public async Task TestConnection_HttpError_FailedValidation()
        {
            var eval = Eval(StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized, "bad key"));
            var state = await eval.TestConnectionAsync(Settings("anthropic"), new FakeSecretStore("sk-x"), CancellationToken.None);
            Assert.Equal(AiAvailability.FailedValidation, state);
        }
    }
}
