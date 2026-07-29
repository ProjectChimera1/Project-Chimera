#nullable enable
using Godot;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "GameOverOverlay" phase (runtime position 16). Creates the full-screen game-over dimming overlay
    /// (hidden until MainScene.ShowGameOver populates it with live match data). Publishes ctx.GameOverOverlay.
    /// Behavior-identical to MainScene.SetupGameOverOverlay.
    ///
    /// <para>Story 11.2 (FR-66): ALSO constructs the session-shell overlays — the in-match menu
    /// (<see cref="InMatchMenuOverlay"/>) and the kit victory/defeat score screen (<see cref="ScoreScreenOverlay"/>) —
    /// here (rather than a new phase) so the canonical phase order stays untouched. Both are hidden until MainScene
    /// opens them at runtime; the legacy dimming <see cref="ColorRect"/> is retained as a fallback container.</para>
    /// </summary>
    public sealed class GameOverOverlayPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public GameOverOverlayPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "GameOverOverlay";

        public void Run()
        {
            // Root dimming rect — reused as the overlay root
            var root = new ColorRect { Color = new Color(0f, 0f, 0f, 0.65f), Visible = false };
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _ctx.UiCanvas.AddChild(root);
            _ctx.GameOverOverlay = root;

            // Story 11.2 — the in-match menu (Esc/F10) + the kit score screen. Own CanvasLayers (added to the scene,
            // not UiCanvas), self-owning their kit context exactly like SettingsPanel. Hidden until MainScene shows them.
            var menu = new InMatchMenuOverlay();
            _ctx.Scene.AddChild(menu);
            menu.Initialize();
            _ctx.InMatchMenu = menu;

            // Story 11.3 — the SP save/load disk rail: resolve user://saves/ to an OS-absolute path on the Godot edge and
            // hand it to the Godot-free LocalSaveStore (the HeroPickerPhase → LocalProfileSource pattern). Injected into
            // the menu so its slot picker can show per-slot metadata; MainScene reads it for IssueSave/IssueLoad/autosave.
            string absSaves = ProjectSettings.GlobalizePath("user://saves");
            _ctx.SaveStore = new Persistence.LocalSaveStore(absSaves);
            menu.SetSaveStore(_ctx.SaveStore);

            var score = new ScoreScreenOverlay();
            _ctx.Scene.AddChild(score);
            score.Initialize();
            _ctx.ScoreScreen = score;
        }
    }
}
