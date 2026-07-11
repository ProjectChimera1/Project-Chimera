#nullable enable
using Godot;
using ProjectChimera.CreationSuite;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 5.5 (FR-17, UX-DR40) "FactionDefiner" phase — appended after <see cref="HeroPickerPhase"/>, second to
    /// last in the canonical order (Story 5.9's <c>OnboardingPhase</c> now runs after it, since onboarding drives
    /// panels every earlier phase — including this one — has already constructed). Creates the Faction Definer
    /// guided-wizard panel and wires it to game state
    /// only. Unlike <see cref="BuildingCardPhase"/>/<see cref="TechTreePhase"/> this phase does NOT bind an existing
    /// <see cref="Definitions.FactionDefinition"/> — the wizard always assembles a BRAND-NEW faction from scratch
    /// (Story 5.5's own AC1), scanning the on-disk faction JSONs for its Roster / Buildings &amp; Tech preset pools
    /// rather than editing the currently-loaded scenario faction.
    ///
    /// <para>Toggle with X in Edit mode (verified unused — see <c>MainScene._UnhandledInput</c>).</para>
    /// </summary>
    public sealed class FactionDefinerPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public FactionDefinerPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "FactionDefiner";

        public void Run()
        {
            _ctx.FactionDefinerPanel = new FactionDefinerPanel();
            _ctx.Scene.AddChild(_ctx.FactionDefinerPanel);

            _ctx.FactionDefinerPanel.Initialize(_ctx.GameState);

            GD.Print("[FactionDefiner] Initialized — press X in Edit mode to open (5-step guided faction wizard).");
        }
    }
}
