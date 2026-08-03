#nullable enable
using Godot;
using ProjectChimera.Core.Sim; // DslVarReadback (faction→slot conversion)
using ProjectChimera.UI; // CustomUiBridge

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 7.8 "CustomHudOverlay" phase — runs immediately AFTER <see cref="HudPhase"/> in the composition-root
    /// phase order (pinned in <c>ScenePhaseOrder.Canonical</c> + <c>PhaseOrderTest</c>). Constructs the
    /// <see cref="CustomUiBridge"/> (its own overlay <c>CanvasLayer</c>) and wires it to the sim's presentation read
    /// rail: the version-stamped <c>DslVarReadback</c> owned by <see cref="SimulationHost"/>. The widget tree is
    /// late-bound via a getter (<c>_ctx.Scenario?.CustomUi</c>) so it survives the F5 Edit→Play re-apply. Pumped each
    /// frame by <c>MainScene._Process</c> via <c>_ctx.CustomHud.Update()</c>.
    ///
    /// The read rail is presentation-only (NOT folded into <c>SimChecksum</c>), so this overlay can never affect the
    /// deterministic tick — a UI mismatch cannot desync (AR-32).
    /// </summary>
    public sealed class CustomHudOverlayPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public CustomHudOverlayPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "CustomHudOverlay";

        public void Run()
        {
            var bridge = new CustomUiBridge { Name = "CustomUiBridge" };
            _ctx.Scene.AddChild(bridge);
            // The local per-player slot for PerPlayer binds is resolved LIVE each frame from the CLAMPED engine
            // faction (LockstepManager.EffectiveLocalFaction — Player1 offline/spectating, the real slot at GoOnline;
            // DW-407: raw LocalFaction would read the stale prior-match slot in an offline-after-online session).
            // The engine Faction enum is 1-based (Player1=1) but the DSL per-player store is
            // 0-based with slot 0 = Player1, so we MUST convert via DslVarReadback.PlayerSlotForFaction — passing the
            // raw engine int would read the NEXT player's slot (off-by-one; e.g. local Player1 → Player2's value).
            // Theme left to the bridge's default (null-safe — numeric labels fall back to the default font). The tree
            // is pulled live each frame.
            // Story 7.9 — also hand the write rail: the lockstep bus a Button press enqueues onto, and the live
            // scenario getter used to resolve an authored event name to its registry index. Both MUST be late-bound
            // getters: this phase runs at position 10 while SceneContext.Lockstep is only created by the Multiplayer
            // phase's MatchLifecycleController many phases later — a by-value handle here is permanently null (and
            // the scenario getter additionally survives the F5 Edit→Play re-apply).
            bridge.Initialize(_ctx.Host.Readback, () => _ctx.Scenario?.CustomUi,
                localFactionGetter: () => DslVarReadback.PlayerSlotForFaction((int)_ctx.Lockstep.EffectiveLocalFaction),
                theme: null,
                lockstepGetter: () => _ctx.Lockstep,
                scenarioGetter: () => _ctx.Scenario);
            _ctx.CustomHud = bridge;
        }
    }
}
