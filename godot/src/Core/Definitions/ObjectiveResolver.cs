#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 7.14 — one deterministic effective objective (id + presentation text + initial state), the SINGLE source
    /// both the sim (reserved-var declaration) and presentation (quest-log/briefing) consume so they agree.
    /// </summary>
    public readonly struct ResolvedObjective
    {
        /// <summary>The objective id (an authored id, or <see cref="ObjectiveResolver.DefaultObjectiveId"/>).</summary>
        public readonly string Id;

        /// <summary>The player-facing title (presentation-only; never enters the tick / any checksum).</summary>
        public readonly string Title;

        /// <summary>Optional longer description (presentation-only).</summary>
        public readonly string? Description;

        /// <summary>The seeded initial state (the reserved var's initial ordinal for authored objectives; the render
        /// state for the synthesized default).</summary>
        public readonly ObjectiveState InitialState;

        /// <summary>
        /// True when this objective declares a FOLDED reserved <c>Global Int</c> DSL variable (authored objectives).
        /// FALSE for the synthesized default — it is presentation-only and declares NO folded sim state, so an
        /// objective-less scenario (every pre-7.14 scenario, including every golden) adds NO new folded var and its
        /// per-tick <c>SimChecksum</c> stays byte-identical (no bump, no world-golden re-baseline). A default
        /// objective is never mutated by any trigger (a scenario driving objective actions authors objectives), so
        /// its state is immutable <see cref="ObjectiveState.Active"/> rendered directly from <see cref="InitialState"/>.
        /// </summary>
        public readonly bool HasReservedVar;

        public ResolvedObjective(string id, string title, string? description, ObjectiveState initialState, bool hasReservedVar)
        {
            Id = id; Title = title; Description = description; InitialState = initialState; HasReservedVar = hasReservedVar;
        }

        /// <summary>The reserved <c>DslVarTable</c> variable name backing this objective's live state.</summary>
        public string ReservedVarName => ObjectiveResolver.ReservedVarName(Id);
    }

    /// <summary>
    /// Story 7.14 — the pure, Godot-free resolver of a scenario's EFFECTIVE objectives: the authored list when present,
    /// else exactly ONE presentation-only default synthesized from the win condition/preset via
    /// <see cref="WinObjectiveText"/>. Consumed by BOTH the sim (reserved-var declaration at
    /// <c>ScenarioDirector.LoadScenario</c>) and presentation (the quest-log panel + briefing surface) so both agree.
    /// Never yields zero objectives — every match shows its goal.
    /// </summary>
    public static class ObjectiveResolver
    {
        /// <summary>The reserved DSL-variable name prefix objective state lives under (an internal namespace the
        /// validator BARS authored variable/objective ids from, so no collision with authored names is possible).</summary>
        public const string ReservedVarPrefix = "objective:";

        /// <summary>The synthesized-default objective id (used only when the scenario authors no objectives).</summary>
        public const string DefaultObjectiveId = "victory";

        /// <summary>The reserved <c>DslVarTable</c> variable name for an objective id.</summary>
        public static string ReservedVarName(string id) => ReservedVarPrefix + id;

        /// <summary>
        /// Resolve the effective objectives: the authored objectives (each backed by a folded reserved var) when
        /// <see cref="ScenarioData.Objectives"/> is non-empty, else exactly one presentation-only default from the
        /// win condition/preset (no folded var). Order is authored order (deterministic).
        /// </summary>
        public static ResolvedObjective[] Resolve(ScenarioData? scenario)
        {
            if (scenario?.Objectives is { Length: > 0 } authored)
            {
                var result = new List<ResolvedObjective>(authored.Length);
                for (int i = 0; i < authored.Length; i++)
                {
                    ScenarioObjective o = authored[i];
                    // Malformed elements (null / empty id) are rejected at load by CheckDeclarations (run by both the
                    // ScenarioValidator gate and the LoadScenario backstop); skip defensively so an ungated caller
                    // never NREs here. A wholly-malformed list falls through to the presentation-only default below.
                    if (o is null || string.IsNullOrEmpty(o.Id)) continue;
                    result.Add(new ResolvedObjective(o.Id, o.Title, o.Description, o.InitialState, hasReservedVar: true));
                }
                if (result.Count > 0) return result.ToArray();
            }

            string title = WinObjectiveText.For(
                scenario?.WinCondition ?? WinCondition.DestroyAllBuildings,
                scenario?.WinConditionSpec);
            return new[]
            {
                new ResolvedObjective(DefaultObjectiveId, title, description: null, ObjectiveState.Active, hasReservedVar: false),
            };
        }

        /// <summary>
        /// Story 7.14 — the SHARED, fail-closed objective + reserved-namespace DECLARATION rulebook, run by BOTH the
        /// <c>ScenarioValidator</c> gate AND the <c>ScenarioDirector.LoadScenario</c> backstop (the Story 7.7
        /// gate/backstop-parity posture) so a direct <c>LoadScenario</c> caller that bypasses the validator fails
        /// closed identically — one rulebook, no drift. Returns a located error string, or <c>null</c> when the
        /// declarations are well-formed. Checks: no authored variable name in the reserved '<c>objective:</c>'
        /// namespace; and (when <see cref="ScenarioData.Objectives"/> is present) no null element, non-empty id, no id
        /// in the reserved namespace, unique ids, non-empty title. NULL scenario / null collections ⇒ nothing to check.
        /// </summary>
        public static string? CheckDeclarations(ScenarioData? scenario)
        {
            if (scenario == null) return null;

            if (scenario.Variables != null)
            {
                for (int i = 0; i < scenario.Variables.Length; i++)
                {
                    ScenarioVariable v = scenario.Variables[i];
                    if (v?.Name != null && v.Name.StartsWith(ReservedVarPrefix, StringComparison.Ordinal))
                        return $"scenario.variables[{i}].name='{v.Name}' uses the reserved '{ReservedVarPrefix}' namespace.";
                }
            }

            if (scenario.Objectives != null)
            {
                var objectiveIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < scenario.Objectives.Length; i++)
                {
                    ScenarioObjective o = scenario.Objectives[i];
                    if (o is null) return $"scenario.objectives[{i}] is null.";
                    if (string.IsNullOrWhiteSpace(o.Id))
                        return $"scenario.objectives[{i}].id must be a non-empty id.";
                    if (o.Id.StartsWith(ReservedVarPrefix, StringComparison.Ordinal))
                        return $"scenario.objectives[{i}].id='{o.Id}' uses the reserved '{ReservedVarPrefix}' namespace.";
                    if (!objectiveIds.Add(o.Id))
                        return $"scenario.objectives[{i}].id='{o.Id}' is a duplicate.";
                    if (string.IsNullOrWhiteSpace(o.Title))
                        return $"scenario.objectives[{i}].title must be a non-empty title.";
                }
            }

            return null;
        }
    }
}
