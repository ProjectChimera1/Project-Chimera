#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 14.7 (DW-164) — the single HARD pre-write gate for the editor's map Export and New-Map write paths.
    ///
    /// DW-164's defect was that BOTH write paths (<c>WinConditionPhase.ExportMapPackage</c> and
    /// <c>WinConditionPhase.CreateNewMap</c>) independently skipped a hard <see cref="ScenarioValidator.Validate"/>
    /// and ran only the non-fatal <c>CollectAdvisories</c> — and only AFTER persisting/packaging — so a scenario
    /// that hard-fails <c>CheckCoord</c> on reload (content stranded past <c>MapBounds</c>, a slot overflow, etc.)
    /// was still written to <c>scenario.json</c> and shipped inside a <c>.chimera.zip</c> whose manifest hash
    /// validated but whose payload was unloadable.
    ///
    /// This is the one shared, Godot-free gate decision the two Export/New-Map paths route through, so those two
    /// can never diverge again (fix Export, forget New-Map). Story 7.7 additionally routes the
    /// <c>MapGeneratorPanel</c> AI-generated save through it; the Import re-save (<c>WinConditionPhase.DoImport</c>)
    /// and <c>PersistenceManifestPanel</c> still write without it (ledgered, out of scope). Callers MUST invoke
    /// <see cref="Check"/> BEFORE any disk write (terrain save, scenario save, package pack) and abort — surfacing
    /// the returned located error — on a non-null return, so a rejected write leaves NOTHING partial on disk.
    ///
    /// This is a HARD gate, distinct from and additive to the non-fatal <c>CollectAdvisories</c> layer: it MUST
    /// NOT be weakened into an advisory or warning-only surface. Since Story 7.7 the master LOAD gate is
    /// fail-closed everywhere too (shadow mode is gone), so this pre-write gate and the load gate now agree: an
    /// exported package that would hard-fail on reload is never written. Note the guarantee is the MODEL-level
    /// validation subset
    /// (coordinates / slots / player-slot economy / painted-cell blocking): slope-derived blocked cells depend on the
    /// terrain heightmap and are recomputed at load, so they are outside this Godot-free gate's view and are not part
    /// of the loadability it certifies.
    ///
    /// Pure: it only consumes <see cref="ScenarioValidator.Validate"/>; it never throws, writes, or logs (the
    /// presentation call site owns logging + status surfacing).
    /// </summary>
    public static class MapWriteGate
    {
        /// <summary>
        /// Run the hard validation gate over <paramref name="scenario"/>. Returns <c>null</c> when the scenario is
        /// safe to write; otherwise the located <see cref="ScenarioValidator.Validate"/> error (field path +
        /// offending value) the caller must surface before aborting the write.
        /// </summary>
        /// <param name="scenario">The scenario about to be persisted/packaged.</param>
        /// <param name="slotFactionDefs">The resolved per-slot faction defs (as the load-path
        /// <c>ScenarioLoadPhase.ValidateBeforeApply</c> passes) so a pre-placed custom building's authored id
        /// resolves identically to reload and the gate verdict matches what reload would produce. Null is valid
        /// (a blank New-Map scenario has no pre-placed custom buildings); <see cref="ScenarioValidator.Validate"/>
        /// null-guards it.</param>
        /// <returns>Null when safe to write; else the located validation error.</returns>
        public static string? Check(ScenarioData scenario, IReadOnlyList<FactionDefinition?>? slotFactionDefs = null)
        {
            ValidationResult r = new ScenarioValidator().Validate(scenario, slotFactionDefs);
            return r.Ok ? null : r.Error;
        }
    }
}
