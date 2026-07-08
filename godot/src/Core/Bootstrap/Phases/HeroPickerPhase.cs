#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;   // LocalProfileSource, PlayerProfile, HeroProfileLoader, StartStateHash
using ProjectChimera.UI;                  // HeroPickerOverlay, GameMode

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 3.9 "HeroPicker" phase (runtime position 26, last). Creates the offline-skirmish hero-picker overlay and
    /// wires the OFFLINE Play-Skirmish launch authority (D-4): <c>MainMenuPhase.OnPlaySkirmish</c> delegates to the
    /// overlay's <see cref="UI.HeroPickerOverlay.RequestSkirmishLaunch"/>, which shows the picker only when the loaded
    /// scenario's <see cref="PersistenceManifest.Enabled"/> is true, else toggles straight to Play (single launch
    /// authority — no double-toggle). Clones <see cref="PersistenceManifestPhase"/>'s shape. The offline disk rail
    /// (<see cref="LocalProfileSource"/>) is pointed at the globalized <see cref="SettingsData.HeroProfileFolder"/>
    /// (<c>user://hero_profiles</c> by default, AR-5) — the Godot edge resolves the path; the rail stays Godot-free.
    /// </summary>
    public sealed class HeroPickerPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public HeroPickerPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "HeroPicker";

        public void Run()
        {
            // Resolve the offline profile directory on the Godot edge; hand the OS-absolute path to the Godot-free rail.
            string folder = _ctx.SettingsMgr?.Current?.HeroProfileFolder ?? "user://hero_profiles";
            string absFolder = ProjectSettings.GlobalizePath(folder);
            var source = new LocalProfileSource(absFolder);

            _ctx.HeroPicker = new HeroPickerOverlay();
            _ctx.Scene.AddChild(_ctx.HeroPicker);
            _ctx.HeroPicker.Initialize(_ctx.Scenario, source, _ctx.SlotFactionDefs, Launch);

            GD.Print("[HeroPicker] Initialized — offline Play-Skirmish hero picker (shown when the scenario's persistence manifest is enabled).");
        }

        /// <summary>
        /// The single offline-skirmish launch: mint the Deployed profile (if any) into <see cref="HeroStore"/> as
        /// deterministic init state, recompute + log the start-state hash so it reflects the mint, then enter Play. A
        /// <c>null</c> profile (Play without a saved hero / persistence disabled) mints nothing — launch always proceeds.
        /// </summary>
        private void Launch(PlayerProfile? profile)
        {
            _ctx.PendingHeroProfile = profile;

            int minted = HeroProfileLoader.LoadInto(_ctx.Host.Heroes, _ctx.Applier.LastAppliedHeroes, profile, _ctx.Log);

            ScenarioData? model = _ctx.Scenario ?? _ctx.FallbackMirror;
            if (_ctx.ScenarioApplied && model != null)
            {
                ulong h = StartStateHash.Compute(model, _ctx.Host.Heroes);
                GD.Print($"[HeroPicker] Launch (minted {minted} hero row(s)) — start-state hash " +
                         $"(algo v{StartStateHash.AlgoVersion}): 0x{h:X16}");
            }

            // Story 3.10: keep PendingHeroProfile SET past launch — the Edit↔Play reset (ResetToAuthoredStart) re-mints
            // the deployed profile on EVERY round-trip (it clears HeroStore first, so the re-mint is non-additive), and
            // a plain F5→Play does not pass back through here. The Edit→Play toggle below fires ModeChanged →
            // ResetToAuthoredStart, which clears + re-applies + re-mints from this profile (idempotent with the mint above).

            if (_ctx.GameState.Mode != GameMode.Play)
                _ctx.GameState.Toggle();
        }
    }
}
