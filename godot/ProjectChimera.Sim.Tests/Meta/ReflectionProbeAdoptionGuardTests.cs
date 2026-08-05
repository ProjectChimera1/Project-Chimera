#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-501 — the reflection-probe adoption guard.
    ///
    /// <para>A white-box test that probes a private member BY NAME and suppresses the nullable warning with a
    /// null-forgiving <c>!</c> is a trap: the <c>!</c> is erased at compile time and does nothing at runtime, so a
    /// RENAMED (or removed, or re-signatured) member does not fail where the probe is written — it fails later, as an
    /// opaque <see cref="NullReferenceException"/> at the use site, naming neither the owner type nor the member. The
    /// test reads as a regression in the code under test when in fact the test has simply gone stale.</para>
    ///
    /// <para>DW-218 removed that idiom from <c>TimerDeterminismTests</c> and introduced
    /// <see cref="ProjectChimera.Sim.Tests.Golden.ReflectionProbe"/>, whose every lookup fails LOUD and NAMED. But
    /// nothing in the suite could see the idiom, so two more sites survived the cleanup untouched
    /// (<c>NodeKindsLockstepTests</c>, <c>TriggerGraphLiveLoweringTests</c>) and were only found by a later manual
    /// sweep — which is exactly the "spread while nobody was looking" shape DW-213 hit. This guard is that missing
    /// eye: the fix and the guard land together so the third recurrence cannot be silent.</para>
    ///
    /// <para>It is a SOURCE scan because the defect is a source-level idiom — <c>!</c> leaves no trace in metadata
    /// whatsoever. It walks the Tier-1 test assembly's own <c>*.cs</c>, blanks comments and string literals (so the
    /// prose above, and the probe's own diagnostics, are not reported as offenders), and asserts that no reflection
    /// member LOOKUP is immediately null-forgiven.</para>
    /// </summary>
    public class ReflectionProbeAdoptionGuardTests
    {
        /// <summary>
        /// The reflection lookups that return <c>null</c> for a MISSING member — the ones where <c>!</c> converts a
        /// stale probe into an unexplained NRE.
        ///
        /// <para><c>GetProperty</c> is deliberately absent: <c>System.Text.Json.JsonElement.GetProperty</c> shares the
        /// name, THROWS rather than returning null, and is used constantly across these tests — banning the token
        /// outright would turn the suite red over correct code. If a reflected property probe is ever needed, add a
        /// <c>ReflectionProbe.Property</c> helper (and tighten this list against a reflection-shaped receiver) rather
        /// than reintroducing the raw idiom.</para>
        ///
        /// <para><c>GetValue</c> is likewise absent: it is a null-VALUE suppression, not a missing-MEMBER one, and it
        /// is legitimately used on <c>FieldInfo</c>s obtained from <c>GetFields()</c> enumeration (which can never be
        /// null). <see cref="ProjectChimera.Sim.Tests.Golden.ReflectionProbe.Read{T}"/> is the better tool there, but
        /// it is a separate, lower-severity concern than a probe that cannot say which member moved.</para>
        /// </summary>
        private static readonly string[] NullableMemberLookups =
        {
            "GetField", "GetMethod", "GetNestedType", "GetConstructor",
        };

        /// <summary>
        /// The two files DW-501 named, pinned by path. The tree-wide scan below subsumes them, but pinning the
        /// specific regression means it cannot come back silently even if the general scan were ever weakened or
        /// scoped down — the same belt-and-braces DW-213's guard uses. Paths are relative to the test project root.
        /// </summary>
        private static readonly string[] Dw501Files =
        {
            "Dsl/NodeKindsLockstepTests.cs",         // t.GetField("_triggerEventTypes", F)!.GetValue(null) ×4
            "Dsl/TriggerGraphLiveLoweringTests.cs",  // typeof(ScenarioDirector).GetField("_execs", …)!
        };

        [Fact]
        public void Dw501Files_ProbePrivateStateThroughTheNullCheckedHelper()
        {
            string root = TestProjectRoot();

            foreach (string relative in Dw501Files)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"DW-501 guard could not find '{relative}' under '{root}'. If the file moved, update {nameof(Dw501Files)}.");

                string text = File.ReadAllText(path);
                Assert.True(text.Contains("ReflectionProbe", StringComparison.Ordinal),
                    $"'{relative}' probes private state by name but no longer routes through ReflectionProbe (DW-501). " +
                    "Restore the null-checked lookup: a rename must fail with a diagnostic naming the owner and the " +
                    "member, never an NRE at the assertion.");
            }
        }

        [Fact]
        public void NoTestSource_NullForgivesAReflectionMemberLookup()
        {
            string root = TestProjectRoot();
            var offenders = new List<string>();

            foreach (string path in EnumerateTestSources(root))
            {
                string text = File.ReadAllText(path);
                string code = CSharpSourceScan.StripCommentsAndLiterals(text);

                foreach ((int line, string lookup) in NullForgivenLookups(code))
                    offenders.Add($"{Relative(root, path)}({line}): {lookup}(…)!");
            }

            Assert.True(offenders.Count == 0,
                $"{offenders.Count} reflection member lookup(s) are null-forgiven with '!' instead of null-CHECKED " +
                $"(DW-218 / DW-501):{Environment.NewLine}  " +
                $"{string.Join(Environment.NewLine + "  ", offenders)}{Environment.NewLine}" +
                $"The '!' does nothing at runtime, so renaming the probed member turns the test into an opaque " +
                $"NullReferenceException at the USE site — no owner type, no member name, no hint the test went " +
                $"stale. Fix: route the lookup through ProjectChimera.Sim.Tests.Golden.ReflectionProbe " +
                $"(Field / StaticField / PublicField / Method / NestedType, then Read<T> / ReadStatic<T>), which " +
                $"fails loud and named. If the member is genuinely optional, use an explicit null check with a " +
                $"message — never '!'.");
        }

        // ── scanning ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Every <c>GetField(…)! / GetMethod(…)! / …</c> in <paramref name="code"/>: a lookup name at a word boundary,
        /// its balanced argument list, and a <c>!</c> as the next non-whitespace character. <c>!=</c> is excluded —
        /// <c>GetField(…) != null</c> is the CORRECT idiom this guard wants people to reach for.
        /// </summary>
        private static IEnumerable<(int Line, string Lookup)> NullForgivenLookups(string code)
        {
            foreach (string lookup in NullableMemberLookups)
            {
                int from = 0;
                while (true)
                {
                    int at = code.IndexOf(lookup, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    from = at + lookup.Length;

                    // Word boundary on both sides: 'GetFields(' and 'MyGetField(' are different members.
                    if (at > 0 && IsIdentifierChar(code[at - 1])) continue;
                    int i = SkipWhitespace(code, at + lookup.Length);
                    if (i >= code.Length || code[i] != '(') continue;

                    int close = MatchParen(code, i);
                    if (close < 0) continue;

                    int bang = SkipWhitespace(code, close + 1);
                    if (bang >= code.Length || code[bang] != '!') continue;
                    if (bang + 1 < code.Length && code[bang + 1] == '=') continue;   // a real null CHECK

                    yield return (CSharpSourceScan.LineOf(code, at), lookup);
                }
            }
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int SkipWhitespace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        /// <summary>Index of the ')' closing the '(' at <paramref name="open"/>, or -1. Literals are already blanked,
        /// so no parenthesis inside a string or comment can unbalance the count.</summary>
        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

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

        /// <summary>The ProjectChimera.Sim.Tests project root — one directory up from this file (…/Meta/). Resolved via
        /// <see cref="CallerFilePathAttribute"/> so there is no hardcoded absolute path and the guard runs unchanged on
        /// a CI checkout (the same mechanism <c>NullableContextHygieneTests</c> and <c>DependencyHygieneTests</c> use).</summary>
        private static string TestProjectRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, ".."));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-501 guard could not locate the test source tree. Resolved path: '{root}'. " +
                    "This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
