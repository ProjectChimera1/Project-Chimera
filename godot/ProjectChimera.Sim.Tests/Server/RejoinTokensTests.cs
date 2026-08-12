#nullable enable
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>Story 15-1 (D-5) — per-match rejoin identity tokens. Godot-free.</summary>
    public class RejoinTokensTests
    {
        [Fact]
        public void MintedToken_Verifies_ForItsSlotOnly()
        {
            var tokens = new RejoinTokens(8);
            ulong t2 = tokens.Mint(2);
            ulong t3 = tokens.Mint(3);

            Assert.True(tokens.Verify(2, t2));
            Assert.True(tokens.Verify(3, t3));
            Assert.False(tokens.Verify(2, t3)); // another slot's token never opens this slot
            Assert.False(tokens.Verify(3, t2));
        }

        [Fact]
        public void UnmintedSlot_ZeroToken_OutOfRange_AllFailClosed()
        {
            var tokens = new RejoinTokens(8);
            tokens.Mint(1);
            Assert.False(tokens.Verify(0, 12345));  // never minted
            Assert.False(tokens.Verify(1, 0));      // the never-issued wire sentinel is never valid
            Assert.False(tokens.Verify(-1, 1));     // out of range
            Assert.False(tokens.Verify(8, 1));
        }

        [Fact]
        public void ReMint_InvalidatesThePriorToken()
        {
            var tokens = new RejoinTokens(8);
            ulong old = tokens.Mint(4);
            ulong fresh = tokens.Mint(4); // a fresh match re-mints
            Assert.False(tokens.Verify(4, old));
            Assert.True(tokens.Verify(4, fresh));
            Assert.NotEqual(old, fresh); // 2^-64 collision — a deterministic assert would be wrong, but a
                                         // colliding CSPRNG draw here means the machine is broken anyway
        }

        [Fact]
        public void Clear_InvalidatesEverything()
        {
            var tokens = new RejoinTokens(8);
            ulong t = tokens.Mint(5);
            tokens.Clear();
            Assert.False(tokens.Verify(5, t));
        }

        [Fact]
        public void TokensAreNeverZero()
        {
            var tokens = new RejoinTokens(8);
            for (int i = 0; i < 100; i++) Assert.NotEqual(0UL, tokens.Mint(0));
        }
    }
}
