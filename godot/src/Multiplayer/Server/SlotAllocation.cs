#nullable enable

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>The role a transport slot plays in a match (Story 9.7 / SD-9).</summary>
    public enum SlotRole
    {
        /// <summary>A live player (slot index &lt; the match's player count).</summary>
        Player,
        /// <summary>An observer (player count &lt;= slot index &lt; the slot ceiling).</summary>
        Spectator,
        /// <summary>No free slot — the connecting peer is rejected (slot index &gt;= the ceiling, or negative).</summary>
        Rejected,
    }

    /// <summary>
    /// Story 9.7 (SD-9) — the Godot-free, Tier-1-testable player/spectator slot classifier. Replaces the fixed
    /// 2-players/2-spectators framing (the old <c>slot &gt;= MAX_PLAYERS</c> literal scattered across the transport
    /// + server) with a DYNAMIC split that is a function of the per-match player count: the first
    /// <c>playerCount</c> slots are Players, the remaining slots up to <c>slotCeiling</c> are Spectators, and
    /// anything at or beyond the ceiling is Rejected (no free slot). Slot identity itself stays
    /// transport-authoritative — this only decides the ROLE of an already-assigned accept-slot, never invents one.
    /// </summary>
    public static class SlotAllocation
    {
        /// <summary>
        /// Classify accept-slot <paramref name="slot"/> given the match's <paramref name="playerCount"/> and the
        /// transport's <paramref name="slotCeiling"/> (players + spectator headroom):
        ///   • <see cref="SlotRole.Player"/> when 0 &lt;= slot &lt; playerCount;
        ///   • <see cref="SlotRole.Spectator"/> when playerCount &lt;= slot &lt; slotCeiling;
        ///   • <see cref="SlotRole.Rejected"/> when slot &lt; 0 or slot &gt;= slotCeiling (no free slot).
        /// The split is dynamic per <paramref name="playerCount"/>, not a hard 2/2 partition.
        /// </summary>
        public static SlotRole Classify(int slot, int playerCount, int slotCeiling)
        {
            if (slot < 0 || slot >= slotCeiling) return SlotRole.Rejected;
            return slot < playerCount ? SlotRole.Player : SlotRole.Spectator;
        }
    }
}
