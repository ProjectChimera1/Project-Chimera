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

            IReadOnlyList<FactionEntry> factionList = factions ?? System.Array.Empty<FactionEntry>();

            var factionByIdRes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (FactionEntry f in factionList)
                factionByIdRes[f.Id] = f.ResPath; // last-wins; the catalog already deduped by id

            // The base map's slots ordered by Slot — we pair the i-th active slot with the i-th base slot BY POSITION
            // (not by matching Slot ordinals), so a non-contiguous setup (e.g. Human=slot1, AI=slot2 on a 4-start map)
            // still lands on the base map's authored start positions in order. Never mutated (a fresh ordered array).
            ScenarioPlayerSlot[] baseSlots = (baseMap.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
                .OrderBy(b => b.Slot).ToArray();

            // The active (Human/Ai) setup slots, renumbered to CONTIGUOUS indices 0..k-1 below so the built scenario is
            // Player1..Playerk contiguous — aligning with both the FactionRegistry active span and
            // ResolveSlotFactionDefs' per-ordinal (by-slot-position) faction writes.
            // Review PATCH (11.1 follow-up): the single Human MUST sort to contiguous index 0 so it becomes Player1 —
            // offline the local human is hardwired to Player1 (LocalFactionPolicy.Effective) and the AI to Player2
            // (AiOpponentSystem.AI_FACTION). Ordering by raw Slot alone let a Human placed in a higher slot than the AI
            // land on index 1 (AI-piloted) while the AI's config took index 0 (human-controlled) — silently swapping who
            // controls which faction/team. Human-first, then by Slot, keeps that swap impossible.
            var activeSlots = (setup.Slots ?? new List<SetupSlot>())
                .Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai)
                .OrderBy(s => s.Kind == SlotKind.Human ? 0 : 1)
                .ThenBy(s => s.Slot)
                .ToList();

            var newSlots = new List<ScenarioPlayerSlot>();
            // Maps each PAIRED base slot's ORIGINAL ordinal → the new contiguous index i that now owns that base
            // position. Drives the pre-placed entity remap below: a base slot with no active pairing (e.g. slots 2/3
            // of a 4-start map launched 1v1) is absent, so its pre-placed content is dropped rather than orphaned.
            var origSlotToNew = new Dictionary<int, int>();
            // Per new contiguous index: the faction that index CHOSE, and the faction the paired base position's
            // pre-placed units were AUTHORED against. Together these drive the cross-faction unit-id remap below.
            var targetFactionByNew   = new Dictionary<int, FactionEntry?>();
            var authoredFactionByNew = new Dictionary<int, FactionEntry?>();
            for (int i = 0; i < activeSlots.Count; i++)
            {
                SetupSlot s = activeSlots[i];
                // Position-based pairing: the i-th active slot takes the i-th base slot's economy/positions
                // (defaults when the base map declares fewer). NOT a Slot-ordinal match — the emitted Slot is the
                // new contiguous index i, decoupled from the setup slot's original ordinal.
                ScenarioPlayerSlot? baseSlot = i < baseSlots.Length ? baseSlots[i] : null;
                if (baseSlot != null) origSlotToNew[baseSlot.Slot] = i;
                string factionJson = (s.FactionId != null && factionByIdRes.TryGetValue(s.FactionId, out string? res))
                    ? res
                    : "";

                targetFactionByNew[i]   = SkirmishRosterMap.ById(factionList, s.FactionId);
                authoredFactionByNew[i] = SkirmishRosterMap.ByResPath(factionList, baseSlot?.FactionJson);

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

            // Review PATCH (11.1 follow-up): the base map's pre-placed Buildings/Units are keyed by ORIGINAL slot
            // ordinal. When the map declares more start positions than the launch has active players (e.g. the shipped
            // 4-start quad_map_01 launched as the honest 1v1), the entities for the dropped slots would otherwise
            // survive the ShallowClone still keyed to slots 2/3 — placed at apply time as ghost Player3/Player4 bases
            // (the buildings loop in ScenarioApplier maps slot→(Faction)(slot+1) with no active-player check and, unlike
            // the units loop, still places a building for a faction with no resolved def). That leaves un-ownable
            // pre-built enemy bases and, under DestroyAllBuildings, an unwinnable match. Fix at the source: the built
            // skirmish scenario must contain entities only for the players that exist. Keep only entities whose slot is
            // a paired base slot, RE-KEYED to the new contiguous owner index; drop the rest. New arrays + copied
            // elements so the caller's baseMap (whose arrays ShallowClone shares by reference) is never mutated.
            // A 2-start map launched 1v1 keeps every entity with an identity remap → byte-identical to the base map.
            built.Buildings = (baseMap.Buildings ?? System.Array.Empty<ScenarioBuilding>())
                .Where(b => origSlotToNew.ContainsKey(b.Slot))
                .Select(b => new ScenarioBuilding
                {
                    Type = b.Type, Slot = origSlotToNew[b.Slot], X = b.X, Z = b.Z, PreBuilt = b.PreBuilt, Rot = b.Rot,
                })
                .ToArray();
            // Pre-placed UNITS additionally need their faction-specific id translated. The base map authored them
            // against the base slot's own faction (alpha_map_01 places alpha's "worker" for both slots), but this slot
            // may have CHOSEN a different faction whose roster is disjoint — beta has "forgehand", not "worker". An
            // untranslated id resolves to no UnitDefinition and the applier's def==null skip drops the unit SILENTLY,
            // which is how a cross-faction launch shipped an AI opponent with zero workers (in-engine gate, 2026-07-28).
            // SkirmishRosterMap re-keys by role — (Category, ordinal-within-category) — so alpha's mage becomes beta's
            // rune_caster. A same-faction launch takes the identity path, so it stays byte-identical to the base map.
            // Buildings need no equivalent: ScenarioBuilding.Type is a shared BuildingType token, not a faction id.
            var mappedUnits = new List<ScenarioUnit>();
            foreach (ScenarioUnit u in baseMap.Units ?? System.Array.Empty<ScenarioUnit>())
            {
                if (u == null) continue;
                if (!origSlotToNew.TryGetValue(u.Slot, out int newSlot)) continue; // orphaned slot → dropped (above)

                targetFactionByNew.TryGetValue(newSlot, out FactionEntry? target);
                authoredFactionByNew.TryGetValue(newSlot, out FactionEntry? authored);

                string? mappedId = SkirmishRosterMap.MapUnitId(u.UnitId ?? "", authored, target);
                // Unmappable (the chosen faction has no unit of that role at all): drop rather than emit an id that
                // cannot resolve. SkirmishSetupValidator blocks this config before Launch, so the UI never gets here.
                if (string.IsNullOrEmpty(mappedId)) continue;

                mappedUnits.Add(new ScenarioUnit
                {
                    UnitId = mappedId!, Slot = newSlot, X = u.X, Z = u.Z, Rot = u.Rot,
                });
            }
            built.Units = mappedUnits.ToArray();

            return built;
        }
    }
}
