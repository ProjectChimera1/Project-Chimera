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
    /// import-time lint) is acyclic, so the walk terminates on its own. As a defensive belt for a graph reached some
    /// other way (e.g. a hand-edited file opened without going through the loader), a per-call "currently visiting"
    /// guard prevents an infinite walk — a node re-entered while still being computed short-circuits to tier 0
    /// rather than looping forever.</para>
    ///
    /// <para><b>Depth safety.</b> The walk is an explicit-stack iteration, not recursion (DW-574, mirroring DW-59 /
    /// DW-573's identical fix to <see cref="TechTreeValidator"/> / <see cref="ResearchValidator"/>). The cycle guard
    /// above bounds the walk against loops but never bounded its DEPTH, so an arbitrarily deep authored prerequisite
    /// chain — one that now survives validation precisely because those two validators were hardened — would have
    /// overflowed the call stack here instead, dying in the editor's tech-tree panel rather than at load.</para>
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

        /// <summary>The memoized longest-path tier walk, driven by an explicit heap stack rather than recursion
        /// (DW-574: this was a plain recursion — one C# call-stack frame per prerequisite-chain depth level — so the
        /// same deep authored chain DW-59/DW-573 hardened the two VALIDATORS against would instead overflow the call
        /// stack here, killing the process uncatchably while the editor laid out its tech-tree panel: load would
        /// survive validation and then die in the UI). Every observable behaviour is unchanged from the recursive
        /// original — the <paramref name="tiers"/> memo, the unresolvable-id short-circuit, the
        /// <paramref name="visiting"/> re-entry cycle guard's "short-circuit to 0 WITHOUT memoizing", prerequisite
        /// examination in array order, and <c>max(prereq tiers) + 1</c> (0 when nothing resolvable contributed).</summary>
        private static int TierOf(string id, Dictionary<string, BuildingDefinition> byId,
            Dictionary<string, int> tiers, HashSet<string> visiting)
        {
            // Four parallel lists = one frame per node currently being computed: the node id, its Prerequisites
            // array (hoisted ONCE on push, exactly like the recursive version's single lookup per call), the index
            // of its next unexamined prerequisite (the "resume point" the call stack used to remember), and the
            // running max over the prereq tiers resolved so far (-1 = "nothing contributed yet", the recursive
            // version's `maxPrereqTier` seed).
            var frameIds = new List<string>();
            var framePrereqs = new List<string[]>();
            var frameNext = new List<int>();
            var frameMax = new List<int>();

            // The initial call's own prologue. A memo hit / unresolvable id / already-visiting node returns
            // immediately without ever opening a frame — identical to the recursive version's three guard lines.
            if (!TryDescend(id, out int immediate)) return immediate;

            while (true)
            {
                int top = frameIds.Count - 1;
                string[] prereqs = framePrereqs[top];

                if (frameNext[top] >= prereqs.Length)
                {
                    // Every prerequisite examined — the recursive epilogue: leave the visiting set, memoize, return.
                    string nodeId = frameIds[top];
                    visiting.Remove(nodeId);
                    int tier = frameMax[top] < 0 ? 0 : frameMax[top] + 1;
                    tiers[nodeId] = tier;
                    frameIds.RemoveAt(top);
                    framePrereqs.RemoveAt(top);
                    frameNext.RemoveAt(top);
                    frameMax.RemoveAt(top);

                    if (frameIds.Count == 0) return tier;   // the initial call unwound — this is its result

                    // Fold the completed child's tier into the parent's running max (the recursive version's
                    // `int t = TierOf(...); if (t > maxPrereqTier) maxPrereqTier = t;`).
                    int parent = frameIds.Count - 1;
                    if (tier > frameMax[parent]) frameMax[parent] = tier;
                    continue;
                }

                string prereq = prereqs[frameNext[top]];
                frameNext[top]++;

                if (!byId.ContainsKey(prereq)) continue;   // unresolvable prerequisite id — skipped, never throws

                // A prerequisite that resolves without opening a frame (memoized, or short-circuited by the cycle
                // guard) contributes its value immediately; one that opens a frame folds in on its retreat above.
                if (!TryDescend(prereq, out int resolved) && resolved > frameMax[top])
                    frameMax[top] = resolved;
            }

            // The recursive prologue. Returns false (with the value the recursive call would have returned) when the
            // node needs no walk; true after opening its frame. Guard ORDER is load-bearing and preserved: memo,
            // then unresolvable id, then the visiting re-entry guard — a node short-circuited by that guard is
            // deliberately NOT memoized, exactly as before, so its real tier is still computed when its own
            // frame completes.
            bool TryDescend(string nodeId, out int immediateTier)
            {
                if (tiers.TryGetValue(nodeId, out immediateTier)) return false;
                if (!byId.TryGetValue(nodeId, out BuildingDefinition? b)) { immediateTier = 0; return false; }
                if (!visiting.Add(nodeId)) { immediateTier = 0; return false; }

                frameIds.Add(nodeId);
                framePrereqs.Add(b.Prerequisites ?? Array.Empty<string>());
                frameNext.Add(0);
                frameMax.Add(-1);
                immediateTier = 0;
                return true;
            }
        }
    }
}
