#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Sim.Tests.Golden;

namespace ProjectChimera.AotGoldens
{
    /// <summary>
    /// Unreal trial A6: the AOT golden replay gate (plan A section 4 A6). Run once as a JIT program (<c>dotnet run</c>), which writes
    /// <c>out/jit_reference.txt</c> (every sequence and value this exe computes) and checks the goldens; run again as the NativeAOT
    /// executable, which reads that reference and prints the gate line
    /// <c>jit_equal=N1/N1 golden_equal=N2/N2 trial=1440/1440 unknown_effect=fail_closed runtime=.NET 8.0.25 aot=1</c>.
    /// Exit 0 only when every check passed. Options: <c>--root &lt;godot dir&gt;</c>, <c>--jit-ref &lt;file&gt;</c>, <c>--emit &lt;file&gt;</c>,
    /// <c>--report &lt;file&gt;</c>.
    /// </summary>
    public static class Program
    {
        private const ulong TrialSeed = 0xC0FFEE1234567890UL;
        private const int TrialTicks = 1440;
        private const string TrialScenario = "trial_1000.json";
        private const string TrialOrders = "trial_1000.orders.csv";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string Fnv(IReadOnlyList<GoldenChecksumReplay.Sample> seq)
        {
            ulong h = 14695981039346656037UL;
            foreach (var s in seq)
            {
                h = (h ^ s.Tick) * 1099511628211UL;
                h = (h ^ s.Hash) * 1099511628211UL;
            }
            return h.ToString("X16", Inv);
        }

        private static string FirstDivergence(IReadOnlyList<GoldenChecksumReplay.Sample> expected, IReadOnlyList<GoldenChecksumReplay.Sample> actual)
        {
            var d = GoldenChecksumReplay.CompareSequences(expected, actual);
            return d is null ? "none" : $"tick {d.Value.Tick} expected 0x{d.Value.Expected:X8} actual 0x{d.Value.Actual:X8}";
        }

        private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>The godot/ directory (it holds resources/data): walk up from the current directory, then from the binary.</summary>
        private static string FindGodotDir()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "resources", "data", "factions"))
                        && Directory.Exists(Path.Combine(dir.FullName, "ProjectChimera.Sim.Tests", "Golden")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException("could not find godot/ (resources/data + ProjectChimera.Sim.Tests/Golden) above the current directory or the binary; pass --root");
        }

        /// <summary>The AotGoldens folder: walk up from the binary to the folder holding SimAotGoldens.csproj.</summary>
        private static string FindOwnDir(string godot) =>
            Path.Combine(godot, "ProjectChimera.Sim.Native", "AotGoldens");

        private static string RuntimeName()
        {
            string d = RuntimeInformation.FrameworkDescription; // ".NET 8.0.25" under both JIT and NativeAOT
            return d;
        }

        public static int Main(string[] args)
        {
            string? root = null, jitRef = null, emit = null, report = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root": root = args[++i]; break;
                    case "--jit-ref": jitRef = args[++i]; break;
                    case "--emit": emit = args[++i]; break;
                    case "--report": report = args[++i]; break;
                    default:
                        Console.Error.WriteLine("usage: SimAotGoldens [--root <godot dir>] [--jit-ref <file>] [--emit <file>] [--report <file>]");
                        return 64;
                }
            }

            bool aot = !RuntimeFeature.IsDynamicCodeSupported;
            string godot = Path.GetFullPath(root ?? FindGodotDir());
            string goldenDir = Path.Combine(godot, "ProjectChimera.Sim.Tests", "Golden");
            string trialDir = Path.Combine(godot, "ProjectChimera.Sim.Tests", "Trial");
            string outDir = Path.Combine(FindOwnDir(godot), "out");
            Directory.CreateDirectory(outDir);
            string runtime = RuntimeName();
            var rep = new StringBuilder();
            void Line(string s) { Console.WriteLine(s); rep.Append(s).Append('\n'); }

            Line($"runtime: {runtime} aot={(aot ? 1 : 0)} stj_reflection={(System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault ? 1 : 0)} " +
                 $"dynamic_code={(RuntimeFeature.IsDynamicCodeSupported ? 1 : 0)} algo={SimChecksum.AlgoVersion}");
            Line($"godot: {godot}");

            // ── 1. every sequence driver ───────────────────────────────────────────────────────────────────────────
            List<Row> rows = Drivers.All();
            var seqs = new Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>>();
            var runFailure = new Dictionary<string, string>();
            foreach (Row r in rows)
            {
                try { seqs[r.Name] = r.Run(); }
                catch (Exception ex) { runFailure[r.Name] = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
            }

            // ── 2. the single-value golden (hero start state) ─────────────────────────────────────────────────────────
            ulong heroStart = 0;
            string? heroStartFailure = null;
            try { heroStart = HeroStartStateScenario.Compute(); }
            catch (Exception ex) { heroStartFailure = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }

            // ── 3. the trial goldens (SimSession + the frozen orders CSV) ───────────────────────────────────────────
            var trial = new Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>>();
            var trialFailure = new Dictionary<string, string>();
            foreach (bool ai in new[] { false, true })
            {
                string key = ai ? "trial-1000-ai" : "trial-1000";
                try { trial[key] = RunTrial(godot, trialDir, ai); }
                catch (Exception ex) { trialFailure[key] = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
            }

            // ── 4. the known-kind fold (AOT must equal JIT) and the unknown kind (AOT must fail closed) ────────────────
            ulong knownFold = CanonicalFold.MixEffect(CanonicalFold.Offset, new DamageEffect(Fixed.FromInt(10), ProjectChimera.Combat.DamageType.Normal));
            string unknownEffect;
            try
            {
                ulong u = CanonicalFold.MixEffect(CanonicalFold.Offset, new ProbeFutureEffect(Fixed.FromInt(3)));
                unknownEffect = "folds_by_reflection:" + u.ToString("X16", Inv); // the JIT behaviour, unchanged by A2
            }
            catch (NotSupportedException ex) when (ex.Message.Contains("NativeAOT", StringComparison.Ordinal))
            {
                unknownEffect = "fail_closed";
            }
            catch (Exception ex)
            {
                unknownEffect = "unexpected:" + ex.GetType().Name;
            }

            // ── 5. the JIT reference: written by the JIT run, read by the AOT run ─────────────────────────────────────
            string refPath = Path.GetFullPath(jitRef ?? emit ?? Path.Combine(outDir, "jit_reference.txt"));
            var jit = new Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>>();
            var jitValues = new Dictionary<string, string>();
            bool haveJit = false;
            if (emit != null || !aot)
            {
                WriteReference(refPath, runtime, seqs, trial, heroStart, knownFold, unknownEffect);
                Line($"jit_reference written: {refPath}");
            }
            else
            {
                if (File.Exists(refPath))
                {
                    ReadReference(refPath, jit, jitValues);
                    haveJit = true;
                    Line($"jit_reference read: {refPath} sha256={Sha256Hex(File.ReadAllBytes(refPath))}");
                }
                else
                {
                    Line($"jit_reference MISSING: {refPath} (run `dotnet run -c Release` in {FindOwnDir(godot)} first)");
                }
            }

            // ── 6. compare ───────────────────────────────────────────────────────────────────────────────────────────
            int n1 = 0, n1ok = 0, n2 = 0, n2ok = 0, exactN = 0, exactOk = 0, driverN = 0, driverOk = 0;
            Line("");
            Line("row | kind | ticks | fnv | jit | golden | golden_sha256 | detail");
            foreach (Row r in rows)
            {
                string kind = r.Kind.ToString().ToLowerInvariant();
                if (!seqs.TryGetValue(r.Name, out var seq))
                {
                    n1++;
                    if (r.GoldenFile != null) n2++;
                    Line($"{r.Name} | {kind} | - | - | FAIL | {(r.GoldenFile != null ? "FAIL" : "n/a")} | - | driver threw {runFailure[r.Name]}");
                    continue;
                }

                string jitCell, detail = "";
                n1++;
                if (haveJit)
                {
                    if (jit.TryGetValue(r.Name, out var js) && GoldenChecksumReplay.CompareSequences(js, seq) is null) { n1ok++; jitCell = "ok"; }
                    else { jitCell = "FAIL"; detail += " jit_divergence=" + (jit.TryGetValue(r.Name, out var js2) ? FirstDivergence(js2, seq) : "no reference row"); }
                }
                else if (!aot) { n1ok++; jitCell = "self"; } // the JIT run is the reference: trivially equal to itself
                else jitCell = "FAIL";

                string goldenCell = "n/a", sha = "-";
                if (r.GoldenFile != null)
                {
                    n2++;
                    string path = Path.Combine(goldenDir, r.GoldenFile);
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        sha = Sha256Hex(bytes);
                        var golden = GoldenChecksumReplay.ParseGolden(bytes);
                        var div = GoldenChecksumReplay.CompareSequences(golden, seq);
                        if (div is null)
                        {
                            n2ok++;
                            goldenCell = "ok";
                            if (r.Kind == Kind.Exact) { exactN++; exactOk++; } else { driverN++; driverOk++; }
                        }
                        else
                        {
                            goldenCell = "FAIL";
                            detail += " golden_divergence=" + GoldenChecksumReplay.DescribeDivergence(div.Value);
                            if (r.Kind == Kind.Exact) exactN++; else driverN++;
                        }
                    }
                    catch (Exception ex)
                    {
                        goldenCell = "FAIL";
                        detail += " golden_error=" + ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
                        if (r.Kind == Kind.Exact) exactN++; else driverN++;
                    }
                }
                Line($"{r.Name} | {kind} | {seq.Count} | {Fnv(seq)} | {jitCell} | {goldenCell} | {sha} |{detail}");
            }

            // hero-start-state: a single value, compared with the golden and with the JIT value
            {
                n2++;
                string goldenCell = "FAIL", jitCell = "n/a", detail = "", sha = "-";
                if (heroStartFailure != null) detail = " threw " + heroStartFailure;
                else
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(Path.Combine(goldenDir, "hero-start-state.golden.txt"));
                        sha = Sha256Hex(bytes);
                        ulong? want = null;
                        foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line[0] == '#') continue;
                            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && parts[0] == "startstatehash")
                                want = ulong.Parse(parts[1], NumberStyles.HexNumber, Inv);
                        }
                        if (want == heroStart) { n2ok++; driverN++; driverOk++; goldenCell = "ok"; }
                        else { driverN++; detail = $" golden=0x{want:X16} actual=0x{heroStart:X16}"; }
                    }
                    catch (Exception ex) { driverN++; detail = " golden_error=" + ex.Message.Split('\n')[0]; }
                }
                n1++;
                if (haveJit)
                {
                    if (jitValues.TryGetValue("hero_start_state", out string? jv) && jv == heroStart.ToString("X16", Inv)) { n1ok++; jitCell = "ok"; }
                    else { jitCell = "FAIL"; detail += " jit=" + (jitValues.TryGetValue("hero_start_state", out string? jv2) ? jv2 : "missing"); }
                }
                else if (!aot) { n1ok++; jitCell = "self"; }
                else jitCell = "FAIL";
                Line($"hero-start-state | value | 1 | {heroStart:X16} | {jitCell} | {goldenCell} | {sha} |{detail}");
            }

            // perturb checks (the committed tests' "detected and located" teeth), run on the AOT code
            int perN = 0, perOk = 0;
            void Perturb(string name, string goldenFile, int ticks, Func<GoldenHarness>? build, int target)
            {
                perN++;
                try
                {
                    var golden = GoldenChecksumReplay.ParseGolden(File.ReadAllBytes(Path.Combine(goldenDir, goldenFile)));
                    const int k = 100;
                    Action<int, EntityWorld> perturb = (i, w) => { if (i == k) w.Health[target] = Fixed.FromRaw(w.Health[target].Raw + 1); };
                    var perturbed = build == null
                        ? GoldenChecksumReplay.RunAndRecord(ticks, perturb)
                        : GoldenChecksumReplay.RunAndRecord(ticks, perturb, build);
                    var d = GoldenChecksumReplay.CompareSequences(golden, perturbed);
                    bool ok = d is not null && d.Value.Tick == (uint)(k + 1) && d.Value.Expected != d.Value.Actual;
                    if (ok) perOk++;
                    Line($"perturb {name} k=100: {(ok ? "ok" : "FAIL")} located={(d is null ? "undetected" : "tick " + d.Value.Tick)}");
                }
                catch (Exception ex) { Line($"perturb {name}: FAIL threw {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
            }
            Perturb("golden-scenario", "golden-scenario.golden.txt", GoldenScenario.DefaultTicks, null, GoldenScenario.PerturbTargetId);
            Perturb("multifaction", "golden-multifaction.golden.txt", MultiFactionScenario.DefaultTicks, MultiFactionScenario.Build, MultiFactionScenario.PerturbTargetId);
            Perturb("multifaction8", "golden-multifaction8.golden.txt", MultiFaction8Scenario.DefaultTicks, MultiFaction8Scenario.Build, MultiFaction8Scenario.Player8UnitId);

            // trial
            int trialMain = 0, trialAi = 0;
            string trialJitMain = "n/a", trialJitAi = "n/a";
            foreach (string key in new[] { "trial-1000", "trial-1000-ai" })
            {
                bool isAi = key.EndsWith("-ai", StringComparison.Ordinal);
                string detail = "";
                int matched = 0;
                if (!trial.TryGetValue(key, out var ts)) detail = " threw " + trialFailure[key];
                else
                {
                    var golden = GoldenChecksumReplay.ParseGolden(File.ReadAllBytes(Path.Combine(goldenDir, key + ".golden.txt")));
                    int n = Math.Min(golden.Count, ts.Count);
                    for (int i = 0; i < n; i++)
                        if (golden[i].Tick == ts[i].Tick && golden[i].Hash == ts[i].Hash) matched++;
                    if (golden.Count != TrialTicks || ts.Count != TrialTicks) matched = Math.Min(matched, TrialTicks - 1); // a length mismatch never reads as 1440/1440
                    if (matched != TrialTicks) detail = " " + FirstDivergence(golden, ts);
                    string jc = "n/a";
                    if (haveJit) jc = jit.TryGetValue(key, out var js) && GoldenChecksumReplay.CompareSequences(js, ts) is null ? "ok" : "FAIL";
                    else if (!aot) jc = "self";
                    if (isAi) trialJitAi = jc; else trialJitMain = jc;
                    detail = $" fnv={Fnv(ts)} jit={jc}" + detail;
                }
                if (isAi) trialAi = matched; else trialMain = matched;
                Line($"{key} | {matched}/{TrialTicks} samples equal the committed golden |{detail}");
            }

            // known and unknown effect kinds
            string knownCell = "n/a";
            bool knownOk = true;
            if (haveJit)
            {
                knownOk = jitValues.TryGetValue("known_effect_fold", out string? kv) && kv == knownFold.ToString("X16", Inv);
                knownCell = knownOk ? "ok" : "FAIL";
            }
            Line($"known_effect_fold={knownFold:X16} jit_equal={knownCell}");
            Line($"unknown_effect_detail={unknownEffect} (expected under NativeAOT: fail_closed)");

            // ── 7. the gate line ─────────────────────────────────────────────────────────────────────────────────────
            string jitField = haveJit ? $"{n1ok}/{n1}" : !aot ? "reference" : $"missing/{n1}"; // the JIT leg is the reference: it has nothing to compare with
            Line("");
            Line($"breakdown: golden_exact={exactOk}/{exactN} golden_driver_or_value={driverOk}/{driverN} perturb_located={perOk}/{perN} " +
                 $"trial_ai={trialAi}/{TrialTicks} trial_jit_equal={trialJitMain} trial_ai_jit_equal={trialJitAi} known_effect_jit_equal={knownCell}");
            string gate = $"jit_equal={jitField} golden_equal={n2ok}/{n2} trial={trialMain}/{TrialTicks} unknown_effect={unknownEffect} runtime={runtime} aot={(aot ? 1 : 0)}";
            Line(gate);

            bool pass = n1ok == n1 && n2ok == n2 && trialMain == TrialTicks && trialAi == TrialTicks && perOk == perN && knownOk
                        && (aot ? unknownEffect == "fail_closed" : unknownEffect.StartsWith("folds_by_reflection", StringComparison.Ordinal))
                        && (haveJit || !aot);
            Line(pass ? "RESULT pass" : "RESULT FAIL");

            string reportPath = Path.GetFullPath(report ?? Path.Combine(outDir, aot ? "report_aot.txt" : "report_jit.txt"));
            File.WriteAllText(reportPath, rep.ToString(), new UTF8Encoding(false));
            return pass ? 0 : 1;
        }

        // ── trial: SimSession + the frozen CSV, exactly Trial1000GoldenTests.Simulate (G/Trial1000GoldenTests.cs:171-203) ──
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RunTrial(string godot, string trialDir, bool ai)
        {
            string scn = Path.Combine(trialDir, TrialScenario), ord = Path.Combine(trialDir, TrialOrders);
            SimSession s = SimSession.Build(godot, scn, TrialSeed, ai);
            OrderScript script = OrderScript.Load(ord);
            SimulationHost host = s.Host;
            host.ChecksumInterval = 1; // the goldens' cadence
            var seq = new List<GoldenChecksumReplay.Sample>(TrialTicks);
            host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            s.PreTick();
            int cursor = 0, applied = 0, dropped = 0;
            for (int t = 0; t < TrialTicks; t++)
            {
                script.ApplyDue(s, ref cursor, ref applied, ref dropped);
                s.Step();
            }
            return seq;
        }

        // ── the JIT reference file ───────────────────────────────────────────────────────────────────────────────────
        private static void WriteReference(string path, string runtime, Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>> seqs,
            Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>> trial, ulong heroStart, ulong knownFold, string unknownEffect)
        {
            var sb = new StringBuilder();
            sb.Append("# SimAotGoldens JIT reference (Unreal trial A6). Written by the JIT run, compared by the NativeAOT run.\n");
            sb.Append("# runtime: ").Append(runtime).Append("\n# algo: ").Append(SimChecksum.AlgoVersion.ToString(Inv)).Append('\n');
            sb.Append("val hero_start_state ").Append(heroStart.ToString("X16", Inv)).Append('\n');
            sb.Append("val known_effect_fold ").Append(knownFold.ToString("X16", Inv)).Append('\n');
            sb.Append("val unknown_effect ").Append(unknownEffect).Append('\n');
            void Seq(string name, IReadOnlyList<GoldenChecksumReplay.Sample> s)
            {
                sb.Append("seq ").Append(name).Append(' ').Append(s.Count.ToString(Inv)).Append('\n');
                foreach (var x in s) sb.Append(x.Tick.ToString(Inv)).Append(' ').Append(x.Hash.ToString("X8", Inv)).Append('\n');
            }
            foreach (var kv in seqs.OrderBy(k => k.Key, StringComparer.Ordinal)) Seq(kv.Key, kv.Value);
            foreach (var kv in trial.OrderBy(k => k.Key, StringComparer.Ordinal)) Seq(kv.Key, kv.Value);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void ReadReference(string path, Dictionary<string, IReadOnlyList<GoldenChecksumReplay.Sample>> seqs, Dictionary<string, string> values)
        {
            List<GoldenChecksumReplay.Sample>? cur = null;
            foreach (string raw in File.ReadAllText(path).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "val") { values[p[1]] = p[2]; cur = null; }
                else if (p[0] == "seq") { cur = new List<GoldenChecksumReplay.Sample>(int.Parse(p[2], Inv)); seqs[p[1]] = cur; }
                else cur!.Add(new GoldenChecksumReplay.Sample(uint.Parse(p[0], Inv), uint.Parse(p[1], NumberStyles.HexNumber, Inv)));
            }
        }
    }

    /// <summary>
    /// A fake "future" effect kind with no explicit CanonicalFold.MixEffect arm (the same shape as the probe in
    /// Validation/EffectFoldCompletenessTests.cs:183-207, which cannot be compiled here without xUnit). It reaches the DW-449
    /// reflection default arm: the JIT folds it by reflection (unchanged), NativeAOT must fail closed (A2). Inert.
    /// </summary>
    internal sealed class ProbeFutureEffect : LeafEffect
    {
        public readonly Fixed Magnitude;
        public readonly int Charges;

        public ProbeFutureEffect(Fixed magnitude, int charges = 3) : base(UnitTag.None)
        {
            Magnitude = magnitude;
            Charges = charges;
        }

        internal override void Apply(in EffectContext ctx) { }
    }
}
