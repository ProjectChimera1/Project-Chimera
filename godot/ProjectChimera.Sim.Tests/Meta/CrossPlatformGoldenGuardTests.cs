#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Story 1.10c (AC4 — AR-37) — permanently locks the cross-OS portability invariant the Windows↔Linux
    /// golden-checksum gate rests on: the committed goldens are stored <b>LF-only</b> (zero <c>\r</c> bytes).
    ///
    /// Why this matters: the gate proves Linux computes byte-identical <c>SimChecksum</c>s by verifying both OSes
    /// against the SAME committed golden. That only holds if the golden bytes are identical on a Windows checkout
    /// and a Linux checkout. Three layers already neutralize line-endings — <c>godot/.gitattributes</c>
    /// (<c>eol=lf</c>), embedded-resource loading (no file paths), and the <c>\r</c>-tolerant
    /// <c>GoldenChecksumReplay.ParseGolden</c>. This guard makes the FIRST layer's outcome permanent at the byte
    /// level: if any golden ever lands with a CRLF (a misconfigured editor, a deleted <c>.gitattributes</c>, a
    /// hand-edit on Windows), the developer's own <c>dotnet test</c> goes red HERE — before a spurious Win↔Linux
    /// diff can ever appear — instead of the regression hiding until someone runs the cross-platform check.
    ///
    /// This asserts the STATUS QUO (all four goldens are already pure LF today; Story 1.10c changes no golden).
    /// The value is that the assertion now runs on every <c>dotnet test</c> — locally, in the Windows CI gate,
    /// AND on the WSL/ubuntu Linux leg (it just reads bytes, so it runs everywhere the suite runs).
    ///
    /// Paths are resolved portably from this source file via <see cref="CallerFilePathAttribute"/> — the same
    /// mechanism <c>DependencyHygieneTests</c>, <c>AnalyzerGateGuardTests</c>, and
    /// <c>GoldenChecksumReplay.GoldenSourcePath</c> use — so there is no hardcoded absolute path and it resolves
    /// correctly on a CI checkout and on the WSL <c>/mnt/d</c> path alike.
    /// </summary>
    public class CrossPlatformGoldenGuardTests
    {
        /// <summary>
        /// Floor on the committed golden count. Goldens are append-only in this project ("the goldens above are
        /// never re-recorded") so this is a MINIMUM, not an exact count — adding a golden never breaks it. Its
        /// purpose is to fail loudly if <see cref="CallerFilePathAttribute"/> resolution ever points at an empty
        /// or wrong directory, which would otherwise let the per-file CR check pass vacuously over zero files.
        /// </summary>
        private const int KnownGoldenCountFloor = 4;

        [Fact]
        public void AllCommittedGoldens_AreStoredLfOnly_NoCarriageReturnBytes()
        {
            string goldenDir = GoldenDir();
            Assert.True(Directory.Exists(goldenDir),
                $"Golden directory not found at '{goldenDir}'. This path is derived from [CallerFilePath]; if the " +
                $"test-project layout moved, update {nameof(GoldenDir)} in this guard.");

            string[] goldens = Directory.GetFiles(goldenDir, "*.golden.txt");
            Assert.True(goldens.Length >= KnownGoldenCountFloor,
                $"Expected at least {KnownGoldenCountFloor} committed goldens under '{goldenDir}', found {goldens.Length}. " +
                $"Either [CallerFilePath] resolution is pointing at the wrong directory (a vacuous-pass hazard this floor " +
                $"guards against), or a golden was deleted — goldens are append-only, so investigate before lowering the floor.");

            foreach (string path in goldens.OrderBy(p => p, StringComparer.Ordinal))
            {
                byte[] bytes = File.ReadAllBytes(path);
                int crIndex = Array.IndexOf(bytes, (byte)'\r');
                Assert.True(crIndex < 0,
                    $"Golden '{Path.GetFileName(path)}' contains a carriage-return (\\r, 0x0D) byte at offset {crIndex}. " +
                    $"Goldens MUST be stored LF-only so a Windows checkout and a Linux checkout embed byte-identical " +
                    $"resources — otherwise the Windows↔Linux cross-platform gate (Story 1.10c / AR-37) reports a SPURIOUS " +
                    $"diff. Fix: ensure godot/.gitattributes still declares 'eol=lf', renormalize the file to LF " +
                    $"(e.g. `git add --renormalize` or re-save with Unix line endings), and re-commit. Do NOT 'fix' it by " +
                    $"re-recording the golden.");
            }
        }

        [Fact]
        public void GitAttributes_DeclaresLfNormalization_SoTheGoldensCheckOutLfOnBothOs()
        {
            // Belt to the CR-byte test's suspenders: the byte check above catches a golden that ALREADY went CRLF;
            // this catches the upstream cause — the git-side normalization rule being weakened or deleted — before a
            // future re-checkout/renormalize can reintroduce CRLF. .gitattributes lives under godot/ (NOT repo root),
            // and its '*' wildcard covers everything under godot/, including the goldens.
            string gitAttributes = GitAttributesPath();
            Assert.True(File.Exists(gitAttributes),
                $"godot/.gitattributes not found at '{gitAttributes}'. It declares 'eol=lf', which is what makes the " +
                $"committed goldens check out LF-only on BOTH Windows and Linux (the AR-37 cross-platform invariant). " +
                $"If it was deleted, the goldens can silently re-acquire CRLF on a Windows renormalize → spurious Win↔Linux diff.");

            string[] lines = File.ReadAllLines(gitAttributes);
            bool declaresLf = lines.Any(l =>
            {
                string trimmed = l.Trim();
                return trimmed.Length > 0
                       && !trimmed.StartsWith("#", StringComparison.Ordinal)
                       && trimmed.IndexOf("eol=lf", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            Assert.True(declaresLf,
                $"godot/.gitattributes exists but no active (non-comment) line declares 'eol=lf'. That normalization is " +
                $"the git-side guarantee the goldens check out LF on Linux as on Windows (AR-37). Restore an 'eol=lf' " +
                $"rule covering the Golden/ path (the existing '* text=auto eol=lf' wildcard covers it).");
        }

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        /// <summary>godot/ProjectChimera.Sim.Tests/Golden/ — one directory up from this file (…/Meta/), then into Golden/.</summary>
        private static string GoldenDir([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "Golden");

        /// <summary>godot/.gitattributes — two directories up from this file (…/ProjectChimera.Sim.Tests/Meta/).</summary>
        private static string GitAttributesPath([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", ".gitattributes");

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
