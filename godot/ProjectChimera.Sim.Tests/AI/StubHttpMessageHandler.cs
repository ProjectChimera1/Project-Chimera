#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — a test-local <see cref="HttpMessageHandler"/> that records the outgoing
    /// <see cref="HttpRequestMessage"/> (URL, headers, body) and returns a canned response — no live network, fully
    /// deterministic. The adapter test seam described in the spec Design Notes.
    /// </summary>
    public sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

        /// <summary>The last request URI seen.</summary>
        public Uri? LastUri { get; private set; }

        /// <summary>The last request body (UTF-8 decoded).</summary>
        public string LastBody { get; private set; } = "";

        /// <summary>The last request headers (name → first value), merged from request + content headers.</summary>
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many times the handler was invoked (the no-fallback tests assert this is exactly 1).</summary>
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
            => _responder = responder;

        // ── Convenience constructors ───────────────────────────────────────────

        /// <summary>Always return a 200 with the given JSON body.</summary>
        public static StubHttpMessageHandler Ok(string jsonBody)
            => new((_, __) => Json(HttpStatusCode.OK, jsonBody));

        /// <summary>Always return the given status with the given body.</summary>
        public static StubHttpMessageHandler Status(HttpStatusCode code, string body = "")
            => new((_, __) => Json(code, body));

        /// <summary>Always throw an <see cref="HttpRequestException"/> (simulates an unreachable host).</summary>
        public static StubHttpMessageHandler Unreachable()
            => new((_, __) => throw new HttpRequestException("simulated connection failure"));

        /// <summary>Throw a <see cref="TaskCanceledException"/> with NO caller token attached — simulates the client
        /// timeout firing (the caller's ct is NOT signalled), which must map to Unreachable, not propagate.</summary>
        public static StubHttpMessageHandler ClientTimeout()
            => new((_, __) => throw new TaskCanceledException("simulated client timeout"));

        /// <summary>Return a 200 whose body stream throws mid-read — simulates a connection reset after headers.</summary>
        public static StubHttpMessageHandler MidStreamFailure()
            => new((_, __) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingHttpContent() });

        public static HttpResponseMessage Json(HttpStatusCode code, string body)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        // ── Handler ────────────────────────────────────────────────────────────

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastUri = request.RequestUri;
            LastBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "";

            LastHeaders.Clear();
            foreach (var h in request.Headers)
                foreach (var v in h.Value) { LastHeaders[h.Key] = v; break; }
            if (request.Content != null)
                foreach (var h in request.Content.Headers)
                    foreach (var v in h.Value) { LastHeaders[h.Key] = v; break; }

            return _responder(request, LastBody);
        }
    }

    /// <summary>Story 8.2 — an <see cref="HttpContent"/> whose body read throws, so a mid-stream connection failure
    /// (after headers were received) can be simulated. The <c>LlmHttp</c> body-read guard must map this to Unreachable
    /// rather than let it escape <c>GenerateAsync</c>.</summary>
    public sealed class ThrowingHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
            => throw new System.IO.IOException("simulated mid-stream connection reset");

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>Story 8.2 — an in-memory <see cref="ISecretStore"/> for tests (no disk).</summary>
    public sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public FakeSecretStore(string? llmKey = null)
        {
            // DW-368: LLM keys are stored PER PROVIDER (SecretIds.ForLlmProvider). The convenience ctor seeds the
            // given key under EVERY catalog provider's id so fixtures that only mean "a key exists for whichever
            // provider the test selects" keep that meaning. Tests exercising the per-provider ROUTING itself build
            // their store state explicitly via Set(SecretIds.ForLlmProvider(...), ...) instead.
            if (!string.IsNullOrEmpty(llmKey))
                foreach (var p in LlmProviderCatalog.Providers)
                    _map[SecretIds.ForLlmProvider(p.Id)] = llmKey!;
        }

        public string Get(string id) => _map.TryGetValue(id, out string? v) ? v : "";
        public void Set(string id, string value) => _map[id] = value;
        public bool Has(string id) => _map.TryGetValue(id, out string? v) && !string.IsNullOrEmpty(v);
        public void Clear(string id) => _map.Remove(id);
    }
}
