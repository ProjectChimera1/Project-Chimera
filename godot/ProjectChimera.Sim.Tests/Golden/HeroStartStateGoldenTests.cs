#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ProjectChimera.Core;              // HeroStore, HeroId, Fixed
using ProjectChimera.Core.Definitions;  // StartStateHash
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 3.2 (AC3 / Task 5) — the HERO START-STATE golden: pins the FNV-64 <see cref="StartStateHash"/> over the
    /// fixed <see cref="HeroStartStateScenario"/> fixture (the "recorded model-layout pin", AC3's SECOND pin; the
    /// first is the independent-FNV pin in <c>StartStateHashTests</c>). It exercises the same two-in-process-runs
    /// byte-identity discipline as the per-tick goldens, and — because a changed roster moves it — is non-vacuous.
    ///
    /// It is DISTINCT from every other golden: <see cref="StartStateHash"/> is computed ONCE (init state), not a
    /// per-tick <see cref="ProjectChimera.Core.SimChecksum"/> sequence, so this golden stores a single 64-bit value
    /// rather than a tick stream — hence the small self-contained loader/recorder below (the shared per-tick
    /// <see cref="GoldenChecksumReplay"/> harness is deliberately untouched). It reuses only that harness's
    /// record-mode env gate + source-path resolver. CROSS-PLATFORM SAFE: all folded fields are int / Fixed.Raw.
    /// </summary>
    public class HeroStartStateGoldenTests
    {
        private const string GoldenFile = "hero-start-state.golden.txt";

        [Fact]
        public void ComputesIdentically_TwoRuns()
        {
            // The single-value analogue of the per-tick "two in-process runs are byte-identical" discipline.
            Assert.Equal(HeroStartStateScenario.Compute(), HeroStartStateScenario.Compute());
        }

        [Fact]
        public void MatchesCommittedGolden()
        {
            if (GoldenChecksumReplay.IsRecordMode) return;
            Assert.Equal(LoadGolden(), HeroStartStateScenario.Compute());
        }

        [Fact]
        public void AddedHero_MovesGolden()
        {
            // Teeth: the pinned value is not vacuous — a changed roster changes it (the StartStateHash covers heroes).
            ulong pinned = HeroStartStateScenario.Compute();
            HeroStore more = HeroStartStateScenario.BuildHeroes();
            more.Mint(new HeroId(4_000_000_037UL), entityId: 15, level: 3, xp: Fixed.FromInt(90));
            Assert.NotEqual(pinned, StartStateHash.Compute(HeroStartStateScenario.BuildModel(), more));
        }

        [Fact]
        public void RecordHeroStartStateBaseline()
        {
            if (!GoldenChecksumReplay.IsRecordMode) return;
            ulong value = HeroStartStateScenario.Compute();
            Assert.Equal(value, HeroStartStateScenario.Compute()); // refuse to record a nondeterministic value
            string path = GoldenChecksumReplay.GoldenSourcePath(GoldenFile);
            File.WriteAllText(path, Format(value), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // ── Minimal single-value golden IO (self-contained; the per-tick GoldenChecksumReplay harness is untouched). ──

        private static string Format(ulong value)
        {
            var sb = new StringBuilder();
            sb.Append("# Project Chimera — hero start-state hash golden (Story 3.2, AC3).\n");
            sb.Append("# Format: \"startstatehash <hex16>\" — the FNV-64 StartStateHash over the HeroStartStateScenario fixture.\n");
            sb.Append($"# startstatehash_algo_version: {StartStateHash.AlgoVersion}\n");
            sb.Append("# Fixture: 2-slot model + 1 node + 1 CommandCenter, plus 2 minted heroes (ids 1e9+7 / 2e9+11).\n");
            sb.Append("# CROSS-PLATFORM SAFE (int/Fixed.Raw only). Re-baseline (intentional change only): CHIMERA_GOLDEN_RECORD=1, run\n");
            sb.Append("# `dotnet test --filter FullyQualifiedName~HeroStartStateGolden`, then `dotnet build` and commit. DO NOT hand-edit.\n");
            sb.Append($"startstatehash {value:X16}\n");
            return sb.ToString();
        }

        private static ulong LoadGolden()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string? res = asm.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(GoldenFile, StringComparison.Ordinal));
            if (res is null)
                throw new InvalidOperationException(
                    $"Golden '{GoldenFile}' is not embedded. Record it: set CHIMERA_GOLDEN_RECORD=1, run " +
                    $"`dotnet test --filter FullyQualifiedName~HeroStartStateGolden`, then rebuild.");
            using Stream stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            foreach (string rawLine in reader.ReadToEnd().Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "startstatehash")
                    return ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            throw new FormatException($"Golden '{GoldenFile}' has no 'startstatehash <hex16>' data line.");
        }
    }
}
