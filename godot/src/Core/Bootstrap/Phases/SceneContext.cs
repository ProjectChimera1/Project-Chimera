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
        public ScenarioData?       FallbackMirror = null; // ScenarioLoad (hardcoded-fallback mirror, for the canonical hash)
        public bool                ScenarioApplied = false; // ScenarioLoad (true once a model reached the sim — gates the _Ready hash)

        // ── HUD (produced by Hud; consumed by Minimap/WinConditionUi/GameOverOverlay/ReplayStatus/Trigger toast) ─
        public CanvasLayer    UiCanvas      = null!;
        public Label          HudLabel      = null!;
        public Label          ResourceLabel = null!;
        public Label          ControlsLabel = null!;
        public PanelContainer StallBanner   = null!;
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
        public AI.LLMService          LlmService     = null!;
        public Label?                 ToastLabel;

        // ── Win condition / game over (produced by WinConditionUi / GameOverOverlay) ───────────────────────
        public PanelContainer WinConditionPanel = null!;
        public Control        GameOverOverlay   = null!;
    }
}
