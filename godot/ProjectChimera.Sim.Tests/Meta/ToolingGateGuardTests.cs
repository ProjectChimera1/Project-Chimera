#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Guards over the TOOLING that drives this repo's gates — the same convention as
    /// <c>AnalyzerGateGuardTests</c> / <c>NakamaTsValidatorCiGateGuardTests</c>: pin the wiring as a Tier-1
    /// invariant so a regression turns the developer's own <c>dotnet test</c> red, rather than surfacing as a
    /// confusing failure hours later inside a script nobody reads until it breaks.
    ///
    /// <para>DW-556 — <c>cross-platform-determinism-check.wsl.sh</c> refused to run from a git WORKTREE, because it
    /// tested for a <c>.git</c> DIRECTORY and a linked worktree's <c>.git</c> is a FILE holding <c>gitdir:</c>. That
    /// is precisely the checkout shape the parallel burn-down track uses, so the Windows↔Linux determinism gate had
    /// to be hand-reproduced against a fresh clone instead — slow and easy to get subtly wrong.</para>
    ///
    /// <para>DW-502 — its guard was retired with the burn-down dispatcher it checked (removed in c794681b); the test
    /// itself was deleted by Unreal trial task A0, 2026-10-01.</para>
    /// </summary>
    public class ToolingGateGuardTests
    {
        private const string WslScriptRelPath = "godot/tools/cross-platform-determinism-check.wsl.sh";

        // ── DW-556: the cross-platform gate must run from a linked worktree ──────

        [Fact]
        public void CrossPlatformCheckScript_AcceptsAWorktreeCheckout_NotOnlyAGitDirectory()
        {
            string script = ScriptPath();
            Assert.True(File.Exists(script),
                $"'{WslScriptRelPath}' not found at '{script}'. This path is derived from [CallerFilePath]; if the " +
                $"WSL worker moved, move this guard with it — the Windows↔Linux determinism gate (AR-37) is what it " +
                $"protects.");

            string[] active = ActiveShellLines(script);

            // The rot form: a DIRECTORY test on $SRC/.git. True for a normal clone, FALSE for every linked
            // worktree, so the gate refused the exact checkout shape the parallel track uses (DW-556).
            string? dirTest = active.FirstOrDefault(l =>
                Regex.IsMatch(l, @"-d\s+""?\$\{?SRC\}?/\.git""?"));
            Assert.True(dirTest == null,
                $"'{WslScriptRelPath}' still tests `-d \"$SRC/.git\"`, which is FALSE for a linked worktree (its " +
                $".git is a FILE containing `gitdir: <path>`), so the cross-platform determinism gate refuses to run " +
                $"from a worktree checkout — the DW-556 defect. Use `-e` and/or `git -C \"$SRC\" rev-parse " +
                $"--git-dir`. Offending line: {dirTest}");

            // ...and the guard must still be a real guard: a work-tree check has to remain, or a typo'd $SRC would
            // sail past into a clone of nothing.
            Assert.True(active.Any(l => Regex.IsMatch(l, @"-e\s+""?\$\{?SRC\}?/\.git""?"))
                        || active.Any(l => l.Contains("rev-parse --git-dir", StringComparison.Ordinal)),
                $"'{WslScriptRelPath}' no longer verifies that $SRC is a git work tree at all. Relaxing the DW-556 " +
                $"directory test must not mean deleting the check — keep `-e \"$SRC/.git\"` and/or " +
                $"`git -C \"$SRC\" rev-parse --git-dir`, both of which accept a clone AND a linked worktree.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>Shell lines with full-line <c>#</c> comments (and blanks) stripped, so a commented-out guard can
        /// never satisfy — or trip — an assertion vacuously.</summary>
        private static string[] ActiveShellLines(string path) =>
            File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToArray();

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        private static string ScriptPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "tools", "cross-platform-determinism-check.wsl.sh");

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
