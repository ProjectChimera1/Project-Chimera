#nullable enable
using ProjectChimera.Core.Sim;   // DslVarReadback (faction→slot conversion)
using ProjectChimera.UI;          // TriggerDebugOverlay

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 7.15 "TriggerDebugOverlay" phase — runs immediately AFTER <see cref="ObjectiveLogOverlayPhase"/> (it
    /// shares the same presentation read-rail + late-bound-getter pattern; pinned in
    /// <c>ScenePhaseOrder.Canonical</c> + <c>PhaseOrderTest</c>). Constructs the trigger-debugging overlay (its own
    /// <see cref="Godot.CanvasLayer"/>) wired to four PRESENTATION-only read sources: the version-stamped
    /// <see cref="DslVarReadback"/> variable watch, the non-folded <c>TriggerFireLog</c> (fired-trigger log + fire
    /// counters), and the folded <c>TriggerEnabledStore</c>'s READ API (enabled state). The overlay is pumped each
    /// frame by <c>MainScene._Process</c> and toggled by F2 (Play-scoped).
    ///
    /// <para>Presentation-only (NEVER folded into <c>SimChecksum</c>); the late-bound <c>() =&gt; _ctx.Host?.*</c> and
    /// <c>() =&gt; _ctx.Scenario</c> getters make it survive the F5 Edit→Play re-apply. The navigate action (click a
    /// fired-log entry) switches to Edit, opens the flat <see cref="CreationSuite.TriggerEditorPanel"/>, and focuses
    /// the corresponding authored trigger row — supplied by <c>MainScene</c> so the overlay never reaches into
    /// GameState / the editor directly.</para>
    /// </summary>
    public sealed class TriggerDebugOverlayPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public TriggerDebugOverlayPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "TriggerDebugOverlay";

        public void Run()
        {
            var overlay = new TriggerDebugOverlay { Name = "TriggerDebugOverlay" };
            _ctx.Scene.AddChild(overlay);
            overlay.Initialize(
                readbackGetter: () => _ctx.Host?.Readback,
                fireLogGetter: () => _ctx.Host?.TriggerFireLog,
                enabledGetter: () => _ctx.Host?.TriggerEnabled,
                scenarioGetter: () => _ctx.Scenario,
                // The engine Faction enum is 1-based; the DSL per-player store is 0-based — convert via
                // PlayerSlotForFaction. Late-bound: Lockstep is created many phases later. DW-407: read the CLAMPED
                // EffectiveLocalFaction (offline/spectator → Player1), never the stale-prone raw LocalFaction.
                localFactionGetter: () => _ctx.Lockstep != null
                    ? DslVarReadback.PlayerSlotForFaction((int)_ctx.Lockstep.EffectiveLocalFaction)
                    : 0,
                // Click-to-navigate crosses the Play→Edit boundary (an inherent mode switch, not a defect):
                // MainScene switches to Edit, opens the flat trigger editor, and focuses the authored trigger row.
                navigate: _ctx.Scene.NavigateToTrigger);
            _ctx.TriggerDebugOverlay = overlay;
        }
    }
}
