#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions; // ScenarioData, ScenarioPlayerSlot

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// Story 11.1 — the pure transform at the heart of the setup flow: turn a <see cref="SkirmishSetup"/> + the chosen
    /// base <see cref="ScenarioData"/> into an in-memory <see cref="ScenarioData"/> ready to hand to the existing
    /// <c>PendingGeneratedScenario</c> handoff. It only rebuilds the <c>PlayerSlots</c> — terrain, entities, resource
    /// nodes, triggers, and the win condition are left byte-identical to the base map. Faction choices are committed as
    /// <c>FactionJson</c> <c>res://</c> paths (never in-memory defs), so the existing <c>ResolveSlotFactionDefs</c> runs
    /// its ability resolution + tag-drop at load (DW-121 closed by construction). Deterministic: same input → identical
    /// output. Godot-free.
    /// </summary>
    public static class SkirmishSetupToScenario
    {
        /// <summary>
        /// Build the launch <see cref="ScenarioData"/>. Each active (Human/Ai) <see cref="SetupSlot"/> becomes a
        /// <see cref="ScenarioPlayerSlot"/> carrying its <see cref="SetupSlot.Slot"/>, the chosen faction's
        /// <c>res://</c> path, its <see cref="SetupSlot.Team"/>, and the base map's per-slot
        /// <c>BaseX/BaseZ/StartOre/StartCrystal</c> for that index; Open/Closed slots are dropped. The caller's
        /// <paramref name="baseMap"/> is never mutated (a fresh clone + a fresh slot array).
        /// </summary>
        public static ScenarioData Build(SkirmishSetup setup, ScenarioData baseMap, IReadOnlyList<FactionEntry> factions)
        {
            // Shallow clone keeps terrain/entities/win-condition references intact; we then swap in a NEW PlayerSlots
            // array so the base map's own slots are never touched.
            ScenarioData built = baseMap.ShallowClone();

            var factionByIdRes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (FactionEntry f in factions ?? System.Array.Empty<FactionEntry>())
                factionByIdRes[f.Id] = f.ResPath; // last-wins; the catalog already deduped by id

            // The base map's slots ordered by Slot — we pair the i-th active slot with the i-th base slot BY POSITION
            // (not by matching Slot ordinals), so a non-contiguous setup (e.g. Human=slot1, AI=slot2 on a 4-start map)
            // still lands on the base map's authored start positions in order. Never mutated (a fresh ordered array).
            ScenarioPlayerSlot[] baseSlots = (baseMap.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
                .OrderBy(b => b.Slot).ToArray();

            // The active (Human/Ai) setup slots, ordered by their original Slot. These are renumbered to CONTIGUOUS
            // indices 0..k-1 below so the built scenario is Player1..Playerk contiguous — aligning with both the
            // FactionRegistry active span and ResolveSlotFactionDefs' per-ordinal (by-slot-position) faction writes.
            var activeSlots = (setup.Slots ?? new List<SetupSlot>())
                .Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai)
                .OrderBy(s => s.Slot)
                .ToList();

            var newSlots = new List<ScenarioPlayerSlot>();
            for (int i = 0; i < activeSlots.Count; i++)
            {
                SetupSlot s = activeSlots[i];
                // Position-based pairing: the i-th active slot takes the i-th base slot's economy/positions
                // (defaults when the base map declares fewer). NOT a Slot-ordinal match — the emitted Slot is the
                // new contiguous index i, decoupled from the setup slot's original ordinal.
                ScenarioPlayerSlot? baseSlot = i < baseSlots.Length ? baseSlots[i] : null;
                string factionJson = (s.FactionId != null && factionByIdRes.TryGetValue(s.FactionId, out string? res))
                    ? res
                    : "";

                newSlots.Add(new ScenarioPlayerSlot
                {
                    Slot         = i, // contiguous 0..k-1
                    FactionJson  = factionJson,
                    Team         = s.Team,
                    StartOre     = baseSlot?.StartOre     ?? 200f,
                    StartCrystal = baseSlot?.StartCrystal ?? 0f,
                    BaseX        = baseSlot?.BaseX        ?? 0f,
                    BaseZ        = baseSlot?.BaseZ        ?? 0f,
                });
            }

            built.PlayerSlots = newSlots.ToArray();
            return built;
        }
    }
}
