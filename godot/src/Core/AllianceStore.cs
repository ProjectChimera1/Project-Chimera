#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 7.12 — the sim-owned <b>alliance mask</b>: a per-faction team id that generalizes win resolution from
    /// the 2-faction "the single other faction" assumption to N-faction, team-aware last-team-standing. Built on the
    /// <see cref="WinStateStore"/>/<see cref="ResearchStore"/> per-faction Struct-of-Arrays shape (indices 0-4, cast
    /// from <see cref="Faction"/>, sized <see cref="FactionRegistry.SLOT_DEFINITIONS_SIZE"/>), integer-only, pure sim
    /// (no Godot / fractional-primitive / wall-clock). Owned as a <see cref="ProjectChimera.Core.Sim.SimulationHost"/> property and
    /// reset from its <c>ClearForReset</c>.
    ///
    /// <para><b>Default = FFA (free-for-all, teams of 1):</b> <c>TeamId[f] == f</c> — every faction is its own team
    /// (team id = faction slot index). Two factions are allied iff they share a team id; a faction is always allied
    /// with itself. <see cref="Faction.Neutral"/> (slot 0) is never a playable team member and never wins/loses (the
    /// win system iterates <see cref="FactionRegistry.ActiveFactions"/>, which excludes Neutral).</para>
    ///
    /// <para>The mask is immutable per match in 1.0 (no in-match diplomacy). It is populated from the lobby by
    /// Story 9.15 later; this story ships the model + the FFA default only. It folds into
    /// <c>SimChecksum</c> (v20) immediately after the <see cref="WinStateStore"/> block: one team-id
    /// <c>Mix</c> per active faction, in <see cref="FactionRegistry.ActiveFactions"/> order. Folding a static array
    /// into the per-tick checksum is the mechanism that forces peers to agree on teams — a peer with a different mask
    /// resolves victory differently and must desync detectably. A <b>null</b> store folds byte-identically to this
    /// default-FFA store (<c>Mix((int)f)</c> per active faction), mirroring the 7.11 null≡empty fold discipline.</para>
    /// </summary>
    public sealed class AllianceStore
    {
        private const int FACTION_COUNT = FactionRegistry.SLOT_DEFINITIONS_SIZE; // 9; indices 0-8 (0 = Neutral, unused)

        /// <summary>Per-faction team id (indexed by <c>(int)Faction</c>). FFA default: <c>TeamId[f] == f</c> (each
        /// faction its own team). Two factions are allied iff their team ids are equal. Folded into
        /// <c>SimChecksum</c> (v20) via <see cref="TeamOf"/> in <see cref="FactionRegistry.ActiveFactions"/> order.
        /// Public (like <see cref="WinStateStore.KothHoldTicks"/>) so Story 9.15's lobby wiring can seed it and the
        /// coverage guard can mutate a slot; immutable per match in 1.0.
        /// <para><b>Domain INVARIANT — team ids MUST be in <c>[0, FACTION_COUNT)</c>.</b> The FFA default (team id =
        /// faction slot) always satisfies this. Story 9.15's lobby seeding MUST map its team choice into this range
        /// (use a faction slot as the team id, e.g. the lowest-slot member of the team), NOT an arbitrary team ordinal.
        /// <c>WinConditionSystem</c>'s team scans (<c>CountLiveTeams</c>/<c>UpdateKothCounters</c>, backed by a
        /// <c>FACTION_COUNT</c>-sized scratch) SILENTLY SKIP an out-of-range team id — an alive faction so seeded would
        /// drop out of the live-team count and mis-resolve victory. Seed within domain to avoid this.</para></summary>
        public readonly int[] TeamId;

        public AllianceStore()
        {
            TeamId = new int[FACTION_COUNT];
            RestoreFfa();
        }

        // FFA default: every faction its own team (team id == faction slot index).
        private void RestoreFfa()
        {
            for (int f = 0; f < FACTION_COUNT; f++) TeamId[f] = f;
        }

        /// <summary>The team id of <paramref name="faction"/> (FFA default: <c>(int)faction</c>). An out-of-range
        /// faction returns its raw slot index (defensive — mirrors the rest of the sim's bounds posture).</summary>
        public int TeamOf(Faction faction)
        {
            int idx = (int)faction;
            return (idx >= 0 && idx < FACTION_COUNT) ? TeamId[idx] : idx;
        }

        /// <summary>True iff <paramref name="a"/> and <paramref name="b"/> are on the same team. A faction is always
        /// allied with itself (even Neutral). Two distinct factions are allied iff they share a team id.</summary>
        public bool AreAllied(Faction a, Faction b)
        {
            if (a == b) return true;              // always allied with self
            return TeamOf(a) == TeamOf(b);
        }

        /// <summary>Story 3.10-style Edit↔Play reset support: restore the FFA default. A cleared store is
        /// byte-for-byte equal to a freshly-constructed one.</summary>
        public void Clear() => RestoreFfa();
    }
}
