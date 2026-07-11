#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The result of a <see cref="FactionValidator"/> pass (Story 5.2, AR-39/AR-12/FR-18 data) — mirrors
    /// <see cref="BuildingValidationResult"/>/<see cref="ManifestValidationResult"/>'s exact shape (a LIST of
    /// located errors, not first-fail) so a future faction editor could badge every offending field at once. Pure,
    /// no logging, no throw — the caller (<see cref="FactionDefinition.LoadFromFile"/> and future Story 5.5/5.6/5.7/
    /// 5.8 gates) decides what to do with a failing result.
    /// </summary>
    public readonly struct FactionValidationResult
    {
        /// <summary>True when the faction passed every check (no errors).</summary>
        public bool Ok => Errors.Count == 0;

        /// <summary>Every located field error found. Empty when the faction is valid.</summary>
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }

        internal FactionValidationResult(IReadOnlyList<(string, string)> errors) => Errors = errors;

        /// <summary>The always-valid result (no errors) — a shared empty instance.</summary>
        public static readonly FactionValidationResult Valid =
            new FactionValidationResult(System.Array.Empty<(string, string)>());
    }

    /// <summary>
    /// The ONE canonical faction-validity gate (Story 5.2, AR-39/AR-12/FR-18 data). Absorbs
    /// <see cref="FactionDefinition.LoadFromFile"/>'s four pre-existing inline validator calls
    /// (<see cref="BuildingDefinitionValidator"/> per building, <see cref="TechTreeValidator"/>,
    /// <see cref="ResourceCostValidator"/>, <see cref="ResearchValidator"/> — unchanged, just relocated here) and
    /// adds five new checks: unknown/empty <see cref="FactionDefinition.AiPreset"/>, an invalid
    /// <see cref="FactionDefinition.Color"/>, a duplicate <see cref="FactionDefinition.Units"/> id, a missing
    /// <c>mesh_path</c> (units + buildings), and a missing required role. Pure C# (no <c>using Godot</c>) — reads
    /// authoring values, reports strings; never throws, never logs.
    ///
    /// <para><b>Why two methods, <see cref="Validate"/> and <see cref="ValidateComplete"/> (Review Loop 2).</b>
    /// <see cref="Validate"/> — the four relocated checks plus the ai_preset closed-set/color/duplicate-unit-id
    /// checks — covers exactly the axes that are NEVER a legitimate mid-edit state (a truly duplicate id, a
    /// malformed color, an unrecognized preset are always bugs, not work-in-progress); it is safe to run on EVERY
    /// <see cref="FactionDefinition.LoadFromFile"/> call, including the Building/Unit Card Editors' Save
    /// self-check. <see cref="ValidateComplete"/> additionally checks missing <c>mesh_path</c> and missing required
    /// roles — both explicitly documented, intended, mid-edit states (<c>UnitCardPanel.Edit.cs</c>'s
    /// <c>MeshError</c>: "blank = box placeholder — always valid"), so they are exposed ONLY for callers that mean
    /// "is this faction finished/playable" — future stories' own wizard-finish/playtest/selectability gates (Story
    /// 5.5/5.6/5.7/5.8), never <see cref="FactionDefinition.LoadFromFile"/> itself.</para>
    /// </summary>
    public static class FactionValidator
    {
        /// <summary>The closed set of recognized <see cref="FactionDefinition.AiPreset"/> ids. Deliberately seeded
        /// with exactly one member — concrete preset ids for alpha/beta are Story 5.3's job, not this one's — but
        /// extended in place (no schema change) by later stories. Internal (not private) so
        /// <see cref="FactionValidatorTests"/> and any future preset-picker UI can enumerate it without duplicating
        /// the set. Exposed as <see cref="IReadOnlyList{T}"/> (not a bare <c>string[]</c>) so a reader cannot mutate
        /// the closed set process-wide via the indexer.</summary>
        internal static readonly IReadOnlyList<string> KnownAiPresets = new[] { "balanced" };

        /// <summary>Case-insensitive membership (mirrors the required-roles check's <see cref="StringComparison.OrdinalIgnoreCase"/>
        /// <c>Category</c> match below, so the two closed-set checks this validator owns are consistently lenient on
        /// case) — an authored <c>"Balanced"</c> is accepted the same as <c>"balanced"</c>.</summary>
        private static readonly HashSet<string> _knownAiPresets = new(KnownAiPresets, StringComparer.OrdinalIgnoreCase);

        /// <summary>The four combat-category <see cref="UnitDefinition.Category"/> values (excluding
        /// <c>"Worker"</c>) any ONE of which satisfies the "required roles" combat half — mirrors the project's
        /// 6-archetype <c>Category</c> system (Worker + Melee/Ranged/Siege/Air/Structure); <c>"Structure"</c> is
        /// excluded because a structure unit is not a combat role for this check.</summary>
        private static readonly string[] _combatCategories = { "Melee", "Ranged", "Siege", "Air" };

        /// <summary>
        /// Validate <paramref name="def"/> against every axis that is NEVER a legitimate mid-edit state: the four
        /// relocated checks (Building-per-item/TechTree/ResourceCost/Research, unchanged) plus the ai_preset
        /// closed-set, color-array, and duplicate-unit-id checks. This is the method
        /// <see cref="FactionDefinition.LoadFromFile"/> calls — see the class doc for why the roster-completeness
        /// checks live only in <see cref="ValidateComplete"/>. Returns every located error (list-all). Pure — never
        /// throws, never logs.
        /// </summary>
        public static FactionValidationResult Validate(FactionDefinition def)
        {
            var errors = new List<(string, string)>();
            if (def is null)
            {
                errors.Add(("faction", "faction is null."));
                return new FactionValidationResult(errors);
            }

            string id = def.Id ?? "";

            // ── Structural pre-check: null lists / null elements ───────────────────────────────────
            // The relocated sub-validators below (TechTreeValidator/ResourceCostValidator, Story 4.x) iterate
            // def.Units/def.Buildings and dereference each element's .Id with NO null guard, so a null list
            // ("units": null) or a null element ("units": [null, ...]) would throw a NullReferenceException inside
            // them — before this method's own guarded loops could report it — turning a located, list-all error
            // into an opaque NRE out of LoadFromFile. Catch every structural fault HERE, before delegating, and
            // return early: a structurally-broken collection cannot be meaningfully list-all-validated. (The sub-
            // validators' own null-intolerance, which affects their other/direct callers too, is tracked in
            // deferred-work.md — DW-100 for TechTreeValidator and the ResourceCostValidator sibling entry.)
            if (def.Units is null)
                errors.Add(("units", Located(id, "units", "units list is null.")));
            else
                for (int i = 0; i < def.Units.Count; i++)
                    if (def.Units[i] is null)
                        errors.Add(("units", Located(id, "units", $"units[{i}] is null.")));
            if (def.Buildings is null)
                errors.Add(("buildings", Located(id, "buildings", "buildings list is null.")));
            else
                for (int i = 0; i < def.Buildings.Count; i++)
                    if (def.Buildings[i] is null)
                        errors.Add(("buildings", Located(id, "buildings", $"buildings[{i}] is null.")));
            if (errors.Count > 0)
                return new FactionValidationResult(errors);

            // ── Relocated (unchanged): per-building BuildingDefinitionValidator ─────────────────
            // (def.Buildings and its elements are non-null here — the structural pre-check above returned early on
            // a null list or null element; the `?? new List<>()` stays as defense in depth. BuildingDefinitionValidator
            // also null-tolerates an individual element internally.)
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                BuildingValidationResult result = BuildingDefinitionValidator.Validate(b);
                if (!result.Ok)
                    foreach ((string fieldPath, string message) in result.Errors)
                        errors.Add((fieldPath, message));
            }

            // ── Relocated (unchanged): TechTree / ResourceCost / Research ───────────────────────
            foreach (string message in TechTreeValidator.Validate(def))
                errors.Add(("prerequisites", message));

            foreach (string message in ResourceCostValidator.Validate(def))
                errors.Add(("cost", message));

            foreach (string message in ResearchValidator.Validate(def))
                errors.Add(("research", message));

            // ── New: ai_preset closed-set ────────────────────────────────────────────────────────
            string preset = def.AiPreset ?? "";
            if (!_knownAiPresets.Contains(preset))
                errors.Add(("ai_preset", Located(id, "ai_preset",
                    string.IsNullOrEmpty(preset)
                        ? "must be authored (empty is not a valid ai_preset)."
                        : $"'{preset}' is not a recognized ai_preset (known: {string.Join(", ", KnownAiPresets)}).")));

            // ── New: color array ─────────────────────────────────────────────────────────────────
            float[]? color = def.Color;
            if (color == null || color.Length != 4)
                errors.Add(("color", Located(id, "color",
                    $"must be an [r, g, b, a] array of length 4 (found length {color?.Length ?? 0}).")));
            else
            {
                for (int i = 0; i < color.Length; i++)
                {
                    float c = color[i];
                    if (float.IsNaN(c) || c < 0f || c > 1f)
                        errors.Add(("color", Located(id, "color",
                            $"component [{i}]={c} must be within [0, 1] (and not NaN).")));
                }
            }

            // ── New: duplicate unit id (mirrors TechTreeValidator's buildingById.TryAdd idiom) ─────
            // (def.Units and its elements are non-null here — the structural pre-check above already caught and
            // early-returned on a null list or null element; the guards below are defense in depth.) A whitespace-
            // only id is treated as "missing" (IsNullOrWhiteSpace), matching ValidateComplete's mesh_path check.
            var unitIds = new Dictionary<string, UnitDefinition>();
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null)
                {
                    errors.Add(("units", Located(id, "units", "a units[] entry is null.")));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(u.Id))
                {
                    errors.Add(("units", Located(id, "units", "a unit is missing an id.")));
                    continue;
                }
                if (!unitIds.TryAdd(u.Id, u))
                    errors.Add(("units", Located(id, "units",
                        $"duplicate unit id '{u.Id}' (another unit already uses this id).")));
            }

            return errors.Count == 0 ? FactionValidationResult.Valid : new FactionValidationResult(errors);
        }

        /// <summary>
        /// <see cref="Validate"/>'s errors PLUS the two roster-completeness checks that only make sense once a
        /// faction is meant to be finished/playable: a unit or building with a missing/empty <c>mesh_path</c>, and a
        /// roster missing a required role (no <c>"Worker"</c> unit, or no combat-category unit — see class doc for
        /// the exact definition). Exposed for future stories' own gates (wizard finish, playtest, selectability) —
        /// NEVER called by <see cref="FactionDefinition.LoadFromFile"/>. Returns every located error (list-all).
        /// Pure — never throws, never logs.
        /// </summary>
        public static FactionValidationResult ValidateComplete(FactionDefinition def)
        {
            FactionValidationResult baseResult = Validate(def);
            var errors = new List<(string, string)>(baseResult.Errors);

            if (def is null)
                return new FactionValidationResult(errors);

            string id = def.Id ?? "";

            // ── Missing mesh_path: units + buildings ─────────────────────────────────────────────
            // `?? new List<>()` guards a null list; an individual null element is skipped (not dereferenced) —
            // unlike Validate's building loop, nothing downstream here delegates to a null-tolerant sub-validator,
            // so this loop must guard itself.
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null) continue;
                if (string.IsNullOrWhiteSpace(u.MeshPath))
                    errors.Add(("mesh_path", Located(id, "mesh_path",
                        $"unit '{u.Id}' is missing mesh_path (required for a complete/playable faction).")));
            }
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;
                if (string.IsNullOrWhiteSpace(b.MeshPath))
                    errors.Add(("mesh_path", Located(id, "mesh_path",
                        $"building '{b.Id}' is missing mesh_path (required for a complete/playable faction).")));
            }

            // ── Missing required roles: Worker present AND >=1 of Melee/Ranged/Siege/Air present ────
            // "Required roles" definition (Design Notes): a minimum-viable-playable roster is an economy unit
            // (Worker) plus at least one thing that can fight (any combat category). Case-insensitive Category
            // match, mirroring GetUnitByCategory's convention.
            bool hasWorker = false;
            bool hasCombat = false;
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null) continue;
                string category = u.Category ?? "";
                if (string.Equals(category, "Worker", StringComparison.OrdinalIgnoreCase))
                {
                    hasWorker = true;
                    continue;
                }
                foreach (string combatCategory in _combatCategories)
                {
                    if (string.Equals(category, combatCategory, StringComparison.OrdinalIgnoreCase))
                    {
                        hasCombat = true;
                        break;
                    }
                }
            }
            if (!hasWorker)
                errors.Add(("units", Located(id, "units", "roster is missing a required Worker unit.")));
            if (!hasCombat)
                errors.Add(("units", Located(id, "units",
                    "roster is missing a required combat unit (Melee, Ranged, Siege, or Air).")));

            return errors.Count == 0 ? FactionValidationResult.Valid : new FactionValidationResult(errors);
        }

        /// <summary>The located error idiom — names the faction id + field path + reason, mirroring
        /// <see cref="BuildingDefinitionValidator"/>'s <c>Located</c>.</summary>
        private static string Located(string id, string path, string reason) =>
            $"faction '{id}'.{path}: {reason}";
    }
}
