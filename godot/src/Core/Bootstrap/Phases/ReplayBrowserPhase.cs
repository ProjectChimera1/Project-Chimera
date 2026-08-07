#nullable enable
using Godot;
using ProjectChimera.Multiplayer;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 9.11 "ReplayBrowser" phase (runtime position beside <see cref="ContentBrowserPhase"/>). Creates the
    /// replay browser panel (hotkey N in Edit mode) and the in-playback control overlay, wires the browser's Play to
    /// the fail-closed <c>TryLoadReplay</c> path, and routes the overlay's pause/speed/seek/perspective actions into
    /// MainScene's replay-control methods. Publishes <c>ctx.ReplayBrowser</c> + <c>ctx.ReplayControls</c>.
    /// </summary>
    public sealed class ReplayBrowserPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ReplayBrowserPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ReplayBrowser";

        public void Run()
        {
            // ── Browser panel ─────────────────────────────────────────────────
            _ctx.ReplayBrowser = new ReplayBrowserPanel();
            _ctx.Scene.AddChild(_ctx.ReplayBrowser);
            _ctx.ReplayBrowser.Initialize("user://replays/");
            _ctx.ReplayBrowser.OnPlay += HandlePlay;

            // ── In-playback controls overlay ──────────────────────────────────
            _ctx.ReplayControls = new ReplayPlaybackControls();
            _ctx.Scene.AddChild(_ctx.ReplayControls);
            _ctx.ReplayControls.Initialize();
            _ctx.ReplayControls.OnTogglePause      += ()  => _ctx.Scene.ReplayTogglePause();
            _ctx.ReplayControls.OnSetSpeed         += s   => _ctx.Scene.ReplaySetSpeed(s);
            _ctx.ReplayControls.OnSeekForward      += t   => _ctx.Scene.ReplaySeekForward(t);
            _ctx.ReplayControls.OnCyclePerspective += ()  => _ctx.Scene.ReplayCyclePerspective();

            GD.Print($"[ReplayBrowser] Initialized — press {Definitions.EditorHotkeys.ChordFor(Definitions.EditorPanelId.ReplayBrowser)} in Edit mode to open. Replays: " +
                     ProjectSettings.GlobalizePath("user://replays/"));
        }

        /// <summary>Play the selected replay by reusing the <c>_Ready</c>-time autoplay path (P1): stash the pending
        /// replay + its scenario as statics that survive <c>ReloadCurrentScene</c>, then reload the scene (mirrors
        /// <c>ContentBrowserPhase.HandleLoadMap</c>). The fresh <c>_Ready</c> loads the replay's scenario into a clean
        /// tick-0 world and calls <c>TryLoadReplay</c>, so the fail-closed re-gate compares against the correctly-
        /// loaded scenario and playback starts from tick 0 (never against a stale in-session world).</summary>
        private void HandlePlay(string path)
        {
            ReplayHeader hdr = ReplayHeader.Read(path);
            if (!hdr.IsPlayable)
            {
                GD.PrintErr($"[ReplayBrowser] '{path}' is unplayable (old/corrupt format) — not loading.");
                return; // Play is already disabled for these rows; guard defensively.
            }

            _ctx.ReplayBrowser.Visible = false;

            MainScene.PendingReplayPath         = path;
            MainScene.PendingReplayScenarioPath = string.IsNullOrEmpty(hdr.ScenarioPath) ? null : hdr.ScenarioPath;
            if (!string.IsNullOrEmpty(hdr.ScenarioPath))
                _ctx.Scene.ScenarioPath = hdr.ScenarioPath; // best-effort; the static above is the reload-durable copy

            _ctx.Scene.GetTree().ReloadCurrentScene();
        }
    }
}
