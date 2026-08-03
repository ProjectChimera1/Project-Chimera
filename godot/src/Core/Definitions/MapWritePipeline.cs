#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-329 (Story 14.7 follow-up) — the ORDERED, Godot-free write sequence behind the editor's two map write
    /// paths, extracted from <c>WinConditionPhase.ExportMapPackage</c> / <c>WinConditionPhase.CreateNewMap</c> so
    /// the DW-164 ordering property is Tier-1 testable instead of code-read-only.
    ///
    /// The property this class owns (and <c>MapWritePipelineTests</c> pins): the HARD <see cref="MapWriteGate"/>
    /// runs FIRST, and on a rejection NO write delegate fires — no terrain region files (the first disk write in
    /// the export sequence), no <c>scenario.json</c> overwrite, no <c>.chimera.zip</c>. A regression that deletes
    /// the abort or reorders the gate below the first write now turns a Tier-1 test red, where before it was
    /// invisible (the phase is a Godot <c>Node</c>-bound class the Godot-free test assembly cannot instantiate).
    ///
    /// The delegates own their platform side (Godot paths, Terrain3D, ContentPackager, status labels, their own
    /// try/catch surfacing) — this class owns only the ORDER and the abort. It never throws on its own, never
    /// writes, never logs; delegate exceptions propagate unchanged so the phase's existing error handling is
    /// byte-identical.
    /// </summary>
    public static class MapWritePipeline
    {
        /// <summary>
        /// The Export write sequence: gate → terrain-save → scenario-save → pack.
        /// </summary>
        /// <param name="scenario">The scenario about to be persisted/packaged.</param>
        /// <param name="slotFactionDefs">The resolved per-slot faction defs (Export forwards
        /// <c>SceneContext.SlotFactionDefs</c>) so a pre-placed custom building's authored id resolves exactly as
        /// reload would — see <see cref="MapWriteGate.Check"/>.</param>
        /// <param name="saveTerrain">Step 2 — persist the live terrain beside the scenario (the FIRST disk write;
        /// <c>WinConditionPhase.SaveTerrainBesideScenario</c>). Returns the terrain folder's absolute path, or null
        /// when there is no terrain / the terrain save failed (the phase clears <c>TerrainRef</c> in that case and
        /// the export continues terrain-less, as built).</param>
        /// <param name="saveScenario">Step 3 — persist <c>scenario.json</c>. Returns false to ABORT the sequence
        /// (the delegate has already surfaced its own failure); pack never runs then.</param>
        /// <param name="packAsync">Step 4 — render preview / gather proof-of-play + screenshots / pack the
        /// <c>.chimera.zip</c>, given the terrain folder from step 2. Owns its own failure surfacing.</param>
        /// <returns>The located <see cref="MapWriteGate"/> rejection the caller must surface (nothing was written),
        /// or null when the gate passed (the sequence ran; any step-level failure was surfaced by its delegate).</returns>
        public static async Task<string?> RunExportAsync(
            ScenarioData scenario,
            IReadOnlyList<FactionDefinition?>? slotFactionDefs,
            Func<string?> saveTerrain,
            Func<bool> saveScenario,
            Func<string?, Task> packAsync)
        {
            // HARD gate FIRST — before ANY write delegate. A rejected export leaves nothing partial on disk.
            string? gateError = MapWriteGate.Check(scenario, slotFactionDefs);
            if (gateError != null) return gateError;

            // Terrain BEFORE scenario-save: SaveTerrainBesideScenario stamps ScenarioData.TerrainRef so the
            // serialized JSON carries the ref and the package can bundle the region files (Story 6.2 order).
            string? terrainDir = saveTerrain();

            if (!saveScenario()) return null; // aborted mid-sequence; the delegate surfaced its own error

            await packAsync(terrainDir);
            return null;
        }

        /// <summary>
        /// The New-Map write sequence: gate → scenario-save (no terrain, no package — a blank map has neither).
        /// </summary>
        /// <param name="scenario">The freshly-built blank scenario about to be persisted.</param>
        /// <param name="saveScenario">The single write step (persist the blank to the scenarios folder). Fires only
        /// when the gate passes; its exceptions propagate to the caller's existing handler unchanged.</param>
        /// <param name="slotFactionDefs">Per-slot faction defs; null is correct for a blank New-Map scenario (no
        /// pre-placed custom buildings), mirroring <see cref="MapWriteGate.Check"/>'s null-tolerant contract.</param>
        /// <returns>The located gate rejection to surface (nothing was written), or null when the save ran.</returns>
        public static string? RunNewMap(
            ScenarioData scenario,
            Action saveScenario,
            IReadOnlyList<FactionDefinition?>? slotFactionDefs = null)
        {
            // HARD gate FIRST — before the write. Same gate decision the Export path consults, so the two
            // paths can never diverge again (fix Export, forget New-Map — the DW-164 defect shape).
            string? gateError = MapWriteGate.Check(scenario, slotFactionDefs);
            if (gateError != null) return gateError;

            saveScenario();
            return null;
        }
    }
}
