#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectChimera.AI.Providers
{
    /// <summary>
    /// Story 8.2 — the shared adapter plumbing that keeps the three adapters thin and identical in their failure
    /// taxonomy. One byte-cap (<see cref="MaxResponseBytes"/>) and one exception→<see cref="NormalizedFailure"/>
    /// mapping live here:
    /// <list type="bullet">
    ///   <item>network / DNS / timeout → <see cref="NormalizedFailure.Unreachable"/></item>
    ///   <item>non-2xx status → <see cref="NormalizedFailure.HttpError"/></item>
    ///   <item>oversized body, or a 2xx body that does not parse into the expected shape →
    ///         <see cref="NormalizedFailure.MalformedResponse"/></item>
    /// </list>
    /// The response is read via a BOUNDED stream read (not <c>ReadAsStringAsync</c>), so a body beyond the cap fails
    /// as malformed BEFORE it is fully materialized into memory. Godot-free — <c>System.Net.Http</c> +
    /// <c>System.Text.Json</c> only.
    /// </summary>
    public static class LlmHttp
    {
        /// <summary>Fixed ceiling on the buffered response body. A body exceeding this fails as
        /// <see cref="NormalizedFailure.MalformedResponse"/> rather than being read unbounded into memory. 1 MiB is
        /// far above any well-formed chat/messages completion this v1 (blocking, non-streaming) layer requests.</summary>
        public const int MaxResponseBytes = 1_048_576;

        private const int ReadChunkBytes = 16 * 1024;

        /// <summary>Deadline for reading the response BODY. Under <see cref="HttpCompletionOption.ResponseHeadersRead"/>
        /// (used below so the status is known before pulling the bounded stream), <see cref="HttpClient.Timeout"/>
        /// covers only the header read — a server that sends headers then stalls the body would otherwise hang the
        /// read indefinitely. A linked CTS bounds it so Test-connection / generation can never hang forever.</summary>
        private const int BodyReadTimeoutMs = 30_000;

        /// <summary>Send <paramref name="req"/> on <paramref name="http"/>, bound-read the response, and classify the
        /// outcome. On success returns <c>(true, body, None, "")</c>; on any provider-side failure returns
        /// <c>(false, null, kind, reason)</c>. Genuine caller cancellation (<paramref name="ct"/> signalled) is
        /// re-thrown so the caller's own cancellation path runs; a timeout (ct NOT signalled) maps to
        /// <see cref="NormalizedFailure.Unreachable"/>.</summary>
        public static async Task<(bool ok, string? body, NormalizedFailure kind, string error)> SendAsync(
            HttpClient http, HttpRequestMessage req, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                // ResponseHeadersRead so the status is known before we pull the (bounded) body stream.
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine caller-requested cancellation — propagate, not a provider failure
            }
            catch (Exception ex)
            {
                // HttpRequestException (DNS/connect refused), or a TaskCanceledException from the client timeout
                // (ct NOT signalled) — both are "could not complete the round-trip" → Unreachable.
                return (false, null, NormalizedFailure.Unreachable, $"unreachable: {ex.Message}");
            }

            using (resp)
            {
                string? body;
                // Bound the body read (see BodyReadTimeoutMs) AND keep any body-read failure inside the failure
                // taxonomy: a mid-stream IOException / connection reset, or the body-read deadline firing, must become
                // an Unreachable NormalizedResult — never an exception escaping GenerateAsync (the ILLMProvider
                // "never throws for a provider-side failure" contract). Genuine caller cancellation still propagates.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(BodyReadTimeoutMs);
                try
                {
                    body = await ReadBoundedAsync(resp.Content, readCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // genuine caller-requested cancellation — propagate, not a provider failure
                }
                catch (Exception ex)
                {
                    // body-read deadline (readCts fired, ct NOT signalled) or a mid-stream network failure.
                    return (false, null, NormalizedFailure.Unreachable, $"unreachable: {ex.Message}");
                }

                if (body == null)
                    return (false, null, NormalizedFailure.MalformedResponse,
                        $"response exceeded the {MaxResponseBytes}-byte cap");

                if (!resp.IsSuccessStatusCode)
                    return (false, null, NormalizedFailure.HttpError,
                        $"HTTP {(int)resp.StatusCode}: {Truncate(body, 240)}");

                return (true, body, NormalizedFailure.None, "");
            }
        }

        /// <summary>Read at most <see cref="MaxResponseBytes"/> from <paramref name="content"/>. Returns the decoded
        /// UTF-8 string, or <c>null</c> if the body exceeds the cap (the buffer never grows past the cap — the last
        /// over-cap chunk is discarded and the read abandoned).</summary>
        public static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
        {
            using Stream stream = await content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[ReadChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, ct)) > 0)
            {
                // Check BEFORE writing so the buffer is bounded at MaxResponseBytes and an oversized body is never
                // fully materialized.
                if (buffer.Length + read > MaxResponseBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>Parse <paramref name="body"/> and pull the content via <paramref name="extract"/>, mapping any
        /// parse/shape miss (or an empty extracted string) to <see cref="NormalizedFailure.MalformedResponse"/>. The
        /// single content-parse chokepoint shared by all three adapters, so their failure taxonomy is identical.</summary>
        public static NormalizedResult ParseContent(string body, Func<JsonElement, string?> extract)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                string? text = extract(doc.RootElement);
                if (string.IsNullOrEmpty(text))
                    return NormalizedResult.Fail(NormalizedFailure.MalformedResponse,
                        "response parsed but carried no content text");
                return NormalizedResult.Success(text!);
            }
            catch (Exception ex) when (
                ex is JsonException
                   or KeyNotFoundException
                   or InvalidOperationException
                   or IndexOutOfRangeException
                   or ArgumentOutOfRangeException
                   or NotSupportedException)
            {
                return NormalizedResult.Fail(NormalizedFailure.MalformedResponse,
                    $"unparseable response: {ex.Message}");
            }
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
