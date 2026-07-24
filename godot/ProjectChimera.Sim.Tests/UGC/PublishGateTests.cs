#nullable enable
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.UGC
{
    /// <summary>
    /// Story 9.8 — the unified pre-publish gate (<see cref="PublishGate"/>). One test per I/O-Matrix refusal row plus
    /// the happy path and the "all reasons listed" fan-out. Pure/Godot-free.
    /// </summary>
    public class PublishGateTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("gate-test-key-abcdef0123456789");
        private const ulong Hash = 0xC0FFEE1234ABCDEF;

        private static ContentPackageManifest ValidManifest() => new()
        {
            ThumbnailFile = "preview/preview.png",
            Description   = new string('x', PublishGate.MinDescriptionLength), // exactly 100
            Screenshots   = new List<string> { "screenshots/shot_00.png" },
            IpConsent     = true,
        };

        private static ProofOfPlayToken ValidToken()
            => ProofOfPlaySigner.Create(Hash, "win", "2026-07-24T00:00:00Z", "map", Key);

        [Fact]
        public void AllPresent_Passes()
        {
            var result = PublishGate.Check(ValidManifest(), ValidToken(), Hash, Key);
            Assert.True(result.Passed);
            Assert.Empty(result.Reasons);
        }

        [Fact]
        public void MissingToken_Fails_NoProofOfPlay()
        {
            var result = PublishGate.Check(ValidManifest(), null, Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonNoToken, result.Reasons);
        }

        [Fact]
        public void TamperedToken_Fails_InvalidToken()
        {
            var token = ValidToken();
            token.MintedAt = "tampered"; // signature no longer matches
            var result = PublishGate.Check(ValidManifest(), token, Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonInvalidToken, result.Reasons);
        }

        [Fact]
        public void NonWinOutcome_Fails_InvalidToken()
        {
            // A correctly-signed but non-win token is still not a valid WIN proof.
            var token = ProofOfPlaySigner.Create(Hash, "loss", "t", "map", Key);
            var result = PublishGate.Check(ValidManifest(), token, Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonInvalidToken, result.Reasons);
        }

        [Fact]
        public void EditedScenario_Fails_TokenStale()
        {
            // Token minted for Hash, but the current model now hashes to something else.
            var result = PublishGate.Check(ValidManifest(), ValidToken(), currentScenarioHash: Hash + 1, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonStaleToken, result.Reasons);
        }

        [Fact]
        public void ShortDescription_Fails()
        {
            var m = ValidManifest();
            m.Description = new string('x', PublishGate.MinDescriptionLength - 1); // 99
            var result = PublishGate.Check(m, ValidToken(), Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonShortDesc, result.Reasons);
        }

        [Fact]
        public void AllWhitespaceDescription_Fails()
        {
            // Review P6: 100 spaces must NOT satisfy the floor (trimmed length is 0).
            var m = ValidManifest();
            m.Description = new string(' ', PublishGate.MinDescriptionLength);
            var result = PublishGate.Check(m, ValidToken(), Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonShortDesc, result.Reasons);
        }

        [Fact]
        public void MissingThumbnail_Fails()
        {
            var m = ValidManifest();
            m.ThumbnailFile = null;
            var result = PublishGate.Check(m, ValidToken(), Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonNoThumbnail, result.Reasons);
        }

        [Fact]
        public void NoScreenshots_Fails()
        {
            var m = ValidManifest();
            m.Screenshots = new List<string>();
            var result = PublishGate.Check(m, ValidToken(), Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonNoScreenshot, result.Reasons);
        }

        [Fact]
        public void NoConsent_Fails()
        {
            var m = ValidManifest();
            m.IpConsent = false;
            var result = PublishGate.Check(m, ValidToken(), Hash, Key);
            Assert.False(result.Passed);
            Assert.Contains(PublishGate.ReasonNoConsent, result.Reasons);
        }

        [Fact]
        public void EverythingMissing_ListsAllReasons()
        {
            var empty = new ContentPackageManifest
            {
                ThumbnailFile = null,
                Description   = "",
                Screenshots   = new List<string>(),
                IpConsent     = false,
            };
            var result = PublishGate.Check(empty, null, Hash, Key);
            Assert.False(result.Passed);
            // Review P9: assert the EXACT reason set (order-independent) so an extra/wrong reason fails the test.
            var expected = new SortedSet<string>
            {
                PublishGate.ReasonNoToken,
                PublishGate.ReasonNoThumbnail,
                PublishGate.ReasonShortDesc,
                PublishGate.ReasonNoScreenshot,
                PublishGate.ReasonNoConsent,
            };
            Assert.Equal(expected, new SortedSet<string>(result.Reasons));
        }
    }
}
