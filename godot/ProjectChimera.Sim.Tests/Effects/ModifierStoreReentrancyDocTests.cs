#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Combat;   // DamageType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-663 regression guard — doc hygiene for <c>ModifierStore</c>'s <b>Re-entrancy</b> paragraph, in the same
    /// shape as <see cref="EffectCapsDocHygieneTests"/>: a cheap source scan that turns a class of stale-comment rot
    /// into a red Tier-1 test, plus the executable premises the refreshed prose now asserts.
    ///
    /// <para><b>The rot.</b> The paragraph scoped the store's behaviour to a RELEASE — "In 2.2b all three phases use
    /// only direct-target leaves … that case is unsupported in 2.2b" — and a second comment on
    /// <c>TryDebitEnergy</c> claimed "no ability exists in 2.2b" while <c>AbilityCastSystem.CastAbility</c> has
    /// debited through it for many stories. A version-scoped claim rots the moment the version moves and gives a
    /// reader nothing to check: the correct documentation names the MECHANISM that holds the line. Here that
    /// mechanism is the Story 2.3 content validator's AC5 fence, which rejects every install leaf and every
    /// <see cref="SearchAreaEffect"/> inside a store-run phase, so no loadable ability can re-enter the store's
    /// dedicated executor. DW-324's doc sweep named this exact line in its scope and closed with it still stale.</para>
    ///
    /// <para><b>Why a blanket token ban.</b> Like DW-535's guards, the scan below bans the "in &lt;version&gt;" prose
    /// form outright in this one file rather than trying to parse which version claims are still true. That is the
    /// point: a claim that is true "in 2.2b" is un-checkable prose, and re-stating it against a NEWER version only
    /// resets the rot clock. Attribution ("the AR-9 ModifierStore (Story 2.2b)", "the 2.2a AccumulateBonus seam") is
    /// history and stays legal — it says where something CAME FROM, not what is true now.</para>
    ///
    /// <para>Godot-free. The scans are read-only over source text; the fence tests drive
    /// <see cref="AbilityValidator"/> directly with hand-built graphs and touch no world state.</para>
    /// </summary>
    public class ModifierStoreReentrancyDocTests
    {
        private const string StoreFile = "ModifierStore.cs";

        private static readonly AbilityValidator V = new();

        /// <summary>
        /// A version-SCOPED present-state claim: "in 2.2b", "In Story 2.13", … Deliberately NOT matching a bare
        /// version token, so historical attribution keeps working.
        /// </summary>
        private static readonly Regex VersionScopedClaim =
            new(@"\bin\s+(?:story\s+)?[0-9]+\.[0-9]+[a-z]?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ── guard 1: the guarded files make no version-scoped present-state claim ──────────────────────

        /// <summary>DW-785 — the scan's FILE SET. <c>ModifierStore.cs</c> was DW-663's own file;
        /// <c>PersistentEffect.cs</c> was added because it carried the identical rot UNGUARDED ("its periodic
        /// EXECUTION … lands in Story 2.2b. In 2.1 the executor … fail-closes (throws)", which had become outright
        /// FALSE — the executor installs through the store), and because a guard scoped to one file cannot stop a
        /// class of defect that is about prose habits. Each entry pairs the file with an anchor string that must
        /// still be present, so deleting the documented member rather than fixing its prose cannot turn the guard
        /// green.</summary>
        public static TheoryData<string, string> VersionClaimGuardedFiles => new()
        {
            { "ModifierStore.cs",    "class ModifierStore" },
            { "PersistentEffect.cs", "class PersistentEffect" },
        };

        [Theory]
        [MemberData(nameof(VersionClaimGuardedFiles))]
        public void GuardedEffectsDoc_MakesNoVersionScopedPresentStateClaim(string fileName, string anchor)
        {
            string path = StoreSourceFile(fileName);
            string[] lines = File.ReadAllLines(path);
            string whole = string.Join("\n", lines);

            // Defeat a vacuous pass (wrong path / renamed file / paragraph deleted rather than fixed).
            Assert.Contains(anchor, whole);

            var offenders = new List<string>();
            for (int i = 0; i < lines.Length; i++)
                if (VersionScopedClaim.IsMatch(lines[i]))
                    offenders.Add($"{fileName}:{i + 1}: {lines[i].Trim()}");

            Assert.True(offenders.Count == 0,
                $"A comment in {fileName} scopes a claim about CURRENT behaviour to a story number "
                + "(\"in 2.2b …\"). That was DW-663 (ModifierStore) and DW-785 (PersistentEffect): the prose "
                + "described the posture as a property of a release long after a MECHANISM became the thing that "
                + "holds it — and in PersistentEffect's case the claim had gone outright false, telling readers the "
                + "executor throws on a Persistent when it installs one. Name the mechanism — the validator gate, "
                + "the guard, the call site, the clamp — instead of the version. Attribution (\"(Story 2.2b)\", "
                + "\"the 2.2a AccumulateBonus seam\") is history and stays fine:\n  "
                + string.Join("\n  ", offenders));
        }

        [Fact]
        public void ModifierStoreDoc_StillCarriesTheReEntrancyParagraphTheGuardIsAbout()
        {
            // Split out of the guard above when it became a per-file theory: the Re-entrancy anchor is specific to
            // ModifierStore.cs and must not become a requirement on every file added to the scan set.
            Assert.Contains("<b>Re-entrancy.</b>", File.ReadAllText(StoreSourceFile(StoreFile)));
        }

        [Fact]
        public void PersistentEffectDoc_NamesTheInstallPathAndTheClamp_NotARelease()
        {
            // DW-785's positive half: the corrected prose has to say what actually happens, or "no version claim"
            // is satisfiable by deleting the explanation entirely.
            string doc = File.ReadAllText(StoreSourceFile("PersistentEffect.cs"));

            Assert.Contains("InstallPersistent", doc, StringComparison.Ordinal);   // where the periods come from
            Assert.Contains("MaxPersistentPeriods", doc, StringComparison.Ordinal); // what bounds PeriodCount
            Assert.Contains("AbilityValidator", doc, StringComparison.Ordinal);     // the AC5 fence, by name
        }

        [Fact]
        public void PersistentEffect_IsInstalledIntoTheStore_NotFailClosedOnRecognition()
        {
            // The executable half of the same correction: the retired doc said the executor "recognizes the type and
            // fail-closes (throws) rather than mutating a nonexistent store". It installs, and runs the initial pulse
            // on the store's own executor — neither is true of a type that throws on recognition.
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            int target = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            var pe = new PersistentEffect(
                initialEffect: new DirectHpDeltaEffect(Fixed.FromInt(-5)),
                periodEffect:  new DirectHpDeltaEffect(Fixed.FromInt(-1)),
                expireEffect:  null,
                periodTicks: 30, periodCount: 3);

            store.InstallPersistent(target, pe, casterId: target, casterFaction: Faction.Player1);

            Assert.Equal(1, store.CountAt(target));                          // a live slot exists
            Assert.Equal(Fixed.FromInt(95).Raw, world.Health[target].Raw);   // the initial pulse ran
        }

        [Fact]
        public void PersistentEffect_PeriodCount_IsClampedByTheStore_NotByAnyReleaseGate()
        {
            // PeriodCount's doc used to say "bounded by EffectCaps.MaxPersistentPeriods in 2.2b". The bound is
            // InstallPersistent's clamp; authoring a count above the cap installs at the cap, and a negative one at 0.
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);

            // Health well above the cap but inside the 16.16 Fixed range, so 256 one-per-tick pulses never floor.
            int over = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(30_000), Fixed.FromInt(4));
            store.InstallPersistent(over,
                new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-1)), null,
                                     periodTicks: 1, periodCount: EffectCaps.MaxPersistentPeriods + 500),
                casterId: over, casterFaction: Faction.Player1);

            // One pulse per tick: the instance must expire exactly at the CAP, not at the authored count.
            for (int t = 0; t < EffectCaps.MaxPersistentPeriods; t++) store.Advance(world, Fixed.Zero);
            Assert.Equal(0, store.CountAt(over));
            Assert.Equal(Fixed.FromInt(30_000 - EffectCaps.MaxPersistentPeriods).Raw, world.Health[over].Raw);
        }

        // ── guard 2: the paragraph names the fence, positively ─────────────────────────────────────────

        [Fact]
        public void ReEntrancyParagraph_NamesTheValidatorFenceAndTheDirectTargetPremise()
        {
            string doc = ReEntrancyParagraph();

            Assert.Contains("Story 2.3", doc);              // the fence's owner
            Assert.Contains("AbilityValidator", doc);        // …by name, so the reader can go read it
            Assert.Contains("spatial: null", doc);           // the direct-target premise
            Assert.Contains("_count", doc);                  // the hazard being fenced off
        }

        // ── guard 3: the direct-target premise, checked against CODE rather than prose ─────────────────

        [Fact]
        public void Store_BuildsExactlyOneEffectContext_AndItIsDirectTarget()
        {
            string src = File.ReadAllText(StoreSourceFile(StoreFile));

            var sites = new List<int>();
            for (int at = src.IndexOf("new EffectContext(", StringComparison.Ordinal); at >= 0;
                 at = src.IndexOf("new EffectContext(", at + 1, StringComparison.Ordinal))
                sites.Add(at);

            Assert.True(sites.Count == 1,
                $"ModifierStore.cs builds {sites.Count} EffectContexts. The Re-entrancy doc claims RunEffectAgainst "
                + "is the ONLY place a store context is built — that single funnel is what makes 'every phase runs "
                + "direct-target' checkable, and it is where the DW-662 dead-host/dead-target guard has its teeth. "
                + "Add the new site to that funnel, or update the paragraph with it.");

            // The one site must be spatial-less: a threaded SpatialHash would let a period fan out, which is exactly
            // the future the paragraph says needs a re-entrancy guard (and the validator fence relaxed) FIRST.
            string body = src.Substring(sites[0], Math.Min(400, src.Length - sites[0]));
            Assert.True(body.Contains("spatial: null", StringComparison.Ordinal),
                "The store's EffectContext no longer passes 'spatial: null'. If a SpatialHash was deliberately "
                + "threaded into the store's executor, then a SearchArea inside a phase now really fans out, the "
                + "AbilityValidator AC5 rejects below stop matching reality, and the Re-entrancy paragraph is wrong:\n"
                + body);
        }

        // ── the fence itself: install leaves are refused in EVERY store-run phase, not just a period ───

        [Theory]
        [InlineData("initial")]
        [InlineData("period")]
        [InlineData("expire")]
        public void InstallLeaf_IsRejected_InEveryPersistentPhase(string phase)
        {
            var apply = new ApplyModifierEffect(Mod(id: 7, periodEffect: null, periodTicks: 0));

            AbilityValidationResult r = V.Validate(Def(PersistentWith(phase, apply)));

            Assert.False(r.Ok);
            Assert.Contains("ApplyModifierEffect is not allowed inside a PersistentEffect phase", r.Error!);
            Assert.Contains($"effect.{phase}_effect", r.Error!);
        }

        [Theory]
        [InlineData("initial")]
        [InlineData("period")]
        [InlineData("expire")]
        public void NestedPersistent_IsRejected_InEveryPersistentPhase(string phase)
        {
            var inner = new PersistentEffect(Leaf(), null, null, 0, 0);

            AbilityValidationResult r = V.Validate(Def(PersistentWith(phase, inner)));

            Assert.False(r.Ok);
            Assert.Contains("nested PersistentEffect is not allowed inside a PersistentEffect phase", r.Error!);
            Assert.Contains($"effect.{phase}_effect", r.Error!);
        }

        [Theory]
        [InlineData("initial")]
        [InlineData("period")]
        [InlineData("expire")]
        public void SearchArea_IsRejected_InEveryPersistentPhase(string phase)
        {
            var search = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, Leaf());

            AbilityValidationResult r = V.Validate(Def(PersistentWith(phase, search)));

            Assert.False(r.Ok);
            Assert.Contains("SearchAreaEffect is not allowed inside a PersistentEffect phase", r.Error!);
            Assert.Contains($"effect.{phase}_effect", r.Error!);
        }

        // ── …and in a Modifier's own period subtree, which the store runs on the SAME dedicated executor ──

        [Fact]
        public void InstallLeaf_IsRejected_InsideAModifierPeriodSubtree()
        {
            var innerApply = new ApplyModifierEffect(Mod(id: 8, periodEffect: null, periodTicks: 0));
            var outer = new ApplyModifierEffect(Mod(id: 9, periodEffect: innerApply, periodTicks: 30));

            AbilityValidationResult r = V.Validate(Def(outer));

            Assert.False(r.Ok);
            Assert.Contains("ApplyModifierEffect is not allowed inside a PersistentEffect phase", r.Error!);
            Assert.Contains("effect.modifier.period_effect", r.Error!);
        }

        [Fact]
        public void SearchArea_IsRejected_InsideAModifierPeriodSubtree()
        {
            var search = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, Leaf());
            var outer = new ApplyModifierEffect(Mod(id: 10, periodEffect: search, periodTicks: 30));

            AbilityValidationResult r = V.Validate(Def(outer));

            Assert.False(r.Ok);
            Assert.Contains("SearchAreaEffect is not allowed inside a PersistentEffect phase", r.Error!);
            Assert.Contains("effect.modifier.period_effect", r.Error!);
        }

        // ── the positive controls: direct-target phases are exactly what the store DOES support ────────

        [Fact]
        public void DirectTargetPhases_AndAModifierDirectTargetPeriod_AreAccepted()
        {
            // All three Persistent phases, each a Sequence of direct-target leaves — the shape the paragraph
            // describes as the only one that reaches the store's executor.
            var persistent = new PersistentEffect(
                initialEffect: new SequenceEffect(new[] { Leaf(), Leaf() }),
                periodEffect: new DamageEffect(Fixed.FromInt(2), DamageType.Magic),
                expireEffect: Leaf(),
                periodTicks: 30, periodCount: 5);
            Assert.True(V.Validate(Def(persistent)).Ok);

            // And a Modifier whose own period is a direct-target leaf (the DW-271 pulse).
            var mod = new ApplyModifierEffect(
                Mod(id: 11, periodEffect: new DamageEffect(Fixed.One, DamageType.Normal), periodTicks: 30));
            Assert.True(V.Validate(Def(mod)).Ok);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────

        private static EffectNode Leaf() => new HealEffect(Fixed.FromInt(1));

        private static AbilityDefinition Def(EffectNode? graph, string id = "dw663") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        /// <summary>A bounds-clean Modifier carrying no stat deltas — only its period shape is under test.</summary>
        private static Modifier Mod(int id, EffectNode? periodEffect, int periodTicks) =>
            new Modifier(id, 30, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                         StatusFlags.None, periodEffect, periodTicks);

        /// <summary>A PersistentEffect carrying <paramref name="node"/> in exactly one named phase.</summary>
        private static PersistentEffect PersistentWith(string phase, EffectNode node) => phase switch
        {
            "initial" => new PersistentEffect(node, null, null, 0, 0),
            // A valid period shape (ticks/count > 0) so the DW-504 mismatch rule has nothing to say — the AC5
            // re-entrancy reject must be the reason this graph fails, not a period-shape coincidence.
            "period"  => new PersistentEffect(null, node, null, 30, 5),
            "expire"  => new PersistentEffect(null, null, node, 0, 0),
            _         => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unknown phase"),
        };

        /// <summary>The <c>&lt;b&gt;Re-entrancy.&lt;/b&gt;</c> prose through the end of the class summary.</summary>
        private static string ReEntrancyParagraph()
        {
            string src = File.ReadAllText(StoreSourceFile(StoreFile));
            int start = src.IndexOf("<b>Re-entrancy.</b>", StringComparison.Ordinal);
            Assert.True(start >= 0, "ModifierStore.cs no longer documents Re-entrancy at all (vacuous-pass hazard).");
            int end = src.IndexOf("</summary>", start, StringComparison.Ordinal);
            Assert.True(end > start, "The Re-entrancy paragraph is not inside a doc summary any more.");
            return src.Substring(start, end - start);
        }

        private static string StoreSourceFile(string fileName)
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
