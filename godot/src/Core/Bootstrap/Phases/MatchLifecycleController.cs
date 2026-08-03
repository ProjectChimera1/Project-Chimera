#nullable enable
using Godot;
using ProjectChimera.Core.Definitions; // CanonicalModelHash / RulesetHash (v4 replay header fields + re-gate)
using ProjectChimera.Multiplayer;
using ProjectChimera.UI;
using System;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Multiplayer" phase + Task 5 MatchLifecycleController (runtime position 17). Sets up the lockstep
    /// stack (transport / lockstep / lobby / chat) AND owns the match lifecycle: <see cref="OnMatchStart"/> (lobby
    /// callback), <see cref="StartRecording"/> / <see cref="StopRecording"/> (replay capture), and
    /// <see cref="TryLoadReplay"/> (Inspector replay autoload). Published as ctx.MatchLifecycle so _Ready's
    /// replay-autoload tail and MainScene's return-to-Edit reset / game-over can drive it. The SINGLE
    /// SimulationHost checksum sink stays owned by _Ready (D5) — it is never re-set here. Behavior-identical to the
    /// former MainScene SetupMultiplayer + OnMatchStart + StartRecording + StopRecording + TryLoadReplay.
    /// </summary>
    public sealed class MatchLifecycleController : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public MatchLifecycleController(SceneContext ctx) => _ctx = ctx;

        public string Name => "Multiplayer";

        public void Run()
        {
            _ctx.MatchLifecycle = this;

            _ctx.Transport = new ENetTransport();
            _ctx.Lockstep  = new LockstepManager(_ctx.Transport, _ctx.World);

            // Story 7.9: wire the sim-side authorized-enqueue handle for the custom-UI write rail UNCONDITIONALLY
            // (not only at online match start): the OFFLINE F5 playtest applies a button raise immediately through
            // the SAME sink, so it must be present without a lobby handshake. The delegate targets the stable
            // ScenarioDirector instance, so it survives every F5 Edit→Play re-apply.
            _ctx.Lockstep.DslEventSink = _ctx.Host.DslEventSink;

            // Story 11.2 (FR-66): wire the folded WinStateStore UNCONDITIONALLY (like DslEventSink above) — the OFFLINE
            // single-player Concede applies immediately through EnqueueConcede's apply-now branch and must latch the
            // conceding faction's verdict without a lobby handshake. The store is the stable SimulationHost instance, so
            // it survives every F5 Edit→Play re-apply. (OnMatchStart re-assigns the SAME instance for the online path.)
            _ctx.Lockstep.WinState = _ctx.Host.WinState;

            // Checksum broadcasting is handled by the SINGLE SimulationHost sink set in _Ready (it forwards to
            // lockstep when ctx.Lockstep.IsOnline). Exactly one SetChecksumSink call per caller (D5).
            _ctx.Lockstep.OnDesync += (tick, local, remote) =>
                _ctx.Log.Warn($"[DESYNC] tick={tick} local=0x{local:X8} remote=0x{remote:X8}");

            // Story 1.9a (UX-DR64e / D10): the authoritative server's TERMINAL halt drives a terminal,
            // danger-styled overlay — visually + behaviorally distinct from the recoverable stall banner
            // (UX-DR28). The P2P OnDesync above is now dormant in server-authoritative online play (the server
            // consumes checksums), so this OnHalt is the live halt path. Offers only "Return to Menu".
            _ctx.Lockstep.OnHalt += (tick, canonical, hasCanonical) => _ctx.Scene.ShowHalt(tick, canonical, hasCanonical);

            // Story 9.7: read the game-server + Nakama endpoint from versioned SettingsData (the Story 8.1
            // provider-config pattern), falling back to the MainScene Inspector exports when a field is unset
            // (empty string / 0 port). So a deployed build can point at a configured static endpoint without an
            // editor rebuild, while dev/editor runs keep the export defaults.
            var settings = _ctx.SettingsMgr?.Current;
            _ctx.LobbyUi = new LobbyUi
            {
                NakamaHost     = NonEmpty(settings?.NakamaHost,   _ctx.Scene.NakamaHost),
                NakamaPort     = Positive(settings?.NakamaPort,   _ctx.Scene.NakamaPort),
                NakamaKey      = NonEmpty(settings?.NakamaKey,    _ctx.Scene.NakamaKey),
                GameServerIp   = NonEmpty(settings?.GameServerIp, _ctx.Scene.GameServerIp),
                GameServerPort = Positive(settings?.GameServerPort, _ctx.Scene.GameServerPort),
                // Story 9.7: the N-slot grid + all-ready gate + matchmaker size derive from the loaded scenario.
                PlayerCount    = LobbyPlayerCount(),
            };
            _ctx.Scene.AddChild(_ctx.LobbyUi);
            _ctx.LobbyUi.Initialize(_ctx.Transport);
            _ctx.LobbyUi.OnMatchStart += OnMatchStart;

            // Story 9.7 / 9.6-carryover: subscribe the previously-unconsumed LockstepManager.OnPlayerDropped so an
            // in-match freeze-and-continue surfaces in the chat/roster ("Player <faction> dropped — slot frozen").
            // Presentation-only — the determinism truth is the merged stream the server keeps broadcasting.
            _ctx.Lockstep.OnPlayerDropped += (faction, applyAtTick) =>
            {
                _ctx.Log.Info($"[Drop] Player {faction} dropped — slot frozen (idle from tick {applyAtTick}).");
                if (_ctx.ChatOverlay is { Visible: true })
                    _ctx.ChatOverlay.AddSystemMessage($"Player {faction} dropped — slot frozen. Match continues.");
            };

            _ctx.ChatOverlay = new MatchChatOverlay();
            _ctx.Scene.AddChild(_ctx.ChatOverlay);
            // Chat is inactive until a match starts (Visible=false by default).
        }

        /// <summary>Story 9.7 (P3): the scenario-derived MATCHMAKER/LOBBY target — clamped to the transport SEAT
        /// ceiling (<see cref="PlayerCountPolicy.MpSeatCeiling"/> == ServerTransport.MAX_PLAYERS), never the larger
        /// sim faction ceiling: never queue/group/expect more players than the transport seats.</summary>
        private int LobbyPlayerCount()
            => PlayerCountPolicy.MpTargetPlayers(_ctx.Scenario?.PlayerSlots?.Length ?? 0);

        /// <summary>Prefer a non-empty configured string; else the export fallback.</summary>
        private static string NonEmpty(string? configured, string fallback)
            => string.IsNullOrEmpty(configured) ? fallback : configured!;

        /// <summary>Prefer a positive configured port; else the export fallback.</summary>
        private static int Positive(int? configured, int fallback)
            => configured is int v && v > 0 ? v : fallback;

        private void OnMatchStart(bool isHost, Faction localFaction)
        {
            // Story 9.4: put the client into server-dictated delay mode iff the match is server-driven (dedicated
            // server / online). In that mode the client is a pure delay follower — it never pings/proposes its own
            // delay change; the ONLY delay mutation is a server DelayDirective (the determinism invariant). In a P2P
            // host match this stays false and the DelayProposal negotiation remains the 2-player behavior.
            _ctx.Lockstep.ServerDictatedDelay = _ctx.LobbyUi.ServerDictated;

            // Wire path-request delegates to FlowFieldBridge — deterministic, no NavServer.
            _ctx.Lockstep.OnRequestPath        = (id, x, z) => _ctx.FlowFieldBridge.RequestPath(id, new Vector3(x, 0f, z));
            _ctx.Lockstep.OnRequestAttackMove  = (id, x, z) => _ctx.FlowFieldBridge.RequestAttackMove(id, new Vector3(x, 0f, z));
            _ctx.Lockstep.OnCancelPath         = id => _ctx.FlowFieldBridge.CancelPath(id);

            // Route SelectionSystem commands through lockstep
            _ctx.Selection.SetLockstep(_ctx.Lockstep);
            // Story 2.8 (D-1): route the command-card Train action through lockstep, and give the manager the
            // production system so a Train order EXECUTES at exec-tick (the deterministic spend + queue on the
            // canonical BuildingStore/ResourceStore). Same instance the replay/offline paths use → no divergence.
            _ctx.CommandCard.SetLockstep(_ctx.Lockstep);
            _ctx.Lockstep.Buildings = _ctx.BuildSys;
            // Story 3.15: give the manager the item runtime so an online UseItem / DropItem EXECUTES at exec-tick (the
            // deterministic charge/heal/ground-return) — the SAME ItemSystem instance the offline SelectionSystem uses.
            _ctx.Lockstep.Items = _ctx.Host.ItemSys;
            // Story 4.9: give the manager the research runtime so an online StartResearch / CancelResearch EXECUTES
            // at exec-tick (the deterministic spend/refund + progress on the canonical ResearchStore/ResourceStore) —
            // the SAME ResearchSystem instance the replay/offline paths use.
            _ctx.Lockstep.Research = _ctx.Host.ResearchSys;
            // Story 7.9: the DslEventSink is wired UNCONDITIONALLY in Run() (offline + online), so nothing to do here.
            // Story 2.12: also give the manager the event bus so a full-ring queued-order reject can emit OrderDenied
            // feedback on the online path (the same bus the offline SelectionSystem path uses); presentation-only.
            _ctx.Lockstep.CombatEvents = _ctx.CombatEvents;
            // Story 11.4 (FR-74): surface an ally's minimap ping on the reliable side-channel (presentation-only, never
            // folded). Remove-then-add keeps it idempotent if the manager instance is reused across matches.
            if (_ctx.MatchAlert != null)
            {
                _ctx.Lockstep.OnMapPingReceived -= _ctx.MatchAlert.HandleMpPing;
                _ctx.Lockstep.OnMapPingReceived += _ctx.MatchAlert.HandleMpPing;
            }
            // Story 11.2 (FR-66): give the manager the folded WinStateStore so a Concede order (online exec-tick OR the
            // offline apply-now branch) latches the conceding faction's VERDICT_LOST on the SAME store the replay path
            // uses. Same instance across live/offline/replay → a concede resolves identically everywhere.
            _ctx.Lockstep.WinState = _ctx.Host.WinState;

            // DW-17 / DW-225 (review): this session's EntityWorld is REUSED across matches — nothing on the online
            // entry path reloads the scene or reseeds the world (GoOnline/GoSpectate do not touch World.Rng), and the
            // offline AuthoredStart reset now leaves World.Rng on a per-match seed. An online match must start EVERY
            // peer's world on the SAME seed; today that is DEFAULT_RNG_SEED (there is no seed handshake yet — Epic 9).
            // ESTABLISH that invariant here rather than assuming it: pin LiveMatchSeed AND reseed the live world to it,
            // so the world, the recorded replay header, and every remote peer share one stream origin (covers both the
            // spectator and player branches below). Without the reseed, an offline F5 playtest earlier in the same
            // session would leave a per-match seed in World.Rng and desync this match. The Epic-9 handshake swaps
            // DEFAULT_RNG_SEED here for the agreed shared seed.
            _ctx.LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED;
            _ctx.World.Rng.Seed(_ctx.LiveMatchSeed);

            if (localFaction == Faction.Neutral)
            {
                // ── Spectator mode ────────────────────────────────────────────
                // Server assigned Neutral → we're an observer. Reveal the full map, observe both command streams.
                _ctx.FogBridge.RevealAll = true;
                _ctx.Lockstep.GoSpectate();

                if (_ctx.ReplayStatusLabel != null)
                {
                    _ctx.ReplayStatusLabel.Text    = "SPECTATING";
                    _ctx.ReplayStatusLabel.Visible = true;
                }

                GD.Print("[MainScene] Match started as SPECTATOR.");
            }
            else
            {
                // ── Player mode ───────────────────────────────────────────────
                // LiveMatchSeed + World.Rng were established to the shared online seed above (DEFAULT_RNG_SEED today),
                // so StartRecording records the exact seed the world runs on — behavior-identical to the pre-change
                // literal, but now an enforced invariant rather than an assumption.
                // Auto-start replay recording before entering lockstep.
                StartRecording();
                _ctx.Lockstep.GoOnline(localFaction);
                // Story 9.5: retarget this client's fog to its server-assigned faction so its OWN units light the fog
                // (a client in slot Player2..Player8 would otherwise reveal Player1's vision). Presentation-only — the
                // fog Grid is not folded into SimChecksum, so a per-client differing fog is correct RTS behaviour.
                _ctx.Fog.SetViewer(localFaction);

                GD.Print($"[MainScene] Match started as {localFaction}.");
            }

            // Enable chat overlay for this match (spectators can read but not send).
            _ctx.ChatOverlay.Visible = true;
            _ctx.ChatOverlay.Initialize(_ctx.Lockstep, localFaction);
            _ctx.ChatOverlay.AddSystemMessage("Match started. Press Enter to chat.");

            // Switch to Play mode
            if (_ctx.GameState.Mode != GameMode.Play)
                _ctx.GameState.SetMode(GameMode.Play);
        }

        /// <summary>
        /// Begin writing a replay file to the user data replays folder. Safe to call even if a recorder is already
        /// active (closes the old one first).
        /// </summary>
        public void StartRecording()
        {
            _ctx.ReplayRecorder?.Close();
            _ctx.ReplayRecorder = null;

            try
            {
                string replayDir = ProjectSettings.GlobalizePath("user://replays/");
                if (!System.IO.Directory.Exists(replayDir))
                    System.IO.Directory.CreateDirectory(replayDir);

                // Timestamp-based filename so each match gets a unique file. Story 9.7: player-count-aware suffix
                // (was the fixed "_1v1") derived from the loaded scenario's PlayerSlots — e.g. "_2p"/"_4p".
                string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                int players = _ctx.Scenario?.PlayerSlots?.Length ?? 0;
                if (players < 2) players = 2;
                string filePath  = System.IO.Path.Combine(replayDir, $"{timestamp}_{players}p.chmr");

                // Story 9.11 (v4 "replay v2"): compute the self-describing header fields — the canonical scenario
                // model hash (re-gated on playback), the ruleset (Effect-Graph caps) hash, this build's model
                // algo-version, and the per-slot roster. All are the SAME values already computed for the MP
                // handshake (MatchAgreementHash) — reuse, do not invent (Design Notes).
                ulong scenarioHash = _ctx.Scenario != null ? CanonicalModelHash.Compute(_ctx.Scenario) : 0UL;
                ulong rulesetHash  = RulesetHash.Compute();
                int slotCount = _ctx.Scenario?.PlayerSlots?.Length ?? 0;
                if (slotCount > FactionRegistry.PLAYER_COUNT) slotCount = FactionRegistry.PLAYER_COUNT;
                var roster = new Faction[slotCount];
                for (int i = 0; i < slotCount; i++) roster[i] = FactionRegistry.ToFaction(i);

                // Match seed (DW-225): record the seed the live world was actually seeded to at match start — the
                // single LiveMatchSeed seam, not a hardcoded literal — so a replay restores the identical stream origin
                // (D6). Online this equals DEFAULT_RNG_SEED (pinned in OnMatchStart below; all peers agree until the
                // Epic-9 seed handshake); the offline reset mints a per-match seed into the same field (DW-17).
                _ctx.ReplayRecorder = new ReplayRecorder(filePath, _ctx.Scene.ScenarioPath, _ctx.LiveMatchSeed,
                    scenarioHash, rulesetHash, CanonicalModelHash.AlgoVersion, roster);
                _ctx.Lockstep.Recorder = _ctx.ReplayRecorder;

                if (_ctx.ReplayStatusLabel != null)
                {
                    _ctx.ReplayStatusLabel.Text    = "◉ REC";
                    _ctx.ReplayStatusLabel.Visible = true;
                }

                GD.Print($"[Replay] Recording → {filePath}");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Replay] Failed to start recording: {e.Message}");
            }
        }

        /// <summary>Stop and finalise the active recorder as an INCOMPLETE recording (no winner) — used on the
        /// return-to-Edit reset and any mid-match teardown. The file is ALWAYS retained on disk (never discarded).</summary>
        public void StopRecording() => StopRecording(0, completed: false);

        /// <summary>Stop and finalise the active recorder, writing the result trailer (winner + finalTick + completed)
        /// so the browser can show the outcome. <paramref name="winnerFaction"/> is the 1-based player number
        /// (0 = no victor). The file is ALWAYS retained on disk regardless of outcome (Story 9.11).</summary>
        public void StopRecording(int winnerFaction, bool completed)
        {
            if (_ctx.ReplayRecorder == null) return;

            _ctx.Lockstep.Recorder = null;
            _ctx.ReplayRecorder.Close(winnerFaction, completed);
            GD.Print($"[Replay] Saved → {_ctx.ReplayRecorder.FilePath}");
            _ctx.ReplayRecorder = null;

            if (_ctx.ReplayStatusLabel != null) _ctx.ReplayStatusLabel.Visible = false;
        }

        /// <summary>Load a replay file and enter Play mode for playback.</summary>
        public void TryLoadReplay(string filePath)
        {
            try
            {
                // ReplayPlayer parses the v4 header (hard-rejecting pre-v4 / forward-incompatible files with an
                // InvalidDataException surfaced below), reads the match seed, and reseeds World.Rng before the first
                // tick so the replayed RNG stream matches the recording.
                var player = new ReplayPlayer(filePath, _ctx.World);

                // ── Story 9.11: fail-closed scenario RE-GATE (mirrors HandshakeGate.CheckStart) ──
                // Recompute the LOADED scenario's canonical hash and refuse to play unless it exactly matches the
                // hash embedded when the replay was recorded — never play a mismatched replay (silent desync). This
                // REPLACES the former soft GD.PrintErr path-mismatch warning.
                ulong loadedHash = _ctx.Scenario != null ? CanonicalModelHash.Compute(_ctx.Scenario) : 0UL;
                string? gateReason = ReplayPlayer.ScenarioGateBlockReason(player.ScenarioHash, loadedHash);
                if (gateReason != null)
                {
                    GD.PrintErr($"[Replay] Refused to play '{filePath}':\n{gateReason}");
                    if (_ctx.ReplayStatusLabel != null)
                    {
                        _ctx.ReplayStatusLabel.Text    = "REPLAY REFUSED (scenario mismatch)";
                        _ctx.ReplayStatusLabel.Visible = true;
                    }
                    _ctx.Scene.ShowReplayLoadError(gateReason); // P6 — surface the reason, never silently inert
                    _ctx.ReplayPlayer = null; // do not enter Play — zero ticks stepped
                    return;
                }

                _ctx.ReplayPlayer = player;
                _ctx.ReplayPlayer.OnRequestPath       = (id, x, z) => _ctx.FlowFieldBridge.RequestPath(id, new Vector3(x, 0f, z));
                _ctx.ReplayPlayer.OnRequestAttackMove = (id, x, z) => _ctx.FlowFieldBridge.RequestAttackMove(id, new Vector3(x, 0f, z));
                _ctx.ReplayPlayer.OnCancelPath        = id => _ctx.FlowFieldBridge.CancelPath(id);
                // Story 2.8 (D-1): give the replay the production system so a recorded Train order re-executes
                // identically to the live match (spend + queue on the canonical stores).
                _ctx.ReplayPlayer.Buildings = _ctx.BuildSys;
                // Story 3.15: give the replay the item runtime so a recorded UseItem / DropItem re-executes identically
                // to the live match (charge-decrement / heal / ground-return on the canonical stores).
                _ctx.ReplayPlayer.Items = _ctx.Host.ItemSys;
                // Story 4.9: give the replay the research runtime so a recorded StartResearch / CancelResearch
                // re-executes identically to the live match (spend/refund + progress on the canonical stores).
                _ctx.ReplayPlayer.Research = _ctx.Host.ResearchSys;
                // Story 7.9: give the replay the sim-side authorized-enqueue handle so a recorded (v3) button-originated
                // DslEvent order re-raises the custom event identically to the live match (same director + queue).
                _ctx.ReplayPlayer.DslEventSink = _ctx.Host.DslEventSink;
                // Story 11.2 (FR-66): give the replay the folded WinStateStore so a recorded Concede order latches the
                // conceding faction's VERDICT_LOST identically to the live match (the one-switch parity rule).
                _ctx.ReplayPlayer.WinState = _ctx.Host.WinState;

                if (_ctx.ReplayStatusLabel != null)
                {
                    _ctx.ReplayStatusLabel.Text    = "▶ REPLAY";
                    _ctx.ReplayStatusLabel.Visible = true;
                }

                // Enter Play mode so the sim runs.
                if (_ctx.GameState.Mode != GameMode.Play)
                    _ctx.GameState.SetMode(GameMode.Play);

                GD.Print($"[Replay] Playing back '{filePath}' ({_ctx.ReplayPlayer.TotalTicks} command events).");
            }
            catch (Exception e)
            {
                // P6: surface the reason (corrupt / legacy / newer-format / truncated) so Play is never silently inert.
                GD.PrintErr($"[Replay] Failed to load '{filePath}': {e.Message}");
                _ctx.ReplayPlayer = null;
                if (_ctx.ReplayStatusLabel != null)
                {
                    _ctx.ReplayStatusLabel.Text    = "REPLAY UNPLAYABLE — " + FirstLine(e.Message);
                    _ctx.ReplayStatusLabel.Visible = true;
                }
                _ctx.Scene.ShowReplayLoadError(e.Message);
            }
        }

        /// <summary>The first line of a (possibly multi-line) reason string — for the compact status label.</summary>
        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl);
        }
    }
}
