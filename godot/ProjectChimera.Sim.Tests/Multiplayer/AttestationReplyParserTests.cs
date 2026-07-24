#nullable enable
using ProjectChimera.Core.Definitions; // AttestationReplyParser, AttestationOutcome, StorageWriteResult, ProfileInvalidReason, OnlineHeroLaunchGate
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 9.12 (P4) — the fail-closed precondition: the whole "an unattested / unreachable-server profile can never
    /// enter online play" guarantee flows through <see cref="AttestationReplyParser"/>, which turns the RAW RPC reply
    /// strings the TS module emits into the value types the client gates on. These tests feed the EXACT JSON the TS
    /// handlers produce and pin that a malformed/empty reply is FAIL-CLOSED (never fail-open), that a legitimate
    /// <c>not_found</c> is distinct from a server error (P9), and that the gate agrees.
    /// </summary>
    public class AttestationReplyParserTests
    {
        // ── Attestation replies (the exact strings rpc_attest_hero_profile returns) ─────────

        [Fact]
        public void ParseAttestation_AttestedTrue_IsOk_AndGateAllows()
        {
            AttestationOutcome o = AttestationReplyParser.ParseAttestation("{\"attested\":true,\"reason\":\"none\"}");
            Assert.True(o.Attested);
            Assert.True(o.CallSucceeded);
            Assert.True(OnlineHeroLaunchGate.CanEnterMatch(o));
        }

        [Fact]
        public void ParseAttestation_AttestedFalseWithValidationReason_IsUnattested_NotOk()
        {
            AttestationOutcome o = AttestationReplyParser.ParseAttestation("{\"attested\":false,\"reason\":\"range\"}");
            Assert.False(o.Attested);
            Assert.True(o.CallSucceeded);                       // the call reached the server (a legitimate answer)
            Assert.Equal(ProfileInvalidReason.Range, o.Reason);
            Assert.False(OnlineHeroLaunchGate.CanEnterMatch(o)); // fail-closed
        }

        [Fact]
        public void ParseAttestation_NotFound_IsUnattested_CallSucceeded_DistinctFromServerError()
        {
            AttestationOutcome o = AttestationReplyParser.ParseAttestation("{\"attested\":false,\"reason\":\"not_found\"}");
            Assert.False(o.Attested);
            Assert.True(o.CallSucceeded);                       // P9: a legitimate "no stored hero" answer, NOT a server error
            Assert.Equal(ProfileInvalidReason.None, o.Reason);
            Assert.False(OnlineHeroLaunchGate.CanEnterMatch(o));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{ this is not json")]
        [InlineData("not-even-json")]
        public void ParseAttestation_EmptyOrGarbled_IsCallFailed_FailClosed(string? json)
        {
            // P4/P9: an empty or unparseable COMPLETED reply cannot be trusted → CallSucceeded=false (a server error the
            // UI surfaces as "try again"), and the gate refuses launch. Never fail-open.
            AttestationOutcome o = AttestationReplyParser.ParseAttestation(json);
            Assert.False(o.CallSucceeded);
            Assert.False(o.Attested);
            Assert.False(OnlineHeroLaunchGate.CanEnterMatch(o));
        }

        // ── Write replies (the exact strings rpc_write_hero_profile returns) ────────────────

        [Fact]
        public void ParseWriteResult_Ok_CarriesVersion()
        {
            StorageWriteResult r = AttestationReplyParser.ParseWriteResult("{\"ok\":true,\"version\":\"v1\"}");
            Assert.True(r.Ok);
            Assert.Equal("v1", r.Version);
        }

        [Fact]
        public void ParseWriteResult_Rejected_CarriesReason_NotOk()
        {
            StorageWriteResult r = AttestationReplyParser.ParseWriteResult("{\"ok\":false,\"reason\":\"identity\"}");
            Assert.False(r.Ok);
            Assert.Equal("identity", r.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{ broken")]
        public void ParseWriteResult_EmptyOrGarbled_IsFailure(string? json)
        {
            StorageWriteResult r = AttestationReplyParser.ParseWriteResult(json);
            Assert.False(r.Ok); // never a false success on an untrustworthy reply
        }

        // ── ReasonOf string→enum mapping ────────────────────────────────────────────────────

        [Theory]
        [InlineData("identity", ProfileInvalidReason.Identity)]
        [InlineData("range", ProfileInvalidReason.Range)]
        [InlineData("inventory", ProfileInvalidReason.Inventory)]
        [InlineData("attributes", ProfileInvalidReason.Attributes)]
        [InlineData("not_found", ProfileInvalidReason.None)]
        [InlineData("weird_unknown", ProfileInvalidReason.None)]
        [InlineData(null, ProfileInvalidReason.None)]
        public void ReasonOf_MapsKnownReasons_UnknownToNone(string? reason, ProfileInvalidReason expected)
        {
            Assert.Equal(expected, AttestationReplyParser.ReasonOf(reason));
        }
    }
}
