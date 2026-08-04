#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-437 (Story 9.12) guard tests — keep the C#&lt;-&gt;TS hero-profile validator parity guarantee FULLY
    /// enforced in CI, as Tier-1 invariants (the 1.10a convention: assert structure as tests that run on every
    /// <c>dotnet test</c>, never as CI-only shell steps).
    ///
    /// The parity mechanism: a single shared oracle
    /// (<c>docs/server-deploy/nakama-modules/test/fixtures/validation-cases.json</c>) is exercised by BOTH
    /// validators — the C# <c>HeroProfileValidator</c> (via the embedded copy, in this very test project) and the
    /// TS <c>validation.ts</c> (via the package's vitest suite). The C# half always ran in the determinism-gate
    /// workflow; the TS half ran only via the README's manual <c>npm test</c>, so a <c>validation.ts</c>
    /// regression that diverged from the shared oracle shipped with CI green (DW-437). The fix added a
    /// <c>nakama-ts-validator-gate</c> job (<c>npm ci</c> + <c>npm test</c>) to
    /// <c>.github/workflows/determinism-gate.yml</c>; these guards fail the Tier-1 suite the moment that wiring
    /// is deleted or quietly de-fanged — the Epic-9 lesson that a gate is only done while it is load-bearing.
    ///
    /// Deliberately NOT asserted: vitest test COUNTS (the TS suite may grow freely) and any TS file internals —
    /// only the wiring that keeps the TS suite load-bearing in CI.
    ///
    /// Paths are resolved portably from this source file via <see cref="CallerFilePathAttribute"/> — the same
    /// mechanism <c>AnalyzerGateGuardTests</c> and <c>CrossPlatformGoldenGuardTests</c> use.
    /// </summary>
    public class NakamaTsValidatorCiGateGuardTests
    {
        /// <summary>Repo-relative package path — also the workflow job's working-directory. One constant so the
        /// failure messages and the assertions cannot drift apart.</summary>
        private const string NakamaPackageRelPath = "docs/server-deploy/nakama-modules";

        [Fact]
        public void DeterminismGateWorkflow_RunsNpmCiAndNpmTest_ForTheNakamaTsValidator()
        {
            string workflow = WorkflowPath();
            Assert.True(File.Exists(workflow),
                $"CI workflow not found at '{workflow}'. This path is derived from [CallerFilePath]; if the workflow " +
                $"file moved or was renamed, update {nameof(WorkflowPath)} in this guard — the TS-validator CI gate " +
                $"(DW-437) must move with it, not vanish.");

            // Strip full-line comments so a '# TODO: npm test' comment can never satisfy the guard vacuously.
            string[] activeLines = File.ReadAllLines(workflow)
                .Where(l =>
                {
                    string t = l.Trim();
                    return t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal);
                })
                .ToArray();

            Assert.True(activeLines.Any(l => l.Contains(NakamaPackageRelPath, StringComparison.Ordinal)),
                $"determinism-gate.yml no longer references '{NakamaPackageRelPath}' on any active (non-comment) line. " +
                $"The nakama-ts-validator-gate job's working-directory/cache wiring is what points npm at the TS " +
                $"validator package; without it the C#<->TS parity guarantee is only half-enforced again (DW-437).");

            // The npm invocations must be actual run: steps — a step *name* or comment mentioning npm is not a gate.
            Assert.True(HasRunLineMatching(activeLines, new Regex(@"\bnpm ci\b")),
                "determinism-gate.yml has no 'run:' step invoking 'npm ci'. The locked install (npm's --locked-mode " +
                "equivalent) is required so the TS validator suite runs against the committed package-lock.json — " +
                "restore the nakama-ts-validator-gate job's install step (DW-437).");

            Assert.True(HasRunLineMatching(activeLines, new Regex(@"\bnpm test\b|\bvitest\b")),
                "determinism-gate.yml has no 'run:' step invoking the TS validator suite ('npm test' / vitest). " +
                "Without it a validation.ts regression that diverges from the shared C#<->TS oracle ships with CI " +
                "green — the exact DW-437 gap. Restore the nakama-ts-validator-gate job's test step.");
        }

        [Fact]
        public void NakamaPackage_TestScript_IsStillVitest_SoNpmTestRunsTheValidatorSuite()
        {
            // The workflow runs 'npm test'; this pins the other end of that indirection. If someone renames or
            // empties the package.json test script, CI would 'pass' while running nothing — same gap, new shape.
            string packageJson = Path.Combine(NakamaPackageDir(), "package.json");
            Assert.True(File.Exists(packageJson),
                $"'{NakamaPackageRelPath}/package.json' not found at '{packageJson}'. If the Nakama module tree moved, " +
                $"update {nameof(NakamaPackageDir)} here AND the workflow job's working-directory together (DW-437).");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            Assert.True(doc.RootElement.TryGetProperty("scripts", out JsonElement scripts)
                        && scripts.TryGetProperty("test", out JsonElement test)
                        && test.ValueKind == JsonValueKind.String
                        && test.GetString()!.Contains("vitest", StringComparison.OrdinalIgnoreCase),
                $"'{NakamaPackageRelPath}/package.json' scripts.test no longer invokes vitest. The CI gate runs " +
                $"'npm test' — if that script stops running the vitest suite, the TS validator is unguarded again " +
                $"even though the workflow job still exists (DW-437).");
        }

        [Fact]
        public void NakamaPackage_CommitsItsLockFile_SoTheCiInstallIsReproducible()
        {
            // npm ci hard-fails without a package-lock.json; this catches the deletion locally (on dotnet test)
            // instead of as a confusing red CI run, and preserves the reproducible-install intent.
            string lockFile = Path.Combine(NakamaPackageDir(), "package-lock.json");
            Assert.True(File.Exists(lockFile),
                $"'{NakamaPackageRelPath}/package-lock.json' is missing. The CI gate installs via 'npm ci', which " +
                $"requires the committed lock file (the npm equivalent of the .NET jobs' --locked-mode). Regenerate " +
                $"and commit it; do not downgrade the workflow to 'npm install'.");
        }

        /// <summary>True when any active line is a <c>run:</c> step whose command matches <paramref name="pattern"/>.
        /// Single-line <c>run:</c> steps only — that is the shape the nakama-ts-validator-gate job commits to; if a
        /// rework moves the npm calls into a <c>run: |</c> block, update this guard alongside it.</summary>
        private static bool HasRunLineMatching(string[] activeLines, Regex pattern) =>
            activeLines.Select(l => l.Trim())
                .Any(t => t.StartsWith("run:", StringComparison.Ordinal) && pattern.IsMatch(t));

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        /// <summary>&lt;repo&gt;/.github/workflows/determinism-gate.yml — three directories up, then into .github/workflows/.</summary>
        private static string WorkflowPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "..", ".github", "workflows", "determinism-gate.yml");

        /// <summary>&lt;repo&gt;/docs/server-deploy/nakama-modules — the TS validator package root.</summary>
        private static string NakamaPackageDir([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "..", "docs", "server-deploy", "nakama-modules");

        /// <summary>Resolve a path relative to THIS source file's directory and normalize away the '..' segments.</summary>
        private static string ResolveFromHere(string thisFilePath, params string[] segments)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException(
                             "Could not resolve this test's source directory via [CallerFilePath].");
            string[] parts = new string[segments.Length + 1];
            parts[0] = dir;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return Path.GetFullPath(Path.Combine(parts));
        }
    }
}
