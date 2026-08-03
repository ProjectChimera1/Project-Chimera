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
            // Story 9.12: the picker depends on the IProfileSource seam. Offline uses the LocalProfileSource disk rail
            // (unchanged); the online path (LobbyUi) swaps in an OnlineProfileSource (server-RPC-only writes).
            IProfileSource source = new LocalProfileSource(absFolder);

            _ctx.HeroPicker = new HeroPickerOverlay();
            _ctx.Scene.AddChild(_ctx.HeroPicker);
            _ctx.HeroPicker.Initialize(_ctx.Scenario, source, _ctx.SlotFactionDefs, Launch);
            // Story 3.13 (D6) / Story 3.16 / DW-27 / DW-32: let Save/Overwrite read the harvested end-of-match Level/Xp +
            // loadout for a hero unit id (captured into SceneContext.Harvest on return-to-Edit before HeroStore is cleared),
            // so the picker persists real grown values. The harvest is captured long AFTER picker init, so hand the picker a
            // live-read closure — not a snapshot — over the single value-typed carrier (default None ⇒ falls back as before).
            _ctx.HeroPicker.HarvestProvider = () => _ctx.Harvest;

            // Story 9.12: give the online lobby (built earlier by MatchLifecycleController) live access to the scenario +
            // per-slot faction defs so its online hero picker — backed by the server-owned OnlineProfileSource, gated on
            // attestation — can compatibility-filter by the local player's server-assigned faction. Wired here (not in
            // MatchLifecycleController, which stays untouched per the spec) because this phase already resolves both, and
            // it runs after the lobby exists. Read lazily at picker-build time (well after setup fully populates them).
            if (_ctx.LobbyUi != null)
            {
                _ctx.LobbyUi.ScenarioProvider        = () => _ctx.Scenario;
                _ctx.LobbyUi.SlotFactionDefsProvider = () => _ctx.SlotFactionDefs;
            }

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

            int minted = HeroProfileLoader.LoadInto(_ctx.Host.Heroes, _ctx.Applier.LastAppliedHeroes, profile, _ctx.Log,
                _ctx.Host.World,                        // Story 3.13: establish the entity→hero link (D-8) for the XP runtime
                _ctx.Host.Items, _ctx.Host.ItemRegistry, // Story 3.16: re-mint the persisted inventory before the hash below
                _ctx.Host.Modifiers, _ctx.Host.ItemSys.UsableSlots, // Story 3.16 review: apply carried stat modifiers + honor the slot cap
                ownerSlot: _ctx.Lockstep?.HeroMintOwnerSlot); // DW-13: mint into the local player's placed hero only; DW-407: clamped (offline → Player1, never a stale prior-match faction; spectator → Neutral = no mint)

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
