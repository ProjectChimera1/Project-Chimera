#nullable enable
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the OpenRouter adapter (OpenAI-compatible chat completions). Posts to
    /// <c>{baseUrl}/chat/completions</c> with an <c>Authorization: Bearer {key}</c> header and the
    /// <c>{model, messages:[{role:system},{role:user}]}</c> body, then extracts
    /// <c>choices[0].message.content</c>. No vendor SDK — <c>System.Net.Http</c> + <c>System.Text.Json</c> only;
    /// the <see cref="HttpClient"/> is injected for testing.
    /// </summary>
    public sealed class OpenRouterProvider : ILLMProvider
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _apiKey;

        public string ProviderId => "openrouter";

        public OpenRouterProvider(HttpClient http, string baseUrl, string model, string apiKey)
        {
            _http     = http;
            _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
            _model    = model;
            _apiKey   = apiKey;
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
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            // A pasted key with an invalid header character makes HttpHeaders.Add throw FormatException; catch it so
            // GenerateAsync honours the ILLMProvider "never throws for a provider-side failure" contract. A bad key is
            // a validation failure (→ FailedValidation), NOT an unreachable host.
            try
            {
                req.Headers.Add("Authorization", "Bearer " + _apiKey);
            }
            catch (FormatException ex)
            {
                return NormalizedResult.Fail(NormalizedFailure.HttpError,
                    $"invalid API key format (cannot be sent as an HTTP header): {ex.Message}");
            }

            var (ok, respBody, kind, error) = await LlmHttp.SendAsync(_http, req, ct);
            if (!ok) return NormalizedResult.Fail(kind, error);

            // choices[0].message.content
            return LlmHttp.ParseContent(respBody!, root =>
                root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        }
    }
}
