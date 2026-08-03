#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;   // ObjectiveResolver, ResolvedObjective, FactionDefinition
using ProjectChimera.Core.Sim;             // DslVarReadback (faction→slot conversion)
using ProjectChimera.UI;                   // ObjectiveLogOverlay, MatchBriefingOverlay
using GameMode = ProjectChimera.UI.GameMode;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 7.14 "ObjectiveOverlay" phase — runs immediately AFTER <see cref="CustomHudOverlayPhase"/> (it shares the
    /// same presentation read-rail pattern; pinned in <c>ScenePhaseOrder.Canonical</c> + <c>PhaseOrderTest</c>).
    /// Constructs the two Story 7.14 overlays (each its own <c>CanvasLayer</c>):
    /// <list type="bullet">
    /// <item>the in-match quest-log (<see cref="ObjectiveLogOverlay"/>) wired to the sim's version-stamped
    /// <see cref="DslVarReadback"/> read rail, pumped each frame by <c>MainScene._Process</c> and toggled by a key; and</item>
    /// <item>the skippable pre-match briefing (<see cref="MatchBriefingOverlay"/>), shown at the Play-start
    /// <c>GameState.ModeChanged</c> edge with the resolved objectives + local faction blurb.</item>
    /// </list>
    /// Both are presentation-only (never folded into <c>SimChecksum</c>) and use late-bound <c>() =&gt; _ctx.Scenario</c>
    /// getters so they survive the F5 Edit→Play re-apply. The briefing never gates the deterministic tick.
    /// </summary>
    public sealed class ObjectiveLogOverlayPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ObjectiveLogOverlayPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ObjectiveOverlay";

        public void Run()
        {
            // ── In-match quest log (read rail + late-bound getters, exactly like CustomHudOverlayPhase) ──
            var log = new ObjectiveLogOverlay { Name = "ObjectiveLogOverlay" };
            _ctx.Scene.AddChild(log);
            log.Initialize(
                readbackGetter: () => _ctx.Host?.Readback,
                scenarioGetter: () => _ctx.Scenario,
                // The engine Faction enum is 1-based; the DSL store is 0-based — convert via PlayerSlotForFaction.
                // Late-bound: Lockstep is created many phases later, so a by-value slot here would be permanently 0.
                // DW-407: read the CLAMPED EffectiveLocalFaction (offline/spectator → Player1) — raw LocalFaction
                // would personalise for the stale prior-match faction in an offline-after-online session.
                localFactionGetter: () => _ctx.Lockstep != null
                    ? DslVarReadback.PlayerSlotForFaction((int)_ctx.Lockstep.EffectiveLocalFaction)
                    : 0);
            _ctx.ObjectiveLog = log;

            // ── Skippable pre-match briefing, shown at Play-start ──
            var briefing = new MatchBriefingOverlay { Name = "MatchBriefingOverlay" };
            _ctx.Scene.AddChild(briefing);
            _ctx.Briefing = briefing;

            // Play-start is the GameState.ModeChanged → Play edge (GameState exists — it is an earlier phase). The
            // briefing is PRESENTATION-ONLY and does NOT gate the tick (a lockstep peer cannot pause its sim on a
            // local dismissal): it is simply shown; Play proceeds regardless of dismissal.
            _ctx.GameState.ModeChanged += mode =>
            {
                if (mode != (int)GameMode.Play) return;
                ResolvedObjective[] objectives = ObjectiveResolver.Resolve(_ctx.Scenario);
                briefing.ShowForScenario(_ctx.Scenario, objectives, LocalFactionBlurb());
            };
        }

        /// <summary>Best-effort local-faction blurb for the briefing (the default P1 faction). Missing ⇒ null ⇒ the
        /// briefing omits the faction section (no crash).</summary>
        private string? LocalFactionBlurb()
        {
            FactionDefinition? def = _ctx.FactionDef;
            if (def == null || string.IsNullOrWhiteSpace(def.DisplayName)) return null;
            return string.IsNullOrWhiteSpace(def.SignatureMechanicDisplay)
                ? def.DisplayName
                : $"{def.DisplayName} — {def.SignatureMechanicDisplay}";
        }
    }
}
