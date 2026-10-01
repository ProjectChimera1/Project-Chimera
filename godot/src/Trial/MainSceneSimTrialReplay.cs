#nullable enable
#if DEBUG
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.UI; // GameMode

namespace ProjectChimera.Core
{
    /// <summary>
    /// Unreal trial A14 (plan A section 4 A14, measured and reported, not gating): the DEBUG-only full-MainScene replay of
    /// <c>trial_1000</c>. Launched windowed as
    /// <c>godot --path godot -- --sim-trial-replay &lt;abs .chmr&gt; --sim-trial-replay-scenario &lt;abs json&gt;
    /// --sim-trial-replay-out &lt;file&gt; [--sim-trial-checksum-every N]</c>.
    ///
    /// <para>It changes nothing in how a replay plays. <see cref="ArmSimTrialReplay"/> (called first thing from
    /// <see cref="ArmSimTrialProbe"/>, i.e. before the boot reads <see cref="ScenarioPath"/>) only fills the SAME static
    /// handoff the replay browser's Play uses (<see cref="PendingReplayPath"/> / <see cref="PendingReplayScenarioPath"/>,
    /// consumed at <c>MainScene.cs:484-490</c>), so the unmodified boot loads the scenario, runs
    /// <c>MatchLifecycleController.TryLoadReplay</c> (re-gates, wires the ReplayPlayer, sets the header's AI plan, enters
    /// Play) and MainScene's own <c>_Process</c> replay branch then steps the match at 1x wall-clock pace with every
    /// presentation system live (PathRequestSystem's frame-paced Move-&gt;Stop writer included, DW-681). Godot's own
    /// <c>[Checksum] tick=N hash=0x........</c> lines (MainScene's checksum sink, interval 60) are the measurement.</para>
    ///
    /// <para>The watcher it subscribes to <c>SceneTree.ProcessFrame</c> runs BEFORE MainScene's <c>_Process</c> each frame:
    /// on the first frame (tick 0, before any step) it writes the replay path's pre-tick hashes, and optionally sets the
    /// host's checksum interval (a pure read of sim state, plan A section 1; default: leave the game's 60); each frame it
    /// records how many ticks the previous frame stepped; once MainScene's replay branch reports the replay finished
    /// (<c>_ctx.ReplayPlayer == null</c>) it writes a summary, makes MainScene's per-frame callbacks inert and quits, so
    /// the offline free-run branch never steps past the replay. Inert without <c>--sim-trial-replay</c>.</para>
    /// </summary>
    public partial class MainScene
    {
        private const string REPLAY_ARG          = "--sim-trial-replay";
        private const string REPLAY_SCENARIO_ARG = "--sim-trial-replay-scenario";
        private const string REPLAY_OUT_ARG      = "--sim-trial-replay-out";
        private const string REPLAY_EVERY_ARG    = "--sim-trial-checksum-every";

        /// <summary>Arms the A14 replay when its arguments are present; returns true when armed (the A8b probe then
        /// stays disarmed).</summary>
        private bool ArmSimTrialReplay()
        {
            string? chmr = CmdArgValue(REPLAY_ARG);
            if (chmr == null) return false;
            string? scenario = CmdArgValue(REPLAY_SCENARIO_ARG);
            string? outPath  = CmdArgValue(REPLAY_OUT_ARG);
            if (string.IsNullOrEmpty(scenario) || string.IsNullOrEmpty(outPath))
            {
                GD.PrintErr($"[SimTrialReplay] {REPLAY_ARG} needs {REPLAY_SCENARIO_ARG} <json> and {REPLAY_OUT_ARG} <file>; quitting.");
                GetTree().Quit(2);
                return true;
            }
            int every = 0;
            string? everyArg = CmdArgValue(REPLAY_EVERY_ARG);
            if (everyArg != null && (!int.TryParse(everyArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out every) || every < 1))
            {
                GD.PrintErr($"[SimTrialReplay] bad {REPLAY_EVERY_ARG} '{everyArg}'; quitting.");
                GetTree().Quit(2);
                return true;
            }

            // The replay browser's handoff (MainScene.cs:484-490): ReplayPath + ScenarioPath for a fresh tick-0 load.
            PendingReplayPath         = chmr;
            PendingReplayScenarioPath = scenario;
            var watcher = new SimTrialReplayWatcher(this, chmr, scenario, outPath, every);
            // SceneTree.process_frame is emitted immediately BEFORE Node._process runs on every node, so the watcher
            // observes each frame before MainScene's _Process steps the sim.
            GetTree().ProcessFrame += watcher.OnProcessFrame;
            GD.Print($"[SimTrialReplay] armed: chmr={chmr} scenario={scenario} out={outPath} checksum_every=" +
                     (every > 0 ? every.ToString(CultureInfo.InvariantCulture) : "game default"));
            return true;
        }

        /// <summary>Per-frame observer of the A14 replay (see the class doc). Reads MainScene's state; writes only the
        /// checksum interval (when asked) and, after the replay has finished, the boot gate that keeps MainScene inert
        /// until the quit lands.</summary>
        private sealed class SimTrialReplayWatcher
        {
            private readonly MainScene _s;
            private readonly string _chmr, _scenario, _out;
            private readonly int _every;
            private bool _started, _done;
            private uint _lastTick;
            private long _frames, _multiStepFrames;
            private int _maxTicksPerFrame;
            private double _startMs;
            private readonly StringBuilder _sb = new(4096);

            public SimTrialReplayWatcher(MainScene s, string chmr, string scenario, string outPath, int every)
            {
                _s = s; _chmr = chmr; _scenario = scenario; _out = outPath; _every = every;
            }

            public void OnProcessFrame()
            {
                if (_done) return;
                try { Observe(); }
                catch (Exception e) { Finish(2, $"watcher exception: {e}"); }
            }

            private void Observe()
            {
                if (_s._bootAborted) { Finish(2, "boot aborted (fail-safe reload)"); return; }
                if (_s._bootPending || _s._host == null || _s._ctx == null) return; // boot not complete yet

                if (!_started)
                {
                    if (_s._ctx.ReplayPlayer == null) { Finish(2, "TryLoadReplay did not start a replay (see [Replay] lines)"); return; }
                    _started = true;
                    _startMs = Time.GetTicksMsec();
                    _lastTick = _s._host.CurrentTick;
                    WriteStart();
                    if (_every > 0) _s._host.ChecksumInterval = _every;
                    // MainScene's single checksum sink (MainScene.cs:582-586) reproduced line for line, plus the state at
                    // the trailer ticks 300/900/1440 (all multiples of the game's interval 60, so the sink fires right
                    // after those exact ticks): units/wide digests and the alive units per command state. The wide
                    // digest folds CommandState, which SimChecksum does not (plan A F40) and DW-681's writer moves.
                    _s._host.SetChecksumSink((tick, checksum) =>
                    {
                        _s._logSink.Info($"[Checksum] tick={tick} hash=0x{checksum:X8}");
                        if (_s._ctx.Lockstep.IsOnline) _s._ctx.Lockstep.SendChecksum(tick, checksum);
                        if (tick == 300 || tick == 900 || tick == 1440)
                            _sb.Append("# state.").Append(tick.ToString(CultureInfo.InvariantCulture)).Append(": ")
                               .Append(StateLine(_s._world, _s._host)).Append('\n');
                    });
                    GD.Print($"[SimTrialReplay] started at tick {_lastTick}, mode={_s._ctx.GameState.Mode}, " +
                             $"checksum_interval={_s._host.ChecksumInterval}");
                    return;
                }

                // Ticks the previous frame stepped (this node runs before MainScene, so the delta is one whole frame).
                uint now = _s._host.CurrentTick;
                int stepped = (int)(now - _lastTick);
                _lastTick = now;
                _frames++;
                if (stepped > 1) _multiStepFrames++;
                if (stepped > _maxTicksPerFrame) _maxTicksPerFrame = stepped;

                if (_s._ctx.ReplayPlayer == null) Finish(0, "replay finished");
                else if (_s._gameOver) Finish(0, "game over latched during replay (stepping stopped)");
            }

            private void WriteStart()
            {
                var h = _s._host!;
                AI.AiControlPlan plan = h.Ai.ControlPlan;
                void K(string k, string v) => _sb.Append("# ").Append(k).Append(": ").Append(v).Append('\n');
                var ver = Engine.GetVersionInfo();
                K("leg", "mainscene_replay");
                K("host", $"godot {(int)ver["major"]}.{(int)ver["minor"]}.{(int)ver["patch"]}");
                K("runtime", RuntimeInformation.FrameworkDescription);
                K("config", "Debug");
                K("dotnet_roll_forward_env", System.Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD") ?? "unset");
                K("chmr", _chmr);
                K("scenario", _scenario);
                K("mode_at_start", _s._ctx!.GameState.Mode.ToString());
                K("tick_at_start", h.CurrentTick.ToString(CultureInfo.InvariantCulture));
                K("rng_state_at_start", "0x" + h.World.Rng.State.ToString("X16", CultureInfo.InvariantCulture));
                K("ai_plan_mask", "0x" + plan.Mask.ToString("X2", CultureInfo.InvariantCulture));
                K("ai", plan.Mask == 0 ? "0" : "1");
                K("game_checksum_interval", h.ChecksumInterval.ToString(CultureInfo.InvariantCulture));
                K("checksum_every_override", _every > 0 ? _every.ToString(CultureInfo.InvariantCulture) : "none");
                ScenarioData? model = _s._ctx.Scenario;
                if (model != null && _s._ctx.ScenarioApplied)
                {
                    PreTickHashes pre = PreTickHashes.Compute(h, model, _s._slotFactionDefs, _s._damageTable, plan);
                    K("hash.start_state", Hex16(pre.StartState));
                    K("hash.canonical_model", Hex16(pre.CanonicalModel));
                    K("hash.content", Hex16(pre.Content));
                    K("hash.ruleset", Hex16(pre.Ruleset));
                    K("hash.agreement", Hex16(pre.Agreement));
                    K("hash.tick0", "0x" + pre.Tick0.ToString("X8", CultureInfo.InvariantCulture));
                }
                else K("hash.start_state", "unavailable (scenario not applied)");
                K("units_at_start", _s._world.AliveCount.ToString(CultureInfo.InvariantCulture));
                K("digest.0", Hex16(WorldDigest.UnitsDigest(_s._world)));
                K("wide.0", Hex16(WorldDigest.WideDigest(_s._world, h.Projectiles)));
                Flush();
            }

            private void Finish(int rc, string why)
            {
                _done = true;
                var h = _s._host;
                void K(string k, string v) => _sb.Append("# ").Append(k).Append(": ").Append(v).Append('\n');
                K("end_reason", why);
                if (h != null)
                {
                    K("tick_at_end", h.CurrentTick.ToString(CultureInfo.InvariantCulture));
                    K("alive_end", _s._world.AliveCount.ToString(CultureInfo.InvariantCulture));
                    K("verdict", (h.WinState.Verdict[(int)Faction.Player1] | (h.WinState.Verdict[(int)Faction.Player2] << 4))
                        .ToString(CultureInfo.InvariantCulture));
                }
                K("game_over", _s._gameOver ? "1" : "0");
                K("frames", _frames.ToString(CultureInfo.InvariantCulture));
                K("multi_step_frames", _multiStepFrames.ToString(CultureInfo.InvariantCulture));
                K("max_ticks_per_frame", _maxTicksPerFrame.ToString(CultureInfo.InvariantCulture));
                double sec = _started ? (Time.GetTicksMsec() - _startMs) / 1000.0 : 0;
                K("replay_wall_s", sec.ToString("F1", CultureInfo.InvariantCulture));
                K("fps_mean", (sec > 0 ? _frames / sec : 0).ToString("F1", CultureInfo.InvariantCulture));
                K("rc", rc.ToString(CultureInfo.InvariantCulture));
                try { Flush(); } catch (Exception e) { GD.PrintErr($"[SimTrialReplay] could not write {_out}: {e.Message}"); rc = 2; }
                _s._bootPending = true; // MainScene's per-frame callbacks stay inert until the quit lands
                GD.Print($"[SimTrialReplay] {why}; quitting rc={rc}");
                _s.GetTree().Quit(rc);
            }

            /// <summary>Same fields as tools/sim-trial/chmr's StateLine (the managed control prints the same line).</summary>
            private static string StateLine(EntityWorld w, Sim.SimulationHost h)
            {
                int alive = 0, move = 0, stop = 0, idle = 0, amove = 0, other = 0;
                for (int i = 0; i < w.HighWaterMark; i++)
                {
                    if (!w.IsAlive(i)) continue;
                    alive++;
                    switch (w.CommandState[i])
                    {
                        case UnitCommand.Move: move++; break;
                        case UnitCommand.Stop: stop++; break;
                        case UnitCommand.Idle: idle++; break;
                        case UnitCommand.AttackMove: amove++; break;
                        default: other++; break;
                    }
                }
                return string.Format(CultureInfo.InvariantCulture,
                    "digest=0x{0:X16} wide=0x{1:X16} alive={2} move={3} stop={4} idle={5} attackmove={6} other={7}",
                    WorldDigest.UnitsDigest(w), WorldDigest.WideDigest(w, h.Projectiles), alive, move, stop, idle, amove, other);
            }

            private void Flush()
            {
                string full = Path.GetFullPath(_out);
                string? dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, _sb.ToString(), new UTF8Encoding(false));
            }
        }
    }
}
#endif
