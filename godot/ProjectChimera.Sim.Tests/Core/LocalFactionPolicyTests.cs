#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 9.5 — the pure rule that resolves the presentation layer's "local faction". Proves the offline/spectator
    /// clamp to <see cref="Faction.Player1"/> (why a stale <c>LockstepManager.LocalFaction</c> from a prior online/
    /// spectate match never leaks into a later offline F5 playtest) and that an online player keeps its assigned
    /// faction. The mixed offline rows kill the mutation that drops the <c>isOnline</c> guard; the spectator row kills
    /// the mutation that drops the <c>!isSpectator</c> guard.
    /// </summary>
    public class LocalFactionPolicyTests
    {
        [Theory]
        // Offline ⇒ always Player1, regardless of a stale localFaction or spectator flag.
        [InlineData(false, false, Faction.Player1, Faction.Player1)]
        [InlineData(false, false, Faction.Player2, Faction.Player1)]
        [InlineData(false, true,  Faction.Neutral, Faction.Player1)]
        [InlineData(false, false, Faction.Player8, Faction.Player1)]
        // Online player ⇒ the assigned faction.
        [InlineData(true,  false, Faction.Player1, Faction.Player1)]
        [InlineData(true,  false, Faction.Player2, Faction.Player2)]
        // Online spectator ⇒ Player1 (the reveal-all reference viewer), never Neutral.
        [InlineData(true,  true,  Faction.Neutral, Faction.Player1)]
        public void Effective_ResolvesOfflineAndSpectatorToPlayer1_OnlinePlayerToAssigned(
            bool isOnline, bool isSpectator, Faction localFaction, Faction expected)
        {
            Assert.Equal(expected, LocalFactionPolicy.Effective(isOnline, isSpectator, localFaction));
        }
    }
}
