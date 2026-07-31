#nullable enable
using Godot;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;
using ProjectChimera.UI.Theme;
using GodotTheme = Godot.Theme; // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 11.4 (FR-74) "MatchAlert" phase — the match-feedback presentation layer. Constructs the shared 3.1x toast
    /// host, the pooled issue-time order-confirmed ground markers, and the <see cref="MatchAlertBridge"/> read-only
    /// CombatEventQueue drainer; wires their dependencies from the context; and publishes them on
    /// <see cref="SceneContext"/>. Runs after Minimap (needs <c>_ctx.Minimap</c>/<c>_ctx.Cam</c>/<c>_ctx.AudioMgr</c>)
    /// and after Camera (needs <c>_ctx.Selection</c>/<c>_ctx.CommandCard</c>); it does NOT need the per-match
    /// LockstepManager (the ping-send closure reads <c>_ctx.Lockstep</c> late, and the ping-receive subscription is
    /// wired per match by <c>MatchLifecycleController</c>). MainScene drains the bridge in its presentation tail BEFORE
    /// CombatFeedbackBridge's single Clear() — mirroring the AudioManager read-only-sibling posture.
    /// </summary>
    public sealed class MatchAlertPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public MatchAlertPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "MatchAlert";

        public void Run()
        {
            ChimeraComponents.EnsureInitialized(_ctx.Scene); // the 3.1x kit must be up before any ChimeraComponents.* call (toast build)

            // Shared toast host (top-left transient stack; DW-313 cap/coalesce lives inside it).
            var toasts = ChimeraToastHost.Create();
            _ctx.Scene.AddChild(toasts);
            _ctx.ToastHost = toasts;

            // Pooled issue-time order-confirmed ground markers.
            var markers = new OrderMarkerBridge();
            _ctx.Scene.AddChild(markers);
            _ctx.OrderMarkers = markers;

            // The feedback coordinator — read-only drainer of the non-folded CombatEventQueue.
            var bridge = new MatchAlertBridge();
            _ctx.Scene.AddChild(bridge);
            bridge.Initialize(
                _ctx.CombatEvents, _ctx.Minimap, _ctx.Cam, _ctx.AudioMgr, toasts,
                // Late-bound local faction (Lockstep is built later; offline/spectator clamps to Player1).
                () => _ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1,
                _ctx.Host.Alliances); // P1: ally-only ping gate (WC3 semantics)
            _ctx.MatchAlert = bridge;

            // Story 11.5 (FR-74): the bottom-bar multi-select subgroup panel + buff/debuff icon row. Pure presentation
            // over the sim (Selection.Subgroups / World status+health / Host.Modifiers) — no new phase, no sim write.
            var selectionPanel = new SelectionSubgroupPanel();
            _ctx.Scene.AddChild(selectionPanel);
            selectionPanel.Initialize(_ctx.Selection, _ctx.World, _ctx.Host.Modifiers, _ctx.UiCanvas);
            _ctx.SelectionPanel = selectionPanel;

            // Issue-time acknowledgment: SelectionSystem plays the ack + spawns a marker on the input frame.
            _ctx.Selection.SetFeedbackDeps(_ctx.AudioMgr, markers);
            // Offline Train/Buy rejection cue (online routes through LockstepManager's own event queue).
            _ctx.CommandCard.SetCombatEvents(_ctx.CombatEvents);

            // Minimap Alt-click ping: show locally (the minimap already added the ring), play the cue, and — in MP —
            // replicate to allies over the reliable side-channel. The closure reads _ctx.Lockstep late (per match).
            _ctx.Minimap.OnLocalPing = world =>
            {
                _ctx.AudioMgr?.PlayPing();
                _ctx.Lockstep?.SendMapPing(Mathf.RoundToInt(world.X), Mathf.RoundToInt(world.Z));
            };
        }
    }
}
