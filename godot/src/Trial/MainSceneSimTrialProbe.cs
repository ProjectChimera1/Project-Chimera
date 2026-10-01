#nullable enable
#if DEBUG
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.UI; // GameMode

namespace ProjectChimera.Core
{
    /// <summary>
    /// Unreal trial A8b (plan A §1, §3.6, §4 A8b): the DEBUG-only MainScene start-state probe. Launched windowed as
    /// <c>godot --path godot -- --sim-trial-probe &lt;abs scenario json&gt; --probe-out &lt;file&gt;</c> with
    /// <c>CHIMERA_MATCH_SEED</c> pinned (<see cref="MatchSeedProducer.PINNED_SEED_ENV"/>), it points
    /// <see cref="ScenarioPath"/> at the scenario BEFORE the boot reads it, lets the unmodified boot run (every setup
    /// phase, the boot apply), then enters Play through the same toggle F5 uses (<see cref="EnterPlayMode"/> →
    /// <c>WinConditionPhase</c>'s ModeChanged handler → <see cref="ResetToAuthoredStart"/>: AI plan, match seed, static
    /// blocked cells, re-apply, obstacle rebuild, hero re-mint, <c>MainScene.cs:2985-3075</c>). Right after that and
    /// before any <c>StepOnce</c> it writes <see cref="PreTickHashes"/> computed from MainScene's OWN loaded inputs
    /// (its host, applied scenario, per-slot faction defs, damage table and the AI plan the host actually holds) and
    /// quits. The same <see cref="PreTickHashes.Compute"/> helper the SimSession legs use, so only inputs can differ.
    ///
    /// <para>Inert without <c>--sim-trial-probe</c>: <see cref="ArmSimTrialProbe"/> returns false and nothing else in
    /// this file runs, so a normal launch behaves exactly as before. Not compiled into a release build.</para>
    ///
    /// <para>Output: one <c># key: value</c> line per field (the trace-header spelling of plan A §3.5) — the six H0 +
    /// tick0 keys compared by <c>tools/sim-trial/run_mainscene_probe.ps1</c> against <c>trial-1000-ai.meta.txt</c>,
    /// plus diagnostics (seed, plan, tick, counts, scenario sha256, units/wide digests at tick 0). Exit 0 when written,
    /// 2 when the probe could not reach a Play-mode tick-0 world.</para>
    /// </summary>
    public partial class MainScene
    {
        private const string PROBE_ARG     = "--sim-trial-probe";
        private const string PROBE_OUT_ARG = "--probe-out";

        /// <summary>Absolute scenario path from <c>--sim-trial-probe</c>; null on every normal launch.</summary>
        private string? _simTrialProbeScenario;

        /// <summary>Output file from <c>--probe-out</c>.</summary>
        private string? _simTrialProbeOut;

        /// <summary>Called from <c>_Ready</c> before the boot reads <see cref="ScenarioPath"/>. Returns true (and points
        /// <see cref="ScenarioPath"/> at the probe scenario) only when both probe arguments are present.</summary>
        private bool ArmSimTrialProbe()
        {
            string? scenario = CmdArgValue(PROBE_ARG);
            if (scenario == null) return false;
            string? outPath = CmdArgValue(PROBE_OUT_ARG);
            if (string.IsNullOrEmpty(outPath))
            {
                GD.PrintErr($"[SimTrialProbe] {PROBE_ARG} needs {PROBE_OUT_ARG} <file>; probe disabled, quitting.");
                GetTree().Quit(2);
                return false;
            }
            _simTrialProbeScenario = scenario;
            _simTrialProbeOut      = outPath;
            ScenarioPath = scenario; // GlobalizePath returns an absolute OS path unchanged
            GD.Print($"[SimTrialProbe] armed: scenario={scenario} out={outPath} " +
                     $"{MatchSeedProducer.PINNED_SEED_ENV}={System.Environment.GetEnvironmentVariable(MatchSeedProducer.PINNED_SEED_ENV) ?? "unset"}");
            return true;
        }

        /// <summary>Called at the end of <c>_Ready</c> (after the boot, before the first frame) when armed: enter Play
        /// the way F5 does, write the pre-tick hashes, quit. Never returns control to a ticking match.</summary>
        private void RunSimTrialProbe()
        {
            int rc;
            try
            {
                rc = WriteSimTrialProbe();
            }
            catch (Exception e)
            {
                GD.PrintErr($"[SimTrialProbe] FAILED: {e}");
                rc = 2;
            }
            // Keep the per-frame callbacks inert for the frame(s) before the quit lands, so no StepOnce runs after the
            // probe (the same gate the boot uses while the phase run is incomplete).
            _bootPending = true;
            GD.Print($"[SimTrialProbe] quitting rc={rc}");
            GetTree().Quit(rc);
        }

        private int WriteSimTrialProbe()
        {
            EnterPlayMode(); // GameState.Toggle → ModeChanged → ResetToAuthoredStart (offline, no replay)
            if (_ctx.GameState.Mode != GameMode.Play)
            {
                GD.PrintErr("[SimTrialProbe] Edit→Play was vetoed (see [Reset] lines above); no hashes written.");
                return 2;
            }
            ScenarioData? model = _ctx.Scenario;
            if (!_ctx.ScenarioApplied || model == null)
            {
                GD.PrintErr("[SimTrialProbe] the probe scenario was not applied (fallback map?); no hashes written.");
                return 2;
            }
            if (_host.CurrentTick != 0)
            {
                GD.PrintErr($"[SimTrialProbe] expected tick 0 before the first step, found {_host.CurrentTick}.");
                return 2;
            }

            AI.AiControlPlan plan = _host.Ai.ControlPlan; // the plan the sim runs (pushed at MainScene.cs:2990-2994)
            PreTickHashes pre = PreTickHashes.Compute(_host, model, _slotFactionDefs, _damageTable, plan);

            var ver = Engine.GetVersionInfo();
            var sb = new StringBuilder(2048);
            void H(string k, string v) => sb.Append("# ").Append(k).Append(": ").Append(v).Append('\n');
            H("leg", "mainscene_probe");
            H("host", $"godot {(int)ver["major"]}.{(int)ver["minor"]}.{(int)ver["patch"]}");
            H("runtime", RuntimeInformation.FrameworkDescription);
            H("config", "Debug");
            H("dotnet_roll_forward_env", System.Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD") ?? "unset");
            H("seed_env", System.Environment.GetEnvironmentVariable(MatchSeedProducer.PINNED_SEED_ENV) ?? "unset");
            H("algo", SimChecksum.AlgoVersion.ToString(CultureInfo.InvariantCulture));
            H("scenario", _simTrialProbeScenario ?? "");
            H("scenario_sha256", File.Exists(_simTrialProbeScenario)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_simTrialProbeScenario!))).ToLowerInvariant()
                : "missing");
            H("seed", Hex16(_ctx.LiveMatchSeed));
            H("rng_state", Hex16(_host.World.Rng.State));
            H("ai", plan.Mask == 0 ? "0" : "1");
            H("ai_plan_mask", "0x" + plan.Mask.ToString("X2", CultureInfo.InvariantCulture));
            H("ai_level", AiLevel.ToString());
            H("mode", _ctx.GameState.Mode.ToString());
            H("tick", _host.CurrentTick.ToString(CultureInfo.InvariantCulture));
            H("hash.start_state", Hex16(pre.StartState));
            H("hash.canonical_model", Hex16(pre.CanonicalModel));
            H("hash.content", Hex16(pre.Content));
            H("hash.ruleset", Hex16(pre.Ruleset));
            H("hash.agreement", Hex16(pre.Agreement));
            H("hash.tick0", "0x" + pre.Tick0.ToString("X8", CultureInfo.InvariantCulture));
            H("units_at_start", _world.AliveCount.ToString(CultureInfo.InvariantCulture));
            H("initial_delay", PreTickHashes.INITIAL_DELAY.ToString(CultureInfo.InvariantCulture));
            H("item_registry", _host.ItemRegistry.Count.ToString(CultureInfo.InvariantCulture));
            H("ability_registry_same_as_ctx", ReferenceEquals(_host.AbilityRegistry, _ctx.AbilityRegistry) ? "1" : "0");
            H("digest.0", Hex16(WorldDigest.UnitsDigest(_world)));
            H("wide.0", Hex16(WorldDigest.WideDigest(_world, _host.Projectiles)));

            string outPath = Path.GetFullPath(_simTrialProbeOut!);
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            GD.Print($"[SimTrialProbe] {pre}");
            GD.Print($"[SimTrialProbe] written: {outPath}");
            return 0;
        }

        private static string Hex16(ulong v) => "0x" + v.ToString("X16", CultureInfo.InvariantCulture);

        /// <summary>The value after <paramref name="flag"/> in the user cmdline args (after <c>--</c>), or null.</summary>
        private static string? CmdArgValue(string flag)
        {
            string[] args = OS.GetCmdlineUserArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }
    }
}
#endif
