using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectChimera.Core
{
    /// <summary>
    /// The six unit archetypes (the only "types" in the data-driven model — everything else composes).
    /// Parsed from <c>UnitDefinition.Category</c> at spawn into <c>EntityWorld.CategoryOf</c> and read ONLY
    /// by the presentation-side <see cref="ProjectChimera.Navigation.FormationPlanner"/> to decide which units
    /// lead (front line) and which trail (back line) in a multi-unit move (Story 1.13, DG-2 / FR-54).
    ///
    /// NOT folded into <c>SimChecksum</c> — it is presentation-read (like <c>EntityWorld.MeshType</c>): the
    /// formation it shapes is computed ONCE on the issuing machine and transmitted as a <c>Fixed</c>
    /// <c>MoveTarget</c> over the wire, so a divergent local category cannot desync. Because it is unhashed, its
    /// integer member values are free to reorder later (unlike the folded <see cref="SeparationPriority"/>).
    /// </summary>
    public enum UnitCategory : byte
    {
        Worker    = 0,
        Melee     = 1,
        Ranged    = 2,
        Siege     = 3,
        Air       = 4,
        Structure = 5,
    }

    /// <summary>
    /// The single closed-set source of truth for the six <see cref="UnitCategory"/> archetypes, derived DIRECTLY from
    /// the enum via <see cref="System.Enum.GetNames(System.Type)"/> — never a re-typed literal. Mirrors the
    /// <c>ResourceCostValidator.KnownResourceIds</c> precedent (an <c>internal static readonly string[]</c> consumed
    /// directly by the validators and the wizard panels), so every consumer reads the SAME set and a future 7th enum
    /// member propagates everywhere with zero hand-edits.
    ///
    /// <para><b>Why safe to centralise.</b> <see cref="UnitCategory"/> is presentation-read and UNHASHED (never folded
    /// into <c>SimChecksum</c>/<c>CanonicalModelHash</c>), so widening this set changes only authoring-time
    /// validation + the wizard dropdowns, never the deterministic wire.</para>
    ///
    /// <para><see cref="All"/> is ordered by the enum's underlying values (Worker=0..Structure=5), matching every
    /// literal it replaces and the existing wizard-dropdown order. <see cref="Combat"/> is defined by EXCLUSION (All
    /// minus the non-combat Worker/Structure), so a future 7th archetype is combat-by-default unless explicitly
    /// excluded here — a single discoverable decision point.</para>
    /// </summary>
    internal static class UnitCategories
    {
        /// <summary>All six archetype names in enum-value order (Worker, Melee, Ranged, Siege, Air, Structure),
        /// derived from the <see cref="UnitCategory"/> enum so it can never drift from it.</summary>
        internal static readonly string[] All = Enum.GetNames(typeof(UnitCategory));

        /// <summary>The combat subset — <see cref="All"/> minus the non-combat Worker/Structure archetypes
        /// (Melee, Ranged, Siege, Air).</summary>
        internal static readonly string[] Combat = All
            .Where(c => c != nameof(UnitCategory.Worker) && c != nameof(UnitCategory.Structure))
            .ToArray();

        /// <summary>The pipe-joined archetype list for validator error messages
        /// (<c>"Worker|Melee|Ranged|Siege|Air|Structure"</c>). Computed once — <see cref="All"/> is immutable, so this
        /// never changes; a <c>static readonly</c> field avoids re-joining on every validation-error construction.</summary>
        internal static readonly string PipeList = string.Join("|", All);

        /// <summary>The Oxford-"or" combat-archetype phrase for the missing-combat error
        /// (<c>"Melee, Ranged, Siege, or Air"</c>). Computed once for the same reason as <see cref="PipeList"/>.</summary>
        internal static readonly string CombatOrPhrase = OrPhrase(Combat);

        /// <summary>Format a list as an Oxford-"or" phrase: 0 items → <c>""</c>; 1 → the item; 2 → <c>"a or b"</c>;
        /// 3+ → <c>"a, b, or c"</c>.</summary>
        private static string OrPhrase(IReadOnlyList<string> items)
        {
            if (items.Count == 0) return "";
            if (items.Count == 1) return items[0];
            if (items.Count == 2) return $"{items[0]} or {items[1]}";
            return string.Join(", ", items.Take(items.Count - 1)) + ", or " + items[items.Count - 1];
        }
    }
}
