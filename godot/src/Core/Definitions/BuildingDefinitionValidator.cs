#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The result of a <see cref="BuildingDefinitionValidator"/> pass (Story 4.1) — mirrors
    /// <see cref="UnitValidationResult"/>'s shape (a LIST of located errors, not first-fail) so
    /// <see cref="FactionDefinition.LoadFromFile"/> can report every missing field across every bad building at once.
    /// Pure, no logging, no throw — the caller (<see cref="FactionDefinition.LoadFromFile"/>) decides to throw.
    /// </summary>
    public readonly struct BuildingValidationResult
    {
        /// <summary>True when the building passed every check (no errors).</summary>
        public bool Ok => Errors.Count == 0;

        /// <summary>Every located field error found. Empty when the building is valid.</summary>
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }

        internal BuildingValidationResult(IReadOnlyList<(string, string)> errors) => Errors = errors;

        /// <summary>The always-valid result (no errors) — a shared empty instance.</summary>
        public static readonly BuildingValidationResult Valid =
            new BuildingValidationResult(System.Array.Empty<(string, string)>());
    }

    /// <summary>
    /// Import-time gate for authored <see cref="BuildingDefinition"/>s (Story 4.1). A building entry loaded from faction
    /// JSON that omits <see cref="BuildingDefinition.ConstructionTime"/>, <see cref="BuildingDefinition.SupplyBonus"/>,
    /// or <see cref="BuildingDefinition.ProducesCategory"/> is a located, list-all error (mirrors
    /// <see cref="UnitDefinitionValidator"/>'s D-9 shape so an editor could one day badge every offending field at
    /// once) — <see cref="FactionDefinition.LoadFromFile"/> throws when any building fails.
    ///
    /// Deliberately lightweight: does NOT mint a <see cref="Validated{T}"/> token (no applier consumes one for
    /// buildings yet); pure C#, Godot-free, no float gameplay math — it reads authoring values and reports strings.
    ///
    /// <para><b>Story 4.5.</b> The <see cref="Validate(BuildingDefinition,IReadOnlyList{BuildingDefinition}?)"/>
    /// overload additionally merges <see cref="UnitDefinitionValidator"/>'s full id/dup-id/enum/cost-range gate (kinded
    /// <c>"building"</c>) over the same def, so the Building Card Editor gets the same coverage
    /// <see cref="UnitDefinitionValidator"/> already gives units — reused via <c>IReadOnlyList&lt;T&gt;</c> covariance
    /// (a <c>List&lt;BuildingDefinition&gt;</c> passes directly as an <c>IReadOnlyList&lt;UnitDefinition&gt;</c>) instead
    /// of duplicating ~20 checks. The pre-4.5 single-arg <see cref="Validate(BuildingDefinition)"/> overload —
    /// <see cref="FactionDefinition.LoadFromFile"/>'s call path — is now a thin <c>Validate(def, null)</c> forward, so it
    /// compiles unchanged and (with no siblings supplied) skips only the duplicate-id check, exactly like the pre-4.5
    /// behavior it replaces (which never checked ids at all).</para>
    /// </summary>
    public static class BuildingDefinitionValidator
    {
        /// <summary>
        /// Validate a single <paramref name="def"/> with no sibling list (no duplicate-id check) — the
        /// <see cref="FactionDefinition.LoadFromFile"/> call path. Forwards to the siblings-aware overload with
        /// <c>siblings: null</c>.
        /// </summary>
        public static BuildingValidationResult Validate(BuildingDefinition def) => Validate(def, null);

        /// <summary>
        /// Validate a single <paramref name="def"/> against its <paramref name="siblings"/> (the faction's
        /// <c>Buildings</c> list, for the uniqueness rule — Story 4.5). Returns every located field error: the four
        /// building-only checks (required-but-missing <c>construction_time</c>/<c>supply_bonus</c>/
        /// <c>produces_category</c>, non-positive <c>hp</c>) PLUS every error <see cref="UnitDefinitionValidator"/>
        /// would report for the same def kinded <c>"building"</c> (id/dup-id/enum/cost-range/…). Pure — never throws,
        /// never logs.
        /// </summary>
        public static BuildingValidationResult Validate(BuildingDefinition def, IReadOnlyList<BuildingDefinition>? siblings)
        {
            var errors = new List<(string, string)>();
            if (def is null)
            {
                errors.Add(("building", "building is null."));
                return new BuildingValidationResult(errors);
            }

            string id = def.Id ?? "";

            // DW-55: Hp is load-bearing once a resolved def is threaded through BuildingStore.Create
            // (BuildingSystem.PlaceBuildingDirect/QueueWorkerBuild). A building that never AUTHORED hp (it silently
            // defaults to UnitDefinition's 100f) is now a distinct located "required but missing" error — the
            // BuildingDefinition.HpAuthored presence flag (set through any Hp assignment path) tells an omitted hp
            // apart from an authored 100. An authored-but-non-positive hp still reports the pre-existing error.
            if (!def.HpAuthored)
                errors.Add(("hp", Located(id, "hp",
                    "is required but missing (a building's HP must be authored).")));
            else if (!float.IsFinite(def.Hp) || def.Hp <= 0f)
                errors.Add(("hp", Located(id, "hp",
                    "must be a positive value (a building's HP must be authored above zero).")));

            if (!def.ConstructionTime.HasValue)
                errors.Add(("construction_time", Located(id, "construction_time",
                    "is required but missing (a building's construction duration must be authored).")));

            if (!def.SupplyBonus.HasValue)
                errors.Add(("supply_bonus", Located(id, "supply_bonus",
                    "is required but missing (author 0 for a building that grants no supply).")));

            if (string.IsNullOrEmpty(def.ProducesCategory))
                errors.Add(("produces_category", Located(id, "produces_category",
                    "is required but missing (author the unit category this building produces, or \"None\" for a non-producer).")));

            // Command-card producer surface (this story): OPTIONAL — an omitted/empty value derives the surface. When
            // authored it must name one of the five known surfaces (case-insensitive); any other value is a located
            // import-time error so BuildingSystem.ResolveCommandCardSurface never has to silently ignore a typo.
            if (!string.IsNullOrEmpty(def.CommandCardProducer)
                && !IsKnownCommandCardProducer(def.CommandCardProducer))
                errors.Add(("command_card_producer", Located(id, "command_card_producer",
                    "must be one of train, research, shop, revive, none (or omit to derive).")));

            // Story 4.5: reuse UnitDefinitionValidator's id/dup-id/enum/cost-range gate over this same def, kinded
            // "building" so every message reads "building '<id>'…" instead of "unit '<id>'…". IReadOnlyList<T>'s
            // covariance lets `siblings` (IReadOnlyList<BuildingDefinition>?) pass directly as the expected
            // IReadOnlyList<UnitDefinition>? — no copying. No ability/behavior/item registry — buildings don't author
            // abilities[]/behaviors[]/shop_stock[] (BuildingCardPanel never wires those pickers).
            UnitValidationResult unitResult = new UnitDefinitionValidator().Validate(
                def, registry: null, behaviorRegistry: null, itemRegistry: null, siblings, kind: "building");
            foreach ((string fieldPath, string message) in unitResult.Errors)
                errors.Add((fieldPath, message));

            return new BuildingValidationResult(errors);
        }

        /// <summary>Case-insensitive membership check for an authored <c>command_card_producer</c> value against the
        /// five known surfaces. The single source of truth the validator + <see cref="BuildingValidationResult"/>
        /// share so a typo can never map to a rendered surface.</summary>
        private static bool IsKnownCommandCardProducer(string value) =>
            value.Equals("train", System.StringComparison.OrdinalIgnoreCase)
            || value.Equals("research", System.StringComparison.OrdinalIgnoreCase)
            || value.Equals("shop", System.StringComparison.OrdinalIgnoreCase)
            || value.Equals("revive", System.StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>The located error idiom — names the building id + field path + reason, mirroring
        /// <see cref="UnitDefinitionValidator"/>'s <c>Located</c>.</summary>
        private static string Located(string id, string path, string reason) =>
            $"building '{id}'.{path}: {reason}";
    }
}
