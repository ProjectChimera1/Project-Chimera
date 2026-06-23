#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Single source of truth for faction-count / slot knowledge in the sim layer.
    /// Pure C# — no Godot. Owns the forward N-player constants and the one (Faction)(slot+1) cast.
    ///
    /// Every checksum, slot loop, and new subsystem iterates factions through THIS type
    /// (its <see cref="ActiveFactions"/> list or <see cref="ToFaction"/>), never a bare FACTION_COUNT
    /// literal. This localizes the faction-count knowledge that was previously scattered across the sim
    /// (AR-3 in game-architecture.md).
    /// </summary>
    public sealed class FactionRegistry
    {
        /// <summary>Playable factions, excluding Neutral. Forward target (ship ceiling 4-now / 8-fast-follow).</summary>
        public const int PLAYER_COUNT = 8;

        /// <summary>Per-faction array size incl. Neutral (slot 0). Forward target.
        /// NOTE: distinct from the as-built ResourceStore/MatchStats FACTION_COUNT=5 (current enum cardinality,
        /// raised by Story 9.2). New slot loops use PLAYER_COUNT / ActiveFactions — never a bare FACTION_COUNT.</summary>
        public const int FACTION_ARRAY_SIZE = 9;

        /// <summary>The ONE place the (Faction)(slot+1) offset lives. slot is 0-based; slot 0 → Player1.</summary>
        public static Faction ToFaction(int slot) => (Faction)(slot + 1);

        private readonly Faction[] _activeFactions; // ascending, deterministic iteration

        /// <summary>
        /// Active factions = Player1..Player{activePlayerCount}, ascending (1.0: contiguous player slots).
        /// </summary>
        /// <param name="activePlayerCount">Number of playable factions in THIS match, in [1, PLAYER_COUNT].</param>
        public FactionRegistry(int activePlayerCount)
        {
            if (activePlayerCount < 1 || activePlayerCount > PLAYER_COUNT)
                throw new System.ArgumentOutOfRangeException(nameof(activePlayerCount),
                    activePlayerCount, $"activePlayerCount must be in [1, {PLAYER_COUNT}].");

            _activeFactions = new Faction[activePlayerCount];
            for (int i = 0; i < activePlayerCount; i++)
                _activeFactions[i] = ToFaction(i);
            // TODO(5.1): hold per-slot FactionDefinition[] and derive ActiveFactions from assigned slots.
        }

        /// <summary>The active factions of this match, ascending. Iterate this — never a 0..FACTION_COUNT loop.</summary>
        public IReadOnlyList<Faction> ActiveFactions => _activeFactions;

        /// <summary>Number of active (playable) factions in this match.</summary>
        public int ActiveCount => _activeFactions.Length;
    }
}
