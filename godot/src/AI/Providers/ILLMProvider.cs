#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the provider-agnostic seam every LLM adapter and the availability evaluator speak. A tiny
    /// Godot-free abstraction (no Godot dependency) over the three hand-rolled adapters (Anthropic, Ollama,
    /// OpenRouter) built on <c>System.Net.Http</c> + <c>System.Text.Json</c> — no vendor SDK, AOT-clean. Lands
    /// under <c>src/AI/Providers/</c> so <c>SimSources.props</c>'s <c>src/AI/**</c> glob picks it into the Tier-1
    /// xUnit harness AND the determinism analyzer.
    ///
    /// <para>The selected provider is AUTHORITATIVE: an <see cref="ILLMProvider"/> instance speaks to exactly one
    /// provider and NEVER falls back to another on failure — a failure is surfaced verbatim as a
    /// <see cref="NormalizedResult"/> with <see cref="NormalizedResult.Ok"/> = false. Repointing
    /// <c>LLMService</c>'s generate methods onto this stack (and removing its implicit Claude→Ollama fallback) is
    /// Story 8.3; this story builds the stack, the config UI, and Test-connection and proves them in isolation.</para>
    /// </summary>
    public interface ILLMProvider
    {
        /// <summary>The stable provider id this adapter serves (<c>anthropic</c>/<c>ollama</c>/<c>openrouter</c>),
        /// matching <c>LlmProviderCatalog</c>. Lets a caller assert which adapter it holds (the no-fallback contract).</summary>
        string ProviderId { get; }

        /// <summary>Post the <paramref name="request"/> to this provider's endpoint and return the normalized result.
        /// Never throws for a provider-side failure (network/HTTP/parse) — those become a failed
        /// <see cref="NormalizedResult"/>. Honors <paramref name="ct"/> for genuine cancellation.</summary>
        Task<NormalizedResult> GenerateAsync(NormalizedRequest request, CancellationToken ct);
    }

    /// <summary>Story 8.2 — the provider-agnostic request: a system prompt, a single user message, and a max-tokens
    /// cap. String-in/string-out (no Godot, no <c>Fixed</c>/float determinism concern — the provider layer never
    /// runs in the deterministic tick).</summary>
    public readonly struct NormalizedRequest
    {
        public string SystemPrompt { get; }
        public string UserMessage { get; }
        public int MaxTokens { get; }

        public NormalizedRequest(string systemPrompt, string userMessage, int maxTokens = 2048)
        {
            SystemPrompt = systemPrompt ?? "";
            UserMessage  = userMessage ?? "";
            MaxTokens    = maxTokens > 0 ? maxTokens : 2048;
        }
    }

    /// <summary>Story 8.2 — the taxonomy of provider-side failures, shared identically across the three adapters
    /// (mapped once in <see cref="LlmHttp"/>). Distinct kinds so the evaluator can classify a Test-connection outcome
    /// into the four availability states without re-inspecting exceptions.</summary>
    public enum NormalizedFailure
    {
        /// <summary>No failure — <see cref="NormalizedResult.Ok"/> is true.</summary>
        None,

        /// <summary>The host could not be reached (DNS/connection failure or timeout). → <c>Unreachable</c>.</summary>
        Unreachable,

        /// <summary>The host answered with a non-2xx status (e.g. 401 bad key, 500). Reached-but-not-healthy. → <c>FailedValidation</c>.</summary>
        HttpError,

        /// <summary>A 2xx body that was oversized, unparseable, or missing the expected content shape. → <c>FailedValidation</c>.</summary>
        MalformedResponse,
    }

    /// <summary>Story 8.2 — the normalized outcome of a <see cref="ILLMProvider.GenerateAsync"/> call. On success,
    /// <see cref="Ok"/> = true and <see cref="Text"/> carries the extracted content; on failure, <see cref="Ok"/> =
    /// false, <see cref="Failure"/> names the kind, and <see cref="Error"/> is a terse human-readable reason.</summary>
    public readonly struct NormalizedResult
    {
        public bool Ok { get; }
        public string Text { get; }
        public string Error { get; }
        public NormalizedFailure Failure { get; }

        private NormalizedResult(bool ok, string text, string error, NormalizedFailure failure)
        {
            Ok      = ok;
            Text    = text;
            Error   = error;
            Failure = failure;
        }

        /// <summary>A successful result carrying the extracted <paramref name="text"/>.</summary>
        public static NormalizedResult Success(string text)
            => new(true, text ?? "", "", NormalizedFailure.None);

        /// <summary>A failed result of the given <paramref name="kind"/> with a terse <paramref name="message"/>.</summary>
        public static NormalizedResult Fail(NormalizedFailure kind, string message)
            => new(false, "", message ?? "", kind);
    }
}
