#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 15-14 — the live-Nakama credential verifier's begin/drain flow over a FAKE fetch seam (no network):
    /// a confirmed matching identity drains onto the main thread; a mismatch or an unverifiable token delivers
    /// nothing (fail-closed); positive verdicts cache; negative ones do not (a transient outage is retryable).
    /// </summary>
    public class NakamaTokenVerifierTests
    {
        private static List<(int slot, string userId)> DrainAll(NakamaTokenVerifier v, int expect,
                                                                int timeoutMs = 5000)
        {
            var got = new List<(int, string)>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (got.Count < expect && sw.ElapsedMilliseconds < timeoutMs)
            {
                v.DrainVerified((s, u) => got.Add((s, u)));
                if (got.Count < expect) Thread.Sleep(5);
            }
            v.DrainVerified((s, u) => got.Add((s, u))); // one final sweep (also proves no extras)
            return got;
        }

        [Fact]
        public void ConfirmedMatchingIdentity_Drains()
        {
            var v = new NakamaTokenVerifier(_ => Task.FromResult<string?>("alice"));
            v.BeginValidate(1, "alice", "tok-1");
            var got = DrainAll(v, expect: 1);
            Assert.Equal(new[] { (1, "alice") }, got.ToArray());
        }

        [Fact]
        public void MismatchedClaim_DeliversNothing()
        {
            var v = new NakamaTokenVerifier(_ => Task.FromResult<string?>("alice"));
            v.BeginValidate(1, "mallory", "stolen-tok"); // real token, wrong claimed account
            Thread.Sleep(50);
            Assert.Empty(DrainAll(v, expect: 0, timeoutMs: 200));
        }

        [Fact]
        public void UnverifiableToken_FailsClosed_ButIsRetryable()
        {
            int calls = 0;
            var v = new NakamaTokenVerifier(_ =>
            {
                // First call: Nakama unreachable (null). Second call: recovered.
                return Task.FromResult<string?>(Interlocked.Increment(ref calls) == 1 ? null : "alice");
            });

            v.BeginValidate(1, "alice", "tok-x");
            Thread.Sleep(50);
            Assert.Empty(DrainAll(v, expect: 0, timeoutMs: 200)); // outage → nothing delivered

            v.BeginValidate(1, "alice", "tok-x");                 // negative was NOT cached → retried, succeeds
            Assert.Equal(new[] { (1, "alice") }, DrainAll(v, expect: 1).ToArray());
        }

        [Fact]
        public void PositiveVerdict_IsCached_FetchRunsOnce()
        {
            int calls = 0;
            var v = new NakamaTokenVerifier(_ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<string?>("alice");
            });

            v.BeginValidate(1, "alice", "tok-c");
            Assert.Single(DrainAll(v, expect: 1));

            v.BeginValidate(1, "alice", "tok-c"); // reconnect re-attest: served from cache, synchronously
            var got = new List<(int, string)>();
            v.DrainVerified((s, u) => got.Add((s, u)));
            Assert.Equal(new[] { (1, "alice") }, got.ToArray());
            Assert.Equal(1, calls);
        }

        [Fact]
        public void VerifiedAttestation_LandsOnTheGate_LanTrustStillIgnores()
        {
            var online = new IdentityGate(TrustMode.OnlineAttest, 8);
            Assert.True(online.RecordVerifiedAttestation(2, "alice"));
            Assert.Equal("alice", online.UserIdOf(2));
            Assert.True(online.MayReady(2, out _));

            var lan = new IdentityGate(TrustMode.LanTrust, 8);
            Assert.False(lan.RecordVerifiedAttestation(2, "alice")); // LAN stores no identity, ever
            Assert.Null(lan.UserIdOf(2));
        }
    }
}
