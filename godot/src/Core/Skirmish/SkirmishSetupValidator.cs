#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// Story 11.1 — gates the skirmish Launch. Returns ALL located errors (not first-fail, mirroring
    /// <c>UnitDefinitionValidator</c>) so the setup screen can list every problem at once; Launch stays disabled while
    /// any error stands. Godot-free / pure — deterministic in, deterministic out. Encodes the Epic-11.7 honesty limit:
    /// only a configuration the runtime can actually pilot (exactly one human, exactly one AI opponent) is launchable.
    /// </summary>
    public sealed class SkirmishSetupValidator
    {
        /// <summary>Validate <paramref name="setup"/> against the chosen <paramref name="map"/> and the discovered
        /// <paramref name="factions"/>. Returns an empty list when the config is launchable, else every located error
        /// with an actionable message.</summary>
        public IReadOnlyList<string> Validate(SkirmishSetup setup, MapEntry map, IReadOnlyList<FactionEntry> factions)
        {
            var errors = new List<string>();
            IReadOnlyList<SetupSlot> slots = setup.Slots ?? new List<SetupSlot>();

            int humanCount = slots.Count(s => s.Kind == SlotKind.Human);
            int aiCount    = slots.Count(s => s.Kind == SlotKind.Ai);
            int activeCount = humanCount + aiCount;

            // 1) Exactly one local human.
            if (humanCount != 1)
                errors.Add("Exactly one Human slot is required.");

            // 2) At least one opponent.
            if (aiCount == 0)
                errors.Add("At least one AI opponent is required.");

            // 3) Honest runtime limit — the sim pilots a single AI opponent today.
            if (aiCount > 1)
                errors.Add("Only one AI opponent is supported (Story 10-10 adds more).");

            // 4) Active slots must fit the map's start positions.
            if (activeCount > map.StartPositionCount)
                errors.Add($"This map supports {map.StartPositionCount} start positions, but {activeCount} slots are active.");

            // 5) Every active slot's faction must resolve in the discovered catalog.
            var knownFactionIds = new HashSet<string>(
                (factions ?? System.Array.Empty<FactionEntry>()).Select(f => f.Id), System.StringComparer.Ordinal);
            foreach (SetupSlot slot in slots.Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai)
                                            .OrderBy(s => s.Slot))
            {
                if (string.IsNullOrEmpty(slot.FactionId))
                    errors.Add($"Slot {slot.Slot + 1}: choose a faction.");
                else if (!knownFactionIds.Contains(slot.FactionId))
                    errors.Add($"Unknown faction: {slot.FactionId}");
            }

            // 6) Team ordinals in range [0, activeCount] (0 = FFA / unassigned).
            foreach (SetupSlot slot in slots.Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai)
                                            .OrderBy(s => s.Slot))
            {
                if (slot.Team < 0 || slot.Team > activeCount)
                    errors.Add($"Slot {slot.Slot + 1}: team must be between 0 and {activeCount}.");
            }

            // 7) There must be at least two opposing SIDES among the active slots — no all-allied set. A Team==0 slot is
            // its OWN distinct side (FFA — mirrors AllianceSeeder's FFA default TeamId[f]==f); all slots sharing a given
            // POSITIVE team ordinal are one side. So 0/0, 0/1 and 1/2 give two sides (pass); 1/1 gives one side (fail).
            if (activeCount >= 2)
            {
                var sides = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (SetupSlot slot in slots.Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai))
                    // Positive teams collapse into one side per ordinal; each Team==0 slot is a unique side keyed by its slot.
                    sides.Add(slot.Team > 0 ? $"t{slot.Team}" : $"ffa{slot.Slot}");

                if (sides.Count < 2)
                    errors.Add("Opposing sides required — the human and the AI cannot be on the same team.");
            }

            return errors;
        }
    }
}
