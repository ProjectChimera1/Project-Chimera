#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions;
using ProjectChimera.CreationSuite;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer;
using ProjectChimera.Navigation;
using ProjectChimera.UGC;
using ProjectChimera.UI;
using System;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Phase 1 Main Scene — Edit-Play loop with resource economy.
    ///
    /// Edit mode:  Left-click = spawn unit/node (Tab cycles mode, Shift = worker).
    ///             Camera: WASD pan, scroll zoom, middle-mouse rotate.
    ///             Simulation is paused.
    ///
    /// Play mode:  Simulation runs. Workers gather ore and return to faction bases.
    ///
    /// F5 toggles modes.
    /// </summary>
    public partial class MainScene : Node3D
    {
        // ── Simulation ────────────────────────────────────────────────────────

        private SimulationLoop    _simLoop     = null!;
        private EntityWorld       _world       = null!;
        private ResourceNodeStore _nodes       = null!;
        private ResourceStore     _resources   = null!;
        private BuildingStore     _buildings   = null!;
        private FogOfWarSystem    _fog         = null!;
        private FactionDefinition _factionDef  = null!;  // default P1 (alpha)
        private FactionDefinition _factionDef2 = null!;  // default P2 (beta)
        // Active per-slot definitions — set by ApplyScenario from slot.FactionJson
        private FactionDefinition[] _slotFactionDefs = null!;
        private Combat.ProjectileStore  _projectiles = null!;
        private Combat.CombatEventQueue _combatEvents = null!;
        private Combat.DamageTable      _damageTable = null!;

        // ── Presentation ──────────────────────────────────────────────────────

        private RtsCameraController _camCtrl          = null!;
        private EntityPlacer        _placer           = null!;
        private SelectionSystem     _selection        = null!;
        private GameState           _gameState        = null!;
        private PathRequestSystem   _pathSystem       = null!;
        private FlowFieldSystem     _flowFieldSys     = null!;
        private FlowFieldBridge     _flowFieldBridge  = null!;
        private BuildingSystem      _buildSys         = null!;
        private CommandCardSystem   _commandCard = null!;
        private NavigationRegion3D  _navRegion    = null!;
        private NavObstacleManager  _navObstacles = null!;
        private StartPositionBridge _startPosBridge = null!;
        private FogOfWarBridge      _fogBridge      = null!;

        /// <summary>
        /// The Terrain3D node created by SetupTerrain(). Null when falling back to PlaneMesh.
        /// Passed to NavObstacleManager and TerrainBrush so they share the same bake strategy.
        /// </summary>
        private Node3D? _terrain = null;

        /// <summary>
        /// Live scenario being edited. Mutated as the user moves entities in Edit mode.
        /// Persisted to JSON when the user saves (future P2.3 save button).
        /// </summary>
        private ScenarioData? _scenario;

        // ── Story 1.7: fail-closed validation gate (shadow on master) + canonical-model hash ──────
        /// <summary>The single pre-tick validation gate (Godot-free). Shadow-mode on master; the GD.PrintErr
        /// rejection log and the shadow/fail-closed apply policy live here in presentation.</summary>
        private readonly Definitions.ScenarioValidator _validator = new();
        /// <summary>Fail-closed toggle (CHIMERA_VALIDATE_FAILCLOSED, default off). Flip only on a release branch.</summary>
        private static readonly bool _failClosed = Definitions.ScenarioGate.IsFailClosed();
        /// <summary>In-memory <see cref="ScenarioData"/> mirror of the hardcoded fallback — the fallback path
        /// leaves <see cref="_scenario"/> null but still needs a real canonical hash at :316.</summary>
        private ScenarioData? _fallbackMirror;

        // Story 1.7 review patch: true once a scenario (or the always-applied fallback) actually reaches the sim.
        // In fail-closed mode a rejected model leaves this false, so _Ready publishes no canonical hash for it.
        private bool _scenarioApplied;

        // ── HUD ───────────────────────────────────────────────────────────────

        private CanvasLayer    _uiCanvas       = null!;
        private Label          _hudLabel       = null!;
        private Label          _resourceLabel  = null!;
        private Label          _controlsLabel  = null!;  // context-sensitive shortcut strip (bottom)
        private PanelContainer _stallBanner    = null!;
        private UI.MinimapBridge _minimap      = null!;

        // ── Multiplayer ───────────────────────────────────────────────────────

        private ENetTransport    _transport       = null!;
        private LockstepManager  _lockstep        = null!;
        private LobbyUi          _lobbyUi         = null!;
        private UI.MatchChatOverlay    _chatOverlay    = null!;
        private UI.ContentBrowserPanel _contentBrowser = null!;
        private UI.SettingsManager     _settingsMgr    = null!;
        private UI.SettingsPanel       _settingsPanel  = null!;
        private UI.MainMenuOverlay     _mainMenu       = null!;
        private UI.AudioManager        _audioMgr       = null!;

        // ── Replay system ─────────────────────────────────────────────────────

        /// <summary>
        /// Active recorder during an online match. Null when not recording.
        /// Auto-started in <see cref="OnMatchStart"/>; closed in <see cref="ShowGameOver"/>
        /// or when returning to Edit mode.
        /// </summary>
        private ReplayRecorder? _replayRecorder;

        /// <summary>
        /// Active replay player. Non-null only when <see cref="ReplayPath"/> is set.
        /// Replaces the online Flush() path in <see cref="_Process"/>.
        /// </summary>
        private ReplayPlayer? _replayPlayer;

        /// <summary>HUD label showing "◉ REC" / "▶ REPLAY" state.</summary>
        private Label? _replayStatusLabel;

        // ── Worker build placement ────────────────────────────────────────────

        /// <summary>Worker ID waiting to receive a placement click, or -1 when not in placement mode.</summary>
        private int _pendingBuildWorkerId = -1;
        private BuildingType _pendingBuildType;
        /// <summary>Semi-transparent ghost mesh shown while the player is picking a placement spot.</summary>
        private MeshInstance3D? _buildGhost;

        // ── Win condition ─────────────────────────────────────────────────────

        private PanelContainer _winConditionPanel = null!;
        private Control        _gameOverOverlay   = null!;
        private bool           _gameOver          = false;
        private int            _playFrames        = 0;

        // ── Trigger system ────────────────────────────────────────────────────

        private ScenarioDirector                      _scenarioDirector = null!;
        private CreationSuite.TriggerEditorPanel      _triggerPanel     = null!;
        private CreationSuite.MapGeneratorPanel       _mapGenPanel      = null!;
        private AI.LLMService                         _llmService       = null!;
        private Label?                                _toastLabel;
        private float                                 _toastTimer;

        // Pending AI-generated scenario: written before scene reload, consumed in LoadAndApplyScenario.
        // Static so it survives the Godot scene reload cycle.
        private static ScenarioData? _pendingGeneratedScenario;

        // ── Match stats ───────────────────────────────────────────────────────

        private readonly MatchStats _matchStats    = new MatchStats();
        /// <summary>Time.GetTicksMsec() value when Play mode first started this match.</summary>
        private ulong _matchStartMs = 0;

        // ── Inspector ─────────────────────────────────────────────────────────

        /// <summary>AI opponent difficulty. Change in the Godot Inspector before running.</summary>
        [Export] public AiDifficulty AiLevel { get; set; } = AiDifficulty.Normal;

        /// <summary>
        /// res:// path to the scenario JSON to load on startup.
        /// Change in the Godot Inspector to switch maps without recompiling.
        /// </summary>
        [Export] public string ScenarioPath { get; set; } =
            "res://resources/data/scenarios/alpha_map_01.json";

        /// <summary>
        /// Absolute path to a .chmr replay file to play back on startup.
        /// Leave empty for normal play. When set the game enters Replay mode
        /// immediately on _Ready() — no lobby, no network, just the recorded match.
        /// Example: "C:/Users/Me/AppData/Roaming/Godot/app_userdata/ProjectChimera/replays/2026-04-14_1v1.chmr"
        /// </summary>
        [Export] public string ReplayPath { get; set; } = "";

        // ── Nakama matchmaking config ──────────────────────────────────────────

        /// <summary>
        /// Nakama server host. Typically the same VPS as the dedicated game server.
        /// Set to your VPS public IP or domain for online play.
        /// </summary>
        [Export] public string NakamaHost { get; set; } = "localhost";

        /// <summary>Nakama HTTP port (default 7350).</summary>
        [Export] public int NakamaPort { get; set; } = 7350;

        /// <summary>Nakama server key — must match docker-compose nakama config (default "defaultkey").</summary>
        [Export] public string NakamaKey { get; set; } = "defaultkey";

        /// <summary>
        /// ENet dedicated game server IP. Players auto-connect here after Nakama matching.
        /// For a VPS setup, this is the same IP as NakamaHost on port GameServerPort.
        /// </summary>
        [Export] public string GameServerIp { get; set; } = "localhost";

        /// <summary>ENet dedicated game server port (default 7777).</summary>
        [Export] public int GameServerPort { get; set; } = 7777;

        // ── mod.io UGC pipeline ───────────────────────────────────────────────

        /// <summary>
        /// mod.io game ID — found in the Mod Manager dashboard after registering your game.
        /// Set to 0 to disable the mod.io Online tab in the content browser.
        /// </summary>
        [Export] public int ModIoGameId { get; set; } = 0;

        /// <summary>
        /// mod.io read-only API key from mod.io > API Access.
        /// Required for browsing and downloading mods. Leave empty to disable mod.io features.
        /// </summary>
        [Export] public string ModIoApiKey { get; set; } = "";

        /// <summary>
        /// Anthropic API key for LLM-powered trigger authoring in the Trigger Editor.
        /// Set via Godot Inspector. Leave empty to use local Ollama fallback only.
        /// </summary>
        [Export] public string AnthropicApiKey { get; set; } = "";

        // ── Constants ─────────────────────────────────────────────────────────

        private const string P1_FACTION_JSON = "res://resources/data/factions/alpha_faction.json";
        private const string P2_FACTION_JSON = "res://resources/data/factions/beta_faction.json";
        private const string DAMAGE_TABLE_JSON = "res://resources/data/damage_table.json";

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void _Ready()
        {
            // ── Dedicated server mode ─────────────────────────────────────────────
            // Running headless (no display server): start the relay server and skip
            // all client-side initialization. The server never renders anything.
            if (DisplayServer.GetName() == "headless" || OS.HasFeature("dedicated_server"))
            {
                int port = ParsePortArg(ProjectChimera.Multiplayer.DedicatedServer.DEFAULT_PORT);
                GD.Print($"[MainScene] Headless mode — starting dedicated server on port {port}.");
                var server = new ProjectChimera.Multiplayer.DedicatedServer();
                AddChild(server);
                server.Start(port);
                return; // skip all visual / client setup
            }

            // Load faction definitions — P1 (alpha) and P2 (beta/Iron Pact)
            string factionAbs = ProjectSettings.GlobalizePath(P1_FACTION_JSON);
            _factionDef = System.IO.File.Exists(factionAbs)
                ? FactionDefinition.LoadFromFile(factionAbs)
                : new FactionDefinition();

            string faction2Abs = ProjectSettings.GlobalizePath(P2_FACTION_JSON);
            _factionDef2 = System.IO.File.Exists(faction2Abs)
                ? FactionDefinition.LoadFromFile(faction2Abs)
                : new FactionDefinition();

            // Default slot assignments — overwritten per-slot by ApplyScenario
            _slotFactionDefs = new FactionDefinition[5];
            _slotFactionDefs[(int)Faction.Player1] = _factionDef;
            _slotFactionDefs[(int)Faction.Player2] = _factionDef2;

            _world        = new EntityWorld();
            _nodes        = new ResourceNodeStore();
            _resources    = new ResourceStore(Fixed.Zero); // starting ore set by scenario
            _buildings    = new BuildingStore();
            _projectiles  = new Combat.ProjectileStore();
            _combatEvents = new Combat.CombatEventQueue();

            // Damage multipliers (AR-26): load the creator-editable table. A malformed file fails closed
            // with a located error (DamageTable.FromJson); a MISSING file falls back to the canonical
            // Default (matching the FactionDefinition graceful pattern above).
            string damageTableAbs = ProjectSettings.GlobalizePath(DAMAGE_TABLE_JSON);
            _damageTable = System.IO.File.Exists(damageTableAbs)
                ? Combat.DamageTable.Load(damageTableAbs)
                : Combat.DamageTable.Default;

            _fog = new FogOfWarSystem(Faction.Player1);

            _buildSys = new BuildingSystem(_buildings, _resources, _factionDef, _factionDef2, _matchStats);
            _scenarioDirector = new ScenarioDirector(_buildings, _resources);
            _simLoop = new SimulationLoop(_world,
                _buildSys,
                new GatheringSystem(_nodes, _resources, _matchStats),
                new MovementSystem(),
                new CombatSystem(_projectiles, _combatEvents, _matchStats, _damageTable),
                new Combat.ProjectileSystem(_projectiles, _combatEvents, _matchStats, _damageTable),
                new SupplySystem(_resources),
                _fog,
                new AiOpponentSystem(_buildings, _resources, _buildSys, AiLevel),
                _scenarioDirector); // runs last — sees fully-updated world state

            // TODO(5.1): derive active player count from the loaded scenario's assigned slots.
            // 2-player today, so new FactionRegistry(2) is behaviour-preserving (Ore[P1]+Ore[P2], byte-identical).
            _simLoop.EnableChecksums(_buildings, _resources, new FactionRegistry(2));
            _simLoop.OnChecksum = (tick, checksum) =>
                GD.Print($"[Checksum] tick={tick} hash=0x{checksum:X8}");

            SetupSettings();        // load persisted settings before anything reads them
            SetupAudio();           // after settings so SFX bus volume is already applied
            SetupGameState();
            SetupLighting();
            SetupTerrain();
            SetupNavigation();      // must run before SetupCamera so _pathSystem is ready
            SetupCamera();          // wires _selection.Initialize(..., _pathSystem)
            SetupRendering();
            SetupHud();
            SetupMinimap();         // after world/buildings are ready
            SetupTerrainBrush();    // after camera and nav so deps are ready
            LoadAndApplyScenario();
            SetupFactionVisuals();   // after scenario so per-slot faction meshes are correct

            // Initialize flow field system now that scenario buildings are placed.
            // RebuildObstacles seeds the obstacle map; Initialize snapshots the initial
            // alive state so FlowFieldBridge's per-frame diff starts clean.
            _flowFieldSys.RebuildObstacles(_buildings);
            _flowFieldBridge.Initialize(_world, _flowFieldSys, _buildings);
            GD.Print("[Navigation] FlowFieldBridge initialized — NavServer3D replaced for deterministic pathfinding.");

            SetupWinConditionUi();  // after scenario so initial WinCondition is correct
            SetupGameOverOverlay(); // pure UI, no scenario dependency
            SetupMultiplayer();       // transport + lobby (last — UI layers on top of everything)
            SetupReplayStatusLabel();
            SetupContentBrowser();   // Phase 4 map browser (above all other UI)
            SetupMainMenu();         // Phase 5 — shown on first launch; dismissed on mode choice
            SetupTriggerEditor();    // Phase 5 — LLM trigger authoring (L key in Edit mode)
            SetupMapGenerator();     // Phase 5 — AI map generation (M key in Edit mode)

            // Compute scenario hash now that both scenario and lobby are ready.
            // Sent with the Ready packet so peers can detect map mismatches before starting.
            // Story 1.7 (AR-23): canonical-model hash over the in-memory APPLIED scenario (not file bytes) —
            // stable across whitespace / JSON key order / 1.0-vs-1 / file path, fixing the AI-gen stale-file
            // desync. Folded to the existing 32-bit Ready-packet wire (widening is Epic 9). _scenario holds the
            // applied model for the file / AI / editor paths; _fallbackMirror holds it for the hardcoded fallback.
            // Story 1.7 review patch: only publish a hash for a model that was actually applied. In fail-closed
            // mode a rejected scenario leaves _scenarioApplied false (nothing reached the sim), so we publish 0 —
            // the handshake treats 0 as fail-open/skip rather than advertising a start-state we never built.
            ScenarioData? hashModel = _scenario ?? _fallbackMirror;
            _lobbyUi.ScenarioHash = (_scenarioApplied && hashModel != null)
                ? Definitions.CanonicalModelHash.ToWire(Definitions.CanonicalModelHash.Compute(hashModel))
                : 0u;
            GD.Print($"[MainScene] Scenario hash: 0x{_lobbyUi.ScenarioHash:X8}");

            // If a replay file is specified via the Inspector, load it now and
            // enter Play mode immediately — no lobby, no network required.
            if (!string.IsNullOrEmpty(ReplayPath))
                TryLoadReplay(ReplayPath);

            GD.Print("[MainScene] Ready. F5=Play/Edit, Tab=cycle mode, Shift+Click=worker, " +
                     "L-Drag=box-select, R-Click=move, Ctrl+1-9=group. N=Multiplayer lobby.");
        }

        /// <summary>
        /// Intercepts input while the player is choosing where to place a building.
        /// Left-click confirms placement; right-click or Escape cancels.
        /// Must run in _Input (not _UnhandledInput) so it beats SelectionSystem and Escape handling.
        /// </summary>
        public override void _Input(InputEvent @event)
        {
            if (_pendingBuildWorkerId < 0) return;

            if (@event is InputEventMouseButton mb && mb.Pressed)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (RaycastFloor(mb.Position, out Vector3 hit))
                    {
                        var pos = new FixedVec3(
                            Fixed.FromFloat(hit.X), Fixed.Zero, Fixed.FromFloat(hit.Z));
                        _buildSys.QueueWorkerBuild(
                            _pendingBuildWorkerId, _pendingBuildType, pos,
                            Faction.Player1, _resources, _world);
                    }
                    CancelBuildPlacement();
                    GetViewport().SetInputAsHandled();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    CancelBuildPlacement();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventKey key && key.Pressed && !key.Echo
                     && key.Keycode == Key.Escape)
            {
                CancelBuildPlacement();
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            // Escape — toggle settings panel (any mode).
            if (key.Keycode == Key.Escape)
            {
                _settingsPanel.ToggleVisible();
                GetViewport().SetInputAsHandled();
                return;
            }

            // Edit-mode-only shortcuts.
            if (_gameState.Mode != GameMode.Edit) return;

            if (key.Keycode == Key.N)
            {
                if (_lobbyUi.Visible) _lobbyUi.Close();
                else _lobbyUi.Show();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.O)
            {
                _contentBrowser.ToggleVisible();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.L)
            {
                _triggerPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.M)
            {
                _mapGenPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _Process(double delta)
        {
            if (_gameState.Mode == GameMode.Play && !_gameOver)
            {
                if (_replayPlayer != null)
                {
                    // Replay mode: feed recorded commands instead of live network/input.
                    // Always advances one tick per frame — no stalling.
                    _replayPlayer.Flush(_simLoop.CurrentTick);
                    _simLoop.StepOnce();

                    if (_replayPlayer.IsFinished)
                    {
                        GD.Print($"[Replay] Finished at tick {_simLoop.CurrentTick}.");
                        _replayPlayer = null;
                        if (_replayStatusLabel != null) _replayStatusLabel.Visible = false;
                    }
                }
                else if (_lockstep.IsOnline)
                {
                    // Online: only step the sim when both peers' commands for this tick have arrived.
                    // Flush() sends local commands, polls transport, and returns true when ready.
                    if (_lockstep.Flush(_simLoop.CurrentTick))
                        _simLoop.StepOnce();
                }
                else
                {
                    // Offline: free-running fixed-timestep as before.
                    _simLoop.Update((float)delta);
                }

                if (_playFrames == 0)
                    _matchStartMs = Time.GetTicksMsec();
                _playFrames++;
                if (_playFrames > 180) // ~3 s grace period at 60 fps before checking win
                    CheckWinCondition();
            }
            else if (_gameState.Mode == GameMode.Edit)
            {
                _playFrames = 0;
            }

            // Update build ghost position to follow the mouse cursor.
            if (_pendingBuildWorkerId >= 0 && _buildGhost != null)
            {
                if (RaycastFloor(GetViewport().GetMousePosition(), out Vector3 ghostHit))
                {
                    _buildGhost.GlobalPosition = new Vector3(ghostHit.X, 1.5f, ghostHit.Z);
                    _buildGhost.Visible = true;
                }
            }

            // Drain LLM callbacks and update toast notification.
            _triggerPanel.Update();
            _mapGenPanel.Update();
            if (_toastTimer > 0)
            {
                _toastTimer -= (float)delta;
                if (_toastTimer <= 0 && _toastLabel != null)
                    _toastLabel.Visible = false;
            }

            UpdateHud();
        }

        // ── Scenario loading ──────────────────────────────────────────────────

        /// <summary>
        /// Resolve <see cref="ScenarioPath"/>, load the JSON, and apply it.
        /// Falls back to a hardcoded default if the file is missing or fails to parse.
        /// </summary>
        private void LoadAndApplyScenario()
        {
            // Check for an AI-generated scenario passed across the scene reload boundary.
            if (_pendingGeneratedScenario != null)
            {
                var generated = _pendingGeneratedScenario;
                _pendingGeneratedScenario = null;
                _scenario = generated;
                ApplyScenario(generated);
                GD.Print($"[MainScene] Loaded AI-generated scenario: \"{generated.DisplayName}\"");
                SetupStartPositionBridge();
                return;
            }

            string abs = ProjectSettings.GlobalizePath(ScenarioPath);
            var scenario = ScenarioSerializer.LoadFromFile(abs);

            if (scenario == null)
            {
                GD.PrintErr($"[MainScene] Scenario not found or failed to parse: {ScenarioPath} — using defaults.");
                ApplyFallbackScenario();
            }
            else
            {
                _scenario = scenario;
                ApplyScenario(scenario);
                GD.Print($"[MainScene] Loaded scenario: \"{scenario.DisplayName}\" ({scenario.Id})");
            }

            SetupStartPositionBridge();
        }

        /// <summary>
        /// Story 1.7 shadow-mode gate: run the model through the validator before it is applied to the sim. On
        /// failure, log a LOCATED rejection (presentation-side — the Godot-free validator never logs) and return
        /// whether to still proceed: shadow mode (default) proceeds; fail-closed (CHIMERA_VALIDATE_FAILCLOSED=1)
        /// halts. Never throws.
        /// </summary>
        private bool ValidateBeforeApply(ScenarioData model, string pathLabel)
        {
            Definitions.ValidationResult result = _validator.Validate(model);
            if (!result.Ok)
                GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {result.Error}");
            return Definitions.ScenarioGate.ShouldProceed(result.Ok, _failClosed);
        }

        /// <summary>
        /// Apply a loaded <see cref="ScenarioData"/> to the simulation stores.
        /// Order: faction setup → resource nodes → buildings → units.
        /// </summary>
        private void ApplyScenario(ScenarioData scenario)
        {
            // Story 1.7: single pre-tick validation gate (shadow on master, fail-closed via env toggle). Shadow
            // mode logs located rejections and proceeds; fail-closed refuses to apply an invalid model.
            if (!ValidateBeforeApply(scenario, "ApplyScenario")) return;
            _scenarioApplied = true; // reached only when the gate permits applying (Story 1.7 review patch)

            // ── 1. Player slots: faction def + starting ore + base deposit point ─
            foreach (var slot in scenario.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                var faction = (Faction)(slot.Slot + 1); // slot 0 → Player1, slot 1 → Player2

                // Load the faction definition specified in the scenario slot (may differ from defaults)
                if (!string.IsNullOrEmpty(slot.FactionJson))
                {
                    string abs = ProjectSettings.GlobalizePath(slot.FactionJson);
                    if (System.IO.File.Exists(abs))
                    {
                        var def = FactionDefinition.LoadFromFile(abs);
                        _slotFactionDefs[(int)faction] = def;
                        _buildSys.SetFactionDef(faction, def);
                    }
                }

                _resources.AddOre(faction, Fixed.FromFloat(slot.StartOre));
                _resources.FactionBase[(int)faction] = new FixedVec3(
                    Fixed.FromFloat(slot.BaseX), Fixed.Zero, Fixed.FromFloat(slot.BaseZ));
            }

            // ── 2. Resource nodes ─────────────────────────────────────────────
            foreach (var node in scenario.ResourceNodes ?? System.Array.Empty<ScenarioResourceNode>())
            {
                var pos = new FixedVec3(Fixed.FromFloat(node.X), Fixed.Zero, Fixed.FromFloat(node.Z));
                _nodes.Create(pos, Fixed.FromFloat(node.Supply),
                              Fixed.FromFloat(node.Rate), node.MaxGatherers);
            }

            // ── 3. Buildings ──────────────────────────────────────────────────
            foreach (var b in scenario.Buildings ?? System.Array.Empty<ScenarioBuilding>())
            {
                var faction  = (Faction)(b.Slot + 1);
                var pos      = new FixedVec3(Fixed.FromFloat(b.X), Fixed.Zero, Fixed.FromFloat(b.Z));
                var bType    = ParseBuildingType(b.Type);
                _buildSys.PlaceBuildingDirect(bType, faction, pos, b.PreBuilt);
            }

            // ── 4. Units ──────────────────────────────────────────────────────
            foreach (var u in scenario.Units ?? System.Array.Empty<ScenarioUnit>())
            {
                var faction = (Faction)(u.Slot + 1);
                // Look up def from the per-slot faction definition set during slot processing above.
                int fIdx = (int)faction;
                var factionDef = (fIdx >= 0 && fIdx < _slotFactionDefs.Length)
                    ? _slotFactionDefs[fIdx] : _factionDef;
                var def = factionDef?.GetUnit(u.UnitId);
                if (def == null)
                {
                    GD.PrintErr($"[MainScene] Scenario unit_id '{u.UnitId}' not found in faction — skipped.");
                    continue;
                }
                SpawnScenarioUnit(def, faction, u.X, u.Z);
            }

            // ── 5. Triggers ────────────────────────────────────────────────────
            _scenarioDirector.LoadScenario(scenario);
        }

        /// <summary>Spawn a unit from a UnitDefinition, wiring all SoA fields.</summary>
        private void SpawnScenarioUnit(UnitDefinition def, Faction faction, float x, float z)
        {
            var pos = new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));
            int id  = _world.Create(pos, faction,
                                    Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            if (id < 0) return;

            _world.VisionRange[id]  = Fixed.FromFloat(def.VisionRange);
            _world.AttackRange[id]  = Fixed.FromFloat(def.AttackRange);
            _world.AttackDamage[id] = Fixed.FromFloat(def.AttackDamage);
            _world.AttackSpeed[id]  = Fixed.FromFloat(def.AttackSpeed);
            _world.DamageTypeOf[id] = def.ParsedDamageType;
            _world.ArmorTypeOf[id]  = def.ParsedArmorType;
            _world.SplashRadius[id] = Fixed.FromFloat(def.SplashRadius);
            _world.SupplyCost[id]   = (byte)def.Supply;

            // Presentation: tag the unit type so MultiMeshBridge renders the right mesh.
            int   fIdx     = (int)faction;
            var   fdef     = (fIdx >= 0 && fIdx < _slotFactionDefs.Length)
                ? _slotFactionDefs[fIdx] : _factionDef;
            int   meshType = fdef?.IndexOfUnit(def.Id) ?? -1;
            _world.MeshType[id] = (byte)(meshType < 0 ? 0 : meshType);

            // Workers need gatherer state; combat units stay at default (Idle command)
            if (string.Equals(def.Category, "Worker", StringComparison.OrdinalIgnoreCase))
            {
                _world.GatherState[id]   = GatherState.Idle;
                _world.CarryCapacity[id] = Fixed.FromFloat(20f);
            }
        }

        /// <summary>Parse a building type string to its enum value.</summary>
        private static BuildingType ParseBuildingType(string type) => type switch
        {
            "Barracks"      => BuildingType.Barracks,
            "ArcheryRange"  => BuildingType.ArcheryRange,
            "SiegeWorkshop" => BuildingType.SiegeWorkshop,
            _               => BuildingType.CommandCenter,
        };

        /// <summary>
        /// Hardcoded fallback used only if the scenario JSON is missing.
        /// Mirrors the layout in alpha_map_01.json so the game is always playable.
        /// </summary>
        private void ApplyFallbackScenario()
        {
            // Story 1.7 (D9): build a ScenarioData mirror of the hardcoded fallback so it passes through the same
            // validation gate (AC1) and gets a real canonical hash at :316 (AC3). The hardcoded apply below is
            // kept VERBATIM — we do NOT reroute through ApplyScenario (that would newly fire match_start triggers
            // and move behavior). The fallback is the missing-file safety net, so it is always applied; the gate
            // call is shadow-validation only (its result is intentionally not used to halt the safety net).
            _fallbackMirror = BuildFallbackMirror();
            ValidateBeforeApply(_fallbackMirror, "fallback");
            _scenarioApplied = true; // the fallback is the always-applied safety net (Story 1.7 review patch)

            // Faction bases
            _resources.FactionBase[(int)Faction.Player1] = new FixedVec3(
                Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero);
            _resources.FactionBase[(int)Faction.Player2] = new FixedVec3(
                Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero);

            // Starting ore
            _resources.AddOre(Faction.Player1, Fixed.FromFloat(200f));
            _resources.AddOre(Faction.Player2, Fixed.FromFloat(200f));

            // Resource nodes
            var rate = Fixed.FromFloat(5f);
            foreach (var (x, z, supply) in new (float, float, float)[]
            {
                ( -20f, -15f, 600f ), ( -20f,  15f, 600f ),
                (  20f, -15f, 600f ), (  20f,  15f, 600f ),
                (   0f, -25f, 400f ), (   0f,  25f, 400f ),
                ( -35f,   0f, 300f ), (  35f,   0f, 300f ),
            })
            {
                _nodes.Create(
                    new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)),
                    Fixed.FromFloat(supply), rate, maxGatherers: 4);
            }

            // Starter command centres
            _buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), preBuilt: true);
            _buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2,
                new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero), preBuilt: true);

            // 2 workers per faction — each faction uses its own worker definition
            var workerDef  = _slotFactionDefs[(int)Faction.Player1]?.GetUnitByCategory("Worker");
            var workerDef2 = _slotFactionDefs[(int)Faction.Player2]?.GetUnitByCategory("Worker") ?? workerDef;
            if (workerDef != null)
            {
                SpawnScenarioUnit(workerDef,  Faction.Player1, -42f, -3f);
                SpawnScenarioUnit(workerDef,  Faction.Player1, -42f, +3f);
            }
            if (workerDef2 != null)
            {
                SpawnScenarioUnit(workerDef2, Faction.Player2, +42f, -3f);
                SpawnScenarioUnit(workerDef2, Faction.Player2, +42f, +3f);
            }
        }

        /// <summary>
        /// Story 1.7: a <see cref="ScenarioData"/> mirror of the hardcoded <see cref="ApplyFallbackScenario"/>
        /// layout, used ONLY to feed the validation gate and the canonical-model hash (the fallback writes stores
        /// directly and never builds a ScenarioData). Keep these literal values in sync with the apply above;
        /// unit_id "worker" is the conventional worker id. Local-only — the fallback fires only when the scenario
        /// file is missing.
        /// </summary>
        private static ScenarioData BuildFallbackMirror() => new ScenarioData
        {
            Id           = "fallback",
            DisplayName  = "Fallback",
            MapBounds    = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z = -25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z =  25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
            },
            Units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z =  3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z =  3f },
            },
        };

        // ── Setup ─────────────────────────────────────────────────────────────

        private void SetupGameState()
        {
            _gameState = new GameState();
            AddChild(_gameState);
        }

        private void SetupCamera()
        {
            _camCtrl = new RtsCameraController();
            _camCtrl.Position = Vector3.Zero;

            // Seed camera properties from persisted settings before first frame.
            var s = _settingsMgr.Current;
            _camCtrl.PanSpeedMultiplier  = s.CameraSpeed;
            _camCtrl.ZoomSpeedMultiplier = s.CameraZoomSpeed;
            _camCtrl.EdgeScrollEnabled   = s.EdgeScrollEnabled;

            AddChild(_camCtrl);

            _placer = new EntityPlacer();
            AddChild(_placer);
            _placer.Initialize(_camCtrl, _world, _nodes, _resources, _buildings, _factionDef,
                               MoveStartPosition, _factionDef2);

            _selection = new SelectionSystem();
            AddChild(_selection);
            _selection.Initialize(_camCtrl, _world, _flowFieldBridge, _buildings);

            _commandCard = new CommandCardSystem();
            AddChild(_commandCard);
            _commandCard.Initialize(_selection, _buildSys, _buildings, _resources, _world);
            _commandCard.OnWorkerBuildRequested += EnterBuildPlacementMode;
        }

        private void SetupLighting()
        {
            var light = new DirectionalLight3D();
            light.Rotation = new Vector3(Mathf.DegToRad(-50), Mathf.DegToRad(30), 0);
            light.LightEnergy = 1.2f;
            AddChild(light);

            var ambient = new WorldEnvironment();
            var env = new Godot.Environment();
            env.AmbientLightSource  = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor   = new Color(0.3f, 0.3f, 0.35f);
            env.AmbientLightEnergy  = 0.5f;
            ambient.Environment = env;
            AddChild(ambient);
        }

        /// <summary>
        /// Create the terrain. Attempts Terrain3D (GDExtension) first; falls back to a flat
        /// PlaneMesh with a GLSL grid shader if the extension is not loaded.
        /// Sets <see cref="_terrain"/> on success.
        /// </summary>
        private void SetupTerrain()
        {
            if (ClassDB.ClassExists("Terrain3D") && ClassDB.CanInstantiate("Terrain3D"))
            {
                _terrain = TryCreateTerrain3D();
            }

            if (_terrain == null)
            {
                // Fallback: flat plane with editor-grid shader
                var ground = new MeshInstance3D();
                var plane  = new PlaneMesh { Size = new Vector2(256, 256) };
                plane.Material = new ShaderMaterial { Shader = BuildGridShader() };
                ground.Mesh = plane;
                AddChild(ground);
                GD.Print("[Terrain] Terrain3D unavailable — using flat PlaneMesh.");
            }
        }

        /// <summary>
        /// Dynamically instantiate a Terrain3D node and import a flat 256×256 heightmap region
        /// centred at the origin, covering ±128 world units in XZ at Y=0.
        /// Returns the node on success, or null if anything fails.
        /// </summary>
        private Node3D? TryCreateTerrain3D()
        {
            try
            {
                var obj = ClassDB.Instantiate("Terrain3D").AsGodotObject();
                if (obj is not Node3D terrain)
                {
                    GD.PrintErr("[Terrain] ClassDB.Instantiate(Terrain3D) did not return a Node3D.");
                    return null;
                }

                terrain.Name = "Terrain3D";
                AddChild(terrain);

                // Set region size before importing data
                terrain.Set("region_size", 256);

                // Flat heightmap: 256×256 RF (32-bit float) image, all zeros = Y=0
                var heightImg = Image.CreateEmpty(256, 256, false, Image.Format.Rf);

                // import_images([heightmap, control_map, color_map], global_pos, offset, scale)
                // Position (-128, 0, -128) centres the region on the world origin.
                var images = new Godot.Collections.Array
                {
                    Variant.From(heightImg),
                    new Variant(), // control map — null/nil
                    new Variant(), // color map  — null/nil
                };
                var terrainData = terrain.Get("data").AsGodotObject();
                terrainData?.Call("import_images", images,
                                  new Vector3(-128f, 0f, -128f), 0f, 1f);

                GD.Print("[Terrain] Terrain3D v1.0.1 initialised — flat 256×256 region (-128..+128 XZ).");
                return terrain;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Terrain] Terrain3D setup failed ({ex.Message}) — falling back to PlaneMesh.");
                return null;
            }
        }

        private static Shader BuildGridShader()
        {
            var s = new Shader();
            s.Code = @"
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;
void fragment() {
    vec2 worldUV = UV * 256.0;
    vec2 grid = abs(fract(worldUV) - 0.5) * 2.0;
    float line = max(grid.x, grid.y);
    float mask = smoothstep(0.85, 0.95, line);
    vec3 baseColor = vec3(0.10, 0.16, 0.10);
    vec3 lineColor = vec3(0.20, 0.30, 0.18);
    ALBEDO = mix(baseColor, lineColor, mask * 0.6);
    ROUGHNESS = 1.0;
}
";
            return s;
        }

        private void SetupRendering()
        {
            // Unit and building visuals are faction-dependent and created later, in
            // SetupFactionVisuals() — after the scenario assigns each slot's faction — so
            // per-slot meshes (alpha vs beta) render correctly. Everything below here is
            // scenario-independent and safe to build now.

            // Resource node visuals
            var nodeBridge = new ResourceNodeBridge();
            AddChild(nodeBridge);
            nodeBridge.Initialize(_nodes);

            // Fog of war overlay
            _fogBridge = new FogOfWarBridge();
            AddChild(_fogBridge);
            _fogBridge.Initialize(_fog);

            // Projectile visuals
            var projBridge = new ProjectileBridge();
            AddChild(projBridge);
            projBridge.Initialize(_projectiles);

            // Combat feedback: hit flashes and camera shake
            var feedbackBridge = new CombatFeedbackBridge();
            AddChild(feedbackBridge);
            feedbackBridge.Initialize(_combatEvents, _camCtrl);
        }

        /// <summary>
        /// Create the faction-dependent visuals (per-faction unit bridge + building bridge).
        /// Must run AFTER LoadAndApplyScenario so each slot's faction definition is final —
        /// the unit and building meshes are taken from the faction actually assigned to that
        /// slot, and tinted to the player's team colour (P1 blue / P2 red).
        /// </summary>
        private void SetupFactionVisuals()
        {
            var p1Color = new Color(0.2f, 0.5f, 1.0f); // Player 1 = blue
            var p2Color = new Color(1.0f, 0.3f, 0.2f); // Player 2 = red

            var p1Def = _slotFactionDefs[(int)Faction.Player1] ?? _factionDef;
            var p2Def = _slotFactionDefs[(int)Faction.Player2] ?? _factionDef2;

            var unitP1 = new MultiMeshBridge();
            AddChild(unitP1);
            unitP1.Initialize(_simLoop, p1Def, Faction.Player1, p1Color);

            var unitP2 = new MultiMeshBridge();
            AddChild(unitP2);
            unitP2.Initialize(_simLoop, p2Def, Faction.Player2, p2Color);

            var buildingBridge = new BuildingBridge();
            AddChild(buildingBridge);
            buildingBridge.Initialize(_buildings, p1Def, p2Def, p1Color, p2Color);

            // Keep the editor placement tool in sync with the slot factions so click-to-spawn
            // in Edit mode produces the same mesh + stats the bridges render (it was wired with
            // the defaults in SetupCamera, before the scenario assigned per-slot factions).
            _placer.SetFactionDefs(p1Def, p2Def);
        }

        /// <summary>
        /// Builds a baked NavigationMesh and registers it with the default World3D navigation map.
        ///
        /// When Terrain3D is active (SetupTerrain succeeded), baking uses
        /// Terrain3D.generate_nav_mesh_source_geometry so the walkable surface matches
        /// the heightmap. Building footprints are carved by ParseSourceGeometryData scanning
        /// StaticBody3D children of the NavigationRegion3D.
        ///
        /// When falling back to PlaneMesh, the old approach is used: a flat BoxShape3D
        /// ground body + BakeNavigationMesh(false).
        ///
        /// NavObstacleManager handles per-building rebakes on the same approach.
        /// </summary>
        private void SetupNavigation()
        {
            const float HALF = 120f;

            // ── NavMesh settings — shared by both code paths ───────────────────
            var navMesh = new NavigationMesh();
            navMesh.AgentRadius   = 0.4f;
            navMesh.AgentHeight   = 1.8f;
            navMesh.AgentMaxClimb = 0.25f; // prevents routing over building tops
            navMesh.CellSize      = 1.0f;  // 1 m/cell — fast bake, good resolution
            navMesh.GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders;
            navMesh.GeometrySourceGeometryMode = NavigationMesh.SourceGeometryMode.RootNodeChildren;

            _navRegion = new NavigationRegion3D();
            _navRegion.NavigationMesh = navMesh;
            AddChild(_navRegion);

            if (_terrain != null)
            {
                // ── Terrain3D path: bake from heightmap faces ─────────────────
                // Building StaticBody3D are added as children of _navRegion by
                // NavObstacleManager — ParseSourceGeometryData picks them up for
                // carving on each rebake.
                InitialBakeWithTerrain(navMesh);
                GD.Print("[Navigation] NavMesh baked via Terrain3D.generate_nav_mesh_source_geometry.");
            }
            else
            {
                // ── Fallback path: flat StaticBody3D ground ───────────────────
                const float GROUND_THICK = 0.2f;
                var groundShape = new BoxShape3D
                {
                    Size = new Vector3(HALF * 2f, GROUND_THICK, HALF * 2f)
                };
                var groundCollision = new CollisionShape3D { Shape = groundShape };
                var groundBody = new StaticBody3D
                {
                    Position = new Vector3(0f, -GROUND_THICK * 0.5f, 0f)
                };
                groundBody.AddChild(groundCollision);
                _navRegion.AddChild(groundBody);
                _navRegion.BakeNavigationMesh(false);
                GD.Print("[Navigation] NavMesh baked (flat StaticBody3D ground fallback).");
            }

            Rid navMap = GetWorld3D().NavigationMap;

            _pathSystem = new PathRequestSystem();
            AddChild(_pathSystem);
            _pathSystem.Initialize(_world, navMap);

            // Flow field pathfinding — deterministic replacement for NavServer3D.
            // Bridge is added to the scene tree here so _Process runs; Initialize() is called
            // in _Ready() after LoadAndApplyScenario() so the obstacle map has all buildings.
            _flowFieldSys    = new FlowFieldSystem();
            _flowFieldBridge = new FlowFieldBridge();
            AddChild(_flowFieldBridge);

            // NavObstacleManager watches BuildingStore and rebakes on any change.
            // Stored as a field so TerrainBrush can call MarkDirty() after sculpting.
            _navObstacles = new NavObstacleManager();
            AddChild(_navObstacles);
            _navObstacles.Initialize(_buildings, _navRegion, _terrain);

            GD.Print($"[Navigation] Map RID={navMap}, walkable ±{HALF} units. NavObstacleManager + FlowFieldBridge active.");
        }

        /// <summary>
        /// Initial NavMesh bake using Terrain3D geometry. Called once in <see cref="SetupNavigation"/>
        /// before any buildings are placed (buildings are carved on the first NavObstacleManager rebake).
        /// </summary>
        private void InitialBakeWithTerrain(NavigationMesh navMeshTemplate)
        {
            const float HALF = 120f;

            // Duplicate so assigning the result back to _navRegion forces re-registration
            var navMesh = (NavigationMesh)navMeshTemplate.Duplicate()!;

            var sourceGeo = new NavigationMeshSourceGeometryData3D();

            // Parse any existing StaticBody3D children of _navRegion (none yet, but future-safe)
            NavigationServer3D.ParseSourceGeometryData(navMeshTemplate, sourceGeo, _navRegion);

            // Add terrain walkable surface from the heightmap (flat at Y=0 initially)
            var aabb = new Aabb(
                new Vector3(-HALF, -5f, -HALF),
                new Vector3(HALF * 2f, 10f, HALF * 2f));
            var faces = _terrain!.Call("generate_nav_mesh_source_geometry", aabb, false)
                                  .As<Vector3[]>();
            if (faces.Length > 0)
                sourceGeo.AddFaces(faces, Transform3D.Identity);

            NavigationServer3D.BakeFromSourceGeometryData(navMesh, sourceGeo);
            _navRegion.NavigationMesh = navMesh;
        }

        /// <summary>
        /// Attach the terrain sculpting brush. Requires Terrain3D (_terrain != null).
        /// No-ops silently when running with the PlaneMesh fallback.
        /// </summary>
        /// <summary>
        /// Create flag-pole markers for the two player start positions.
        /// Reads initial XZ from the live scenario (or fallback defaults).
        /// </summary>
        private void SetupStartPositionBridge()
        {
            var positions = new (float x, float z)[2];

            if (_scenario != null)
            {
                foreach (var slot in _scenario.PlayerSlots)
                {
                    int idx = System.Math.Clamp(slot.Slot, 0, 1);
                    positions[idx] = (slot.BaseX, slot.BaseZ);
                }
            }
            else
            {
                // Fallback positions matching ApplyFallbackScenario
                positions[0] = (-45f, 0f);
                positions[1] = (+45f, 0f);
            }

            _startPosBridge = new StartPositionBridge();
            AddChild(_startPosBridge);
            _startPosBridge.Initialize(positions);
        }

        /// <summary>
        /// Called by EntityPlacer when the user places a start-position marker in Edit mode.
        /// Updates both the live scenario data and the simulation's faction base point.
        /// </summary>
        private void MoveStartPosition(int slot, Vector3 worldPos, float startOre)
        {
            // Update scenario data (persisted on save)
            if (_scenario != null)
            {
                foreach (var s in _scenario.PlayerSlots)
                {
                    if (s.Slot == slot)
                    {
                        s.BaseX    = worldPos.X;
                        s.BaseZ    = worldPos.Z;
                        s.StartOre = startOre;
                        break;
                    }
                }
            }

            // Update live sim: faction deposit / rally point
            var faction = (Faction)(slot + 1);
            _resources.FactionBase[(int)faction] = new FixedVec3(
                Fixed.FromFloat(worldPos.X), Fixed.Zero, Fixed.FromFloat(worldPos.Z));

            // Move the visual marker
            _startPosBridge.SetPosition(slot, worldPos);
        }

        private void SetupTerrainBrush()
        {
            if (_terrain == null) return; // PlaneMesh fallback: no brush
            var brush = new TerrainBrush();
            AddChild(brush);
            brush.Initialize(_terrain, _camCtrl, _navObstacles, _gameState);
            GD.Print("[MainScene] TerrainBrush ready — press T in Edit mode to activate.");
        }

        private void SetupHud()
        {
            _uiCanvas = new CanvasLayer();
            AddChild(_uiCanvas);

            // ── Game-state info: top-left, 3 compact lines ────────────────────
            var hudBg = new StyleBoxFlat
            {
                BgColor                 = new Color(0f, 0f, 0f, 0.55f),
                CornerRadiusTopLeft     = 0,
                CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 0,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 10f,
                ContentMarginRight      = 14f,
                ContentMarginTop        = 6f,
                ContentMarginBottom     = 6f,
            };
            var hudPanel = new PanelContainer();
            hudPanel.AnchorLeft   = 0f;
            hudPanel.AnchorTop    = 0f;
            hudPanel.AnchorRight  = 0f;
            hudPanel.AnchorBottom = 0f;
            hudPanel.OffsetTop    = 4f;
            hudPanel.GrowHorizontal = Control.GrowDirection.End;
            hudPanel.AddThemeStyleboxOverride("panel", hudBg);
            _uiCanvas.AddChild(hudPanel);

            _hudLabel = new Label();
            _hudLabel.AddThemeColorOverride("font_color", Colors.White);
            _hudLabel.AddThemeFontSizeOverride("font_size", 15);
            hudPanel.AddChild(_hudLabel);

            // ── Resource strip: just below the HUD panel ──────────────────────
            _resourceLabel = new Label();
            _resourceLabel.AnchorLeft   = 0f;
            _resourceLabel.AnchorTop    = 0f;
            _resourceLabel.OffsetTop    = 80f;
            _resourceLabel.OffsetLeft   = 10f;
            _resourceLabel.AddThemeColorOverride("font_color", new Color(1f, 0.88f, 0.25f));
            _resourceLabel.AddThemeFontSizeOverride("font_size", 14);
            _uiCanvas.AddChild(_resourceLabel);

            // ── Controls strip: bottom-left, context-sensitive shortcut hints ─
            _controlsLabel = new Label();
            _controlsLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            _controlsLabel.OffsetBottom = -6f;
            _controlsLabel.OffsetLeft   = 6f;
            _controlsLabel.OffsetTop    = -32f;
            _controlsLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.52f, 0.55f));
            _controlsLabel.AddThemeFontSizeOverride("font_size", 12);
            _uiCanvas.AddChild(_controlsLabel);

            // ── Stall banner: centered at top, hidden until peer is slow ─────
            _stallBanner = new PanelContainer { Visible = false };
            _stallBanner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
            _stallBanner.GrowHorizontal = Control.GrowDirection.Both;
            _stallBanner.OffsetTop      = 8f;

            var stallStyle = new StyleBoxFlat
            {
                BgColor                 = new Color(0.8f, 0.6f, 0f, 0.88f),
                CornerRadiusTopLeft     = 6,
                CornerRadiusTopRight    = 6,
                CornerRadiusBottomLeft  = 6,
                CornerRadiusBottomRight = 6,
                ContentMarginLeft       = 18f,
                ContentMarginRight      = 18f,
                ContentMarginTop        = 6f,
                ContentMarginBottom     = 6f,
            };
            _stallBanner.AddThemeStyleboxOverride("panel", stallStyle);

            var stallLabel = new Label { Text = "Waiting for peer\u2026" };
            stallLabel.AddThemeFontSizeOverride("font_size", 17);
            stallLabel.AddThemeColorOverride("font_color", Colors.White);
            _stallBanner.AddChild(stallLabel);

            _uiCanvas.AddChild(_stallBanner);
        }

        private void SetupMinimap()
        {
            _minimap = new UI.MinimapBridge();
            _uiCanvas.AddChild(_minimap);
            _minimap.Initialize(_world, _buildings, _fog, _camCtrl);
        }

        // ── HUD ───────────────────────────────────────────────────────────────

        private void UpdateHud()
        {
            bool isEdit = _gameState.Mode == GameMode.Edit;
            string modeTag = isEdit ? "EDIT" : "PLAY";

            // ── Line 1: performance / sim state ──────────────────────────────
            string checksumStr = _simLoop.LastChecksum == 0 ? "—"
                : $"0x{_simLoop.LastChecksum:X8}";
            string onlineTag = _lockstep.IsOnline ? "  ONLINE" : "";

            // ── Line 2: unit counts ───────────────────────────────────────────
            int p1 = CountFaction(Faction.Player1);
            int p2 = CountFaction(Faction.Player2);

            // ── Line 3: selection / placing state ─────────────────────────────
            int selCount  = _selection.SelectedIds.Count;
            string groupTag = _selection.ActiveGroupIndex >= 0
                ? $" [grp {_selection.ActiveGroupIndex + 1}]" : "";
            string selInfo = selCount == 0 ? "—"
                : selCount == 1
                    ? $"id {_selection.FocusId} [{_world.FactionOf[_selection.FocusId]}]{groupTag}"
                    : $"{selCount} units{groupTag}";

            _hudLabel.Text =
                $"FPS {Engine.GetFramesPerSecond()}   [{modeTag}]   Tick {_simLoop.CurrentTick}   Hash {checksumStr}{onlineTag}\n" +
                $"P1: {p1} units   P2: {p2} units   Total: {_world.AliveCount}\n" +
                (isEdit ? $"Placing: {_placer.ModeLabel}" : $"Selected: {selInfo}");

            // ── Resource label: ore + supply ──────────────────────────────────
            int p1Ore    = (int)_resources.Ore[(int)Faction.Player1].ToFloat();
            int p2Ore    = (int)_resources.Ore[(int)Faction.Player2].ToFloat();
            int p1Sup    = _resources.SupplyUsed[(int)Faction.Player1];
            int p2Sup    = _resources.SupplyUsed[(int)Faction.Player2];
            int p1SupCap = _resources.SupplyCap[(int)Faction.Player1];
            int p2SupCap = _resources.SupplyCap[(int)Faction.Player2];
            int nodes    = CountActiveNodes();
            int bldgs    = CountAliveBuildings();

            _resourceLabel.Text =
                $"P1  {p1Ore,5} ore   {p1Sup}/{p1SupCap} supply\n" +
                $"P2  {p2Ore,5} ore   {p2Sup}/{p2SupCap} supply\n" +
                $"Nodes: {nodes}   Buildings: {bldgs}";

            // ── Controls strip: context-sensitive shortcut hints ──────────────
            if (_pendingBuildWorkerId >= 0)
            {
                string bName = _pendingBuildType switch
                {
                    BuildingType.CommandCenter => "Command Center",
                    BuildingType.Barracks      => "Barracks",
                    BuildingType.ArcheryRange  => "Archery Range",
                    BuildingType.SiegeWorkshop => "Siege Workshop",
                    _ => "Building"
                };
                _controlsLabel.Text = $"Placing {bName} — Left-click to place   Right-click / Esc to cancel";
            }
            else if (isEdit)
            {
                string snap = _placer.GridSnapEnabled ? "ON" : "OFF";
                string edge = _camCtrl.EdgeScrollEnabled ? "ON" : "OFF";
                _controlsLabel.Text =
                    $"F5=Play   N=Lobby   O=Maps   Esc=Settings   T=Terrain   G=Snap({snap})   E=Edge({edge})" +
                    $"   Tab=Mode   U=Unit   B=Building   Del=Delete   Ctrl+Z=Undo";
            }
            else
            {
                _controlsLabel.Text =
                    "F5=Edit   R-Click=Move   Q+Click=AttackMove   S=Stop   H=Hold   1-9=Groups   Esc=Deselect";
            }

            // ── Stall banner ──────────────────────────────────────────────────
            _stallBanner.Visible = _lockstep.IsOnline && _lockstep.IsStalling;
        }

        // ── Worker build placement ────────────────────────────────────────────

        /// <summary>
        /// Called when the player clicks a build button on a worker's command card.
        /// Enters placement mode: a ghost mesh tracks the cursor until the player
        /// left-clicks a position (confirming the build) or cancels with Esc/right-click.
        /// </summary>
        private void EnterBuildPlacementMode(int workerId, BuildingType bType)
        {
            _pendingBuildWorkerId = workerId;
            _pendingBuildType     = bType;

            // Create or replace the ghost mesh for the new building type.
            _buildGhost?.QueueFree();
            var box = new BoxMesh();
            box.Size = new Vector3(4f, 3f, 4f);
            var mat = new StandardMaterial3D();
            mat.AlbedoColor  = new Color(0.3f, 0.8f, 0.3f, 0.45f);
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded;
            box.Material     = mat;
            _buildGhost          = new MeshInstance3D();
            _buildGhost.Mesh     = box;
            _buildGhost.Visible  = false;
            AddChild(_buildGhost);

            string bName = _pendingBuildType switch
            {
                BuildingType.CommandCenter => "Command Center",
                BuildingType.Barracks      => "Barracks",
                BuildingType.ArcheryRange  => "Archery Range",
                BuildingType.SiegeWorkshop => "Siege Workshop",
                _ => "Building"
            };
            GD.Print($"[MainScene] Placement mode: {bName} (worker {workerId}) — click to place.");
        }

        /// <summary>Exit placement mode, hide the ghost, and reset state.</summary>
        private void CancelBuildPlacement()
        {
            _pendingBuildWorkerId = -1;
            if (_buildGhost != null)
                _buildGhost.Visible = false;
        }

        /// <summary>
        /// Cast a ray from the camera through <paramref name="screenPos"/> and find
        /// where it intersects the Y=0 ground plane.
        /// </summary>
        private bool RaycastFloor(Vector2 screenPos, out Vector3 hit)
        {
            hit = Vector3.Zero;
            var camera = _camCtrl?.GetCamera();
            if (camera == null) return false;

            Vector3 origin = camera.ProjectRayOrigin(screenPos);
            Vector3 dir    = camera.ProjectRayNormal(screenPos);
            if (Mathf.Abs(dir.Y) < 0.0001f) return false;

            float t = -origin.Y / dir.Y;
            if (t < 0f) return false;

            hit = origin + dir * t;
            return true;
        }

        private int CountFaction(Faction faction)
        {
            int count = 0, cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
                if (_world.IsAlive(i) && _world.FactionOf[i] == faction) count++;
            return count;
        }

        private int CountActiveNodes()
        {
            int count = 0;
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes.Active[i]) count++;
            return count;
        }

        private int CountAliveBuildings()
        {
            int count = 0;
            for (int i = 0; i < _buildings.Count; i++)
                if (_buildings.Alive[i]) count++;
            return count;
        }

        // ── Win Condition UI ──────────────────────────────────────────────────

        /// <summary>
        /// Build the Edit-mode panel that lets designers choose the win condition.
        /// Hidden when switching to Play mode; restored on return to Edit.
        /// </summary>
        private void SetupWinConditionUi()
        {
            float vpWidth = GetViewport().GetVisibleRect().Size.X;

            var panel = new PanelContainer
            {
                Position          = new Vector2(vpWidth - 360f, 330f),
                CustomMinimumSize = new Vector2(350f, 0f),
            };

            var vbox = new VBoxContainer();
            panel.AddChild(vbox);

            var title = new Label { Text = "Win Condition" };
            title.AddThemeFontSizeOverride("font_size", 14);
            vbox.AddChild(title);

            var group = new ButtonGroup();
            var current = _scenario?.WinCondition ?? WinCondition.DestroyAllBuildings;

            var btnBuildings = new Button
            {
                Text          = "Destroy All Buildings",
                ToggleMode    = true,
                ButtonPressed = current == WinCondition.DestroyAllBuildings,
            };
            btnBuildings.ButtonGroup = group;
            vbox.AddChild(btnBuildings);

            var btnUnits = new Button
            {
                Text          = "Eliminate All Units",
                ToggleMode    = true,
                ButtonPressed = current == WinCondition.EliminateAllUnits,
            };
            btnUnits.ButtonGroup = group;
            vbox.AddChild(btnUnits);

            btnBuildings.Toggled += (on) => { if (on && _scenario != null) _scenario.WinCondition = WinCondition.DestroyAllBuildings; };
            btnUnits.Toggled     += (on) => { if (on && _scenario != null) _scenario.WinCondition = WinCondition.EliminateAllUnits; };

            // ── Map I/O section ────────────────────────────────────────────────
            vbox.AddChild(new HSeparator());

            var ioTitle = new Label { Text = "Map Package" };
            ioTitle.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(ioTitle);

            // Map name field (pre-filled from scenario display name)
            var nameRow = new HBoxContainer();
            nameRow.AddChild(new Label { Text = "Name:", CustomMinimumSize = new Vector2(54, 0) });
            var mapNameField = new LineEdit
            {
                Text                = _scenario?.DisplayName ?? "My Map",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MaxLength           = 64,
            };
            mapNameField.AddThemeFontSizeOverride("font_size", 12);
            nameRow.AddChild(mapNameField);
            vbox.AddChild(nameRow);

            // Author field
            var authorRow = new HBoxContainer();
            authorRow.AddChild(new Label { Text = "Author:", CustomMinimumSize = new Vector2(54, 0) });
            var authorField = new LineEdit
            {
                Text                = "Unknown",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MaxLength           = 40,
            };
            authorField.AddThemeFontSizeOverride("font_size", 12);
            authorRow.AddChild(authorField);
            vbox.AddChild(authorRow);

            // Export / Import buttons
            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 6);
            var exportBtn = new Button { Text = "Export .chimera.zip",
                                         CustomMinimumSize = new Vector2(160, 30) };
            var importBtn = new Button { Text = "Import .chimera.zip",
                                         CustomMinimumSize = new Vector2(160, 30) };
            exportBtn.AddThemeFontSizeOverride("font_size", 12);
            importBtn.AddThemeFontSizeOverride("font_size", 12);
            btnRow.AddChild(exportBtn);
            btnRow.AddChild(importBtn);
            vbox.AddChild(btnRow);

            var ioStatusLabel = new Label { Text = "" };
            ioStatusLabel.AddThemeFontSizeOverride("font_size", 11);
            ioStatusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            vbox.AddChild(ioStatusLabel);

            exportBtn.Pressed += () => ExportMapPackage(
                mapNameField.Text.Trim(), authorField.Text.Trim(), ioStatusLabel);
            importBtn.Pressed += () => ImportMapPackage(ioStatusLabel);

            _winConditionPanel = panel;
            _uiCanvas.AddChild(panel);

            // Show only in Edit mode; reset game state on return from Play
            _gameState.ModeChanged += (mode) =>
            {
                _winConditionPanel.Visible = (mode == (int)GameMode.Edit);
                if (mode == (int)GameMode.Edit)
                {
                    // Return to Edit: dismiss any active game-over overlay
                    if (_gameOverOverlay != null) _gameOverOverlay.Visible = false;
                    _gameOver     = false;
                    _playFrames   = 0;
                    _matchStartMs = 0;
                    _matchStats.Reset();

                    // Stop recording and clear replay player.
                    StopRecording();
                    _replayPlayer = null;
                    if (_replayStatusLabel != null) _replayStatusLabel.Visible = false;

                    // Reset spectator fog reveal.
                    if (_fogBridge != null) _fogBridge.RevealAll = false;

                    // Close chat overlay and tear down lockstep subscription.
                    _chatOverlay.Close();
                    _chatOverlay.Visible = false;

                    // Close map browser if open.
                    _contentBrowser.Visible = false;
                }
            };

            _winConditionPanel.Visible = (_gameState.Mode == GameMode.Edit);
        }

        /// <summary>
        /// Build the full-screen game-over overlay. Hidden until ShowGameOver() is called.
        /// The overlay is populated with live match data at show-time, not at setup-time.
        // ── Map I/O: Export / Import ──────────────────────────────────────────────

        private void ExportMapPackage(string mapName, string author, Label statusLabel)
        {
            if (_scenario == null) { statusLabel.Text = "No scenario loaded."; return; }

            // Save current scenario state to disk first.
            string scenAbs = ProjectSettings.GlobalizePath(ScenarioPath);
            try { Definitions.ScenarioSerializer.SaveToFile(_scenario, scenAbs); }
            catch (Exception ex) { statusLabel.Text = $"Save failed: {ex.Message}"; return; }

            // Determine output path: same directory as scenario, same slug name.
            string slug   = Definitions.ContentPackager.Slugify(
                string.IsNullOrEmpty(mapName) ? _scenario.DisplayName : mapName);
            string outDir = System.IO.Path.GetDirectoryName(scenAbs)!;
            string outZip = System.IO.Path.Combine(outDir, $"{slug}.chimera.zip");

            var opts = new Definitions.ContentPackager.PackOptions
            {
                DisplayName   = string.IsNullOrEmpty(mapName) ? _scenario.DisplayName : mapName,
                Author        = string.IsNullOrEmpty(author) ? "Unknown" : author,
                Description   = _scenario.DisplayName,
                PlayerCount   = _scenario.PlayerSlots?.Length ?? 2,
                Tags          = new System.Collections.Generic.List<string>
                {
                    _scenario.PlayerSlots?.Length == 4 ? "2v2" : "1v1"
                },
            };

            try
            {
                var manifest = Definitions.ContentPackager.Pack(scenAbs, outZip, opts);
                statusLabel.Text = $"Exported: {System.IO.Path.GetFileName(outZip)}\n" +
                                   $"Hash: 0x{manifest.ScenarioHash:X8}";
                GD.Print($"[MapIO] Exported package: {outZip}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Export failed: {ex.Message}";
                GD.PrintErr($"[MapIO] Export error: {ex}");
            }
        }

        private void ImportMapPackage(Label statusLabel)
        {
            // Open a native file dialog via Godot's FileDialog node.
            var dlg = new FileDialog
            {
                FileMode  = FileDialog.FileModeEnum.OpenFile,
                Access    = FileDialog.AccessEnum.Filesystem,
                Title     = "Import Map Package",
                Filters   = new[] { "*.chimera.zip ; Chimera Map Package" },
            };
            dlg.FileSelected += (path) =>
            {
                dlg.QueueFree();
                DoImport(path, statusLabel);
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(900, 600));
        }

        private void DoImport(string zipPath, Label statusLabel)
        {
            // Extract to user://imported_maps/<slug>/
            var manifest = Definitions.ContentPackager.ReadManifest(zipPath);
            if (manifest == null) { statusLabel.Text = "Invalid package (no manifest)."; return; }

            string extractDir = ProjectSettings.GlobalizePath(
                $"user://imported_maps/{manifest.Id}/");
            try
            {
                var result = Definitions.ContentPackager.Unpack(zipPath, extractDir);
                // Copy the scenario to the project's scenarios directory so it can be selected.
                string destScenario = ProjectSettings.GlobalizePath(
                    $"res://resources/data/scenarios/{manifest.Id}.json");
                System.IO.File.Copy(result.ScenarioPath, destScenario, overwrite: true);

                // Copy any custom faction files.
                foreach (var fp in result.FactionPaths)
                {
                    string destFaction = ProjectSettings.GlobalizePath(
                        $"res://resources/data/factions/{System.IO.Path.GetFileName(fp)}");
                    System.IO.File.Copy(fp, destFaction, overwrite: true);
                }

                statusLabel.Text = $"Imported: {manifest.DisplayName}\n" +
                                   $"by {manifest.Author} v{manifest.Version}\n" +
                                   $"Set ScenarioPath to: res://resources/data/scenarios/{manifest.Id}.json";
                GD.Print($"[MapIO] Imported '{manifest.DisplayName}' → {destScenario}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Import failed: {ex.Message}";
                GD.PrintErr($"[MapIO] Import error: {ex}");
            }
        }

        /// <summary>
        /// Build the full-screen game-over overlay. Hidden until ShowGameOver() is called.
        /// The overlay is populated with live match data at show-time, not at setup-time.
        /// </summary>
        private void SetupGameOverOverlay()
        {
            // Root dimming rect — reused as the overlay root
            var root = new ColorRect { Color = new Color(0f, 0f, 0f, 0.65f), Visible = false };
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _uiCanvas.AddChild(root);
            _gameOverOverlay = root;
        }

        /// <summary>Populate and display the victory/defeat panel with live match data.</summary>
        private void ShowGameOver(int winnerPlayer)
        {
            _gameOver = true;

            // Notify chat before closing it.
            _chatOverlay.AddSystemMessage($"Player {winnerPlayer} wins! GG");

            // Finalise replay recording — match is over.
            StopRecording();

            // ── Gather stats ─────────────────────────────────────────────────
            ulong elapsedMs = _matchStartMs > 0 ? Time.GetTicksMsec() - _matchStartMs : 0;
            uint  totalSec  = (uint)(elapsedMs / 1000);
            string duration = $"{totalSec / 60}:{totalSec % 60:D2}";

            int p1Kills  = _matchStats.Kills(Faction.Player1);
            int p2Kills  = _matchStats.Kills(Faction.Player2);
            int p1Built  = _matchStats.UnitsBuilt(Faction.Player1);
            int p2Built  = _matchStats.UnitsBuilt(Faction.Player2);
            int p1Ore    = _matchStats.OreMined(Faction.Player1);
            int p2Ore    = _matchStats.OreMined(Faction.Player2);

            // Faction colors — match building/selection palette
            Color p1Color = new Color(0.25f, 0.55f, 1.0f);
            Color p2Color = new Color(1.0f,  0.25f, 0.25f);

            // Clear previous children (safety guard against double-trigger)
            foreach (Node child in _gameOverOverlay.GetChildren())
            {
                _gameOverOverlay.RemoveChild(child);
                child.QueueFree();
            }

            // ── Card ─────────────────────────────────────────────────────────
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            card.CustomMinimumSize = new Vector2(560, 380);
            _gameOverOverlay.AddChild(card);

            var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            vbox.AddThemeConstantOverride("separation", 14);
            card.AddChild(vbox);

            // ── Heading ───────────────────────────────────────────────────────
            bool localWin = (winnerPlayer == 1);
            var heading = new Label
            {
                Text                = localWin ? "VICTORY" : "DEFEAT",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            heading.AddThemeFontSizeOverride("font_size", 64);
            heading.AddThemeColorOverride("font_color",
                localWin ? new Color(1f, 0.85f, 0.1f) : new Color(0.8f, 0.2f, 0.2f));
            vbox.AddChild(heading);

            var winner = new Label
            {
                Text                = $"Player {winnerPlayer} Wins!",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            winner.AddThemeFontSizeOverride("font_size", 26);
            winner.AddThemeColorOverride("font_color", winnerPlayer == 1 ? p1Color : p2Color);
            vbox.AddChild(winner);

            vbox.AddChild(new HSeparator());

            // ── Duration ─────────────────────────────────────────────────────
            var durLabel = new Label
            {
                Text                = $"Match Duration:  {duration}",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            durLabel.AddThemeFontSizeOverride("font_size", 20);
            durLabel.AddThemeColorOverride("font_color", Colors.LightGray);
            vbox.AddChild(durLabel);

            vbox.AddChild(new HSeparator());

            // ── Stat table header row ─────────────────────────────────────────
            // Helper: create a two-column stat row with a centred label and two value columns
            void AddStatRow(string rowLabel, string p1Val, string p2Val)
            {
                var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                row.AddThemeConstantOverride("separation", 0);

                // Row label (left)
                var lbl = new Label
                {
                    Text                = rowLabel,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    CustomMinimumSize   = new Vector2(160, 0),
                };
                lbl.AddThemeFontSizeOverride("font_size", 18);
                lbl.AddThemeColorOverride("font_color", Colors.LightGray);

                // P1 value
                var v1 = new Label
                {
                    Text                = p1Val,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    CustomMinimumSize   = new Vector2(140, 0),
                };
                v1.AddThemeFontSizeOverride("font_size", 20);
                v1.AddThemeColorOverride("font_color", p1Color);

                // P2 value
                var v2 = new Label
                {
                    Text                = p2Val,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    CustomMinimumSize   = new Vector2(140, 0),
                };
                v2.AddThemeFontSizeOverride("font_size", 20);
                v2.AddThemeColorOverride("font_color", p2Color);

                row.AddChild(lbl);
                row.AddChild(v1);
                row.AddChild(v2);
                vbox.AddChild(row);
            }

            // Column headers
            AddStatRow("", "Player 1", "Player 2");

            // Stats
            AddStatRow("Kills",        $"{p1Kills}",         $"{p2Kills}");
            AddStatRow("Units Built",  $"{p1Built}",         $"{p2Built}");
            AddStatRow("Ore Mined",    $"{p1Ore:N0}",        $"{p2Ore:N0}");

            vbox.AddChild(new HSeparator());

            // ── Hint ─────────────────────────────────────────────────────────
            var hint = new Label
            {
                Text                = "Press F5 to return to Edit",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            hint.AddThemeFontSizeOverride("font_size", 15);
            hint.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            vbox.AddChild(hint);

            _gameOverOverlay.Visible = true;
            GD.Print($"[WinCondition] Player {winnerPlayer} wins — {duration} — " +
                     $"P1: {p1Kills}k/{p1Built}u/{p1Ore}ore  " +
                     $"P2: {p2Kills}k/{p2Built}u/{p2Ore}ore. Press F5 to return to Edit.");
        }

        // ── Replay status label ───────────────────────────────────────────────

        /// <summary>
        /// Add a small "◉ REC" / "▶ REPLAY" label anchored top-right, below the HUD.
        /// Visible only when recording or replaying.
        /// </summary>
        private void SetupReplayStatusLabel()
        {
            var label = new Label
            {
                Text                = "",
                Visible             = false,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            label.AddThemeColorOverride("font_color", new Color(1f, 0.25f, 0.25f));
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            label.OffsetRight  = -10f;
            label.OffsetTop    = 10f;
            label.OffsetLeft   = -200f;
            label.OffsetBottom = 40f;
            _uiCanvas.AddChild(label);
            _replayStatusLabel = label;
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void SetupAudio()
        {
            _audioMgr = new UI.AudioManager();
            AddChild(_audioMgr);
            // Initialize is deferred to after sim objects are constructed (called at end of _Ready
            // path, but _combatEvents already exists at this point).
            _audioMgr.Initialize(_combatEvents);
        }

        private void SetupSettings()
        {
            // SettingsManager: loads user://settings.json on _Ready, fires OnSettingsChanged.
            _settingsMgr = new UI.SettingsManager();
            AddChild(_settingsMgr);

            // SettingsPanel: layer 15, toggled via Escape key.
            _settingsPanel = new UI.SettingsPanel();
            AddChild(_settingsPanel);
            _settingsPanel.Initialize(_settingsMgr);

            // Apply settings to systems that are already initialised (camera applied later
            // in SetupCamera; audio buses applied in SettingsManager._Ready already).
            _settingsMgr.OnSettingsChanged += ApplySettingsToSystems;
        }

        /// <summary>Push live settings values into scene systems.</summary>
        private void ApplySettingsToSystems(Core.Definitions.SettingsData s)
        {
            // Camera pan/zoom speed — _camCtrl may not yet exist on first call; guard it.
            if (_camCtrl != null)
            {
                _camCtrl.PanSpeedMultiplier  = s.CameraSpeed;
                _camCtrl.ZoomSpeedMultiplier = s.CameraZoomSpeed;
                // Only push EdgeScroll on settings-change events, not on initial load —
                // the user may have toggled E key mid-session. We do set the initial value
                // from settings at camera setup time (SetupCamera → after settings are loaded).
            }

            // Minimap visibility.
            if (_minimap != null) _minimap.Visible = s.ShowMinimap;

            // FPS display via HUD label (show in top-left if ShowFps is enabled;
            // the HUD already shows FPS in the first line — just log the preference for now).
            // Full implementation: toggle the FPS portion of _hudLabel in UpdateHud().
        }

        // ── Multiplayer setup ─────────────────────────────────────────────────

        private void SetupMultiplayer()
        {
            _transport = new ENetTransport();
            _lockstep  = new LockstepManager(_transport, _world);

            // Wire checksum broadcasting through lockstep when online
            _simLoop.OnChecksum = (tick, hash) =>
            {
                GD.Print($"[Checksum] tick={tick} hash=0x{hash:X8}");
                _lockstep.SendChecksum(tick, hash);
            };

            _lockstep.OnDesync += (tick, local, remote) =>
                GD.PrintErr($"[DESYNC] tick={tick} local=0x{local:X8} remote=0x{remote:X8}");

            _lobbyUi = new LobbyUi
            {
                NakamaHost    = NakamaHost,
                NakamaPort    = NakamaPort,
                NakamaKey     = NakamaKey,
                GameServerIp  = GameServerIp,
                GameServerPort = GameServerPort
            };
            AddChild(_lobbyUi);
            _lobbyUi.Initialize(_transport);
            _lobbyUi.OnMatchStart += OnMatchStart;

            _chatOverlay = new UI.MatchChatOverlay();
            AddChild(_chatOverlay);
            // Chat is inactive until a match starts (Visible=false by default).
        }

        // ── Content Browser ───────────────────────────────────────────────────

        private void SetupContentBrowser()
        {
            // Create mod.io service if credentials are configured in the Inspector.
            ModIoService? modIo = null;
            if (ModIoGameId > 0 && !string.IsNullOrWhiteSpace(ModIoApiKey))
            {
                modIo = new ModIoService(ModIoGameId, ModIoApiKey);
                GD.Print($"[ContentBrowser] mod.io service created (game ID {ModIoGameId}).");
            }

            _contentBrowser = new UI.ContentBrowserPanel();
            AddChild(_contentBrowser);
            _contentBrowser.Initialize("user://packages/", modIo);
            _contentBrowser.OnLoadMap += HandleLoadMap;

            GD.Print("[ContentBrowser] Initialized — press O in Edit mode to open. " +
                     "Drop .chimera.zip files into: " +
                     ProjectSettings.GlobalizePath("user://packages/"));
        }

        // ── Main Menu ─────────────────────────────────────────────────────────

        private void SetupMainMenu()
        {
            _mainMenu = new UI.MainMenuOverlay();
            AddChild(_mainMenu);
            _mainMenu.Initialize(version: "0.1-alpha");

            _mainMenu.OnPlaySkirmish += () =>
            {
                // Enter Play mode immediately with whatever scenario is loaded.
                if (_gameState.Mode != GameMode.Play)
                    _gameState.Toggle();
            };

            _mainMenu.OnCreate += () =>
            {
                // Ensure we're in Edit mode.
                if (_gameState.Mode != GameMode.Edit)
                    _gameState.Toggle();
            };

            _mainMenu.OnBrowse += () =>
            {
                // Ensure Edit mode so the browser opens correctly.
                if (_gameState.Mode != GameMode.Edit)
                    _gameState.Toggle();
                _contentBrowser.ToggleVisible();
            };

            _mainMenu.OnGenerateMap += () =>
            {
                // Switch to Edit mode and open the map generator panel.
                if (_gameState.Mode != GameMode.Edit)
                    _gameState.Toggle();
                _mapGenPanel.Toggle();
            };

            _mainMenu.OnSettings += () => _settingsPanel.ToggleVisible();

            _mainMenu.OnQuit += () => GetTree().Quit();

            GD.Print("[MainMenu] Initialized — showing title screen.");
        }

        // ── Trigger Editor ────────────────────────────────────────────────────

        private void SetupTriggerEditor()
        {
            _llmService = new AI.LLMService { AnthropicApiKey = AnthropicApiKey };

            _triggerPanel = new CreationSuite.TriggerEditorPanel();
            AddChild(_triggerPanel);

            // Build unit ID list from all loaded faction defs.
            var unitIds = new System.Collections.Generic.HashSet<string>();
            foreach (var def in _slotFactionDefs)
                if (def?.Units != null)
                    foreach (var u in def.Units) unitIds.Add(u.Id);

            var context = new AI.ScenarioContext
            {
                UnitIds   = new string[unitIds.Count],
                MapBounds = _scenario?.MapBounds ?? 120f
            };
            unitIds.CopyTo(context.UnitIds);

            _triggerPanel.Initialize(_scenario, _gameState, _llmService, context);

            // Wire ScenarioDirector presentation-layer delegates.
            _scenarioDirector.OnSpawnUnit = (unitId, slot, x, z, count) =>
            {
                var faction    = (Faction)(slot + 1);
                int fIdx       = (int)faction;
                var factionDef = (fIdx >= 0 && fIdx < _slotFactionDefs.Length)
                    ? _slotFactionDefs[fIdx] : _factionDef;
                var def = factionDef?.GetUnit(unitId);
                if (def == null)
                {
                    GD.PrintErr($"[ScenarioDirector] spawn_unit: unknown unit_id '{unitId}' for slot {slot}.");
                    return;
                }
                for (int i = 0; i < count; i++)
                    SpawnScenarioUnit(def, faction, x + i * 2.5f, z);
            };

            _scenarioDirector.OnDisplayMessage = ShowTriggerMessage;

            _scenarioDirector.OnPlaySound = _ => _audioMgr?.PlayBuildingPlaced();

            _scenarioDirector.OnVictory = winnerSlot => ShowGameOver(winnerSlot + 1);

            // Build the toast label used by OnDisplayMessage.
            _toastLabel = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode        = TextServer.AutowrapMode.Word,
                Visible             = false,
                ZIndex              = 10
            };
            _toastLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            _toastLabel.OffsetTop  = 140f;
            _toastLabel.OffsetLeft = -300f;
            _toastLabel.OffsetRight = 300f;
            _uiCanvas.AddChild(_toastLabel);

            GD.Print("[TriggerEditor] Initialized — press L in Edit mode to open. " +
                     "Anthropic API key " +
                     (string.IsNullOrEmpty(AnthropicApiKey) ? "not set (Ollama fallback)." : "configured."));
        }

        // ── Map Generator ─────────────────────────────────────────────────────

        private void SetupMapGenerator()
        {
            _mapGenPanel = new CreationSuite.MapGeneratorPanel();
            AddChild(_mapGenPanel);

            // Build unit ID list — same pass used by SetupTriggerEditor.
            var unitIds = new System.Collections.Generic.HashSet<string>();
            foreach (var def in _slotFactionDefs)
                if (def?.Units != null)
                    foreach (var u in def.Units) unitIds.Add(u.Id);

            var context = new AI.MapGeneratorContext
            {
                UnitIds          = new string[unitIds.Count],
                MapBounds        = _scenario?.MapBounds ?? 120f,
                Slot0FactionJson = _scenario?.PlayerSlots?.Length > 0
                    ? _scenario.PlayerSlots[0].FactionJson
                    : "res://resources/data/factions/alpha_faction.json",
                Slot1FactionJson = _scenario?.PlayerSlots?.Length > 1
                    ? _scenario.PlayerSlots[1].FactionJson
                    : "res://resources/data/factions/beta_faction.json",
            };
            unitIds.CopyTo(context.UnitIds);

            _mapGenPanel.Initialize(_gameState, _llmService, context);
            _mapGenPanel.OnLoadRequested += LoadGeneratedScenario;

            GD.Print("[MapGenerator] Initialized — press M in Edit mode to open.");
        }

        /// <summary>
        /// Load an AI-generated scenario into the active session.
        /// Stores the data in a static field so it survives the scene reload,
        /// then reloads the scene — no disk write required.
        /// </summary>
        public void LoadGeneratedScenario(ScenarioData scenario)
        {
            _pendingGeneratedScenario = scenario;
            GD.Print($"[MapGenerator] Loading \"{scenario.DisplayName}\" — reloading scene.");
            GetTree().ReloadCurrentScene();
        }

        /// <summary>Show a brief HUD notification from a trigger display_message action.</summary>
        private void ShowTriggerMessage(string text, float duration)
        {
            if (_toastLabel == null) return;
            _toastLabel.Text    = text;
            _toastLabel.Visible = true;
            _toastTimer = duration;
        }

        /// <summary>
        /// Called when the user clicks Load on a map package in the content browser.
        /// Extracts the .chimera.zip to user://imported_maps/, copies the scenario to
        /// the scenarios folder, then reloads the current scene so the new map loads.
        /// </summary>
        private void HandleLoadMap(string zipPath)
        {
            var manifest = Definitions.ContentPackager.ReadManifest(zipPath);
            if (manifest == null)
            {
                GD.PrintErr($"[ContentBrowser] Cannot read manifest from '{zipPath}'.");
                return;
            }

            // Extract to user://imported_maps/<slug>/
            string extractDir = ProjectSettings.GlobalizePath($"user://imported_maps/{manifest.Id}/");
            try
            {
                var result = Definitions.ContentPackager.Unpack(zipPath, extractDir);

                // Copy scenario into the project's scenarios resource directory.
                string destScenario = ProjectSettings.GlobalizePath(
                    $"res://resources/data/scenarios/{manifest.Id}.json");
                System.IO.File.Copy(result.ScenarioPath, destScenario, overwrite: true);

                // Copy any bundled faction files.
                foreach (var fp in result.FactionPaths)
                {
                    string destFaction = ProjectSettings.GlobalizePath(
                        $"res://resources/data/factions/{System.IO.Path.GetFileName(fp)}");
                    System.IO.File.Copy(fp, destFaction, overwrite: true);
                }

                // Update the Inspector property so the next scene reload picks this map.
                ScenarioPath = $"res://resources/data/scenarios/{manifest.Id}.json";

                GD.Print($"[ContentBrowser] Loaded '{manifest.DisplayName}' → {ScenarioPath}. Reloading scene...");

                // Reload the whole scene to reset all simulation state cleanly.
                GetTree().ReloadCurrentScene();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ContentBrowser] Failed to load '{manifest.DisplayName}': {ex.Message}");

                // Re-open the browser so user can try again.
                _contentBrowser.ToggleVisible();
            }
        }

        private void OnMatchStart(bool isHost, Faction localFaction)
        {
            // Wire path-request delegates to FlowFieldBridge — deterministic, no NavServer.
            _lockstep.OnRequestPath        = (id, x, z) => _flowFieldBridge.RequestPath(id, new Godot.Vector3(x, 0f, z));
            _lockstep.OnRequestAttackMove  = (id, x, z) => _flowFieldBridge.RequestAttackMove(id, new Godot.Vector3(x, 0f, z));
            _lockstep.OnCancelPath         = id => _flowFieldBridge.CancelPath(id);

            // Route SelectionSystem commands through lockstep
            _selection.SetLockstep(_lockstep);

            if (localFaction == Faction.Neutral)
            {
                // ── Spectator mode ────────────────────────────────────────────
                // Server assigned Neutral → we're an observer. Reveal the full map,
                // start observing both command streams, and show the SPECTATING label.
                _fogBridge.RevealAll = true;
                _lockstep.GoSpectate();

                if (_replayStatusLabel != null)
                {
                    _replayStatusLabel.Text    = "SPECTATING";
                    _replayStatusLabel.Visible = true;
                }

                GD.Print("[MainScene] Match started as SPECTATOR.");
            }
            else
            {
                // ── Player mode ───────────────────────────────────────────────
                // Auto-start replay recording before entering lockstep.
                StartRecording();
                _lockstep.GoOnline(localFaction);

                GD.Print($"[MainScene] Match started as {localFaction}.");
            }

            // Enable chat overlay for this match (spectators can read but not send).
            _chatOverlay.Visible = true;
            _chatOverlay.Initialize(_lockstep, localFaction);
            _chatOverlay.AddSystemMessage("Match started. Press Enter to chat.");

            // Switch to Play mode
            if (_gameState.Mode != GameMode.Play)
                _gameState.SetMode(GameMode.Play);
        }

        /// <summary>
        /// Begin writing a replay file to the user data replays folder.
        /// Safe to call even if a recorder is already active (closes the old one first).
        /// </summary>
        private void StartRecording()
        {
            _replayRecorder?.Close();
            _replayRecorder = null;

            try
            {
                string replayDir = ProjectSettings.GlobalizePath("user://replays/");
                if (!System.IO.Directory.Exists(replayDir))
                    System.IO.Directory.CreateDirectory(replayDir);

                // Timestamp-based filename so each match gets a unique file.
                string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filePath  = System.IO.Path.Combine(replayDir, $"{timestamp}_1v1.chmr");

                // Match seed: a fixed default for now (the real MP seed handshake is Epic 9). The
                // EntityWorld's RNG already starts at this value; record it so a replay restores the
                // identical stream origin (D6 — lockstep regenerates the stream from the seed alone).
                _replayRecorder = new ReplayRecorder(filePath, ScenarioPath, EntityWorld.DEFAULT_RNG_SEED);
                _lockstep.Recorder = _replayRecorder;

                if (_replayStatusLabel != null)
                {
                    _replayStatusLabel.Text    = "◉ REC";
                    _replayStatusLabel.Visible = true;
                }

                GD.Print($"[Replay] Recording → {filePath}");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Replay] Failed to start recording: {e.Message}");
            }
        }

        /// <summary>
        /// Stop and finalise the active recorder, freeing the file handle.
        /// </summary>
        private void StopRecording()
        {
            if (_replayRecorder == null) return;

            _lockstep.Recorder = null;
            _replayRecorder.Close();
            GD.Print($"[Replay] Saved → {_replayRecorder.FilePath}");
            _replayRecorder = null;

            if (_replayStatusLabel != null) _replayStatusLabel.Visible = false;
        }

        /// <summary>
        /// Load a replay file and enter Play mode for playback.
        /// </summary>
        private void TryLoadReplay(string filePath)
        {
            try
            {
                // ReplayPlayer reads the match seed from the header and reseeds _world.Rng before the
                // first tick (v1 files → default seed), so the replayed RNG stream matches the recording.
                _replayPlayer = new ReplayPlayer(filePath, _world);
                _replayPlayer.OnRequestPath       = (id, x, z) => _flowFieldBridge.RequestPath(id, new Godot.Vector3(x, 0f, z));
                _replayPlayer.OnRequestAttackMove = (id, x, z) => _flowFieldBridge.RequestAttackMove(id, new Godot.Vector3(x, 0f, z));
                _replayPlayer.OnCancelPath        = id => _flowFieldBridge.CancelPath(id);

                // The replay embeds the scenario path — override if it differs from the
                // currently-loaded scenario so the map matches.
                if (_replayPlayer.ScenarioPath != ScenarioPath)
                    GD.PrintErr($"[Replay] Scenario mismatch: replay was recorded on " +
                                $"'{_replayPlayer.ScenarioPath}' but loaded '{ScenarioPath}'. " +
                                $"Set ScenarioPath in the Inspector to match for accurate playback.");

                if (_replayStatusLabel != null)
                {
                    _replayStatusLabel.Text    = "▶ REPLAY";
                    _replayStatusLabel.Visible = true;
                }

                // Enter Play mode so the sim runs.
                if (_gameState.Mode != GameMode.Play)
                    _gameState.SetMode(GameMode.Play);

                GD.Print($"[Replay] Playing back '{filePath}' ({_replayPlayer.TotalTicks} command events).");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Replay] Failed to load '{filePath}': {e.Message}");
            }
        }

        // ── Win Condition Check ───────────────────────────────────────────────

        /// <summary>
        /// Evaluate the active win condition. Called every frame during Play mode
        /// after the initial grace period. Sets <see cref="_gameOver"/> and shows
        /// the overlay on the first faction to satisfy the losing criterion.
        /// </summary>
        private void CheckWinCondition()
        {
            if (_scenario == null) return;

            switch (_scenario.WinCondition)
            {
                case WinCondition.DestroyAllBuildings:
                {
                    bool p1Alive = false, p2Alive = false;
                    for (int i = 0; i < _buildings.Count; i++)
                    {
                        if (!_buildings.Alive[i]) continue;
                        if (_buildings.FactionOf[i] == Faction.Player1) p1Alive = true;
                        else if (_buildings.FactionOf[i] == Faction.Player2) p2Alive = true;
                        if (p1Alive && p2Alive) return; // both still standing — exit early
                    }
                    if (!p1Alive) ShowGameOver(2);
                    else if (!p2Alive) ShowGameOver(1);
                    break;
                }

                case WinCondition.EliminateAllUnits:
                {
                    bool p1Alive = false, p2Alive = false;
                    int cap = _world.HighWaterMark;
                    for (int i = 0; i < cap; i++)
                    {
                        if (!_world.IsAlive(i)) continue;
                        if (_world.FactionOf[i] == Faction.Player1) p1Alive = true;
                        else if (_world.FactionOf[i] == Faction.Player2) p2Alive = true;
                        if (p1Alive && p2Alive) return;
                    }
                    if (!p1Alive) ShowGameOver(2);
                    else if (!p2Alive) ShowGameOver(1);
                    break;
                }
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Parse "--port N" from Godot command-line args (after the "--" separator).
        /// Returns <paramref name="defaultPort"/> if the arg is absent or malformed.
        /// Example: ./game.x86_64 --headless -- --port 7778
        /// </summary>
        private static int ParsePortArg(int defaultPort)
        {
            var args = OS.GetCmdlineUserArgs(); // args after "--"
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out int p))
                    return p;
            }
            return defaultPort;
        }

    }
}
