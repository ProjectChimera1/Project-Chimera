#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Import-time referential + cycle lint for a faction's tech-tree prerequisite graph (Story 4.2). Runs
    /// over the SAME <see cref="FactionDefinition.LoadFromFile"/> aggregate <c>errors</c> channel that
    /// <see cref="BuildingDefinitionValidator"/> already throws with — additive, not a replacement.
    ///
    /// Three independent checks:
    /// 1. Duplicate-id detection — a repeated <c>Buildings[].Id</c> is a located error (list-all: every repeat
    ///    is reported). Without this, a duplicate id would silently collapse to its first occurrence for BOTH
    ///    the referential-lint id set and the cycle-detection graph, so a genuine cycle reachable only through
    ///    the second building's distinct prerequisites would go undetected (review-pass fix).
    /// 2. Referential lint — every <c>Buildings[].Prerequisites</c> / <c>Units[].Prerequisites</c> entry must
    ///    name a building id that actually exists (both entity kinds reference building ids; a unit can never
    ///    itself be a prerequisite target). A prerequisite referencing an unknown id is a located error. List-all,
    ///    same as check 1 — every offending reference is reported, not just the first.
    /// 3. Cycle detection — a 3-color DFS walking ONLY Buildings→Buildings edges (a prerequisite is satisfied
    ///    by an alive, constructed BUILDING — <see cref="TechTreeChecker.AreMet"/> — so units are always graph
    ///    leaves and can never participate in a cycle; restricting the walk to buildings keeps this
    ///    O(buildings) with no loss of correctness). Unlike checks 1-2, this is FIRST-FAIL: it stops after the
    ///    first cycle found and reports the exact chain, including the closing repeated id (e.g. <c>"a -> b -> a"</c>).
    ///    The walk is an explicit-stack iteration, not recursion (DW-59) — an arbitrarily deep authored
    ///    prerequisite chain can never overflow the call stack during faction load.
    ///
    /// A null <c>Prerequisites</c> array (malformed JSON <c>"prerequisites": null</c>) is treated as empty —
    /// never throws an NRE. Pure C#, no logging, no throw — the caller (<see cref="FactionDefinition.LoadFromFile"/>)
    /// decides to throw.
    /// </summary>
    public static class TechTreeValidator
    {
        private enum Color { White, Gray, Black }

        /// <summary>
        /// Validate <paramref name="def"/>'s tech-tree graph. Returns every located duplicate-id and
        /// referential-lint error, followed by at most one cycle error (stops walking after the first cycle is
        /// found). Empty when the tech tree has no duplicate ids, is fully referential, and is acyclic.
        /// </summary>
        public static IReadOnlyList<string> Validate(FactionDefinition def)
        {
            var errors = new List<string>();
            if (def == null) return errors;

            // Building id set — the only valid prerequisite target (Buildings[] AND Units[] both reference
            // building ids). A blank authored id is excluded, never crashes the set build. A DUPLICATE id is a
            // located error (review-pass fix): without this, the second building sharing an id would silently
            // collapse into the first for both the referential-lint set and the cycle-detection graph below,
            // masking a cycle reachable only through the duplicate's own, distinct prerequisites.
            var buildingIds = new HashSet<string>();
            var buildingById = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100: malformed JSON null element — skipped, never an NRE
                if (string.IsNullOrEmpty(b.Id)) continue;
                if (!buildingById.TryAdd(b.Id, b))
                {
                    errors.Add($"building '{b.Id}': duplicate building id (another building already uses this id).");
                    continue;
                }
                buildingIds.Add(b.Id);
            }

            // ── Referential lint: buildings ─────────────────────────────────────
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100
                string id = b.Id ?? "";
                foreach (string prereq in b.Prerequisites ?? Array.Empty<string>())
                {
                    if (!buildingIds.Contains(prereq))
                        errors.Add($"building '{id}'.prerequisites: references unknown building id '{prereq}'.");
                }
            }

            // ── Referential lint: units ──────────────────────────────────────────
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
            {
                if (u is null) continue;   // DW-100
                string id = u.Id ?? "";
                foreach (string prereq in u.Prerequisites ?? Array.Empty<string>())
                {
                    if (!buildingIds.Contains(prereq))
                        errors.Add($"unit '{id}'.prerequisites: references unknown building id '{prereq}'.");
                }
            }

            // ── Cycle detection: Buildings→Buildings edges only, 3-color DFS in list order ──────────────────
            // Story 4.6: extracted into DetectCycle so the visual tech-tree editor's ValidateProposedEdge can reuse
            // the SAME code path (byte-identical wording), rather than reimplementing the walk.
            string? cycleMessage = DetectCycle(def);
            if (cycleMessage != null)
                errors.Add(cycleMessage);

            return errors;
        }

        /// <summary>
        /// Story 4.6: the single-edge validation surface the Visual Tech Tree Editor's <c>connection_request</c>
        /// handler calls BEFORE drawing an edge or mutating any data. The target building's
        /// <see cref="BuildingDefinition.Prerequisites"/> is TEMPORARILY appended with <paramref name="sourceId"/>
        /// (restored in a <c>finally</c>, so this method never leaves <paramref name="def"/> mutated), then
        /// <see cref="DetectCycle"/> runs over the whole graph as if the edge already existed — including a self-edge
        /// (<paramref name="sourceId"/> == <paramref name="targetId"/>), which resolves to the SAME building
        /// temporarily listing itself as its own prerequisite, so the DFS finds that 1-node cycle through the
        /// identical code path as every other case rather than a separately hand-formatted string. Returns null when
        /// the proposed edge is safe (no cycle); the located cycle message otherwise — byte-identical to what an
        /// authored file containing that same edge would fail import-lint with, because both paths share this one
        /// DFS.
        ///
        /// Review-pass fix (Story 4.11): a building's Prerequisites source was always another building already
        /// present in the graph — until 4.11 put research nodes on the SAME GraphEdit port type, making a research
        /// id newly reachable here as <paramref name="sourceId"/>. <see cref="DetectCycle"/> only walks
        /// Buildings→Buildings edges, so it silently ignores a non-building sourceId and would otherwise return
        /// null ("valid") for it. Rejected inline here — wording byte-identical to the import-time referential lint
        /// — mirroring <see cref="ResearchValidator.ValidateProposedEdge"/>'s own unknown-source-id check.
        /// </summary>
        public static string? ValidateProposedEdge(FactionDefinition def, string sourceId, string targetId)
        {
            if (def == null) return null;

            BuildingDefinition? target = null;
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100
                if (b.Id == targetId) { target = b; break; }
            }
            if (target == null) return null;   // unresolved target — the editor never calls this with one (defensive no-op)

            bool sourceIsBuilding = false;
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100
                if (b.Id == sourceId) { sourceIsBuilding = true; break; }
            }
            if (!sourceIsBuilding)
                return $"building '{targetId}'.prerequisites: references unknown building id '{sourceId}'.";

            string[]? original = target.Prerequisites;
            target.Prerequisites = new List<string>(original ?? Array.Empty<string>()) { sourceId }.ToArray();
            try
            {
                return DetectCycle(def);
            }
            finally
            {
                // Prerequisites is a non-nullable-typed property that malformed JSON can still leave null at runtime
                // (see NullPrerequisitesArray_TreatedAsEmpty_NoThrowNoCrash) — null-forgiving restores the exact
                // original reference (including null) rather than a defaulted-empty array.
                target.Prerequisites = original!;
            }
        }

        /// <summary>The cycle-DFS walk extracted out of <see cref="Validate"/> (Story 4.6) so it has a single,
        /// independently-callable home shared by import-time linting and <see cref="ValidateProposedEdge"/>. Rebuilds
        /// its own building-id set/map (mirrors <see cref="Validate"/>'s construction) since it must run standalone —
        /// unchanged logic/wording from the original inline block; first-fail (stops at the first cycle found).</summary>
        private static string? DetectCycle(FactionDefinition def)
        {
            var buildingIds = new HashSet<string>();
            var buildingById = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100
                if (string.IsNullOrEmpty(b.Id)) continue;
                if (!buildingById.TryAdd(b.Id, b)) continue;   // duplicate id — already reported by Validate's own check
                buildingIds.Add(b.Id);
            }

            var color = new Dictionary<string, Color>();
            foreach (string id in buildingIds)
                color[id] = Color.White;

            var path = new List<string>();
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
            {
                if (b is null) continue;   // DW-100
                if (string.IsNullOrEmpty(b.Id)) continue;
                if (color[b.Id] != Color.White) continue;
                string? cycleMessage = Visit(b.Id, buildingById, buildingIds, color, path);
                if (cycleMessage != null) return cycleMessage;
            }
            return null;
        }

        /// <summary>DFS visit of one building node via an explicit heap stack (DW-59: this was a plain
        /// recursion — one C# call-stack frame per graph-depth level — which an unbounded creator-authored
        /// or malicious-scale prerequisite chain thousands of levels deep could overflow during faction
        /// load, and a StackOverflowException kills the whole process uncatchably). Traversal order,
        /// 3-color semantics, first-fail behavior, and the emitted message are unchanged from the
        /// recursive original. Returns a formatted cycle message the instant a gray (in-progress) node is
        /// re-encountered, abandoning the walk without further work; returns null on a clean
        /// (fully-black) subtree.</summary>
        private static string? Visit(string id, Dictionary<string, BuildingDefinition> buildingById,
            HashSet<string> buildingIds, Dictionary<string, Color> color, List<string> path)
        {
            // Three parallel lists = one frame per Gray node on the current DFS chain: the node id, its
            // Prerequisites array (looked up ONCE on push, exactly like the recursive version's one lookup
            // per Visit entry), and the index of its next unexamined prerequisite (the "resume point" the
            // call stack used to remember). `path` mirrors frameIds push-for-push/pop-for-pop because the
            // cycle-extraction slice below reads it — same shared-parameter contract as before.
            var frameIds = new List<string>();
            var framePrereqs = new List<string[]>();
            var frameNext = new List<int>();

            Push(id);

            while (frameIds.Count > 0)
            {
                int top = frameIds.Count - 1;
                string[] prereqs = framePrereqs[top];

                if (frameNext[top] >= prereqs.Length)
                {
                    // Every prerequisite of the top node examined and clean — retreat (the recursive epilogue).
                    path.RemoveAt(path.Count - 1);
                    color[frameIds[top]] = Color.Black;
                    frameIds.RemoveAt(top);
                    framePrereqs.RemoveAt(top);
                    frameNext.RemoveAt(top);
                    continue;
                }

                string prereq = prereqs[frameNext[top]];
                frameNext[top]++;

                if (!buildingIds.Contains(prereq)) continue; // unknown-id already reported by the referential lint above

                Color prereqColor = color[prereq];
                if (prereqColor == Color.Gray)
                {
                    // Cycle: extract the chain from prereq's first appearance in the current DFS path through
                    // the end, then close it by repeating prereq (e.g. "a -> b -> a").
                    int startIdx = path.IndexOf(prereq);
                    var chain = new List<string>();
                    for (int i = startIdx; i < path.Count; i++)
                        chain.Add(path[i]);
                    chain.Add(prereq);
                    return $"tech tree cycle: {string.Join(" -> ", chain)}.";
                }
                if (prereqColor == Color.White)
                    Push(prereq);
                // Black prereq: already-proven-clean subtree — skip, same as the recursive version.
            }

            return null;

            // The recursive prologue: mark Gray, extend the shared path, and open a frame with the node's
            // prerequisite list hoisted (null Prerequisites / unknown id defensively → empty, matching the
            // original's `building?.Prerequisites ?? Array.Empty<string>()`).
            void Push(string nodeId)
            {
                color[nodeId] = Color.Gray;
                path.Add(nodeId);
                BuildingDefinition? building = buildingById.TryGetValue(nodeId, out BuildingDefinition? bd) ? bd : null;
                frameIds.Add(nodeId);
                framePrereqs.Add(building?.Prerequisites ?? Array.Empty<string>());
                frameNext.Add(0);
            }
        }
    }
}
