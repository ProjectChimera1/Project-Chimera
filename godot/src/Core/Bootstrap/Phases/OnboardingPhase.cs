#nullable enable
using Godot;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 5.9 (NFR-2) "Onboarding" phase — the LAST phase in the canonical order, appended after
    /// <see cref="FactionDefinerPhase"/>. Creates the "Your First Scenario" guided-onboarding overlay and wires
    /// the Settings panel's "Replay onboarding" action. Runs last deliberately: <see cref="OnboardingPanel"/>
    /// drives <c>UnitCardPanel</c> (and, indirectly through it, every other panel/tool a first-time creator
    /// touches) via <see cref="MainScene"/>'s thin wrapper methods, so every panel it might open must already
    /// exist — this holds uniformly for all ~30 preceding phases, not just one. That set happens to include
    /// <see cref="SettingsPhase"/> (runtime position 1), whose <see cref="SettingsManager.Load"/> has therefore
    /// long since populated <see cref="Definitions.SettingsData.HasSeenOnboarding"/> by the time this phase's
    /// auto-offer check runs.
    /// </summary>
    public sealed class OnboardingPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public OnboardingPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Onboarding";

        public void Run()
        {
            _ctx.OnboardingPanel = new OnboardingPanel();
            _ctx.Scene.AddChild(_ctx.OnboardingPanel);
            _ctx.OnboardingPanel.Initialize(_ctx.GameState, _ctx.SettingsMgr, _ctx.Scene, _ctx.Scenario);

            // Settings panel "Replay onboarding" action (Story 5.9): reset the persisted flag and re-open onboarding
            // from step 1. Wired here (not by SettingsPhase, which runs first and has no OnboardingPanel handle yet)
            // — an event on SettingsPanel keeps the two panels decoupled despite the 30-phase gap between them.
            _ctx.SettingsPanel.OnReplayOnboardingRequested += () =>
            {
                _ctx.SettingsMgr.Current.HasSeenOnboarding = false;
                _ctx.SettingsMgr.Save();
                _ctx.SettingsPanel.Close();               // close Settings so the reopened onboarding is actually visible
                _ctx.OnboardingPanel.Open(forceRestart: true);
            };

            // Auto-offer on first boot (AC1): a fresh user://settings.json, or an explicit HasSeenOnboarding=false,
            // shows the walkthrough without any creator action. Never re-offered once seen/skipped (AC5) — only the
            // replay action above can reopen it after that.
            if (!_ctx.SettingsMgr.Current.HasSeenOnboarding)
                _ctx.OnboardingPanel.Open();

            GD.Print("[Onboarding] Initialized" + (_ctx.SettingsMgr.Current.HasSeenOnboarding ? "." : " — auto-offering \"Your First Scenario\"."));
        }
    }
}
