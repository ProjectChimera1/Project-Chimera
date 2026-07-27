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
using System.Globalization;
using System.Linq;

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
        /// <summary>Story 2.4b: registry of validated abilities (built from <see cref="ABILITIES_DIR"/>), injected
        /// into the host and published on <c>SceneContext</c> for the command card's label reads. Empty until _Ready builds it.</summary>
        private AbilityRegistry _abilityRegistry = AbilityRegistry.Empty;
        /// <summary>Story 3.6: registry of behaviors (built from <see cref="BEHAVIORS_DIR"/>), published on
        /// <c>SceneContext</c> for the Unit Card Editor's behavior picker + compat validation. Authoring-only (no host
        /// injection, no sim consumer — D-2). Empty until _Ready builds it.</summary>
        private BehaviorRegistry _behaviorRegistry = BehaviorRegistry.Empty;
        /// <summary>Story 3.15: registry of validated items (built from <see cref="ITEMS_DIR"/>), injected into the host
        /// so scenario item placement + the editor Item palette resolve item ids. Empty until _Ready builds it.</summary>
        private ItemRegistry _itemRegistry = ItemRegistry.Empty;
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
        // Story 7.12 — per-player elimination: set once when the LOCAL faction latches VERDICT_LOST while the match is
        // still unresolved. Flips to the RevealAll spectator view + a non-terminal defeat banner; the match keeps
        // ticking until fully resolved (then ShowGameOver fires). Reset by ResetMatchOnReturnToEdit.
        private bool           _localEliminated   = false;
        private Label?         _defeatBanner;

        // ── Trigger system ────────────────────────────────────────────────────

        // ScenarioDirector handle moved to SceneContext.ScenarioDirector (Story 1.8c; binder uses ctx).
        // TriggerPanel / MapGenPanel / LlmService / ToastLabel moved to SceneContext (Story 1.8c TriggerEditor / MapGenerator phases).
        private float                                 _toastTimer;

        // Pending AI-generated scenario moved to ScenarioLoadPhase.PendingGeneratedScenario (Story 1.8c).

        // ── Match stats ───────────────────────────────────────────────────────

        private MatchStats _matchStats    = null!;  // alias of _host.MatchStats (assigned in _Ready, Story 1.8a)
        /// <summary>Time.GetTicksMsec() value when Play mode first started this match.</summary>
        private ulong _matchStartMs = 0;

        // ── Replay playback controls (Story 9.11) — presentation-only; NEVER touch sim state or the checksum ──
        private bool _replayPaused;
        private int  _replaySpeed = 1;   // sim ticks stepped per frame (1/2/4/8)
        private int  _replaySeekTo = -1; // >=0 → fast render-free re-sim to this tick, then clear
        private int  _replayPerspective = -1; // -1 = reveal-all; 0..N-1 = roster[i] fog viewer

        /// <summary>Max sim ticks the seek-forward advances per frame (P4): a bounded batch so a long seek advances
        /// over several frames instead of freezing the window on the main thread. 240 ticks = 8 s of sim/frame.</summary>
        private const int REPLAY_SEEK_TICKS_PER_FRAME = 240;

        /// <summary>Story 9.11 (P1): cross-reload handoff from the replay browser's Play. The browser stashes the
        /// pending replay + its scenario here and reloads the scene; the fresh <c>_Ready</c> consumes them BEFORE the
        /// phase list runs — so <c>ScenarioLoadPhase</c> loads the replay's scenario into a clean tick-0 world and the
        /// re-gate compares against the correctly-loaded scenario — then autoplays the replay. Static so they survive
        /// <c>ReloadCurrentScene</c> (the <c>ScenarioLoadPhase.PendingGeneratedScenario</c> precedent).</summary>
        internal static string? PendingReplayPath;
        internal static string? PendingReplayScenarioPath;

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

        // Story 8.1: the two plaintext [Export] LLM / mod.io key fields were removed — API keys are no longer
        // hardcodable/committable/shippable Inspector strings. Keys now come from the Godot-free ISecretStore
        // (user://secrets/*.key, gitignored); SettingsPhase constructs the store and the TriggerEditor/ContentBrowser
        // phases source their keys from it. ModIoGameId (a non-secret public id) stays.

        // ── Constants ─────────────────────────────────────────────────────────

        // internal so UnitCardPhase (Story 3.4) can thread the default P1 faction path into the Unit Card Editor for write-back.
        internal const string P1_FACTION_JSON = "res://resources/data/factions/alpha_faction.json";
        private const string P2_FACTION_JSON = "res://resources/data/factions/beta_faction.json";
        private const string DAMAGE_TABLE_JSON = "res://resources/data/damage_table.json";
        /// <summary>Story 2.4b: directory of validated ability JSONs, indexed into the AbilityRegistry (client + server).</summary>
        private const string ABILITIES_DIR = "res://resources/data/abilities";
        /// <summary>Story 3.6: directory of behavior JSONs, indexed into the BehaviorRegistry (authoring-only — the Unit
        /// Card Editor reads it for the behavior picker + compat validation; no sim system consumes it).</summary>
        private const string BEHAVIORS_DIR = "res://resources/data/behaviors";
        internal const string ITEMS_DIR    = "res://resources/data/items"; // Story 3.15 (internal: the ItemCard phase reads it)
        /// <summary>Story 5.7 (FR-19): directory scanned by <see cref="FactionDefinition.LoadSelectableFromDirectory"/>
        /// for wizard-authored/showcase factions — the same directory <see cref="P1_FACTION_JSON"/>/<see cref="P2_FACTION_JSON"/>
        /// live in.</summary>
        private const string FACTIONS_DIR  = "res://resources/data/factions";

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>True when running as a headless dedicated server (set in _Ready's headless branch). Gates the
        /// client-only lifecycle callbacks (_Process/_Input/_UnhandledInput) — in headless mode _Ready returns
        /// early before building the presentation context, so those would dereference a null _ctx. (Story 1.9a)</summary>
        private bool _headless;

        /// <summary>Story 7.1: pin InvariantCulture process-wide at the earliest-running entry of the game process
        /// root — a hardening net so number formatting/parsing anywhere (editor, UGC, AI-gen) is locale-independent
        /// and can never diverge across peers. Runs before <see cref="_Ready"/>. The tick path itself is already
        /// culture-free (Fixed-only, no string round-trips); this closes the gap outside it.</summary>
        public override void _EnterTree()
        {
            CultureInfo.DefaultThreadCurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            // DefaultThread* only governs threads that have not already materialized a culture; the main thread
            // (and any autoload that ran first) may have. Pin the running thread explicitly so the net covers it.
            CultureInfo.CurrentCulture   = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public override void _Ready()
        {
#if DEBUG
            // Story 1.9a (DEBUG-only): in-process loopback desync self-test. Verifies the full server→desync→HALT
            // path over real loopback ENet in ONE process. Run: godot --headless -- --loopback-test
            if (HasCmdArg("--loopback-test"))
            {
                _headless = true;
                GD.Print("[MainScene] --loopback-test → running in-process loopback desync self-test.");
                AddChild(new ProjectChimera.Multiplayer.LoopbackDesyncSelfTest());
                return;
            }
#endif

            // ── Dedicated server mode ─────────────────────────────────────────────
            // Headless (no display server) OR an explicit `-- --server` window (loopback smoke). The server holds
            // validated sim state but renders no game; with --server it shows a small "DEDICATED SERVER" window so
            // it is never an invisible, port-holding ghost process (which otherwise persists across smoke runs).
            bool isHeadlessServer = DisplayServer.GetName() == "headless" || OS.HasFeature("dedicated_server");
            bool isWindowedServer = false;
#if DEBUG
            isWindowedServer = HasCmdArg("--server");
#endif
            if (isHeadlessServer || isWindowedServer)
            {
                _headless = true; // gates the client-only _Process/_Input/_UnhandledInput (no _ctx is built below)
                int port = ParsePortArg(ProjectChimera.Multiplayer.DedicatedServer.DEFAULT_PORT);
                GD.Print($"[MainScene] Dedicated server on port {port} (windowed={isWindowedServer && !isHeadlessServer}).");

                // Story 1.9a / AR-38: build the SAME Godot-free sim spine the client uses (SimulationHost +
                // ScenarioValidator + ScenarioApplier) via ServerBootstrap, so the server holds VALIDATED
                // start-state (server start-state checksum == client offline start-state). All res:// resolution
                // happens HERE on the Godot edge; ServerBootstrap stays Godot-free. The server does NOT tick this
                // host in 1.9a — it is the arbiter that quorums over peer-reported checksums (the live re-sim vote
                // is Epic 9). A null host (missing/invalid scenario) ⇒ relay + quorum only.
                SimulationHost? serverSimHost = BuildHeadlessServerSimHost();

                // Story 1.9b review (P1): inject the presentation log sink so the dedicated server actually PRINTS
                // its FR-39 determinism verdict (per-window lines + MATCH SUMMARY) to the server console. Without
                // this, Log defaults to NullLogSink and every AC1 verdict line is silently dropped in production.
                var server = new ProjectChimera.Multiplayer.DedicatedServer
                {
                    SimHost = serverSimHost, Log = _logSink,
                    // Story 9.7: the scenario-derived player count (set by BuildHeadlessServerSimHost) — the count the
                    // match must collect before StartGame, matching the client FactionRegistry(N).
                    ExpectedPlayerCount = _serverExpectedPlayers,
                };
                AddChild(server);
                server.Start(port);

#if DEBUG
                if (isWindowedServer && !isHeadlessServer) ShowServerWindowMarker(port);
#endif
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

            // Story 2.4b: build the ability registry from resources/data/abilities/ and resolve each loaded unit's
            // ability ids → registry indices BEFORE the host (and any spawn). LoadFromDirectory takes an ABSOLUTE OS
            // path (Directory.GetFiles) so res:// is resolved via GlobalizePath — the same pattern the faction load
            // above uses. Resolving the SHARED UnitDefinition objects once per faction back-fills def.AbilityIndices,
            // which ApplyUnitDefinition reads on every spawn (trained via BuildingSystem / editor via EntityPlacer);
            // the per-slot scenario defs are resolved separately in ScenarioLoadPhase (a distinct loaded instance).
            string abilitiesAbs = ProjectSettings.GlobalizePath(ABILITIES_DIR);
            _abilityRegistry = AbilityRegistry.LoadFromDirectory(
                abilitiesAbs, name => GD.Print($"[Abilities] skipped invalid {name}"));
            foreach (var u in _factionDef.Units)  u.ResolveAbilities(_abilityRegistry);
            foreach (var u in _factionDef2.Units) u.ResolveAbilities(_abilityRegistry);

            // Story 3.6: build the behavior registry from resources/data/behaviors/ (authoring-only — no Resolve loop,
            // nothing in the sim consumes a behavior yet). The Unit Card Editor reads it via SceneContext.
            string behaviorsAbs = ProjectSettings.GlobalizePath(BEHAVIORS_DIR);
            _behaviorRegistry = BehaviorRegistry.LoadFromDirectory(
                behaviorsAbs, name => GD.Print($"[Behaviors] skipped invalid {name}"));

            // Story 3.15: build the item registry from resources/data/items/ (fail-closed per file), injected into the
            // host so scenario item placement + the editor Item palette resolve item ids. Empty when the dir is absent.
            string itemsAbs = ProjectSettings.GlobalizePath(ITEMS_DIR);
            _itemRegistry = ItemRegistry.LoadFromDirectory(
                itemsAbs, name => GD.Print($"[Items] skipped invalid {name}"));

            // Story 5.7 (FR-19/UX-DR80): discover every SELECTABLE (ValidateComplete-passing) faction under
            // resources/data/factions/, fresh on every scene load (Godot reloads MainScene from disk on every
            // Play/Playtest, so this alone satisfies "no restart needed" — same posture as the Ability/Behavior/
            // Item registries above). No skirmish/lobby picker screen exists yet (Story 11.1, which depends on
            // THIS list), so the console-printed discovered set is the one currently-real "selectable list" surface.
            string factionsAbs = ProjectSettings.GlobalizePath(FACTIONS_DIR);
            var selectableFactions = FactionDefinition.LoadSelectableFromDirectory(
                factionsAbs, (name, reason) => GD.Print($"[Factions] skipped invalid {name}: {reason}"));
            GD.Print($"[Factions] {selectableFactions.Count} selectable: "
                + string.Join(", ", selectableFactions.Select(f => f.Id)));

            // Story 2.11 (review C1): tag-validate the DEFAULT-SEEDED faction defs on the client too, mirroring the
            // server's validate-every-def posture (ServerBootstrap). ResolveSlotFactionDefs only validates slots that
            // carry an explicit faction_json file, so without this a default/fallback slot would spawn an unknown-tag
            // unit the arbitrating server drops → client/server roster desync (AC2.1). No-op on today's tag-free defs.
            // (The headless dedicated server already returned above and validates via ServerBootstrap, so this is
            // client-only; the drop mutates the shared _factionDef/_factionDef2 BEFORE they seed _slotFactionDefs below.)
            foreach (string err in UnitTagValidator.ValidateAndDropUnits(_factionDef))
                GD.PrintErr($"[UnitTagValidator] {err} (unit dropped)");
            foreach (string err in UnitTagValidator.ValidateAndDropUnits(_factionDef2))
                GD.PrintErr($"[UnitTagValidator] {err} (unit dropped)");

            // AR-3 / Story 5.1: construct the registry before it's needed and let it own per-slot storage
            // (SlotDefinitions) instead of a locally-allocated array — the registry now holds the per-slot
            // FactionDefinition lookups; see FactionRegistry.SlotDefinitions. TODO(5.1) partially resolved:
            // this is the "hold per-slot FactionDefinition[]" half; deriving ActiveFactions from assigned
            // slots (the TODO's other half) is intentionally NOT done — see FactionRegistry's ctor comment.
            // Story 9.7: the active-player count now DERIVES from the loaded scenario's PlayerSlots.Count (the only
            // source of N, fed identically to the server's activeFactionCount so client/server checksum spans cannot
            // diverge). Both peers load the identical, agreement-gated scenario, so a raw pre-parse here (before the
            // validated apply in ScenarioLoadPhase) reads the same count the server reads. ClampActivePlayers keeps
            // N=2 for today's 2-slot default (byte-identical to the old hardcoded 2).
            // Story 9.11 (P1): consume a pending replay handoff from the browser's Play (survives ReloadCurrentScene).
            // Point ScenarioPath at the replay's scenario so it loads FRESH into a clean tick-0 world below; the
            // autoplay tail then runs TryLoadReplay so the re-gate compares against the correctly-loaded scenario.
            if (PendingReplayPath != null)
            {
                ReplayPath = PendingReplayPath;
                if (!string.IsNullOrEmpty(PendingReplayScenarioPath)) ScenarioPath = PendingReplayScenarioPath;
                PendingReplayPath = null;
                PendingReplayScenarioPath = null;
            }

            int activePlayers = ClampActivePlayers(PeekScenarioPlayerSlots(ScenarioPath));
            var factions = new FactionRegistry(activePlayers);

            // Default slot assignments — overwritten per-slot by the ResolveSlotFactionDefs pre-pass
            _slotFactionDefs = factions.SlotDefinitions;
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
            //    stores, the canonical 10-system tick order (ModifierSystem at index 3), the
            //    SimulationLoop, and the single checksum sink. MainScene injects the presentation GodotLogSink
            //    plus the loaded inputs; sim truth now lives on the host (the fields below are aliases of it).
            _host = SimulationHost.Create(
                _logSink,
                factions,
                _factionDef,
                _factionDef2,
                _damageTable,
                AiLevel,
                _abilityRegistry, // Story 2.4b: the 7th arg — makes AbilityCastSystem cast a real ability in-game (was Empty)
                _itemRegistry);   // Story 3.15: the 8th arg — item placement + pickup/use resolve real item defs (was Empty)

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
            // Story 3.14: route hero-revival respawns through the shared applier spawn path (so a revived hero also gets
            // MeshType/worker wiring), reusing the one ApplyUnitDefinition mapper. Determinism-identical to the host's
            // default closure; only the presentation MeshType differs.
            _host.SetReviveSpawn(_applier.SpawnUnitAt);

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
                AbilityRegistry = _abilityRegistry, // Story 2.4b: the command card reads this for ability labels
                BehaviorRegistry = _behaviorRegistry, // Story 3.6: the Unit Card Editor reads this for the behavior picker + compat
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
                new CustomHudOverlayPhase(_ctx), // Story 7.8 — the custom-UI read rail overlay, after Hud (shares nothing; own CanvasLayer)
                new ObjectiveLogOverlayPhase(_ctx), // Story 7.14 — in-match quest log (read rail) + skippable briefing (own CanvasLayers)
                new TriggerDebugOverlayPhase(_ctx), // Story 7.15 — trigger-debug overlay (variable watch + fired-log + fire counters + enabled; own CanvasLayer)
                new MinimapPhase(_ctx),
                new TerrainBrushPhase(_ctx),
                new ScenarioLoadPhase(_ctx),
                new RegionToolPhase(_ctx),   // Story 6.4 — after ScenarioLoad so _ctx.Scenario exists to mutate
                new PathabilityToolPhase(_ctx), // Story 6.5 — after ScenarioLoad so _ctx.Scenario + _ctx.Pathability exist
                new CameraToolPhase(_ctx),   // Story 6.6 — after PathabilityTool; shares _ctx.Placer.History
                new WaterToolPhase(_ctx),    // Story 6.6 — after CameraTool; shares _ctx.Placer.History
                new FactionVisualsPhase(_ctx),
                new FlowFieldInitPhase(_ctx),
                new WinConditionPhase(_ctx),
                new GameOverOverlayPhase(_ctx),
                new MatchLifecycleController(_ctx),
                new ReplayStatusPhase(_ctx),
                new ContentBrowserPhase(_ctx),
                new ReplayBrowserPhase(_ctx),   // Story 9.11 — replay browser (hotkey N) + in-playback controls overlay

                new MainMenuPhase(_ctx),
                new TriggerEditorPhase(_ctx),
                new DslGraphEditorPhase(_ctx),   // Story 7.10 — after TriggerEditor so _ctx.TriggerPanel exists (T2↔T3 wiring)
                new MapGeneratorPhase(_ctx),
                new AbilityEditorPhase(_ctx),
                new UnitCardPhase(_ctx),
                new ItemCardPhase(_ctx),
                new BuildingCardPhase(_ctx),
                new TechTreePhase(_ctx),   // Story 4.6 — must run AFTER BuildingCardPhase so _ctx.BuildingCardPanel already exists
                new PersistenceManifestPhase(_ctx),
                new HeroPickerPhase(_ctx),
                new FactionDefinerPhase(_ctx),
                new OnboardingPhase(_ctx),   // Story 5.9 — must be last (mirrors ScenePhaseOrder.Canonical); drives
                                              // panels every earlier phase has already constructed
            };
            new ScenePhaseRunner(phases).Run();

            // Story 6.6 — the prop renderer (one MultiMesh per distinct prop mesh) reads the live scenario each frame.
            // Created AFTER the phase runner so ScenarioLoad has populated _ctx.Scenario; it late-binds via the getter
            // so it survives the F5 Edit→Play re-apply. Not a phase (no cross-phase dependency to sequence).
            var propRenderer = new UI.PropRenderer { Name = "PropRenderer" };
            AddChild(propRenderer);
            propRenderer.Initialize(() => _ctx.Scenario);

            // Compute scenario hash now that both scenario and lobby are ready.
            // Sent with the Ready packet so peers can detect map mismatches before starting.
            // Story 1.7 (AR-23): canonical-model hash over the in-memory APPLIED scenario (not file bytes) —
            // stable across whitespace / JSON key order / 1.0-vs-1 / file path, fixing the AI-gen stale-file
            // desync. Folded to the existing 32-bit Ready-packet wire (widening is Epic 9). _ctx.Scenario holds the
            // applied model for the file / AI / editor paths; _ctx.FallbackMirror holds it for the hardcoded fallback.
            // Story 1.7 review patch: only publish a hash for a model that was actually applied. In fail-closed
            // mode a rejected scenario leaves _ctx.ScenarioApplied false (nothing reached the sim), so we publish 0
            // rather than advertising a start-state we never built. Story 7.7: 0 is now fail-CLOSED — HandshakeGate
            // BLOCKS the lobby start on either side's 0 ("scenario hash not computed"), so a host with no applied
            // scenario can no longer start a match.
            ScenarioData? hashModel = _ctx.Scenario ?? _ctx.FallbackMirror;
            _ctx.LobbyUi.ScenarioHash = (_ctx.ScenarioApplied && hashModel != null)
                ? Definitions.CanonicalModelHash.ToWire(Definitions.CanonicalModelHash.Compute(hashModel))
                : 0u;
            GD.Print($"[MainScene] Scenario hash: 0x{_ctx.LobbyUi.ScenarioHash:X8}");

            // Story 3.2 (AC3, D-3): compute the canonical START-STATE hash over the applied model + the HeroStore init
            // state (empty until Story 3.9's load path). Its own FNV-64 / AlgoVersion, distinct from the scenario hash
            // above. COMPUTED + logged here (the CanonicalModelHash precedent, Story 1.7); it is deliberately NOT put in
            // the Ready packet — wiring it into the server-attested multi-hash handshake is Epic 9 / M5 (D-3), so
            // PROTOCOL_VERSION is untouched. Fail-closed to 0 for an unapplied model, mirroring the scenario hash.
            // Story 3.9 (D-1): mint the DEPLOYED profile (if any) into HeroStore AFTER ScenarioApplier.Apply and BEFORE
            // StartStateHash.Compute, so the persisted level/xp fold into the hash automatically. At first boot
            // PendingHeroProfile is null → nothing minted → HeroStore stays empty → every golden/stamp is byte-identical
            // (no-profile flows are unchanged). The player-facing Deploy path mints + recomputes at launch time.
            Definitions.HeroProfileLoader.LoadInto(_host.Heroes, _applier.LastAppliedHeroes, _ctx.PendingHeroProfile,
                world: _host.World, // Story 3.13: establish the entity→hero link (D-8) for the XP runtime
                items: _host.Items, registry: _host.ItemRegistry, // Story 3.16: re-mint persisted inventory before the hash
                modifiers: _host.Modifiers, usableSlots: _host.ItemSys.UsableSlots, // Story 3.16 review: apply carried stat modifiers + honor the slot cap
                ownerSlot: _ctx.Lockstep?.LocalFaction); // DW-13: mint the deployed profile into the local player's placed hero only (null at first boot / no profile is inert)
            ulong startStateHash = (_ctx.ScenarioApplied && hashModel != null)
                ? Definitions.StartStateHash.Compute(hashModel, _host.Heroes)
                : 0UL;
            GD.Print($"[MainScene] Start-state hash (algo v{Definitions.StartStateHash.AlgoVersion}): 0x{startStateHash:X16}");

            // Story 9.4: the single 64-bit match-agreement value carried on the widened Ready packet and gated
            // fail-closed before tick 0 (server-attested + P2P HandshakeGate). Folds the ruleset (EffectCaps
            // structural caps), the initial input delay, the roster + faction-count (from the applied model), and
            // the StartStateHash above. Fail-closed to 0 for an unapplied model, mirroring the hashes above.
            // Story 9.16: gather the loaded CONTENT definitions the applied scenario references — the distinct faction
            // defs, the ability + item registries, and the damage table — and fold them into MatchAgreementHash via
            // ContentHash so a content-byte mismatch (a unit stat, a damage cell, an ability effect, an extra ability
            // file that reindexes the registry) rejects fail-closed at the handshake instead of desyncing mid-match.
            // Computed ONCE here at load time over the already-materialized registries/defs (the Start/Ready path only
            // READS the cached value — no start-button recompute). Presentation-only gather (this is the presentation
            // layer); the fold itself is Godot-free.
            var loadedFactions = new System.Collections.Generic.List<Definitions.FactionDefinition>();
            if (_ctx.SlotFactionDefs != null)
                foreach (Definitions.FactionDefinition? fd in _ctx.SlotFactionDefs)
                    if (fd != null) loadedFactions.Add(fd);
            _ctx.LobbyUi.MatchAgreementHash = (_ctx.ScenarioApplied && hashModel != null)
                ? Definitions.MatchAgreementHash.Compute(
                    ProjectChimera.Multiplayer.LockstepManager.INPUT_DELAY, hashModel, _host.Heroes,
                    loadedFactions, _ctx.AbilityRegistry, _host.ItemRegistry, _ctx.DamageTable)
                : 0UL;
            // Story 9.16: cache the LOCAL per-domain content breakdown for the handshake-block "which domain" surfacing.
            _ctx.LobbyUi.ContentBreakdown = (_ctx.ScenarioApplied && hashModel != null)
                ? Definitions.ContentHash.Describe(loadedFactions, _ctx.AbilityRegistry, _host.ItemRegistry, _ctx.DamageTable).ToString()
                : null;
            GD.Print($"[MainScene] Match-agreement hash (algo v{Definitions.MatchAgreementHash.AlgoVersion}): 0x{_ctx.LobbyUi.MatchAgreementHash:X16}");
            GD.Print($"[MainScene] Content breakdown: {_ctx.LobbyUi.ContentBreakdown}");

            // If a replay file is specified via the Inspector OR handed off from the browser's Play (P1), load it now
            // and enter Play mode immediately — no lobby, no network required. On success, reset the playback control
            // session (1x, reveal-all, no pending seek).
            if (!string.IsNullOrEmpty(ReplayPath))
            {
                _ctx.MatchLifecycle.TryLoadReplay(ReplayPath);
                if (_ctx.ReplayPlayer != null) BeginReplayPlaybackSession();
            }

#if DEBUG
            // Story 1.9a (Task 10 loopback smoke, DEBUG-only): if launched with `-- --autojoin <ip:port>`, this
            // client auto-connects to the dedicated server and auto-readies (no lobby clicks), so the one-click
            // launcher (godot/tools/loopback-desync-smoke.cmd) can stand up server + 2 clients into a live match.
            // Then F9 (handled in _UnhandledInput) induces a one-peer desync → server HALT → both clients halt.
            string? autoJoin = ParseAutoJoinArg();
            if (autoJoin != null)
            {
                int sep    = autoJoin.LastIndexOf(':');
                string ip  = sep > 0 ? autoJoin.Substring(0, sep) : autoJoin;
                int port   = (sep > 0 && int.TryParse(autoJoin.Substring(sep + 1), out int ap))
                    ? ap : ProjectChimera.Multiplayer.DedicatedServer.DEFAULT_PORT;
                GD.Print($"[MainScene] --autojoin {ip}:{port} — auto-connecting to dedicated server (loopback smoke).");
                if (_ctx.MainMenu != null) _ctx.MainMenu.Visible = false; // auto-join bypasses the Play-Skirmish button; hide the title screen so it doesn't cover the game
                _ctx.LobbyUi.AutoJoinDedicated(ip, port);
            }
#endif

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
            if (_headless) return; // dedicated server has no input / no _ctx
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
                            _ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1, _resources, _world);
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
            if (_headless) return; // dedicated server has no input / no _ctx
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

            // Story 7.14 — the in-match quest-log toggle. Handled ABOVE the Edit-mode guard so it fires in PLAY, but
            // scoped to Play only: the quest log is an in-match affordance, so F1 does nothing in Edit (it would
            // otherwise pop the log over the map editor reflecting stale/empty state). F1 is unclaimed by
            // SelectionSystem / EntityPlacer / the Edit-mode letter ladder (the conventional "objectives/help" key).
            // Presentation-only — no sim write, checksum byte-identical. Null-guarded: the overlay phase may not have
            // run in a reduced scene.
            if (key.Keycode == Key.F1)
            {
                if (_ctx.GameState.Mode == GameMode.Play)
                    _ctx.ObjectiveLog?.Toggle();
                GetViewport().SetInputAsHandled();
                return;
            }

            // Story 7.15 — the in-match trigger-debug overlay toggle. Handled ABOVE the Edit-mode guard so it fires
            // in PLAY, but scoped to Play only (a developer diagnostic of a running match: variable watch, fired-log,
            // fire counters, enabled state). F2 is unclaimed — no other Key.F2 check anywhere in src/ and no InputMap
            // action binds it. Presentation-only — no sim write, checksum byte-identical. Null-guarded: the overlay
            // phase may not have run in a reduced scene (the F1 precedent).
            if (key.Keycode == Key.F2)
            {
                if (_ctx.GameState.Mode == GameMode.Play)
                    _ctx.TriggerDebugOverlay?.Toggle();
                GetViewport().SetInputAsHandled();
                return;
            }

            // Edit-mode-only shortcuts.
            if (_ctx.GameState.Mode != GameMode.Edit) return;

            // Story 9.7: the dev-only Edit-mode `N` lobby toggle is REMOVED — the multiplayer lobby is now reached
            // from the real MainMenu "Multiplayer" destination (MainMenuPhase.OnMultiplayer → LobbyUi.Show), not a
            // hidden keybind. `N` is now free.
            if (key.Keycode == Key.O)
            {
                _ctx.ContentBrowser.ToggleVisible();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.N)
            {
                // Story 9.11: N opens the replay browser (freed in Story 9.7 when the dev lobby keybind was removed).
                _ctx.ReplayBrowser.ToggleVisible();
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
            else if (key.Keycode == Key.K)
            {
                _ctx.AbilityEditorPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.J)
            {
                _ctx.UnitCardPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.G)
            {
                // Story 3.16: G opens the Item Card Editor (I is reserved for in-match Inventory, K for the Ability editor).
                _ctx.ItemCardPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.C)
            {
                // Story 4.5: C opens the Building Card Editor (verified unused — no other Key.C check anywhere in
                // src/ and no InputMap action binds physical key C in project.godot; B is taken by EntityPlacer's
                // building-placement-mode toggle).
                _ctx.BuildingCardPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.V)
            {
                // V (not P — P is the Patrol command in SelectionSystem, active whenever units are selected in Edit mode).
                _ctx.PersistenceManifestPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.R)
            {
                // Story 4.6: R opens the Visual Tech Tree Editor (verified unused — no other Key.R check anywhere in
                // src/ and no InputMap action binds physical key R in project.godot; T is already claimed by
                // TerrainBrush/SelectionSystem).
                _ctx.TechTreePanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.X)
            {
                // Story 5.5: X opens the Faction Definer guided wizard (verified unused — no other Key.X check
                // anywhere in src/ and no InputMap action binds physical key X in project.godot).
                _ctx.FactionDefinerPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.Y && !key.CtrlPressed)
            {
                // Story 7.10: Y opens the T3 node-graph editor. The spec's example key (G) is taken (EntityPlacer
                // grid-snap + ItemCard editor), so Y is used — plain Y is unbound (every other Key.Y usage is
                // Ctrl+Y = redo, handled by the focused card panel's _Input before this _UnhandledInput ever runs;
                // the !CtrlPressed guard keeps redo out of this toggle even when no card is focused).
                _ctx.DslGraphEditorPanel.Toggle();
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Onboarding wrappers (Story 5.9) ──────────────────────────────────────
        // Thin public wrappers around the SAME panel handles the hotkey switch above already drives, so
        // OnboardingPanel can navigate real panels/mode without duplicating their open/duplicate/toggle logic
        // (D-2 precedent — mirrors how EntityPlacer/UnitCardPanel are driven rather than re-implemented).

        /// <summary>Open the Unit Card Editor. With <paramref name="templateUnitId"/>, duplicates that curated unit
        /// fresh and binds the clone (onboarding step 1) — returns whether the duplicate actually happened. With
        /// null, just ensures the panel is open without creating anything (onboarding steps 2/3 revisiting the
        /// unit step 1 already created) and always returns true.</summary>
        public bool OpenUnitCardPanel(string? templateUnitId = null)
        {
            if (templateUnitId != null) return _ctx.UnitCardPanel.StartFromTemplate(templateUnitId);
            _ctx.UnitCardPanel.EnsureVisible();
            return true;
        }

        /// <summary>
        /// Story 7.15 — click-to-navigate landing for the trigger-debug overlay's fired-log entries. Crosses the
        /// Play→Edit boundary (the debug overlay runs in Play; the trigger editors are Edit-mode tools): switch
        /// <c>GameState</c> to Edit — which resets the running match, inherent to the engine's mode model, not a
        /// defect (the user clicked to go EDIT that trigger) — open the flat <see cref="CreationSuite.TriggerEditorPanel"/>,
        /// and focus + highlight the authored trigger at <paramref name="triggerIndex"/>. A best-effort exec→
        /// <c>Triggers[]</c> map miss opens the editor unfocused rather than crashing (FocusTrigger bounds-guards).
        /// </summary>
        public void NavigateToTrigger(int triggerIndex)
        {
            _ctx.TriggerDebugOverlay?.Close();                                 // don't leave the diagnostic panel over the editor
            if (_ctx.GameState.Mode == GameMode.Play) _ctx.GameState.Toggle(); // → Edit (resets the match, by design)
            _ctx.TriggerPanel.FocusTrigger(triggerIndex);                      // ensures the panel is open + focuses the row
        }

        /// <summary>Re-sync the WinConditionUi corner panel's radio selection from the live scenario (Story 5.9
        /// review pass) — called after <c>OnboardingPanel</c>'s win-condition step mutates the same field, so the
        /// two surfaces never silently disagree on what's currently selected.</summary>
        public void RefreshWinConditionUi() => _ctx.WinConditionUiRefresh?.Invoke();

        /// <summary>Enter Play mode — idempotent (a no-op if already playing). Drives the same GameState toggle F5
        /// uses (onboarding step 6's optional "Enter Play Mode" button; never a new hotkey).</summary>
        public void EnterPlayMode()
        {
            if (_ctx.GameState.Mode == GameMode.Edit) _ctx.GameState.Toggle();
        }

        // ── Replay playback controls (Story 9.11) ────────────────────────────────
        // Presentation-only: these mutate ONLY the local playback flags + the view-only fog viewer. They never touch
        // sim state or the checksum — determinism is unaffected (seek/speed/pause are pure re-sim).

        /// <summary>Reset the playback control state at the start of a replay session (called by ReplayBrowserPhase
        /// after a successful <c>TryLoadReplay</c>): playing at 1x, reveal-all perspective, no pending seek.</summary>
        public void BeginReplayPlaybackSession()
        {
            _replayPaused      = false;
            _replaySpeed       = 1;
            _replaySeekTo      = -1;
            _replayPerspective = -1;
            if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = true;
        }

        /// <summary>Pause / resume replay stepping.</summary>
        public void ReplayTogglePause() => _replayPaused = !_replayPaused;

        /// <summary>Set the replay speed (sim ticks stepped per frame): 1/2/4/8. Resumes if paused.</summary>
        public void ReplaySetSpeed(int speed)
        {
            _replaySpeed  = ReplayFormat.ClampSpeed(speed);
            _replayPaused = false;
        }

        /// <summary>Request a fast, render-free re-sim FORWARD to <paramref name="targetTick"/> (no-op if not ahead —
        /// there is no rewind in 1.0; backward navigation is a restart from tick 0).</summary>
        public void ReplaySeekForward(uint targetTick)
        {
            if (_ctx.ReplayPlayer == null) return;
            if (targetTick > _host.CurrentTick) _replaySeekTo = (int)targetTick;
        }

        /// <summary>Cycle the replay perspective: reveal-all → each roster player's fog → reveal-all. View-only — goes
        /// through <c>Fog.SetViewer</c> / <c>FogBridge.RevealAll</c>, which are NOT folded into the checksum.</summary>
        public void ReplayCyclePerspective()
        {
            var rp = _ctx.ReplayPlayer;
            int n = rp?.Roster.Length ?? 0;
            int next = _replayPerspective + 1;
            if (next >= n) next = -1; // wrap back to reveal-all
            _replayPerspective = next;

            if (_replayPerspective < 0)
            {
                if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = true;
            }
            else
            {
                if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = false;
                _ctx.Fog.SetViewer(rp!.Roster[_replayPerspective]);
            }
        }

        /// <summary>Human-readable label for the current replay perspective (for the controls overlay).</summary>
        private string CurrentPerspectiveLabel()
        {
            var rp = _ctx.ReplayPlayer;
            if (_replayPerspective < 0 || rp == null || _replayPerspective >= rp.Roster.Length)
                return "Reveal All";
            return rp.Roster[_replayPerspective].ToString();
        }

        public override void _Process(double delta)
        {
            if (_headless) return; // dedicated server: no presentation context (the DedicatedServer node self-polls)
            if (_ctx.GameState.Mode == GameMode.Play && !_gameOver)
            {
                if (_ctx.ReplayPlayer != null)
                {
                    // Replay mode: feed recorded commands instead of live network/input. Story 9.11 adds pause /
                    // speed (1/2/4/8) / seek-forward — all pure re-sim (deterministic; no rewind snapshots, no
                    // checksum effect). Backward navigation is a restart-from-tick-0 (not implemented here — no rewind
                    // in 1.0); seek-forward fast-loops Flush+StepOnce to the target without a per-frame render.
                    var rp = _ctx.ReplayPlayer;
                    _ctx.ReplayControls?.SetActive(true);

                    if (_replaySeekTo >= 0)
                    {
                        // P4: advance a BOUNDED batch of ticks this frame (not the whole seek at once) so a long seek
                        // spans several frames instead of freezing the window on the main thread. Keep the target set
                        // until reached; clear it once we arrive (or the replay ends).
                        uint target = (uint)_replaySeekTo;
                        int budget = REPLAY_SEEK_TICKS_PER_FRAME;
                        while (_host.CurrentTick < target && !rp.IsFinished && budget-- > 0)
                        {
                            rp.Flush(_host.CurrentTick);
                            _host.StepOnce();
                        }
                        if (_host.CurrentTick >= target || rp.IsFinished)
                            _replaySeekTo = -1;
                    }
                    else if (!_replayPaused)
                    {
                        int steps = _replaySpeed < 1 ? 1 : _replaySpeed;
                        for (int s = 0; s < steps && !rp.IsFinished; s++)
                        {
                            rp.Flush(_host.CurrentTick);
                            _host.StepOnce();
                        }
                    }

                    if (rp.IsFinished)
                    {
                        GD.Print($"[Replay] Finished at tick {_host.CurrentTick}.");
                        _ctx.ReplayPlayer = null;
                        if (_ctx.ReplayStatusLabel != null) _ctx.ReplayStatusLabel.Visible = false;
                        _ctx.ReplayControls?.SetActive(false);
                    }
                    else
                    {
                        _ctx.ReplayControls?.UpdateReadout(
                            _host.CurrentTick, rp.LastTick, _replayPaused, _replaySpeed,
                            CurrentPerspectiveLabel(), seeking: _replaySeekTo >= 0);
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

                // Story 7.11/7.12: win evaluation lives in the deterministic sim-layer WinConditionSystem (the grace
                // period is a tick count there, not this frame count). Presentation merely CONSUMES verdicts — it
                // holds NO win math. Story 7.12 makes elimination per-player: a locally-eliminated player flips to the
                // RevealAll spectator view with a non-terminal defeat banner and KEEPS ticking; only when the match is
                // FULLY resolved (a team won, or the no-victor form) does ShowGameOver fire once. Faction.Player1==1
                // aligns with the 1-based overlay arg (no adapter math). The ScenarioDirector OnVictory escape hatch
                // still works too.
                int winnerFaction = _host.WinState.WinnerFaction();
                if (winnerFaction != 0)
                {
                    ShowGameOver(winnerFaction);
                }
                else if (_host.WinCon.IsFullyResolved())
                {
                    // Every active faction is latched but no team WON (a lone team wiped itself out): the no-victor
                    // match-over form. 0 = "no victor" → ShowGameOver renders its defeat/match-over form.
                    ShowGameOver(0);
                }
                else
                {
                    // Match continues. If the LOCAL player has latched VERDICT_LOST, switch to the spectator reveal +
                    // a non-terminal defeat banner and keep watching until the match fully resolves. LocalFaction is
                    // Player1 offline (LockstepManager default).
                    Faction local = _ctx.Lockstep.LocalFaction;
                    if (!_localEliminated && local != Faction.Neutral
                        && _host.WinState.Verdict[(int)local] == WinStateStore.VERDICT_LOST)
                        OnLocalPlayerEliminated();
                }
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
            _ctx.CustomHud.Update(); // Story 7.8 — pull the version-stamped read rail; re-format only changed widgets
            _ctx.ObjectiveLog?.Update(); // Story 7.14 — pull objective state off the read rail; re-format only on version change (null-guarded: phase may be absent in a reduced scene, matching the F1 toggle guard)
            _ctx.TriggerDebugOverlay?.Update(); // Story 7.15 — pull the variable watch / fired-log / fire counters / enabled state; re-format only on change (null-guarded, matching the F2 toggle guard)
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
        internal bool MoveStartPosition(int slot, Vector3 worldPos, float startOre, float startCrystal)
        {
            // Update scenario data (persisted on save). Story 6.7: if the author placed a NEW slot (2–4 start
            // positions, add-slot), the Godot-free UpsertStartSlot appends a ScenarioPlayerSlot for it rather than
            // silently dropping the placement, and reports whether a new slot was created.
            bool created = _ctx.Scenario?.UpsertStartSlot(slot, worldPos.X, worldPos.Z, startOre, startCrystal) ?? false;

            // Update live sim: faction deposit / rally point. Routed through the applier (Story 1.8b D6) — the
            // unified sole writer of FactionBase; after 1.8b no MainScene code writes Resources.FactionBase directly.
            var faction = (Faction)(slot + 1);
            _applier.SetFactionBase(faction, new FixedVec3(
                Fixed.FromFloat(worldPos.X), Fixed.Zero, Fixed.FromFloat(worldPos.Z)));

            // Move the visual marker
            _ctx.StartPosBridge.SetPosition(slot, worldPos);
            return created;
        }

        /// <summary>Story 6.7 (patch 3) — persist an in-place economy edit (ore/crystal) to an already-placed start
        /// slot immediately, without moving it or appending. Wired to the palette Ore/Crystal spinners.</summary>
        internal void SetStartSlotEconomy(int slot, float ore, float crystal)
            => _ctx.Scenario?.UpdateStartSlotEconomy(slot, ore, crystal);

        /// <summary>
        /// Story 6.7 — remove the trailing start slot (2–4 add/remove). Drops the matching
        /// <see cref="ScenarioPlayerSlot"/> from the live scenario and hides its flag marker. The engine keeps a
        /// minimum of 2 slots (enforced in the editor), so this only ever removes slot 2 or 3.
        /// </summary>
        internal void RemoveStartPosition(int slot)
        {
            // Story 6.7 (review pass 2) — only touch sim/visual state when a slot was ACTUALLY removed. RemoveStartSlot
            // matches by exact Slot value, so a no-match request (e.g. a non-contiguous set where count-1 is not a live
            // slot value) must NOT zero an unrelated faction's deposit point or hide a still-live marker.
            bool removed = _ctx.Scenario?.RemoveStartSlot(slot) ?? false;
            if (!removed) return;

            // Clear the stale sim base for the removed faction so a subsequent placement / re-add does not inherit a
            // ghost deposit point from the dropped slot. Route the slot→faction offset through the canonical
            // FactionRegistry.ToFaction cast site (never a scattered raw (Faction)(slot+1)).
            _applier.SetFactionBase(FactionRegistry.ToFaction(slot), new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));

            _ctx.StartPosBridge.EnsureVisible(slot, false);
        }

        // ── ScenarioData sync (Story 6.1) ─────────────────────────────────────
        // EntityPlacer keeps ScenarioData in sync with the live stores by calling these back on every building/unit/
        // resource-node place/delete AND both directions of their undo/redo (mirroring the MoveStartPosition bridge).
        // They mutate _ctx.Scenario.{Buildings,Units,ResourceNodes} so placements survive save/reload AND the F5
        // Edit→Play re-apply — ResetToAuthoredStart re-applies ONLY _ctx.Scenario, so an unsynced placement is lost.
        // Identity-preserving: RemoveMatch captures and returns the REAL entry object; ReAdd/RemoveHandle operate on
        // that exact reference — so a delete→undo restores an authored node's economy fields / a building's pre_built
        // flag verbatim rather than reconstructing a lossy value.

        private const float SCENARIO_SYNC_EPS = 0.1f; // world-unit tolerance matching a live row to its scenario entry

        /// <summary>Story 6.8 — the <c>ScenarioBuilding.Type</c> string for an authored building id (its dual meaning):
        /// a BUILT-IN id serializes as its legacy <see cref="BuildingType"/> ENUM NAME (e.g. <c>"command_center"</c> →
        /// <c>"CommandCenter"</c>), so every existing scenario stays byte-identical; a CUSTOM id (no enum member)
        /// serializes as the authored id verbatim (e.g. <c>"watchtower"</c>). Uses the single
        /// <see cref="TechTreeChecker.BuildingTypeFromId"/> id↔enum source.</summary>
        private static string ScenarioTypeString(string buildingId) =>
            TechTreeChecker.BuildingTypeFromId(buildingId) is BuildingType bt ? bt.ToString() : buildingId;

        /// <summary>Story 6.1 — building sync callback fired by <see cref="EntityPlacer"/>. See
        /// <see cref="EntityPlacer.ScenarioSyncOp"/>. Editor buildings are always P1 (slot 0) in practice, but the
        /// slot is derived from the faction so the handler stays general.</summary>
        internal object? SyncBuilding(EntityPlacer.ScenarioSyncOp op, object? handle,
                                     string buildingId, Faction faction, Vector3 pos, bool preBuilt)
        {
            var scen = _ctx.Scenario;
            if (scen == null) return null;
            switch (op)
            {
                case EntityPlacer.ScenarioSyncOp.Add:
                {
                    int addSlot = (int)faction - 1;
                    // Story 6.1: never persist a placement for a slot the scenario doesn't declare — ScenarioValidator
                    // fails closed on an undeclared slot, so an unguarded append would veto the very next F5 (and Save).
                    // Skipping keeps such a placement cosmetic-only (its pre-6.1 behavior); the null handle makes the
                    // matching undo/redo legs no-op by construction.
                    if (!SlotDeclared(scen, addSlot))
                    {
                        GD.PrintErr($"[MainScene] Building sync: slot {addSlot} is not a declared player_slot — placement not persisted (would invalidate F5/Save).");
                        return null;
                    }
                    var entry = new ScenarioBuilding
                    {
                        Type = ScenarioTypeString(buildingId), Slot = addSlot,
                        X = pos.X, Z = pos.Z, PreBuilt = preBuilt,
                    };
                    scen.Buildings = AppendEntry(scen.Buildings, entry);
                    return entry;
                }
                case EntityPlacer.ScenarioSyncOp.ReAdd:
                    if (handle is ScenarioBuilding b) scen.Buildings = AppendEntry(scen.Buildings, b);
                    return handle;
                case EntityPlacer.ScenarioSyncOp.RemoveHandle:
                    scen.Buildings = RemoveByIdentity(scen.Buildings, handle as ScenarioBuilding, out _);
                    return null;
                case EntityPlacer.ScenarioSyncOp.RemoveMatch:
                {
                    int slot = (int)faction - 1;
                    // Story 6.1: an undeclared-slot placement was never persisted by Add (SlotDeclared guard), so
                    // there is nothing to remove — return silently rather than firing the drift diagnostic below,
                    // symmetric with Add's skip. Otherwise deleting a cosmetic-only P2 placement in a single-slot
                    // scenario would log a false "live store may have drifted" error.
                    if (!SlotDeclared(scen, slot)) return null;
                    ScenarioBuilding? match = null;
                    foreach (var e in scen.Buildings ?? Array.Empty<ScenarioBuilding>())
                        if (e.Slot == slot && PosMatch(e.X, e.Z, pos)) { match = e; break; }
                    if (match == null)
                    {
                        GD.PrintErr($"[MainScene] Building sync: no ScenarioData.Buildings entry matched a delete at ({pos.X:F1},{pos.Z:F1}) slot {slot} — live store may have drifted.");
                        return null;
                    }
                    scen.Buildings = RemoveByIdentity(scen.Buildings, match, out _);
                    return match;
                }
            }
            return null;
        }

        /// <summary>Story 6.1 — unit sync callback fired by <see cref="EntityPlacer"/>. Matched by slot + position
        /// (a def-less spawn is never persisted, so it never reaches RemoveMatch).</summary>
        internal object? SyncUnit(EntityPlacer.ScenarioSyncOp op, object? handle,
                                 string unitId, Faction faction, Vector3 pos)
        {
            var scen = _ctx.Scenario;
            if (scen == null) return null;
            switch (op)
            {
                case EntityPlacer.ScenarioSyncOp.Add:
                {
                    int addSlot = (int)faction - 1;
                    // Story 6.1: skip persisting a placement for an undeclared slot (see SyncBuilding.Add) so a P2
                    // unit placed in a single-slot scenario cannot brick the next F5/Save via ScenarioValidator.
                    if (!SlotDeclared(scen, addSlot))
                    {
                        GD.PrintErr($"[MainScene] Unit sync: slot {addSlot} is not a declared player_slot — placement not persisted (would invalidate F5/Save).");
                        return null;
                    }
                    var entry = new ScenarioUnit { UnitId = unitId, Slot = addSlot, X = pos.X, Z = pos.Z };
                    scen.Units = AppendEntry(scen.Units, entry);
                    return entry;
                }
                case EntityPlacer.ScenarioSyncOp.ReAdd:
                    if (handle is ScenarioUnit u) scen.Units = AppendEntry(scen.Units, u);
                    return handle;
                case EntityPlacer.ScenarioSyncOp.RemoveHandle:
                    scen.Units = RemoveByIdentity(scen.Units, handle as ScenarioUnit, out _);
                    return null;
                case EntityPlacer.ScenarioSyncOp.RemoveMatch:
                {
                    int slot = (int)faction - 1;
                    // Story 6.1: an undeclared-slot placement was never persisted by Add (SlotDeclared guard), so
                    // there is nothing to remove — return silently rather than firing the drift diagnostic below,
                    // symmetric with Add's skip (see SyncBuilding.RemoveMatch).
                    if (!SlotDeclared(scen, slot)) return null;
                    ScenarioUnit? match = null;
                    foreach (var e in scen.Units ?? Array.Empty<ScenarioUnit>())
                        if (e.Slot == slot && PosMatch(e.X, e.Z, pos)) { match = e; break; }
                    if (match == null)
                    {
                        GD.PrintErr($"[MainScene] Unit sync: no ScenarioData.Units entry matched a delete at ({pos.X:F1},{pos.Z:F1}) slot {slot} — live store may have drifted.");
                        return null;
                    }
                    scen.Units = RemoveByIdentity(scen.Units, match, out _);
                    return match;
                }
            }
            return null;
        }

        /// <summary>Story 6.1 — resource-node sync callback fired by <see cref="EntityPlacer"/>. Matched by position
        /// (nodes have no slot). RemoveMatch returns the REAL authored entry so its economy fields survive undo.</summary>
        internal object? SyncResourceNode(EntityPlacer.ScenarioSyncOp op, object? handle,
                                         Vector3 pos, float supply, float rate, int maxGatherers)
        {
            var scen = _ctx.Scenario;
            if (scen == null) return null;
            switch (op)
            {
                case EntityPlacer.ScenarioSyncOp.Add:
                {
                    var entry = new ScenarioResourceNode { X = pos.X, Z = pos.Z, Supply = supply, Rate = rate, MaxGatherers = maxGatherers };
                    scen.ResourceNodes = AppendEntry(scen.ResourceNodes, entry);
                    return entry;
                }
                case EntityPlacer.ScenarioSyncOp.ReAdd:
                    if (handle is ScenarioResourceNode n) scen.ResourceNodes = AppendEntry(scen.ResourceNodes, n);
                    return handle;
                case EntityPlacer.ScenarioSyncOp.RemoveHandle:
                    scen.ResourceNodes = RemoveByIdentity(scen.ResourceNodes, handle as ScenarioResourceNode, out _);
                    return null;
                case EntityPlacer.ScenarioSyncOp.RemoveMatch:
                {
                    ScenarioResourceNode? match = null;
                    foreach (var e in scen.ResourceNodes ?? Array.Empty<ScenarioResourceNode>())
                        if (PosMatch(e.X, e.Z, pos)) { match = e; break; }
                    if (match == null)
                    {
                        GD.PrintErr($"[MainScene] ResourceNode sync: no ScenarioData.ResourceNodes entry matched a delete at ({pos.X:F1},{pos.Z:F1}) — live store may have drifted.");
                        return null;
                    }
                    scen.ResourceNodes = RemoveByIdentity(scen.ResourceNodes, match, out _);
                    return match;
                }
            }
            return null;
        }

        /// <summary>Story 6.6 — prop sync callback fired by <see cref="EntityPlacer"/>. Props are scenario-owned (no
        /// live store), so this is the sole writer of <c>ScenarioData.Props</c>. Matched by position on RemoveMatch.
        /// The <c>scale</c> is stored as null when 1 (the omit-when-default default). Rotation/scale/non-blocking props
        /// never touch sim state or either checksum; only a blocking prop's footprint reaches the load-time grid.</summary>
        internal object? SyncProp(EntityPlacer.ScenarioSyncOp op, object? handle,
                                  string propId, Vector3 pos, float rot, float scale, bool blocks)
        {
            var scen = _ctx.Scenario;
            if (scen == null) return null;
            switch (op)
            {
                case EntityPlacer.ScenarioSyncOp.Add:
                {
                    var entry = new ScenarioProp
                    {
                        PropId = propId, X = pos.X, Z = pos.Z, Rot = rot,
                        Scale = Mathf.IsEqualApprox(scale, 1f) ? (float?)null : scale,
                        BlocksPathing = blocks,
                    };
                    scen.Props = AppendEntry(scen.Props, entry);
                    return entry;
                }
                case EntityPlacer.ScenarioSyncOp.ReAdd:
                    if (handle is ScenarioProp p) scen.Props = AppendEntry(scen.Props, p);
                    return handle;
                case EntityPlacer.ScenarioSyncOp.RemoveHandle:
                    scen.Props = RemoveByIdentity(scen.Props, handle as ScenarioProp, out _);
                    return null;
                case EntityPlacer.ScenarioSyncOp.RemoveMatch:
                {
                    ScenarioProp? match = null;
                    foreach (var e in scen.Props ?? Array.Empty<ScenarioProp>())
                        if (PosMatch(e.X, e.Z, pos)) { match = e; break; }
                    if (match == null)
                    {
                        GD.PrintErr($"[MainScene] Prop sync: no ScenarioData.Props entry matched a delete at ({pos.X:F1},{pos.Z:F1}).");
                        return null;
                    }
                    scen.Props = RemoveByIdentity(scen.Props, match, out _);
                    return match;
                }
            }
            return null;
        }

        private static bool PosMatch(float x, float z, Vector3 pos)
            => Mathf.Abs(x - pos.X) <= SCENARIO_SYNC_EPS && Mathf.Abs(z - pos.Z) <= SCENARIO_SYNC_EPS;

        /// <summary>Story 6.1 — true when <paramref name="slot"/> is a declared <c>player_slot</c> in the scenario.
        /// The sync's Add path refuses to persist a placement for any other slot, mirroring the fail-closed
        /// <c>ScenarioValidator</c> gate so a placement can never invalidate the scenario for F5/Save.</summary>
        private static bool SlotDeclared(ScenarioData scen, int slot)
        {
            foreach (var p in scen.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>())
                if (p.Slot == slot) return true;
            return false;
        }

        /// <summary>Append one entry to a (possibly-null) scenario sub-array, returning a fresh array. Treats an
        /// explicitly-null array (a scenario JSON with an explicit <c>null</c> sub-array) as empty.</summary>
        private static T[] AppendEntry<T>(T[]? arr, T entry)
        {
            int n = arr?.Length ?? 0;
            var result = new T[n + 1];
            if (n > 0) Array.Copy(arr!, result, n);
            result[n] = entry;
            return result;
        }

        /// <summary>Remove <paramref name="entry"/> from a scenario sub-array by REFERENCE identity (not value),
        /// returning a fresh array. <paramref name="found"/> reports whether the reference was present.</summary>
        private static T[] RemoveByIdentity<T>(T[]? arr, T? entry, out bool found) where T : class
        {
            found = false;
            if (arr == null || arr.Length == 0 || entry == null) return arr ?? Array.Empty<T>();
            // Explicit reference identity (NOT Array.IndexOf, which would switch to value equality should these
            // scenario entry classes ever gain an Equals override / become records) — the handle must remove the
            // exact captured object, never a value-equal sibling.
            int idx = -1;
            for (int i = 0; i < arr.Length; i++)
                if (ReferenceEquals(arr[i], entry)) { idx = i; break; }
            if (idx < 0) return arr;
            var result = new T[arr.Length - 1];
            Array.Copy(arr, 0, result, 0, idx);
            Array.Copy(arr, idx + 1, result, idx, arr.Length - idx - 1);
            found = true;
            return result;
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

            // Story 2.4b (AC4): show P1's Crystal balance (the scarce resource — its store/SoA existed and is folded,
            // but was never displayed) and, when a caster is focused, its Energy pool — so a player can read WHY an
            // ability button is greyed (the same affordability inputs the command card's disable predicate reads).
            int p1Crystal = (int)_resources.Crystal[(int)Faction.Player1].ToFloat();

            int focusId = _ctx.Selection.FocusId;
            string energyLine = "";
            if (focusId >= 0 && _world.IsAlive(focusId) && _world.MaxEnergy[focusId] > Fixed.Zero)
                energyLine = $"\nEnergy: {_world.Energy[focusId].ToInt()} / {_world.MaxEnergy[focusId].ToInt()}";

            _ctx.ResourceLabel.Text =
                $"P1  {p1Ore,5} ore   {p1Crystal,4} crystal   {p1Sup}/{p1SupCap} supply\n" +
                $"P2  {p2Ore,5} ore   {p2Sup}/{p2SupCap} supply\n" +
                $"Nodes: {nodes}   Buildings: {bldgs}" +
                energyLine;

            // ── Controls strip: context-sensitive shortcut hints ──────────────
            if (_pendingBuildWorkerId >= 0)
            {
                string bName = _pendingBuildType switch
                {
                    BuildingType.CommandCenter => "Command Center",
                    BuildingType.Barracks      => "Barracks",
                    BuildingType.ArcheryRange  => "Archery Range",
                    BuildingType.SiegeWorkshop => "Siege Workshop",
                    BuildingType.Aviary        => "Aviary",
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
                BuildingType.Aviary        => "Aviary",
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

        /// <summary>Populate and display the victory/defeat panel with live match data. Review P1:
        /// <paramref name="winnerPlayer"/> == 0 means "no victor" (a LOST-only outcome — a single-active-faction
        /// preset loss latches only VERDICT_LOST) and renders the defeat/match-over form.</summary>
        internal void ShowGameOver(int winnerPlayer)
        {
            _gameOver = true;

            // Story 7.12 — the match has now FULLY resolved, so hide the non-terminal "DEFEATED — spectating until the
            // match ends" banner: the terminal game-over overlay replaces it (otherwise the banner's "until the match
            // ends" promise would contradict the overlay and linger until the return to Edit).
            if (_defeatBanner != null) _defeatBanner.Visible = false;

            // Notify chat before closing it.
            _ctx.ChatOverlay.AddSystemMessage(winnerPlayer > 0 ? $"Player {winnerPlayer} wins! GG" : "Match over — defeat. GG");

            // Finalise replay recording — match is over. Story 9.11: write the result trailer (winner + finalTick +
            // completed) so the browser shows the outcome, and capture the file path FIRST (StopRecording clears the
            // recorder handle) for the "Save Replay" affordance below. The file is always retained on disk.
            string? savedReplayPath = _ctx.ReplayRecorder?.FilePath;
            _ctx.MatchLifecycle.StopRecording(winnerPlayer, completed: true);

            // ── Gather stats ─────────────────────────────────────────────────
            ulong elapsedMs = _matchStartMs > 0 ? Time.GetTicksMsec() - _matchStartMs : 0;
            uint  totalSec  = (uint)(elapsedMs / 1000);
            string duration = $"{totalSec / 60}:{totalSec % 60:D2}";

            // Story 9.15 — render EVERY active slot (up to 8), not just P1/P2. The Godot-free GameOverSummary builder
            // emits one row per faction with a latched verdict (WON/LOST + kills/losses/built/ore + canonical color),
            // correct even on a non-contiguous active set.
            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(_matchStats, _host.WinState);

            // The winning factions (a team victory latches WON for every ally). Drives the sub-heading phrasing.
            var wonFactions = new System.Collections.Generic.List<GameOverSummary.GameOverRow>();
            foreach (GameOverSummary.GameOverRow r in rows) if (r.Won) wonFactions.Add(r);

            // The winner's canonical color for the sub-heading (the team representative when >1 ally won; defeat-gray on no victor).
            Color winnerColor = winnerPlayer > 0
                ? FactionPalette.ForFaction((Faction)winnerPlayer).ToColor()
                : new Color(0.8f, 0.2f, 0.2f);

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
            // Story 9.15 — VICTORY iff the LOCAL player's OWN faction latched WON. winnerPlayer is the team REPRESENTATIVE
            // (lowest WON slot), NOT the local seat — keying VICTORY off it would show DEFEAT to a winning ally on a higher
            // slot while that same player's stat row reads WON (the 2v2 contradiction). Resolve the local faction via the
            // policy-resolved, null-guarded accessor the sibling sites use (raw LocalFaction is stale offline-after-online
            // and NREs when Lockstep is null, since it is declared null!).
            var localFaction = _ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1;
            bool localWin = _host.WinState.Verdict[(int)localFaction] == WinStateStore.VERDICT_WON;
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
                // A TEAM win (>1 faction latched WON) is an ALLIED victory, not one player's — enumerate the WON factions
                // so the sub-line never contradicts the multi-row table. Single winner keeps the "Player N Wins!" phrasing;
                // winnerPlayer 0 = "no victor" (LOST-only outcome — the DEFEAT heading already applies).
                Text                = winnerPlayer <= 0 ? "No Victor — Match Over"
                                    : wonFactions.Count > 1
                                        ? $"Team Victory — {string.Join(", ", wonFactions.ConvertAll(w => w.Name))} Win!"
                                        : $"Player {winnerPlayer} Wins!",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            winner.AddThemeFontSizeOverride("font_size", 26);
            winner.AddThemeColorOverride("font_color", winnerColor);
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

            // ── Per-player stat table (Story 9.15 — one row per active slot, up to 8) ─────────────
            // Fixed-width column layout: Player | Result | Kills | Losses | Built | Ore. Replaces the former
            // two-column (P1/P2-only) grid so slots 3–8 render.
            void AddScoreRow(string c0, string c1, string c2, string c3, string c4, string c5,
                             Color color, int fontSize)
            {
                var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
                row.AddThemeConstantOverride("separation", 0);
                void Cell(string text, float width, HorizontalAlignment align)
                {
                    var lbl = new Label { Text = text, HorizontalAlignment = align, CustomMinimumSize = new Vector2(width, 0) };
                    lbl.AddThemeFontSizeOverride("font_size", fontSize);
                    lbl.AddThemeColorOverride("font_color", color);
                    row.AddChild(lbl);
                }
                Cell(c0, 120, HorizontalAlignment.Left);
                Cell(c1, 90,  HorizontalAlignment.Center);
                Cell(c2, 80,  HorizontalAlignment.Center);
                Cell(c3, 80,  HorizontalAlignment.Center);
                Cell(c4, 90,  HorizontalAlignment.Center);
                Cell(c5, 110, HorizontalAlignment.Center);
                vbox.AddChild(row);
            }

            // Header row (light gray) + one canonical-color-tinted row per active faction.
            AddScoreRow("Player", "Result", "Kills", "Losses", "Built", "Ore", Colors.LightGray, 16);
            foreach (GameOverSummary.GameOverRow r in rows)
            {
                Color rowColor = Color.Color8(r.ColorR, r.ColorG, r.ColorB, r.ColorA);
                AddScoreRow($"{r.ColorGlyph} {r.Name}", r.VerdictLabel,
                            $"{r.Kills}", $"{r.Losses}", $"{r.UnitsBuilt}", $"{r.OreMined:N0}",
                            rowColor, 18);
            }

            vbox.AddChild(new HSeparator());

            // ── Save Replay (Story 9.11) ──────────────────────────────────────
            // The auto-recorded .chmr is ALWAYS retained on disk; this affordance only renames/annotates it. Present
            // only when a recording was active (absent during replay playback, where there is no recorder).
            if (!string.IsNullOrEmpty(savedReplayPath))
            {
                var saveBtn = new Button { Text = "Save Replay", CustomMinimumSize = new Vector2(200, 40) };
                saveBtn.AddThemeFontSizeOverride("font_size", 16);
                string capturedPath = savedReplayPath!;
                saveBtn.Pressed += () => PromptSaveReplay(capturedPath);
                vbox.AddChild(saveBtn);
            }

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
            var summaryLine = new System.Text.StringBuilder();
            foreach (GameOverSummary.GameOverRow r in rows)
                summaryLine.Append($"  {r.Name}:{r.VerdictLabel} {r.Kills}k/{r.UnitsBuilt}u/{r.OreMined}ore");
            GD.Print($"[WinCondition] {(winnerPlayer > 0 ? $"Player {winnerPlayer} wins" : "Match over — no victor")} — {duration} —" +
                     $"{summaryLine}. Press F5 to return to Edit.");
        }

        /// <summary>Story 9.11 — the score-screen "Save Replay" affordance: a small rename dialog over the just-
        /// recorded .chmr. The file is already on disk; confirming just renames it (never re-encodes / discards).</summary>
        private void PromptSaveReplay(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            var dlg = new AcceptDialog { Title = "Save Replay", DialogHideOnOk = true };
            var vb  = new VBoxContainer();
            vb.AddThemeConstantOverride("separation", 8);
            vb.AddChild(new Label { Text = "Rename this replay:" });
            var edit = new LineEdit
            {
                Text              = System.IO.Path.GetFileNameWithoutExtension(path),
                CustomMinimumSize = new Vector2(320, 0),
            };
            vb.AddChild(edit);
            dlg.AddChild(vb);
            dlg.AddCancelButton("Cancel");
            dlg.Confirmed += () =>
            {
                string? error = RenameReplayFile(path, edit.Text);
                dlg.QueueFree();
                if (error != null) ShowReplayMessage("Save failed", error);
            };
            dlg.Canceled  += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(380, 150));
        }

        /// <summary>Rename a .chmr on disk to <paramref name="newBaseName"/> (invalid chars sanitized). NEVER clobbers
        /// a different existing replay ("a replay is never silently discarded"): returns a human-readable error on
        /// refusal, or null on success. A blank/failed rename leaves the original file in place.</summary>
        private static string? RenameReplayFile(string path, string newBaseName)
        {
            newBaseName = (newBaseName ?? "").Trim();
            if (newBaseName.Length == 0) return "Name cannot be empty.";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                newBaseName = newBaseName.Replace(c, '_');

            string dir  = System.IO.Path.GetDirectoryName(path) ?? "";
            string dest = System.IO.Path.Combine(dir, newBaseName + ".chmr");
            if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase)) return null; // unchanged — no-op
            if (System.IO.File.Exists(dest)) return "A replay with that name already exists.";
            try
            {
                System.IO.File.Move(path, dest, overwrite: false); // fail-closed: never overwrite another replay
                GD.Print($"[Replay] Saved replay as {dest}");
                return null;
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Replay] Rename failed: {e.Message}");
                return e.Message;
            }
        }

        /// <summary>Public entry for MatchLifecycleController to surface a replay load refusal/error (P6) — a corrupt/
        /// legacy/newer-format file or a scenario re-gate mismatch shows a dialog instead of a silently-inert Play.</summary>
        public void ShowReplayLoadError(string reason) => ShowReplayMessage("Cannot play replay", reason);

        /// <summary>Small info dialog for replay Save/Load feedback (P2/P6).</summary>
        private void ShowReplayMessage(string title, string text)
        {
            var dlg = new AcceptDialog { Title = title, DialogText = text };
            dlg.Confirmed += () => dlg.QueueFree();
            dlg.Canceled  += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(440, 150));
        }

        /// <summary>
        /// Story 1.9a (UX-DR64e / D10): show the TERMINAL desync-halt overlay. Reuses the GameOverOverlay root but
        /// is danger-styled and behaviorally terminal — the match has already stopped advancing (LockstepManager
        /// gates Flush on its _halted flag). DISTINCT from the recoverable stall banner (UX-DR28, a transient warn
        /// pill): this ends the match and offers only "Return to Menu". Voiced to the "Commander" (UX-DR65).
        /// Exact copy is the story's recommended default (Open Question #1).
        /// </summary>
        internal void ShowHalt(uint tick, uint canonicalHash, bool hasCanonical)
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

            // Mono status string (UX-DR65): show the canonical hash for an attributed DesyncAlert, else the tick
            // for a global Halt. Branch on hasCanonical — NOT "canonicalHash != 0" — because 0 is a valid 32-bit
            // checksum, so an attributed desync hashing to 0 must still render as "#00000000", not "@tick".
            var status = new Label
            {
                Text                = hasCanonical ? $"· desync · #{canonicalHash:X8}" : $"· desync · @tick {tick}",
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
        /// Story 3.10 (NFR-1 / UX-DR62 / UX-DR83): the in-place Edit↔Play reset-to-authored-start. Restores the sim
        /// world to a freshly-applied authored start WITHOUT a scene reload or host reconstruction, so BOTH F5 edges
        /// route through it — Edit→Play starts the sim from a clean authored board that reflects trigger edits made in
        /// Edit, and Play→Edit restores the authored board for editing. Sequence: (optional) snapshot live hero
        /// rows → re-validate the live edited scenario (fail-closed veto) → <c>ClearForReset</c> → re-apply the
        /// authored scenario (or the VALIDATED fallback mirror, Story 7.7) against the cleared host → re-mint the
        /// deployed hero profile (or the preserved snapshot) → recompute the start-state hash → fold in the
        /// lifecycle reset.
        ///
        /// <para>Fail-closed: if the edited <c>_ctx.Scenario</c> fails <see cref="ScenarioValidator"/> the reset ABORTS
        /// BEFORE clearing anything (world unchanged), surfaces the located error, and returns false so the caller
        /// vetoes the Edit→Play toggle — never entering Play on an invalid scenario (mirrors the F5 unit gate).</para>
        ///
        /// <param name="preserveHeroProgress">When false (default), re-mint the deployed profile's authored initial
        /// Level/Xp (discarding runtime growth). When true (persistence-test), snapshot the live HeroStore Level/Xp per
        /// stable HeroId before the clear and re-mint that snapshot. Pre-3.13 the two coincide (no runtime XP growth).</param>
        /// <returns>True when the reset completed (or was a clean no-op); false when an invalid scenario vetoed it.</returns>
        /// </summary>
        internal bool ResetToAuthoredStart(bool preserveHeroProgress)
        {
            // 1. Snapshot the live deployed hero's Level/Xp BEFORE anything clears (preserve path only). Pre-3.13 this
            //    equals the profile's authored values, but the seam is exercised now so Story 3.13 hooks in unchanged.
            // DW-27/DW-32: the plain-data capture is lifted into the Godot-free HeroHarvestResolver so the has-vs-fallback
            // decision is Tier-1 tested. Capture keys on the persisted Alive row (NOT Alive3_14) so a fallen hero stays
            // harvestable. The resolver reproduces the old inline scan byte-for-byte: None (Has=false) when no hero is
            // deployed or no live row matches. The harvest is stashed on SceneContext so the picker's Save/Overwrite reads
            // the REAL grown values (through the manifest shape); the snapshot re-mint below is additionally gated on the
            // persistence-test mode.
            var harvest = Definitions.HeroHarvestResolver.Capture(
                _host.Heroes, _host.Items, _host.ItemRegistry, _ctx.PendingHeroProfile);
            _ctx.Harvest = harvest;
            bool  haveSnapshot = harvest.Has && preserveHeroProgress;
            int   snapLevel    = harvest.Level;
            Fixed snapXp       = harvest.Xp;

            // 2. Fail-closed re-validation of the edited scenario (the Validated<> proof is not retained past boot).
            //    Validate BEFORE the clear so an invalid edit leaves the world entirely unchanged (AC: fail closed).
            Validated<ScenarioData> validated = default;
            bool hasScenario = _ctx.Scenario != null;
            if (hasScenario)
            {
                ValidationResult r = new ScenarioValidator().Validate(_ctx.Scenario!, _slotFactionDefs); // Story 6.8: authored-building-id gate
                if (!r.Ok)
                {
                    GD.PrintErr($"[Reset] Edited scenario failed validation — staying in Edit: {r.Error}");
                    ShowTriggerMessage($"Cannot enter Play — invalid scenario:\n{r.Error}", 5f);
                    return false; // veto: nothing cleared, world unchanged
                }
                validated = r.Value;
            }

            // 2b. Fail-closed roster-completeness gate (Story 14.4, DW-97). Mirrors the scenario veto above:
            //     validate BEFORE the clear so an incomplete faction leaves the world entirely unchanged, and
            //     return false so the caller stays in Edit. Threads _abilityRegistry so the
            //     signature_mechanic_effect_id resolution check fires at THIS launch gate (the boot-shadow and
            //     discovery paths deliberately stay registry-less — see FactionLaunchGate). The pure decision
            //     lives in FactionLaunchGate (Tier-1 testable); this layer only does the Godot side effects.
            string? factionBlock = Definitions.FactionLaunchGate.FirstIncompleteReason(_slotFactionDefs, _abilityRegistry);
            if (factionBlock != null)
            {
                GD.PrintErr($"[Reset] {factionBlock.Replace("\n", " ")} — staying in Edit");
                ShowTriggerMessage($"Cannot enter Play — {factionBlock}", 5f);
                return false; // veto: nothing cleared, world unchanged
            }

            // 3. Clear every store to its authored-start (post-ctor) state — in place, no host reconstruction.
            _host.ClearForReset();

            // 3a. DW-138: wipe the shared editor undo/redo history the instant ClearForReset re-mints the stores.
            //     This is past the fail-closed vetoes above, so every id-invalidating path (incl. the build-defect
            //     empty-board early-return below) is covered: ClearForReset + re-apply reassigns slot ids, so any
            //     surviving undo/redo closure would replay a stale-id op — and Story 6.1 routes those closures into
            //     ScenarioData, so a post-F5 Ctrl+Z/Y could strip or re-add a scenario entry that no longer matches
            //     the live entity, corrupting the persisted board. Clearing here kills that closure before it can run.
            _ctx.Placer.History.Clear();

            // 3b. DW-157 (Story 14.8): rebuild the static PathabilityGrid from the CURRENT edited ScenarioData and
            //     re-inject it into the SAME 3 sinks the boot path (ScenarioLoadPhase.BuildAndInjectPathabilityGrid)
            //     fans it out to. Boot builds this grid ONCE and the applier previously re-threaded its STALE cached
            //     grid on every re-apply, so a blocking prop / water volume / painted cell added, moved, or removed in
            //     Edit mode was walked straight through until a full reload. Routing through the ONE shared
            //     ScenarioApplier.BuildPathabilityGrid recipe (the same the boot path uses) guarantees byte-identical
            //     blocked cells for an unchanged model. The applier grid MUST be set BEFORE Apply (Apply threads
            //     _pathability into EntityWorld before any spawn). Reuse the applier's ALREADY-injected elevation grid
            //     for slope re-derivation — DW-157 is painted/prop/water only, terrain re-bake is out of scope. Skipped
            //     on the fallback path (fallback maps are flat, as at boot).
            if (hasScenario)
            {
                var grid = ScenarioApplier.BuildPathabilityGrid(_ctx.Scenario, _applier.ElevationGrid);
                _applier.SetPathabilityGrid(grid);           // → threaded into EntityWorld inside Apply (below)
                _ctx.FlowFieldSys?.SetStaticBlocked(grid?.Blocked); // routing half (OR'd in on RebuildObstacles)
                _ctx.Pathability = grid;                     // editor overlay
            }
            else
            {
                // Fallback re-apply is flat: the boot fallback path clears exactly these sinks
                // (ScenarioLoadPhase, "a REUSED applier must not carry a prior sculpted load's blocking"), and
                // ResetToAuthoredStart IS the reused-applier case — so clear them symmetrically here. Without this a
                // scenario→fallback transition would leave a prior scenario's blocked cells stranded on the sinks.
                // The boot fallback ALSO nulls the applier's ELEVATION grid; that clear is deliberately absent here:
                // _ctx.Scenario == null is only reachable via the boot fallback path itself (missing/unparseable/
                // rejected file), which already nulled the elevation grid — so it is provably null on this branch,
                // and the scenario branch above deliberately REUSES it (DW-157: terrain re-bake is out of scope).
                _applier.SetPathabilityGrid(null);
                _ctx.FlowFieldSys?.SetStaticBlocked(null);
                _ctx.Pathability = null;
            }

            // 4. Re-apply the authored scenario against the cleared host. ScenarioApplier.Apply is additive/non-
            //    idempotent, so it MUST run only after the clear; LoadScenario inside it rebuilds ScenarioDirector
            //    (trigger/timer/variable) state, making Edit-side trigger add/remove/enable live this Play.
            //    Story 7.7: the fallback branch routes through the SAME validated-mirror writer path as boot (the
            //    legacy un-tokened ApplyFallback is retired) — no apply path skips the validator anymore.
            if (hasScenario)
            {
                _applier.Apply(validated);
            }
            else
            {
                // Same validated-mirror recipe as boot, worker ids resolved by category from the slot defs (a
                // custom-faction fallback still spawns workers). Note: the boot fallback nulls the elevation/
                // pathability sinks BEFORE applying; this branch cleared pathability above and elevation is
                // provably already null here (see the comment on the fallback sink-clear branch).
                ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(_slotFactionDefs);
                ValidationResult fr = new ScenarioValidator().Validate(mirror, _slotFactionDefs);
                if (fr.Ok)
                {
                    _ctx.FallbackMirror = mirror;
                    _applier.Apply(fr.Value);
                }
                else
                {
                    // Build defect: the shipped mirror must always validate. ClearForReset already ran, so the
                    // world is EMPTY — surface it loudly and VETO (return false) instead of falling through and
                    // reporting a successful reset over an empty board (review follow-up).
                    GD.PrintErr($"[Reset] Fallback mirror REJECTED — applying nothing: {fr.Error}");
                    ShowTriggerMessage($"Reset failed — fallback map invalid (build defect):\n{fr.Error}", 8f);
                    return false;
                }
            }

            // 4b. DW-157 (Story 14.8): force the flow-field static obstacle mask to take effect THIS Play. The
            //     per-frame FlowFieldBridge.CheckBuildingChanges only rebuilds obstacles when the BUILDING set changed
            //     — an obstacle-only edit leaves buildings identical across the reset, so without this explicit rebuild
            //     the refreshed static mask injected above would never be OR'd into the obstacle map until a building
            //     placement/destruction happened to trigger a rebuild. Unconditional: on the fallback branch the mask
            //     was just cleared to null above, so this drops any prior scenario's stranded static marks too.
            _ctx.FlowFieldSys?.RebuildObstacles(_buildings);

            // 5. Re-mint the deployed hero as deterministic init state (non-additive: the store was just cleared).
            //    Discard path re-mints the profile's authored Level/Xp; preserve path re-mints the snapshot values.
            if (preserveHeroProgress && haveSnapshot && _ctx.PendingHeroProfile != null)
            {
                Definitions.PlayerProfile pending = _ctx.PendingHeroProfile;
                // DW-15: route the preserve snapshot through the SAME BuildProfile(... DeriveProfileShape() ...) seam Save
                // uses (HeroPickerOverlay.OnSave/OnOverwrite), so the re-mint honours the manifest-selected attributes
                // (only hero.level / hero.xp / hero.inventory the shape carries) instead of re-minting hardcoded level+xp
                // keys unconditionally. Defensive fallback: if the manifest/shape resolves null (a PendingHeroProfile
                // implies persistence was enabled, so this is not expected), keep the old hardcoded-Values snapshot so
                // behaviour never regresses.
                Definitions.PlayerProfileShape? shape =
                    (_ctx.Scenario ?? _ctx.FallbackMirror)?.PersistenceManifest?.DeriveProfileShape();
                Definitions.PlayerProfile snapProfile = shape != null
                    ? Definitions.HeroProfileLoader.BuildProfile(
                        pending.ProfileId, pending.HeroDefId, pending.FactionId, pending.DisplayName, pending.SignatureAbility,
                        snapLevel, snapXp, shape, harvest.Inventory ?? pending.Inventory)
                    : new Definitions.PlayerProfile
                    {
                        ProfileId        = pending.ProfileId,
                        HeroDefId        = pending.HeroDefId,
                        FactionId        = pending.FactionId,
                        DisplayName      = pending.DisplayName,
                        SignatureAbility = pending.SignatureAbility,
                        Values           = new System.Collections.Generic.List<Definitions.ProfileAttributeValue>
                        {
                            new("hero.level", snapLevel),
                            new("hero.xp", snapXp.Raw),
                        },
                        // harvest.Inventory is a captured List (CaptureInventory) or null; null ⇒ fall back to pending (a List) —
                        // the same `HarvestedHeroInventory ?? pending.Inventory` result as pre-extraction, now cast-free (the
                        // harvest carries the concrete List type) and identical to the shape branch's fallback above.
                        Inventory        = harvest.Inventory ?? pending.Inventory,
                    };
                Definitions.HeroProfileLoader.LoadInto(_host.Heroes, _applier.LastAppliedHeroes, snapProfile, _logSink, _host.World,
                    _host.Items, _host.ItemRegistry, _host.Modifiers, _host.ItemSys.UsableSlots,
                    ownerSlot: _ctx.Lockstep?.LocalFaction); // Story 3.16 + DW-13
            }
            else
            {
                Definitions.HeroProfileLoader.LoadInto(_host.Heroes, _applier.LastAppliedHeroes, _ctx.PendingHeroProfile, _logSink, _host.World,
                    _host.Items, _host.ItemRegistry, _host.Modifiers, _host.ItemSys.UsableSlots,
                    ownerSlot: _ctx.Lockstep?.LocalFaction); // Story 3.16: re-mint the deployed profile's persisted inventory + carried stat modifiers; DW-13: local player's placed hero only
            }

            // 6. Recompute + log the start-state hash so it reflects the re-applied board + re-minted heroes.
            ScenarioData? hashModel = _ctx.Scenario ?? _ctx.FallbackMirror;
            if (_ctx.ScenarioApplied && hashModel != null)
            {
                ulong h = Definitions.StartStateHash.Compute(hashModel, _host.Heroes);
                GD.Print($"[Reset] Reset-to-authored-start (algo v{Definitions.StartStateHash.AlgoVersion}): 0x{h:X16}");
            }

            // 7. Fold in the existing match-lifecycle/presentation reset (game-over, play frames, stats, replay, fog).
            ResetMatchOnReturnToEdit();
            return true;
        }

        /// <summary>
        /// Story 1.8c — the return-to-Edit match reset (formerly inline in SetupWinConditionUi's ModeChanged
        /// handler). Wired by WinConditionPhase via ctx.Scene; kept on MainScene because it touches the match-
        /// lifecycle state MainScene retains (_gameOver / _playFrames / _matchStartMs / _matchStats).
        /// </summary>
        internal void ResetMatchOnReturnToEdit()
        {
            // DW-138: wipe the shared editor undo/redo history on the return-to-Edit seam too. ResetToAuthoredStart
            // already clears it after ClearForReset for the offline F5 loop; this second site additionally covers the
            // online/replay path that returns directly to Edit via this method WITHOUT going through
            // ResetToAuthoredStart's ClearForReset. A double Clear() on the common (offline) path is a harmless no-op.
            _ctx.Placer.History.Clear();

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

            // Story 9.11: tear down the replay playback overlay + reset its control state.
            _ctx.ReplayControls?.SetActive(false);
            _replayPaused = false; _replaySpeed = 1; _replaySeekTo = -1; _replayPerspective = -1;

            // Reset spectator fog reveal + the Story 7.12 per-player defeat banner.
            if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = false;
            // Story 9.5 (review): reset the lockstep online-state on the way back to Edit. GoOnline/GoSpectate set
            // IsOnline/IsSpectator (and LocalFaction) but the LockstepManager is built ONCE at bootstrap and survives
            // every F5 Edit↔Play re-apply, and nothing else resets these flags — so without this a subsequent OFFLINE
            // F5 playtest in the same process would see IsOnline still true and EffectiveLocalFaction still resolve to
            // the prior match's (e.g. Player2) faction, breaking selection/command/minimap/worker-build. GoOffline is a
            // pure flag reset (no transport teardown); a following online match re-establishes everything via GoOnline.
            _ctx.Lockstep.GoOffline();
            // Story 9.5: retarget the fog viewer back to Player1 (the offline reference). The fog's _faction is
            // independent of the lockstep flags above, so it needs its own reset. OnMatchStart's SetViewer mutated
            // _faction to the online faction and nothing else resets it on the way back to Edit, so a subsequent
            // offline F5 would otherwise reveal the prior match's (e.g. Player2) vision. FogOfWarSystem.Reset() wipes
            // the Grid but NOT _faction — and it runs on the later Edit→Play transition AFTER OnMatchStart's SetViewer,
            // so this return-to-edit seam (not Reset) is the correct place to restore the default viewer.
            _ctx.Fog.SetViewer(Faction.Player1);
            _localEliminated = false;
            if (_defeatBanner != null) _defeatBanner.Visible = false;

            // Close chat overlay and tear down lockstep subscription.
            _ctx.ChatOverlay.Close();
            _ctx.ChatOverlay.Visible = false;

            // Close map browser if open.
            _ctx.ContentBrowser.Visible = false;
        }

        // ── Win Condition Check ───────────────────────────────────────────────
        // Story 7.11: the former per-frame, P1/P2-hardcoded CheckWinCondition switch is DELETED. Win evaluation now
        // lives in the deterministic sim-layer WinConditionSystem (server-checkable, byte-identical across peers);
        // presentation only reads the folded WinStateStore verdict in _Process (see there) to drive ShowGameOver.
        // MainScene holds NO win math.

        /// <summary>
        /// Story 7.12 — the LOCAL player has latched <see cref="WinStateStore.VERDICT_LOST"/> but the match is not yet
        /// fully resolved: flip to the existing RevealAll spectator view (mirroring the spectator pattern in
        /// <c>MatchLifecycleController</c> and <c>ResetMatchOnReturnToEdit</c>) and show a NON-terminal defeat banner.
        /// The sim keeps ticking (this is not <c>_gameOver</c>); only full resolution fires <see cref="ShowGameOver"/>.
        /// Idempotent — guarded by <c>_localEliminated</c> so it runs once per match.
        /// </summary>
        private void OnLocalPlayerEliminated()
        {
            _localEliminated = true;

            // Reveal the whole map so the eliminated player keeps watching (the existing spectator reveal toggle).
            if (_ctx.FogBridge != null) _ctx.FogBridge.RevealAll = true;

            // A persistent, non-terminal top-of-screen banner (distinct from the terminal game-over overlay). Created
            // lazily on the HUD canvas so it survives across matches; ResetMatchOnReturnToEdit hides it.
            if (_defeatBanner == null)
            {
                _defeatBanner = new Label
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Text                = "DEFEATED — spectating until the match ends",
                };
                _defeatBanner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
                _defeatBanner.OffsetTop    = 24;
                _defeatBanner.OffsetBottom = 68;
                _defeatBanner.AddThemeFontSizeOverride("font_size", 28);
                _defeatBanner.AddThemeColorOverride("font_color", new Color(0.9f, 0.25f, 0.25f));
                _ctx.UiCanvas.AddChild(_defeatBanner);
            }
            _defeatBanner.Visible = true;

            _ctx.ChatOverlay?.AddSystemMessage("You were eliminated — spectating until the match ends.");
            GD.Print("[WinCondition] Local player eliminated — spectating (RevealAll) until the match resolves.");
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

            // AR-3 / Story 5.1: source slotDefs from a registry instance instead of a locally-allocated array
            // (mirrors the client _Ready migration above). NOTE: this `factions` is a slot-storage source
            // only — it is NOT threaded into ServerBootstrap.Build below, which constructs its own separate
            // internal FactionRegistry(activeFactionCount) for checksum purposes (pre-existing, unchanged).
            // Only `slotDefs` (the array) crosses that boundary.
            var factions = new FactionRegistry(2);
            var slotDefs = factions.SlotDefinitions;
            slotDefs[(int)Faction.Player1] = p1Def;
            slotDefs[(int)Faction.Player2] = p2Def;

            // Damage table — mirror the client _Ready seeding (missing file → canonical Default).
            string dtAbs = ProjectSettings.GlobalizePath(DAMAGE_TABLE_JSON);
            var damageTable = System.IO.File.Exists(dtAbs) ? Combat.DamageTable.Load(dtAbs) : Combat.DamageTable.Default;

            // Ability registry — mirror the client _Ready seeding (Story 2.4b). Built from the SAME files; ServerBootstrap
            // resolves the slot defs' ability ids → identical ascending-Id indices (MP parity: a registry mismatch desyncs).
            string abilitiesAbs = ProjectSettings.GlobalizePath(ABILITIES_DIR);
            AbilityRegistry abilityRegistry = AbilityRegistry.LoadFromDirectory(
                abilitiesAbs, name => GD.Print($"[Abilities] skipped invalid {name}"));

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
                var f = FactionRegistry.ToFaction(slot.Slot);
                if ((int)f < 0 || (int)f >= slotDefs.Length) continue;
                string fAbs = ProjectSettings.GlobalizePath(slot.FactionJson);
                if (System.IO.File.Exists(fAbs)) slotDefs[(int)f] = FactionDefinition.LoadFromFile(fAbs);
            }

            // Story 9.7: derive N from the scenario's PlayerSlots.Count (the SAME clamp the client uses), so the
            // server's checksum span + the DedicatedServer's ExpectedPlayerCount match the client's FactionRegistry(N).
            int serverActivePlayers = ClampActivePlayers(model.PlayerSlots?.Length ?? 0);
            _serverExpectedPlayers = serverActivePlayers;

            // ServerBootstrap validates (fail-closed: invalid ⇒ null + log via the seam) and applies through the
            // shared spine. activeFactionCount mirrors the client's FactionRegistry(N) derived from the same scenario.
            SimulationHost? host = ServerBootstrap.Build(model, slotDefs, damageTable, _logSink,
                activeFactionCount: serverActivePlayers, abilityRegistry: abilityRegistry);
            if (host != null)
                GD.Print($"[ServerBootstrap] Validated server sim spine built + applied (AR-38), N={serverActivePlayers} players.");
            return host;
        }

        /// <summary>Story 9.7: the scenario-derived expected player count for the headless dedicated server (fed to
        /// <c>DedicatedServer.ExpectedPlayerCount</c>). Set by <see cref="BuildHeadlessServerSimHost"/>; defaults to 2
        /// when the scenario is missing (the server then runs as relay + quorum only).</summary>
        private int _serverExpectedPlayers = 2;

        /// <summary>Story 9.7: peek only the PlayerSlots count of a scenario file (a cheap raw parse, no validation)
        /// so the client can size its <c>FactionRegistry(N)</c> before ScenarioLoadPhase applies the validated model.
        /// Returns 0 on any missing/parse failure (→ ClampActivePlayers falls back to 2).</summary>
        private int PeekScenarioPlayerSlots(string scenarioPath)
        {
            // Story 9.7 (P5): uses ScenarioSerializer.LoadFromFile — the SAME loader the headless server derives N
            // from (BuildHeadlessServerSimHost), so client + server read the identical PlayerSlots.Count. On a
            // missing/parse failure we do NOT silently downgrade: LOG it (the log seam) and fall back to 2.
            try
            {
                string abs = ProjectSettings.GlobalizePath(scenarioPath);
                ScenarioData? model = ScenarioSerializer.LoadFromFile(abs);
                if (model == null)
                {
                    _logSink.Warn($"[MainScene] Scenario '{scenarioPath}' missing/parse-failed for N-derivation — defaulting to 2 players.");
                    return 0;
                }
                return model.PlayerSlots?.Length ?? 0;
            }
            catch (Exception e)
            {
                _logSink.Warn($"[MainScene] Scenario N-derivation threw for '{scenarioPath}' ({e.Message}) — defaulting to 2 players.");
                return 0;
            }
        }

        /// <summary>Story 9.7: clamp a scenario's raw PlayerSlots count into a legal SIM active-player count via the
        /// Godot-free, Tier-1-tested <see cref="ProjectChimera.Multiplayer.PlayerCountPolicy.SimActivePlayers"/>
        /// (fewer than 2 → 2, byte-identical to the pre-9.7 1v1; capped at <see cref="FactionRegistry.PLAYER_COUNT"/>).</summary>
        private static int ClampActivePlayers(int rawSlotCount)
            => ProjectChimera.Multiplayer.PlayerCountPolicy.SimActivePlayers(rawSlotCount);

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

#if DEBUG
        /// <summary>
        /// Story 1.9a (loopback smoke, DEBUG-only): parse "--autojoin ip:port" from the user cmdline args
        /// (after "--"). Returns the "ip:port" string, or null if absent.
        /// </summary>
        private static string? ParseAutoJoinArg()
        {
            var args = OS.GetCmdlineUserArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--autojoin") return args[i + 1];
            return null;
        }

        /// <summary>Story 1.9a (DEBUG-only): true if <paramref name="flag"/> is present in the user cmdline args (after "--").</summary>
        private static bool HasCmdArg(string flag)
        {
            foreach (var a in OS.GetCmdlineUserArgs()) if (a == flag) return true;
            return false;
        }

        /// <summary>
        /// Story 1.9a (DEBUG-only): a minimal on-screen marker for a windowed `-- --server` dedicated server
        /// (loopback smoke), so it is a visible, closeable window instead of an invisible port-holding ghost.
        /// </summary>
        private void ShowServerWindowMarker(int port)
        {
            GetWindow().Title = $"Chimera Dedicated Server — port {port}";
            var cl = new CanvasLayer();
            var bg = new ColorRect { Color = new Color(0.08f, 0.10f, 0.14f) };
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            cl.AddChild(bg);
            var lbl = new Label
            {
                Text                = $"DEDICATED SERVER (loopback)\nport {port}\n\nLeave this open during the test.\nClose this window to stop the server.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            lbl.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            lbl.AddThemeFontSizeOverride("font_size", 22);
            cl.AddChild(lbl);
            AddChild(cl);
        }
#endif

    }
}
