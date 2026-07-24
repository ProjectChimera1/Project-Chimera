#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, FactionRegistry

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.7 (AC1) — the server-authoritative, frozen slot→faction roster snapshot taken at match start. The
    /// single source downstream code faction-stamps from: <c>slot → FactionRegistry.ToFaction(slot)</c>, built from
    /// the transport arrival order (the accept-slots), NEVER a client-supplied faction byte and never the live
    /// <c>_slots</c> array (which can change mid-match). Replaces the deleted lexicographic Nakama hint.
    ///
    /// Godot-free / Tier-1-testable. Frozen: constructed once via <see cref="TryFreeze"/> and thereafter read-only.
    /// Fail-closed: a duplicate slot, an out-of-range slot, or a missing slot (the arrival set does not cover
    /// exactly slots 0..playerCount-1) rejects the build (the caller HALTs rather than starting a match whose slot
    /// assignment it cannot attest).
    /// </summary>
    public sealed class AssignedRoster
    {
        /// <summary>Number of players in this frozen match (the sub-bundle fan-in width).</summary>
        public int PlayerCount { get; }

        private readonly Faction[] _slotFactions; // index = slot (0..PlayerCount-1) → authoritative faction

        private AssignedRoster(int playerCount, Faction[] slotFactions)
        {
            PlayerCount   = playerCount;
            _slotFactions = slotFactions;
        }

        /// <summary>The authoritative faction for a player slot; <see cref="Faction.Neutral"/> for an out-of-range slot.</summary>
        public Faction FactionForSlot(int slot)
            => (uint)slot < (uint)PlayerCount ? _slotFactions[slot] : Faction.Neutral;

        /// <summary>A fresh copy of the slot→faction array (indexed by slot, length <see cref="PlayerCount"/>) — the
        /// exact <c>slotFaction</c> array the <c>MergedTickBuilder</c>/<c>DelayController</c> consume.</summary>
        public Faction[] SlotFactions() => (Faction[])_slotFactions.Clone();

        /// <summary>
        /// Freeze a roster from the connected player slots in transport arrival order. Succeeds only when
        /// <paramref name="arrivalSlots"/> is exactly <paramref name="playerCount"/> DISTINCT slots covering
        /// 0..playerCount-1 (any duplicate, out-of-range, or missing slot → reject). On success each slot maps to
        /// <see cref="FactionRegistry.ToFaction"/>(slot).
        /// </summary>
        public static bool TryFreeze(IReadOnlyList<int> arrivalSlots, int playerCount,
                                     out AssignedRoster? roster, out string? error)
        {
            roster = null;
            error  = null;

            if (playerCount < 1 || playerCount > FactionRegistry.PLAYER_COUNT)
            {
                error = $"playerCount must be in [1, {FactionRegistry.PLAYER_COUNT}] (got {playerCount}).";
                return false;
            }
            if (arrivalSlots == null || arrivalSlots.Count != playerCount)
            {
                error = $"expected {playerCount} arrival slots, got {(arrivalSlots?.Count ?? 0)}.";
                return false;
            }

            var seen = new bool[playerCount];      // dense bool[] (no Dictionary enumeration) — analyzer-safe
            var factions = new Faction[playerCount];
            foreach (int slot in arrivalSlots)
            {
                if (slot < 0 || slot >= playerCount)
                {
                    error = $"slot {slot} is out of range [0, {playerCount}).";
                    return false;
                }
                if (seen[slot])
                {
                    error = $"duplicate slot {slot} in arrival order.";
                    return false;
                }
                seen[slot]     = true;
                factions[slot] = FactionRegistry.ToFaction(slot);
            }
            // Count == playerCount, all distinct, all in-range ⇒ 0..playerCount-1 are all covered.

            roster = new AssignedRoster(playerCount, factions);
            return true;
        }
    }
}
