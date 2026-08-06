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
    /// DW-606 extends the same guard to the OTHER half of "shipped": neither <c>tsc --noEmit</c> nor vitest ever
    /// produces the artifact Nakama loads. That is the esbuild BUNDLE — <c>build/index.js</c>, mounted into
    /// <c>/nakama/data/modules</c> by <c>docker-compose.yml</c> — so a change that typechecks and passes vitest
    /// could still fail to bundle, or bundle to a format Nakama's goja runtime cannot load, with CI green. The job
    /// now runs <c>npm run build</c> as well, and the two guards below pin both ends of it: the workflow step, and
    /// the package.json script's es2017/cjs/node flags (which a bundling run would NOT catch drifting — esbuild
    /// exits 0 for an esm/es2022 bundle; it only explodes later, inside goja, at module load).
    ///
    /// Deliberately NOT asserted: vitest test COUNTS (the TS suite may grow freely) and any TS file internals —
    /// only the wiring that keeps the TS suite and the deploy bundle load-bearing in CI.
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
            string[] activeLines = ActiveLines(workflow);

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
            string packageJson = PackageJsonPath();
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

        [Fact]
        public void DeterminismGateWorkflow_RunsNpmRunBuild_SoTheDeployBundleIsProvenBuildable()
        {
            // DW-606. typecheck + vitest never emit build/index.js, so before this step the ONLY thing that ever
            // exercised the esbuild bundling was a human following the README before `docker compose up` — and a
            // missing/broken bundle is fail-closed for every player (the entrypoint hard-exits without it).
            string workflow = WorkflowPath();
            Assert.True(File.Exists(workflow),
                $"CI workflow not found at '{workflow}'. This path is derived from [CallerFilePath]; if the workflow " +
                $"file moved or was renamed, update {nameof(WorkflowPath)} in this guard — the deploy-bundle CI step " +
                $"(DW-606) must move with it, not vanish.");

            string[] activeLines = ActiveLines(workflow);

            Assert.True(HasRunLineMatching(activeLines, new Regex(@"\bnpm run build\b")),
                "determinism-gate.yml has no 'run:' step invoking 'npm run build'. Without it, a change that " +
                "typechecks and passes vitest but breaks the esbuild bundle ships with CI green, and the break only " +
                "surfaces at `docker compose up` — where a missing build/index.js fail-closes every player out of " +
                "online play. Restore the nakama-ts-validator-gate job's build step (DW-606).");
        }

        [Fact]
        public void NakamaPackage_BuildScript_StillBundlesForTheGojaTarget()
        {
            // The workflow deliberately runs a bare 'npm run build' rather than inlining esbuild flags, so THIS is
            // where the goja-compatibility contract is pinned. A drift here would not fail the CI build step at all:
            // esbuild happily emits an esm/es2022 bundle and exits 0. It would fail inside Nakama, at module load.
            string packageJson = PackageJsonPath();
            Assert.True(File.Exists(packageJson),
                $"'{NakamaPackageRelPath}/package.json' not found at '{packageJson}'. If the Nakama module tree moved, " +
                $"update {nameof(NakamaPackageDir)} here AND the workflow job's working-directory together (DW-606).");

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(packageJson));

            Assert.True(doc.RootElement.TryGetProperty("scripts", out JsonElement scripts)
                        && scripts.TryGetProperty("build", out JsonElement build)
                        && build.ValueKind == JsonValueKind.String
                        && build.GetString()!.Contains("esbuild", StringComparison.OrdinalIgnoreCase),
                $"'{NakamaPackageRelPath}/package.json' scripts.build no longer invokes esbuild. The CI gate runs " +
                $"'npm run build' — if that script stops producing the deploy bundle, the step passes while proving " +
                $"nothing (DW-606).");

            string buildScript = scripts.GetProperty("build").GetString()!;

            foreach ((string flag, string why) in RequiredBuildFlags)
            {
                Assert.True(buildScript.Contains(flag, StringComparison.OrdinalIgnoreCase),
                    $"'{NakamaPackageRelPath}/package.json' scripts.build no longer passes '{flag}' to esbuild — {why}. " +
                    $"esbuild would still exit 0 and the CI build step would still be green; the failure would land in " +
                    $"Nakama's goja runtime at module load instead (DW-606). Current script: {buildScript}");
            }

            // Tie the emitted file to the DEPLOYED path: package.json `main` is build/index.js and docker-compose.yml
            // mounts ./nakama-modules/build into /nakama/data/modules, so an --outfile elsewhere is a bundle that
            // builds green and never deploys.
            Assert.True(doc.RootElement.TryGetProperty("main", out JsonElement main)
                        && main.ValueKind == JsonValueKind.String,
                $"'{NakamaPackageRelPath}/package.json' has no string 'main' field; it names the bundle Nakama loads " +
                $"and this guard cross-checks the esbuild --outfile against it (DW-606).");

            string mainPath = main.GetString()!;
            Assert.True(buildScript.Contains($"--outfile={mainPath}", StringComparison.Ordinal),
                $"'{NakamaPackageRelPath}/package.json' scripts.build does not write to '--outfile={mainPath}' (the " +
                $"'main' field, and the file docker-compose.yml mounts into /nakama/data/modules). The two drifted " +
                $"apart, so the build would go green while emitting a bundle that never deploys (DW-606). " +
                $"Current script: {buildScript}");
        }

        /// <summary>The esbuild flags that keep the deploy bundle loadable by Nakama's goja runtime. Asserted one at
        /// a time so a failure names the exact flag that drifted and why it matters.</summary>
        private static readonly (string Flag, string Why)[] RequiredBuildFlags =
        {
            ("--bundle",
                "goja loads ONE file out of the mounted modules directory, so the imports must be inlined at build time"),
            ("--platform=node",
                "selects the CommonJS-oriented resolution/defaults the Nakama runtime module expects"),
            ("--format=cjs",
                "goja is not an ESM loader — an 'esm' bundle fails at module load rather than at build time"),
            ("--target=es2017",
                "goja implements roughly ES2017; a higher target emits syntax it cannot parse (and matches tsconfig's target)"),
        };

        /// <summary>Workflow lines with full-line comments (and blanks) stripped, so a commented-out step can never
        /// satisfy a guard vacuously.</summary>
        private static string[] ActiveLines(string workflowPath) =>
            File.ReadAllLines(workflowPath)
                .Where(l =>
                {
                    string t = l.Trim();
                    return t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal);
                })
                .ToArray();

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

        /// <summary>The Nakama module package manifest — the far end of every <c>npm run &lt;script&gt;</c> the CI job invokes.</summary>
        private static string PackageJsonPath() => Path.Combine(NakamaPackageDir(), "package.json");

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
