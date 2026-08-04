#nullable enable
using System;
using ProjectChimera.Core; // Faction, FactionRegistry

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// DW-412 — the authoritative slot→faction table's CONSTRUCTOR plus its fail-closed well-formedness guard.
    ///
    /// <para>That table (the dedicated server's <c>SLOT_FACTION</c>) is the only thing standing between a transport
    /// slot and a faction, and <see cref="DropCoordinator.FactionToSlot"/> reads it BACKWARDS: it resolves an inbound
    /// <c>DropAck</c>'s (untrusted) faction byte to a slot by scanning the table and returning the FIRST match. That
    /// scan is a sound inverse only while the table is INJECTIVE — no two player slots naming the same faction. A
    /// duplicate would resolve a survivor's ACK to the WRONG slot, and <see cref="DropController.RecordAck"/>'s
    /// <c>droppedSlot == _pendingDroppedSlot</c> check would then silently DISCARD it: the freeze never commits, the
    /// frozen-slot injection never starts, and the merged fan-in stalls on the departed peer forever — a hang with no
    /// error anywhere. A <see cref="Faction.Neutral"/> (or out-of-enum) entry is the same defect from the other side:
    /// it would make a garbage/unknown faction byte resolve to a REAL slot instead of the −1 "unknown" sentinel.</para>
    ///
    /// <para>The mapping is injective by construction today (<c>FactionRegistry.ToFaction(i) == (Faction)(i + 1)</c>),
    /// so the mis-map is currently unreachable — but nothing PINNED it, so a future hand-built, roster-sourced, or
    /// re-ordered table could reopen that stall with a green suite. This type is that pin: the table is validated at
    /// the moment it is built, and again at the moment <see cref="DropCoordinator"/> takes ownership of one.</para>
    ///
    /// <para>Godot-free / Tier-1-testable. Pure slot bookkeeping — off-tick, never folded into <c>SimChecksum</c>.</para>
    /// </summary>
    public static class SlotFactionTable
    {
        /// <summary>
        /// Build the authoritative slot→faction table for <paramref name="slotCount"/> player slots
        /// (<c>slot s → FactionRegistry.ToFaction(s)</c>) and VALIDATE it before handing it out, so the mapping can
        /// never ship non-injective — not even if <see cref="FactionRegistry.ToFaction"/> is ever redefined.
        /// </summary>
        /// <param name="slotCount">Player slots to cover, in [1, <see cref="FactionRegistry.PLAYER_COUNT"/>].</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is outside
        /// [1, <see cref="FactionRegistry.PLAYER_COUNT"/>] — past that the derived value leaves the
        /// <see cref="Faction"/> enum entirely and no valid table exists.</exception>
        /// <exception cref="ArgumentException">The derived table is not well-formed (see <see cref="TryValidate"/>).</exception>
        public static Faction[] Build(int slotCount)
        {
            if (slotCount < 1 || slotCount > FactionRegistry.PLAYER_COUNT)
                throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount,
                    $"slotCount must be in [1, {FactionRegistry.PLAYER_COUNT}] — past that FactionRegistry.ToFaction " +
                    "leaves the Faction enum, so no well-formed slot->faction table exists.");

            var table = new Faction[slotCount];
            for (int s = 0; s < slotCount; s++) table[s] = FactionRegistry.ToFaction(s);
            AssertValid(table, slotCount, nameof(slotCount));
            return table;
        }

        /// <summary>
        /// Pure predicate — are the first <paramref name="count"/> entries of <paramref name="slotFaction"/> a valid
        /// slot→faction mapping? Two rules, both required for <see cref="DropCoordinator.FactionToSlot"/>'s
        /// first-match scan to be a true inverse:
        /// <list type="bullet">
        ///   <item>every entry names a REAL player faction (Player1..Player{<see cref="FactionRegistry.PLAYER_COUNT"/>}) —
        ///   never <see cref="Faction.Neutral"/> and never an out-of-enum byte; and</item>
        ///   <item>no faction is named by two slots (INJECTIVE).</item>
        /// </list>
        /// Entries at or past <paramref name="count"/> are NOT inspected — the server's table is sized to the
        /// transport's seat ceiling while a given match only quorums over its own connected player prefix.
        /// </summary>
        /// <param name="slotFaction">The slot→faction table (may be longer than <paramref name="count"/>).</param>
        /// <param name="count">How many leading slots this match actually uses.</param>
        /// <param name="error">On <c>false</c>, a message naming the offending slot(s); <c>null</c> on success.</param>
        public static bool TryValidate(Faction[]? slotFaction, int count, out string? error)
        {
            error = null;

            if (count < 1)
            {
                error = $"count must be >= 1 (got {count}).";
                return false;
            }
            if (slotFaction == null || slotFaction.Length < count)
            {
                error = $"slotFaction must cover {count} slots (got {(slotFaction?.Length ?? 0)}).";
                return false;
            }

            // Dense first-slot-per-faction table (index = (int)faction), not a Dictionary — no hash enumeration,
            // allocation-light, and analyzer-safe. Mirrors AssignedRoster.TryFreeze's dense `seen` bool[].
            var firstSlotFor = new int[FactionRegistry.PLAYER_COUNT + 1]; // index 0 = Neutral, never a valid entry
            for (int i = 0; i < firstSlotFor.Length; i++) firstSlotFor[i] = -1;

            for (int s = 0; s < count; s++)
            {
                int id = (int)slotFaction[s];
                if (id < 1 || id > FactionRegistry.PLAYER_COUNT)
                {
                    error = $"slot {s} maps to {slotFaction[s]}, which is not a player faction " +
                            $"(expected Player1..Player{FactionRegistry.PLAYER_COUNT}) — an unknown faction byte " +
                            "would then resolve to a real slot instead of the -1 sentinel.";
                    return false;
                }
                if (firstSlotFor[id] >= 0)
                {
                    error = $"faction {slotFaction[s]} is mapped by BOTH slot {firstSlotFor[id]} and slot {s} — the " +
                            "slot->faction table must be injective, or a DropAck resolves to the wrong slot and is " +
                            "silently discarded (the freeze never commits and the merged fan-in stalls forever).";
                    return false;
                }
                firstSlotFor[id] = s;
            }

            return true;
        }

        /// <summary>Fail-closed wrapper over <see cref="TryValidate"/> — throws <see cref="ArgumentException"/>
        /// carrying the offending slot(s) rather than letting a mis-mapping table into the drop machinery.</summary>
        /// <param name="slotFaction">The slot→faction table to validate.</param>
        /// <param name="count">How many leading slots this match actually uses.</param>
        /// <param name="paramName">The caller's parameter name, for the thrown exception.</param>
        public static void AssertValid(Faction[]? slotFaction, int count, string paramName)
        {
            if (!TryValidate(slotFaction, count, out string? error))
                throw new ArgumentException(error, paramName);
        }
    }
}
