#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;

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

        /// <summary>Per-slot <see cref="SlotDefinitions"/> array size (AR-3 / Story 5.1).
        /// Deliberately matches the AS-BUILT ResourceStore/MatchStats/BuildingSystem/ResearchSystem
        /// FACTION_COUNT=5 (current Faction enum cardinality: Neutral+Player1..4) — NOT the forward
        /// FACTION_ARRAY_SIZE (9) above. ScenarioApplier.InFactionRange derives its bounds-safety from this
        /// array's .Length matching those still-5-sized arrays; widening only this one would let slots 4-7
        /// pass InFactionRange and then throw IndexOutOfRangeException against them. Raising this in lockstep
        /// with the other FACTION_COUNT=5 arrays is Story 9.2's job, not this one's.</summary>
        public const int SLOT_DEFINITIONS_SIZE = 5;

        /// <summary>Per-slot <see cref="FactionDefinition"/> lookup, indexed by <c>(int)Faction</c>. The registry
        /// owns the storage; MainScene/ServerBootstrap's Godot-edge composition roots populate it after resolving
        /// res:// paths (the registry itself never touches res:// or does file I/O — see class doc). Unassigned
        /// slots stay <c>null</c>. Use <see cref="GetSlotDefinition"/> for a bounds-checked read (AC3).</summary>
        public FactionDefinition?[] SlotDefinitions { get; } = new FactionDefinition?[SLOT_DEFINITIONS_SIZE];

        /// <summary>
        /// Bounds-checked per-slot lookup (AC3, epics.md Story 5.1 verbatim): returns the assigned
        /// <see cref="FactionDefinition"/> for an in-range, populated slot; returns <c>null</c> — never throws —
        /// for both an unassigned in-range slot and a genuinely out-of-range <paramref name="faction"/> value
        /// (any <c>(int)faction</c> outside <c>[0, SLOT_DEFINITIONS_SIZE)</c>). Additive, read-only API; existing
        /// direct <see cref="SlotDefinitions"/> consumers (already bounds-guarded by their own callers, e.g.
        /// ScenarioApplier.InFactionRange) are not required to migrate to it.
        /// </summary>
        public FactionDefinition? GetSlotDefinition(Faction faction)
        {
            int idx = (int)faction;
            return idx >= 0 && idx < SlotDefinitions.Length ? SlotDefinitions[idx] : null;
        }

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
            // ActiveFactions intentionally stays activePlayerCount-driven, NOT derived from SlotDefinitions
            // occupancy: dozens of call sites (goldens, unit tests, ServerBootstrap) construct
            // new FactionRegistry(N) purely for checksum iteration span and never populate SlotDefinitions —
            // coupling the two would silently empty ActiveFactions for all of them (see Design Notes,
            // spec-5-1-factionregistry-canonical-faction-slot-constants-ar-3.md).
        }

        /// <summary>The active factions of this match, ascending. Iterate this — never a 0..FACTION_COUNT loop.</summary>
        public IReadOnlyList<Faction> ActiveFactions => _activeFactions;

        /// <summary>Number of active (playable) factions in this match.</summary>
        public int ActiveCount => _activeFactions.Length;
    }
}
