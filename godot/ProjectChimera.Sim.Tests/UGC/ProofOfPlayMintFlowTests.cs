#nullable enable
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.UGC
{
    /// <summary>An in-memory <see cref="ISecretStore"/> for the provisioning tests.</summary>
    internal sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string Get(string id) => _d.TryGetValue(id, out var v) ? v : "";
        public void Set(string id, string value) => _d[id] = value ?? "";
        public bool Has(string id) => !string.IsNullOrEmpty(Get(id));
        public void Clear(string id) => _d.Remove(id);
    }

    /// <summary>
    /// Story 9.8 — the end-to-end mint flow WITHOUT Godot: build a <see cref="ScenarioData"/> → canonical Compute →
    /// mint → verify → edit the model → assert the token is now stale (but still untampered). This is the pure
    /// mirror of the <c>ScenarioDelegateBinder</c> mint hook + <c>PublishGate</c> staleness rule (AC2).
    /// </summary>
    public class ProofOfPlayMintFlowTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("mint-flow-key-abc123");

        [Fact]
        public void Mint_BindsToCanonicalModel_ContentEditMakesStale_CosmeticEditDoesNot()
        {
            var scenario = new ScenarioData { Id = "m", DisplayName = "M", MapBounds = 120f };

            ulong hash = CanonicalModelHash.Compute(scenario);
            var token = ProofOfPlaySigner.Create(hash, "win", "2026-07-24T00:00:00Z", scenario.Id, Key);

            // Freshly minted, model unchanged: verifies AND matches the current hash.
            Assert.True(ProofOfPlaySigner.Verify(token, Key));
            Assert.True(ProofOfPlaySigner.MatchesScenario(token, CanonicalModelHash.Compute(scenario)));

            // A real CONTENT edit (folded field) moves the canonical hash → the token is stale.
            scenario.MapBounds = 100f;
            ulong editedHash = CanonicalModelHash.Compute(scenario);
            Assert.NotEqual(hash, editedHash);
            Assert.False(ProofOfPlaySigner.MatchesScenario(token, editedHash));
            // Staleness ≠ tamper: the signature is still intact.
            Assert.True(ProofOfPlaySigner.Verify(token, Key));

            // A COSMETIC edit (Id/DisplayName are hash-EXCLUDED) does NOT move the hash → not stale.
            var renamed = new ScenarioData { Id = "renamed", DisplayName = "Totally Different", MapBounds = 120f };
            Assert.True(ProofOfPlaySigner.MatchesScenario(token, CanonicalModelHash.Compute(renamed)));
        }

        [Fact]
        public void ShouldMint_TrueOnlyForLocalFactionWin()
        {
            // Exercise the REAL shared decision the binder calls (review P5) — not a re-implemented mirror.
            Assert.True (ProofOfPlayMint.ShouldMint(0, Faction.Player1)); // self win  → mint
            Assert.False(ProofOfPlayMint.ShouldMint(1, Faction.Player1)); // P2 wins   → no mint
            Assert.True (ProofOfPlayMint.ShouldMint(1, Faction.Player2)); // self win  → mint
            Assert.False(ProofOfPlayMint.ShouldMint(0, Faction.Player2)); // P1 wins   → no mint
        }

        [Fact]
        public void ResolveScenarioId_PrefersId_ThenSlug_ThenFallback()
        {
            Assert.Equal("my-id",
                ProofOfPlayMint.ResolveScenarioId(new ScenarioData { Id = "my-id", DisplayName = "Ignored" }));
            Assert.Equal(ContentPackager.Slugify("Cool Map"),
                ProofOfPlayMint.ResolveScenarioId(new ScenarioData { Id = "", DisplayName = "Cool Map" }));
            Assert.Equal("scenario",
                ProofOfPlayMint.ResolveScenarioId(new ScenarioData { Id = "", DisplayName = "" }));
        }

        [Fact]
        public void GetOrProvisionSigningKey_EmptyStore_ProvisionsRoundTrippableKey()
        {
            var store = new FakeSecretStore();

            var status = ProofOfPlayMint.GetOrProvisionSigningKey(store, out var key);
            Assert.Equal(SigningKeyStatus.Provisioned, status);
            Assert.Equal(32, key.Length);

            // A second call finds the stored key and returns the identical bytes.
            var status2 = ProofOfPlayMint.GetOrProvisionSigningKey(store, out var key2);
            Assert.Equal(SigningKeyStatus.Existing, status2);
            Assert.Equal(key, key2);

            // The provisioned key actually signs + verifies.
            var token = ProofOfPlaySigner.Create(0x99, "win", "t", "id", key);
            Assert.True(ProofOfPlaySigner.Verify(token, key2));
        }

        [Fact]
        public void GetOrProvisionSigningKey_CorruptStore_DoesNotOverwrite()
        {
            var store = new FakeSecretStore();
            store.Set(SecretIds.ProofOfPlay, "not-hex-garbage!!");

            var status = ProofOfPlayMint.GetOrProvisionSigningKey(store, out var key);
            Assert.Equal(SigningKeyStatus.CorruptExisting, status);
            Assert.Empty(key);
            // The corrupt value is LEFT INTACT — never rotated (which would invalidate all prior tokens).
            Assert.Equal("not-hex-garbage!!", store.Get(SecretIds.ProofOfPlay));
        }
    }
}
