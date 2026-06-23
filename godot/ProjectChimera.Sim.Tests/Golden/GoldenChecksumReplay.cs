#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ProjectChimera.Core;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Reusable engine for the golden-checksum replay regression harness (migration Step 1).
    ///
    /// Responsibilities:
    ///   - <see cref="RunAndRecord"/>: build a fresh sim, step it N ticks, capture the per-tick SimChecksum.
    ///   - <see cref="CompareSequences"/>: locate the FIRST divergence (tick + expected vs actual).
    ///   - golden-file IO: <see cref="LoadGolden"/> (embedded resource), <see cref="FormatGolden"/>,
    ///     <see cref="ParseGolden"/>, and the env-var-gated re-baseline writer <see cref="MaybeRecord"/>.
    ///
    /// Stories 1.3b (widen SimChecksum + re-baseline), 1.4 (negative tests), and 1.5 (SimRng) reuse this
    /// directly — keep it clean and dependency-free (System.* + the sim source only).
    /// </summary>
    public static class GoldenChecksumReplay
    {
        /// <summary>Committed golden baseline filename (embedded resource + source file under Golden/).</summary>
        public const string GoldenFileName = "golden-scenario.golden.txt";

        /// <summary>Set this env var to "1" to re-baseline (write the freshly recorded golden to source).</summary>
        public const string RecordEnvVar = "CHIMERA_GOLDEN_RECORD";

        /// <summary>One recorded checksum sample: the sim tick and its 32-bit FNV-1a world hash.</summary>
        public readonly record struct Sample(uint Tick, uint Hash);

        /// <summary>First point at which two sequences disagree: the tick and both hashes.</summary>
        public readonly record struct Divergence(uint Tick, uint Expected, uint Actual);

        /// <summary>True when running in re-baseline (record) mode — see <see cref="RecordEnvVar"/>.</summary>
        public static bool IsRecordMode =>
            Environment.GetEnvironmentVariable(RecordEnvVar) == "1";

        /// <summary>
        /// Build a FRESH harness, subscribe to its checksum signal, and step it <paramref name="ticks"/>
        /// times, returning the recorded (tick, hash) sequence.
        ///
        /// The optional <paramref name="perturb"/> hook runs BEFORE each <see cref="SimulationLoop.StepOnce"/>,
        /// so a perturbation injected at loop index K is reflected in that iteration's checksum (tick K+1) —
        /// giving a clean, off-by-one-free located tick for AC3.
        /// </summary>
        public static IReadOnlyList<Sample> RunAndRecord(int ticks, Action<int, EntityWorld>? perturb = null,
            Func<GoldenHarness>? build = null)
        {
            build ??= GoldenScenario.Build; // default: the 1.2 scenario — existing call sites are unaffected
            GoldenHarness harness = build(); // fresh stores + systems; no statics
            var seq = new List<Sample>(ticks);
            harness.Loop.OnChecksum = (tick, hash) => seq.Add(new Sample(tick, hash));

            for (int i = 0; i < ticks; i++)
            {
                perturb?.Invoke(i, harness.World); // perturb BEFORE step => located tick = K+1
                harness.Loop.StepOnce();
            }

            return seq;
        }

        /// <summary>
        /// Return the FIRST entry at which <paramref name="actual"/> diverges from <paramref name="expected"/>,
        /// or null if the two sequences are byte-identical. A length mismatch is reported explicitly as a
        /// divergence at the first missing/extra tick (the absent side's hash is reported as 0).
        /// </summary>
        public static Divergence? CompareSequences(IReadOnlyList<Sample> expected, IReadOnlyList<Sample> actual)
        {
            int n = Math.Min(expected.Count, actual.Count);
            for (int i = 0; i < n; i++)
            {
                if (expected[i].Tick != actual[i].Tick || expected[i].Hash != actual[i].Hash)
                {
                    // Report against the actual run's tick; both ticks are identical on the normal path.
                    return new Divergence(actual[i].Tick, expected[i].Hash, actual[i].Hash);
                }
            }

            if (expected.Count != actual.Count)
            {
                // Length mismatch: the shorter sequence is "missing" entries from index n onward.
                return actual.Count > expected.Count
                    ? new Divergence(actual[n].Tick, 0u, actual[n].Hash)   // actual has an extra tick
                    : new Divergence(expected[n].Tick, expected[n].Hash, 0u); // actual is missing a tick
            }

            return null;
        }

        /// <summary>Human-readable one-line drift message for assertion failures.</summary>
        public static string DescribeDivergence(Divergence d) =>
            $"Checksum drift at tick {d.Tick}: expected 0x{d.Expected:X8}, actual 0x{d.Actual:X8}";

        // ── Golden-file IO ───────────────────────────────────────────────────────

        /// <summary>
        /// Load the committed golden baseline from the EMBEDDED resource (portable across Windows/Linux:
        /// no file paths, no line-ending fragility — required for the 1.10c cross-platform gate).
        /// </summary>
        public static IReadOnlyList<Sample> LoadGolden(string fileName = GoldenFileName)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? resourceName = asm.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
            if (resourceName is null)
                throw new InvalidOperationException(
                    $"Golden baseline '{fileName}' is not embedded in the test assembly. " +
                    $"Generate it first: set {RecordEnvVar}=1 and run the Golden tests, then rebuild. " +
                    $"(Embedded resources found: {string.Join(", ", asm.GetManifestResourceNames())})");

            using Stream stream = asm.GetManifestResourceStream(resourceName)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ParseGolden(ms.ToArray());
        }

        /// <summary>
        /// Parse golden bytes into samples. Robust to CRLF/LF (splits on '\n', trims '\r'); skips blank
        /// lines and '#' header comments. The checksum values themselves are byte-identical across
        /// platforms — only the file transport needs to be newline-tolerant.
        /// </summary>
        public static IReadOnlyList<Sample> ParseGolden(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);
            var samples = new List<Sample>();

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim(); // trims surrounding whitespace incl. a trailing '\r'
                if (line.Length == 0 || line[0] == '#') continue;

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    throw new FormatException($"Malformed golden line (expected '<tick> <hashHex>'): '{line}'");

                uint tick = uint.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                uint hash = uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber,
                                       System.Globalization.CultureInfo.InvariantCulture);
                samples.Add(new Sample(tick, hash));
            }

            return samples;
        }

        /// <summary>
        /// Descriptive metadata for a golden file's leading '#' comment block, so each scenario's golden
        /// self-identifies. <see cref="ParseGolden"/> skips '#' lines, so this is documentation only — it never
        /// affects the parsed samples. (Story 1.3a fix: <see cref="FormatGolden"/> previously hardcoded the 1.2
        /// scenario's text, which mislabelled the multi-faction golden as "Story 1.2 / GoldenScenario.Build()".)
        /// </summary>
        /// <param name="Title">Baseline title, e.g. "golden-checksum replay baseline (Story 1.2)".</param>
        /// <param name="ScenarioDescription">What the sequence pins (scenario builder + step settings).</param>
        /// <param name="RebaselineHint">Exact re-baseline recipe for THIS golden (its own test filter + rebuild + commit).</param>
        public readonly record struct GoldenHeader(string Title, string ScenarioDescription, string RebaselineHint);

        /// <summary>Default header — the Story 1.2 <see cref="GoldenScenario"/> baseline. Call sites that omit a
        /// header keep this text; the filter names only the 1.2 tests so a re-baseline won't touch other goldens.</summary>
        public static readonly GoldenHeader DefaultHeader = new(
            "golden-checksum replay baseline (Story 1.2)",
            "Pins today's SimChecksum sequence for GoldenScenario.Build() stepped via StepOnce at ChecksumInterval=1.",
            $"set {RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~GoldenChecksumReplay`, then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit.");

        /// <summary>
        /// Serialize a sequence to the committed golden text format: a documented header comment followed by
        /// one "<tick> <hashHex8>" line per sample. Uses '\n' explicitly (not Environment.NewLine) for
        /// cross-platform stability. Pass a <paramref name="header"/> so the file self-identifies; omit it for
        /// the default (Story 1.2) baseline.
        /// </summary>
        public static string FormatGolden(IReadOnlyList<Sample> seq, GoldenHeader? header = null)
        {
            GoldenHeader h = header ?? DefaultHeader;
            var sb = new StringBuilder();
            sb.Append($"# Project Chimera — {h.Title}.\n");
            sb.Append("# Format: \"<tick> <hashHex8>\" per line — tick decimal, hash 8 uppercase hex digits (no 0x).\n");
            // Self-identifying algorithm version (Story 1.3b). ParseGolden skips '#' lines, so this is
            // informational only — but it lets a baseline declare which SimChecksum algorithm produced it.
            sb.Append($"# checksum_algo_version: {ProjectChimera.Core.SimChecksum.AlgoVersion}\n");
            sb.Append($"# {h.ScenarioDescription}\n");
            sb.Append($"# Samples: {seq.Count} (one line per sim tick).\n");
            sb.Append($"# Re-baseline (intentional behavior change only): {h.RebaselineHint}\n");
            foreach (Sample s in seq)
                sb.Append($"{s.Tick} {s.Hash:X8}\n");
            return sb.ToString();
        }

        /// <summary>
        /// In record mode, write <paramref name="seq"/> to the SOURCE golden file (located via
        /// <see cref="GoldenSourcePath"/>) and return true; otherwise do nothing and return false.
        /// The dev then rebuilds (to refresh the embedded copy) and commits. Pass a <paramref name="header"/>
        /// so the written file self-identifies (omit it for the default Story 1.2 baseline).
        /// </summary>
        public static bool MaybeRecord(IReadOnlyList<Sample> seq, string fileName = GoldenFileName,
            GoldenHeader? header = null)
        {
            if (!IsRecordMode) return false;
            string path = GoldenSourcePath(fileName);
            File.WriteAllText(path, FormatGolden(seq, header), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }

        /// <summary>
        /// Absolute path of the SOURCE golden file, resolved from this source file's compile-time location
        /// via <see cref="CallerFilePathAttribute"/> (the golden lives beside this file under Golden/).
        /// Used only by the re-baseline writer, which runs on the build machine.
        /// </summary>
        public static string GoldenSourcePath(string fileName = GoldenFileName, [CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve the Golden/ source directory.");
            return Path.Combine(dir, fileName);
        }
    }
}
