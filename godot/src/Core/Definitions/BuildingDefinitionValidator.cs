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
        /// <c>Buildings</c> list, for the uniqueness rule — Story 4.5). Returns every located field error: the
        /// building-only checks (required-but-missing <c>hp</c>/<c>construction_time</c>/<c>supply_bonus</c>/
        /// <c>produces_category</c>, plus the optional <c>command_card_producer</c>/<c>nav_footprint</c> shapes) PLUS
        /// every error <see cref="UnitDefinitionValidator"/> would report for the same def kinded <c>"building"</c>
        /// (id/dup-id/enum/cost-range/stat-bounds/…). Pure — never throws, never logs.
        ///
        /// <para><b>DW-527.</b> The non-positive-<c>hp</c> VALUE rule is no longer duplicated here — the shared unit
        /// gate owns it (strictly positive, finite, below the 16.16 ceiling) and this validator inherits it through the
        /// reuse below, so a bad building hp badges its control exactly ONCE (D-9). Only the <c>HpAuthored</c> PRESENCE
        /// check, which the shared gate cannot express, stayed behind.</para>
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
            // defaults to UnitDefinition's 100f) is a distinct located "required but missing" error — the
            // BuildingDefinition.HpAuthored presence flag (set through any Hp assignment path) tells an omitted hp
            // apart from an authored 100.
            //
            // DW-527: the VALUE half of the old rule (`!IsFinite(Hp) || Hp <= 0f`) is GONE from here. It now lives in
            // UnitDefinitionValidator's shared stat gate, which this validator already reuses below over the very same
            // def — so keeping a local copy would emit TWO located errors keyed "hp" for one control on every
            // non-positive / non-finite building hp (it already did, for a negative or NaN hp, because the shared gate's
            // generic bound rejected those too), which is exactly the doubled per-field badge (D-9) DW-380 had to fix
            // for attack_speed. Deleting it loses NO coverage: the shared rule is a strict superset — non-positive,
            // non-finite, AND at/above the 16.16 ceiling, the last of which the old `<= 0f` branch never looked at (it
            // was already the reused gate that caught it). What stays here is the one rule the shared gate cannot
            // express — PRESENCE — because HpAuthored is a BuildingDefinition-only flag the unit gate knows nothing of.
            bool hpBadged = false;
            if (!def.HpAuthored)
            {
                errors.Add(("hp", Located(id, "hp",
                    "is required but missing (a building's HP must be authored).")));
                hpBadged = true;
            }

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

            // DW-169: nav_footprint is OPTIONAL, but when authored it must satisfy the SAME rule the footprint
            // resolution policy applies (BuildingDefinition.TryGetNavFootprint — exactly [x, y, z], all finite and
            // strictly positive). Reporting the malformed value here keeps "validated content" and "content the
            // resolver honors" the same set — a typo'd footprint is a located import-time error, never a silent
            // fall-through to the mesh-AABB/default footprint.
            if (def.NavFootprint != null && !def.TryGetNavFootprint(out _, out _, out _))
                errors.Add(("nav_footprint", Located(id, "nav_footprint",
                    "must be exactly [width_x, height_y, depth_z] — 3 finite values, each greater than zero (or omit to derive from the mesh).")));

            // Story 4.5: reuse UnitDefinitionValidator's id/dup-id/enum/cost-range gate over this same def, kinded
            // "building" so every message reads "building '<id>'…" instead of "unit '<id>'…". IReadOnlyList<T>'s
            // covariance lets `siblings` (IReadOnlyList<BuildingDefinition>?) pass directly as the expected
            // IReadOnlyList<UnitDefinition>? — no copying. No ability/behavior/item registry — buildings don't author
            // abilities[]/behaviors[]/shop_stock[] (BuildingCardPanel never wires those pickers).
            UnitValidationResult unitResult = new UnitDefinitionValidator().Validate(
                def, registry: null, behaviorRegistry: null, itemRegistry: null, siblings, kind: "building");
            foreach ((string fieldPath, string message) in unitResult.Errors)
            {
                // DW-527, one badge per field (D-9): a building that never AUTHORED hp is already badged above with the
                // strictly more actionable "required but missing" message, so the shared gate's verdict on the value it
                // merely INHERITED must not add a second badge to the same control. In every real authoring path an
                // un-authored hp is UnitDefinition's 100f default, which the shared rule passes anyway — this guard only
                // bites for a def whose Hp was written through a UnitDefinition-typed reference (the non-virtual `new`
                // shadow does not flag those), and it is what makes "exactly one hp error" a property of the merge
                // rather than an accident of the default value.
                if (hpBadged && fieldPath == "hp") continue;
                errors.Add((fieldPath, message));
            }

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
