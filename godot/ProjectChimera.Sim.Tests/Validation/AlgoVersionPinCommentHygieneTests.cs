#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-583 regression guard — AlgoVersion pin-COMMENT hygiene, the sibling of the DW-194 pin-NAME guard in
    /// <see cref="AlgoVersionTestNameHygieneTests"/>.
    ///
    /// <para><b>The rot.</b> Pin sites accreted a hand-copied bump log in their trailing comment
    /// (<c>Assert.Equal(14, CanonicalModelHash.AlgoVersion); // 9→10 (custom-events fold); Story 7.9: 10→11</c>).
    /// Every such copy is truthful as history and makes no claim about the present — but it stops wherever the
    /// author who wrote it stopped, so a reader skimming for "what version are we on" reads 11 while the pin says
    /// 14. Eight sites had rotted this way by the time DW-583 was filed, one of them (<c>// AlgoVersion (= 11)</c>)
    /// stating the stale number as a bare fact.</para>
    ///
    /// <para><b>The rule.</b> On any line whose CODE references <c>&lt;Family&gt;.AlgoVersion</c> and which carries a
    /// trailing <c>//</c> comment, the HIGHEST version number the comment claims must not be BELOW that family's
    /// current pinned constant. A comment naming no version is always fine (the preferred form — it cannot rot). A
    /// bump log whose newest entry IS the current pin is fine too, and stays fine until the next bump, at which
    /// point this guard turns red and forces the author to bring it current or delete it — the ratchet that stops
    /// the partial history re-accreting. The authoritative per-version rationale lives ONCE, on each AlgoVersion
    /// constant's own XML doc.</para>
    ///
    /// <para><b>Boundaries (deliberate).</b> Only the code half of a line is matched, so PROSE that merely mentions
    /// the symbol is untouched — a golden re-record narrative legitimately records the version in force when it was
    /// recorded (<c>ProceduralMapGeneratorTests</c>'s "Re-recorded for Story 7.8: … bumped 8→9"). Only the test
    /// assembly is scanned: DW-583's location class. Only unambiguous version notation counts as a claim
    /// (<c>vN</c>, <c>N→M</c>, <c>N-&gt;M</c>, <c>= N</c>); dotted story numbers ("Story 7.9", "11.6") are stripped
    /// first so a story tag is never mistaken for a version.</para>
    /// </summary>
    public class AlgoVersionPinCommentHygieneTests
    {
        /// <summary>The hash families whose pins this guard polices, with their CURRENT pinned constant.</summary>
        private static readonly (string Token, int Pinned)[] Families =
        {
            ("SimChecksum",        SimChecksum.AlgoVersion),
            ("CanonicalModelHash", CanonicalModelHash.AlgoVersion),
            ("StartStateHash",     StartStateHash.AlgoVersion),
            ("MatchAgreementHash", MatchAgreementHash.AlgoVersion),
            ("RulesetHash",        RulesetHash.AlgoVersion),
            ("ContentHash",        ContentHash.AlgoVersion),
        };

        /// <summary>Matches a QUALIFIED pin reference in code, e.g. <c>CanonicalModelHash.AlgoVersion</c>.</summary>
        private static readonly Regex QualifiedPin = new(
            @"\b(SimChecksum|CanonicalModelHash|StartStateHash|MatchAgreementHash|RulesetHash|ContentHash)\.AlgoVersion\b",
            RegexOptions.Compiled);

        // Version-claim notations. Dotted story numbers are stripped BEFORE these run.
        private static readonly Regex Arrow  = new(@"(\d+)\s*(?:→|->|-->)\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex VForm  = new(@"\bv(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EqForm = new(@"=\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex Dotted = new(@"\d+\.\d+", RegexOptions.Compiled);

        /// <summary>The line-comment marker, as data — see <c>PureCommentAndDocLines_AreNotPinSites</c>.</summary>
        private const string Slashes = "//";

        /// <summary>
        /// Version numbers a pin-line comment CLAIMS. Dotted story numbers ("Story 7.9") are stripped first, then
        /// only unambiguous version notation is read: arrow bumps (both endpoints), <c>vN</c>, and <c>= N</c>.
        /// Free-standing integers are deliberately NOT claims — prose is full of them.
        /// </summary>
        internal static IReadOnlyList<int> ClaimedVersions(string commentText)
        {
            string text = Dotted.Replace(commentText, " ");
            var numbers = new List<int>();

            foreach (Match m in Arrow.Matches(text))
            {
                numbers.Add(ParseInt(m.Groups[1].Value));
                numbers.Add(ParseInt(m.Groups[2].Value));
            }
            foreach (Match m in VForm.Matches(text)) numbers.Add(ParseInt(m.Groups[1].Value));
            foreach (Match m in EqForm.Matches(text)) numbers.Add(ParseInt(m.Groups[1].Value));

            return numbers;
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : -1;

        /// <summary>
        /// Split a source line into (code, trailing-comment) at the first <c>//</c>, or null when the line has no
        /// trailing comment or is a pure comment/XML-doc line (nothing but whitespace before the <c>//</c>).
        /// </summary>
        internal static (string Code, string Comment)? SplitTrailingComment(string line)
        {
            int at = line.IndexOf("//", StringComparison.Ordinal);
            if (at < 0) return null;
            string code = line.Substring(0, at);
            if (string.IsNullOrWhiteSpace(code)) return null; // pure comment / /// XML doc → prose, not a pin site
            return (code, line.Substring(at + 2));
        }

        // ── the guard ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void AlgoVersionPinComments_DoNotTrailTheCurrentPin()
        {
            string testsDir = TestsDir();
            Assert.True(Directory.Exists(testsDir), $"tests directory not found at '{testsDir}' (via [CallerFilePath]).");

            var offenders = new List<string>();
            int pinLinesWithClaims = 0;

            foreach (string file in Directory.GetFiles(testsDir, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var split = SplitTrailingComment(lines[i]);
                    if (split is null) continue;

                    Match pin = QualifiedPin.Match(split.Value.Code);
                    if (!pin.Success) continue;

                    var claims = ClaimedVersions(split.Value.Comment);
                    if (claims.Count == 0) continue; // version-neutral comment — cannot rot

                    pinLinesWithClaims++;

                    string family = pin.Groups[1].Value;
                    int pinned = Families.First(f => f.Token == family).Pinned;
                    int newest = claims.Max();
                    if (newest >= pinned) continue; // the log reaches the current pin (or beyond) — not misleading

                    offenders.Add(
                        $"{Path.GetFileName(file)}:{i + 1}: comment's newest version is {newest} but " +
                        $"{family}.AlgoVersion is {pinned} — a reader skimming this line for the current version is " +
                        $"misled. Delete the partial bump log (the history lives on {family}.AlgoVersion's XML doc) " +
                        $"or bring it current (DW-583).\n      {lines[i].Trim()}");
                }
            }

            Assert.True(offenders.Count == 0,
                "Stale AlgoVersion pin-comment(s) — the trailing bump log trails the pinned value:\n  "
                + string.Join("\n  ", offenders));

            // Defeat a vacuous pass (wrong directory / broken line-splitter / broken claim regex): the suite really
            // does carry version-claiming pin comments (the current ones, e.g. the SimChecksum "(21→22)" logs), so
            // observing NONE means the scan stopped seeing pin lines rather than that they are all clean.
            Assert.True(pinLinesWithClaims > 0,
                $"The scan of '{testsDir}' found no version-claiming AlgoVersion pin comment at all — the path, the " +
                "line splitter, or the claim notation drifted (a vacuous-pass hazard), not a genuinely clean suite.");
        }

        // ── the extractor's own teeth (real comment text from this suite's history) ─────────────────────

        [Theory]
        // The DW-583 rot class — every one of these was a real trailing comment on a pin asserting 14.
        [InlineData(" 7.5 re-land merge bumped 9→10 (custom-events registry + graph node-kind fold); Story 7.9: 10→11 (Button fold)", new[] { 9, 10, 10, 11 })]
        [InlineData(" v9 = Story 7.8 custom UI; v10 = Story 7.5 (merge) custom-event registry fold; v11 = Story 7.9 Button fold", new[] { 9, 10, 11 })]
        [InlineData(" Story 7.5 (merge): custom-event registry folded (9→10); Story 7.9: Button fold (10→11)", new[] { 9, 10, 10, 11 })]
        [InlineData(" AlgoVersion (= 11)", new[] { 11 })]
        // Current logs — legal until the next bump.
        [InlineData(" Story 11.6: production-queue + head-timer folded (21→22)", new[] { 21, 22 })]
        [InlineData(" v22 = Story 11.6 production-queue + head-timer fold", new[] { 22 })]
        // Version-neutral / non-claims — the preferred form, and the story-tag false-positive guard.
        [InlineData(" bump history: CanonicalModelHash.AlgoVersion's XML doc", new int[0])]
        [InlineData(" Story 7.9 landed the Button fold", new int[0])]
        [InlineData(" reinterpret-Type needs no bump", new int[0])]
        public void ClaimExtraction_ReadsOnlyUnambiguousVersionNotation(string comment, int[] expected) =>
            Assert.Equal(expected, ClaimedVersions(comment));

        [Fact]
        public void PureCommentAndDocLines_AreNotPinSites()
        {
            // Prose that merely MENTIONS the symbol is history, not a pin — a golden re-record narrative may
            // legitimately name the version in force when it was recorded.
            Assert.Null(SplitTrailingComment("        // Re-recorded for Story 7.8: CanonicalModelHash.AlgoVersion bumped 8→9"));
            Assert.Null(SplitTrailingComment("        /// <summary>… (AlgoVersion 6) …</summary>"));
            Assert.Null(SplitTrailingComment("            Assert.Equal(14, CanonicalModelHash.AlgoVersion);")); // no comment

            // Built by concatenation so this fixture is not itself a stale pin line: the guard scans its OWN source
            // like every other file (no self-exemption — that would be a hole), and a literal "…AlgoVersion); // v11"
            // on one line here would be a genuine offender.
            string pinLine = "            Assert.Equal(14, CanonicalModelHash.AlgoVersion); " + Slashes + " v11 fold";

            var split = SplitTrailingComment(pinLine);
            Assert.NotNull(split);
            Assert.Contains("CanonicalModelHash.AlgoVersion", split!.Value.Code);
            Assert.Equal(" v11 fold", split.Value.Comment);
        }

        [Fact]
        public void EveryPolicedFamily_ResolvesToItsLiveConstant()
        {
            // Defeats a copy-paste pin table drifting from the real constants (which would silently un-police a family).
            Assert.Equal(SimChecksum.AlgoVersion,        Families.First(f => f.Token == "SimChecksum").Pinned);
            Assert.Equal(CanonicalModelHash.AlgoVersion, Families.First(f => f.Token == "CanonicalModelHash").Pinned);
            Assert.Equal(StartStateHash.AlgoVersion,     Families.First(f => f.Token == "StartStateHash").Pinned);
            Assert.Equal(MatchAgreementHash.AlgoVersion, Families.First(f => f.Token == "MatchAgreementHash").Pinned);
            Assert.Equal(RulesetHash.AlgoVersion,        Families.First(f => f.Token == "RulesetHash").Pinned);
            Assert.Equal(ContentHash.AlgoVersion,        Families.First(f => f.Token == "ContentHash").Pinned);

            // The scan's family regex must cover exactly the table (a family in one but not the other is a hole).
            foreach (var (token, _) in Families)
                Assert.Matches(QualifiedPin, $"{token}.AlgoVersion");
        }

        // ── path helper (this file lives in godot/ProjectChimera.Sim.Tests/Validation/) ─────────────────
        private static string TestsDir([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            return Path.GetFullPath(Path.Combine(dir, ".."));
        }
    }
}
