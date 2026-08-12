#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-864 — the DW-554 guard, extended across the MERGED-TICK golden family. A golden's embedded re-baseline
    /// recipe must record THAT golden and nothing else.
    ///
    /// <para><b>The defect.</b> Story 9.3's N=2 merged golden shipped the recipe
    /// <c>--filter FullyQualifiedName~MergedTick</c>. <c>FullyQualifiedName~</c> is a SUBSTRING match, so it also
    /// selects <see cref="MergedTickN3GoldenTests"/> — the class whose <c>RecordMergedBaseline</c> theory rewrites
    /// BOTH <c>golden-merged-n3</c> and <c>golden-merged-n4</c>. Anyone following the committed header to re-baseline
    /// the N=2 golden would therefore also re-record two other committed pins, destroying the independent raised-count
    /// evidence Story 9.7 exists to provide — and nothing in the run output would say so, because a record run's job
    /// IS to overwrite goldens. Exactly the DW-554 shape, a different family. Mitigated only by trailing prose
    /// ("NEVER record the existing goldens"), which is advice, not a gate.</para>
    ///
    /// <para><b>The fix this pins.</b> The N=2 recipe is now <c>~MergedTickN2</c> and its recorder was renamed
    /// <see cref="MergedTickN2GoldenTests"/> so that filter actually selects it (a narrowed filter that selects
    /// NOTHING is the opposite failure — it leaves the golden stale, which the first assertion below catches).
    /// The N=3/N=4 recipes already named <c>~MergedTickN3</c> and are checked here for the first time.</para>
    ///
    /// <para><b>What is pinned.</b> For each golden in this family, every <c>FullyQualifiedName~X</c> token in BOTH
    /// the in-code <see cref="GoldenChecksumReplay.GoldenHeader"/> (the source of a future re-record) AND the
    /// COMMITTED golden's own header line (the artifact a human actually copies) must select its own recorder class
    /// and must NOT select any OTHER recorder in the family. Both sources are checked independently on purpose:
    /// narrowing the code while leaving the committed header wide still leads the next human to destroy the other
    /// pins. N=3 and N=4 legitimately SHARE one recorder — the rule is "no OTHER recorder", not "no other golden".</para>
    /// </summary>
    public class MergedTickRebaselineFilterTests
    {
        /// <summary>The vstest filter token as written in every re-baseline recipe. Only <c>FullyQualifiedName~</c>
        /// occurrences count — prose that merely names a filter ("NEVER widen this to ~MergedTick") is not a recipe
        /// and must not be read as one.</summary>
        private static readonly Regex FilterToken = new(@"FullyQualifiedName~([A-Za-z0-9_.]+)", RegexOptions.Compiled);

        /// <summary>Every RECORDER in this family — the classes whose record-mode run overwrites a committed golden.
        /// A recipe may select its own; selecting any other is the DW-864 defect.</summary>
        private static readonly Type[] FamilyRecorders =
        {
            typeof(MergedTickN2GoldenTests),
            typeof(MergedTickN3GoldenTests),
        };

        [Fact]
        public void N2RebaselineRecipe_SelectsOnlyItsOwnRecorder()
        {
            AssertRecipeIsExclusive(
                owner: typeof(MergedTickN2GoldenTests),
                inCodeSource: "MergedTickN2Scenario.Header",
                inCodeHint: MergedTickN2Scenario.Header.RebaselineHint,
                goldenFileName: MergedTickN2Scenario.GoldenFileName);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void RaisedCountRebaselineRecipes_SelectOnlyTheirOwnRecorder(int n)
        {
            AssertRecipeIsExclusive(
                owner: typeof(MergedTickN3GoldenTests),
                inCodeSource: $"MergedTickN3Scenario.Header({n})",
                inCodeHint: MergedTickN3Scenario.Header(n).RebaselineHint,
                goldenFileName: MergedTickN3Scenario.GoldenFileName(n));
        }

        /// <summary>
        /// Non-vacuity fence. The guard above is only meaningful while the recorder class names really do share a
        /// prefix — if a future rename made them disjoint, every filter would trivially pass and the guard would rot
        /// into decoration. This pins the exact overlap that makes the narrowing necessary: the ORIGINAL bare token
        /// still matches both recorders, while each narrowed token separates them.
        /// </summary>
        [Fact]
        public void TheBareMergedTickFilter_StillOverMatches_SoTheNarrowingIsNotDecorative()
        {
            string n2 = FqnOf(typeof(MergedTickN2GoldenTests));
            string n3 = FqnOf(typeof(MergedTickN3GoldenTests));

            Assert.Contains("MergedTick", n2, StringComparison.Ordinal);
            Assert.Contains("MergedTick", n3, StringComparison.Ordinal);      // the over-match, still real

            Assert.Contains("MergedTickN2", n2, StringComparison.Ordinal);    // …and the narrowing separates them
            Assert.DoesNotContain("MergedTickN2", n3, StringComparison.Ordinal);
            Assert.Contains("MergedTickN3", n3, StringComparison.Ordinal);
            Assert.DoesNotContain("MergedTickN3", n2, StringComparison.Ordinal);
        }

        /// <summary>
        /// The N=3 and N=4 goldens SHARE a recorder, which is legal and is why the rule is "no other RECORDER". This
        /// pins that sharing so a later reader does not mistake the two identical recipes for a copy-paste slip — and
        /// so splitting the recorder in two becomes a deliberate act that must re-narrow both recipes.
        /// </summary>
        [Fact]
        public void TheRaisedCountGoldens_DeliberatelyShareOneRecorder()
        {
            Assert.Equal(
                MergedTickN3Scenario.Header(3).RebaselineHint,
                MergedTickN3Scenario.Header(4).RebaselineHint);
            Assert.NotEqual(MergedTickN3Scenario.GoldenFileName(3), MergedTickN3Scenario.GoldenFileName(4));
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>Assert every filter token in the owner's in-code recipe AND in the committed golden's header
        /// selects <paramref name="owner"/> and no OTHER recorder in <see cref="FamilyRecorders"/>.</summary>
        private static void AssertRecipeIsExclusive(Type owner, string inCodeSource, string inCodeHint, string goldenFileName)
        {
            string ownerFqn = FqnOf(owner);

            foreach ((string source, string text) in new[]
            {
                ($"{inCodeSource}'s in-code GoldenHeader", inCodeHint),
                ($"the committed {goldenFileName} header", CommittedRebaselineLine(goldenFileName)),
            })
            {
                string[] filters = FilterToken.Matches(text).Select(m => m.Groups[1].Value).ToArray();
                Assert.True(filters.Length > 0,
                    $"{source} declares no `FullyQualifiedName~` filter at all — a re-baseline recipe without one " +
                    $"records EVERY golden. Text was: {text}");

                foreach (string f in filters)
                {
                    Assert.True(ownerFqn.Contains(f, StringComparison.Ordinal),
                        $"{source} names filter '~{f}', which does not select its own recorder {ownerFqn} — " +
                        $"following that recipe would record nothing and leave the golden stale.");

                    foreach (Type other in FamilyRecorders)
                    {
                        if (other == owner) continue;
                        Assert.False(FqnOf(other).Contains(f, StringComparison.Ordinal),
                            $"DW-864: {source} names filter '~{f}', which ALSO selects {FqnOf(other)}. A re-baseline " +
                            $"of {goldenFileName} following that recipe would silently re-record that recorder's " +
                            $"golden(s) too, destroying an independent pin. Narrow the filter (and the committed " +
                            $"header line) so it selects only its own recorder.");
                    }
                }
            }
        }

        private static string FqnOf(Type t) => t.FullName
            ?? throw new InvalidOperationException($"{t.Name} has no FullName — vstest filters match on it.");

        /// <summary>The committed golden's own "# Re-baseline …" header line, read from the source file on disk —
        /// that file, not the embedded copy, is what a human opens and copies the recipe out of.</summary>
        private static string CommittedRebaselineLine(string fileName)
        {
            string path = Path.Combine(GoldenDir(), fileName);
            Assert.True(File.Exists(path), $"Committed golden not found at '{path}'.");

            string? line = File.ReadLines(path)
                .FirstOrDefault(l => l.StartsWith("# Re-baseline", StringComparison.Ordinal));

            Assert.True(line is not null,
                $"'{fileName}' carries no '# Re-baseline' header line — FormatGolden always writes one, so either " +
                $"the file was hand-edited or the header format changed.");
            return line!;
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Golden/, beside the goldens.</summary>
        private static string GoldenDir([CallerFilePath] string thisFilePath = "") =>
            Path.GetDirectoryName(thisFilePath)
            ?? throw new InvalidOperationException("Could not resolve the Golden/ source directory.");
    }
}
