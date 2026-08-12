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
    /// DW-213 — the nullable-annotation-context guard.
    ///
    /// <para>A nullable reference-type annotation (<c>string?</c>, <c>Foo?</c>, <c>bool[]?</c>) written in a file that is
    /// NOT inside a <c>#nullable</c> annotations context produces compiler warning <b>CS8632</b> and — worse than the
    /// noise — the annotation is silently IGNORED: it documents an intent the compiler never enforces and never emits
    /// into metadata. The ledger entry recorded two such files; by the time the entry was worked it had spread to four
    /// (<c>FlowFieldSystem</c>, <c>EntityWorld</c>, <c>ResourceNodeStore</c>, <c>ResourceStore</c>), because nothing in
    /// the suite could see the class re-opening. This guard is that missing eye.</para>
    ///
    /// <para>It is a SOURCE scan, not a metadata scan, because CS8632 is a source-level diagnostic: it walks
    /// <c>godot/src/**.cs</c>, skips every file that already declares <c>#nullable enable</c> (414 of 440 at the time of
    /// writing — the house convention), strips comments/strings from the rest, and asserts that none of them writes a
    /// nullable annotation on a REFERENCE type. Value-type annotations (<c>int?</c>) are legal without the directive and
    /// are deliberately not flagged.</para>
    ///
    /// <para>The scanned tree is located portably via <see cref="CallerFilePathAttribute"/> — the same mechanism
    /// <c>DependencyHygieneTests</c> and <c>GoldenChecksumReplay.GoldenSourcePath</c> use — so there is no hardcoded
    /// absolute path and the guard runs unchanged on a CI checkout.</para>
    /// </summary>
    public class NullableContextHygieneTests
    {
        /// <summary>
        /// The files DW-213 named (plus the one it recorded as already fixed). Pinned by name so the specific
        /// regression the ledger entry describes cannot come back silently even if the tree-wide scan below were ever
        /// weakened. Paths are relative to <c>godot/src/</c>.
        /// </summary>
        private static readonly string[] Dw213Files =
        {
            "Navigation/FlowFieldSystem.cs",   // out FlowField? / bool[]? _staticBlocked
            "Economy/GatheringSystem.cs",      // MatchStats? (already carried the directive when DW-213 was worked)
            "Core/EntityWorld.cs",             // PathabilityGrid? Pathability + SetPathabilityGrid(PathabilityGrid?)
            "Core/ResourceNodeStore.cs",       // string? requiresStructureId
            "Core/ResourceStore.cs",           // SupplyConfig? config
        };

        [Fact]
        public void Dw213Files_DeclareTheNullableDirective()
        {
            string root = SrcRoot();

            foreach (string relative in Dw213Files)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path),
                    $"DW-213 guard could not find '{relative}' under '{root}'. If the file moved, update {nameof(Dw213Files)}.");

                Assert.True(DeclaresNullableEnable(File.ReadAllText(path)),
                    $"'{relative}' carries nullable reference-type annotations but no '#nullable enable' directive " +
                    $"(DW-213). Every annotation in it is a CS8632 warning AND a silently-ignored annotation. " +
                    $"Restore the '#nullable enable' line at the top of the file, or remove the annotations.");
            }
        }

        [Fact]
        public void NoShippingSourceFile_AnnotatesAReferenceType_OutsideANullableContext()
        {
            string root = SrcRoot();
            var offenders = new List<string>();

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                string text = File.ReadAllText(path);
                if (DeclaresNullableEnable(text)) continue;   // inside an annotations context — CS8632 impossible.

                string code = CSharpSourceScan.StripCommentsAndLiterals(text);
                foreach ((int line, string snippet) in ReferenceTypeAnnotations(code))
                    offenders.Add($"{Relative(root, path)}({line}): {snippet}");
            }

            Assert.True(offenders.Count == 0,
                $"{offenders.Count} nullable REFERENCE-type annotation(s) live outside a '#nullable' annotations " +
                $"context (CS8632 — the annotation is ignored by the compiler, DW-213):{Environment.NewLine}  " +
                $"{string.Join(Environment.NewLine + "  ", offenders)}{Environment.NewLine}" +
                $"Fix: add '#nullable enable' as the first line of each file listed (the house convention — 414 of 440 " +
                $"shipping source files already do), then correct any nullable-flow warnings the directive surfaces. " +
                $"Removing the '?' is the only other acceptable fix. If a flagged declaration is a VALUE type (a " +
                $"struct — legal to annotate without the directive) this is a false positive; adding '#nullable enable' " +
                $"is still the correct, behaviour-neutral remedy.");
        }

        // ── scanning helpers ─────────────────────────────────────────────────────

        /// <summary>True when the file opts into a nullable ANNOTATIONS context anywhere (the directive is usually
        /// line 1, but a leading <c>#if</c> can precede it). A bare <c>#nullable disable</c> deliberately does NOT
        /// satisfy this — it re-opens CS8632.</summary>
        private static bool DeclaresNullableEnable(string text)
        {
            foreach (string raw in text.Split('\n'))
                if (raw.TrimStart().StartsWith("#nullable enable", StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// A nullable annotation in a DECLARATION position: <c>Type? name</c> followed by one of <c>; , = ) {</c>
        /// (field, property, parameter, local, lambda). A ternary can never match — its true-branch is always followed
        /// by <c>:</c>, which is not a terminator here.
        /// </summary>
        private static readonly Regex DeclarationAnnotation = new(
            @"(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*(?:\s*<[^<>;{}()]*>)?(?<arr>(?:\s*\[\s*,*\s*\])*))\s*\?\s+@?[A-Za-z_][A-Za-z0-9_]*\s*(?:[;,=)\{])",
            RegexOptions.Compiled);

        /// <summary>
        /// A nullable RETURN type: a member declaration whose modifier run anchors the match, so a ternary followed by
        /// a call (<c>Ready ? Compute() : Fallback()</c>) cannot be mistaken for one.
        /// </summary>
        private static readonly Regex ReturnTypeAnnotation = new(
            @"^[ \t]*(?:(?:public|private|protected|internal|static|virtual|override|sealed|abstract|new|partial|async|extern|unsafe|readonly)[ \t]+)+(?<type>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*(?:\s*<[^<>;{}()]*>)?(?<arr>(?:\s*\[\s*,*\s*\])*))\s*\?\s+@?[A-Za-z_][A-Za-z0-9_]*\s*[(<]",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Reference-type keywords that are unambiguously annotatable-only-in-context.</summary>
        private static readonly HashSet<string> ReferenceKeywords = new(StringComparer.Ordinal) { "string", "object", "dynamic" };

        /// <summary>
        /// Simple names of every REFERENCE type compiled into this assembly. The Tier-1 test assembly compiles the whole
        /// Godot-free sim source set (see <c>GodotFreeBoundaryTest</c>), so this resolves the sim layer's own types
        /// EXACTLY rather than guessing from their casing — <c>Fixed?</c> / <c>FixedVec3?</c> are nullable VALUE types
        /// (legal without the directive) while <c>PathabilityGrid?</c> / <c>SupplyConfig?</c> / <c>FlowField?</c> are
        /// not. Self-maintaining: a new sim type joins the set the moment it compiles.
        /// </summary>
        private static readonly HashSet<string> SimReferenceTypeNames = BuildSimReferenceTypeNames();

        private static HashSet<string> BuildSimReferenceTypeNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type t in typeof(ProjectChimera.Core.Fixed).Assembly.GetTypes())
            {
                if (t.IsValueType || t.IsEnum) continue;
                string name = t.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Every <c>Type?</c> in <paramref name="code"/> whose annotated type is PROVABLY a reference type: an array
        /// (always a reference type), <c>string</c>/<c>object</c>/<c>dynamic</c>, or a type this assembly compiles and
        /// knows to be a class/interface/delegate. An unrecognised name is deliberately NOT flagged — a false positive
        /// (a struct from a Godot-coupled assembly this test cannot see) would turn the suite red over legal code,
        /// which is strictly worse than missing a warning outside the sim layer.
        /// </summary>
        private static IEnumerable<(int Line, string Snippet)> ReferenceTypeAnnotations(string code)
        {
            foreach (Regex rx in new[] { DeclarationAnnotation, ReturnTypeAnnotation })
            {
                foreach (Match m in rx.Matches(code))
                {
                    string type = m.Groups["type"].Value;
                    bool isArray = m.Groups["arr"].Value.Trim().Length > 0;
                    string bare = BareTypeName(type);

                    bool isReference = isArray
                                       || ReferenceKeywords.Contains(bare)
                                       || SimReferenceTypeNames.Contains(bare);
                    if (!isReference) continue;

                    yield return (CSharpSourceScan.LineOf(code, m.Index), Collapse(m.Value));
                }
            }
        }

        /// <summary>The final identifier of a (possibly qualified/generic/array) type name — <c>A.B.List&lt;T&gt;[]</c> ⇒ <c>List</c>.</summary>
        private static string BareTypeName(string type)
        {
            int generic = type.IndexOf('<');
            string head = generic >= 0 ? type.Substring(0, generic) : type;
            int bracket = head.IndexOf('[');
            if (bracket >= 0) head = head.Substring(0, bracket);
            string[] parts = head.Split('.');
            return parts[parts.Length - 1].Trim();
        }

        private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        private static string Relative(string root, string path) =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/')
                : path;

        // DW-795 — the comment/literal stripper and the index→line helper used to live here as a private,
        // functionally identical TWIN of Meta/CSharpSourceScan. Two copies of a source-stripping heuristic drift,
        // and a drifted stripper silently weakens whichever guard is behind it (this one). Both now route through
        // the shared implementation; SharedTestHelperTwinGuardTests keeps a third copy from reappearing.

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Meta/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-213 guard could not locate the shipping source tree. Resolved path: '{root}'. " +
                    $"This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
