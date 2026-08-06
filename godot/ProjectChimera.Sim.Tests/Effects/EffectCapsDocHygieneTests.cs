#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-535 regression guard — doc hygiene for <c>EffectCaps</c> and the <c>EffectExecutor</c> SearchArea call
    /// site, in the same shape as <c>AlgoVersionPinCommentHygieneTests</c>: a cheap source scan that turns a class
    /// of stale-comment rot into a red Tier-1 test instead of a trap a future author walks into.
    ///
    /// <para><b>The rot.</b> Two independent contradictions, neither authored — both reintroduced by MERGE ORDER,
    /// because the doc sweep that rewrote the class header branched from before the SearchArea target-selection
    /// fix landed.</para>
    /// <list type="number">
    ///   <item>The class doc asserted "Nothing here is 'reserved' any more" while <c>MaxSpawnCount</c> and
    ///   <c>MaxPersistentPeriods</c>, 46 lines below, still read "Reserved (Story 2.x) … Named now to forbid a bare
    ///   literal later". The file stated both positions at once: trust the header and you conclude both constants
    ///   are enforced, trust the members and you conclude they are inert placeholders. (The header was the true
    ///   half — both have had real enforcers since 7.6 / 2.2b.)</item>
    ///   <item>Three comments still described a post-query <c>Array.Sort</c> + compaction pipeline that no longer
    ///   exists: the predicate now runs INSIDE the spatial query
    ///   (<c>SpatialHash.QueryRadiusLowestIds</c>), which returns matches already ascending. The
    ///   load-bearing one was <c>MaxHitsPerSearch</c>'s "the most entities a single QueryRadius may return before
    ///   filtering" — it reads as a pre-filter scratch buffer, but the buffer now only ever receives already-matching
    ///   ids, so its length IS the post-filter lowest-N selection window. A future story shrinking it "because it is
    ///   just a scratch array" would silently change which entities an area effect hits.</item>
    /// </list>
    ///
    /// <para><b>Why blanket token bans.</b> The guards below ban the words outright in these files rather than
    /// trying to parse affirmative-vs-negated prose. That is deliberate: the correct documentation here is POSITIVE
    /// ("the query returns matches already ascending by id"), and a negative sentence about a deleted step ("there
    /// is no separate sort any more") is itself rot bait — it only makes sense to a reader who remembers the thing
    /// that was removed. Banning the token forces the positive form. The failure messages say exactly this.</para>
    ///
    /// <para>Godot-free. The scans are read-only over source text; the behavioural pin at the bottom is
    /// Fixed-only with no RNG.</para>
    /// </summary>
    public class EffectCapsDocHygieneTests
    {
        private const string CapsFile     = "EffectCaps.cs";
        private const string ExecutorFile = "EffectExecutor.cs";

        /// <summary>Any word claiming a cap is inert — the DW-535 contradiction's vocabulary.</summary>
        private static readonly Regex ReservedWord =
            new(@"\b(?:reserv\w*|placeholder\w*)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Any form of "sort" — the deleted SearchArea pipeline's vocabulary.</summary>
        private static readonly Regex SortWord =
            new(@"\bsort\w*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Real ordering CONSTRUCTS, matched against comment-stripped code only.</summary>
        private static readonly Regex SortConstruct =
            new(@"Array\.Sort|\.Sort\s*\(|\.OrderBy(?:Descending)?\s*\(", RegexOptions.Compiled);

        // ── guard 1: no cap in EffectCaps is described as reserved / a placeholder ──────────────────────

        [Fact]
        public void EffectCaps_DescribesNoCapAsReservedOrAPlaceholder()
        {
            string path = EffectsSourceFile(CapsFile);
            string[] lines = File.ReadAllLines(path);

            // Defeat a vacuous pass (wrong path / renamed file): the two members that actually rotted must be here.
            string whole = string.Join("\n", lines);
            Assert.Contains(nameof(EffectCaps.MaxSpawnCount), whole);
            Assert.Contains(nameof(EffectCaps.MaxPersistentPeriods), whole);

            var offenders = new List<string>();
            for (int i = 0; i < lines.Length; i++)
                if (ReservedWord.IsMatch(lines[i]))
                    offenders.Add($"{CapsFile}:{i + 1}: {lines[i].Trim()}");

            Assert.True(offenders.Count == 0,
                "EffectCaps.cs describes a cap as reserved / a placeholder. Every cap in that file has a real "
                + "enforcer and is folded into RulesetHash (RulesetHashTests enforces the count parity), so such a "
                + "doc is false, and DW-535 was exactly the contradiction it creates with the class header. "
                + "Document WHERE the bound is applied instead — validator, runtime clamp, or buffer size:\n  "
                + string.Join("\n  ", offenders));
        }

        // ── guard 2: neither file describes a sort step in the SearchArea pipeline ─────────────────────

        [Fact]
        public void EffectCapsAndExecutor_DoNotDescribeASortStepInTheSearchAreaPipeline()
        {
            var offenders = new List<string>();

            foreach (string name in new[] { CapsFile, ExecutorFile })
            {
                string path = EffectsSourceFile(name);
                string[] lines = File.ReadAllLines(path);

                // Vacuous-pass defense: both files must really still talk about the SearchArea path.
                Assert.Contains(nameof(SearchAreaEffect), string.Join("\n", lines));

                for (int i = 0; i < lines.Length; i++)
                    if (SortWord.IsMatch(lines[i]))
                        offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }

            Assert.True(offenders.Count == 0,
                "A comment here describes a sort step in the SearchArea pipeline, which has none: "
                + "SpatialHash.QueryRadiusLowestIds evaluates the target predicate INSIDE the query and returns the "
                + "matches already in ascending-id order (DW-535). State that positively — 'the query returns matches "
                + "already ascending by id' — rather than negating a step that no longer exists:\n  "
                + string.Join("\n  ", offenders));
        }

        // ── guard 3: the premise behind guard 2, checked against CODE rather than prose ────────────────

        [Fact]
        public void SearchAreaPipeline_ContainsNoOrderingConstruct()
        {
            string dir = EffectsSourceDir();
            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i]);
                    if (SortConstruct.IsMatch(code))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            Assert.True(scanned > 0, $"No sources scanned under '{dir}' — the path drifted (vacuous-pass hazard).");

            // If this ever goes red it is not necessarily a defect: someone reintroduced a real ordering step. It is
            // the signal that guard 2's blanket ban has outlived its premise and the docs must be revisited together.
            Assert.True(offenders.Count == 0,
                "An ordering construct reappeared under src/Effects. Target selection is supposed to arrive already "
                + "ascending from SpatialHash.QueryRadiusLowestIds, which is why the comment guard above forbids "
                + "describing a sort. If this step is intentional, update that guard and the EffectCaps docs with "
                + "it:\n  " + string.Join("\n  ", offenders));
        }

        // ── the pin the ledger asked for: MaxHitsPerSearch's real semantics, made executable ───────────

        [Fact]
        public void HitBufferLength_IsThePostFilterSelectionWindow_NotAPreFilterScratchCapacity()
        {
            // The claim MaxHitsPerSearch's doc now makes: the buffer's LENGTH decides how many matches survive, and
            // non-matching entities never occupy a slot. Arranged so a pre-filter scratch buffer gives a concretely
            // different answer — the caster and 40 allies hold the LOWEST ids, so a 5-slot pre-filter buffer would
            // fill with ids 0..4 (all rejected by an Enemy filter) and compact down to ZERO targets.
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            const int allies = 40;
            for (int i = 0; i < allies; i++)
                w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));          // ids 1..40
            const int enemies = 20;
            for (int i = 0; i < enemies; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));          // ids 41..60

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            // A window deliberately NARROWER than MaxSearchTargets, so the fan-out clamp in FindTargets is inert and
            // the buffer length is provably the only thing bounding the result.
            const int window = 5;
            Assert.True(window < EffectCaps.MaxSearchTargets);
            var buffer = new int[window];

            int n = search.FindTargets(in ctx, buffer);

            Assert.Equal(window, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(Faction.Player2, w.FactionOf[buffer[i]]); // only matches ever occupy a slot
                Assert.Equal(allies + 1 + i, buffer[i]);               // the lowest `window` ENEMY ids, ascending
            }

            // And widening the window widens the selection — the coupling the old "scratch buffer" wording hid.
            var wider = new int[window * 2];
            Assert.Equal(window * 2, search.FindTargets(in ctx, wider));
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Everything before the first <c>//</c> on a line (handles both <c>//</c> and <c>///</c>).</summary>
        private static string StripComment(string line)
        {
            int at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line.Substring(0, at);
        }

        private static string EffectsSourceFile(string fileName)
        {
            string path = Path.Combine(EffectsSourceDir(), fileName);
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");
            return path;
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Effects/ ⇒ ../../src/Effects/.</summary>
        private static string EffectsSourceDir([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            return Path.GetFullPath(Path.Combine(dir, "..", "..", "src", "Effects"));
        }
    }
}
