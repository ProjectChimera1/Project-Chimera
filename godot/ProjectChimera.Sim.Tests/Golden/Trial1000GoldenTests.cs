#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Unreal trial A4 (plan A section 3.3, section 4 A4): the goldens of scenario <c>trial_1000</c> (500 v 500
    /// units, 1,440 ticks, 2,077 frozen orders) and their meta sidecars. The match is built by
    /// <see cref="SimSession"/> and driven by the frozen order list (<c>Trial/trial_1000.orders.csv</c>, produced once
    /// by <c>SimTrialCli gen</c>), exactly as every other leg of the trial replays it.
    ///
    /// <para>Two variants: <c>main</c> (AI control plan None, compared on every OS; see its test for the float caveat) and <c>ai</c>
    /// (<see cref="ProjectChimera.AI.AiControlPlan.OfflineDefault"/>; the AI scores with <c>float</c>, so like
    /// <see cref="AiActiveGoldenTests"/> it is same-machine-deterministic but cross-platform-suspect and its
    /// golden-match is WINDOWS-GATED). Each golden has a sidecar <c>Golden/trial-1000{,-ai}.meta.txt</c> carrying
    /// the pre-tick hashes (H0), the file hashes and the start-state facts that every leg's trace header must equal,
    /// and a sidecar <c>Golden/trial-1000{,-ai}.trailer.txt</c> carrying the digests and end counts that every leg's
    /// trace trailer must equal (<c>ST/compare_traces.py</c>).</para>
    ///
    /// <para><b>RE-RECORD RULE.</b> The trial uses the shipped faction, ability and damage content, so a balance edit
    /// moves these goldens. Re-record (CHIMERA_GOLDEN_RECORD=1, filter <c>Trial1000</c>) ONLY when <c>hash.content</c>
    /// changed in the same commit; otherwise a mismatch is a determinism bug. The failure messages say which case
    /// applies.</para>
    /// </summary>
    public class Trial1000GoldenTests
    {
        private const string MainGolden = "trial-1000.golden.txt";
        private const string AiGolden   = "trial-1000-ai.golden.txt";
        private const string MainMeta   = "trial-1000.meta.txt";
        private const string AiMeta     = "trial-1000-ai.meta.txt";
        private const string MainTrailer = "trial-1000.trailer.txt";
        private const string AiTrailer   = "trial-1000-ai.trailer.txt";
        private static readonly int[] DigestTicks = { 0, 300, 900, 1440 };
        private const ulong Seed  = 0xC0FFEE1234567890UL;
        private const int   Ticks = 1440;
        private const string Map = "trial_1000.json";
        private const string Orders = "trial_1000.orders.csv";

        private static readonly GoldenChecksumReplay.GoldenHeader MainHeader = new(
            "trial_1000 golden-checksum baseline (Unreal trial A4) - AI control plan None",
            "Pins the SimChecksum sequence of Trial/trial_1000.json (500 v 500, seed 0xC0FFEE1234567890) built by SimSession and driven by the frozen Trial/trial_1000.orders.csv (2,077 orders), one StepOnce per tick for 1,440 ticks at ChecksumInterval=1. Header fields (H0, file hashes) are in trial-1000.meta.txt.",
            $"only when hash.content changed in the same commit (shipped content edit); a mismatch with an unchanged hash.content is a determinism bug. set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~Trial1000`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        private static readonly GoldenChecksumReplay.GoldenHeader AiHeader = new(
            "trial_1000 golden-checksum baseline (Unreal trial A4) - AI control plan OfflineDefault - SAME-MACHINE ONLY; EXCLUDED from the Win-Linux gate",
            "Same match as trial-1000.golden.txt with the OfflineDefault AI control plan (the AI scores with float, AiOpponentSystem.cs: PROVEN = same-machine determinism; NOT PROVEN = cross-platform). The frozen orders are replayed as written, so orders aimed at units the AI-variant battle killed earlier are dropped by OrderApplier (deterministic). Windows-gated like ai-active-scenario.golden.txt.",
            $"only when hash.content changed in the same commit; a mismatch with an unchanged hash.content is a determinism bug. set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~Trial1000` on Windows, then `dotnet build` and commit. DO NOT hand-edit.");

        // ── The two matches, each simulated once per test process ───────────────────────────────────────────────

        private sealed record Run(
            IReadOnlyList<GoldenChecksumReplay.Sample> Samples, PreTickHashes Pre, int UnitsAtStart, int AliveAt300,
            int AliveEnd, int Verdict, int Applied, int Dropped, int OrderRows, bool ChecksumReadMatchesFold,
            ulong RngBeforeSeed, string ScenarioSha, string OrdersSha, int ItemCount, ulong OrdersDigest,
            IReadOnlyList<(int Tick, ulong Units, ulong Wide)> Digests);

        private static readonly Lazy<Run> MainRun = new(() => Simulate(ai: false));
        private static readonly Lazy<Run> AiRun   = new(() => Simulate(ai: true));

        private static readonly Lazy<(string Scenario, string Orders)> Files = new(ExtractFiles);

        private static string ContentRoot()
            => Directory.GetParent(RealContentFixture.DataDir("factions"))!.Parent!.Parent!.FullName;

        private static byte[] ReadResource(string fileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? name = asm.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
            if (name == null)
                throw new InvalidOperationException($"'{fileName}' is not embedded in the test assembly (found: {string.Join(", ", asm.GetManifestResourceNames().Where(n => n.Contains("rial")))})");
            using Stream s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>The embedded scenario and order list, written byte-for-byte to a temp directory (the loaders take
        /// file paths). The directory is named by the content hash of the two files, so every test process reuses the
        /// same one (nothing accumulates in %TEMP%); a file is rewritten only when its bytes differ.</summary>
        private static (string Scenario, string Orders) ExtractFiles()
        {
            byte[] map = ReadResource(Map), orders = ReadResource(Orders);
            string key = Convert.ToHexString(SHA256.HashData(map.Concat(orders).ToArray())).Substring(0, 16).ToLowerInvariant();
            string dir = Path.Combine(Path.GetTempPath(), "chimera-trial1000-" + key);
            Directory.CreateDirectory(dir);
            string scn = Path.Combine(dir, Map), ord = Path.Combine(dir, Orders);
            WriteIfDifferent(scn, map);
            WriteIfDifferent(ord, orders);
            return (scn, ord);
        }

        private static void WriteIfDifferent(string path, byte[] bytes)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
            string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true); // atomic replace: a parallel test process never reads a half file
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static Run Simulate(bool ai)
        {
            var (scn, ord) = Files.Value;
            SimSession s = SimSession.Build(ContentRoot(), scn, Seed, ai);
            OrderScript script = OrderScript.Load(ord);
            SimulationHost host = s.Host;
            host.ChecksumInterval = 1; // the goldens' cadence: the scheduled fold runs every tick
            var seq = new List<GoldenChecksumReplay.Sample>(Ticks);
            host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));

            PreTickHashes pre = s.PreTick();
            int unitsAtStart = host.World.AliveCount;
            int cursor = 0, applied = 0, dropped = 0, aliveAt300 = -1;
            bool readMatches = true;
            // Digest points as in every leg's trace: digest.t/wide.t after the step that produced tick t, before the
            // orders of tick t (t = 0: the freshly built world).
            var digests = new List<(int Tick, ulong Units, ulong Wide)>();
            void RecordDigest(int t)
            {
                if (Array.IndexOf(DigestTicks, t) >= 0)
                    digests.Add((t, WorldDigest.UnitsDigest(host.World), WorldDigest.WideDigest(host.World, host.Projectiles)));
            }
            RecordDigest(0);
            for (int t = 0; t < Ticks; t++)
            {
                script.ApplyDue(s, ref cursor, ref applied, ref dropped);
                s.Step();
                // The pure-read checksum equals the fold the loop just did (what the CLI and every leg report).
                if (host.ComputeChecksumNow() != host.LastChecksum) readMatches = false;
                if (host.CurrentTick == 300) aliveAt300 = host.World.AliveCount;
                RecordDigest((int)host.CurrentTick);
            }
            int verdict = host.WinState.Verdict[(int)Faction.Player1] | (host.WinState.Verdict[(int)Faction.Player2] << 4);
            return new Run(seq, pre, unitsAtStart, aliveAt300, host.World.AliveCount, verdict, applied, dropped, script.Count,
                readMatches, s.RngStateBeforeSeed, Sha256(scn), Sha256(ord), host.ItemRegistry.Count, script.Digest(), digests);
        }

        // ── Meta sidecar ────────────────────────────────────────────────────────────────────────────────────────

        private static string Hex16(ulong v) => "0x" + v.ToString("X16", CultureInfo.InvariantCulture);

        /// <summary>The meta fields of a run, in file order (the keys of every leg's trace header that are the same in
        /// every leg of one variant, plus <c>initial_delay</c> and <c>item_registry</c>).</summary>
        private static List<KeyValuePair<string, string>> MetaFields(Run r, bool ai) => new()
        {
            new("algo", SimChecksum.AlgoVersion.ToString(CultureInfo.InvariantCulture)),
            new("scenario_sha256", r.ScenarioSha),
            new("orders_sha256", r.OrdersSha),
            new("seed", Hex16(Seed)),
            new("ai", ai ? "1" : "0"),
            new("hash.start_state", Hex16(r.Pre.StartState)),
            new("hash.canonical_model", Hex16(r.Pre.CanonicalModel)),
            new("hash.content", Hex16(r.Pre.Content)),
            new("hash.ruleset", Hex16(r.Pre.Ruleset)),
            new("hash.agreement", Hex16(r.Pre.Agreement)),
            new("hash.tick0", "0x" + r.Pre.Tick0.ToString("X8", CultureInfo.InvariantCulture)),
            new("units_at_start", r.UnitsAtStart.ToString(CultureInfo.InvariantCulture)),
            new("initial_delay", PreTickHashes.INITIAL_DELAY.ToString(CultureInfo.InvariantCulture)),
            new("item_registry", r.ItemCount.ToString(CultureInfo.InvariantCulture)),
        };

        private static string FormatMeta(Run r, bool ai)
        {
            var sb = new StringBuilder();
            // Line 1 names both goldens, so the two metas differ only in the `ai` and `hash.agreement` lines.
            sb.Append("# Project Chimera - meta sidecar of the trial-1000 goldens (Unreal trial A4, plan A section 3.5).\n");
            sb.Append("# One `key: value` per line (lines starting with # are prose). Every leg's trace header must equal these fields;\n");
            sb.Append("# ST/compare_traces.py enforces it. Generated by Trial1000GoldenTests in record mode. DO NOT hand-edit.\n");
            sb.Append("# The main and ai metas differ only in `ai` and `hash.agreement` (the agreement hash folds the AI control plan).\n");
            foreach (var kv in MetaFields(r, ai)) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        private static Dictionary<string, string> ParseMeta(string fileName)
        {
            var d = new Dictionary<string, string>();
            foreach (string raw in Encoding.UTF8.GetString(ReadResource(fileName)).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int c = line.IndexOf(':');
                if (c <= 0) throw new FormatException($"{fileName}: malformed meta line '{line}'");
                d[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
            }
            return d;
        }

        // ── Trailer sidecar ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The trailer fields of a run, in file order: the units and wide digests at 0/300/900/1440 and the
        /// order and end-state counts. Every leg's trace trailer must equal these (<c>ST/compare_traces.py</c>
        /// compares each leg with this file, so a run with a single leg is still checked; the wide digest is the only
        /// check of SoA state the checksum does not fold, F40). They differ between main and ai, which is why they
        /// are not in the meta (the two metas differ only in <c>ai</c> and <c>hash.agreement</c>).</summary>
        private static List<KeyValuePair<string, string>> TrailerFields(Run r)
        {
            var f = new List<KeyValuePair<string, string>>();
            foreach (var d in r.Digests)
            {
                string t = d.Tick.ToString(CultureInfo.InvariantCulture);
                f.Add(new("digest." + t, Hex16(d.Units)));
                f.Add(new("wide." + t, Hex16(d.Wide)));
            }
            f.Add(new("orders_digest", Hex16(r.OrdersDigest)));
            f.Add(new("orders_applied", r.Applied.ToString(CultureInfo.InvariantCulture)));
            f.Add(new("orders_dropped", r.Dropped.ToString(CultureInfo.InvariantCulture)));
            f.Add(new("alive_end", r.AliveEnd.ToString(CultureInfo.InvariantCulture)));
            f.Add(new("verdict", r.Verdict.ToString(CultureInfo.InvariantCulture)));
            return f;
        }

        private static string FormatTrailer(Run r)
        {
            var sb = new StringBuilder();
            sb.Append("# Project Chimera - trailer sidecar of the trial-1000 goldens (Unreal trial A4, plan A section 3.5).\n");
            sb.Append("# One `key: value` per line (lines starting with # are prose). Every leg's trace trailer must equal these fields:\n");
            sb.Append("# units/wide digests after the step that produced tick t (before tick t's orders), the order counts, alive_end and\n");
            sb.Append("# verdict = Verdict[Player1] | Verdict[Player2] << 4. ST/compare_traces.py enforces it. Generated by\n");
            sb.Append("# Trial1000GoldenTests in record mode and checked against the build in normal mode. DO NOT hand-edit.\n");
            foreach (var kv in TrailerFields(r)) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        private static void AssertTrailer(string trailerFile, string metaFile, Run r)
        {
            var have = ParseMeta(trailerFile);
            var want = TrailerFields(r);
            foreach (var kv in want)
            {
                Assert.True(have.TryGetValue(kv.Key, out string? v), $"{trailerFile} is missing key '{kv.Key}'");
                Assert.True(v == kv.Value,
                    $"{trailerFile} '{kv.Key}': committed {v}, current build {kv.Value}. " + RuleHint(metaFile, r));
            }
            Assert.Equal(want.Count, have.Count); // no stray keys
        }

        private static string RuleHint(string metaFile, Run r)
        {
            string metaContent = ParseMeta(metaFile).GetValueOrDefault("hash.content", "?");
            string now = Hex16(r.Pre.Content);
            return metaContent == now
                ? $"hash.content is UNCHANGED ({now}): this is a determinism bug, NOT a re-baseline case."
                : $"hash.content changed (meta {metaContent}, now {now}): shipped content was edited, so re-recording " +
                  $"(CHIMERA_GOLDEN_RECORD=1, filter Trial1000) is allowed in this commit.";
        }

        // ── Tests ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The main variant reproduces the committed golden at every one of the 1,440 ticks. Not OS-gated: the
        /// sim is Fixed-point plus the deterministic float footprint and path-delegate conversions (F14, F15), so this
        /// is expected to hold on Linux too; the ubuntu job of determinism-gate.yml is its first Linux run, and a red
        /// result there is a determinism finding (a DW), never a re-record.</summary>
        [Fact]
        public void Main_CsvReplay_MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Run r = MainRun.Value;
            var golden = GoldenChecksumReplay.LoadGolden(MainGolden);
            Assert.Equal(Ticks, golden.Count);
            var div = GoldenChecksumReplay.CompareSequences(golden, r.Samples);
            Assert.True(div is null,
                div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value) + ". " + RuleHint(MainMeta, r));
        }

        /// <summary>The ai variant reproduces its golden. WINDOWS-GATED like
        /// <see cref="AiActiveGoldenTests.MatchesCommittedGolden_OnTheRecordingPlatform"/>: the AI's float scoring is
        /// not proven cross-platform.</summary>
        [Fact]
        public void Ai_CsvReplay_MatchesCommittedGolden_OnTheRecordingPlatform()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            if (!OperatingSystem.IsWindows()) return;
            Run r = AiRun.Value;
            var golden = GoldenChecksumReplay.LoadGolden(AiGolden);
            Assert.Equal(Ticks, golden.Count);
            var div = GoldenChecksumReplay.CompareSequences(golden, r.Samples);
            Assert.True(div is null,
                div is null ? "" : GoldenChecksumReplay.DescribeDivergence(div.Value) + ". " + RuleHint(AiMeta, r));
        }

        /// <summary>The meta sidecars are what the current build computes (H0, file hashes, start facts): a stale meta
        /// or an edited scenario/orders file fails here, naming the key.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Meta_EqualsTheCurrentBuild(bool ai)
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Run r = ai ? AiRun.Value : MainRun.Value;
            var meta = ParseMeta(ai ? AiMeta : MainMeta);
            foreach (var kv in MetaFields(r, ai))
            {
                Assert.True(meta.TryGetValue(kv.Key, out string? have), $"meta is missing key '{kv.Key}'");
                Assert.True(have == kv.Value,
                    $"meta '{kv.Key}': committed {have}, current build {kv.Value}. " + RuleHint(ai ? AiMeta : MainMeta, r));
            }
            Assert.Equal(MetaFields(r, ai).Count, meta.Count); // no stray keys
        }

        /// <summary>The trailer sidecars are what the current build computes (digests at 0/300/900/1440, order counts,
        /// alive_end, verdict): compare_traces.py checks every leg against them. The ai trailer is WINDOWS-GATED with
        /// the ai golden (its digests come from the float AI path).</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Trailer_EqualsTheCurrentBuild(bool ai)
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            if (ai && !OperatingSystem.IsWindows()) return;
            AssertTrailer(ai ? AiTrailer : MainTrailer, ai ? AiMeta : MainMeta, ai ? AiRun.Value : MainRun.Value);
        }

        /// <summary>The two metas differ only in <c>ai</c> and <c>hash.agreement</c> (the agreement hash folds the AI
        /// control plan; nothing else about the start state depends on it).</summary>
        [Fact]
        public void Metas_DifferOnlyInAiAndAgreement()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            var a = ParseMeta(MainMeta);
            var b = ParseMeta(AiMeta);
            Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal), b.Keys.OrderBy(k => k, StringComparer.Ordinal));
            var differing = a.Keys.Where(k => a[k] != b[k]).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "ai", "hash.agreement" }, differing);
        }

        /// <summary>The seed-after-apply order equals Godot's seed-then-apply order only while the apply draws no RNG;
        /// on the trial scenario the world RNG is still the default right after <c>ServerBootstrap.Build</c>.</summary>
        [Fact]
        public void TrialScenario_RngIsDefaultBeforeTheMatchSeed()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, MainRun.Value.RngBeforeSeed);
        }

        /// <summary>The run is the one claimed: 1,000 units, 2,077 orders all applied, nobody wins by tick 1,440, a real
        /// battle (at least 990 alive at tick 300, fewer than 600 at the end), and the pure-read checksum equals the
        /// scheduled fold at every tick.</summary>
        [Fact]
        public void Main_IsTheClaimedBattle()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Run r = MainRun.Value;
            Assert.Equal(1000, r.UnitsAtStart);
            Assert.Equal(2077, r.OrderRows);
            Assert.Equal(2077, r.Applied);
            Assert.Equal(0, r.Dropped);
            Assert.Equal(0, r.Verdict);
            Assert.True(r.AliveAt300 >= 990, $"alive at tick 300 = {r.AliveAt300}, expected >= 990 (the lines meet near x = 0 by then)");
            Assert.True(r.AliveEnd < 600, $"alive at tick 1440 = {r.AliveEnd}, expected < 600 (the battle must bite)");
            Assert.True(r.ChecksumReadMatchesFold, "ComputeChecksumNow() differed from the scheduled fold on some tick");
            Assert.True(r.Samples.Select(x => x.Hash).Distinct().Count() > 1000, "golden sequence barely evolves");
        }

        /// <summary>The ai variant builds the plan into the agreement hash, replays the same file, and keeps the verdict
        /// open; its dropped orders are exactly those aimed at units the AI-variant battle killed earlier.</summary>
        [Fact]
        public void Ai_IsTheSameMatchWithTheAiPlan()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Run m = MainRun.Value, a = AiRun.Value;
            Assert.Equal(m.OrdersSha, a.OrdersSha);
            Assert.Equal(m.UnitsAtStart, a.UnitsAtStart);
            Assert.NotEqual(m.Pre.Agreement, a.Pre.Agreement);
            Assert.Equal(m.Pre.StartState, a.Pre.StartState);
            Assert.Equal(0, a.Verdict);
            Assert.Equal(a.OrderRows, a.Applied + a.Dropped);
            Assert.True(a.ChecksumReadMatchesFold);
        }

        /// <summary>
        /// Record hook. In re-baseline mode (CHIMERA_GOLDEN_RECORD=1) writes both goldens, metas and trailers into
        /// Golden/ (the ai pair only on Windows, the recording platform); otherwise it only checks that the format
        /// round-trips. Rebuild afterwards to refresh the embedded copies.
        /// </summary>
        [Fact]
        public void RecordTrialBaselines()
        {
            Run m = MainRun.Value;
            Assert.Equal(Ticks, m.Samples.Count);
            Assert.True(GoldenChecksumReplay.ParseGolden(Encoding.UTF8.GetBytes(GoldenChecksumReplay.FormatGolden(m.Samples, MainHeader))).SequenceEqual(m.Samples));
            if (!GoldenChecksumReplay.IsRecordMode) return;

            // Refuse to record anything a second run cannot reproduce.
            Run again = Simulate(ai: false);
            Assert.True(m.Samples.SequenceEqual(again.Samples), "two in-process runs of the main variant diverged; not recording");
            Assert.True(FormatTrailer(m) == FormatTrailer(again), "two in-process runs of the main variant have different trailers; not recording");
            GoldenChecksumReplay.MaybeRecord(m.Samples, MainGolden, MainHeader);
            File.WriteAllText(GoldenChecksumReplay.GoldenSourcePath(MainMeta), FormatMeta(m, false), new UTF8Encoding(false));
            File.WriteAllText(GoldenChecksumReplay.GoldenSourcePath(MainTrailer), FormatTrailer(m), new UTF8Encoding(false));

            if (OperatingSystem.IsWindows())
            {
                Run a = AiRun.Value;
                Assert.Equal(Ticks, a.Samples.Count);
                Run aAgain = Simulate(ai: true);
                Assert.True(a.Samples.SequenceEqual(aAgain.Samples), "two in-process runs of the ai variant diverged; not recording");
                Assert.True(FormatTrailer(a) == FormatTrailer(aAgain), "two in-process runs of the ai variant have different trailers; not recording");
                GoldenChecksumReplay.MaybeRecord(a.Samples, AiGolden, AiHeader);
                File.WriteAllText(GoldenChecksumReplay.GoldenSourcePath(AiMeta), FormatMeta(a, true), new UTF8Encoding(false));
                File.WriteAllText(GoldenChecksumReplay.GoldenSourcePath(AiTrailer), FormatTrailer(a), new UTF8Encoding(false));
            }
        }
    }
}
