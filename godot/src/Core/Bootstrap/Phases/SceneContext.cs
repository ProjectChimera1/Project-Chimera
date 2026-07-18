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
        public FactionDefinition?[] SlotFactionDefs = null!;
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

        // ── Multiplayer + match lifecycle (Multiplayer / ReplayStatus / MatchLifecycle) ────────────────────
        public MatchLifecycleController MatchLifecycle = null!; // Multiplayer phase / Task 5 (replay autoload + return-to-Edit reset drive it)
        public ENetTransport    Transport    = null!;
        public LockstepManager  Lockstep     = null!;
        public LobbyUi          LobbyUi      = null!;
        public UI.MatchChatOverlay    ChatOverlay    = null!;
        public ReplayRecorder?        ReplayRecorder;   // MatchLifecycle (active recorder during an online match)
        public ReplayPlayer?          ReplayPlayer;     // MatchLifecycle (active replay player; _Process reads this)
        public Label?                 ReplayStatusLabel;

        // ── Top UI layers (ContentBrowser / MainMenu / Settings / TriggerEditor / MapGenerator) ────────────
        public UI.ContentBrowserPanel ContentBrowser = null!;
        public UI.SettingsManager     SettingsMgr    = null!;
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
        // ── Story 3.13 (D6): end-of-match harvest of the deployed hero's live Level/Xp, captured before HeroStore is
        //    cleared on return-to-Edit, so the hero picker's Save/Overwrite persists the REAL grown values (routed
        //    through the manifest shape) instead of the authored placeholders. Keyed by the deployed hero's unit id. ─
        public bool                HasHarvestedHeroProgress;
        public string?             HarvestedHeroDefId;
        public int                 HarvestedHeroLevel;
        public Fixed               HarvestedHeroXp;
        // ── Story 3.16: the deployed hero's carried inventory (item-def id + charges), harvested at the same seam so the
        //    picker Save/Overwrite persists the REAL loadout when the manifest carries hero.inventory. Null ⇒ empty. ─
        public System.Collections.Generic.List<Definitions.ProfileInventoryItem>? HarvestedHeroInventory;
        public AI.LLMService          LlmService     = null!;
        public Label?                 ToastLabel;

        // ── Win condition / game over (produced by WinConditionUi / GameOverOverlay) ───────────────────────
        public PanelContainer WinConditionPanel = null!;
        public Control        GameOverOverlay   = null!;
        /// <summary>Story 5.9 review pass: re-sync the WinConditionUi corner panel's radio selection from the live
        /// <see cref="Definitions.ScenarioData.WinCondition"/> — set by <c>WinConditionPhase</c>, called by
        /// <c>OnboardingPanel</c> (via <c>MainScene.RefreshWinConditionUi</c>) after IT mutates the same field, so
        /// the two win-condition UI surfaces never silently disagree on what's currently selected.</summary>
        public Action?         WinConditionUiRefresh;
    }
}
