#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Closed-set fail-closed validation for authored <c>UnitDefinition.tags</c> (Story 2.11, AC2). Units are NEVER
    /// content-validated today (no <c>FactionValidator</c>/<c>UnitValidator</c> exists — a full one is an Epic-5.2
    /// deliverable, and the lenient faction loader has no <c>Disallow</c>/converters), and <see cref="UnitDefinition.ParsedTags"/>
    /// is deliberately LENIENT (an unknown token folds to <see cref="UnitTag.None"/>). This is the missing REJECT half of
    /// that accessor/validator split (the same split the codebase uses for <c>AbilityDefinition.ParsedTargeting</c> →
    /// <c>AbilityValidator</c>): it names the offending unit id + token in a located error and DROPS the unit, so no bad
    /// definition ever spawns.
    ///
    /// D-2 (minimal slice): this is a lightweight fail-closed DROP, NOT a new <see cref="Validated{T}"/> minter — it does
    /// not mint a proof token (so it needs no entry in the sole-minter allow-list) and does not pre-empt the Epic-5.2
    /// <c>FactionValidator</c>. Godot-free (pure C#, <c>src/Core/Definitions</c>) so it runs identically on the client
    /// pre-pass (<c>ScenarioLoadPhase</c>) and the server pre-pass (<c>ServerBootstrap</c>) — client/server parity before
    /// any <c>SpawnUnit</c>. Each caller logs the returned located errors with its own sink (Godot <c>GD.PrintErr</c> /
    /// <c>ILogSink</c>).
    /// </summary>
    public static class UnitTagValidator
    {
        /// <summary>The closed set of authorable unit tags (exact-case, mirrors <see cref="UnitDefinition.ParsedTags"/>'s
        /// recognized tokens and <see cref="UnitTag"/>'s members). Static — allocated once (the <c>ScenarioValidator</c>
        /// closed-set idiom), so the per-unit scan allocates nothing.</summary>
        private static readonly string[] _knownTags = { "Organic", "Mechanical", "Magical" };

        /// <summary>Exact-match membership in the closed tag set (case-sensitive; null is never a member) — the
        /// <c>ScenarioValidator.InSet</c> idiom.</summary>
        private static bool InSet(string? value)
        {
            if (value is null) return false;
            for (int i = 0; i < _knownTags.Length; i++)
                if (_knownTags[i] == value) return true;
            return false;
        }

        /// <summary>
        /// Finds the FIRST tag on <paramref name="def"/> that is not in the closed set and reports it via
        /// <paramref name="token"/>. Returns <c>true</c> when an offender is found (the offender MAY itself be
        /// <c>null</c> — a JSON <c>[null]</c> element is not a known tag); returns <c>false</c> only when EVERY tag is
        /// valid (including the null/empty back-compat case). The bool decouples "found an offender" from the offender's
        /// VALUE, so a <c>null</c> array element can never masquerade as the all-valid result (Story 2.11 review C2: the
        /// earlier <c>string?</c>-returning shape used <c>null</c> for BOTH "all valid" and "the offender is null", so
        /// <c>tags:[null,"Undead"]</c> silently passed fail-closed validation AND masked the co-located real typo).
        /// Scans the raw strings (NOT <see cref="UnitDefinition.ParsedTags"/>, which would silently swallow the bad token
        /// to <see cref="UnitTag.None"/>) so the exact offending string can be named.
        /// </summary>
        public static bool TryFindInvalidTag(UnitDefinition def, out string? token)
        {
            token = null;
            string[]? tags = def.Tags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Length; i++)
                if (!InSet(tags[i])) { token = tags[i]; return true; }
            return false;
        }

        /// <summary>The located error for a bad tag, mirroring <c>AbilityValidator.Located</c> ("ability '{id}'.{path}: {reason}")
        /// but for the unit axis — names BOTH the unit id and the offending token (AC2.2). A <c>null</c> token (a JSON
        /// <c>[null]</c> element) renders as <c>(null)</c> so the message stays readable.</summary>
        public static string Located(string unitId, string? token) =>
            $"unit '{unitId}'.tags: '{token ?? "(null)"}' is not a known unit tag (Organic|Mechanical|Magical).";

        /// <summary>
        /// Validate every unit in <paramref name="faction"/>'s <c>Units</c> list and DROP each one carrying an unknown
        /// tag (AC2.3 fail-closed per-item drop — mirrors <c>AbilityRegistry</c> keeping only <c>Ok</c> abilities). A
        /// dropped unit makes <c>FactionDefinition.GetUnit</c> return null → the applier's <c>def == null → skip</c> sink
        /// runs → no <c>Create</c>/<c>SpawnUnit</c> → no <c>EntityWorld</c> slot; the rest of the faction still loads.
        /// Returns the located error strings (one per dropped unit) for the caller to log with its own sink.
        ///
        /// Enumeration-safety: collects offenders in a FIRST read-only pass, then removes them in a SECOND pass via
        /// <c>List.RemoveAll</c> — NEVER removes inside a <c>foreach</c> over <c>faction.Units</c> (which would throw
        /// <c>InvalidOperationException</c> for mutating the list mid-enumeration). Buildings are out of scope (units-only,
        /// AC2 — building defs never route through <c>ApplyUnitDefinition</c>, so no tag predicate reads them).
        /// </summary>
        public static List<string> ValidateAndDropUnits(FactionDefinition faction)
        {
            var errors = new List<string>();
            // First pass: identify offenders (read-only — do NOT mutate faction.Units here).
            foreach (UnitDefinition u in faction.Units)
            {
                if (TryFindInvalidTag(u, out string? bad)) errors.Add(Located(u.Id, bad));
            }
            // Second pass: drop every unit carrying an invalid tag (RemoveAll does its own safe in-place compaction —
            // it is NOT a foreach over the list, so this does not hit the mid-enumeration-mutation trap).
            if (errors.Count > 0)
                faction.Units.RemoveAll(u => TryFindInvalidTag(u, out _));
            return errors;
        }
    }
}
