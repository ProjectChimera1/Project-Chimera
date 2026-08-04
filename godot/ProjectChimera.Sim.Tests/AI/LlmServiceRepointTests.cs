#nullable enable
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using ProjectChimera.AI;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.3 — proves both generate methods are repointed onto the Story 8.2 <see cref="ILLMProvider"/> stack:
    /// the configured provider's adapter is used against a stub; a failing provider yields NO second attempt (the
    /// Claude→Ollama fallback is gone); and a synchronous-unavailable case (NoProvider/NoKey) short-circuits with the
    /// four-state message and NO network request. The async path is driven via the existing <c>DrainEvents()</c> pump.
    /// </summary>
    public class LlmServiceRepointTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        private static SettingsData Settings(string provider = "anthropic", string baseUrl = "")
            => new() { LlmProvider = provider, LlmModel = "m", LlmBaseUrl = baseUrl };

        /// <summary>Wrap raw model text in an Anthropic Messages-API response body (the shape AnthropicProvider parses).</summary>
        private static string AnthropicBody(string text)
            => JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } } });

        private const string ValidTriggerJson =
            "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
            "\"actions\":[{\"type\":\"add_resources\",\"faction\":0,\"amount\":100}]}";

        private const string ValidScenarioJson =
            "{\"player_slots\":[" +
            "{\"slot\":0,\"faction_json\":\"a\",\"base_x\":-45,\"base_z\":0}," +
            "{\"slot\":1,\"faction_json\":\"b\",\"base_x\":45,\"base_z\":0}]," +
            "\"resource_nodes\":[{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}]," +
            "\"buildings\":[{\"type\":\"CommandCenter\",\"slot\":0,\"x\":-45,\"z\":0},{\"type\":\"CommandCenter\",\"slot\":1,\"x\":45,\"z\":0}]," +
            "\"units\":[{\"unit_id\":\"worker\",\"slot\":0,\"x\":-42,\"z\":3}]}";

        private static ScenarioContext TrigCtx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };
        private static MapGeneratorContext MapCtx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>Drive a trigger generation to completion via the DrainEvents pump (bounded wait).</summary>
        private static (TriggerDefinition? trigger, string? error) RunTrigger(LLMService svc)
        {
            TriggerDefinition? t = null; string? e = null; bool done = false;
            svc.GenerateTriggerAsync("do a thing", TrigCtx(), (tr, er) => { t = tr; e = er; done = true; });
            Pump(svc, () => done);
            return (t, e);
        }

        private static (ScenarioData? scenario, string? error) RunScenario(LLMService svc)
        {
            ScenarioData? s = null; string? e = null; bool done = false;
            svc.GenerateScenarioAsync("a map", MapCtx(), (sc, er) => { s = sc; e = er; done = true; });
            Pump(svc, () => done);
            return (s, e);
        }

        private static void Pump(LLMService svc, Func<bool> done)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < 5000)
            {
                svc.DrainEvents();
                Thread.Sleep(5);
            }
            svc.DrainEvents();
            Assert.True(done(), "generation callback did not fire within the timeout.");
        }

        // ── Repoint happy path ────────────────────────────────────────────────

        [Fact]
        public void GenerateTrigger_ConfiguredProvider_UsesAdapter_ReturnsValidatedDraft()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(error);
            Assert.NotNull(trigger);
            Assert.Equal(1, stub.CallCount);
            // The configured (anthropic) adapter posted to the anthropic Messages path.
            Assert.Contains("/v1/messages", stub.LastUri!.AbsoluteUri);
        }

        [Fact]
        public void GenerateScenario_ConfiguredProvider_UsesAdapter_ReturnsValidatedDraft()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidScenarioJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (scenario, error) = RunScenario(svc);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Equal(1, stub.CallCount);
        }

        // ── No fallback ───────────────────────────────────────────────────────

        [Fact]
        public void GenerateTrigger_ProviderFails_SurfacesError_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom");
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(trigger);
            // A reached-but-unhealthy answer (500 → HttpError) is voiced with the shared four-state microcopy, matching
            // Test-connection — not a raw adapter string (Story 8.3 review patch).
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.FailedValidation), error);
            // Exactly ONE adapter invocation — the old Claude→Ollama fallback would have made a second call.
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateScenario_ProviderThrows_SurfacesError_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Unreachable();
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (scenario, error) = RunScenario(svc);

            Assert.Null(scenario);
            // A network failure (Unreachable) is voiced with the shared four-state microcopy (Story 8.3 review patch).
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.Unreachable), error);
            Assert.Equal(1, stub.CallCount);
        }

        // ── Unavailable (config-derived, synchronous, no network) ─────────────

        [Fact]
        public void GenerateTrigger_NoKey_ShortCircuits_FourStateMessage_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(trigger);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), error);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateTrigger_NoProvider_ShortCircuits_FourStateMessage_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            var svc = new LLMService(() => Settings("nope"), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(trigger);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoProvider), error);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateTrigger_OllamaLanHost_ShortCircuits_HostRestrictedMessage_NoRequest()
        {
            // DW-370 (recorded decision): a LAN-hosted Ollama config short-circuits the generate path with the
            // HostRestricted message — which NAMES the loopback-only restriction — instead of the misleading
            // "no AI provider is configured", and still sends nothing to the disallowed host.
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            var svc = new LLMService(
                () => Settings("ollama", baseUrl: "http://192.168.1.5:11434"),
                new FakeSecretStore(), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(trigger);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.HostRestricted), error);
            Assert.Contains("loopback", error!, StringComparison.OrdinalIgnoreCase); // the restriction is named
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateScenario_NoKey_ShortCircuits_FourStateMessage_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidScenarioJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            var (scenario, error) = RunScenario(svc);

            Assert.Null(scenario);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), error);
            Assert.Equal(0, stub.CallCount);
        }

        // ── Key is read only via ISecretStore ─────────────────────────────────

        [Fact]
        public void GenerateTrigger_KeyFlowsFromSecretStore_IntoAdapterHeader()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-secret-123"), new HttpClient(stub));

            var (_, error) = RunTrigger(svc);

            Assert.Null(error);
            // The anthropic adapter sends the key it read from the secret store as x-api-key — proving the key source.
            Assert.True(stub.LastHeaders.TryGetValue("x-api-key", out string? sent));
            Assert.Equal("sk-secret-123", sent);
        }

        // ── Owned-client redirect hardening (Story 8.3 review patch) ──────────

        [Fact]
        public void OwnedHttpHandler_RefusesRedirects()
        {
            // A real key flows through the owned client via the provider adapters; .NET does NOT strip a custom
            // x-api-key header on a cross-host redirect, so the owned handler MUST refuse redirects. Every other test
            // injects an explicit HttpClient, so this is the only coverage of the owned (http: null) construction path.
            Assert.False(LLMService.BuildOwnedHttpHandler().AllowAutoRedirect);
        }

        // ── Markdown-fenced provider replies are stripped before validation ──

        [Fact]
        public void GenerateTrigger_FencedJsonResponse_IsStrippedAndValidated()
        {
            // Real models routinely wrap JSON in a ```json … ``` fence; StripMarkdown must remove it on the repointed
            // path so the fenced body still validates (a dropped StripMarkdown would fail validation as "Invalid JSON").
            string fenced = "```json\n" + ValidTriggerJson + "\n```";
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(fenced));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            var (trigger, error) = RunTrigger(svc);

            Assert.Null(error);
            Assert.NotNull(trigger);
        }
    }
}
