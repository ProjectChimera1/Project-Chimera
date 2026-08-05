#nullable enable
using System;
using System.IO;
using System.Linq;
using ProjectChimera.Combat;             // DamageType
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // AbilityValidator, AbilityValidationResult, AbilityLoader
using ProjectChimera.Effects;            // Modifier / PersistentEffect / leaves
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-504 (owner decision, 2026-08-04) — the <c>period_ticks</c>/<c>period_effect</c> MISMATCH is a HARD REJECT
    /// everywhere, not a warning.
    ///
    /// <para>Before this, the same defect was gated three different ways depending on where it appeared: a Persistent
    /// with a <c>period_effect</c> and <c>period_ticks &lt;= 0</c> was REJECTED on a <c>while_alive</c> passive
    /// (<c>AbilityValidator.ValidatePassiveShape</c>), but only WARNED for a Modifier anywhere, and only warned for a
    /// Persistent on an active ability. DW-278 made that asymmetry visible; it did not finish the rule, so an author who
    /// ignored the warning still shipped an inert period. These tests pin the finished rule — every one is RED before the
    /// promotion, because <c>Ok</c> was true and the message rode <c>Warnings</c> instead of <c>Error</c>.</para>
    ///
    /// <para>Teeth in the other direction too: the promotion must not swallow the while_alive-specific message, must not
    /// touch the period shapes that are legitimately fine, must not fire on the DW-278 warnings that stay non-fatal, and
    /// must leave every shipped ability loading.</para>
    /// </summary>
    public class AbilityPeriodMismatchRejectTests
    {
        private static readonly AbilityValidator V = new();

        private static AbilityDefinition Def(EffectNode? graph, string id = "ptest") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        private static AbilityDefinition Passive(string activation, EffectNode graph, string targeting = "None",
                                                 string id = "ppass") =>
            new AbilityDefinition { Id = id, Targeting = targeting, Activation = activation, EffectGraph = graph };

        private static Modifier Mod(int duration, EffectNode? period = null, int periodTicks = 0,
                                    StackRule rule = StackRule.Refresh, int maxStacks = 1) =>
            new Modifier(11, duration, rule, maxStacks,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(3), moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: period, periodTicks: periodTicks);

        /// <summary>Assert the ability was REJECTED with a single located error hitting every fragment, and — because a
        /// rejected graph was never walked to completion — that no warnings leaked alongside it.</summary>
        private static void AssertRejected(AbilityValidationResult r, params string[] fragments)
        {
            Assert.False(r.Ok, "expected a hard reject, but the ability validated");
            Assert.NotNull(r.Error);
            Assert.Null(r.Value.Value);              // no runnable Validated<AbilityDefinition> escaped the gate
            Assert.Empty(r.Warnings);                // an error is reported alone — never double-reported as a warning
            foreach (string fragment in fragments) Assert.Contains(fragment, r.Error!);
        }

        // ── Case 1: a Modifier declaring a period_effect its period_ticks can never fire ──

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-30)]
        public void ModifierPeriodEffect_WithNonPositivePeriodTicks_IsRejected(int periodTicks)
        {
            // ModifierStore.HasPeriod requires PeriodTicks > 0, so this pulse can NEVER fire while the modifier happily
            // grants its stat deltas — which is exactly what made it invisible as a warning.
            AbilityValidationResult r = V.Validate(Def(
                new ApplyModifierEffect(Mod(duration: 60, period: new HealEffect(Fixed.FromInt(2)), periodTicks: periodTicks))));
            AssertRejected(r, "ptest", "effect.modifier.period_ticks", $"period_ticks={periodTicks}", "never fires");
        }

        // ── Case 2: a Modifier declaring a period_ticks with nothing to pulse ──

        [Fact]
        public void ModifierPeriodTicks_WithNoPeriodEffect_IsRejected()
        {
            AbilityValidationResult r = V.Validate(Def(
                new ApplyModifierEffect(Mod(duration: 60, period: null, periodTicks: 15))));
            AssertRejected(r, "ptest", "effect.modifier.period_ticks", "period_ticks=15", "ignored");
        }

        // ── Case 3: the same period_ticks mismatch on a Persistent, off the while_alive path ──

        [Fact]
        public void ActivePersistent_WithPeriodEffectButNonPositivePeriodTicks_IsRejected()
        {
            AbilityValidationResult r = V.Validate(Def(
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 0, periodCount: 5)));
            AssertRejected(r, "ptest", "effect.period_ticks", "period_ticks=0", "never fires");
        }

        [Fact]
        public void OnHitPersistent_WithPeriodEffectButNonPositivePeriodTicks_IsRejected()
        {
            // on_hit imposes no root-shape rule of its own, so before DW-504 this rider was ungated entirely.
            var p = new PersistentEffect(null, new DamageEffect(Fixed.FromInt(4), DamageType.Magic), null,
                                         periodTicks: 0, periodCount: 5);
            AssertRejected(V.Validate(Passive("on_hit", p, id: "ptest")),
                           "ptest", "effect.period_ticks", "never fires");
        }

        [Fact]
        public void AuraGrantModifier_WithAnUnfireablePeriod_IsRejected()
        {
            // The aura root-shape rules (finite positive duration + Refresh) are satisfied — only the period is wrong,
            // which isolates the promoted rule from the Story 2.6 aura gate.
            var aura = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new ApplyModifierEffect(Mod(duration: 2, period: new HealEffect(Fixed.FromInt(1)), periodTicks: 0)));
            AssertRejected(V.Validate(Passive("aura", aura, id: "ptest")),
                           "ptest", "effect.child.modifier.period_ticks", "never fires");
        }

        [Fact]
        public void NestedModifierMismatch_IsFound_AtAnyDepth()
        {
            // The reject rides the same walk as the AC4/AC5 gates, so it must reach a modifier buried under a Sequence
            // inside a SearchArea — not just a root-level one.
            var graph = new SequenceEffect(
                new HealEffect(Fixed.FromInt(1)),
                new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Enemy,
                    new ApplyModifierEffect(Mod(duration: 30, period: null, periodTicks: 7))));
            AssertRejected(V.Validate(Def(graph)),
                           "effect.children[1].child.modifier.period_ticks", "period_ticks=7", "ignored");
        }

        [Fact]
        public void ThePromotedError_SuppressesACoOccurringWarningOnTheSameDescriptor()
        {
            // period_ticks 0 (now an ERROR) and period_count 0 (still a WARNING) on one descriptor. The result must be
            // the error alone: a rejected graph publishes no warnings, so the author is not handed a warning about a
            // period that the error already told them cannot exist.
            var p = new PersistentEffect(new HealEffect(Fixed.FromInt(1)),
                                         new DamageEffect(Fixed.FromInt(2), DamageType.Normal), null,
                                         periodTicks: 0, periodCount: 0);
            AssertRejected(V.Validate(Def(p)), "effect.period_ticks", "never fires");
        }

        // ── Precedence: the while_alive rules still own their message ──

        [Fact]
        public void WhileAlivePersistent_StillFailsWithItsActivationSpecificMessage()
        {
            // The promoted generic reject is reported only AFTER ValidatePassiveShape, so the older, more specific
            // message is not shadowed — the same defect is reported once, by the rule that owns it.
            var p = new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 0, periodCount: 5);
            AbilityValidationResult r = V.Validate(Passive("while_alive", p, targeting: "Self", id: "ptest"));
            Assert.False(r.Ok);
            Assert.Contains("while_alive", r.Error!);
            Assert.Contains("period_ticks", r.Error!);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void StructuralRejects_StillWinOverThePromotedPeriodReject()
        {
            // A 0-child Sequence is a WalkGraph reject returned INLINE; the deferred period error must not displace it.
            // Ordering is load-bearing: the stack pops children[1] first, so the modifier's period error is already
            // collected when the empty Sequence aborts the walk — and the walk's error is still what surfaces.
            var graph = new SequenceEffect(
                new SequenceEffect(),
                new ApplyModifierEffect(Mod(duration: 30, period: null, periodTicks: 9)));
            AbilityValidationResult r = V.Validate(Def(graph));
            Assert.False(r.Ok);
            Assert.Contains("0 children", r.Error!);
        }

        // ── Teeth: the promotion is narrow — sane periods pass, and the surviving warnings stay warnings ──

        [Fact]
        public void WellFormedPeriods_StillPass_WithNoWarnings()
        {
            Assert.True(V.Validate(Def(new ApplyModifierEffect(
                Mod(duration: 90, period: new HealEffect(Fixed.FromInt(1)), periodTicks: 10)))).Ok);
            // period_ticks 0 with NO period_effect is the well-formed "no period at all" shape — the shipped aura's shape.
            Assert.True(V.Validate(Def(new ApplyModifierEffect(Mod(duration: 90, period: null, periodTicks: 0)))).Ok);
            Assert.True(V.Validate(Def(
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 15, periodCount: 5))).Ok);
            // A Persistent with a period_ticks but no period_effect is NOT part of DW-504's promotion (it was never
            // warned about either) — it must keep validating, or the change over-reached.
            Assert.True(V.Validate(Def(
                new PersistentEffect(new HealEffect(Fixed.FromInt(3)), null, null, periodTicks: 15, periodCount: 5))).Ok);
        }

        [Fact]
        public void TheSurvivingDw278Warnings_AreStillNonFatal()
        {
            // Teeth against over-promotion: these four still PASS with a warning. Rejecting any of them would be a
            // content-breaking change DW-504 did not authorise.
            AbilityValidationResult zeroDuration = V.Validate(Def(new ApplyModifierEffect(Mod(duration: 0))));
            Assert.True(zeroDuration.Ok, zeroDuration.Error);
            Assert.Single(zeroDuration.Warnings);

            AbilityValidationResult stackingPeriod = V.Validate(Def(new ApplyModifierEffect(
                Mod(duration: 90, period: new DamageEffect(Fixed.FromInt(2), DamageType.Magic), periodTicks: 10,
                    rule: StackRule.Stack, maxStacks: 5))));
            Assert.True(stackingPeriod.Ok, stackingPeriod.Error);
            Assert.Single(stackingPeriod.Warnings);

            AbilityValidationResult phaseless = V.Validate(Def(new PersistentEffect(null, null, null, 0, 0)));
            Assert.True(phaseless.Ok, phaseless.Error);
            Assert.Single(phaseless.Warnings);

            AbilityValidationResult zeroCount = V.Validate(Def(
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 15, periodCount: 0)));
            Assert.True(zeroCount.Ok, zeroCount.Error);
            Assert.Single(zeroCount.Warnings);
        }

        // ── The decision's premise: the promotion is FREE because shipped content is clean ──

        [Fact]
        public void EveryShippedAbility_StillLoads_UnderThePromotedRule()
        {
            string dir = ResolveDataDir("abilities");
            string[] files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                AbilityValidationResult r = AbilityLoader.LoadFromFile(file);
                Assert.True(r.Ok, $"{Path.GetFileName(file)}: {r.Error}");
            }
        }

        /// <summary>Walk up from the test binary to the repo's <c>resources/data/&lt;sub&gt;</c>.</summary>
        private static string ResolveDataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }
    }
}
