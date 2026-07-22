#nullable enable
using System.Net;
using System.Net.Http;
using ProjectChimera.AI;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.4 — proves each <c>Generate{Unit,Ability,Hero,Faction}DraftAsync</c> is consumed on the Story 8.2/8.3
    /// provider stack with NO fallback and NO network when unavailable: a configured provider + stub returns a
    /// validated non-null def (CallCount==1); a NoProvider/NoKey config short-circuits with the four-state message and
    /// NO request (CallCount==0); a provider failure surfaces the four-state message and makes no second attempt; and a
    /// markdown-fenced reply is stripped before validation. Driven via the existing <c>DrainEvents()</c> pump.
    /// </summary>
    public class EntityDraftGenerationTests
    {
        // ── Unit ───────────────────────────────────────────────────────────────

        [Fact]
        public void GenerateUnitDraft_ConfiguredProvider_ReturnsValidatedDraft_CallCount1()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidUnitJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateUnitDraftAsync("a bruiser", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
            Assert.Equal(1, stub.CallCount);
            Assert.Contains("/v1/messages", stub.LastUri!.AbsoluteUri);
        }

        [Fact]
        public void GenerateUnitDraft_NoKey_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidUnitJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateUnitDraftAsync("x", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), err);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateUnitDraft_ProviderFails_FourState_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom");
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateUnitDraftAsync("x", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.FailedValidation), err);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateUnitDraft_FencedJson_IsStripped()
        {
            string fenced = "```json\n" + ValidUnitJson + "\n```";
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(fenced));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateUnitDraftAsync("x", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
        }

        // ── Ability ──────────────────────────────────────────────────────────────

        [Fact]
        public void GenerateAbilityDraft_ConfiguredProvider_ReturnsValidatedDraft_CallCount1()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidAbilityJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            AbilityDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateAbilityDraftAsync("a heal", AbilityCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
            // Numbers materialized as Fixed at parse (ContentJson.Options / FixedJsonConverter).
            Assert.Equal(Fixed.FromFloat(3f).Raw, def!.Cooldown.Raw);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateAbilityDraft_NoProvider_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidAbilityJson));
            var svc = new LLMService(() => Settings("nope"), new FakeSecretStore("sk-x"), new HttpClient(stub));

            AbilityDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateAbilityDraftAsync("x", AbilityCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoProvider), err);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateAbilityDraft_ProviderUnreachable_FourState_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Unreachable();
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            AbilityDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateAbilityDraftAsync("x", AbilityCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.Unreachable), err);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateAbilityDraft_FencedJson_IsStripped()
        {
            string fenced = "```json\n" + ValidAbilityJson + "\n```";
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(fenced));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            AbilityDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateAbilityDraftAsync("x", AbilityCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
        }

        // ── Hero ───────────────────────────────────────────────────────────────

        [Fact]
        public void GenerateHeroDraft_ConfiguredProvider_ReturnsHeroUnit_CallCount1()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidHeroJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateHeroDraftAsync("a champion", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
            Assert.True(def!.IsHero);
            Assert.NotNull(def.Hero);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateHeroDraft_NoKey_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidHeroJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            UnitDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateHeroDraftAsync("x", UnitCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), err);
            Assert.Equal(0, stub.CallCount);
        }

        // ── Faction ──────────────────────────────────────────────────────────────

        [Fact]
        public void GenerateFactionDraft_ConfiguredProvider_ReturnsValidatedDraft_CallCount1()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidFactionJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            FactionDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateFactionDraftAsync("fire folk", FactionCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
            Assert.Equal(2, def!.Units.Count);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateFactionDraft_NoKey_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidFactionJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            FactionDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateFactionDraftAsync("x", FactionCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), err);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateFactionDraft_ProviderFails_FourState_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized, "nope");
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            FactionDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateFactionDraftAsync("x", FactionCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(def);
            Assert.NotNull(err);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public void GenerateFactionDraft_FencedJson_IsStripped()
        {
            string fenced = "```json\n" + ValidFactionJson + "\n```";
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(fenced));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            FactionDefinition? def = null; string? err = null; bool done = false;
            svc.GenerateFactionDraftAsync("x", FactionCtx(), (d, e) => { def = d; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(def);
        }
    }
}
