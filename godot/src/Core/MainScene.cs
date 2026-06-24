#nullable enable
using Godot;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core.Bootstrap;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
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

        private SimulationHost    _host        = null!;   // Story 1.8a: the Godot-free sim composition root
        private readonly ILogSink _logSink     = new GodotLogSink(); // presentation log seam injected into _host
        private ScenarioApplier   _applier     = null!;   // Story 1.8b: the sole Godot-free writer of sim truth
        private SceneContext      _ctx         = null!;   // Story 1.8c: presentation composition-root context (shared phase handles)
        private EntityWorld       _world       = null!;
        private ResourceNodeStore _nodes       = null!;
        private ResourceStore     _resources   = null!;
        private BuildingStore     _buildings   = null!;
        private FogOfWarSystem    _fog         = null!;
        private FactionDefinition _factionDef  = null!;  // default P1 (alpha)
        private FactionDefinition _factionDef2 = null!;  // default P2 (beta)
        // Active per-slot definitions — resolved by the presentation pre-pass (ResolveSlotFactionDefs) from
        // slot.FactionJson and shared IN PLACE with ScenarioApplier (Story 1.8b). Elements are null until resolved.
        private FactionDefinition?[] _slotFactionDefs = null!;
        private Combat.ProjectileStore  _projectiles = null!;
        private Combat.CombatEventQueue _combatEvents = null!;
        private Combat.DamageTable      _damageTable = null!;

        // ── Presentation ──────────────────────────────────────────────────────

        // Camera handles (Cam / Placer / Selection) moved to SceneContext (Story 1.8c CameraPhase).
        // GameState handle moved to SceneContext.GameState (Story 1.8c GameStatePhase).
        // Navigation handles (PathSystem / FlowFieldSys / FlowFieldBridge) moved to SceneContext (Story 1.8c NavigationPhase).
        private BuildingSystem      _buildSys         = null!;
        // CommandCard handle moved to SceneContext.CommandCard (Story 1.8c CameraPhase).
        // NavRegion / NavObstacles moved to SceneContext (Story 1.8c NavigationPhase).
        // StartPosBridge moved to SceneContext.StartPosBridge (Story 1.8c ScenarioLoadPhase).
        // FogBridge moved to SceneContext.FogBridge (Story 1.8c RenderingPhase).

        // Terrain handle moved to SceneContext.Terrain (Story 1.8c TerrainPhase).

        // Live scenario (Scenario), the Story-1.7 validation gate (validator + fail-closed toggle), the fallback
        // mirror (FallbackMirror), and the applied-flag (ScenarioApplied) moved to ScenarioLoadPhase + SceneContext
        // (Story 1.8c). The _Ready scenario-hash tail reads ctx.Scenario / ctx.FallbackMirror / ctx.ScenarioApplied.

        // ── HUD ───────────────────────────────────────────────────────────────

        // HUD handles moved to SceneContext (Story 1.8c HudPhase): UiCanvas / HudLabel / ResourceLabel /
        // ControlsLabel / StallBanner. UpdateHud and the UI phases read them back via _ctx.
        // Minimap handle moved to SceneContext.Minimap (Story 1.8c MinimapPhase).

        // ── Multiplayer ───────────────────────────────────────────────────────

        // Transport / Lockstep / LobbyUi / ChatOverlay / ContentBrowser / MainMenu handles moved to SceneContext
        // (Story 1.8c MatchLifecycle / ContentBrowser / MainMenu phases).
        // SettingsManager / SettingsPanel handles moved to SceneContext (Story 1.8c SettingsPhase).
        // AudioManager handle moved to SceneContext.AudioMgr (Story 1.8c AudioPhase).

        // ── Replay system ─────────────────────────────────────────────────────

        // Replay recorder/player + the REC/REPLAY status label moved to SceneContext (Story 1.8c MatchLifecycle /
        // ReplayStatus phases). _Process reads ctx.ReplayPlayer for the replay flush path.

        // ── Worker build placement ────────────────────────────────────────────

        /// <summary>Worker ID waiting to receive a placement click, or -1 when not in placement mode.</summary>
        private int _pendingBuildWorkerId = -1;
        private BuildingType _pendingBuildType;
        /// <summary>Semi-transparent ghost mesh shown while the player is picking a placement spot.</summary>
        private MeshInstance3D? _buildGhost;

        // ── Win condition ─────────────────────────────────────────────────────

        // WinConditionPanel / GameOverOverlay moved to SceneContext (Story 1.8c WinCondition / GameOver phases).
        private bool           _gameOver          = false;
        private int            _playFrames        = 0;

        // ── Trigger system ────────────────────────────────────────────────────

        // ScenarioDirector handle moved to SceneContext.ScenarioDirector (Story 1.8c; binder uses ctx).
        // TriggerPanel / MapGenPanel / LlmService / ToastLabel moved to SceneContext (Story 1.8c TriggerEditor / MapGenerator phases).
        private float                                 _toastTimer;

        // Pending AI-generated scenario moved to ScenarioLoadPhase.PendingGeneratedScenario (Story 1.8c).

        // ── Match stats ───────────────────────────────────────────────────────

        private MatchStats _matchStats    = null!;  // alias of _host.MatchStats (assigned in _Ready, Story 1.8a)
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

                // Story 1.9a / AR-38: build the SAME Godot-free sim spine the client uses (SimulationHost +
                // ScenarioValidator + ScenarioApplier) via ServerBootstrap, so the server holds VALIDATED
                // start-state (server start-state checksum == client offline start-state). All res:// resolution
                // happens HERE on the Godot edge; ServerBootstrap stays Godot-free. The server does NOT tick this
                // host in 1.9a — it is the arbiter that quorums over peer-reported checksums (the live re-sim vote
                // is Epic 9). A null host (missing/invalid scenario) ⇒ relay + quorum only.
                SimulationHost? serverSimHost = BuildHeadlessServerSimHost();

                var server = new ProjectChimera.Multiplayer.DedicatedServer { SimHost = serverSimHost };
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

            // Default slot assignments — overwritten per-slot by the ResolveSlotFactionDefs pre-pass
            _slotFactionDefs = new FactionDefinition?[5];
            _slotFactionDefs[(int)Faction.Player1] = _factionDef;
            _slotFactionDefs[(int)Faction.Player2] = _factionDef2;

            // Damage multipliers (AR-26): load the creator-editable table. A malformed file fails closed
            // with a located error (DamageTable.FromJson); a MISSING file falls back to the canonical
            // Default (matching the FactionDefinition graceful pattern above).
            string damageTableAbs = ProjectSettings.GlobalizePath(DAMAGE_TABLE_JSON);
            _damageTable = System.IO.File.Exists(damageTableAbs)
                ? Combat.DamageTable.Load(damageTableAbs)
                : Combat.DamageTable.Default;

            // ── Sim spine (Story 1.8a / AR-6): SimulationHost is the single Godot-free owner of the SoA
            //    stores, the canonical 9-system tick order (ModifierSystem reserved at index 3), the
            //    SimulationLoop, and the single checksum sink. MainScene injects the presentation GodotLogSink
            //    plus the loaded inputs; sim truth now lives on the host (the fields below are aliases of it).
            //    TODO(5.1): derive the active player count from the loaded scenario's assigned slots; 2-player
            //    today, so new FactionRegistry(2) is behaviour-preserving (Ore[P1]+Ore[P2], byte-identical).
            _host = SimulationHost.Create(
                _logSink,
                new FactionRegistry(2),
                _factionDef,
                _factionDef2,
                _damageTable,
                AiLevel);

            _world            = _host.World;
            _nodes            = _host.Nodes;
            _resources        = _host.Resources;
            _buildings        = _host.Buildings;
            _projectiles      = _host.Projectiles;
            _combatEvents     = _host.CombatEvents;
            _matchStats       = _host.MatchStats;
            _fog              = _host.Fog;
            _buildSys         = _host.BuildSys;
            // (ScenarioDirector alias dropped — ctx.ScenarioDirector is set from _host.ScenarioDirector above; the
            //  ScenarioDelegateBinder/TriggerEditorPhase use ctx.ScenarioDirector.)

            // Story 1.8b: the sole Godot-free writer of sim truth. It shares the _slotFactionDefs array (the
            // presentation pre-pass fills it in place before each apply) and the presentation log seam.
            _applier = new ScenarioApplier(_host, _logSink, _slotFactionDefs);

            // Story 1.8c: build the presentation composition-root context. Sim-spine handles are populated now
            // (host/applier + the store aliases); each ISetupPhase fills in its presentation products as it runs,
            // and MainScene's runtime methods (_Process/_Input/UpdateHud/…) read shared handles back off _ctx.
            _ctx = new SceneContext(this)
            {
                Host = _host, Applier = _applier, Log = _logSink,
                World = _world, Nodes = _nodes, Resources = _resources, Buildings = _buildings,
                Fog = _fog, Projectiles = _projectiles, CombatEvents = _combatEvents, DamageTable = _damageTable,
                MatchStats = _matchStats, BuildSys = _buildSys, ScenarioDirector = _host.ScenarioDirector,
                FactionDef = _factionDef, FactionDef2 = _factionDef2, SlotFactionDefs = _slotFactionDefs,
            };

            // Single checksum sink (D5): ONE owner. Offline → log; online → also forward to lockstep
            // (replaces the former double-set: this inline log sink + the SetupMultiplayer overwrite). The
            // lambda reads _ctx.Lockstep.IsOnline at tick time, so it is correct once SetupMultiplayer has run.
            _host.SetChecksumSink((tick, checksum) =>
            {
                _logSink.Info($"[Checksum] tick={tick} hash=0x{checksum:X8}");
                if (_ctx.Lockstep.IsOnline) _ctx.Lockstep.SendChecksum(tick, checksum);
            });

            // ── Composition root (Story 1.8c / AR-3) ──────────────────────────────
            // The ordered Setup* sequence is now an asserted ISetupPhase[] literal. ScenePhaseRunner.Run()
            // re-asserts the live order matches ScenePhaseOrder.Canonical at startup (throws on any
            // reorder/add/remove — it never silently reorders, constraint C1), and the Tier-1 PhaseOrderTest
            // pins that same canonical order. Every phase is a concrete *Phase class under src/Core/Bootstrap/Phases/
            // owning its own setup body + products (carried on the SceneContext) — MainScene is now presentation/
            // wiring only. To change the order, edit ScenePhaseOrder.Canonical AND PhaseOrderTest — never reorder
            // this literal alone.
            var phases = new ISetupPhase[]
            {
                new SettingsPhase(_ctx),
                new AudioPhase(_ctx),
                new GameStatePhase(_ctx),
                new LightingPhase(_ctx),
                new TerrainPhase(_ctx),
                new NavigationPhase(_ctx),
                new CameraPhase(_ctx),
                new RenderingPhase(_ctx),
                new HudPhase(_ctx),
                new MinimapPhase(_ctx),
                new TerrainBrushPhase(_ctx),
                new ScenarioLoadPhase(_ctx),
                new FactionVisualsPhase(_ctx),
                new FlowFieldInitPhase(_ctx),
                new WinConditionPhase(_ctx),
                new GameOverOverlayPhase(_ctx),
                new MatchLifecycleController(_ctx),
                new ReplayStatusPhase(_ctx),
                new ContentBrowserPhase(_ctx),
                new MainMenuPhase(_ctx),
                new TriggerEditorPhase(_ctx),
                new MapGeneratorPhase(_ctx),
            };
            new ScenePhaseRunner(phases).Run();

            // Compute scenario hash now that both scenario and lobby are ready.
            // Sent with the Ready packet so peers can detect map mismatches before starting.
            // Story 1.7 (AR-23): canonical-model hash over the in-memory APPLIED scenario (not file bytes) —
            // stable across whitespace / JSON key order / 1.0-vs-1 / file path, fixing the AI-gen stale-file
            // desync. Folded to the existing 32-bit Ready-packet wire (widening is Epic 9). _ctx.Scenario holds the
            // applied model for the file / AI / editor paths; _ctx.FallbackMirror holds it for the hardcoded fallback.
            // Story 1.7 review patch: only publish a hash for a model that was actually applied. In fail-closed
            // mode a rejected scenario leaves _ctx.ScenarioApplied false (nothing reached the sim), so we publish 0 —
            // the handshake treats 0 as fail-open/skip rather than advertising a start-state we never built.
            ScenarioData? hashModel = _ctx.Scenario ?? _ctx.FallbackMirror;
            _ctx.LobbyUi.ScenarioHash = (_ctx.ScenarioApplied && hashModel != null)
                ? Definitions.CanonicalModelHash.ToWire(Definitions.CanonicalModelHash.Compute(hashModel))
                : 0u;
            GD.Print($"[MainScene] Scenario hash: 0x{_ctx.LobbyUi.ScenarioHash:X8}");

            // If a replay file is specified via the Inspector, load it now and
            // enter Play mode immediately — no lobby, no network required.
            if (!string.IsNullOrEmpty(ReplayPath))
                _ctx.MatchLifecycle.TryLoadReplay(ReplayPath);

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
                _ctx.SettingsPanel.ToggleVisible();
                GetViewport().SetInputAsHandled();
                return;
            }

#if DEBUG
            // Story 1.9a (Task 10 loopback smoke, DEBUG-only): F9 perturbs THIS peer's sim so its next
            // SimChecksum diverges — letting a single-machine loopback induce a one-peer desync. The server's
            // collector then sees no strict majority (N=2) and broadcasts a terminal HALT; both clients show the
            // terminal HALT overlay (distinct from the stall banner). Mirrors the golden AC3 +1-raw nudge.
            if (key.Keycode == Key.F9 && _ctx.Lockstep.IsOnline)
            {
                for (int id = 0; id < _world.HighWaterMark; id++)
                {
                    if (!_world.IsAlive(id)) continue;
                    _world.Health[id] = Fixed.FromRaw(_world.Health[id].Raw + 1);
                    GD.PrintErr($"[DEBUG] Induced desync: nudged entity {id} health (+1 raw) — local checksum will diverge.");
                    break;
                }
                GetViewport().SetInputAsHandled();
                return;
            }
#endif

            // Edit-mode-only shortcuts.
            if (_ctx.GameState.Mode != GameMode.Edit) return;

            if (key.Keycode == Key.N)
            {
                if (_ctx.LobbyUi.Visible) _ctx.LobbyUi.Close();
                else _ctx.LobbyUi.Show();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.O)
            {
                _ctx.ContentBrowser.ToggleVisible();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.L)
            {
                _ctx.TriggerPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.M)
            {
                _ctx.MapGenPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _Process(double delta)
        {
            if (_ctx.GameState.Mode == GameMode.Play && !_gameOver)
            {
                if (_ctx.ReplayPlayer != null)
                {
                    // Replay mode: feed recorded commands instead of live network/input.
                    // Always advances one tick per frame — no stalling.
                    _ctx.ReplayPlayer.Flush(_host.CurrentTick);
                    _host.StepOnce();

                    if (_ctx.ReplayPlayer.IsFinished)
                    {
                        GD.Print($"[Replay] Finished at tick {_host.CurrentTick}.");
                        _ctx.ReplayPlayer = null;
                        if (_ctx.ReplayStatusLabel != null) _ctx.ReplayStatusLabel.Visible = false;
                    }
                }
                else if (_ctx.Lockstep.IsOnline)
                {
                    // Online: only step the sim when both peers' commands for this tick have arrived.
                    // Flush() sends local commands, polls transport, and returns true when ready.
                    if (_ctx.Lockstep.Flush(_host.CurrentTick))
                        _host.StepOnce();
                }
                else
                {
                    // Offline: free-running fixed-timestep as before.
                    _host.Update((float)delta);
                }

                if (_playFrames == 0)
                    _matchStartMs = Time.GetTicksMsec();
                _playFrames++;
                if (_playFrames > 180) // ~3 s grace period at 60 fps before checking win
                    CheckWinCondition();
            }
            else if (_ctx.GameState.Mode == GameMode.Edit)
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
            _ctx.TriggerPanel.Update();
            _ctx.MapGenPanel.Update();
            if (_toastTimer > 0)
            {
                _toastTimer -= (float)delta;
                if (_toastTimer <= 0 && _ctx.ToastLabel != null)
                    _ctx.ToastLabel.Visible = false;
            }

            UpdateHud();
        }




        /// <summary>
        /// Called by EntityPlacer when the user places a start-position marker in Edit mode.
        /// Updates both the live scenario data and the simulation's faction base point.
        /// </summary>
        internal void MoveStartPosition(int slot, Vector3 worldPos, float startOre)
        {
            // Update scenario data (persisted on save)
            if (_ctx.Scenario != null)
            {
                foreach (var s in _ctx.Scenario.PlayerSlots)
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

            // Update live sim: faction deposit / rally point. Routed through the applier (Story 1.8b D6) — the
            // unified sole writer of FactionBase; after 1.8b no MainScene code writes Resources.FactionBase directly.
            var faction = (Faction)(slot + 1);
            _applier.SetFactionBase(faction, new FixedVec3(
                Fixed.FromFloat(worldPos.X), Fixed.Zero, Fixed.FromFloat(worldPos.Z)));

            // Move the visual marker
            _ctx.StartPosBridge.SetPosition(slot, worldPos);
        }



        // ── HUD ───────────────────────────────────────────────────────────────

        private void UpdateHud()
        {
            bool isEdit = _ctx.GameState.Mode == GameMode.Edit;
            string modeTag = isEdit ? "EDIT" : "PLAY";

            // ── Line 1: performance / sim state ──────────────────────────────
            string checksumStr = _host.LastChecksum == 0 ? "—"
                : $"0x{_host.LastChecksum:X8}";
            string onlineTag = _ctx.Lockstep.IsOnline ? "  ONLINE" : "";

            // ── Line 2: unit counts ───────────────────────────────────────────
            int p1 = CountFaction(Faction.Player1);
            int p2 = CountFaction(Faction.Player2);

            // ── Line 3: selection / placing state ─────────────────────────────
            int selCount  = _ctx.Selection.SelectedIds.Count;
            string groupTag = _ctx.Selection.ActiveGroupIndex >= 0
                ? $" [grp {_ctx.Selection.ActiveGroupIndex + 1}]" : "";
            string selInfo = selCount == 0 ? "—"
                : selCount == 1
                    ? $"id {_ctx.Selection.FocusId} [{_world.FactionOf[_ctx.Selection.FocusId]}]{groupTag}"
                    : $"{selCount} units{groupTag}";

            _ctx.HudLabel.Text =
                $"FPS {Engine.GetFramesPerSecond()}   [{modeTag}]   Tick {_host.CurrentTick}   Hash {checksumStr}{onlineTag}\n" +
                $"P1: {p1} units   P2: {p2} units   Total: {_world.AliveCount}\n" +
                (isEdit ? $"Placing: {_ctx.Placer.ModeLabel}" : $"Selected: {selInfo}");

            // ── Resource label: ore + supply ──────────────────────────────────
            int p1Ore    = (int)_resources.Ore[(int)Faction.Player1].ToFloat();
            int p2Ore    = (int)_resources.Ore[(int)Faction.Player2].ToFloat();
            int p1Sup    = _resources.SupplyUsed[(int)Faction.Player1];
            int p2Sup    = _resources.SupplyUsed[(int)Faction.Player2];
            int p1SupCap = _resources.SupplyCap[(int)Faction.Player1];
            int p2SupCap = _resources.SupplyCap[(int)Faction.Player2];
            int nodes    = CountActiveNodes();
            int bldgs    = CountAliveBuildings();

            _ctx.ResourceLabel.Text =
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
                _ctx.ControlsLabel.Text = $"Placing {bName} — Left-click to place   Right-click / Esc to cancel";
            }
            else if (isEdit)
            {
                string snap = _ctx.Placer.GridSnapEnabled ? "ON" : "OFF";
                string edge = _ctx.Cam.EdgeScrollEnabled ? "ON" : "OFF";
                _ctx.ControlsLabel.Text =
                    $"F5=Play   N=Lobby   O=Maps   Esc=Settings   T=Terrain   G=Snap({snap})   E=Edge({edge})" +
                    $"   Tab=Mode   U=Unit   B=Building   Del=Delete   Ctrl+Z=Undo";
            }
            else
            {
                _ctx.ControlsLabel.Text =
                    "F5=Edit   R-Click=Move   Q+Click=AttackMove   S=Stop   H=Hold   1-9=Groups   Esc=Deselect";
            }

            // ── Stall banner ──────────────────────────────────────────────────
            _ctx.StallBanner.Visible = _ctx.Lockstep.IsOnline && _ctx.Lockstep.IsStalling;
        }

        // ── Worker build placement ────────────────────────────────────────────

        /// <summary>
        /// Called when the player clicks a build button on a worker's command card.
        /// Enters placement mode: a ghost mesh tracks the cursor until the player
        /// left-clicks a position (confirming the build) or cancels with Esc/right-click.
        /// </summary>
        internal void EnterBuildPlacementMode(int workerId, BuildingType bType)
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
            var camera = _ctx.Cam?.GetCamera();
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

        /// <summary>
        /// Build the full-screen game-over overlay. Hidden until ShowGameOver() is called.
        /// The overlay is populated with live match data at show-time, not at setup-time.
        // ── Map I/O: Export / Import ──────────────────────────────────────────────


        /// <summary>
        /// Build the full-screen game-over overlay. Hidden until ShowGameOver() is called.
        /// The overlay is populated with live match data at show-time, not at setup-time.
        /// </summary>

        /// <summary>Populate and display the victory/defeat panel with live match data.</summary>
        internal void ShowGameOver(int winnerPlayer)
        {
            _gameOver = true;

            // Notify chat before closing it.
            _ctx.ChatOverlay.AddSystemMessage($"Player {winnerPlayer} wins! GG");

            // Finalise replay recording — match is over.
            _ctx.MatchLifecycle.StopRecording();

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
            foreach (Node child in _ctx.GameOverOverlay.GetChildren())
            {
                _ctx.GameOverOverlay.RemoveChild(child);
                child.QueueFree();
            }

            // ── Card ─────────────────────────────────────────────────────────
            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            card.CustomMinimumSize = new Vector2(560, 380);
            _ctx.GameOverOverlay.AddChild(card);

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

            _ctx.GameOverOverlay.Visible = true;
            GD.Print($"[WinCondition] Player {winnerPlayer} wins — {duration} — " +
                     $"P1: {p1Kills}k/{p1Built}u/{p1Ore}ore  " +
                     $"P2: {p2Kills}k/{p2Built}u/{p2Ore}ore. Press F5 to return to Edit.");
        }

        /// <summary>
        /// Story 1.9a (UX-DR64e / D10): show the TERMINAL desync-halt overlay. Reuses the GameOverOverlay root but
        /// is danger-styled and behaviorally terminal — the match has already stopped advancing (LockstepManager
        /// gates Flush on its _halted flag). DISTINCT from the recoverable stall banner (UX-DR28, a transient warn
        /// pill): this ends the match and offers only "Return to Menu". Voiced to the "Commander" (UX-DR65).
        /// Exact copy is the story's recommended default (Open Question #1).
        /// </summary>
        internal void ShowHalt(uint tick, uint canonicalHash)
        {
            if (_gameOver) return;   // a terminal state (win/lose or a prior halt) is already shown
            _gameOver = true;        // stop win-condition / play-mode processing

            _ctx.ChatOverlay?.AddSystemMessage("Simulation desync — match halted.");
            _ctx.MatchLifecycle.StopRecording();

            // Clear any previous overlay children (defensive — e.g. a stale game-over card).
            foreach (Node child in _ctx.GameOverOverlay.GetChildren())
            {
                _ctx.GameOverOverlay.RemoveChild(child);
                child.QueueFree();
            }

            var card = new PanelContainer();
            card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            card.CustomMinimumSize = new Vector2(560, 300);
            _ctx.GameOverOverlay.AddChild(card);

            var vbox = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            vbox.AddThemeConstantOverride("separation", 14);
            card.AddChild(vbox);

            var heading = new Label { Text = "MATCH HALTED", HorizontalAlignment = HorizontalAlignment.Center };
            heading.AddThemeFontSizeOverride("font_size", 56);
            heading.AddThemeColorOverride("font_color", new Color(0.85f, 0.15f, 0.15f)); // danger, NOT the victory gold
            vbox.AddChild(heading);

            var body = new Label
            {
                Text                = $"Simulation desync detected at tick {tick}. The match cannot continue.",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode        = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize   = new Vector2(480, 0),
            };
            body.AddThemeFontSizeOverride("font_size", 20);
            body.AddThemeColorOverride("font_color", Colors.LightGray);
            vbox.AddChild(body);

            // Mono status string (UX-DR65): show the canonical hash when present (DesyncAlert), else the tick (Halt).
            var status = new Label
            {
                Text                = canonicalHash != 0u ? $"· desync · #{canonicalHash:X8}" : $"· desync · @tick {tick}",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            status.AddThemeFontSizeOverride("font_size", 15);
            status.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            vbox.AddChild(status);

            vbox.AddChild(new HSeparator());

            var menuBtn = new Button { Text = "Return to Menu", CustomMinimumSize = new Vector2(200, 40) };
            menuBtn.Pressed += () =>
            {
                _ctx.GameOverOverlay.Visible = false;
                if (_ctx.MainMenu != null) _ctx.MainMenu.Visible = true;
            };
            vbox.AddChild(menuBtn);

            _ctx.GameOverOverlay.Visible = true;
            GD.PrintErr($"[HALT] Match halted at tick {tick} (canonical 0x{canonicalHash:X8}) — simulation desync. Terminal.");
        }

        // ── Replay status label ───────────────────────────────────────────────

        /// <summary>
        /// Add a small "◉ REC" / "▶ REPLAY" label anchored top-right, below the HUD.
        /// Visible only when recording or replaying.
        /// </summary>

        // ── Settings ──────────────────────────────────────────────────────────

        /// <summary>Push live settings values into scene systems. Bridge retained on MainScene (it touches the
        /// camera/minimap MainScene keeps); re-subscribed by SettingsPhase via ctx.Scene.</summary>
        internal void ApplySettingsToSystems(Core.Definitions.SettingsData s)
        {
            // Camera pan/zoom speed — _ctx.Cam may not yet exist on first call; guard it.
            if (_ctx.Cam != null)
            {
                _ctx.Cam.PanSpeedMultiplier  = s.CameraSpeed;
                _ctx.Cam.ZoomSpeedMultiplier = s.CameraZoomSpeed;
                // Only push EdgeScroll on settings-change events, not on initial load —
                // the user may have toggled E key mid-session. We do set the initial value
                // from settings at camera setup time (SetupCamera → after settings are loaded).
            }

            // Minimap visibility.
            if (_ctx.Minimap != null) _ctx.Minimap.Visible = s.ShowMinimap;

            // FPS display via HUD label (show in top-left if ShowFps is enabled;
            // the HUD already shows FPS in the first line — just log the preference for now).
            // Full implementation: toggle the FPS portion of _ctx.HudLabel in UpdateHud().
        }

        // ── Multiplayer setup ─────────────────────────────────────────────────


        // ── Content Browser ───────────────────────────────────────────────────


        // ── Main Menu ─────────────────────────────────────────────────────────


        // ── Trigger Editor ────────────────────────────────────────────────────


        // ── Map Generator ─────────────────────────────────────────────────────


        /// <summary>
        /// Load an AI-generated scenario into the active session.
        /// Stores the data in a static field so it survives the scene reload,
        /// then reloads the scene — no disk write required.
        /// </summary>
        public void LoadGeneratedScenario(ScenarioData scenario)
        {
            ScenarioLoadPhase.PendingGeneratedScenario = scenario;
            GD.Print($"[MapGenerator] Loading \"{scenario.DisplayName}\" — reloading scene.");
            GetTree().ReloadCurrentScene();
        }

        /// <summary>Show a brief HUD notification from a trigger display_message action.</summary>
        internal void ShowTriggerMessage(string text, float duration)
        {
            if (_ctx.ToastLabel == null) return;
            _ctx.ToastLabel.Text    = text;
            _ctx.ToastLabel.Visible = true;
            _toastTimer = duration;
        }




        /// <summary>
        /// Story 1.8c — the return-to-Edit match reset (formerly inline in SetupWinConditionUi's ModeChanged
        /// handler). Wired by WinConditionPhase via ctx.Scene; kept on MainScene because it touches the match-
        /// lifecycle state MainScene retains (_gameOver / _playFrames / _matchStartMs / _matchStats).
        /// </summary>
        internal void ResetMatchOnReturnToEdit()
        {
            // Dismiss any active game-over overlay
            if (_ctx.GameOverOverlay != null) _ctx.GameOverOverlay.Visible = false;
            _gameOver     = false;
            _playFrames   = 0;
            _matchStartMs = 0;
            _matchStats.Reset();

            // Stop recording and clear replay player.
            _ctx.MatchLifecycle.StopRecording();
            _ctx.ReplayPlayer = null;
            if (_ctx.ReplayStatusLabel != null) _ctx.ReplayStatusLabel.Visible = false;

            // Reset spectator fog reveal.
            if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = false;

            // Close chat overlay and tear down lockstep subscription.
            _ctx.ChatOverlay.Close();
            _ctx.ChatOverlay.Visible = false;

            // Close map browser if open.
            _ctx.ContentBrowser.Visible = false;
        }

        // ── Win Condition Check ───────────────────────────────────────────────

        /// <summary>
        /// Evaluate the active win condition. Called every frame during Play mode
        /// after the initial grace period. Sets <see cref="_gameOver"/> and shows
        /// the overlay on the first faction to satisfy the losing criterion.
        /// </summary>
        private void CheckWinCondition()
        {
            if (_ctx.Scenario == null) return;

            switch (_ctx.Scenario.WinCondition)
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
        /// Story 1.9a (AR-38, D2): resolve all res:// inputs on the Godot edge and build the server's Godot-free
        /// sim spine via <see cref="ServerBootstrap"/>. Mirrors the client _Ready faction/damage-table seeding and
        /// the ScenarioLoadPhase per-slot faction resolution, so the server's validated start-state is
        /// byte-identical to a client's. Returns null if the scenario is missing/parse-fails or fails validation
        /// (fail-closed) — the caller then runs the server as a relay + quorum without a held sim spine.
        /// </summary>
        private SimulationHost? BuildHeadlessServerSimHost()
        {
            // Faction defaults — mirror the client _Ready seeding (P1 alpha, P2 beta/Iron Pact).
            string p1Abs = ProjectSettings.GlobalizePath(P1_FACTION_JSON);
            var p1Def = System.IO.File.Exists(p1Abs) ? FactionDefinition.LoadFromFile(p1Abs) : new FactionDefinition();
            string p2Abs = ProjectSettings.GlobalizePath(P2_FACTION_JSON);
            var p2Def = System.IO.File.Exists(p2Abs) ? FactionDefinition.LoadFromFile(p2Abs) : new FactionDefinition();

            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = p1Def;
            slotDefs[(int)Faction.Player2] = p2Def;

            // Damage table — mirror the client _Ready seeding (missing file → canonical Default).
            string dtAbs = ProjectSettings.GlobalizePath(DAMAGE_TABLE_JSON);
            var damageTable = System.IO.File.Exists(dtAbs) ? Combat.DamageTable.Load(dtAbs) : Combat.DamageTable.Default;

            // Scenario model from the configured path.
            string scnAbs = ProjectSettings.GlobalizePath(ScenarioPath);
            ScenarioData? model = ScenarioSerializer.LoadFromFile(scnAbs);
            if (model == null)
            {
                GD.PrintErr($"[ServerBootstrap] Scenario '{ScenarioPath}' missing/parse-failed — server runs relay + quorum only (no validated sim spine).");
                return null;
            }

            // Per-slot faction resolution — mirror ScenarioLoadPhase.ResolveSlotFactionDefs (the one path-resolution).
            foreach (var slot in model.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                if (string.IsNullOrEmpty(slot.FactionJson)) continue;
                var f = (Faction)(slot.Slot + 1); // slot 0 → Player1, slot 1 → Player2
                if ((int)f < 0 || (int)f >= slotDefs.Length) continue;
                string fAbs = ProjectSettings.GlobalizePath(slot.FactionJson);
                if (System.IO.File.Exists(fAbs)) slotDefs[(int)f] = FactionDefinition.LoadFromFile(fAbs);
            }

            // ServerBootstrap validates (fail-closed: invalid ⇒ null + log via the seam) and applies through the
            // shared spine. activeFactionCount=2 mirrors the client's new FactionRegistry(2) (1v1 today).
            SimulationHost? host = ServerBootstrap.Build(model, slotDefs, damageTable, _logSink, activeFactionCount: 2);
            if (host != null)
                GD.Print("[ServerBootstrap] Validated server sim spine built + applied (AR-38).");
            return host;
        }

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
