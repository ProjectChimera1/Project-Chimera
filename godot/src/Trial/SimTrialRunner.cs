#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Trial
{
    /// <summary>
    /// Unreal trial A8: the Godot leg of check (a) (plan A section 3.6). Runs the frozen <c>trial_1000</c> battle through
    /// <see cref="SimSession"/> and <see cref="OrderScript"/> inside Godot 4.6.3's hosted .NET runtime, the sim compiled
    /// by <c>godot.csproj</c> (GODOT defined, Debug), and writes the trace the comparator reads
    /// (<c>tools/sim-trial/compare_traces.py</c>, same format as the CLI leg plus <c>godot_define</c>).
    /// <para>Run: <c>GODOT --headless --path godot res://scenes/sim_trial.tscn -- --scenario S --orders O --out T
    /// [--content godot-dir] [--seed N] [--ticks N] [--ai] [--digest-ticks 0,300,900,1440] [--commit sha] [--dirty 0|1] [--leg name]</c>.
    /// Exit code: 0 ok, 2 any failure (also written to stderr), 64 bad arguments. The whole <c>_Ready</c> is one try/catch
    /// so a failure ends the process instead of leaving a headless Godot running.</para>
    /// Every tick's checksum is read with <c>ComputeChecksumNow</c> (a pure read); the host's own checksum sink also prints
    /// Godot's native <c>[Checksum] tick=N hash=0x........</c> line every 60 ticks, like the game does.
    /// </summary>
    public partial class SimTrialRunner : Node
    {
        /// <summary>Warnings to the Godot console; info dropped (the trace is the product).</summary>
        private sealed class RunnerLog : ILogSink
        {
            public void Info(string message) { }
            public void Warn(string message) => GD.Print("[W] " + message);
        }

        private sealed class ArgError : Exception
        {
            public ArgError(string message) : base(message) { }
        }

        /// <inheritdoc/>
        public override void _Ready()
        {
            int rc;
            try
            {
                rc = Run(ParseArgs(OS.GetCmdlineUserArgs()));
            }
            catch (ArgError e)
            {
                GD.PrintErr("error: " + e.Message);
                rc = 64;
            }
            catch (Exception e)
            {
                GD.PrintErr("error: " + e);
                rc = 2;
            }
            GetTree().Quit(rc);
        }

        private sealed class Options
        {
            public string? Content, Scenario, Orders, Out, Commit, Dirty, Leg;
            public ulong Seed = 0xC0FFEE1234567890UL;
            public int Ticks = 1440;
            public bool Ai;
            public int[] DigestTicks = { 0, 300, 900, 1440 };
        }

        private static Options ParseArgs(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i];
                string Next() => i + 1 < a.Length ? a[++i] : throw new ArgError(k + " needs a value");
                switch (k)
                {
                    case "--content": o.Content = Next(); break;
                    case "--scenario": o.Scenario = Next(); break;
                    case "--orders": o.Orders = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--commit": o.Commit = Next(); break;
                    case "--dirty": o.Dirty = Next(); break;
                    case "--leg": o.Leg = Next(); break;
                    case "--ai": o.Ai = true; break;
                    case "--seed": o.Seed = ParseSeed(Next()); break;
                    case "--ticks":
                        if (!int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out o.Ticks) || o.Ticks < 1)
                            throw new ArgError("--ticks must be a positive integer");
                        break;
                    case "--digest-ticks":
                        var list = new List<int>();
                        foreach (string p in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!int.TryParse(p.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int t))
                                throw new ArgError("--digest-ticks: '" + p + "' is not an integer");
                            list.Add(t);
                        }
                        o.DigestTicks = list.ToArray();
                        break;
                    default: throw new ArgError("unknown option " + k);
                }
            }
            if (o.Scenario == null) throw new ArgError("--scenario is required");
            if (o.Orders == null) throw new ArgError("--orders is required");
            if (o.Out == null) throw new ArgError("--out is required");
            return o;
        }

        private static ulong ParseSeed(string s)
        {
            string t = s.Trim();
            bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(t.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v)
                : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v);
            if (!ok) throw new ArgError("--seed: '" + s + "' is not a 64-bit decimal or 0x-hex number");
            return v;
        }

        private static string Hex8(uint v) => v.ToString("X8", CultureInfo.InvariantCulture);
        private static string Hex16(ulong v) => "0x" + v.ToString("X16", CultureInfo.InvariantCulture);
        private static string Sha256Hex(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private int Run(Options o)
        {
            // res:// globalized is the godot project dir that holds resources/data (what SimSession calls contentRoot).
            string content = Path.GetFullPath(o.Content ?? ProjectSettings.GlobalizePath("res://"));
            string scenario = Path.GetFullPath(o.Scenario!);
            string ordersPath = Path.GetFullPath(o.Orders!);

            SimSession session = SimSession.Build(content, scenario, o.Seed, o.Ai, new RunnerLog());
            SimulationHost host = session.Host;
            EntityWorld world = host.World;

            // Godot's native checksum line, every 60 ticks: the same sink shape MainScene installs. The per-tick hashes
            // below come from ComputeChecksumNow; the sink's value is cross-checked against them.
            var nativeLines = new List<(uint Tick, uint Hash)>();
            host.ChecksumInterval = 60;
            host.SetChecksumSink((tick, checksum) =>
            {
                nativeLines.Add((tick, checksum));
                GD.Print($"[Checksum] tick={tick} hash=0x{checksum:X8}");
            });

            PreTickHashes pre = session.PreTick();
            int unitsAtStart = world.AliveCount;
            OrderScript orders = OrderScript.Load(ordersPath);
            int cursor = 0, applied = 0, dropped = 0;

            var digestTicks = new SortedSet<int>(o.DigestTicks);
            var digests = new SortedDictionary<int, (ulong Units, ulong Wide)>();
            void RecordDigest(int t)
            {
                if (digestTicks.Contains(t))
                    digests[t] = (WorldDigest.UnitsDigest(world), WorldDigest.WideDigest(world, host.Projectiles));
            }

            var hashes = new uint[o.Ticks];
            RecordDigest(0);
            for (int t = 0; t < o.Ticks; t++)
            {
                orders.ApplyDue(session, ref cursor, ref applied, ref dropped);
                session.Step();
                if (host.CurrentTick != (uint)(t + 1)) throw new InvalidOperationException($"tick desync: expected {t + 1}, got {host.CurrentTick}");
                hashes[t] = host.ComputeChecksumNow();
                RecordDigest(t + 1);
            }

            foreach (var (tick, hash) in nativeLines)
                if (tick >= 1 && tick <= hashes.Length && hashes[tick - 1] != hash)
                    throw new InvalidOperationException($"native checksum sink tick {tick} = {Hex8(hash)} but ComputeChecksumNow = {Hex8(hashes[tick - 1])}");

            int verdict = host.WinState.Verdict[(int)Faction.Player1] | (host.WinState.Verdict[(int)Faction.Player2] << 4);
            int aliveEnd = world.AliveCount;
            string commit = o.Commit ?? "unknown";
            string dirty = o.Dirty ?? "1";

            string trace = BuildTrace(o, scenario, ordersPath, commit, dirty, pre, unitsAtStart, hashes, digests, orders, applied, dropped, aliveEnd, verdict);
            string outPath = Path.GetFullPath(o.Out!);
            string? dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outPath, trace, new UTF8Encoding(false));

            GD.Print($"units={unitsAtStart} orders={orders.Count} verdict={verdict}");
            GD.Print($"applied={applied} dropped={dropped} alive_end={aliveEnd} native_checksum_lines={nativeLines.Count}");
            GD.Print($"trace written: {outPath}");
            return 0;
        }

        private static string BuildTrace(Options o, string scenario, string ordersPath, string commit, string dirty,
            PreTickHashes pre, int unitsAtStart, uint[] hashes, SortedDictionary<int, (ulong Units, ulong Wide)> digests,
            OrderScript orders, int applied, int dropped, int aliveEnd, int verdict)
        {
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
#if GODOT
            const string godotDefine = "1";
#else
            const string godotDefine = "0";
#endif
            var ver = Engine.GetVersionInfo();
            string host = $"godot {(int)ver["major"]}.{(int)ver["minor"]}.{(int)ver["patch"]}";

            var sb = new StringBuilder(hashes.Length * 14 + 2048);
            void H(string k, string v) => sb.Append("# ").Append(k).Append(": ").Append(v).Append('\n');
            H("leg", o.Leg ?? (o.Ai ? "godot_ai" : "godot"));
            H("host", host);
            H("runtime", RuntimeInformation.FrameworkDescription);
            H("config", config);
            H("godot_define", godotDefine);
            H("dotnet_roll_forward_env", System.Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD") ?? "unset");
            H("commit", commit);
            H("dirty", dirty);
            H("algo", SimChecksum.AlgoVersion.ToString(CultureInfo.InvariantCulture));
            H("scenario_sha256", Sha256Hex(scenario));
            H("orders_sha256", Sha256Hex(ordersPath));
            H("seed", Hex16(o.Seed));
            H("ai", o.Ai ? "1" : "0");
            H("hash.start_state", Hex16(pre.StartState));
            H("hash.canonical_model", Hex16(pre.CanonicalModel));
            H("hash.content", Hex16(pre.Content));
            H("hash.ruleset", Hex16(pre.Ruleset));
            H("hash.agreement", Hex16(pre.Agreement));
            H("hash.tick0", "0x" + Hex8(pre.Tick0));
            H("units_at_start", unitsAtStart.ToString(CultureInfo.InvariantCulture));
            for (int t = 0; t < hashes.Length; t++)
                sb.Append((t + 1).ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Hex8(hashes[t])).Append('\n');
            foreach (var kv in digests)
            {
                H("digest." + kv.Key.ToString(CultureInfo.InvariantCulture), Hex16(kv.Value.Units));
                H("wide." + kv.Key.ToString(CultureInfo.InvariantCulture), Hex16(kv.Value.Wide));
            }
            H("orders_digest", Hex16(orders.Digest()));
            H("orders_applied", applied.ToString(CultureInfo.InvariantCulture));
            H("orders_dropped", dropped.ToString(CultureInfo.InvariantCulture));
            H("alive_end", aliveEnd.ToString(CultureInfo.InvariantCulture));
            H("verdict", verdict.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
