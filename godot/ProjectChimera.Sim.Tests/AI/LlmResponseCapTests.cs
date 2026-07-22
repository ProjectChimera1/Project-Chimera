#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — the response byte cap: an oversized 2xx body fails as malformed/oversized, and a body just under
    /// the cap still parses. The read is bounded (<see cref="LlmHttp.ReadBoundedAsync"/>), so the buffer never grows
    /// past the ceiling.
    /// </summary>
    public class LlmResponseCapTests
    {
        [Fact]
        public async Task OversizedBody_FailsAsMalformed()
        {
            // A body that is BOTH well-shaped (a real text block the Anthropic extractor accepts) AND over the cap, so
            // the ONLY thing that can make it fail is the byte cap firing. (A shape-only-invalid body would fail as
            // malformed even with the cap removed, which would let a dropped cap ship green — the regression this guards.)
            string filler = new string('x', LlmHttp.MaxResponseBytes + 1024);
            string body = "{\"content\":[{\"type\":\"text\",\"text\":\"" + filler + "\"}]}";
            var stub = StubHttpMessageHandler.Ok(body);
            var provider = new AnthropicProvider(new HttpClient(stub), "https://api.anthropic.com", "m", "k");

            NormalizedResult r = await provider.GenerateAsync(new NormalizedRequest("s", "u"), CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.MalformedResponse, r.Failure);
        }

        [Fact]
        public async Task ReadBoundedAsync_UnderCap_ReturnsBody()
        {
            var content = new StringContent("{\"ok\":1}");
            string? body = await LlmHttp.ReadBoundedAsync(content, CancellationToken.None);
            Assert.Equal("{\"ok\":1}", body);
        }

        [Fact]
        public async Task ReadBoundedAsync_OverCap_ReturnsNull()
        {
            var content = new StringContent(new string('a', LlmHttp.MaxResponseBytes + 1));
            string? body = await LlmHttp.ReadBoundedAsync(content, CancellationToken.None);
            Assert.Null(body); // bounded read abandons past the cap
        }
    }
}
