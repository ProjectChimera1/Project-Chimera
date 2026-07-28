#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>
    /// Story 11.1 (in-engine gate fix) — translates a map's pre-placed unit ids into the roster of the faction a slot
    /// actually chose.
    ///
    /// <para><b>Why this exists.</b> Every shipped scenario authors its starting army against ONE faction's ids —
    /// <c>alpha_map_01</c> places <c>unit_id: "worker"</c> for both slots. The factions have DISJOINT rosters
    /// (<c>alpha</c>: <c>worker/infantry/…/mage</c>; <c>beta</c>: <c>forgehand/footsoldier/…/rune_caster</c>). Before
    /// this story faction always came from the scenario file, so the ids always matched; the setup screen introduced
    /// faction CHOICE, and an unmapped id silently resolves to no <c>UnitDefinition</c> — the applier's
    /// <c>def == null</c> skip then drops the unit with no error, so the player who picked the "wrong" faction starts
    /// with no workers, no income, and no way to recover. Verified in-engine 2026-07-28: alpha-vs-beta launched with
    /// <c>P2: 0 units</c> where the map authors 2.
    ///
    /// <para><b>The role coordinate.</b> A unit's role is (<c>Category</c>, ordinal-within-category) resolved against
    /// the AUTHORED faction's roster in authored order — e.g. alpha's <c>mage</c> is (Ranged, 1), which is beta's
    /// <c>rune_caster</c>. This is the "8-unit role skeleton" the FMA faction redesign preserved, read as data rather
    /// than hardcoded, so a community faction that follows the skeleton maps for free.
    ///
    /// <para><b>Degradation.</b> <see cref="FactionValidator.ValidateComplete"/> — the selectability gate — only
    /// guarantees a Worker plus at least one combat unit, NOT a full skeleton. So a legitimately selectable faction may
    /// have fewer units in a category (clamped to the last one of that category) or none at all (unmappable →
    /// <see cref="SkirmishSetupValidator"/> blocks Launch before the transform is ever reached).
    ///
    /// Pure/Godot-free and deterministic: ascending list iteration only, no dictionary ordering in any decision path.
    /// </summary>
    public static class SkirmishRosterMap
    {
        /// <summary>
        /// Translate <paramref name="unitId"/> — authored against <paramref name="authored"/> — into the equivalent
        /// unit id in <paramref name="target"/>'s roster. Returns <c>null</c> when no equivalent exists, which is the
        /// caller's signal to block (validator) or drop (transform); it never invents an id.
        /// </summary>
        /// <param name="unitId">The pre-placed unit id as the map authored it.</param>
        /// <param name="authored">The faction the map's ids were written against; null when it cannot be resolved.</param>
        /// <param name="target">The faction the slot actually chose.</param>
        public static string? MapUnitId(string unitId, FactionEntry? authored, FactionEntry? target)
        {
            if (string.IsNullOrEmpty(unitId) || target == null) return null;

            // Same faction (by res:// identity, the FactionJson key) → identity. This keeps a same-faction launch
            // byte-identical to the base map, so the common path is provably unchanged by this remap.
            if (authored != null && !string.IsNullOrEmpty(authored.ResPath)
                && string.Equals(authored.ResPath, target.ResPath, System.StringComparison.Ordinal))
                return unitId;

            IReadOnlyList<FactionUnitEntry> targetUnits = target.Units ?? System.Array.Empty<FactionUnitEntry>();

            // Locate the authored unit to learn its role. If the authored roster is unknown or no longer carries the
            // id, fall back to an exact id match in the target (covers factions that deliberately share ids).
            int srcIndex = IndexOf(authored, unitId);
            if (srcIndex < 0)
                return ContainsId(targetUnits, unitId) ? unitId : null;

            IReadOnlyList<FactionUnitEntry> srcUnits = authored!.Units ?? System.Array.Empty<FactionUnitEntry>();
            string category = srcUnits[srcIndex].Category ?? "";
            if (string.IsNullOrEmpty(category))
                return ContainsId(targetUnits, unitId) ? unitId : null;

            // Ordinal within the category, counting only entries BEFORE it in authored order.
            int ordinal = 0;
            for (int i = 0; i < srcIndex; i++)
                if (SameCategory(srcUnits[i].Category, category)) ordinal++;

            // The target's units of that same category, in authored order.
            var candidates = new List<FactionUnitEntry>();
            for (int i = 0; i < targetUnits.Count; i++)
                if (SameCategory(targetUnits[i].Category, category)) candidates.Add(targetUnits[i]);

            if (candidates.Count == 0) return null; // no equivalent role at all → unmappable

            // Clamp: a target with fewer units in this category still fields its closest analogue rather than losing
            // the unit outright (a shallower roster should not silently cost a player their starting army).
            int pick = ordinal < candidates.Count ? ordinal : candidates.Count - 1;
            string mapped = candidates[pick].Id ?? "";
            return string.IsNullOrEmpty(mapped) ? null : mapped;
        }

        /// <summary>The category token of <paramref name="unitId"/> in <paramref name="authored"/>, or "" if unknown.
        /// Used by the validator to name the missing role in its actionable message.</summary>
        public static string CategoryOf(string unitId, FactionEntry? authored)
        {
            int idx = IndexOf(authored, unitId);
            if (idx < 0) return "";
            return (authored!.Units ?? System.Array.Empty<FactionUnitEntry>())[idx].Category ?? "";
        }

        /// <summary>Resolve a faction by its <c>res://</c> path (the <c>FactionJson</c> key), or null.</summary>
        public static FactionEntry? ByResPath(IReadOnlyList<FactionEntry>? factions, string? resPath)
        {
            if (factions == null || string.IsNullOrEmpty(resPath)) return null;
            for (int i = 0; i < factions.Count; i++)
                if (factions[i] != null
                    && string.Equals(factions[i].ResPath, resPath, System.StringComparison.Ordinal))
                    return factions[i];
            return null;
        }

        /// <summary>Resolve a faction by its id, or null.</summary>
        public static FactionEntry? ById(IReadOnlyList<FactionEntry>? factions, string? id)
        {
            if (factions == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < factions.Count; i++)
                if (factions[i] != null && string.Equals(factions[i].Id, id, System.StringComparison.Ordinal))
                    return factions[i];
            return null;
        }

        private static int IndexOf(FactionEntry? faction, string unitId)
        {
            IReadOnlyList<FactionUnitEntry> units = faction?.Units ?? System.Array.Empty<FactionUnitEntry>();
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && string.Equals(units[i].Id, unitId, System.StringComparison.Ordinal)) return i;
            return -1;
        }

        private static bool ContainsId(IReadOnlyList<FactionUnitEntry> units, string unitId)
        {
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && string.Equals(units[i].Id, unitId, System.StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Case-insensitive category comparison, mirroring <c>FactionDefinition.GetUnitsByCategory</c>.</summary>
        private static bool SameCategory(string? a, string? b) =>
            string.Equals(a ?? "", b ?? "", System.StringComparison.OrdinalIgnoreCase);
    }
}
