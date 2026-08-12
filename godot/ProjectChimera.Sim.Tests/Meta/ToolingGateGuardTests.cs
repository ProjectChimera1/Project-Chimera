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
    /// <para>DW-502 — the burn-down dispatcher defaulted the Tier-1 baseline to a hardcoded literal. It went stale
    /// immediately: seven independent worktrees measured a figure ~120 short of it and had to disprove a phantom
    /// regression; one reached for <c>git stash</c> to explain the gap and cross-wired every parallel worktree's
    /// shared stash stack (DW-521). A stale baseline is worse than no baseline, so the caller that just measured it
    /// must supply it.</para>
    /// </summary>
    public class ToolingGateGuardTests
    {
        private const string WslScriptRelPath = "godot/tools/cross-platform-determinism-check.wsl.sh";
        private const string DispatcherRelPath = ".claude/workflows/dw-burndown.workflow.js";

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

        // ── DW-502: the burn-down dispatcher must not carry a stale baseline ─────

        [Fact]
        public void BurnDownDispatcher_HasNoHardcodedTier1Baseline()
        {
            string dispatcher = DispatcherPath();
            Assert.True(File.Exists(dispatcher),
                $"'{DispatcherRelPath}' not found at '{dispatcher}'. This path is derived from [CallerFilePath]; if " +
                $"the burn-down workflow moved, move this guard with it — a hardcoded suite baseline is the " +
                $"regression it exists to prevent (DW-502).");

            string[] active = ActiveJsLines(dispatcher);

            string? fallback = active.FirstOrDefault(l =>
                Regex.IsMatch(l, @"baselineTests\s*(\?\?|\|\|)\s*\d"));
            Assert.True(fallback == null,
                $"'{DispatcherRelPath}' hardcodes a fallback Tier-1 pass count for baselineTests. That literal is a " +
                $"snapshot of ONE commit and rots on the next merge: seven burn-down worktrees measured ~120 fewer " +
                $"tests than the stale figure and had to disprove a phantom regression, and one triggered a " +
                $"cross-worktree `git stash` incident doing so (DW-502 / DW-521). Require args.baselineTests and " +
                $"fail the launch when it is missing. Offending line: {fallback}");

            // The other end of the same rule: dropping the fallback is only safe if the value is REQUIRED, otherwise
            // every agent silently receives `undefined` as its baseline.
            Assert.True(active.Any(l => l.Contains("baselineTests", StringComparison.Ordinal)),
                $"'{DispatcherRelPath}' no longer references baselineTests at all. The dispatcher must still take " +
                $"the measured Tier-1 count from its caller and hand it to the agents (DW-502).");
            Assert.True(active.Any(l => Regex.IsMatch(l, @"error:\s*'missing baselineTests")),
                $"'{DispatcherRelPath}' does not refuse to launch when baselineTests is absent. Without that guard " +
                $"the removed fallback just becomes `undefined` in every agent's prompt — the same wrong-number " +
                $"failure with a less readable value (DW-502).");
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>Shell lines with full-line <c>#</c> comments (and blanks) stripped, so a commented-out guard can
        /// never satisfy — or trip — an assertion vacuously.</summary>
        private static string[] ActiveShellLines(string path) =>
            File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(t => t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                .ToArray();

        /// <summary>JS lines with full-line <c>//</c> comments (and blanks) stripped, same reason.</summary>
        private static string[] ActiveJsLines(string path) =>
            File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(t => t.Length > 0 && !t.StartsWith("//", StringComparison.Ordinal))
                .ToArray();

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        private static string ScriptPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "tools", "cross-platform-determinism-check.wsl.sh");

        private static string DispatcherPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "..", ".claude", "workflows", "dw-burndown.workflow.js");

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
