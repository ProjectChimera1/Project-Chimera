#nullable enable
using ProjectChimera.Core; // FactionRegistry

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 9.7 (P3/P5) — the Godot-free, Tier-1-testable single source of truth for player-count clamps. Two
    /// distinct ceilings:
    ///   • <see cref="MpSeatCeiling"/> — how many players the TRANSPORT can seat (and therefore the max the
    ///     matchmaker may queue/group and the lobby may expect). <c>ServerTransport.MAX_PLAYERS</c> pins to THIS
    ///     constant, so the two can never drift.
    ///   • <see cref="FactionRegistry.PLAYER_COUNT"/> — how many faction slots the SIM can run (offline skirmish +
    ///     the checksum span). Larger than the MP seat ceiling on purpose: a 5–8-slot scenario is playable offline.
    /// The MP target is clamped to <see cref="MpSeatCeiling"/> (never queue/group/expect more players than the
    /// transport seats); the SIM active-count derives from the FULL scenario slot count (only clamped to the sim
    /// ceiling). Both share the <see cref="MpFloor"/> of 2 (multiplayer needs two players; N=2 stays byte-identical).
    /// </summary>
    public static class PlayerCountPolicy
    {
        /// <summary>The multiplayer floor — a match needs at least two players.</summary>
        public const int MpFloor = 2;

        /// <summary>The transport seat ceiling (verified ship max). <c>ServerTransport.MAX_PLAYERS</c> pins to this;
        /// raising both to 8 is the documented constant bump.</summary>
        public const int MpSeatCeiling = 4;

        /// <summary>
        /// The SIM active-player count for a scenario with <paramref name="rawSlotCount"/> player slots: clamped to
        /// [<see cref="MpFloor"/>, <see cref="FactionRegistry.PLAYER_COUNT"/>]. Fewer than 2 (or a
        /// missing/unparsed scenario) → 2 (byte-identical to the pre-9.7 hardcoded 1v1). This is the ONLY source of
        /// N fed identically to the client <c>FactionRegistry(N)</c> and the server's <c>activeFactionCount</c>.
        /// </summary>
        public static int SimActivePlayers(int rawSlotCount)
            => rawSlotCount < MpFloor ? MpFloor
             : rawSlotCount > FactionRegistry.PLAYER_COUNT ? FactionRegistry.PLAYER_COUNT
             : rawSlotCount;

        /// <summary>
        /// The MATCHMAKER / LOBBY expected-player target for a scenario with <paramref name="rawSlotCount"/> player
        /// slots: clamped to [<see cref="MpFloor"/>, <see cref="MpSeatCeiling"/>] — never queue/group/expect more
        /// players than the transport can seat.
        /// </summary>
        public static int MpTargetPlayers(int rawSlotCount)
            => rawSlotCount < MpFloor ? MpFloor
             : rawSlotCount > MpSeatCeiling ? MpSeatCeiling
             : rawSlotCount;
    }
}
