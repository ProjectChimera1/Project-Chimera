#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-375 — pins <see cref="LLMService"/>'s ownership lifecycle, which nothing enforced before:
    /// <list type="bullet">
    ///   <item>every generate entry point (all SEVEN flows, not just the trigger/scenario pair the ledger named)
    ///         CANCELS AND DISPOSES the <see cref="CancellationTokenSource"/> it supersedes. The old
    ///         <c>x?.Cancel(); x = new()</c> reassignment abandoned one undisposed source per Generate press;</item>
    ///   <item><see cref="LLMService.Dispose"/> exists at all, releases every token source AND the owned
    ///         <see cref="HttpClient"/>/<see cref="HttpClientHandler"/> pair built on the <c>http: null</c> path
    ///         (Story 8.3 added it and nothing ever released it), and leaves an INJECTED client alone;</item>
    ///   <item>the ordering invariants that make the disposal safe: cancel-before-dispose (so a still-unwinding
    ///         request's token never observes the disposal) and emptied slots (so a teardown-ordering Cancel* cannot
    ///         throw out of <c>CancellationTokenSource.Cancel</c>).</item>
    /// </list>
    /// Godot-free / Tier-1; no network — the stub handler is asserted untouched where it matters.
    /// </summary>
    public class LlmServiceLifecycleTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        private const string ValidTriggerJson =
            "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
            "\"actions\":[{\"type\":\"add_resources\",\"faction\":0,\"amount\":100}]}";

        private static ScenarioContext        TrigCtx()    => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };
        private static MapGeneratorContext    MapCtx()     => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };
        private static BalanceAnalysisContext BalanceCtx() => new() { UnitIds = new[] { "grunt" } };

        private static IEnumerable<LLMService.GenerationFlow> AllFlows()
            => (LLMService.GenerationFlow[])Enum.GetValues(typeof(LLMService.GenerationFlow));

        /// <summary>One "the creator pressed Generate" for <paramref name="flow"/>, with a callback that does nothing —
        /// these tests observe the token-source lifecycle, not the generated payload.</summary>
        private static void Press(LLMService svc, LLMService.GenerationFlow flow)
        {
            switch (flow)
            {
                case LLMService.GenerationFlow.Trigger:
                    svc.GenerateTriggerAsync("x", TrigCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.Scenario:
                    svc.GenerateScenarioAsync("x", MapCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.UnitDraft:
                    svc.GenerateUnitDraftAsync("x", UnitCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.AbilityDraft:
                    svc.GenerateAbilityDraftAsync("x", AbilityCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.HeroDraft:
                    svc.GenerateHeroDraftAsync("x", UnitCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.FactionDraft:
                    svc.GenerateFactionDraftAsync("x", FactionCtx(), (_, __) => { }); break;
                case LLMService.GenerationFlow.BalanceAnalysis:
                    svc.GenerateBalanceAnalysisAsync("x", BalanceCtx(), (_, __) => { }); break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(flow), flow, "unmapped generation flow");
            }
        }

        /// <summary>A service whose provider is deliberately unknown ("nope"), so every press short-circuits inside
        /// <c>LlmProviderFactory.TryCreate</c> with NO network call — the token-source lifecycle runs untouched by
        /// anything else. The stub is still injected so the tests can assert nothing was sent.</summary>
        private static LLMService NoNetworkService(HttpClient client)
            => new(() => Settings("nope"), new FakeSecretStore("sk-x"), client);

        // ── Reassign disposes the superseded source (the leak itself) ─────────

        [Fact]
        public void RepeatedGenerate_CancelsAndDisposesTheSupersededTokenSource_OnEveryFlow()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            using var client = new HttpClient(stub);
            using LLMService svc = NoNetworkService(client);

            foreach (LLMService.GenerationFlow flow in AllFlows())
            {
                Assert.Null(svc.ActiveTokenSource(flow));   // nothing installed before that flow's first press

                Press(svc, flow);
                CancellationTokenSource first = svc.ActiveTokenSource(flow)!;
                Assert.NotNull(first);
                Assert.False(first.IsCancellationRequested);

                Press(svc, flow);
                CancellationTokenSource second = svc.ActiveTokenSource(flow)!;
                Assert.NotNull(second);
                Assert.NotSame(first, second);

                // CANCELLED — and cancelled BEFORE it was disposed, the ordering that keeps a still-unwinding request
                // safe (see ACancelledThenDisposedSource_StaysSafeForAStillUnwindingRequest).
                Assert.True(first.IsCancellationRequested, $"{flow}: the superseded source was not cancelled.");
                // DISPOSED — a disposed source's Token getter throws, which is the only external evidence of disposal.
                // This is exactly what the pre-DW-375 `x?.Cancel(); x = new()` reassignment never did: one leaked
                // CancellationTokenSource per Generate press, for the whole life of the bootstrap singleton.
                Assert.Throws<ObjectDisposedException>(() => { _ = first.Token; });

                // ...while the freshly installed source is live and usable.
                Assert.False(second.IsCancellationRequested);
                _ = second.Token;
            }

            Assert.Equal(0, stub.CallCount);   // the unknown-provider short-circuit really did keep this off the network
        }

        // ── Dispose releases what the service owns ────────────────────────────

        [Fact]
        public void Dispose_DisposesTheOwnedHttpClient()
        {
            // The owned (http: null) construction path is the shipping one — TriggerEditorPhase builds the service that
            // way — and is unreachable from every other Tier-1 test, all of which inject a stub-backed client.
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), http: null);
            HttpClient? owned = svc.OwnedHttpClient;
            Assert.NotNull(owned);

            owned!.Timeout = TimeSpan.FromSeconds(7);   // live before disposal (no request has started)

            svc.Dispose();

            // Story 8.3 gave the service its own HttpClient + AllowAutoRedirect=false HttpClientHandler and nothing ever
            // released either. Disposing the client releases the handler with it (see the companion test below).
            Assert.Throws<ObjectDisposedException>(() => { owned.Timeout = TimeSpan.FromSeconds(7); });
        }

        [Fact]
        public void DisposingAnHttpClient_AlsoDisposesTheHandlerItWasBuiltOver()
        {
            // Pins the framework contract Dispose relies on: `new HttpClient(handler)` takes OWNERSHIP of the handler,
            // so disposing the owned client is sufficient to release the owned HttpClientHandler too — LLMService
            // deliberately keeps no second reference to it.
            var handler = new DisposeTrackingHandler();
            var client  = new HttpClient(handler);
            Assert.False(handler.Disposed);

            client.Dispose();

            Assert.True(handler.Disposed);
        }

        [Fact]
        public async Task Dispose_LeavesAnInjectedHttpClient_Alive()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            using var injected = new HttpClient(stub);
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), injected);

            Assert.Null(svc.OwnedHttpClient);   // the service knows it did not build this one

            svc.Dispose();

            // Still fully usable: the caller that supplied the client owns its lifetime. A "simplification" that always
            // disposed _http would tear a shared/injected client down under its owner.
            injected.Timeout = TimeSpan.FromSeconds(7);
            using HttpResponseMessage resp = await injected.GetAsync("http://localhost/ping");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public void Dispose_CancelsEveryFlow_EmptiesTheSlots_AndLeavesCancelSafe()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            using var client = new HttpClient(stub);
            LLMService svc = NoNetworkService(client);

            foreach (LLMService.GenerationFlow flow in AllFlows()) Press(svc, flow);

            var live = new List<CancellationTokenSource>();
            foreach (LLMService.GenerationFlow flow in AllFlows())
            {
                CancellationTokenSource? source = svc.ActiveTokenSource(flow);
                Assert.NotNull(source);
                live.Add(source!);
            }

            svc.Dispose();

            foreach (CancellationTokenSource source in live)
            {
                // Cancelled first, so an in-flight request unwinds through its own cancellation path rather than into
                // a disposed HttpClient — then disposed.
                Assert.True(source.IsCancellationRequested);
                Assert.Throws<ObjectDisposedException>(() => { _ = source.Token; });
            }
            foreach (LLMService.GenerationFlow flow in AllFlows())
                Assert.Null(svc.ActiveTokenSource(flow));

            // The slots are EMPTIED precisely so a teardown-ordering Cancel* stays safe: CancellationTokenSource.Cancel
            // throws ObjectDisposedException on a disposed source, so leaving the disposed sources in place would turn
            // "cancel then dispose" in a caller's teardown into a crash.
            svc.Cancel();
            svc.CancelScenario();
            svc.CancelDrafts();
            svc.CancelBalanceAnalysis();
            svc.DrainEvents();   // still drains anything already queued
            svc.Dispose();       // idempotent
        }

        [Fact]
        public void Generate_AfterDispose_ThrowsObjectDisposed_OnEveryFlow()
        {
            var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), http: null);
            svc.Dispose();

            // Fail loudly at the call site rather than running against a disposed client and surfacing an opaque
            // "Cannot access a disposed object" through the async error callback a frame later.
            foreach (LLMService.GenerationFlow flow in AllFlows())
                Assert.Throws<ObjectDisposedException>(() => Press(svc, flow));
        }

        // ── The framework premise cancel-before-dispose rests on ──────────────

        [Fact]
        public void ACancelledThenDisposedSource_StaysSafeForAStillUnwindingRequest()
        {
            // Disposing a source whose token an in-flight request still holds is safe ONLY because the source is
            // cancelled first: the token then stays permanently "cancellation requested", and every member such a
            // request touches short-circuits on that state instead of checking disposal. If a future runtime changes
            // this, disposing on reassign becomes unsafe — and this test says so, instead of a mystery
            // ObjectDisposedException appearing in a creator's error toast.
            var cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            cts.Cancel();
            cts.Dispose();

            Assert.True(token.IsCancellationRequested);
            Assert.ThrowsAny<OperationCanceledException>(() => token.ThrowIfCancellationRequested());
            using (token.Register(() => { })) { }                               // HttpClient's cancellation plumbing
            using (CancellationTokenSource.CreateLinkedTokenSource(token)) { }  // LlmHttp's bounded body-read CTS

            // The only three members that DO throw after disposal — none is reachable from an in-flight generation,
            // and LLMService never calls Cancel on a source it has disposed (the slot is emptied instead).
            Assert.Throws<ObjectDisposedException>(() => { _ = cts.Token; });
            Assert.Throws<ObjectDisposedException>(() => cts.Cancel());
            Assert.Throws<ObjectDisposedException>(() => { _ = token.WaitHandle; });
        }

        // ── End-to-end: a superseded press must not break the one that replaced it ──

        [Fact]
        public void SupersedingGenerate_SecondPressStillCompletes_NoDisposedObjectInTheCallback()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidTriggerJson));
            using var client = new HttpClient(stub);
            using var svc = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), client);

            // The real "creator pressed Generate twice" shape: the second press cancels AND disposes the first press's
            // source while that request may still be unwinding through its token.
            svc.GenerateTriggerAsync("first", TrigCtx(), (_, __) => { });

            TriggerDefinition? trigger = null; string? error = null; bool done = false;
            svc.GenerateTriggerAsync("second", TrigCtx(), (t, e) => { trigger = t; error = e; done = true; });
            Pump(svc, () => done);

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private sealed class DisposeTrackingHandler : HttpClientHandler
        {
            public bool Disposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
