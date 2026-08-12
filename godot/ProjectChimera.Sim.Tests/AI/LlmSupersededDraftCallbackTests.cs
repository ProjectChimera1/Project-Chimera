#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using Xunit;
using static ProjectChimera.Sim.Tests.AI.EntityDraftTestData;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-381 — the per-kind in-flight cancellation apparatus (<c>_unitCts</c> / <c>_abilityCts</c> / <c>_heroCts</c> /
    /// <c>_factionCts</c> / <c>_balanceCts</c>) was CANCELLED on a new request but nothing checked the token before
    /// the callback was queued. <c>RunDraftAsync</c> only swallowed <see cref="OperationCanceledException"/>; a
    /// provider that returned NORMALLY a moment after the cancel still enqueued <c>onComplete</c>, so a superseded
    /// draft or balance run repainted the editor's rows/forms over the newer run that replaced it — the classic
    /// stale-response-wins race. DW-375 closed the other half of the entry (the superseded source is now cancelled
    /// AND disposed); this file pins the callback half.
    ///
    /// <para>The race is made DETERMINISTIC rather than slept on: the stub response body is delivered through a stream
    /// that signals once it has been fully drained, and the cancellation is issued from that signal. The provider call
    /// therefore always completes successfully <i>after</i> the token was cancelled — exactly the window the ledger
    /// entry describes, with no timing assumption anywhere.</para>
    ///
    /// Godot-free / Tier-1; no live network.
    /// </summary>
    public class LlmSupersededDraftCallbackTests
    {
        /// <summary>Drain the main-thread queue for a fixed window. Unlike <c>EntityDraftTestData.Pump</c> this must
        /// NOT assert a callback arrived — the whole point is that a superseded run delivers none, so the window has
        /// to close on its own.</summary>
        private static void PumpQuiet(LLMService svc, Func<bool> stopEarly, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms && !stopEarly())
            {
                svc.DrainEvents();
                Thread.Sleep(5);
            }
            svc.DrainEvents();
        }

        // ── The headline: a cancelled run's callback never reaches the editor ──

        [Fact]
        public void ADraftCancelledAfterTheProviderReturned_NeverEnqueuesItsCallback()
        {
            LLMService? svc = null;
            var handler = new BodyDrainSignallingHandler(
                AnthropicBody(ValidUnitJson), onFirstBodyDrained: () => svc!.CancelDrafts());
            using var client  = new HttpClient(handler);
            using var service = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), client);
            svc = service;

            int callbacks = 0;
            service.GenerateUnitDraftAsync("x", UnitCtx(), (_, __) => Interlocked.Increment(ref callbacks));

            PumpQuiet(service, () => Volatile.Read(ref callbacks) > 0);

            Assert.Equal(1, handler.CallCount);                  // the provider really did run and return 200 OK
            Assert.True(handler.FirstBodyDrained);               // …and really did deliver its whole body
            Assert.Equal(0, Volatile.Read(ref callbacks));       // …yet the superseded result never repainted anything
        }

        /// <summary>The same guard on the BALANCE flow, which is the one Story 8.5 added and the ledger entry names
        /// first — a superseded analysis must not repaint suggestion rows over a newer report.</summary>
        [Fact]
        public void ACancelledBalanceAnalysis_NeverEnqueuesItsCallback()
        {
            const string Report = "{\"suggestions\":[{\"unit_id\":\"grunt\",\"field\":\"hp\",\"current\":100,\"proposed\":120,\"rationale\":\"r\"}]}";

            LLMService? svc = null;
            var handler = new BodyDrainSignallingHandler(
                AnthropicBody(Report), onFirstBodyDrained: () => svc!.CancelBalanceAnalysis());
            using var client  = new HttpClient(handler);
            using var service = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), client);
            svc = service;

            int callbacks = 0;
            service.GenerateBalanceAnalysisAsync("x", new BalanceAnalysisContext { UnitIds = new[] { "grunt" } },
                (_, __) => Interlocked.Increment(ref callbacks));

            PumpQuiet(service, () => Volatile.Read(ref callbacks) > 0);

            Assert.Equal(1, handler.CallCount);
            Assert.Equal(0, Volatile.Read(ref callbacks));
        }

        // ── The real shape: press Generate twice, only the SECOND result lands ──

        [Fact]
        public void SupersedingPress_OnlyTheNewerRunsResultReachesTheCallback()
        {
            LLMService? svc = null;
            var stale = new UnitDraftJson("stale_unit");
            var fresh = new UnitDraftJson("fresh_unit");

            var seen = new List<string?>();
            void OnComplete(UnitDefinition? def, string? error)
            {
                lock (seen) seen.Add(def?.Id ?? $"ERROR:{error}");
            }

            // The first request's body signals once drained; that signal issues the SECOND press, whose
            // ReplaceTokenSource cancels (and disposes) the first press's source while it is still unwinding.
            var handler = new BodyDrainSignallingHandler(
                new[] { AnthropicBody(stale.Json), AnthropicBody(fresh.Json) },
                onFirstBodyDrained: () => svc!.GenerateUnitDraftAsync("second", UnitCtx(), OnComplete));

            using var client  = new HttpClient(handler);
            using var service = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), client);
            svc = service;

            service.GenerateUnitDraftAsync("first", UnitCtx(), OnComplete);

            PumpQuiet(service, () => { lock (seen) return seen.Count > 0; });
            PumpQuiet(service, () => false, ms: 300);   // give any straggler (the superseded one) a chance to land

            lock (seen)
            {
                Assert.Equal(2, handler.CallCount);                     // both presses really did reach the provider
                Assert.Single(seen);                                    // …but the superseded one was dropped
                Assert.Equal("fresh_unit", seen[0]);                    // …and the newer run is what the editor sees
            }
        }

        // ── A run that is NOT cancelled still completes — the guard must not eat the normal path ──

        [Fact]
        public void AnUncancelledDraft_StillDeliversItsCallback()
        {
            var stub = StubHttpMessageHandler.Ok(AnthropicBody(ValidUnitJson));
            using var client  = new HttpClient(stub);
            using var service = new LLMService(() => Settings(), new FakeSecretStore("sk-x"), client);

            UnitDefinition? def = null; string? error = null; bool done = false;
            service.GenerateUnitDraftAsync("x", UnitCtx(), (d, e) => { def = d; error = e; done = true; });
            Pump(service, () => done);

            Assert.Null(error);
            Assert.NotNull(def);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>A valid unit-draft body under a chosen id, so the two presses in the superseding test are
        /// distinguishable by the value that reaches the callback.</summary>
        private sealed class UnitDraftJson
        {
            public UnitDraftJson(string id) => Json =
                $"{{\"id\":\"{id}\",\"display_name\":\"U\",\"category\":\"Melee\"," +
                "\"hp\":120,\"speed\":4,\"attack_damage\":12,\"attack_range\":1.5,\"attack_speed\":1," +
                "\"cost_ore\":60,\"supply\":2}";

            public string Json { get; }
        }

        /// <summary>
        /// Returns 200 OK bodies in order, and fires <c>onFirstBodyDrained</c> at the exact moment the FIRST response
        /// body has been read to completion — i.e. after the provider has certainly succeeded and before
        /// <c>RunDraftAsync</c> reaches its enqueue. That is what makes this a deterministic reproduction of the
        /// "provider returned normally just after the cancel" window instead of a sleep-and-hope race.
        /// </summary>
        private sealed class BodyDrainSignallingHandler : HttpMessageHandler
        {
            private readonly string[] _bodies;
            private readonly Action   _onFirstBodyDrained;
            private int _calls;
            private int _drained;

            public BodyDrainSignallingHandler(string body, Action onFirstBodyDrained)
                : this(new[] { body }, onFirstBodyDrained) { }

            public BodyDrainSignallingHandler(string[] bodies, Action onFirstBodyDrained)
            {
                _bodies             = bodies;
                _onFirstBodyDrained = onFirstBodyDrained;
            }

            public int  CallCount        => Volatile.Read(ref _calls);
            public bool FirstBodyDrained => Volatile.Read(ref _drained) != 0;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int n = Interlocked.Increment(ref _calls);
                byte[] bytes = Encoding.UTF8.GetBytes(_bodies[Math.Min(n - 1, _bodies.Length - 1)]);

                Action? signal = n != 1 ? null : () =>
                {
                    Volatile.Write(ref _drained, 1);
                    _onFirstBodyDrained();
                };

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new DrainSignallingStream(bytes, signal)) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return Task.FromResult(response);
            }
        }

        /// <summary>A read-only stream over a fixed buffer that invokes <c>onDrained</c> on the read that reports
        /// end-of-stream — the last observable instant of a SUCCESSFUL provider round-trip. Deliberately overrides the
        /// array <c>ReadAsync</c> overload <c>LlmHttp.ReadBoundedAsync</c> calls, and never throws, so the read
        /// completes normally even though the token is cancelled inside the signal.</summary>
        private sealed class DrainSignallingStream : Stream
        {
            private readonly byte[] _bytes;
            private readonly Action? _onDrained;
            private int _pos;
            private bool _signalled;

            public DrainSignallingStream(byte[] bytes, Action? onDrained)
            {
                _bytes     = bytes;
                _onDrained = onDrained;
            }

            public override bool CanRead  => true;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => _bytes.Length;
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = Math.Min(count, _bytes.Length - _pos);
                if (n > 0)
                {
                    Array.Copy(_bytes, _pos, buffer, offset, n);
                    _pos += n;
                    return n;
                }
                if (!_signalled)
                {
                    _signalled = true;
                    _onDrained?.Invoke();   // the body is fully delivered — cancel HERE, before the enqueue
                }
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => Task.FromResult(Read(buffer, offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                byte[] tmp = new byte[buffer.Length];
                int n = Read(tmp, 0, tmp.Length);
                new ReadOnlySpan<byte>(tmp, 0, n).CopyTo(buffer.Span);
                return new ValueTask<int>(n);
            }
        }
    }
}
