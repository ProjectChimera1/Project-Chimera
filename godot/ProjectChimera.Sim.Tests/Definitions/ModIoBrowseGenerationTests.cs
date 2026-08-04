#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-428 — Tier-1 proof that mod.io browse carries an incrementing request-generation token so a stale/late
    /// response can never repopulate the online list out of issue order. Drives the REAL
    /// <see cref="ModIoService.BrowseModsAsync"/> pipeline (Task.Run → HttpClient → completion enqueue →
    /// <see cref="ModIoService.DrainEvents"/>) through a scripted <see cref="HttpMessageHandler"/> whose response
    /// ordering the test controls, and synchronizes deterministically by awaiting the returned request task —
    /// when it completes, its completion action is guaranteed to be in the drain queue.
    ///
    /// Without the generation gate, <see cref="StaleResponseArrivingAfterTheNewerOnesIsDropped"/> and
    /// <see cref="EarlyResponseOfASupersededBrowseIsDropped"/> fail: whatever arrives last (or first) wins in
    /// arrival order, rendering a mod set that no longer matches the search/sort/tag controls.
    /// </summary>
    public class ModIoBrowseGenerationTests
    {
        // ── Scripted HTTP handler: every request parks on a TCS the test completes on demand ──────────────

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly object _gate = new();
            private readonly List<(string Url, TaskCompletionSource<HttpResponseMessage> Tcs)> _requests = new();

            public int RequestCount { get { lock (_gate) return _requests.Count; } }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<HttpResponseMessage>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _requests.Add((request.RequestUri!.ToString(), tcs));
                return tcs.Task;
            }

            /// <summary>Complete the first parked request whose URL contains <paramref name="urlSubstring"/>.</summary>
            public void Complete(string urlSubstring, HttpResponseMessage response)
            {
                TaskCompletionSource<HttpResponseMessage>? tcs = null;
                lock (_gate)
                {
                    foreach (var (url, t) in _requests)
                        if (url.Contains(urlSubstring)) { tcs = t; break; }
                }
                if (tcs == null)
                    throw new InvalidOperationException($"No parked request matching '{urlSubstring}'.");
                tcs.SetResult(response);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

        private static HttpResponseMessage ModsResponse(int id, string name)
        {
            string json = "{\"data\":[{\"id\":" + id + ",\"name\":\"" + name + "\"}]," +
                          "\"result_count\":1,\"result_total\":1,\"result_offset\":0,\"result_limit\":20}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage ErrorResponse(string message)
        {
            string json = "{\"error\":{\"code\":500,\"message\":\"" + message + "\"}}";
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Wait (bounded) until <paramref name="count"/> HTTP requests reached the handler.</summary>
        private static async Task WaitForRequestsAsync(ScriptedHandler handler, int count)
        {
            for (int i = 0; i < 1500 && handler.RequestCount < count; i++)
                await Task.Delay(10);
            Assert.True(handler.RequestCount >= count,
                $"Timed out waiting for {count} HTTP request(s); saw {handler.RequestCount}.");
        }

        /// <summary>Await a browse request task with a bound so a regression can never hang the suite.</summary>
        private static async Task AwaitOrFail(Task task)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(winner == task, "The browse request task did not complete in time.");
            await task; // propagate any fault
        }

        private static (ModIoService Svc, ScriptedHandler Handler,
                        List<List<ModIoMod>> Delivered, List<(string Op, string Msg)> Errors) MakeService()
        {
            var handler = new ScriptedHandler();
            var svc     = new ModIoService(gameId: 1, apiKey: "K", httpHandler: handler);
            var delivered = new List<List<ModIoMod>>();
            var errors    = new List<(string, string)>();
            svc.OnBrowseComplete += mods => delivered.Add(mods);
            svc.OnError          += (op, msg) => errors.Add((op, msg));
            return (svc, handler, delivered, errors);
        }

        // ── The DW-428 defect: stale response arriving AFTER the newer one must be dropped ────────────────

        [Fact]
        public async Task StaleResponseArrivingAfterTheNewerOnesIsDropped()
        {
            var (svc, handler, delivered, errors) = MakeService();

            Task taskA = svc.BrowseModsAsync(searchQuery: "alpha"); // generation 1 — superseded below
            Task taskB = svc.BrowseModsAsync(searchQuery: "beta");  // generation 2 — the latest
            await WaitForRequestsAsync(handler, 2);

            // The NEWER query's response arrives first and renders.
            handler.Complete("_q=beta", ModsResponse(201, "Beta Map"));
            await AwaitOrFail(taskB);
            svc.DrainEvents();
            Assert.Single(delivered);
            Assert.Equal("Beta Map", delivered[0][0].Name);

            // The SUPERSEDED query's response arrives late. Pre-DW-428 it repopulated the list in arrival
            // order (stale mod set under newer controls); the generation gate must drop it.
            handler.Complete("_q=alpha", ModsResponse(101, "Alpha Map"));
            await AwaitOrFail(taskA);
            svc.DrainEvents();

            Assert.Single(delivered); // still only Beta — the stale set never fired OnBrowseComplete
            Assert.Empty(errors);
        }

        // ── Arrival-order variant: the superseded browse completes FIRST and must still be dropped ────────

        [Fact]
        public async Task EarlyResponseOfASupersededBrowseIsDropped()
        {
            var (svc, handler, delivered, _) = MakeService();

            Task taskA = svc.BrowseModsAsync(searchQuery: "alpha"); // superseded the moment B is issued
            Task taskB = svc.BrowseModsAsync(searchQuery: "beta");
            await WaitForRequestsAsync(handler, 2);

            handler.Complete("_q=alpha", ModsResponse(101, "Alpha Map"));
            await AwaitOrFail(taskA);
            svc.DrainEvents();
            Assert.Empty(delivered); // not the latest generation — dropped even though it arrived first

            handler.Complete("_q=beta", ModsResponse(201, "Beta Map"));
            await AwaitOrFail(taskB);
            svc.DrainEvents();
            Assert.Single(delivered); // the pipeline is not wedged: the newest browse still delivers
            Assert.Equal("Beta Map", delivered[0][0].Name);
        }

        // ── Guard against over-gating: the plain single-browse path must be untouched ─────────────────────

        [Fact]
        public async Task SingleBrowseStillDelivers()
        {
            var (svc, handler, delivered, errors) = MakeService();

            Task task = svc.BrowseModsAsync(searchQuery: "solo");
            await WaitForRequestsAsync(handler, 1);
            handler.Complete("_q=solo", ModsResponse(7, "Solo Map"));
            await AwaitOrFail(task);
            svc.DrainEvents();

            Assert.Single(delivered);
            Assert.Equal("Solo Map", delivered[0][0].Name);
            Assert.Empty(errors);
        }

        // ── Error path: a stale browse error is dropped; the latest browse's error still surfaces ─────────

        [Fact]
        public async Task StaleBrowseErrorIsDroppedButLatestBrowseErrorIsDelivered()
        {
            var (svc, handler, delivered, errors) = MakeService();

            Task taskA = svc.BrowseModsAsync(searchQuery: "alpha"); // will fail late — superseded
            Task taskB = svc.BrowseModsAsync(searchQuery: "beta");
            await WaitForRequestsAsync(handler, 2);

            handler.Complete("_q=beta", ModsResponse(201, "Beta Map"));
            await AwaitOrFail(taskB);
            svc.DrainEvents();
            Assert.Single(delivered);

            // The stale browse's HTTP 500 must not clobber the newer result's status.
            handler.Complete("_q=alpha", ErrorResponse("stale boom"));
            await AwaitOrFail(taskA);
            svc.DrainEvents();
            Assert.Empty(errors);

            // But a FRESH browse that fails still surfaces its error — the gate drops only superseded work.
            Task taskC = svc.BrowseModsAsync(searchQuery: "gamma");
            await WaitForRequestsAsync(handler, 3);
            handler.Complete("_q=gamma", ErrorResponse("fresh boom"));
            await AwaitOrFail(taskC);
            svc.DrainEvents();

            Assert.Single(errors);
            Assert.Equal("browse", errors[0].Op);
            Assert.Equal("fresh boom", errors[0].Msg);
        }

        // ── Seam sanity: IsLatestBrowse tracks issue order, not completion order ──────────────────────────

        [Fact]
        public async Task IsLatestBrowse_TracksIssueOrder()
        {
            var (svc, handler, _, _) = MakeService();

            Task t1 = svc.BrowseModsAsync(searchQuery: "one"); // generation 1
            Assert.True(svc.IsLatestBrowse(1));

            Task t2 = svc.BrowseModsAsync(searchQuery: "two"); // generation 2 supersedes 1
            Assert.False(svc.IsLatestBrowse(1));
            Assert.True(svc.IsLatestBrowse(2));

            // Tidy up the parked requests so no background task outlives the test.
            await WaitForRequestsAsync(handler, 2);
            handler.Complete("_q=one", ModsResponse(1, "One"));
            handler.Complete("_q=two", ModsResponse(2, "Two"));
            await AwaitOrFail(t1);
            await AwaitOrFail(t2);
        }
    }
}
