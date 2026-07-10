#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Import-time gate for a faction's authored <see cref="FactionDefinition.Research"/> list (Story 4.8). Runs
    /// over the SAME <see cref="FactionDefinition.LoadFromFile"/> aggregate <c>errors</c> channel that
    /// <see cref="BuildingDefinitionValidator"/>/<see cref="TechTreeValidator"/>/<see cref="ResourceCostValidator"/>
    /// already throw with — additive, not a replacement. Pure C#, no logging, no throw — the caller
    /// (<see cref="FactionDefinition.LoadFromFile"/>) decides to throw.
    ///
    /// <para>Checks, in order:</para>
    /// <list type="number">
    /// <item>Duplicate research id (list-all — mirrors <see cref="TechTreeValidator"/>'s duplicate-building-id
    /// check, same rationale: an unreported duplicate would silently collapse both the referential-lint id set and
    /// the cycle-detection graph to the first occurrence).</item>
    /// <item>Per-level field checks — empty <see cref="ResearchDefinition.Levels"/>, non-positive
    /// <see cref="ResearchLevel.TimeTicks"/>, a <see cref="ResearchLevel.Cost"/> key outside
    /// <see cref="ResourceCostValidator.KnownResourceIds"/> — and the definition-level out-of-<c>[0,1]</c>
    /// <see cref="ResearchDefinition.CancelRefundFraction"/> (list-all).</item>
    /// <item>Referential lint — every <see cref="ResearchDefinition.Prerequisites"/> entry must name a building id
    /// OR a research id; every <see cref="BuildingDefinition.AvailableResearch"/> entry must name a research id
    /// (list-all).</item>
    /// <item>Cycle detection — a 3-color DFS walking ONLY Research→Research edges (a building named in
    /// <see cref="ResearchDefinition.Prerequisites"/> is always a graph leaf, exactly like
    /// <see cref="TechTreeValidator"/> restricts its walk to Buildings→Buildings and treats units as leaves).
    /// Unlike the checks above, this is FIRST-FAIL: it stops after the first cycle found and reports the exact
    /// chain, including the closing repeated id (e.g. <c>"a -> b -> a"</c>) — byte-identical wording convention to
    /// <see cref="TechTreeValidator"/>'s <c>"tech tree cycle: …"</c>, kinded <c>"research cycle: …"</c>.</item>
    /// <item>A per-faction research-count sanity cap (<see cref="MaxResearchPerFaction"/>).</item>
    /// </list>
    ///
    /// A null <see cref="ResearchDefinition.Prerequisites"/>/<see cref="ResearchDefinition.Levels"/>/
    /// <see cref="BuildingDefinition.AvailableResearch"/> is treated as empty everywhere it is read — never an
    /// NRE, mirroring <see cref="TechTreeValidator"/>'s <c>?? Array.Empty&lt;string&gt;()</c> idiom throughout.
    /// </summary>
    public static class ResearchValidator
    {
        private enum Color { White, Gray, Black }

        /// <summary>
        /// A structural sanity ceiling on how many research entries one faction may author — NOT a design-
        /// significant balance limit. No existing "count cap" precedent exists elsewhere in this codebase
        /// (<c>ScenarioValidator</c>/<c>EntityWorld</c>/<c>EffectCaps</c> caps are all runtime/memory caps, not
        /// authoring caps); 64 matches the structural-cap convention already used in the forward architecture
        /// (<c>MaxSearchTargets</c>/<c>MaxSpawnCount</c> = 64).
        /// </summary>
        public const int MaxResearchPerFaction = 64;

        /// <summary>The known resource ids with runtime backing today — reuses
        /// <see cref="ResourceCostValidator.KnownResourceIds"/> as the SOLE source of truth (never a second,
        /// independently-maintained set).</summary>
        private static readonly HashSet<string> _knownResourceIds = new(ResourceCostValidator.KnownResourceIds);

        /// <summary>The 16.16 representable ceiling for a level's cost amount AND for each modifier-delta float —
        /// references <see cref="ResourceCostValidator.Range"/> as the SOLE source of truth (never a second copy that
        /// could drift; second-review-pass fix promoting the previously-duplicated local constant). A level's cost
        /// was previously range-checked only for an unknown resource KEY, never for the amount, so a negative value
        /// (which ADDS that resource each time it is spent, per <see cref="ResourceCostValidator"/>'s own documented
        /// footgun) or a value ≥ this ceiling loaded cleanly.</summary>
        private const int CostRange = ResourceCostValidator.Range;

        /// <summary>
        /// Validate <paramref name="def"/>'s <see cref="FactionDefinition.Research"/> list. Returns every located
        /// duplicate-id, per-level-field, cancel-refund-range, and referential-lint error (list-all), followed by
        /// at most one cycle error (first-fail) and at most one over-cap error. Empty when the faction's research
        /// content is fully well-formed.
        /// </summary>
        public static IReadOnlyList<string> Validate(FactionDefinition def)
        {
            var errors = new List<string>();
            if (def == null) return errors;

            // Review-pass fix: a null "research"/"buildings" list (malformed JSON "research": null) must never NRE
            // this whole gate — treated as empty, mirroring TechTreeValidator's ?? Array.Empty<string>() idiom.
            List<ResearchDefinition> research = def.Research ?? new List<ResearchDefinition>();
            List<BuildingDefinition> buildings = def.Buildings ?? new List<BuildingDefinition>();

            // Research id set — duplicates are a located error (mirrors TechTreeValidator's duplicate-building-id
            // rationale: without this, the second entry sharing an id would silently collapse into the first for
            // both the referential-lint set and the cycle-detection graph below). A null element (malformed JSON
            // array entry) is skipped — never an NRE. A blank/missing id is its OWN located error (review-pass
            // fix: previously it loaded silently and was permanently unreferenceable via GetResearch/prerequisites).
            var researchIds = new HashSet<string>();
            var researchById = new Dictionary<string, ResearchDefinition>();
            foreach (ResearchDefinition? r in research)
            {
                if (r == null) continue;
                if (string.IsNullOrEmpty(r.Id))
                {
                    errors.Add("research ''.id: must be a non-empty id.");
                    continue;
                }
                if (!researchById.TryAdd(r.Id, r))
                {
                    errors.Add($"research '{r.Id}': duplicate research id (another research entry already uses this id).");
                    continue;
                }
                researchIds.Add(r.Id);
            }

            // Building id set — the OTHER valid prerequisite target (a research may gate on a completed building).
            var buildingIds = new HashSet<string>();
            foreach (BuildingDefinition? b in buildings)
            {
                if (b != null && !string.IsNullOrEmpty(b.Id)) buildingIds.Add(b.Id);
            }

            // ── Per-research field checks + prerequisite referential lint ───────────────────
            foreach (ResearchDefinition? r in research)
            {
                if (r == null) continue;
                string id = r.Id ?? "";

                // Review-pass fix: character-set sanitization, mirroring UnitDefinitionValidator.SanitizeId's
                // [a-z0-9_] gate — consistency with every other id-bearing content type. Skipped for the blank-id
                // case (already reported above).
                if (!string.IsNullOrEmpty(id) && UnitDefinitionValidator.SanitizeId(id) != id)
                    errors.Add($"research '{id}'.id: contains characters outside [a-z0-9_]; rename before saving.");

                List<ResearchLevel> levels = r.Levels ?? new List<ResearchLevel>();
                if (levels.Count == 0)
                {
                    errors.Add($"research '{id}'.levels: must declare at least one level (empty ladder).");
                }
                else
                {
                    for (int i = 0; i < levels.Count; i++)
                    {
                        ResearchLevel? level = levels[i];
                        if (level == null) continue; // a null element inside Levels (malformed JSON) — never an NRE

                        if (level.TimeTicks <= 0)
                            errors.Add($"research '{id}'.levels[{i}].time_ticks={level.TimeTicks} must be a positive value.");

                        if (level.Cost != null)
                        {
                            foreach (var (key, value) in level.Cost)
                            {
                                if (!_knownResourceIds.Contains(key))
                                {
                                    errors.Add($"research '{id}'.levels[{i}].cost: references unknown resource id '{key}' " +
                                               "(no runtime resource registered for it yet).");
                                    continue; // an unknown key's value is meaningless to range-check
                                }

                                // Review-pass fix: range-check the value too (mirrors ResourceCostValidator's
                                // unit/building cost range check) — previously only the key was validated, so a
                                // negative value (ADDS that resource each time it is spent) or a value ≥ CostRange
                                // loaded cleanly.
                                if (value < 0)
                                    errors.Add($"research '{id}'.levels[{i}].cost['{key}']={value} must be >= 0 " +
                                               "(a negative cost ADDS that resource each time it is spent).");
                                else if (value >= CostRange)
                                    errors.Add($"research '{id}'.levels[{i}].cost['{key}']={value} exceeds the maximum resource cost ({CostRange}).");
                            }
                        }

                        // Review-pass fix: NaN/Infinity guard on the four stat-delta floats — unlike every other
                        // numeric field this story introduces, these had no finite-value check; a malformed value
                        // would silently load and later apply as a real permanent stat delta once 4.9 wires it up.
                        if (level.ModifierDelta != null)
                        {
                            ResearchModifierDelta md = level.ModifierDelta;
                            CheckFiniteModifier(errors, id, i, "max_health_delta", md.MaxHealthDelta);
                            CheckFiniteModifier(errors, id, i, "attack_damage_delta", md.AttackDamageDelta);
                            CheckFiniteModifier(errors, id, i, "move_speed_delta", md.MoveSpeedDelta);
                            CheckFiniteModifier(errors, id, i, "armor_delta", md.ArmorDelta);
                        }
                    }
                }

                // Review-pass fix: an explicit NaN guard ahead of the range check — NaN comparisons are always
                // false, so "< 0f || > 1f" previously let a NaN cancel_refund_fraction silently pass.
                if (float.IsNaN(r.CancelRefundFraction))
                    errors.Add($"research '{id}'.cancel_refund_fraction={r.CancelRefundFraction} must not be NaN.");
                else if (r.CancelRefundFraction < 0f || r.CancelRefundFraction > 1f)
                    errors.Add($"research '{id}'.cancel_refund_fraction={r.CancelRefundFraction} must be within [0, 1].");

                foreach (string prereq in r.Prerequisites ?? Array.Empty<string>())
                {
                    if (!buildingIds.Contains(prereq) && !researchIds.Contains(prereq))
                        errors.Add($"research '{id}'.prerequisites: references unknown id '{prereq}' (not a known building or research id).");
                }
            }

            // ── Referential lint: BuildingDefinition.AvailableResearch ───────────────────────
            foreach (BuildingDefinition? b in buildings)
            {
                if (b == null) continue;
                string id = b.Id ?? "";
                foreach (string researchId in b.AvailableResearch ?? Array.Empty<string>())
                {
                    if (!researchIds.Contains(researchId))
                        errors.Add($"building '{id}'.available_research: references unknown research id '{researchId}'.");
                }
            }

            // ── Cycle detection: Research→Research edges only, 3-color DFS in list order ────
            string? cycleMessage = DetectCycle(def);
            if (cycleMessage != null)
                errors.Add(cycleMessage);

            // ── Per-faction research count cap ───────────────────────────────────────────────
            if (research.Count > MaxResearchPerFaction)
                errors.Add($"research: faction authors {research.Count} research entries, exceeding the " +
                           $"sanity cap of {MaxResearchPerFaction}.");

            return errors;
        }

        /// <summary>Finite + range guard for one <see cref="ResearchModifierDelta"/> field (review-pass fix) —
        /// rejects NaN/Infinity, and (second-review-pass fix) a finite magnitude at/beyond the 16.16
        /// <see cref="Fixed"/> ceiling. The four delta floats quantize into the same <see cref="Fixed"/> fields as a
        /// level's <c>cost</c> will (4.9), so a finite-but-out-of-range value (e.g. 100000) that passed the
        /// finite-only check would overflow at quantization — this closes the asymmetry with the level-cost range
        /// check above. Symmetric bound (deltas may legitimately be negative, unlike a cost). Located per-field/per-level.</summary>
        private static void CheckFiniteModifier(List<string> errors, string id, int levelIndex, string field, float value)
        {
            if (!float.IsFinite(value))
                errors.Add($"research '{id}'.levels[{levelIndex}].modifier_delta.{field}={value} must be a finite value (not NaN/Infinity).");
            else if (value <= -CostRange || value >= CostRange)
                errors.Add($"research '{id}'.levels[{levelIndex}].modifier_delta.{field}={value} exceeds the representable " +
                           $"±{CostRange} range (would overflow the 16.16 Fixed field it quantizes into at order time).");
        }

        /// <summary>The cycle-DFS walk, restricted to Research→Research edges only — a <see cref="Prerequisites"/>
        /// entry naming a building id (always a valid target per the referential lint above) is a graph leaf and
        /// never participates in the walk, exactly like <see cref="TechTreeValidator.DetectCycle"/> restricts its
        /// walk to Buildings→Buildings. First-fail (stops at the first cycle found).</summary>
        private static string? DetectCycle(FactionDefinition def)
        {
            // Rebuilds standalone (mirrors TechTreeValidator.DetectCycle) — a null Research list/element is treated
            // as empty/skipped, never an NRE.
            List<ResearchDefinition> research = def.Research ?? new List<ResearchDefinition>();

            var researchIds = new HashSet<string>();
            var researchById = new Dictionary<string, ResearchDefinition>();
            foreach (ResearchDefinition? r in research)
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                if (!researchById.TryAdd(r.Id, r)) continue; // duplicate id — already reported by Validate's own check
                researchIds.Add(r.Id);
            }

            var color = new Dictionary<string, Color>();
            foreach (string id in researchIds)
                color[id] = Color.White;

            var path = new List<string>();
            foreach (ResearchDefinition? r in research)
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                if (color[r.Id] != Color.White) continue;
                string? cycleMessage = Visit(r.Id, researchById, researchIds, color, path);
                if (cycleMessage != null) return cycleMessage;
            }
            return null;
        }

        /// <summary>DFS visit of one research node. Returns a formatted cycle message the instant a gray
        /// (in-progress) node is re-encountered, unwinding without further work; returns null on a clean
        /// (fully-black) subtree.</summary>
        private static string? Visit(string id, Dictionary<string, ResearchDefinition> researchById,
            HashSet<string> researchIds, Dictionary<string, Color> color, List<string> path)
        {
            color[id] = Color.Gray;
            path.Add(id);

            ResearchDefinition? research = researchById.TryGetValue(id, out ResearchDefinition? rd) ? rd : null;
            foreach (string prereq in research?.Prerequisites ?? Array.Empty<string>())
            {
                if (!researchIds.Contains(prereq)) continue; // a building id (or unknown id) — not a research edge

                Color prereqColor = color[prereq];
                if (prereqColor == Color.Gray)
                {
                    // Cycle: extract the chain from prereq's first appearance in the current DFS path through
                    // the end, then close it by repeating prereq (e.g. "a -> b -> a").
                    int startIdx = path.IndexOf(prereq);
                    var chain = new List<string>();
                    for (int i = startIdx; i < path.Count; i++)
                        chain.Add(path[i]);
                    chain.Add(prereq);
                    return $"research cycle: {string.Join(" -> ", chain)}.";
                }
                if (prereqColor == Color.White)
                {
                    string? result = Visit(prereq, researchById, researchIds, color, path);
                    if (result != null) return result;
                }
            }

            path.RemoveAt(path.Count - 1);
            color[id] = Color.Black;
            return null;
        }
    }
}
