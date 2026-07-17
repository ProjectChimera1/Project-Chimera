#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Pure (Godot-free) fail-closed roster-completeness decision for the client Edit→Play launch boundary
    /// (Story 14.4, DW-97). Splits the DECISION from the side effects (the Story 1.7 split-decision idiom): this
    /// type runs <see cref="FactionValidator.ValidateComplete"/> over every resolved
    /// slot faction and returns the FIRST located block reason (or <c>null</c> when every faction is complete);
    /// the caller (<c>MainScene.ResetToAuthoredStart</c>) — the only layer allowed to touch Godot — performs the
    /// presentation side effects (<c>GD.PrintErr</c>, the HUD toast) and the actual veto.
    ///
    /// <para><b>Why split out of <c>MainScene</c>.</b> <c>ResetToAuthoredStart</c> is a Godot <c>Node</c> method
    /// outside the Tier-1 Godot-free test globs, so its launch-gate logic cannot be unit-tested directly — the same
    /// constraint that drove Story 1.7's Godot-free gate extraction. Living here, in
    /// <c>src/Core/Definitions</c>, this decision compiles into the Godot-free Tier-1 test assembly and is unit-
    /// testable; the located reason it returns is presentation-surfaced by the caller.</para>
    ///
    /// <para><b>Registry threading (closes 14.3's deferral at the launch gate).</b> Story 14.3 shipped
    /// <see cref="FactionValidator.ValidateComplete"/> with an optional registry and left threading one at the
    /// launch gate to 14.4. The caller threads the real loaded <c>AbilityRegistry</c> through here so the
    /// <c>signature_mechanic_effect_id</c> resolution check fires when entering Play. This closes the deferral at
    /// the launch gate ONLY — the other two <c>ValidateComplete</c> sites (the boot match-load shadow diagnostic in
    /// <c>ScenarioLoadPhase</c> and the boot discovery scan in <c>FactionDefinition.LoadSelectableFromDirectory</c>)
    /// still call it registry-less by design, so the signature check stays dormant there. A <c>null</c>/empty
    /// registry deliberately skips ONLY the signature check (existing <see cref="FactionValidator.ValidateComplete"/>
    /// semantics); the hero/roster/mesh checks still enforce.</para>
    ///
    /// Pure — never throws, never logs.
    /// </summary>
    public static class FactionLaunchGate
    {
        /// <summary>
        /// Runs <see cref="FactionValidator.ValidateComplete"/> over every non-null entry of
        /// <paramref name="slotFactionDefs"/> and returns the first located block reason
        /// (<c>"faction '&lt;id&gt;' is incomplete:\n&lt;message&gt;"</c>), or <c>null</c> when every non-null slot
        /// faction is complete. Null slot entries are skipped (a slot may be unresolved). The signature check fires
        /// only when <paramref name="abilityRegistry"/> is supplied (existing <c>ValidateComplete</c> semantics).
        /// Pure: no logging, no side effects.
        /// </summary>
        /// <param name="slotFactionDefs">The resolved per-slot faction definitions; entries may be null.</param>
        /// <param name="abilityRegistry">Registry used to resolve <c>signature_mechanic_effect_id</c>; null skips
        /// only that check.</param>
        /// <returns>The first located incompleteness reason, or <c>null</c> if all non-null defs are complete.</returns>
        public static string? FirstIncompleteReason(
            IReadOnlyList<FactionDefinition?> slotFactionDefs, AbilityRegistry? abilityRegistry)
        {
            if (slotFactionDefs == null) return null;
            foreach (FactionDefinition? def in slotFactionDefs)
            {
                if (def is null) continue;
                FactionValidationResult r = FactionValidator.ValidateComplete(def, abilityRegistry);
                if (!r.Ok)
                {
                    string msg = r.Errors.Count > 0 ? r.Errors[0].Message : "faction is incomplete";
                    return $"faction '{def.Id}' is incomplete:\n{msg}";
                }
            }
            return null;
        }
    }
}
