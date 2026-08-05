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
    /// Story 8.2 — drives the availability states through the stub handler + fake secret store: NoProvider, NoKey,
    /// Healthy, Unreachable, FailedValidation, (DW-370) HostRestricted, and (DW-589) HostNotAllowlisted. The
    /// synchronous split (config-derived vs round-trip) and the no-fallback contract are exercised here.
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
        public void EvaluateConfig_NonAllowlistedBaseUrl_HostNotAllowlisted()
        {
            // A saved base-URL override to a non-allowlisted host must NOT present as Healthy (the panels enable
            // Generate on Healthy) — the sync state must agree with what the factory / round-trip will actually accept.
            // DW-589 (re-pinned from NoProvider): the refusal is voiced as HostNotAllowlisted, whose microcopy names
            // the pinned hosts, so the creator fixes the base URL rather than the provider picker.
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.HostNotAllowlisted,
                eval.EvaluateConfig(Settings("anthropic", "https://evil.example.com"), new FakeSecretStore("sk-x")));
        }

        [Fact]
        public void EvaluateConfig_CloudPinnedBaseUrl_StillHealthyCandidate()
        {
            // Guard the other arm of DW-589: an explicit override that IS the pinned host remains a Healthy candidate —
            // the new HostNotAllowlisted classification must not leak onto permitted cloud hosts.
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.Healthy,
                eval.EvaluateConfig(Settings("anthropic", "https://api.anthropic.com"), new FakeSecretStore("sk-x")));
        }

        [Fact]
        public void EvaluateConfig_MalformedBaseUrl_NoProvider()
        {
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.NoProvider,
                eval.EvaluateConfig(Settings("anthropic", "not a url"), new FakeSecretStore("sk-x")));
        }

        [Fact]
        public void EvaluateConfig_OllamaLanBaseUrl_HostRestricted()
        {
            // DW-370: the synchronous state every AI panel renders for a LAN-hosted Ollama
            // (http://192.168.1.5:11434) is HostRestricted — whose microcopy names the loopback-only restriction —
            // not the generic NoProvider. The host stays rejected (loopback-only policy kept).
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.HostRestricted,
                eval.EvaluateConfig(Settings("ollama", "http://192.168.1.5:11434"), new FakeSecretStore()));
        }

        [Fact]
        public void EvaluateConfig_OllamaLoopbackBaseUrl_StillHealthyCandidate()
        {
            // Guard the other arm of DW-370: a loopback override remains a Healthy candidate — the new
            // HostRestricted classification must not leak onto permitted loopback hosts.
            var eval = Eval(StubHttpMessageHandler.Ok("{}"));
            Assert.Equal(AiAvailability.Healthy,
                eval.EvaluateConfig(Settings("ollama", "http://127.0.0.1:11434"), new FakeSecretStore()));
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
        public async Task TestConnection_OllamaLanBaseUrl_HostRestricted_NoRequest()
        {
            // DW-370: Test connection against a LAN-hosted Ollama refuses pre-flight with HostRestricted — the
            // restriction-naming state — and never sends a request to the disallowed host.
            var stub = StubHttpMessageHandler.Ok("{}");
            var eval = Eval(stub);
            var state = await eval.TestConnectionAsync(
                Settings("ollama", "http://192.168.1.5:11434"), new FakeSecretStore(), CancellationToken.None);
            Assert.Equal(AiAvailability.HostRestricted, state);
            Assert.Equal(0, stub.CallCount); // rejected before any network call
        }

        [Fact]
        public async Task TestConnection_CloudNonAllowlistedBaseUrl_HostNotAllowlisted_NoRequest()
        {
            // DW-589: Test connection against a cloud provider pointed off the pinned allowlist refuses pre-flight
            // with the allowlist-naming state — and never sends the stored API key to the disallowed host.
            var stub = StubHttpMessageHandler.Ok("{}");
            var eval = Eval(stub);
            var state = await eval.TestConnectionAsync(
                Settings("anthropic", "https://evil.example.com"), new FakeSecretStore("sk-x"), CancellationToken.None);
            Assert.Equal(AiAvailability.HostNotAllowlisted, state);
            Assert.Equal(0, stub.CallCount); // rejected before any network call
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
