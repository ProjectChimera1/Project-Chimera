#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — pins the LlmHttp failure taxonomy on the paths the adapter-happy-path tests do not reach: a
    /// mid-stream body failure and a client timeout must both become an Unreachable <see cref="NormalizedResult"/>
    /// (never an escaping throw — the ILLMProvider "never throws for a provider-side failure" contract, which the
    /// Story 8.3 repoint will rely on), while a genuine caller cancellation must propagate.
    /// </summary>
    public class LlmHttpResilienceTests
    {
        private static readonly NormalizedRequest Req = new("SYS", "USER", maxTokens: 512);

        private static HttpClient Client(StubHttpMessageHandler h) => new(h);

        [Fact]
        public async Task MidStreamBodyFailure_MapsToUnreachable_NeverThrows()
        {
            var stub = StubHttpMessageHandler.MidStreamFailure();
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "k");

            // Must NOT throw — the body-read guard turns the mid-stream IOException into a failed result.
            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.Unreachable, r.Failure);
        }

        [Fact]
        public async Task ClientTimeout_MapsToUnreachable()
        {
            var stub = StubHttpMessageHandler.ClientTimeout();
            var provider = new OllamaProvider(Client(stub), "http://localhost:11434", "m");

            // A client-timeout TaskCanceledException with the caller's ct NOT signalled → Unreachable, not propagated.
            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.Unreachable, r.Failure);
        }

        [Fact]
        public async Task GenuineCancellation_Propagates()
        {
            var stub = StubHttpMessageHandler.Ok("{\"content\":[{\"type\":\"text\",\"text\":\"unused\"}]}");
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "k");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // A caller-signalled token must surface as an OperationCanceledException, not be swallowed into a state.
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                () => provider.GenerateAsync(Req, cts.Token));
        }
    }
}
