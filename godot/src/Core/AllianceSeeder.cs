#nullable enable
using ProjectChimera.Core.Definitions; // ScenarioData / ScenarioPlayerSlot

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 9.14 — the pure, Godot-free mapping from a scenario's per-slot TEAM ordinals to the sim-owned
    /// <see cref="AllianceStore"/> team-id mask. This is the seeding step Story 7.12's alliance model was built for
    /// (its doc's "seeded by 9.15 later" refers to THIS story — the numbering drifted).
    ///
    /// <para><b>Canonical team-id encoding (the load-bearing invariant).</b> Lobby/scenario teams are ordinals
    /// (1,2,…), but <see cref="AllianceStore.TeamId"/> values MUST be valid faction slots in <c>[1, FACTION_COUNT)</c>
    /// (<see cref="WinConditionSystem"/>'s team scans SILENTLY DROP an out-of-range team id and mis-resolve victory).
    /// Each team maps to the <b>lowest faction slot among its members</b>: e.g. slots {1,2}=teamA → id 1,
    /// {3,4}=teamB → id 3. A <c>Team==0</c> (unassigned) slot keeps its own-faction id — degenerating to the FFA
    /// default (<c>TeamId[f]==f</c>), byte-identical to pre-9.14.</para>
    ///
    /// <para>Integer-only, deterministic (the min is computed over ASCENDING active factions, never a
    /// Dictionary-enumeration that could reorder), and it never touches Neutral (index 0). Idempotent: it restores
    /// FFA first, so a re-apply always re-seeds from a clean default.</para>
    /// </summary>
    public static class AllianceSeeder
    {
        private const int FACTION_COUNT = FactionRegistry.SLOT_DEFINITIONS_SIZE; // 9; indices 0-8 (0 = Neutral)

        /// <summary>
        /// Seed <paramref name="alliances"/> from <paramref name="model"/>'s per-slot teams: restore FFA, then for
        /// each active slot whose <see cref="ScenarioPlayerSlot.Team"/> is positive, set its faction's team id to the
        /// canonical (lowest-faction-slot) id of its team group. FFA (all <c>Team==0</c>, or a null/empty model) leaves
        /// the mask at the default <c>TeamId[f]==f</c>.
        /// </summary>
        public static void Seed(AllianceStore alliances, ScenarioData? model)
        {
            alliances.Clear(); // restore FFA (TeamId[f]==f) so a re-seed is non-additive
            ComputeTeamIds(model, alliances.TeamId);
        }

        /// <summary>
        /// Allocate a fresh <c>FACTION_COUNT</c>-sized canonical-team-id array (FFA default <c>teamIdByFaction[f]==f</c>,
        /// then teamed members overwritten) — the SAME mapping <see cref="Seed"/> writes into the sim mask. Used by the
        /// match-agreement handshake (fold the canonical ids keyed by faction, so the hash validates EXACTLY the mask
        /// the sim seeds — a reordered/sparse <c>PlayerSlots</c> can never make the two disagree) and by the lobby glyph.
        /// Not a per-tick path (one allocation at handshake / lobby-rebuild time) — Godot-free, integer-only.
        /// </summary>
        public static int[] ComputeTeamIds(ScenarioData? model)
        {
            var teamIdByFaction = new int[FACTION_COUNT];
            for (int f = 0; f < FACTION_COUNT; f++) teamIdByFaction[f] = f; // FFA default
            ComputeTeamIds(model, teamIdByFaction);
            return teamIdByFaction;
        }

        /// <summary>
        /// Fill <paramref name="teamIdByFaction"/> (indexed by <c>(int)Faction</c>, sized at least
        /// <see cref="FACTION_COUNT"/>) with the canonical team id per faction — the SAME mapping <see cref="Seed"/>
        /// writes, exposed so the lobby can render a per-slot team glyph keyed by the canonical id. Assumes the array
        /// already carries the FFA default (<c>teamIdByFaction[f]==f</c>); it overwrites only teamed members. Slots
        /// out of the playable range, or with <c>Team&lt;=0</c>, are left untouched.
        /// </summary>
        public static void ComputeTeamIds(ScenarioData? model, int[] teamIdByFaction)
        {
            var slots = model?.PlayerSlots;
            if (slots == null) return;

            for (int i = 0; i < slots.Length; i++)
            {
                ScenarioPlayerSlot slot = slots[i];
                int team = slot.Team;
                if (team <= 0) continue; // FFA / unassigned — keep the own-faction (self-team) default

                int faction = slot.Slot + 1; // slot 0 → Player1 == faction slot index 1
                if (faction <= 0 || faction >= FACTION_COUNT) continue; // never index Neutral / out of range

                // Canonical id = the lowest faction-slot index among this team's members. Scan all slots in ascending
                // order (deterministic; n <= PLAYER_COUNT), keeping the min valid member faction. Guaranteed to find
                // at least this slot itself, so the id is always a valid in-range faction slot.
                int canonical = faction;
                for (int j = 0; j < slots.Length; j++)
                {
                    ScenarioPlayerSlot other = slots[j];
                    if (other.Team != team) continue;
                    int otherFaction = other.Slot + 1;
                    if (otherFaction <= 0 || otherFaction >= FACTION_COUNT) continue;
                    if (otherFaction < canonical) canonical = otherFaction;
                }

                teamIdByFaction[faction] = canonical;
            }
        }
    }
}
