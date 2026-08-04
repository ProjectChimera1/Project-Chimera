#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core.Definitions; // ScenarioData, ScenarioPlayerSlot

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// Story 11.1 — the pure transform at the heart of the setup flow: turn a <see cref="SkirmishSetup"/> + the chosen
    /// base <see cref="ScenarioData"/> into an in-memory <see cref="ScenarioData"/> ready to hand to the existing
    /// <c>PendingGeneratedScenario</c> handoff. It rebuilds the <c>PlayerSlots</c>, re-keys the pre-placed
    /// entities to the surviving contiguous slots, and (DW-458) prunes/reconciles every other per-slot reference —
    /// trigger events/conditions/actions, the win-condition preset spec, and Income resource-node owners — so a
    /// map launched with fewer active players than it authors start positions never boots with dangling slot
    /// references. When every base slot survives with its original ordinal (the common 1v1-on-a-2-start-map path)
    /// the reconcile is skipped entirely and terrain/triggers/win-condition stay reference-identical to the base
    /// map (byte-identical on serialize). Faction choices are committed as <c>FactionJson</c> <c>res://</c> paths
    /// (never in-memory defs), so the existing <c>ResolveSlotFactionDefs</c> runs its ability resolution + tag-drop
    /// at load (DW-121 closed by construction). Deterministic: same input → identical output. Godot-free.
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
            // ResolveSlotFactionDefs' per-ordinal (by-slot-position) faction writes. The ordering itself lives in
            // ActiveSlotsInLaunchOrder (DW-460) — shared with the setup screen's swatch coloring so the two can never
            // diverge.
            IReadOnlyList<SetupSlot> activeSlots = ActiveSlotsInLaunchOrder(setup.Slots);

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
            // (DW-458) buildingIndexMap: authored buildings-array index → index in the rebuilt array, for surviving
            // entries only. The LandmarkDestruction preset references a building by PLACEMENT-LIST INDEX, so the
            // filter below shifts every index after a dropped entry — the win-spec reconcile reads this map.
            var buildingIndexMap = new Dictionary<int, int>();
            ScenarioBuilding[] baseBuildings = baseMap.Buildings ?? System.Array.Empty<ScenarioBuilding>();
            var mappedBuildings = new List<ScenarioBuilding>(baseBuildings.Length);
            for (int bi = 0; bi < baseBuildings.Length; bi++)
            {
                ScenarioBuilding b = baseBuildings[bi];
                if (b == null || !origSlotToNew.TryGetValue(b.Slot, out int newOwner)) continue;
                buildingIndexMap[bi] = mappedBuildings.Count;
                mappedBuildings.Add(new ScenarioBuilding
                {
                    Type = b.Type, Slot = newOwner, X = b.X, Z = b.Z, PreBuilt = b.PreBuilt, Rot = b.Rot,
                });
            }
            built.Buildings = mappedBuildings.ToArray();
            // Pre-placed UNITS additionally need their faction-specific id translated. The base map authored them
            // against the base slot's own faction (alpha_map_01 places alpha's "worker" for both slots), but this slot
            // may have CHOSEN a different faction whose roster is disjoint — beta has "forgehand", not "worker". An
            // untranslated id resolves to no UnitDefinition and the applier's def==null skip drops the unit SILENTLY,
            // which is how a cross-faction launch shipped an AI opponent with zero workers (in-engine gate, 2026-07-28).
            // SkirmishRosterMap re-keys by role — (Category, ordinal-within-category) — so alpha's mage becomes beta's
            // rune_caster. A same-faction launch takes the identity path, so it stays byte-identical to the base map.
            // Buildings need no equivalent: ScenarioBuilding.Type is a shared BuildingType token, not a faction id.
            // (DW-458) unitIndexMap: authored units-array index → index in the rebuilt array, for surviving entries
            // only — the Assassination preset references its leader by PLACEMENT-LIST INDEX (see buildingIndexMap).
            var unitIndexMap = new Dictionary<int, int>();
            ScenarioUnit[] baseUnits = baseMap.Units ?? System.Array.Empty<ScenarioUnit>();
            var mappedUnits = new List<ScenarioUnit>(baseUnits.Length);
            for (int ui = 0; ui < baseUnits.Length; ui++)
            {
                ScenarioUnit u = baseUnits[ui];
                if (u == null) continue;
                if (!origSlotToNew.TryGetValue(u.Slot, out int newSlot)) continue; // orphaned slot → dropped (above)

                targetFactionByNew.TryGetValue(newSlot, out FactionEntry? target);
                authoredFactionByNew.TryGetValue(newSlot, out FactionEntry? authored);

                string? mappedId = SkirmishRosterMap.MapUnitId(u.UnitId ?? "", authored, target);
                // Unmappable (the chosen faction has no unit of that role at all): drop rather than emit an id that
                // cannot resolve. SkirmishSetupValidator blocks this config before Launch, so the UI never gets here.
                if (string.IsNullOrEmpty(mappedId)) continue;

                unitIndexMap[ui] = mappedUnits.Count;
                mappedUnits.Add(new ScenarioUnit
                {
                    UnitId = mappedId!, Slot = newSlot, X = u.X, Z = u.Z, Rot = u.Rot,
                });
            }
            built.Units = mappedUnits.ToArray();

            // ── DW-458 (decision 2026-07-30: prune-and-reconcile). Strip/rewrite every remaining per-slot reference
            // the ShallowClone still shares with the base map: trigger events/conditions/actions, the win-condition
            // preset spec, and Income resource-node owners. Skipped entirely when the remap is the COMPLETE IDENTITY
            // (every base slot paired to its own ordinal — e.g. any 2-start map launched 1v1), so the common path
            // keeps the base map's references untouched and serializes byte-identically. ──
            bool identityRemap = origSlotToNew.Count == baseSlots.Length;
            if (identityRemap)
                foreach (KeyValuePair<int, int> kv in origSlotToNew)
                    if (kv.Key != kv.Value) { identityRemap = false; break; }
            if (!identityRemap)
            {
                built.Triggers = ReconcileTriggers(
                    baseMap.Triggers ?? System.Array.Empty<TriggerDefinition>(), origSlotToNew,
                    PerPlayerVariableNames(baseMap.Variables));
                built.WinConditionSpec = ReconcileWinSpec(
                    baseMap.WinConditionSpec, origSlotToNew, unitIndexMap, buildingIndexMap);
                built.ResourceNodes = ReconcileResourceNodes(
                    baseMap.ResourceNodes ?? System.Array.Empty<ScenarioResourceNode>(), origSlotToNew);
            }

            return built;
        }

        /// <summary>
        /// DW-460 — the single source of truth for the ACTIVE-slot launch order: Human first, then by ascending
        /// setup-row ordinal. Review PATCH (11.1 follow-up): the single Human MUST sort to contiguous index 0 so it
        /// becomes Player1 — offline the local human is hardwired to Player1 (LocalFactionPolicy.Effective) and the
        /// AI to Player2 (AiOpponentSystem.AI_FACTION). Ordering by raw Slot alone let a Human placed in a higher
        /// slot than the AI land on index 1 (AI-piloted) while the AI's config took index 0 (human-controlled) —
        /// silently swapping who controls which faction/team. <see cref="Build"/> renumbers slots from exactly this
        /// list, and the setup screen keys its color swatches from it (<see cref="LaunchIndexBySlot"/>) — sharing the
        /// ONE ordering is what makes swatch/in-match color drift structurally impossible (the DW-460 defect:
        /// swatches keyed by the map's start-position ROW index while the in-match color keys by the post-transform
        /// contiguous index). Deterministic (stable LINQ sort); a null list yields an empty result.
        /// </summary>
        public static IReadOnlyList<SetupSlot> ActiveSlotsInLaunchOrder(IEnumerable<SetupSlot>? slots) =>
            (slots ?? Enumerable.Empty<SetupSlot>())
                .Where(s => s.Kind == SlotKind.Human || s.Kind == SlotKind.Ai)
                .OrderBy(s => s.Kind == SlotKind.Human ? 0 : 1)
                .ThenBy(s => s.Slot)
                .ToList();

        /// <summary>
        /// DW-460 — for each ACTIVE setup slot (keyed by its <see cref="SetupSlot.Slot"/> row ordinal): the
        /// contiguous launch index <see cref="Build"/> renumbers it to, which is also its team-color palette index
        /// (<c>TeamColorPalette.SlotColorAt</c>). Open/Closed rows launch no player and get NO entry — the setup
        /// screen renders those swatches with <c>TeamColorPalette.InactiveSlotColor</c>. Duplicate row ordinals
        /// (never produced by the screen, one row per start position) resolve last-wins rather than throwing.
        /// </summary>
        public static IReadOnlyDictionary<int, int> LaunchIndexBySlot(IEnumerable<SetupSlot>? slots)
        {
            var bySlot = new Dictionary<int, int>();
            IReadOnlyList<SetupSlot> ordered = ActiveSlotsInLaunchOrder(slots);
            for (int i = 0; i < ordered.Count; i++) bySlot[ordered[i].Slot] = i;
            return bySlot;
        }

        // ── DW-458 reconcile helpers ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The trigger EVENT kinds whose <c>faction</c> field is a player-slot reference the director
        /// dispatches on (<c>ScenarioDirector.EventMatches</c> compares <c>f.Slot == def.Faction</c> for exactly
        /// these). <c>match_start</c>/<c>timer_expires</c>/<c>custom_event</c> are system-keyed — never touched.</summary>
        private static readonly string[] FactionKeyedEventKinds =
        {
            "unit_dies", "building_completed", "resource_threshold", "unit_count_threshold",
            "unit_damaged", "unit_trained", "ability_cast", "hero_level", "player_chat",
        };

        /// <summary>The CONDITION kinds whose <c>faction</c> field always selects a player slot
        /// (<c>ScenarioDirector.EvalCondition</c> does <c>(Faction)(c.Faction + 1)</c> for these).
        /// <c>variable_comparison</c> is slot-keyed only for a PerPlayer-declared variable — handled separately.</summary>
        private static readonly string[] FactionKeyedConditionKinds =
        {
            "building_exists", "resource_comparison", "unit_count", "unit_in_region",
        };

        /// <summary>The ACTION kinds whose <c>faction</c> field always targets a player slot
        /// (<c>ScenarioDirector.ExecuteLeaf</c>: spawn owner / verdict faction / ore credit target).
        /// <c>set_variable</c> is slot-keyed only for a PerPlayer-declared variable — handled separately.</summary>
        private static readonly string[] FactionKeyedActionKinds =
        {
            "spawn_unit", "victory", "defeat", "add_resources",
        };

        private static bool IsIn(string[] set, string? kind)
        {
            for (int i = 0; i < set.Length; i++)
                if (string.Equals(set[i], kind, System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The names of the base map's PerPlayer-declared DSL variables — a
        /// <c>variable_comparison</c>/<c>set_variable</c> naming one of these uses its <c>faction</c> field as the
        /// player-slot selector (<c>DslVarTable.GetInt(name, faction)</c>), so it participates in the reconcile.</summary>
        private static HashSet<string> PerPlayerVariableNames(ScenarioVariable[]? variables)
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (ScenarioVariable v in variables ?? System.Array.Empty<ScenarioVariable>())
                if (v != null && v.Scope == ProjectChimera.Dsl.VarScope.PerPlayer && !string.IsNullOrEmpty(v.Name))
                    names.Add(v.Name);
            return names;
        }

        /// <summary>
        /// Prune/rewrite the flat trigger list for the dropped-slot launch (DW-458):
        /// <list type="bullet">
        /// <item>an EVENT referencing a dropped slot is STRIPPED (it can never occur); a trigger whose authored
        /// events are all stripped is DROPPED (nothing can ever fire it);</item>
        /// <item>a CONDITION referencing a dropped slot DROPS the whole trigger — the author gated it on a player
        /// who is not in this match, and stripping the guard instead would let the trigger fire when it must not;</item>
        /// <item>an ACTION referencing a dropped slot is STRIPPED (no player to act on); a trigger with no actions
        /// left is DROPPED (it can no longer do anything);</item>
        /// <item>every KEPT slot reference is REWRITTEN to the new contiguous ordinal via <paramref name="slotMap"/>.</item>
        /// </list>
        /// Fresh instances throughout — the base map's trigger objects are never mutated.
        /// </summary>
        private static TriggerDefinition[] ReconcileTriggers(
            TriggerDefinition[] triggers, Dictionary<int, int> slotMap, HashSet<string> perPlayerVars)
        {
            var kept = new List<TriggerDefinition>(triggers.Length);
            foreach (TriggerDefinition t in triggers)
            {
                if (t == null) continue;

                TriggerEvent[] events = t.Events ?? System.Array.Empty<TriggerEvent>();
                var newEvents = new List<TriggerEvent>(events.Length);
                foreach (TriggerEvent e in events)
                {
                    if (e == null) continue;
                    bool slotKeyed = IsIn(FactionKeyedEventKinds, e.Type) && e.Faction >= 0;
                    if (slotKeyed && !slotMap.ContainsKey(e.Faction)) continue; // dropped slot → strip the event
                    newEvents.Add(new TriggerEvent
                    {
                        Type = e.Type, Faction = slotKeyed ? slotMap[e.Faction] : e.Faction,
                        BuildingType = e.BuildingType, TimerName = e.TimerName,
                        Amount = e.Amount, Count = e.Count, Operator = e.Operator,
                    });
                }
                if (events.Length > 0 && newEvents.Count == 0) continue; // no way left to fire → drop the trigger

                TriggerCondition[] conditions = t.Conditions ?? System.Array.Empty<TriggerCondition>();
                var newConditions = new List<TriggerCondition>(conditions.Length);
                bool deadGuard = false;
                foreach (TriggerCondition c in conditions)
                {
                    if (c == null) continue;
                    bool slotKeyed = (IsIn(FactionKeyedConditionKinds, c.Type)
                                      || (string.Equals(c.Type, "variable_comparison", System.StringComparison.Ordinal)
                                          && c.Variable != null && perPlayerVars.Contains(c.Variable)))
                                     && c.Faction >= 0;
                    if (slotKeyed && !slotMap.ContainsKey(c.Faction)) { deadGuard = true; break; }
                    newConditions.Add(new TriggerCondition
                    {
                        Type = c.Type, Faction = slotKeyed ? slotMap[c.Faction] : c.Faction,
                        BuildingType = c.BuildingType, Amount = c.Amount, Count = c.Count,
                        Variable = c.Variable, RegionId = c.RegionId, Value = c.Value, Operator = c.Operator,
                    });
                }
                if (deadGuard) continue; // gated on a player who is not in the match → drop the trigger

                TriggerAction[] actions = t.Actions ?? System.Array.Empty<TriggerAction>();
                var newActions = new List<TriggerAction>(actions.Length);
                foreach (TriggerAction a in actions)
                {
                    if (a == null) continue;
                    bool slotKeyed = (IsIn(FactionKeyedActionKinds, a.Type)
                                      || (string.Equals(a.Type, "set_variable", System.StringComparison.Ordinal)
                                          && a.Variable != null && perPlayerVars.Contains(a.Variable)))
                                     && a.Faction >= 0;
                    if (slotKeyed && !slotMap.ContainsKey(a.Faction)) continue; // dropped slot → strip the action
                    newActions.Add(new TriggerAction
                    {
                        Type = a.Type, UnitId = a.UnitId, Faction = slotKeyed ? slotMap[a.Faction] : a.Faction,
                        X = a.X, Z = a.Z, Count = a.Count, Text = a.Text, Duration = a.Duration,
                        TimerName = a.TimerName, TimerSeconds = a.TimerSeconds, Amount = a.Amount,
                        Variable = a.Variable, Value = a.Value, SoundId = a.SoundId,
                    });
                }
                if (actions.Length > 0 && newActions.Count == 0) continue; // nothing left to do → drop the trigger

                kept.Add(new TriggerDefinition
                {
                    Name = t.Name, Enabled = t.Enabled, RunOnce = t.RunOnce,
                    CooldownSeconds = t.CooldownSeconds, Priority = t.Priority,
                    Events = newEvents.ToArray(), Conditions = newConditions.ToArray(), Actions = newActions.ToArray(),
                });
            }
            return kept.ToArray();
        }

        /// <summary>
        /// Reconcile the win-condition preset spec for the dropped-slot launch (DW-458). A preset whose designated
        /// slot/placement survived is REWRITTEN to the new ordinal/index; one whose target was dropped is PRUNED to
        /// null, falling back to the base map's built-in <see cref="ScenarioData.WinCondition"/> enum — the honest
        /// degradation (the authored goal referenced a player who is not in this match). KingOfTheHill is
        /// region-keyed (regions are never dropped) and passes through untouched; fresh instances when rewritten.
        /// </summary>
        private static WinConditionSpec? ReconcileWinSpec(
            WinConditionSpec? spec, Dictionary<int, int> slotMap,
            Dictionary<int, int> unitIndexMap, Dictionary<int, int> buildingIndexMap)
        {
            if (spec == null || spec.Preset == WinPresetKind.None || spec.Preset == WinPresetKind.KingOfTheHill)
                return spec;

            switch (spec.Preset)
            {
                case WinPresetKind.TimedSurvival:
                    if (!slotMap.TryGetValue(spec.FactionSlot, out int newSlot)) return null; // designated player dropped
                    return new WinConditionSpec
                    {
                        Preset = WinPresetKind.TimedSurvival, FactionSlot = newSlot, SurviveTicks = spec.SurviveTicks,
                    };

                case WinPresetKind.Assassination:
                    if (!unitIndexMap.TryGetValue(spec.LeaderUnitIndex, out int newUnitIndex)) return null; // leader dropped
                    return new WinConditionSpec
                    {
                        Preset = WinPresetKind.Assassination, LeaderUnitIndex = newUnitIndex,
                    };

                case WinPresetKind.LandmarkDestruction:
                    if (!buildingIndexMap.TryGetValue(spec.StructureIndex, out int newBuildingIndex)) return null; // landmark dropped
                    return new WinConditionSpec
                    {
                        Preset = WinPresetKind.LandmarkDestruction, StructureIndex = newBuildingIndex,
                    };

                default:
                    return spec; // unknown preset — leave for the validator's fail-closed default arm
            }
        }

        /// <summary>
        /// Reconcile resource-node owners for the dropped-slot launch (DW-458). An Income node owned by a dropped
        /// slot is DROPPED (there is no player to credit — and the validator fail-closes on an undeclared Income
        /// <c>owner_slot</c>, which would otherwise reject the whole built scenario at boot). A kept owner is
        /// REWRITTEN to the new ordinal. A non-Income node's <c>owner_slot</c> is inert; one referencing a dropped
        /// slot is normalized to -1 (unset) so no dangling ordinal survives. Untouched nodes keep their original
        /// references (never mutated).
        /// </summary>
        private static ScenarioResourceNode[] ReconcileResourceNodes(
            ScenarioResourceNode[] nodes, Dictionary<int, int> slotMap)
        {
            var kept = new List<ScenarioResourceNode>(nodes.Length);
            foreach (ScenarioResourceNode n in nodes)
            {
                if (n == null) continue;
                if (n.OwnerSlot < 0 || slotMap.TryGetValue(n.OwnerSlot, out _))
                {
                    // Owner survives (or none declared): rewrite only when the ordinal actually changes.
                    if (n.OwnerSlot >= 0 && slotMap[n.OwnerSlot] != n.OwnerSlot)
                        kept.Add(CloneNodeWithOwner(n, slotMap[n.OwnerSlot]));
                    else
                        kept.Add(n);
                    continue;
                }
                bool income = string.Equals(n.CollectionModel, "Income", System.StringComparison.Ordinal);
                if (income) continue;                    // Income node owned by a dropped player → drop the node
                kept.Add(CloneNodeWithOwner(n, -1));     // inert owner on a dropped slot → normalize to unset
            }
            return kept.ToArray();
        }

        /// <summary>A field-complete copy of a resource node with a different <see cref="ScenarioResourceNode.OwnerSlot"/>
        /// (the base map's node object is never mutated).</summary>
        private static ScenarioResourceNode CloneNodeWithOwner(ScenarioResourceNode n, int ownerSlot) => new()
        {
            X = n.X, Z = n.Z, Supply = n.Supply, Rate = n.Rate, MaxGatherers = n.MaxGatherers,
            CollectionModel = n.CollectionModel, ResourceType = n.ResourceType,
            RequiresStructure = n.RequiresStructure, RequiresStructureRadius = n.RequiresStructureRadius,
            OwnerSlot = ownerSlot, IncomePeriodTicks = n.IncomePeriodTicks, Rot = n.Rot,
        };
    }
}
