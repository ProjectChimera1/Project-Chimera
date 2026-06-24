#nullable enable
using Godot;
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

            // Checksum broadcasting is handled by the SINGLE SimulationHost sink set in _Ready (it forwards to
            // lockstep when ctx.Lockstep.IsOnline). Exactly one SetChecksumSink call per caller (D5).
            _ctx.Lockstep.OnDesync += (tick, local, remote) =>
                _ctx.Log.Warn($"[DESYNC] tick={tick} local=0x{local:X8} remote=0x{remote:X8}");

            // Story 1.9a (UX-DR64e / D10): the authoritative server's TERMINAL halt drives a terminal,
            // danger-styled overlay — visually + behaviorally distinct from the recoverable stall banner
            // (UX-DR28). The P2P OnDesync above is now dormant in server-authoritative online play (the server
            // consumes checksums), so this OnHalt is the live halt path. Offers only "Return to Menu".
            _ctx.Lockstep.OnHalt += (tick, canonical) => _ctx.Scene.ShowHalt(tick, canonical);

            _ctx.LobbyUi = new LobbyUi
            {
                NakamaHost     = _ctx.Scene.NakamaHost,
                NakamaPort     = _ctx.Scene.NakamaPort,
                NakamaKey      = _ctx.Scene.NakamaKey,
                GameServerIp   = _ctx.Scene.GameServerIp,
                GameServerPort = _ctx.Scene.GameServerPort
            };
            _ctx.Scene.AddChild(_ctx.LobbyUi);
            _ctx.LobbyUi.Initialize(_ctx.Transport);
            _ctx.LobbyUi.OnMatchStart += OnMatchStart;

            _ctx.ChatOverlay = new MatchChatOverlay();
            _ctx.Scene.AddChild(_ctx.ChatOverlay);
            // Chat is inactive until a match starts (Visible=false by default).
        }

        private void OnMatchStart(bool isHost, Faction localFaction)
        {
            // Wire path-request delegates to FlowFieldBridge — deterministic, no NavServer.
            _ctx.Lockstep.OnRequestPath        = (id, x, z) => _ctx.FlowFieldBridge.RequestPath(id, new Vector3(x, 0f, z));
            _ctx.Lockstep.OnRequestAttackMove  = (id, x, z) => _ctx.FlowFieldBridge.RequestAttackMove(id, new Vector3(x, 0f, z));
            _ctx.Lockstep.OnCancelPath         = id => _ctx.FlowFieldBridge.CancelPath(id);

            // Route SelectionSystem commands through lockstep
            _ctx.Selection.SetLockstep(_ctx.Lockstep);

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
                // Auto-start replay recording before entering lockstep.
                StartRecording();
                _ctx.Lockstep.GoOnline(localFaction);

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

                // Timestamp-based filename so each match gets a unique file.
                string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string filePath  = System.IO.Path.Combine(replayDir, $"{timestamp}_1v1.chmr");

                // Match seed: a fixed default for now (the real MP seed handshake is Epic 9). The EntityWorld's RNG
                // already starts at this value; record it so a replay restores the identical stream origin (D6).
                _ctx.ReplayRecorder = new ReplayRecorder(filePath, _ctx.Scene.ScenarioPath, EntityWorld.DEFAULT_RNG_SEED);
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

        /// <summary>Stop and finalise the active recorder, freeing the file handle.</summary>
        public void StopRecording()
        {
            if (_ctx.ReplayRecorder == null) return;

            _ctx.Lockstep.Recorder = null;
            _ctx.ReplayRecorder.Close();
            GD.Print($"[Replay] Saved → {_ctx.ReplayRecorder.FilePath}");
            _ctx.ReplayRecorder = null;

            if (_ctx.ReplayStatusLabel != null) _ctx.ReplayStatusLabel.Visible = false;
        }

        /// <summary>Load a replay file and enter Play mode for playback.</summary>
        public void TryLoadReplay(string filePath)
        {
            try
            {
                // ReplayPlayer reads the match seed from the header and reseeds World.Rng before the first tick
                // (v1 files → default seed), so the replayed RNG stream matches the recording.
                _ctx.ReplayPlayer = new ReplayPlayer(filePath, _ctx.World);
                _ctx.ReplayPlayer.OnRequestPath       = (id, x, z) => _ctx.FlowFieldBridge.RequestPath(id, new Vector3(x, 0f, z));
                _ctx.ReplayPlayer.OnRequestAttackMove = (id, x, z) => _ctx.FlowFieldBridge.RequestAttackMove(id, new Vector3(x, 0f, z));
                _ctx.ReplayPlayer.OnCancelPath        = id => _ctx.FlowFieldBridge.CancelPath(id);

                // The replay embeds the scenario path — warn if it differs from the currently-loaded scenario.
                if (_ctx.ReplayPlayer.ScenarioPath != _ctx.Scene.ScenarioPath)
                    GD.PrintErr($"[Replay] Scenario mismatch: replay was recorded on " +
                                $"'{_ctx.ReplayPlayer.ScenarioPath}' but loaded '{_ctx.Scene.ScenarioPath}'. " +
                                $"Set ScenarioPath in the Inspector to match for accurate playback.");

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
                GD.PrintErr($"[Replay] Failed to load '{filePath}': {e.Message}");
            }
        }
    }
}
