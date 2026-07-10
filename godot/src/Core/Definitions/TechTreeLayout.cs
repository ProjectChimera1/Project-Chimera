#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 4.6 — the pure, Godot-free tier-layout algorithm the Visual Tech Tree Editor (and its tests) both depend
    /// on. Tier(b) = longest-path depth over <see cref="BuildingDefinition.Prerequisites"/>: 0 when a building has no
    /// (resolvable) prerequisites, else <c>1 + max(tier(p))</c> over every prerequisite id that resolves to a loaded
    /// building. An unresolvable prerequisite id (references a building not in the list) is skipped defensively —
    /// never throws — mirroring <see cref="TechTreeValidator"/>'s referential-lint posture of leaving unknown-id
    /// detection to the linter, not the layout.
    ///
    /// <para>No tier value is ever authored or persisted anywhere in <see cref="BuildingDefinition"/>'s schema — this
    /// is a deterministic pure function recomputed fresh every time the editor rebuilds its graph, so a reload always
    /// redraws identically from the current <c>Prerequisites</c> data alone (Design Notes).</para>
    ///
    /// <para><b>Cycle safety.</b> A well-formed faction (one that has passed <see cref="TechTreeValidator.Validate"/>'s
    /// import-time lint) is acyclic, so ordinary recursion terminates. As a defensive belt for a graph reached some
    /// other way (e.g. a hand-edited file opened without going through the loader), a per-call "currently visiting"
    /// guard prevents infinite recursion — a node re-entered while still being computed short-circuits to tier 0
    /// rather than stack-overflowing.</para>
    /// </summary>
    public static class TechTreeLayout
    {
        /// <summary>Compute the tier of every building with a non-empty <see cref="BuildingDefinition.Id"/> in
        /// <paramref name="buildings"/>. Deterministic: the same building list always yields the same tiers,
        /// regardless of dictionary/enumeration order (each tier is a pure max-over-prerequisites computation).</summary>
        public static Dictionary<string, int> ComputeTiers(IReadOnlyList<BuildingDefinition> buildings)
        {
            var tiers = new Dictionary<string, int>();
            if (buildings == null) return tiers;

            var byId = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in buildings)
            {
                if (string.IsNullOrEmpty(b.Id)) continue;
                byId[b.Id] = b;   // last occurrence wins — defensive only; a duplicate id is TechTreeValidator's job
            }

            var visiting = new HashSet<string>();

            foreach (BuildingDefinition b in buildings)
            {
                if (string.IsNullOrEmpty(b.Id)) continue;
                if (!tiers.ContainsKey(b.Id)) TierOf(b.Id, byId, tiers, visiting);
            }

            return tiers;
        }

        private static int TierOf(string id, Dictionary<string, BuildingDefinition> byId,
            Dictionary<string, int> tiers, HashSet<string> visiting)
        {
            if (tiers.TryGetValue(id, out int cached)) return cached;
            if (!byId.TryGetValue(id, out BuildingDefinition? b)) return 0;   // unresolvable — caller already skips these edges
            if (!visiting.Add(id)) return 0;   // defensive cycle guard (see class doc) — never hit for a validated graph

            int maxPrereqTier = -1;
            foreach (string prereq in b.Prerequisites ?? Array.Empty<string>())
            {
                if (!byId.ContainsKey(prereq)) continue;   // unresolvable prerequisite id — skipped, never throws
                int t = TierOf(prereq, byId, tiers, visiting);
                if (t > maxPrereqTier) maxPrereqTier = t;
            }

            visiting.Remove(id);
            int tier = maxPrereqTier < 0 ? 0 : maxPrereqTier + 1;
            tiers[id] = tier;
            return tier;
        }
    }
}
