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
    /// Story 8.2 — the Anthropic Messages adapter. Posts to <c>{baseUrl}/v1/messages</c> with the
    /// <c>x-api-key</c> + <c>anthropic-version: 2023-06-01</c> headers and the
    /// <c>{model, max_tokens, system, messages:[{role:user,content}]}</c> body, then extracts
    /// <c>content[0].text</c>. Mirrors the shape of the legacy <c>LLMService.TryClaudeAsync</c> (untouched by this
    /// story). No vendor SDK — <c>System.Net.Http</c> + <c>System.Text.Json</c> only. The <see cref="HttpClient"/>
    /// is injected so the adapter is unit-testable against a stub handler with no live network.
    /// </summary>
    public sealed class AnthropicProvider : ILLMProvider
    {
        private const string AnthropicVersion = "2023-06-01";

        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _apiKey;

        public string ProviderId => "anthropic";

        public AnthropicProvider(HttpClient http, string baseUrl, string model, string apiKey)
        {
            _http     = http;
            _endpoint = baseUrl.TrimEnd('/') + "/v1/messages";
            _model    = model;
            _apiKey   = apiKey;
        }

        public async Task<NormalizedResult> GenerateAsync(NormalizedRequest request, CancellationToken ct)
        {
            var body = new
            {
                model      = _model,
                max_tokens = request.MaxTokens,
                system     = request.SystemPrompt,
                messages   = new[] { new { role = "user", content = request.UserMessage } },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            // A pasted key with an invalid header character (embedded space/newline/control char) makes
            // HttpHeaders.Add throw FormatException. Catch it here so GenerateAsync honours the ILLMProvider
            // "never throws for a provider-side failure" contract (the 8.3 generate path relies on it); a bad key
            // is a validation failure (→ FailedValidation), NOT an unreachable host.
            try
            {
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", AnthropicVersion);
            }
            catch (FormatException ex)
            {
                return NormalizedResult.Fail(NormalizedFailure.HttpError,
                    $"invalid API key format (cannot be sent as an HTTP header): {ex.Message}");
            }

            var (ok, respBody, kind, error) = await LlmHttp.SendAsync(_http, req, ct);
            if (!ok) return NormalizedResult.Fail(kind, error);

            // The Messages API returns `content` as an array of typed blocks; select the FIRST block whose
            // type == "text" rather than assuming content[0] is text — a thinking/tool_use-first response (the 8.3
            // generate path, which shares this adapter) would otherwise misclassify a healthy answer as malformed.
            return LlmHttp.ParseContent(respBody!, root =>
            {
                foreach (var block in root.GetProperty("content").EnumerateArray())
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                        && block.TryGetProperty("text", out var text))
                        return text.GetString();
                return null; // no text block → MalformedResponse
            });
        }
    }
}
