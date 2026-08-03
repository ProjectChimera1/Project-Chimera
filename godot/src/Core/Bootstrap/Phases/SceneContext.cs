#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.CreationSuite;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer;
using ProjectChimera.Navigation;
using ProjectChimera.UI;
using System;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c (AR-3, design-decision 4): the presentation-side composition-root context. A mutable holder
    /// populated as the <see cref="ISetupPhase"/> sequence runs — each concrete <c>*Phase</c> reads the typed
    /// handles it needs and writes its own products back here, so cross-phase dependencies are explicit
    /// constructor inputs instead of hidden <c>_Ready</c> ordering (minimal ceremony, no DI framework — C8).
    ///
    /// This type holds Godot Node handles, so it lives under <c>Bootstrap/Phases/</c> and is EXCLUDED from the
    /// Godot-free Tier-1 assembly (the kernel — ISetupPhase etc. — stays Godot-free and globbed-in).
    ///
    /// <para><see cref="Scene"/> is the owning <see cref="MainScene"/> node: phases call <c>Scene.AddChild(...)</c>
    /// (they create Nodes, they are not Nodes), read Inspector config off it, and wire the handful of bridge
    /// callbacks it retains (build placement, win/lose, scenario reload) that touch state <c>MainScene</c> keeps.</para>
    /// </summary>
    public sealed class SceneContext
    {
        public SceneContext(MainScene scene) => Scene = scene;

        /// <summary>The owning MainScene node — phases attach children to it and read its Inspector config / bridge callbacks.</summary>
        public MainScene Scene { get; }

        // ── Sim spine (constructed in _Ready before the phase list; aliases of SimulationHost) ─────────────
        public SimulationHost     Host         = null!;
        public ScenarioApplier    Applier      = null!;
        public ILogSink           Log          = null!;
        public EntityWorld        World        = null!;
        public ResourceNodeStore  Nodes        = null!;
        public ResourceStore      Resources    = null!;
        public BuildingStore      Buildings    = null!;
        public FogOfWarSystem     Fog          = null!;
        public ProjectileStore    Projectiles  = null!;
        public CombatEventQueue   CombatEvents = null!;
        public DamageTable        DamageTable  = null!;
        public MatchStats         MatchStats   = null!;
        public BuildingSystem     BuildSys     = null!;
        public ScenarioDirector   ScenarioDirector = null!;
        public FactionDefinition  FactionDef   = null!;  // default P1 (alpha)
        public FactionDefinition  FactionDef2  = null!;  // default P2 (beta)
        /// <summary>DW-17 / DW-225: the seed the live world's RNG was last (re)seeded to at match start — the single
        /// source of truth read by BOTH the offline AuthoredStart reset (which mints + applies a fresh per-match seed)
        /// and the <c>ReplayRecorder</c> (which records this instead of a hardcoded literal). Defaults to
        /// <see cref="EntityWorld.DEFAULT_RNG_SEED"/>; the online path pins it back to DEFAULT (all peers agree) until
        /// the Epic-9 seed handshake supplies a networked seed.</summary>
        public ulong LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED;
        public FactionDefinition?[] SlotFactionDefs = null!;
        /// <summary>DW-229: a <c>_Ready</c>-time clone of the seeded per-slot faction defs
        /// (<c>[Player1=_factionDef, Player2=_factionDef2, rest null]</c>). The shared
        /// <see cref="SlotFactionResolver.Resolve"/> resets <see cref="SlotFactionDefs"/> to THIS baseline before
        /// re-resolving per-slot faction JSON, so a cleared/repointed faction_json reverts to its default and
        /// re-resolution is idempotent across Edit↔Play re-applies. Distinct backing array from
        /// <see cref="SlotFactionDefs"/> — never aliased to it.</summary>
        public FactionDefinition?[] SeededSlotFactionDefs = null!;
        /// <summary>Story 2.4b: the validated ability registry (built by MainScene._Ready from the abilities dir).
        /// The command card reads it to turn a per-entity <c>AbilityId</c> index into an <c>AbilityDefinition</c> for
        /// labels, and <c>ScenarioLoadPhase</c> reads it to resolve per-slot defs — the <see cref="SimulationHost"/>
        /// does NOT expose the registry (it lives privately inside <c>AbilityCastSystem</c>). Defaults to Empty.</summary>
        public AbilityRegistry AbilityRegistry = AbilityRegistry.Empty;

        /// <summary>Story 3.6: the loaded behavior registry (built by MainScene._Ready from the behaviors dir). The Unit
        /// Card Editor reads it to offer the behavior picker and to reject undefined / archetype-incompatible behavior
        /// refs. Authoring-only — no sim system consumes it (D-2). Defaults to Empty.</summary>
        public BehaviorRegistry BehaviorRegistry = BehaviorRegistry.Empty;

        // ── Presentation handles (each produced by the phase named below; consumed by later phases / runtime) ─
        public RtsCameraController Cam        = null!;   // Camera
        public EntityPlacer        Placer     = null!;   // Camera
        public SelectionSystem     Selection  = null!;   // Camera
        public CommandCardSystem   CommandCard = null!;  // Camera
        public GameState           GameState  = null!;   // GameState
        public PathRequestSystem   PathSystem = null!;   // Navigation
        public FlowFieldSystem     FlowFieldSys    = null!;  // Navigation
        public FlowFieldBridge     FlowFieldBridge = null!;  // Navigation
        public NavigationRegion3D  NavRegion       = null!;  // Navigation
        public NavObstacleManager  NavObstacles    = null!;  // Navigation
        public Node3D?             Terrain    = null;    // Terrain (null on PlaneMesh fallback)
        /// <summary>Story 11.7 (FR-66): the scene key <see cref="DirectionalLight3D"/> produced by
        /// <c>LightingPhase</c>. The video quality tier's <c>ShadowEnabled</c> toggle is the one display knob that
        /// needs a scene handle, so it rides the <c>MainScene.ApplySettingsToSystems</c> bridge against this — null in
        /// menus (no match running), so every bridge push is null-guarded.</summary>
        public DirectionalLight3D? KeyLight = null;      // Lighting
        public FogOfWarBridge      FogBridge  = null!;   // Rendering
        public StartPositionBridge StartPosBridge = null!;  // ScenarioLoad
        public ScenarioData?       Scenario   = null;    // ScenarioLoad (live edited scenario; null on fallback)
        /// <summary>Story 6.5: the load-time pathability grid (painted ∪ slope-derived blocked cells) built by
        /// ScenarioLoad; null when nothing is blocked. Read by the PathabilityTool overlay (a later phase) for its
        /// initial render. The sim consumes its own injected copy (via the applier/EntityWorld), never this handle.</summary>
        public ProjectChimera.Navigation.PathabilityGrid? Pathability = null;  // ScenarioLoad
        public ScenarioData?       FallbackMirror = null; // ScenarioLoad (hardcoded-fallback mirror, for the canonical hash)
        public bool                ScenarioApplied = false; // ScenarioLoad (true once a model reached the sim — gates the _Ready hash)

        // ── HUD (produced by Hud; consumed by Minimap/WinConditionUi/GameOverOverlay/ReplayStatus/Trigger toast) ─
        public CanvasLayer    UiCanvas      = null!;
        public Label          HudLabel      = null!;
        public Label          ResourceLabel = null!;
        public Label          ControlsLabel = null!;
        public PanelContainer StallBanner   = null!;
        public UI.CustomUiBridge CustomHud  = null!;     // CustomHudOverlay (Story 7.8 — the custom-UI read rail)
        public UI.ObjectiveLogOverlay ObjectiveLog = null!; // ObjectiveOverlay (Story 7.14 — in-match quest log, read rail)
        public UI.TriggerDebugOverlay TriggerDebugOverlay = null!; // TriggerDebugOverlay (Story 7.15 — variable watch + fired-trigger log + fire counters + enabled state)
        public UI.MatchBriefingOverlay Briefing = null!;    // ObjectiveOverlay (Story 7.14 — skippable pre-match briefing)
        public UI.MinimapBridge Minimap     = null!;     // Minimap
        // Story 11.4 (FR-74) — the match-feedback layer (MatchAlert phase).
        public UI.MatchAlertBridge  MatchAlert   = null!; // MatchAlert — the read-only CombatEventQueue drainer (alerts/denials/cues)
        public UI.OrderMarkerBridge OrderMarkers = null!; // MatchAlert — pooled issue-time order-confirmed ground markers
        public UI.Components.ChimeraToastHost ToastHost = null!; // MatchAlert — the shared 3.1x toast stack (DW-313 cap/coalesce)
        // Story 11.5 (FR-74) — the bottom-bar multi-select subgroup grid + buff/debuff icon row (MatchAlert phase).
        public UI.SelectionSubgroupPanel SelectionPanel = null!; // MatchAlert — drained by MainScene._Process

        // ── Multiplayer + match lifecycle (Multiplayer / ReplayStatus / MatchLifecycle) ────────────────────
        public MatchLifecycleController MatchLifecycle = null!; // Multiplayer phase / Task 5 (replay autoload + return-to-Edit reset drive it)
        public ENetTransport    Transport    = null!;
        public LockstepManager  Lockstep     = null!;
        public LobbyUi          LobbyUi      = null!;
        public UI.MatchChatOverlay    ChatOverlay    = null!;
        public ReplayRecorder?        ReplayRecorder;   // MatchLifecycle (active recorder during an online match)
        public ReplayPlayer?          ReplayPlayer;     // MatchLifecycle (active replay player; _Process reads this)
        public Label?                 ReplayStatusLabel;
        // Story 9.11 — replay UX (produced by ReplayBrowserPhase; consumed by MainScene runtime).
        public UI.ReplayBrowserPanel     ReplayBrowser  = null!; // main-menu/Edit-mode replay browser (hotkey N)
        public UI.ReplayPlaybackControls ReplayControls = null!; // in-playback overlay (pause/speed/seek/perspective)

        // ── Top UI layers (ContentBrowser / MainMenu / Settings / TriggerEditor / MapGenerator) ────────────
        public UI.ContentBrowserPanel ContentBrowser = null!;
        /// <summary>Story 9.9: the shared render/session custom-asset registry. Populated by the content browser on
        /// download (integrity-verified ingest of a package's bundled GLBs) and consulted by the unit render bridges
        /// (<see cref="UI.MultiMeshBridge"/>) so a downloaded custom unit renders its bundled mesh; a null registry is
        /// never threaded — this always-present instance keeps the seam live while empty for non-custom content.</summary>
        public UI.AssetRegistry       AssetRegistry  = new();
        public UI.SettingsManager     SettingsMgr    = null!;
        /// <summary>Story 8.1: the Godot-free secret store (file-backed over <c>user://secrets</c>). Constructed once
        /// in <c>SettingsPhase</c>; the TriggerEditor / ContentBrowser phases source their LLM / mod.io keys from it
        /// via <see cref="ISecretStore.Get"/> instead of the removed plaintext <c>[Export]</c> fields on MainScene.</summary>
        public ISecretStore           SecretStore    = null!;
        /// <summary>Story 8.2: the Godot-free availability evaluator (over a shared short-timeout <c>HttpClient</c>).
        /// Constructed in <c>SettingsPhase</c> alongside the secret store; the Settings provider-config section drives
        /// Test-connection through it, and the TriggerEditor / MapGenerator panels drive their on-open availability
        /// status line from <see cref="AI.Providers.AiAvailabilityEvaluator.EvaluateConfig"/>.</summary>
        public AI.Providers.AiAvailabilityEvaluator AiEvaluator = null!;
        public UI.SettingsPanel       SettingsPanel  = null!;
        public UI.MainMenuOverlay     MainMenu       = null!;
        public UI.AudioManager        AudioMgr       = null!;
        public CreationSuite.TriggerEditorPanel TriggerPanel = null!;
        public CreationSuite.MapGeneratorPanel  MapGenPanel  = null!;
        public CreationSuite.AbilityEditorPanel AbilityEditorPanel = null!;  // Story 2.5a (AbilityEditor phase)
        public CreationSuite.UnitCardPanel      UnitCardPanel      = null!;  // Story 3.3 (UnitCard phase)
        public CreationSuite.BuildingCardPanel  BuildingCardPanel  = null!;  // Story 4.5 (BuildingCard phase)
        public CreationSuite.TechTreePanel      TechTreePanel      = null!;  // Story 4.6 (TechTree phase)
        public CreationSuite.DslGraphEditorPanel DslGraphEditorPanel = null!;  // Story 7.10 (DslGraphEditor phase)
        public CreationSuite.ItemCardPanel      ItemCardPanel      = null!;  // Story 3.16 (ItemCard phase)
        public CreationSuite.PersistenceManifestPanel PersistenceManifestPanel = null!;  // Story 3.8 (PersistenceManifest phase)
        public CreationSuite.FactionDefinerPanel FactionDefinerPanel = null!;  // Story 5.5 (FactionDefiner phase)
        public UI.OnboardingPanel OnboardingPanel = null!;  // Story 5.9 (Onboarding phase — last)
        public UI.HeroPickerOverlay HeroPicker = null!;  // Story 3.9 (HeroPicker phase) — offline Play-Skirmish hero picker + launch authority
        public UI.SkirmishSetupOverlay SkirmishSetup = null!;  // Story 11.1 (MainMenu phase) — the skirmish setup screen reached from "Play"
        /// <summary>Story 3.9: the profile the player Deployed at the offline skirmish start, handed off to
        /// <c>HeroProfileLoader.LoadInto</c> to mint into <c>HeroStore</c> as init state; cleared after mint. Null ⇒
        /// nothing minted ("play without a saved hero" / persistence disabled).</summary>
        public Definitions.PlayerProfile? PendingHeroProfile;
        /// <summary>Story 3.10 (UX-DR62): when false (default), the Edit↔Play reset DISCARDS playtest hero progress and
        /// re-mints the deployed profile's authored initial Level/Xp on return-to-Edit; when true, it snapshots the
        /// live <c>HeroStore</c> Level/Xp per stable <c>HeroId</c> before clearing and re-mints the snapshot (progress
        /// kept). Pre-3.13 there is no runtime XP growth so both branches coincide — this is the tested seam Story 3.13
        /// consumes. Sourced by <c>WinConditionPhase</c> for the <c>ResetToAuthoredStart(preserveHeroProgress)</c> call.</summary>
        public bool PersistenceTestMode;
        // ── Story 3.13 (D6) / Story 3.16 / DW-27 / DW-32: end-of-match harvest of the deployed hero's live Level/Xp +
        //    carried inventory, captured (by the Godot-free HeroHarvestResolver) before HeroStore is cleared on
        //    return-to-Edit, so the hero picker's Save/Overwrite persists the REAL grown values + loadout (routed through
        //    the manifest shape) instead of the authored placeholders. A single value-typed carrier — the has-vs-heroDefId
        //    match now lives inside the resolver (ResolveProgress/ResolveInventory), read live by the picker via
        //    HeroPickerPhase's HarvestProvider. Defaults to None (Has=false), so an un-harvested picker session falls
        //    back exactly as before. ─
        public Definitions.HeroHarvestResolver.HeroHarvest Harvest = Definitions.HeroHarvestResolver.HeroHarvest.None;
        public AI.LLMService          LlmService     = null!;
        public Label?                 ToastLabel;

        // ── Win condition / game over (produced by WinConditionUi / GameOverOverlay) ───────────────────────
        public PanelContainer WinConditionPanel = null!;
        public Control        GameOverOverlay   = null!;
        /// <summary>Story 11.2 (FR-66): the in-match menu (Esc/F10) — Resume / Settings / Save / Load / Concede / Quit +
        /// game-speed + pause. Constructed by <c>GameOverOverlayPhase</c>; opened/wired by MainScene runtime.</summary>
        public UI.InMatchMenuOverlay InMatchMenu = null!;
        /// <summary>Story 11.3 (FR-67): the SP save/load disk rail (Godot-free core over the globalized <c>user://saves/</c>
        /// directory). Constructed by <c>GameOverOverlayPhase</c>; used by MainScene's IssueSave/IssueLoad/autosave.</summary>
        public Persistence.ISaveStore SaveStore = null!;
        /// <summary>Story 11.2 (FR-66): the kit victory/defeat score screen (replaces the raw-node ShowGameOver body).
        /// Constructed by <c>GameOverOverlayPhase</c>; populated by <c>MainScene.ShowGameOver</c>.</summary>
        public UI.ScoreScreenOverlay ScoreScreen = null!;
        /// <summary>Story 5.9 review pass: re-sync the WinConditionUi corner panel's radio selection from the live
        /// <see cref="Definitions.ScenarioData.WinCondition"/> — set by <c>WinConditionPhase</c>, called by
        /// <c>OnboardingPanel</c> (via <c>MainScene.RefreshWinConditionUi</c>) after IT mutates the same field, so
        /// the two win-condition UI surfaces never silently disagree on what's currently selected.</summary>
        public Action?         WinConditionUiRefresh;
    }
}
