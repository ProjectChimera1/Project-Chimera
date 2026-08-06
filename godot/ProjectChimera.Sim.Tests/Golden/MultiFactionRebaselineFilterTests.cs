#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-554 — a golden's embedded re-baseline recipe must record THAT golden and nothing else.
    ///
    /// <para><b>The defect.</b> Story 1.3a's N=4 multi-faction golden shipped the recipe
    /// <c>--filter FullyQualifiedName~MultiFaction</c>. That was unambiguous when it was written, and silently stopped
    /// being so when DW-387 added <see cref="MultiFactionExpansionTests"/>: <c>FullyQualifiedName~</c> is a SUBSTRING
    /// match, so the recipe also selects that class — whose <c>RecordEightFactionBaseline</c> rewrites
    /// <c>golden-multifaction8.golden.txt</c>. Anyone following the committed header to re-baseline the N=4 golden
    /// would therefore ALSO re-record the N=8 golden, destroying the independent cross-process pin DW-387 exists to
    /// provide — and nothing in the run output would say so, because a record run's job IS to overwrite goldens.
    /// The failure is silent by construction, which is why it needs a test rather than care.</para>
    ///
    /// <para><b>What is pinned.</b> For each golden in this family, every <c>FullyQualifiedName~X</c> token in BOTH
    /// the in-code <see cref="GoldenChecksumReplay.GoldenHeader"/> (the source of a future re-record) AND the
    /// COMMITTED golden's own header line (the artifact a human actually copies) must match its own recorder class
    /// and must NOT match the other recorder class. Both sources are checked independently on purpose: narrowing the
    /// code while leaving the committed header wide still leads the next human to destroy the other pin.</para>
    ///
    /// <para>Scoped to this family deliberately: the same over-match exists elsewhere in the golden set and is not
    /// this bundle's to change during a re-baseline window (a golden-header edit is a deliberate act). Generalizing
    /// the sweep is worth doing once, outside a record window.</para>
    /// </summary>
    public class MultiFactionRebaselineFilterTests
    {
        /// <summary>The vstest filter token as written in every re-baseline recipe. Only <c>FullyQualifiedName~</c>
        /// occurrences count — prose that merely names a filter ("NEVER widen this to ~MultiFaction") is not a
        /// recipe and must not be read as one.</summary>
        private static readonly Regex FilterToken = new(@"FullyQualifiedName~([A-Za-z0-9_.]+)", RegexOptions.Compiled);

        private const string N4Golden = "golden-multifaction.golden.txt";
        private const string N8Golden = "golden-multifaction8.golden.txt";

        [Fact]
        public void N4RebaselineRecipe_SelectsItsOwnRecorder_AndNeverTheN8Recorder()
        {
            AssertRecipeIsExclusive(
                owner: typeof(MultiFactionGoldenTests),
                other: typeof(MultiFactionExpansionTests),
                goldenFileName: N4Golden);
        }

        [Fact]
        public void N8RebaselineRecipe_SelectsItsOwnRecorder_AndNeverTheN4Recorder()
        {
            AssertRecipeIsExclusive(
                owner: typeof(MultiFactionExpansionTests),
                other: typeof(MultiFactionGoldenTests),
                goldenFileName: N8Golden);
        }

        /// <summary>
        /// Non-vacuity fence. The guard above is only meaningful while the two recorder class names really do share a
        /// prefix — if a future rename made them disjoint, every filter would trivially pass and the guard would rot
        /// into decoration. This pins the exact overlap that makes the narrowing necessary: the ORIGINAL bare token
        /// still matches both classes, while the narrowed token matches only the N=4 one.
        /// </summary>
        [Fact]
        public void TheBareMultiFactionFilter_StillOverMatches_SoTheNarrowingIsNotDecorative()
        {
            string n4 = FqnOf(typeof(MultiFactionGoldenTests));
            string n8 = FqnOf(typeof(MultiFactionExpansionTests));

            Assert.Contains("MultiFaction", n4, StringComparison.Ordinal);
            Assert.Contains("MultiFaction", n8, StringComparison.Ordinal);   // the over-match, still real
            Assert.Contains("MultiFactionGolden", n4, StringComparison.Ordinal);
            Assert.DoesNotContain("MultiFactionGolden", n8, StringComparison.Ordinal); // and the narrowing separates them
        }

        // ── helpers ───────────────────────────────────────────────────────────────

        /// <summary>Assert every filter token in <paramref name="owner"/>'s in-code header AND in
        /// <paramref name="goldenFileName"/>'s committed header selects <paramref name="owner"/> and not
        /// <paramref name="other"/>.</summary>
        private static void AssertRecipeIsExclusive(Type owner, Type other, string goldenFileName)
        {
            string ownerFqn = FqnOf(owner);
            string otherFqn = FqnOf(other);

            foreach ((string source, string text) in new[]
            {
                ($"{owner.Name}'s in-code GoldenHeader", HeaderOf(owner).RebaselineHint),
                ($"the committed {goldenFileName} header",  CommittedRebaselineLine(goldenFileName)),
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
                    Assert.False(otherFqn.Contains(f, StringComparison.Ordinal),
                        $"DW-554: {source} names filter '~{f}', which ALSO selects {otherFqn}. A re-baseline of " +
                        $"{goldenFileName} following that recipe would silently re-record the OTHER golden too, " +
                        $"destroying an independent cross-process pin. Narrow the filter (and the committed header " +
                        $"line) so it selects only its own recorder.");
                }
            }
        }

        private static string FqnOf(Type t) => t.FullName
            ?? throw new InvalidOperationException($"{t.Name} has no FullName — vstest filters match on it.");

        /// <summary>The single <see cref="GoldenChecksumReplay.GoldenHeader"/> a recorder class declares, resolved by
        /// TYPE rather than by field name so renaming the field cannot silently un-guard the recipe.</summary>
        private static GoldenChecksumReplay.GoldenHeader HeaderOf(Type owner)
        {
            FieldInfo[] fields = owner
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(GoldenChecksumReplay.GoldenHeader))
                .ToArray();

            Assert.True(fields.Length == 1,
                $"Expected exactly one GoldenHeader field on {owner.Name}, found {fields.Length} " +
                $"({string.Join(", ", fields.Select(f => f.Name))}). This guard reads the recipe from it.");

            return (GoldenChecksumReplay.GoldenHeader)fields[0].GetValue(null)!;
        }

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
