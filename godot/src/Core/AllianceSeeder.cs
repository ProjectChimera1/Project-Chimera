#nullable enable
using System.Collections.Generic; // DW-442 — the diagnostic channel's List<string>
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

        /// <summary>
        /// DW-442 — the seeder's own, human-readable account of what <see cref="ComputeTeamIds(ScenarioData?, int[])"/>
        /// SILENTLY did to the authored teams, so an authoring surface can surface it instead of shipping a map whose
        /// alliances do not match its JSON. Reported in ascending declaration order (deterministic), one line per
        /// finding:
        /// <list type="number">
        /// <item><b>Out-of-range member</b> — a <c>team&gt;0</c> slot whose faction index falls outside
        /// <c>[1,FACTION_COUNT)</c>. The mapping <c>continue</c>s past it, so that slot is never written into the mask
        /// AND never lowers its team's canonical id: it plays FFA while the file claims a team. Pre-DW-442 this was
        /// dropped with no log at all (unlike <c>ScenarioApplier</c>, which warns on its own out-of-range drops).</item>
        /// <item><b>Duplicate <c>.Slot</c></b> — two entries naming one faction both write
        /// <c>teamIdByFaction[faction]</c> and the LAST one wins, so the seeded mask depends on <c>PlayerSlots</c>
        /// ORDER rather than on the declarations alone. (<c>ScenarioValidator.Validate</c> hard-rejects a duplicate
        /// slot, so this fires only on a model that never passed that gate — the lobby's own
        /// <c>ComputeTeamIds</c> call, or an editor model mid-edit.)</item>
        /// <item><b>Inert team-of-one</b> — a positive ordinal held by exactly one FACTION. Its canonical id is its own
        /// faction slot, i.e. byte-identical to the FFA default: the team buys that slot no ally whatsoever.</item>
        /// </list>
        ///
        /// <para>Pure and read-only: it allocates its own list, mutates nothing, and is NEVER called from
        /// <see cref="Seed"/> or <see cref="ComputeTeamIds(ScenarioData?, int[])"/> — the mapping those write is
        /// untouched by this method's existence, so no checksum, golden or agreement hash can move. Null-element safe
        /// (<c>ScenarioValidator</c> locates a null entry; this must never NRE ahead of it). Not a per-tick path.</para>
        /// </summary>
        /// <param name="model">The scenario whose per-slot teams to account for (null / no slots ⇒ empty).</param>
        /// <returns>Every diagnostic, ascending by slot index; empty when the authored teams map exactly as written.</returns>
        public static IReadOnlyList<string> CollectDiagnostics(ScenarioData? model)
        {
            var diagnostics = new List<string>();
            var slots = model?.PlayerSlots;
            if (slots == null) return diagnostics;

            for (int i = 0; i < slots.Length; i++)
            {
                ScenarioPlayerSlot slot = slots[i];
                if (slot is null) continue; // a null entry is ScenarioValidator's to locate — never NRE here
                int team = slot.Team;
                int faction = slot.Slot + 1;              // slot 0 → Player1 == faction slot index 1
                bool inRange = faction > 0 && faction < FACTION_COUNT;

                // (1) OUT-OF-RANGE MEMBER — the mapping's `continue` at the faction-range guard.
                if (team > 0 && !inRange)
                {
                    diagnostics.Add(
                        $"player_slots[{i}].slot={slot.Slot} is outside the engine faction range " +
                        $"[0,{FACTION_COUNT - 1}), so its team={team} membership is dropped by the alliance seeder — " +
                        "that slot ends up allied with nobody.");
                    continue; // nothing further is knowable about a slot the mapping never touches
                }

                if (!inRange) continue; // an out-of-range FFA slot is inert either way — nothing was dropped

                // (2) DUPLICATE .Slot — last-write-wins, so the seeded mask depends on declaration ORDER. Only
                //     reportable when at least one of the two entries actually carries a team: with both at 0 the
                //     mapping writes nothing at all, so there is no seeder behavior to account for.
                int prev = -1;
                for (int j = 0; j < i; j++)
                {
                    ScenarioPlayerSlot other = slots[j];
                    if (other is not null && other.Slot == slot.Slot) prev = j; // the LAST earlier duplicate
                }
                if (prev >= 0 && (team > 0 || slots[prev].Team > 0))
                    diagnostics.Add(
                        $"player_slots[{i}] and player_slots[{prev}] both declare slot={slot.Slot}; the alliance " +
                        $"seeder is last-write-wins, so team={team} overrides team={slots[prev].Team} — reordering " +
                        "player_slots would seed a different alliance mask from the same declarations.");

                // (3) INERT TEAM-OF-ONE — a positive ordinal no other FACTION shares seeds the FFA default.
                if (team > 0 && CountTeamFactions(slots, team) == 1)
                    diagnostics.Add(
                        $"player_slots[{i}] (slot={slot.Slot}) is the only member of team={team}; a team of one has " +
                        "no allies, so it is seeded exactly like an unassigned (team=0 / FFA) slot.");
            }

            return diagnostics;
        }

        /// <summary>
        /// DW-442 — how many distinct in-range FACTIONS carry <paramref name="team"/>. Counts factions, not entries:
        /// a duplicated <c>.Slot</c> is ONE faction listed twice and must not read as a two-member team (which would
        /// hide the inert-team-of-one finding behind an authoring mistake). Allocation-free; slot arrays are capped at
        /// <c>PLAYER_COUNT</c>, and this is a diagnostic path, never a per-tick one.
        /// </summary>
        private static int CountTeamFactions(ScenarioPlayerSlot[] slots, int team)
        {
            int count = 0;
            for (int j = 0; j < slots.Length; j++)
            {
                ScenarioPlayerSlot other = slots[j];
                if (other is null || other.Team != team) continue;
                int f = other.Slot + 1;
                if (f <= 0 || f >= FACTION_COUNT) continue; // the mapping skips these, so they are not members

                bool alreadyCounted = false;
                for (int k = 0; k < j; k++)
                {
                    ScenarioPlayerSlot earlier = slots[k];
                    if (earlier is not null && earlier.Team == team && earlier.Slot == other.Slot)
                    {
                        alreadyCounted = true; // same faction, already tallied at its first declaration
                        break;
                    }
                }
                if (!alreadyCounted) count++;
            }
            return count;
        }
    }
}
