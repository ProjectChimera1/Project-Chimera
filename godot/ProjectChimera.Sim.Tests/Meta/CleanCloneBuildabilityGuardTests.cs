#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-907 (residual of DW-905) — the clean-clone guard, in the established
    /// <c>AnalyzerGateGuardTests</c> / <c>NakamaTsValidatorCiGateGuardTests</c> convention: assert the WIRING as a
    /// Tier-1 invariant that runs on every <c>dotnet test</c>, never as a CI-only shell step.
    ///
    /// <para>The defect class: nothing in this repo ever verified that a FRESH CLONE produces a working build. Two
    /// instances surfaced months apart, both found by a human happening to set up a new machine, and both invisible
    /// to the full CI suite because CI ran on a checkout where the files already existed or were regenerated:</para>
    /// <list type="number">
    /// <item>DW-905 — a <c>bin/</c> .gitignore rule excluded the Terrain3D GDExtension binaries, so a fresh clone had
    /// the <c>terrain.gdextension</c> manifest but not the libraries it names. Godot did not merely warn: it DISABLED
    /// the addon and rewrote the tracked <c>project.godot</c>, silently diverging the new machine from every other one.</item>
    /// <item><c>docs/server-deploy/nakama-modules/build/</c> — gitignored and absent until someone runs
    /// <c>npm run build</c>; mitigated only by a hand-written entrypoint guard a previous author thought to add.</item>
    /// </list>
    ///
    /// <para>This guard closes the cheap half — and it is the half that would have caught DW-905 the moment the rule
    /// was written rather than N months later on a laptop. For every <c>[libraries]</c> entry of the shipped target
    /// platforms it asserts the file (a) exists on disk and (b) is TRACKED IN GIT. (b) is the load-bearing one: a
    /// .gitignore rule that re-excludes the binaries leaves them present on the machine that built them and missing
    /// on every clone, so an existence check alone would stay green exactly where DW-905 stayed green.</para>
    ///
    /// <para>Deliberately NOT asserted: the non-x86_64 / macos / android / ios / web rows. Those binaries are not
    /// committed and this project does not build for them today — asserting them would be a red test for a platform
    /// nobody targets. Widen <see cref="ShippedPlatformPrefixes"/> / <see cref="ShippedArchSuffix"/> in the same
    /// commit that starts shipping one.</para>
    ///
    /// <para>Paths are resolved portably from this source file via <see cref="CallerFilePathAttribute"/>, the same
    /// mechanism the sibling Meta guards use.</para>
    /// </summary>
    public class CleanCloneBuildabilityGuardTests
    {
        /// <summary>Repo-relative path of the GDExtension manifest under guard. One constant so the failure text and
        /// the assertions cannot drift apart.</summary>
        private const string GdExtensionRelPath = "godot/addons/terrain_3d/terrain.gdextension";

        /// <summary>The platform key prefixes this project actually builds and ships (see the class doc).</summary>
        private static readonly string[] ShippedPlatformPrefixes = { "windows.", "linux." };

        /// <summary>The architecture suffix whose binaries are committed.</summary>
        private const string ShippedArchSuffix = ".x86_64";

        /// <summary>Floor on how many shipped-platform library rows the parse must find (windows/linux ×
        /// debug/release). A floor rather than an exact count so adding a row never breaks the guard — its job is to
        /// fail loudly if the parse silently matches NOTHING, which would let every per-file check pass vacuously.</summary>
        private const int ShippedLibraryRowFloor = 4;

        [Fact]
        public void Terrain3dGdExtension_ShippedPlatformLibraries_ExistOnDisk()
        {
            var rows = ShippedLibraryRows();

            foreach ((string key, string resPath) in rows)
            {
                string abs = ResolveResPath(resPath);
                Assert.True(File.Exists(abs),
                    $"'{GdExtensionRelPath}' declares [libraries] {key} = \"{resPath}\", but no file exists at " +
                    $"'{abs}'. Godot does not warn about this — it DISABLES the addon and rewrites the tracked " +
                    $"project.godot ([editor_plugins] enabled), silently diverging this checkout from every other " +
                    $"one (DW-905). Either commit the binary or remove the row.");
            }
        }

        [Fact]
        public void Terrain3dGdExtension_ShippedPlatformLibraries_AreTrackedInGit()
        {
            var rows = ShippedLibraryRows();
            string repoRoot = RepoRoot();

            // Only meaningful inside a git checkout. A source archive / exported tree has no index to consult, and
            // the on-disk half above still runs there. Never a silent skip in the environments that matter: dev
            // machines and the CI runner both check out with git.
            if (!GitAvailable(repoRoot)) return;

            foreach ((string key, string resPath) in rows)
            {
                string relFromRepo = RepoRelative(repoRoot, ResolveResPath(resPath));
                Assert.True(GitTracks(repoRoot, relFromRepo),
                    $"'{GdExtensionRelPath}' declares [libraries] {key} = \"{resPath}\", and the file exists on THIS " +
                    $"machine, but git does not track '{relFromRepo}'. That is exactly DW-905: the binary is present " +
                    $"wherever it was built and absent from every fresh clone, where Godot then disables the addon " +
                    $"and rewrites the tracked project.godot. Check for a .gitignore rule swallowing it (a bare " +
                    $"'bin/' rule was the culprit) and commit the file, or drop the row from the manifest.");
            }
        }

        // ── .gdextension parsing ─────────────────────────────────────────────────

        /// <summary>Parse the <c>[libraries]</c> section and return the rows for the shipped platforms/arch.</summary>
        private static List<(string Key, string ResPath)> ShippedLibraryRows()
        {
            string manifest = GdExtensionPath();
            Assert.True(File.Exists(manifest),
                $"'{GdExtensionRelPath}' not found at '{manifest}'. This path is derived from [CallerFilePath]; if " +
                $"the addon moved or was removed, move this guard with it rather than deleting it — the clean-clone " +
                $"hole it covers (DW-907) is a class, not one file.");

            var rows = new List<(string, string)>();
            bool inLibraries = false;
            foreach (string raw in File.ReadAllLines(manifest))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)
                                     || line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inLibraries = line.Equals("[libraries]", StringComparison.Ordinal);
                    continue;
                }
                if (!inLibraries) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim().Trim('"');
                if (value.Length == 0) continue;

                if (ShippedPlatformPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))
                    && key.EndsWith(ShippedArchSuffix, StringComparison.Ordinal))
                    rows.Add((key, value));
            }

            Assert.True(rows.Count >= ShippedLibraryRowFloor,
                $"Parsed only {rows.Count} shipped-platform [libraries] rows out of '{GdExtensionRelPath}' " +
                $"(expected at least {ShippedLibraryRowFloor}: windows/linux × debug/release {ShippedArchSuffix}). " +
                $"Either the manifest's format changed and this parser no longer matches — in which case the guard " +
                $"would pass over ZERO files and prove nothing — or rows were deleted. Investigate before lowering " +
                $"the floor.");
            return rows;
        }

        /// <summary>Map a <c>res://</c> path to an absolute OS path. <c>res://</c> is the Godot project root, which
        /// in this repo is <c>godot/</c>.</summary>
        private static string ResolveResPath(string resPath)
        {
            Assert.StartsWith("res://", resPath, StringComparison.Ordinal);
            string relative = resPath.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(GodotProjectDir(), relative));
        }

        // ── git interrogation ────────────────────────────────────────────────────

        private static bool GitAvailable(string repoRoot) =>
            TryRunGit(repoRoot, "rev-parse --git-dir", out int exit) && exit == 0;

        private static bool GitTracks(string repoRoot, string repoRelativePath) =>
            TryRunGit(repoRoot, $"ls-files --error-unmatch -- \"{repoRelativePath}\"", out int exit) && exit == 0;

        /// <summary>Run <c>git</c> in <paramref name="repoRoot"/>. Returns false when git could not be launched at
        /// all (absent from PATH, sandboxed) — distinct from git running and reporting a non-zero exit, which is a
        /// real answer and comes back as true with that code.</summary>
        private static bool TryRunGit(string repoRoot, string arguments, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory       = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.StandardOutput.ReadToEnd();   // drain before waiting so a chatty git can never deadlock us
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60_000)) return false;
                exitCode = p.ExitCode;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string RepoRelative(string repoRoot, string absolute) =>
            Path.GetRelativePath(repoRoot, absolute).Replace('\\', '/');

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        /// <summary>&lt;repo&gt;/godot — the Godot project root, i.e. what <c>res://</c> resolves to.</summary>
        private static string GodotProjectDir([CallerFilePath] string p = "") => ResolveFromHere(p, "..", "..");

        /// <summary>&lt;repo&gt; — the repository root.</summary>
        private static string RepoRoot([CallerFilePath] string p = "") => ResolveFromHere(p, "..", "..", "..");

        /// <summary>&lt;repo&gt;/godot/addons/terrain_3d/terrain.gdextension.</summary>
        private static string GdExtensionPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "addons", "terrain_3d", "terrain.gdextension");

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
