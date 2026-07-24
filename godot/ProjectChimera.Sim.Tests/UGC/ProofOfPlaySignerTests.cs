#nullable enable
using System.Text;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.UGC
{
    /// <summary>
    /// Story 9.8 — HMAC sign/verify + stale detection for <see cref="ProofOfPlaySigner"/> (I/O-Matrix rows:
    /// Tampered token, Edited scenario). Deterministic, Godot-free.
    /// </summary>
    public class ProofOfPlaySignerTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-signing-key-0123456789abcdef");

        [Fact]
        public void Create_ThenVerify_Passes()
        {
            var token = ProofOfPlaySigner.Create(0xDEADBEEFCAFEF00D, "win", "2026-07-24T00:00:00Z", "my-map", Key);
            Assert.True(ProofOfPlaySigner.Verify(token, Key));
            Assert.Equal("win", token.Outcome);
            Assert.Equal("my-map", token.ScenarioId);
            Assert.Equal("DEADBEEFCAFEF00D", token.ScenarioHash); // X16 hex of the ulong
        }

        [Fact]
        public void Verify_FailsForNullToken() => Assert.False(ProofOfPlaySigner.Verify(null, Key));

        [Fact]
        public void Verify_FailsForWrongKey()
        {
            var token = ProofOfPlaySigner.Create(123, "win", "t", "id", Key);
            Assert.False(ProofOfPlaySigner.Verify(token, Encoding.UTF8.GetBytes("a-different-key-entirely")));
        }

        [Fact]
        public void Verify_FailsWhenScenarioHashEdited()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.ScenarioHash = ProofOfPlaySigner.HashToHex(0x2222);
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void Verify_FailsWhenOutcomeEdited()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.Outcome = "loss";
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void Verify_FailsWhenMintedAtEdited()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.MintedAt = "9999-01-01T00:00:00Z";
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void Verify_FailsWhenScenarioIdEdited()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.ScenarioId = "someone-elses-map";
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void Verify_FailsWhenSignatureEdited()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.Signature = "00000000";               // valid hex, wrong value
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void Verify_FailsForMalformedHexSignature()
        {
            var token = ProofOfPlaySigner.Create(0x1111, "win", "t", "id", Key);
            token.Signature = "not-hex!!";              // non-hex ⇒ decode throws ⇒ Verify false (never throws)
            Assert.False(ProofOfPlaySigner.Verify(token, Key));
        }

        [Fact]
        public void MatchesScenario_TrueForSameHash_FalseForEdited()
        {
            var token = ProofOfPlaySigner.Create(0xABCDEF, "win", "t", "id", Key);
            Assert.True(ProofOfPlaySigner.MatchesScenario(token, 0xABCDEF));
            Assert.False(ProofOfPlaySigner.MatchesScenario(token, 0xABCDE0)); // edited scenario ⇒ new hash ⇒ stale
        }
    }
}
