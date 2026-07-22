#nullable enable
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the local Ollama adapter. Posts to <c>{baseUrl}/api/chat</c> (per the epic — NOT the legacy
    /// <c>/api/generate</c> the untouched <c>LLMService</c> still uses) with the
    /// <c>{model, messages:[{role:system},{role:user}], stream:false}</c> body, then extracts
    /// <c>message.content</c>. Ollama is a local (loopback) provider and needs NO API key. No vendor SDK —
    /// <c>System.Net.Http</c> + <c>System.Text.Json</c> only; the <see cref="HttpClient"/> is injected for testing.
    /// </summary>
    public sealed class OllamaProvider : ILLMProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;

        public string ProviderId => "ollama";

        public OllamaProvider(HttpClient http, string baseUrl, string model)
        {
            _http     = http;
            _endpoint = baseUrl.TrimEnd('/') + "/api/chat";
            _model    = model;
        }

        public async Task<NormalizedResult> GenerateAsync(NormalizedRequest request, CancellationToken ct)
        {
            var body = new
            {
                model    = _model,
                messages = new[]
                {
                    new { role = "system", content = request.SystemPrompt },
                    new { role = "user",   content = request.UserMessage },
                },
                stream = false,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };

            var (ok, respBody, kind, error) = await LlmHttp.SendAsync(_http, req, ct);
            if (!ok) return NormalizedResult.Fail(kind, error);

            // message.content
            return LlmHttp.ParseContent(respBody!, root =>
                root.GetProperty("message").GetProperty("content").GetString());
        }
    }
}
