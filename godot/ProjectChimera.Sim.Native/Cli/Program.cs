#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.SimTrial
{
    /// <summary>gen would overwrite a different, frozen order list (exit 65).</summary>
    internal sealed class FrozenOrdersException : Exception
    {
        public FrozenOrdersException(string message) : base(message) { }
    }

    /// <summary>Console log sink: warnings to stderr, info dropped (the trace is the product).</summary>
    internal sealed class CliLog : ILogSink
    {
        public void Info(string message) { }
        public void Warn(string message) => Console.Error.WriteLine("[W] " + message);
    }

    /// <summary>Parsed command line (plan A section 4 A4: <c>--content --scenario --orders --seed --ticks --ai --out
    /// --digest-ticks --commit --dirty</c>).</summary>
    internal sealed class CliOptions
    {
        public string Mode = "";
        public string? Content;
        public string? Scenario;
        public string? Orders;
        public ulong Seed = 0xC0FFEE1234567890UL;
        public int Ticks = 1440;
        public bool Ai;
        public string? Out;
        public int[] DigestTicks = { 0, 300, 900, 1440 };
        public string? Commit;
        public string? Dirty;
        public bool Force;
    }

    /// <summary>
    /// Unreal trial A4: <c>SimTrialCli gen|run</c>, the managed reference leg (plan A section 3.6).
    /// <code>
    ///   gen  --scenario trial_1000.json --orders trial_1000.orders.csv [--ai] [--out trace.txt] [--force] ...
    ///        plays the scripted battle (TrialScript), writes the order list it issued to --orders (an existing file
    ///        with different bytes is the frozen input of the goldens: gen refuses, exit 65, unless --force), prints
    ///        "units=N orders=M verdict=V" and the checksum at ticks 1/60/300/ticks.
    ///   run  --scenario trial_1000.json --orders trial_1000.orders.csv [--ai] [--out trace.txt] ...
    ///        replays the frozen order list. gen's trace and run's trace of the same match are byte-identical.
    /// </code>
    /// Order of operations per tick t (identical in every leg): orders whose tick == t are applied in file order, then
    /// one step produces tick t + 1, then the checksum of the new state is read (<c>ComputeChecksumNow</c>, a pure
    /// read). <c>digest.&lt;t&gt;</c>/<c>wide.&lt;t&gt;</c> are taken after the step that produced tick t, before the
    /// orders of tick t (t = 0: the freshly built world). <c>verdict</c> is
    /// <c>Verdict[Player1] | Verdict[Player2] &lt;&lt; 4</c> (0 = both undecided).
    /// </summary>
    internal static class Program
    {
        private const string Usage =
            "usage: SimTrialCli gen|run --scenario <json> --orders <csv> [--content <godot dir>] [--seed <dec|0xhex>] " +
            "[--ticks N] [--ai] [--out <trace>] [--digest-ticks 0,300,900,1440] [--commit <sha>] [--dirty 0|1] [--force (gen)]";

        private static int Main(string[] args)
        {
            try
            {
                CliOptions o = Parse(args);
                return Execute(o);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                Console.Error.WriteLine(Usage);
                return 64;
            }
            catch (FrozenOrdersException e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 65;
            }
            catch (SimSessionBuildException e)
            {
                Console.Error.WriteLine($"error: session build failed ({e.Failure}): {e.Message}");
                return 3;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e);
                return 2;
            }
        }

        private static CliOptions Parse(string[] a)
        {
            if (a.Length == 0 || (a[0] != "gen" && a[0] != "run")) throw new ArgumentException("first argument must be gen or run");
            var o = new CliOptions { Mode = a[0] };
            for (int i = 1; i < a.Length; i++)
            {
                string k = a[i];
                string Next() => i + 1 < a.Length ? a[++i] : throw new ArgumentException(k + " needs a value");
                switch (k)
                {
                    case "--content": o.Content = Next(); break;
                    case "--scenario": o.Scenario = Next(); break;
                    case "--orders": o.Orders = Next(); break;
                    case "--seed": o.Seed = ParseSeed(Next()); break;
                    case "--ticks": o.Ticks = ParseInt(k, Next()); break;
                    case "--ai": o.Ai = true; break;
                    case "--out": o.Out = Next(); break;
                    case "--digest-ticks": o.DigestTicks = ParseTickList(Next()); break;
                    case "--commit": o.Commit = Next(); break;
                    case "--dirty": o.Dirty = Next(); break;
                    case "--force": o.Force = true; break;
                    default: throw new ArgumentException("unknown option " + k);
                }
            }
            if (o.Scenario == null) throw new ArgumentException("--scenario is required");
            if (o.Orders == null) throw new ArgumentException("--orders is required");
            if (o.Ticks < 1) throw new ArgumentException("--ticks must be at least 1");
            return o;
        }

        private static int ParseInt(string option, string s)
        {
            if (!int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                throw new ArgumentException($"{option}: '{s}' is not an integer");
            return v;
        }

        private static ulong ParseSeed(string s)
        {
            string t = s.Trim();
            bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(t.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v)
                : ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v);
            if (!ok) throw new ArgumentException($"--seed: '{s}' is not a 64-bit decimal or 0x-hex number");
            return v;
        }

        private static int[] ParseTickList(string s)
        {
            var list = new List<int>();
            foreach (string p in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                list.Add(ParseInt("--digest-ticks", p));
            return list.ToArray();
        }

        /// <summary>The <c>godot/</c> directory holding <c>resources/data</c>: walk up from the current directory,
        /// then from the binary.</summary>
        private static string FindContent()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "resources", "data"))) return dir.FullName;
                    string sub = Path.Combine(dir.FullName, "godot", "resources", "data");
                    if (Directory.Exists(sub)) return Path.Combine(dir.FullName, "godot");
                    dir = dir.Parent;
                }
            }
            throw new ArgumentException("could not find godot/resources/data; pass --content");
        }

        private static string Hex8(uint v) => v.ToString("X8", CultureInfo.InvariantCulture);
        private static string Hex16(ulong v) => "0x" + v.ToString("X16", CultureInfo.InvariantCulture);

        private static string Sha256Hex(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static int Execute(CliOptions o)
        {
            string content = Path.GetFullPath(o.Content ?? FindContent());
            string scenario = Path.GetFullPath(o.Scenario!);
            string ordersPath = Path.GetFullPath(o.Orders!);
            bool gen = o.Mode == "gen";

            SimSession session = SimSession.Build(content, scenario, o.Seed, o.Ai, new CliLog());
            SimulationHost host = session.Host;
            EntityWorld world = host.World;
            host.ChecksumInterval = 0; // the loop reads the checksum itself, every tick (ComputeChecksumNow is a pure read)

            PreTickHashes pre = session.PreTick();
            int unitsAtStart = world.AliveCount;

            TrialScript? script = gen ? new TrialScript(session) : null;
            OrderScript? replay = gen ? null : OrderScript.Load(ordersPath);
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
                if (gen) script!.IssueFor(t);
                else replay!.ApplyDue(session, ref cursor, ref applied, ref dropped);
                session.Step();
                if (host.CurrentTick != (uint)(t + 1)) throw new InvalidOperationException($"tick desync: expected {t + 1}, got {host.CurrentTick}");
                hashes[t] = host.ComputeChecksumNow();
                RecordDigest(t + 1);
            }

            OrderScript finalOrders;
            if (gen)
            {
                finalOrders = new OrderScript(script!.Rows);
                SaveOrders(finalOrders, ordersPath, o.Force);
                applied = script.Applied;
                dropped = script.Dropped;
            }
            else
            {
                finalOrders = replay!;
            }

            int verdict = VerdictCode(host);
            int aliveEnd = world.AliveCount;

            string commit = o.Commit ?? GitHead(content) ?? "unknown";
            string dirty = o.Dirty ?? GitDirty(content) ?? "1";

            string trace = BuildTrace(o, content, scenario, ordersPath, commit, dirty, pre, unitsAtStart, hashes, digests,
                finalOrders, applied, dropped, aliveEnd, verdict);
            if (o.Out != null)
            {
                string outPath = Path.GetFullPath(o.Out);
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, trace, new UTF8Encoding(false));
            }

            Console.WriteLine($"units={unitsAtStart} orders={finalOrders.Count} verdict={verdict}");
            foreach (int t in new SortedSet<int> { 1, 60, 300, 1440, o.Ticks })
                if (t >= 1 && t <= o.Ticks) Console.WriteLine($"tick {t} {Hex8(hashes[t - 1])}");
            Console.WriteLine($"applied={applied} dropped={dropped} alive_end={aliveEnd}");
            return 0;
        }

        /// <summary>gen writes the order list it issued. An existing file with the same bytes is left untouched; one
        /// with different bytes is the frozen input of the trial goldens, so gen refuses unless --force.</summary>
        private static void SaveOrders(OrderScript orders, string path, bool force)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(orders.Format());
            if (File.Exists(path))
            {
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
                if (!force)
                    throw new FrozenOrdersException(
                        $"{path} exists and differs from the order list this gen produced; it is the frozen input of the " +
                        "trial goldens. Write to another path, or pass --force to replace it (then re-record per the " +
                        "Trial1000GoldenTests rule).");
            }
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }

        private static int VerdictCode(SimulationHost host) =>
            host.WinState.Verdict[(int)Faction.Player1] | (host.WinState.Verdict[(int)Faction.Player2] << 4);

        private static string BuildTrace(CliOptions o, string content, string scenario, string ordersPath, string commit, string dirty,
            PreTickHashes pre, int unitsAtStart, uint[] hashes, SortedDictionary<int, (ulong Units, ulong Wide)> digests,
            OrderScript orders, int applied, int dropped, int aliveEnd, int verdict)
        {
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            var sb = new StringBuilder(hashes.Length * 14 + 2048);
            void H(string k, string v) => sb.Append("# ").Append(k).Append(": ").Append(v).Append('\n');
            H("leg", "cli");
            H("host", "sim-trial-cli");
            H("runtime", RuntimeInformation.FrameworkDescription);
            H("config", config);
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

        // ── Git stamp (used only when --commit / --dirty are not given) ─────────────────────────────────────────
        private static string? Git(string workDir, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("git", "-C \"" + workDir + "\" " + arguments)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using Process? p = Process.Start(psi);
                if (p == null) return null;
                string text = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? text : null;
            }
            catch (Exception) { return null; }
        }

        private static string? GitHead(string content) => Git(content, "rev-parse HEAD")?.Trim();

        /// <summary>"1" when <c>git status --porcelain -- godot tools/sim-trial</c> is non-empty (the rule publish.ps1
        /// stamps with), else "0"; null when git is unavailable.</summary>
        private static string? GitDirty(string content)
        {
            string? top = Git(content, "rev-parse --show-toplevel")?.Trim();
            if (string.IsNullOrEmpty(top)) return null;
            string? status = Git(top!, "status --porcelain -- godot tools/sim-trial");
            return status == null ? null : (status.Trim().Length == 0 ? "0" : "1");
        }
    }
}
