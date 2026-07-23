#nullable enable
using System;
using ProjectChimera.Core; // Faction

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.3 (Patches E2/E3) — the Godot-free lobby + chat policy decisions the <c>DedicatedServer</c> node
    /// delegates to, extracted so they are Tier-1 unit-testable (the node itself <c>using Godot;</c> and is excluded
    /// from the sim assembly). Pure functions over primitives + delegate probes — no transport, no Godot types.
    /// </summary>
    public static class ServerLobbyPolicy
    {
        /// <summary>
        /// Story 9.3 (chat-spoof fix) — the authoritative chat faction for a sender at <paramref name="slot"/>: a
        /// player slot (0..<paramref name="maxPlayers"/>-1) stamps its own transport-authoritative faction; a
        /// spectator slot (&gt;= <paramref name="maxPlayers"/>) or an out-of-range slot stamps
        /// <see cref="Faction.Neutral"/>. Never trusts a client-supplied faction byte.
        /// </summary>
        public static Faction StampChatFaction(int slot, Faction[] slotFaction, int maxPlayers)
            => slot >= 0 && slot < maxPlayers ? slotFaction[slot] : Faction.Neutral;

        /// <summary>
        /// Count connected PLAYER slots — slots 0..<paramref name="maxPlayers"/>-1 only, so a connected spectator
        /// (slot &gt;= <paramref name="maxPlayers"/>) is never counted toward quorum (D6).
        /// </summary>
        public static int CountConnectedPlayers(Func<int, bool> isConnected, int maxPlayers)
        {
            int count = 0;
            for (int s = 0; s < maxPlayers; s++)
                if (isConnected(s)) count++;
            return count;
        }

        /// <summary>Count connected PLAYER slots that have readied (spectators never send Ready and are excluded).</summary>
        public static int CountReadyPlayers(Func<int, bool> isConnected, Func<int, bool> isReady, int maxPlayers)
        {
            int count = 0;
            for (int s = 0; s < maxPlayers; s++)
                if (isConnected(s) && isReady(s)) count++;
            return count;
        }

        /// <summary>
        /// Story 9.3 (N-shaped count machine) — the match starts iff exactly <paramref name="expected"/> players are
        /// BOTH connected AND ready (no <c>_ready[0] &amp;&amp; _ready[1]</c> two-slot literal). Requires a positive
        /// expected count so a zero-player lobby never "starts".
        /// </summary>
        public static bool ShouldStart(int connectedPlayers, int readyPlayers, int expected)
            => expected > 0 && connectedPlayers == expected && readyPlayers == expected;
    }
}
