#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 15-14 (DW-200, Alec's 2026-08-12 ruling: LIVE NAKAMA validation) — the asynchronous credential
    /// verifier behind <see cref="TrustMode.OnlineAttest"/>: a presented (userId, session token) is genuine iff
    /// Nakama itself answers <c>GET /v2/account</c> for that token with the SAME user id. A forged/expired token
    /// gets 401 (fetch returns null); a stolen token presented with a different claimed userId mismatches.
    ///
    /// <para><b>Why async, and where the seam sits.</b> The dedicated server pumps its transport at frame rate —
    /// a blocking HTTP call inside packet dispatch would stall every live match on the relay. So validation is
    /// begin/drain: <see cref="BeginValidate"/> fires the fetch on the thread pool and parks the outcome;
    /// <see cref="DrainVerified"/> is called from the server's main-loop <c>_Process</c> and delivers results on
    /// the main thread (the NakamaService.DrainEvents idiom). The HTTP itself is an injected
    /// <c>fetchAccountUserId(token) → userId-or-null</c> seam, so Tier-1 tests drive the whole flow with a fake,
    /// and the default implementation (<see cref="ForServer"/>) is the only place that touches the network.</para>
    ///
    /// <para><b>Fail-closed posture.</b> A fetch that errors, times out, or returns a mismatched id delivers
    /// NOTHING — the slot simply stays unattested and cannot Ready (the gate's posture). A per-token result cache
    /// bounds re-validation (a client that re-attests on reconnect re-uses its verdict); negative results are NOT
    /// cached (a transient Nakama outage must not permanently damn a valid token).</para>
    /// </summary>
    public sealed class NakamaTokenVerifier
    {
        private readonly Func<string, System.Threading.Tasks.Task<string?>> _fetchAccountUserId;
        private readonly ConcurrentQueue<(int slot, string userId)> _verified = new();
        private readonly ConcurrentDictionary<string, string> _tokenCache = new(); // token → confirmed userId

        public NakamaTokenVerifier(Func<string, System.Threading.Tasks.Task<string?>> fetchAccountUserId)
            => _fetchAccountUserId = fetchAccountUserId;

        /// <summary>The production verifier: validates tokens against a live Nakama at
        /// <paramref name="baseUrl"/> (e.g. <c>http://127.0.0.1:7350</c>) via <c>GET /v2/account</c>.</summary>
        public static NakamaTokenVerifier ForServer(string baseUrl)
        {
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string url = baseUrl.TrimEnd('/') + "/v2/account";
            return new NakamaTokenVerifier(async token =>
            {
                try
                {
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using var resp = await http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) return null; // 401 = forged/expired — fail-closed
                    using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    return doc.RootElement.TryGetProperty("user", out var user) &&
                           user.TryGetProperty("id", out var id) ? id.GetString() : null;
                }
                catch
                {
                    return null; // network fault = unverified (never fail-open); not cached, so a retry can succeed
                }
            });
        }

        /// <summary>
        /// Begin validating <paramref name="slot"/>'s credential. Delivery happens later via
        /// <see cref="DrainVerified"/> — only when Nakama confirmed the token AND its account id equals
        /// <paramref name="claimedUserId"/>. Anything else delivers nothing (the slot stays unattested).
        /// </summary>
        public void BeginValidate(int slot, string claimedUserId, string token)
        {
            if (string.IsNullOrEmpty(claimedUserId) || string.IsNullOrEmpty(token)) return;

            if (_tokenCache.TryGetValue(token, out string? cached))
            {
                if (cached == claimedUserId) _verified.Enqueue((slot, cached));
                return;
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                string? confirmed = await _fetchAccountUserId(token);
                if (confirmed == null) return;         // unverifiable — fail-closed, retryable
                _tokenCache[token] = confirmed;        // positive results cache (reconnect re-attest is free)
                if (confirmed == claimedUserId) _verified.Enqueue((slot, confirmed));
            });
        }

        /// <summary>Drain confirmed attestations on the caller's (main) thread — invoked from the server loop.
        /// Returns how many were delivered.</summary>
        public int DrainVerified(Action<int, string> onVerified)
        {
            int n = 0;
            while (_verified.TryDequeue(out (int slot, string userId) v)) { onVerified(v.slot, v.userId); n++; }
            return n;
        }
    }
}
