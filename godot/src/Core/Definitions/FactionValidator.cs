#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

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

            // ── Story 15-21: the hero attribute model + per-hero attribute coherence ────────────────
            ValidateAttributeModel(errors, id, def);

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

            // ── DW-111: duplicate building id (self-contained; may co-report with TechTreeValidator's own check) ──
            // (def.Buildings and its elements are non-null here — the structural pre-check early-returned otherwise.)
            // A blank id is treated as "missing" and skipped, mirroring the duplicate-unit-id loop above.
            var buildingIds = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null || string.IsNullOrWhiteSpace(b.Id)) continue;
                if (!buildingIds.TryAdd(b.Id, b))
                    errors.Add(("buildings", Located(id, "buildings",
                        $"duplicate building id '{b.Id}' (another building already uses this id).")));
            }

            // ── DW-111: duplicate research id (self-contained; may co-report with ResearchValidator's own check) ──
            // def.Research is NOT covered by the structural pre-check (which guards only Units/Buildings), so a null
            // list or null element is guarded here directly.
            var researchIds = new Dictionary<string, ResearchDefinition>();
            foreach (ResearchDefinition r in def.Research ?? new List<ResearchDefinition>())
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Id)) continue;
                if (!researchIds.TryAdd(r.Id, r))
                    errors.Add(("research", Located(id, "research",
                        $"duplicate research id '{r.Id}' (another research entry already uses this id).")));
            }

            // ── DW-89: cross-namespace collision — a research id must not also be a building id ─────────────────
            // The one check no sub-validator covers (TechTreeValidator checks building ids, ResearchValidator checks
            // research ids, but neither compares the two namespaces). List-all across every colliding research id.
            foreach (string researchId in researchIds.Keys)
                if (buildingIds.ContainsKey(researchId))
                    errors.Add(("research", Located(id, "research",
                        $"research id '{researchId}' collides with a building id (ids must be unique across buildings and research).")));

            // ── DW-115: starting resources must be a finite value >= 0 (rejects negatives, NaN, and +/-Infinity) ──
            // Field paths named exactly starting_ore/starting_crystal — DW-114's wizard StepForError routing keys on them.
            if (!float.IsFinite(def.StartingOre) || def.StartingOre < 0f)
                errors.Add(("starting_ore", Located(id, "starting_ore",
                    $"must be a finite value >= 0 (found {def.StartingOre}).")));
            if (!float.IsFinite(def.StartingCrystal) || def.StartingCrystal < 0f)
                errors.Add(("starting_crystal", Located(id, "starting_crystal",
                    $"must be a finite value >= 0 (found {def.StartingCrystal}).")));

            return errors.Count == 0 ? FactionValidationResult.Valid : new FactionValidationResult(errors);
        }

        /// <summary>
        /// <see cref="Validate"/>'s errors PLUS the roster-completeness checks that only make sense once a
        /// faction is meant to be finished/playable: a unit or building with a missing/empty <c>mesh_path</c>, a
        /// roster missing a required role (no <c>"Worker"</c> unit, or no combat-category unit — see class doc for
        /// the exact definition), and the two descriptor-reference resolution checks added by Story 14.3 (DW-106):
        /// a non-empty <see cref="FactionDefinition.HeroUnitId"/> must name a unit in this faction's own
        /// <see cref="FactionDefinition.Units"/> roster, and a non-empty
        /// <see cref="FactionDefinition.SignatureMechanicEffectId"/> must resolve against a supplied
        /// <paramref name="abilityRegistry"/>.
        ///
        /// <para><b>Optional-registry semantics (Story 14.3).</b> <paramref name="abilityRegistry"/> defaults to
        /// <c>null</c> so every existing caller (<see cref="FactionDefinition.LoadSelectableFromDirectory"/>,
        /// <c>ScenarioLoadPhase</c>, tests) compiles and behaves unchanged. The hero check is registry-independent so
        /// it runs at EVERY <see cref="ValidateComplete"/> site; note that at the wizard save-gate a dangling
        /// <c>hero_unit_id</c> is pre-nulled by <c>FactionDefinerWizardCore.ClearStaleHeroReference</c> BEFORE this
        /// method is called, so in practice the located <c>hero_unit_id</c> error surfaces only at the non-wizard
        /// sites (discovery/match-load) — the wizard silently repairs it instead (Story 5.6, unchanged). The signature check needs the
        /// registry to resolve an ability id, so it fires ONLY when a registry is supplied — a null registry
        /// deliberately SKIPS it (resolution is impossible without one) rather than failing closed. Every REAL client
        /// site now threads one: the wizard save-gate (14.3), the Edit→Play launch gate (14.4), and — since DW-327 —
        /// the boot discovery scan (<see cref="FactionDefinition.LoadSelectableFromDirectory"/>) plus the boot
        /// match-load shadow (<c>SlotFactionResolver</c>), so "selectable" and "launchable" cannot disagree on a
        /// dangling signature id; a null registry now means a caller that genuinely has none (a test, or a
        /// pre-registry ordering). Both checks fire only for a non-empty field: a null/empty/whitespace
        /// <c>hero_unit_id</c>/<c>signature_mechanic_effect_id</c> is a legitimate unauthored-descriptor state (these
        /// fields default <c>null</c>) and passes.</para>
        ///
        /// <para><b>Why here, not <see cref="Validate"/> (epic-14 technical decision, supersedes DW-106's looser
        /// wording).</b> <see cref="Validate"/> runs on every <see cref="FactionDefinition.LoadFromFile"/> — including
        /// the Building/Unit Card Editors' lenient Save self-check — and takes no registry; wiring a registry-dependent
        /// or roster-completeness check there would break that path and re-open the editor regression the two-method
        /// split exists to prevent. These id-resolution checks therefore live in <see cref="ValidateComplete"/> only.</para>
        ///
        /// Exposed for callers' own gates (wizard finish, playtest, selectability) — NEVER called by
        /// <see cref="FactionDefinition.LoadFromFile"/>. Returns every located error (list-all). Pure — never throws,
        /// never logs.
        /// </summary>
        public static FactionValidationResult ValidateComplete(FactionDefinition def, AbilityRegistry? abilityRegistry = null)
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
            //
            // DW-505: these two are the only ITEM-level errors this faction-level validator emits, so they use
            // LocatedItem ("unit '<id>'.mesh_path: …"), NOT the faction-level Located ("faction '<id>'.mesh_path: …").
            // The kind label has to LEAD: FactionDefinerWizardCore.StepForError disambiguates a shared field path by
            // sniffing a leading "unit '"/"building '" — the same wording convention MeshAssetLint,
            // UnitDefinitionValidator, BuildingDefinitionValidator, TechTreeValidator and ResourceCostValidator all
            // emit — so the previous `faction '<id>'.mesh_path: unit '<id>' is missing…` shape defeated the sniff and
            // sent an author with a missing UNIT mesh_path to the Buildings & Tech step, which has no roster control
            // at all. The faction id is preserved in the tail, so no diagnostic detail is lost, and this now agrees
            // with MeshAssetLint's sibling dangling-path message on the very same mesh_path axis.
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null) continue;
                if (string.IsNullOrWhiteSpace(u.MeshPath))
                    errors.Add(("mesh_path", LocatedItem("unit", u.Id ?? "", "mesh_path",
                        $"must be authored (required for a complete/playable faction '{id}').")));
            }
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;
                if (string.IsNullOrWhiteSpace(b.MeshPath))
                    errors.Add(("mesh_path", LocatedItem("building", b.Id ?? "", "mesh_path",
                        $"must be authored (required for a complete/playable faction '{id}').")));
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
                foreach (string combatCategory in UnitCategories.Combat)
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
                    $"roster is missing a required combat unit ({UnitCategories.CombatOrPhrase}).")));

            // ── DW-106: hero_unit_id resolves against this faction's own roster ──────────────────────
            // Registry-independent, so effective at every ValidateComplete site (discovery/match-load included; the
            // wizard save-gate pre-nulls a dangling ref via ClearStaleHeroReference, so it never trips there).
            // Only fires for a non-empty id — a null/empty HeroUnitId is a legitimate unauthored-descriptor state.
            // Id match is ordinal/case-sensitive by design: unit and ability ids are case-sensitive keys throughout
            // (AbilityRegistry.IndexOf is ordinal too), UNLIKE the deliberately case-insensitive ai_preset/Category
            // closed-set checks above — those are human-facing category tokens, these are exact reference ids.
            if (!string.IsNullOrWhiteSpace(def.HeroUnitId) && def.Units != null
                && !def.Units.Any(u => u != null && u.Id == def.HeroUnitId))
                errors.Add(("hero_unit_id", Located(id, "hero_unit_id",
                    $"names unit '{def.HeroUnitId}' which is not in this faction's roster.")));

            // ── DW-106: signature_mechanic_effect_id resolves against a supplied AbilityRegistry ─────
            // Fires ONLY when a registry is supplied (resolution needs one); a null registry skips it. Only for a
            // non-empty id — a null/empty SignatureMechanicEffectId is a legitimate unauthored-descriptor state.
            if (abilityRegistry != null && !string.IsNullOrWhiteSpace(def.SignatureMechanicEffectId)
                && abilityRegistry.IndexOf(def.SignatureMechanicEffectId) < 0)
                errors.Add(("signature_mechanic_effect_id", Located(id, "signature_mechanic_effect_id",
                    $"'{def.SignatureMechanicEffectId}' does not resolve to any loaded ability.")));

            return errors.Count == 0 ? FactionValidationResult.Valid : new FactionValidationResult(errors);
        }

        /// <summary>The located error idiom — names the faction id + field path + reason, mirroring
        /// <see cref="BuildingDefinitionValidator"/>'s <c>Located</c>. For a FACTION-level fault only; an error about
        /// one roster item uses <see cref="LocatedItem"/> so the item kind label leads (DW-505).</summary>
        private static string Located(string id, string path, string reason) =>
            $"faction '{id}'.{path}: {reason}";

        /// <summary>The ITEM-level located error idiom — <c>"{kind} '{id}'.{path}: {reason}"</c>, the shape
        /// <see cref="MeshAssetLint"/>/<see cref="UnitDefinitionValidator"/>/<see cref="BuildingDefinitionValidator"/>/
        /// <see cref="TechTreeValidator"/>/<see cref="ResourceCostValidator"/> all emit and the shape
        /// <see cref="FactionDefinerWizardCore.StepForError"/> routes on. Use this — never <see cref="Located"/> — for
        /// any error whose subject is a single unit/building rather than the faction as a whole, so the wizard jumps
        /// the author to the step that actually owns the offending item (DW-505).</summary>
        private static string LocatedItem(string kind, string itemId, string path, string reason) =>
            $"{kind} '{itemId}'.{path}: {reason}";

        // ── Story 15-21: hero attribute model bounds ─────────────────────────────────────────────────────────────
        // Overflow arithmetic these caps guarantee (16.16 Fixed saturates ~32767): a resolved per-stat contribution
        // is bounded by AttrPerPointMax × AttrValueMax(base) = 256 × 1024? NO — the caps below bound the RESOLVED
        // sums directly (the resolver output), which is what the growth modifier actually installs:
        //   resolvedBase[stat] ≤ AttrResolvedBaseMax (4096) and resolvedPerLevel[stat] ≤ AttrResolvedPerLevelMax
        //   (256) ⇒ worst lifetime contribution = 4096 + 256 × 99 stacks = 29,440 < 32,767 — Fixed-safe beside the
        //   flat hero.*_per_level growth, whose own DW-488 bound already accounts for its half.
        private const float AttrValueMax           = 4096f; // a single authored base/per_level attribute value
        private const float AttrPerPointMax        = 256f;  // a single derived-rule per_point coefficient
        private const float AttrResolvedBaseMax    = 4096f; // Σ per-stat resolved base contribution
        private const float AttrResolvedPerLevelMax = 256f; // Σ per-stat resolved per-level contribution

        /// <summary>
        /// Story 15-21 — validate the faction's <c>attribute_model</c> (declared attributes + derived mapping,
        /// closed stat vocabulary fail-closed) and every hero's <c>attributes</c> block against it (keys/primary ∈
        /// declared ids, non-negative finite values, resolved-contribution overflow caps). All rules are
        /// list-all-errors (never first-fail), each on a located field path so the wizard can route it.
        /// Negative values and negative <c>per_point</c> are REJECTED for v1 (a negative max-health contribution
        /// could ceiling-collapse a hero at mint — the DW-325 class; drawback attributes are a future decision).
        /// </summary>
        private static void ValidateAttributeModel(List<(string, string)> errors, string id, FactionDefinition def)
        {
            AttributeModelDefinition? model = def.AttributeModel;
            var declared = new HashSet<string>(StringComparer.Ordinal);

            if (model != null)
            {
                // Declared attributes: non-empty unique ids.
                var attrs = model.Attributes;
                if (attrs == null || attrs.Count == 0)
                    errors.Add(("attribute_model", Located(id, "attribute_model.attributes",
                        "an attribute_model must declare at least one attribute (or remove the block).")));
                else
                {
                    for (int i = 0; i < attrs.Count; i++)
                    {
                        string aid = attrs[i]?.Id ?? "";
                        if (string.IsNullOrWhiteSpace(aid))
                            errors.Add(("attribute_model", Located(id, $"attribute_model.attributes[{i}].id",
                                "attribute id must be authored (empty is not a valid id).")));
                        else if (string.Equals(aid, "primary", StringComparison.Ordinal))
                            errors.Add(("attribute_model", Located(id, $"attribute_model.attributes[{i}].id",
                                "'primary' is the reserved per-hero selector and cannot be a declared attribute id.")));
                        else if (!declared.Add(aid))
                            errors.Add(("attribute_model", Located(id, $"attribute_model.attributes[{i}].id",
                                $"duplicate attribute id '{aid}' (ids must be unique).")));
                    }
                }

                // Derived rules: attribute ∈ declared ∪ {"primary"}, stat ∈ the CLOSED vocabulary, bounded per_point.
                var derived = model.Derived;
                if (derived != null)
                {
                    for (int i = 0; i < derived.Count; i++)
                    {
                        DerivedStatRule? r = derived[i];
                        if (r == null) { errors.Add(("attribute_model", Located(id, $"attribute_model.derived[{i}]", "rule is null."))); continue; }
                        string attr = r.Attribute ?? "";
                        if (!string.Equals(attr, "primary", StringComparison.Ordinal) && !declared.Contains(attr))
                            errors.Add(("attribute_model", Located(id, $"attribute_model.derived[{i}].attribute",
                                $"'{attr}' is not a declared attribute id (or the reserved 'primary' selector).")));
                        if (!AttributeStats.TryIndexOf(r.Stat, out _))
                            errors.Add(("attribute_model", Located(id, $"attribute_model.derived[{i}].stat",
                                $"'{r.Stat}' is not in the closed derived-stat vocabulary ({string.Join(", ", AttributeStats.Ids)}).")));
                        if (!float.IsFinite(r.PerPoint) || r.PerPoint < 0f || r.PerPoint >= AttrPerPointMax)
                            errors.Add(("attribute_model", Located(id, $"attribute_model.derived[{i}].per_point",
                                $"={r.PerPoint} must be finite and in [0, {(int)AttrPerPointMax}).")));
                    }
                }
            }

            // Per-hero attribute blocks: require a model, reference only declared ids, bounded non-negative values,
            // and cap the RESOLVED contributions (the same resolver the runtime uses — no drift).
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                HeroAttributesDefinition? ha = u?.Hero?.Attributes;
                if (ha == null) continue;
                string uid = u!.Id ?? "";

                if (model == null)
                {
                    errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes",
                        "hero authors attributes but the faction declares no attribute_model — add the model (or a preset) first.")));
                    continue;
                }

                if (!string.IsNullOrEmpty(ha.Primary) && !declared.Contains(ha.Primary!))
                    errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes.primary",
                        $"'{ha.Primary}' is not a declared attribute id.")));

                CheckHeroAttrValues(errors, uid, "hero.attributes.base", ha.Base, declared);
                CheckHeroAttrValues(errors, uid, "hero.attributes.per_level", ha.PerLevel, declared);

                // Resolved-contribution caps (the DW-650 mirror for the attribute half of the growth modifier).
                var (rBase, rPerLevel) = HeroAttributeResolver.Resolve(model, ha);
                for (int s = 0; s < AttributeStats.Count; s++)
                {
                    var statDef = ProjectChimera.Core.Stats.StatVocabulary.All[s];
                    if (rBase[s].ToFloat() >= AttrResolvedBaseMax)
                        errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes",
                            $"resolved base contribution to '{statDef.JsonName}' ({rBase[s].ToFloat():0.##}) exceeds the Fixed-safe cap {(int)AttrResolvedBaseMax}.")));
                    if (rPerLevel[s].ToFloat() >= AttrResolvedPerLevelMax)
                        errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes",
                            $"resolved per-level contribution to '{statDef.JsonName}' ({rPerLevel[s].ToFloat():0.##}) exceeds the Fixed-safe cap {(int)AttrResolvedPerLevelMax} (99-stack overflow guard).")));
                    // Story 15-24a: a stat with a registry per-delta cap (the percent family) caps its RESOLVED
                    // contributions there too — a "+9000% attack damage at level 1" model must fail authoring,
                    // not silently clamp to the recompute's Σ bound in play (validated-but-misbehaving content).
                    if (statDef.MaxAbsDeltaRaw != 0)
                    {
                        if (rBase[s].Raw > statDef.MaxAbsDeltaRaw || rBase[s].Raw < -statDef.MaxAbsDeltaRaw)
                            errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes",
                                $"resolved base contribution to '{statDef.JsonName}' ({rBase[s].ToFloat():0.##}) exceeds its per-stat cap ({Fixed.FromRaw(statDef.MaxAbsDeltaRaw).ToFloat():0.##} — a fraction-valued stat).")));
                        if (rPerLevel[s].Raw > statDef.MaxAbsDeltaRaw || rPerLevel[s].Raw < -statDef.MaxAbsDeltaRaw)
                            errors.Add(("attribute_model", LocatedItem("unit", uid, "hero.attributes",
                                $"resolved per-level contribution to '{statDef.JsonName}' ({rPerLevel[s].ToFloat():0.##}) exceeds its per-stat cap ({Fixed.FromRaw(statDef.MaxAbsDeltaRaw).ToFloat():0.##} — a fraction-valued stat).")));
                    }
                }
            }
        }

        /// <summary>One hero attribute value dictionary: every key ∈ declared ids, every value finite, non-negative,
        /// below <see cref="AttrValueMax"/>. Iterates a SORTED key copy so error ORDER is deterministic (the
        /// validator's list-all contract; never raw dictionary enumeration order).</summary>
        private static void CheckHeroAttrValues(List<(string, string)> errors, string uid, string path,
                                                Dictionary<string, float>? values, HashSet<string> declared)
        {
            if (values == null || values.Count == 0) return;
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                if (!declared.Contains(k))
                    errors.Add(("attribute_model", LocatedItem("unit", uid, $"{path}.{k}",
                        $"'{k}' is not a declared attribute id.")));
                float v = values[k];
                if (!float.IsFinite(v) || v < 0f || v >= AttrValueMax)
                    errors.Add(("attribute_model", LocatedItem("unit", uid, $"{path}.{k}",
                        $"={v} must be finite and in [0, {(int)AttrValueMax}).")));
            }
        }
    }
}
