#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-795 / DW-760 — the PRIVATE-TWIN guard for shared test infrastructure.
    ///
    /// <para><b>The defect class.</b> A guard needs a helper; the helper already exists somewhere else in the test
    /// tree; copying it is one keystroke cheaper than referencing it. The copy is behaviour-identical on the day it
    /// is made and then drifts — and because the copies live inside TESTS, nothing downstream fails when they
    /// disagree. The guard behind the drifted copy just quietly weakens. Two instances were on the ledger at once:
    /// DW-795 (a private twin of <see cref="CSharpSourceScan"/>'s comment/literal stripper inside
    /// <c>NullableContextHygieneTests</c> — a drifted stripper silently un-blinds or over-blinds a source scan) and
    /// DW-760 (a second copy of the shipped-ability-roster FLOOR, so a deliberate roster change had to update two
    /// numbers nothing kept in sync, and either could sink below the real count and go back to being the vacuous
    /// guard DW-107/DW-536 were written to remove).</para>
    ///
    /// <para><b>Why a source scan.</b> Both twins compile, pass, and are invisible to every behavioural test —
    /// duplication leaves no trace in metadata. Only a scan of the test tree's own source can see it. Same
    /// mechanism and portable <see cref="CallerFilePathAttribute"/> tree location as
    /// <c>ReflectionProbeAdoptionGuardTests</c> / <c>NullableContextHygieneTests</c>.</para>
    ///
    /// <para><b>On the allowlist below.</b> It is DEBT, deliberately made visible rather than silently tolerated:
    /// three older private strippers predate the shared one and sit outside DW-795's named scope. Adding a file to
    /// it is a decision to be argued for; the correct move for new code is always the shared helper.</para>
    /// </summary>
    public class SharedTestHelperTwinGuardTests
    {
        // ── DW-795: one comment/literal stripper for the whole suite ─────────────────────────────────────────

        /// <summary>The shared implementation's own file — the OWNER, not a twin.</summary>
        private const string ScannerOwner = "Meta/CSharpSourceScan.cs";

        /// <summary>
        /// Pre-existing private strippers, recorded as known debt rather than excused silently. Each predates
        /// <see cref="CSharpSourceScan"/> and sits outside the scope of the entry that extracted it, so folding
        /// them in is its own change (they are NOT all identical — two are scoped-down variants — which is exactly
        /// why they need reading before merging, and exactly why they are the drift risk this guard describes).
        /// Removing an entry here after routing that file through the shared helper is always safe; ADDING one is
        /// re-opening the defect.
        /// </summary>
        private static readonly string[] KnownPrivateStripperDebt =
        {
            "Combat/CombatCommandSwitchCompletenessTests.cs",
            "Meta/PositionWriterGuardTests.cs",
            "Multiplayer/StartPathTeamAgreementTests.cs",
        };

        /// <summary>A DECLARATION of one of the shared scanner's members (a call site can never match: the name is
        /// preceded here by an access modifier and <c>static</c>, and followed by a parameter list).</summary>
        private static readonly Regex SharedScannerDeclaration = new(
            @"\b(?:private|public|internal|protected)\s+(?:static\s+)?(?:string|int)\s+(?<name>StripCommentsAndLiterals|LineOf)\s*\(",
            RegexOptions.Compiled);

        [Fact]
        public void NoTestSource_KeepsAPrivateTwinOfTheSharedSourceScanner()
        {
            string root = TestProjectRoot();
            var offenders = new List<string>();

            foreach (string path in EnumerateTestSources(root))
            {
                string relative = Relative(root, path);
                if (relative.Equals(ScannerOwner, StringComparison.OrdinalIgnoreCase)) continue;
                if (KnownPrivateStripperDebt.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;

                string code = CSharpSourceScan.StripCommentsAndLiterals(File.ReadAllText(path));
                foreach (Match m in SharedScannerDeclaration.Matches(code))
                    offenders.Add($"{relative}({CSharpSourceScan.LineOf(code, m.Index)}): {m.Groups["name"].Value}");
            }

            Assert.True(offenders.Count == 0,
                $"{offenders.Count} private twin(s) of {ScannerOwner} were declared in the test tree " +
                $"(DW-795):{Environment.NewLine}  " +
                $"{string.Join(Environment.NewLine + "  ", offenders)}{Environment.NewLine}" +
                $"A second copy of a source-stripping heuristic is behaviour-identical the day it is written and " +
                $"drifts after — and a drifted stripper silently weakens whichever source guard is behind it, with " +
                $"nothing to fail. Fix: call ProjectChimera.Sim.Tests.Meta.CSharpSourceScan (or add " +
                $"'using static ProjectChimera.Sim.Tests.Meta.CSharpSourceScan;', the idiom HotkeyTierGuardTests " +
                $"uses) and delete the copy. If the copy is genuinely needed, extend the SHARED one.");
        }

        /// <summary>
        /// The teeth for the allowlist itself: an entry naming a file that no longer HAS a private stripper is a
        /// stale excuse, and one naming a file that does not exist is a typo silently widening the exemption.
        /// Both make the guard above quietly weaker, which is the same silent-degradation shape it exists to stop.
        /// </summary>
        [Fact]
        public void EveryPrivateStripperDebtEntry_StillNamesARealTwin()
        {
            string root = TestProjectRoot();

            foreach (string relative in KnownPrivateStripperDebt)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"DW-795 allowlist entry '{relative}' names a file that does not exist under '{root}' — it " +
                    $"exempts nothing and hides a typo. Correct the path or delete the entry.");

                string code = CSharpSourceScan.StripCommentsAndLiterals(File.ReadAllText(path));
                Assert.True(SharedScannerDeclaration.IsMatch(code),
                    $"DW-795 allowlist entry '{relative}' no longer declares a private stripper — the debt was " +
                    $"paid off. DELETE the entry so the guard covers this file again.");
            }
        }

        // ── DW-760: one shipped-ability-roster floor for the whole suite ─────────────────────────────────────

        /// <summary>The shared implementation's own file — the OWNER of the floor constant.</summary>
        private const string FloorOwner = "RealContentFixture.cs";

        /// <summary>An <c>int</c> field declaration, in either house naming convention
        /// (<c>MIN_SHIPPED_ABILITY_COUNT</c> / <c>MinShippedAbilityCount</c>); the name is classified below.</summary>
        private static readonly Regex IntFieldDeclaration = new(
            @"\b(?:private|public|internal|protected)?\s*(?:const|static\s+readonly)\s+int\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=",
            RegexOptions.Compiled);

        [Fact]
        public void NoTestSource_KeepsAPrivateCopyOfTheShippedAbilityFloor()
        {
            string root = TestProjectRoot();
            var offenders = new List<string>();

            foreach (string path in EnumerateTestSources(root))
            {
                string relative = Relative(root, path);
                if (relative.Equals(FloorOwner, StringComparison.OrdinalIgnoreCase)) continue;

                string code = CSharpSourceScan.StripCommentsAndLiterals(File.ReadAllText(path));
                foreach (Match m in IntFieldDeclaration.Matches(code))
                {
                    if (!IsShippedAbilityFloorName(m.Groups["name"].Value)) continue;
                    offenders.Add($"{relative}({CSharpSourceScan.LineOf(code, m.Index)}): {m.Groups["name"].Value}");
                }
            }

            Assert.True(offenders.Count == 0,
                $"{offenders.Count} private copy/copies of the shipped-ability-roster floor were declared outside " +
                $"{FloorOwner} (DW-760):{Environment.NewLine}  " +
                $"{string.Join(Environment.NewLine + "  ", offenders)}{Environment.NewLine}" +
                $"A second floor is a second number a deliberate roster change has to remember, and nothing keeps " +
                $"them in sync — one drifts below the real count and its guard silently becomes vacuous again " +
                $"(the exact regression DW-107/DW-536 closed). Fix: use " +
                $"ProjectChimera.Sim.Tests.RealContentFixture.MinShippedAbilityCount and its guarded load.");
        }

        /// <summary>Name-shape test for the floor constant, convention-agnostic: <c>MIN_SHIPPED_ABILITY_COUNT</c>,
        /// <c>MinShippedAbilityCount</c> and <c>ShippedAbilityFloor</c> all classify the same.</summary>
        private static bool IsShippedAbilityFloorName(string name)
        {
            string flat = name.Replace("_", string.Empty).ToLowerInvariant();
            return flat.Contains("shipped", StringComparison.Ordinal)
                && flat.Contains("abilit", StringComparison.Ordinal);
        }

        // ── scanning plumbing (mirrors ReflectionProbeAdoptionGuardTests) ────────────────────────────────────

        /// <summary>The test assembly's own sources — build output (obj/, bin/) excluded: generated
        /// AssemblyInfo/AssemblyAttributes files are not authored code and must not be able to fail this guard.</summary>
        private static IEnumerable<string> EnumerateTestSources(string root) =>
            Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !IsBuildOutput(root, p))
                     .OrderBy(p => p, StringComparer.Ordinal);

        private static bool IsBuildOutput(string root, string path)
        {
            string rel = Relative(root, path);
            return rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Relative(string root, string path) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/')
                : path;

        /// <summary>The ProjectChimera.Sim.Tests project root — one directory up from this file (…/Meta/), resolved
        /// via <see cref="CallerFilePathAttribute"/> so there is no hardcoded absolute path and the guard runs
        /// unchanged on a CI checkout.</summary>
        private static string TestProjectRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, ".."));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-795/DW-760 guard could not locate the test source tree. Resolved path: '{root}'. " +
                    "This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
