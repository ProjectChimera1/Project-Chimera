#nullable enable
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.2 — each adapter against a <see cref="StubHttpMessageHandler"/>: asserts the URL path, the headers,
    /// the request-body shape, and the parsed <see cref="NormalizedResult.Text"/>. The whole layer uses only
    /// <c>System.Net.Http</c>/<c>System.Text.Json</c> — no vendor SDK (this assembly compiles without one, which the
    /// build itself proves).
    /// </summary>
    public class LlmProviderAdapterTests
    {
        private static readonly NormalizedRequest Req = new("SYS", "USER", maxTokens: 512);

        private static HttpClient Client(StubHttpMessageHandler h) => new(h);

        // ── Anthropic ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Anthropic_PostsCorrectPath_Headers_Body_ParsesText()
        {
            var stub = StubHttpMessageHandler.Ok(
                "{\"content\":[{\"type\":\"text\",\"text\":\"hello-anthropic\"}]}");
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "claude-x", "sk-abc");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal("hello-anthropic", r.Text);
            Assert.Equal("anthropic", provider.ProviderId);
            Assert.Equal("https://api.anthropic.com/v1/messages", stub.LastUri!.ToString());
            Assert.Equal("sk-abc", stub.LastHeaders["x-api-key"]);
            Assert.Equal("2023-06-01", stub.LastHeaders["anthropic-version"]);

            using var doc = JsonDocument.Parse(stub.LastBody);
            Assert.Equal("claude-x", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal(512, doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("SYS", doc.RootElement.GetProperty("system").GetString());
            Assert.Equal("user", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("USER", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        }

        // ── Ollama ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Ollama_PostsChatPath_Body_ParsesText_NoKey()
        {
            var stub = StubHttpMessageHandler.Ok(
                "{\"message\":{\"role\":\"assistant\",\"content\":\"hello-ollama\"}}");
            var provider = new OllamaProvider(Client(stub), "http://localhost:11434", "llama3.1");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal("hello-ollama", r.Text);
            Assert.Equal("ollama", provider.ProviderId);
            // Per epic: /api/chat, NOT the legacy /api/generate.
            Assert.Equal("http://localhost:11434/api/chat", stub.LastUri!.ToString());
            Assert.False(stub.LastHeaders.ContainsKey("Authorization")); // local, no key

            using var doc = JsonDocument.Parse(stub.LastBody);
            Assert.Equal("llama3.1", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("system", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("SYS", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("user", doc.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
            Assert.Equal("USER", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        }

        // ── OpenRouter ────────────────────────────────────────────────────────────

        [Fact]
        public async Task OpenRouter_PostsCompletionsPath_BearerHeader_Body_ParsesText()
        {
            var stub = StubHttpMessageHandler.Ok(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello-openrouter\"}}]}");
            var provider = new OpenRouterProvider(Client(stub), "https://openrouter.ai/api/v1", "some/model", "or-key");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal("hello-openrouter", r.Text);
            Assert.Equal("openrouter", provider.ProviderId);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", stub.LastUri!.ToString());
            Assert.Equal("Bearer or-key", stub.LastHeaders["Authorization"]);

            using var doc = JsonDocument.Parse(stub.LastBody);
            Assert.Equal("some/model", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("system", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("user", doc.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
        }

        // ── Failure surfaced verbatim, no masking ─────────────────────────────────

        [Fact]
        public async Task Adapter_500_ReturnsHttpError_NotOk()
        {
            var stub = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError, "boom");
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "k");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.HttpError, r.Failure);
            Assert.Equal(1, stub.CallCount);
        }

        [Fact]
        public async Task Adapter_Unreachable_ReturnsUnreachable_NotOk()
        {
            var stub = StubHttpMessageHandler.Unreachable();
            var provider = new OllamaProvider(Client(stub), "http://localhost:11434", "m");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.Unreachable, r.Failure);
        }

        [Fact]
        public async Task Adapter_2xx_JunkBody_ReturnsMalformed_NotOk()
        {
            var stub = StubHttpMessageHandler.Ok("{\"unexpected\":true}");
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "k");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.MalformedResponse, r.Failure);
        }

        // ── Anthropic multi-block content (guards the "first TEXT block" selection) ─────────────

        [Fact]
        public async Task Anthropic_ThinkingFirstThenText_ParsesTheTextBlock()
        {
            // A realistic Messages-API response whose first block is NOT text (thinking/tool_use). The adapter must
            // select the first block whose type == "text" — a naive content[0] would misclassify a healthy answer as
            // malformed (the regression this guards; the shared 8.3 generate path depends on it).
            var stub = StubHttpMessageHandler.Ok(
                "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"…\"},{\"type\":\"text\",\"text\":\"real-answer\"}]}");
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "k");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.True(r.Ok);
            Assert.Equal("real-answer", r.Text);
        }

        // ── Malformed key must NOT throw out of GenerateAsync (ILLMProvider never-throws contract) ──────

        [Fact]
        public async Task Anthropic_MalformedKey_FailsWithoutThrowing_AndNeverDispatches()
        {
            var stub = StubHttpMessageHandler.Ok("{\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}");
            // An embedded newline is an invalid HTTP header value → HttpHeaders.Add would throw FormatException.
            var provider = new AnthropicProvider(Client(stub), "https://api.anthropic.com", "m", "bad\nkey");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.HttpError, r.Failure); // maps to FailedValidation, not Unreachable
            Assert.Equal(0, stub.CallCount); // request was never dispatched
        }

        [Fact]
        public async Task OpenRouter_MalformedKey_FailsWithoutThrowing_AndNeverDispatches()
        {
            var stub = StubHttpMessageHandler.Ok(
                "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}");
            var provider = new OpenRouterProvider(Client(stub), "https://openrouter.ai/api/v1", "m", "bad\nkey");

            NormalizedResult r = await provider.GenerateAsync(Req, CancellationToken.None);

            Assert.False(r.Ok);
            Assert.Equal(NormalizedFailure.HttpError, r.Failure);
            Assert.Equal(0, stub.CallCount);
        }
    }
}
