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
    /// </summary>
    public static class BuildingDefinitionValidator
    {
        /// <summary>
        /// Validate a single <paramref name="def"/>. Returns every located field error (required-but-missing
        /// <c>construction_time</c>/<c>supply_bonus</c>/<c>produces_category</c>). Pure — never throws, never logs.
        /// </summary>
        public static BuildingValidationResult Validate(BuildingDefinition def)
        {
            var errors = new List<(string, string)>();
            if (def is null)
            {
                errors.Add(("building", "building is null."));
                return new BuildingValidationResult(errors);
            }

            string id = def.Id ?? "";

            // Review pass (Story 4.1): Hp is no longer vestigial once a resolved def is threaded through
            // BuildingStore.Create (BuildingSystem.PlaceBuildingDirect/QueueWorkerBuild), so a non-positive value is
            // rejected the same as the other now-load-bearing fields. This does NOT catch an omitted `hp` silently
            // defaulting to UnitDefinition's 100f (Hp is inherited as a non-nullable float, unlike the three
            // required-nullable fields below) — closing that fully requires either a UnitDefinition-wide nullable-Hp
            // change or JSON-presence tracking, both out of proportion here; deferred (see deferred-work.md).
            if (def.Hp <= 0f)
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

            return new BuildingValidationResult(errors);
        }

        /// <summary>The located error idiom — names the building id + field path + reason, mirroring
        /// <see cref="UnitDefinitionValidator"/>'s <c>Located</c>.</summary>
        private static string Located(string id, string path, string reason) =>
            $"building '{id}'.{path}: {reason}";
    }
}
