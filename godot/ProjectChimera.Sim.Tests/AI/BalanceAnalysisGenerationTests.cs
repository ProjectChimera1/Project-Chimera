#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using ProjectChimera.AI;
using ProjectChimera.AI.Providers;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.5 — proves <c>GenerateBalanceAnalysisAsync</c> is consumed on the Story 8.2/8.3 provider stack with NO
    /// fallback and NO network when unavailable: a configured provider + stub returns a non-null <see cref="BalanceReport"/>
    /// (CallCount==1); a NoProvider/NoKey config short-circuits with the four-state message and NO request (CallCount==0);
    /// a provider failure surfaces the four-state message and makes no second attempt; and a markdown-fenced reply is
    /// stripped before validation. Driven via the existing <c>DrainEvents()</c> pump.
    /// </summary>
    public class BalanceAnalysisGenerationTests
    {
        // A valid suggestions payload targeting two roster ids + tunable fields.
        private const string ValidReportJson =
            "{\"suggestions\":[" +
            "{\"unit_id\":\"grunt\",\"field\":\"attack_damage\",\"current\":10,\"proposed\":14,\"rationale\":\"melee too soft\"}," +
            "{\"unit_id\":\"archer\",\"field\":\"cost_ore\",\"current\":50,\"proposed\":65,\"rationale\":\"too cheap for its range\"}" +
            "]}";

        private static BalanceAnalysisContext Ctx() =>
            new() { UnitIds = new List<string> { "grunt", "archer" } };

        [Fact]
        public void GenerateBalanceAnalysis_ConfiguredProvider_ReturnsReport_CallCount1()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidReportJson));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            BalanceReport? report = null; string? err = null; bool done = false;
            svc.GenerateBalanceAnalysisAsync("melee feels weak", Ctx(), (r, e) => { report = r; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(report);
            Assert.Equal(2, report!.Suggestions.Count);
            Assert.Equal("grunt", report.Suggestions[0].UnitId);
            Assert.Equal("attack_damage", report.Suggestions[0].Field);
            Assert.Equal(14, report.Suggestions[0].Proposed);
            Assert.False(string.IsNullOrEmpty(report.Suggestions[0].Rationale));
            Assert.Equal(1, stub.CallCount);
            Assert.Contains("/v1/messages", stub.LastUri!.AbsoluteUri);
        }

        [Fact]
        public void GenerateBalanceAnalysis_NoKey_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidReportJson));
            var svc = new LLMService(() => Settings("anthropic"), new FakeSecretStore(/* no key */), new HttpClient(stub));

            BalanceReport? report = null; string? err = null; bool done = false;
            svc.GenerateBalanceAnalysisAsync("x", Ctx(), (r, e) => { report = r; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(report);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoKey), err);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateBalanceAnalysis_NoProvider_ShortCircuits_FourState_NoRequest()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidReportJson));
            var svc = new LLMService(() => Settings("nope"), new FakeSecretStore("sk-x"), new HttpClient(stub));

            BalanceReport? report = null; string? err = null; bool done = false;
            svc.GenerateBalanceAnalysisAsync("x", Ctx(), (r, e) => { report = r; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(report);
            Assert.Equal(AiAvailabilityMessages.Describe(AiAvailability.NoProvider), err);
            Assert.Equal(0, stub.CallCount);
        }

        [Fact]
        public void GenerateBalanceAnalysis_ProviderFails_FourState_NoSecondAttempt()
        {
            var stub = StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized, "nope");
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            BalanceReport? report = null; string? err = null; bool done = false;
            svc.GenerateBalanceAnalysisAsync("x", Ctx(), (r, e) => { report = r; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(report);
            Assert.NotNull(err);
            Assert.Equal(1, stub.CallCount);   // the selected provider is authoritative — no second attempt
        }

        [Fact]
        public void GenerateBalanceAnalysis_FencedJson_IsStripped()
        {
            string fenced = "```json\n" + ValidReportJson + "\n```";
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(fenced));
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), new HttpClient(stub));

            BalanceReport? report = null; string? err = null; bool done = false;
            svc.GenerateBalanceAnalysisAsync("x", Ctx(), (r, e) => { report = r; err = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(err);
            Assert.NotNull(report);
            Assert.Equal(2, report!.Suggestions.Count);
        }
    }
}
