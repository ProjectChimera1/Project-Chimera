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
    /// DW-278 — the ability validator's non-fatal WARNING channel. Before this, <c>AbilityValidationResult</c> carried
    /// only <c>Ok</c>/<c>Error</c>/<c>Value</c>, so the whole authorable-but-INERT class was silent: content that loads,
    /// validates, runs, and quietly does nothing (or not what the author wrote). Each test here proves one footgun now
    /// surfaces a LOCATED warning while <see cref="AbilityValidationResult.Ok"/> stays TRUE (rejecting these would break
    /// existing content and overreach the fail-closed gate) — every one is RED without the channel, because
    /// <c>Warnings</c> would be empty.
    ///
    /// Teeth in both directions: well-formed graphs warn about NOTHING (including all shipped
    /// <c>resources/data/abilities/*.json</c>, so the channel is signal not noise), a REJECTED ability reports its error
    /// only, and the cases the hard passive rules already reject are not double-reported.
    ///
    /// <para>DW-504 narrowed the channel: the three <c>period_ticks</c>/<c>period_effect</c> MISMATCH cases graduated to
    /// hard rejects (owner decision, 2026-08-04) and are covered by <see cref="AbilityPeriodMismatchRejectTests"/>. What
    /// stays here is the class that genuinely must NOT reject — every shape below loads, runs, and does something; it just
    /// does not do what a naive author would assume.</para>
    /// </summary>
    public class AbilityInertContentWarningTests
    {
        private static readonly AbilityValidator V = new();

        private static AbilityDefinition Def(EffectNode? graph, string id = "wtest") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        private static AbilityDefinition Passive(string activation, EffectNode graph, string id = "wpass") =>
            new AbilityDefinition { Id = id, Targeting = "None", Activation = activation, EffectGraph = graph };

        /// <summary>A stat-only modifier with an explicit duration (the well-formed baseline).</summary>
        private static Modifier Mod(int duration, EffectNode? period = null, int periodTicks = 0,
                                    StackRule rule = StackRule.Refresh, int maxStacks = 1) =>
            new Modifier(11, duration, rule, maxStacks,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(3), moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: period, periodTicks: periodTicks);

        /// <summary>Assert the result PASSED and carries exactly one warning whose located message hits every fragment.</summary>
        private static void AssertOneWarning(AbilityValidationResult r, string expectedFieldPath, params string[] fragments)
        {
            Assert.True(r.Ok, r.Error);                            // a warning must NEVER fail the gate
            Assert.NotNull(r.Value.Value);                         // the proof-of-validation token is still minted
            Assert.Single(r.Warnings);
            (string FieldPath, string Message) w = r.Warnings[0];
            Assert.Equal(expectedFieldPath, w.FieldPath);
            Assert.Contains("wtest", w.Message);                   // located: id …
            Assert.Contains(expectedFieldPath, w.Message);         // … + field path
            foreach (string fragment in fragments) Assert.Contains(fragment, w.Message);
        }

        // ── The DW-270 semantics footgun ──

        [Fact]
        public void ModifierWithZeroDurationTicks_Warns_ButStillPasses()
        {
            // DW-270: 0 reads as "instantaneous" to an author but is a ONE-TICK modifier. Warn, never reject — 0 is a
            // legal (if surprising) authored value and rejecting it would be a breaking change.
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(Mod(duration: 0))));
            AssertOneWarning(r, "effect.modifier.duration_ticks", "duration_ticks 0", "one full tick");
        }

        [Fact]
        public void ModifierWithPositiveOrPermanentDuration_WarnsAboutNothing()
        {
            // Teeth: the two SANE durations must be silent, or the warning is noise.
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(Mod(duration: 90)))).Warnings);
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(Mod(duration: -1)))).Warnings);
        }

        // ── Inert modifier periods ──
        //
        // DW-504 (owner decision 2026-08-04): the two period_ticks/period_effect MISMATCH cases that used to live here
        // — a period_effect with period_ticks <= 0, and period_ticks > 0 with no period_effect — were promoted from
        // warnings to HARD REJECTS, matching the while_alive Persistent rule. Their coverage moved to
        // AbilityPeriodMismatchRejectTests. What remains below is the part of the modifier channel that is still
        // (correctly) non-fatal: a shape that runs and does something, just not what a naive author expected.

        [Fact]
        public void StackingPeriodicModifier_Warns_ThePulseDoesNotScaleWithStacks()
        {
            // The non-scaling stacked-DoT footgun: Apply re-adds the stat deltas per stack, but Advance runs the period
            // ONCE per boundary regardless of _stackCount — so a "stacking DoT" deals flat damage however deep it stacks.
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(duration: 90, period: new DamageEffect(Fixed.FromInt(2), DamageType.Magic), periodTicks: 10,
                    rule: StackRule.Stack, maxStacks: 5))));
            AssertOneWarning(r, "effect.modifier.period_effect", "max_stacks=5", "does not");
        }

        [Fact]
        public void NonStackingPeriodicModifier_WarnsAboutNothing()
        {
            // Teeth: a correct periodic modifier (Refresh, positive period) is silent.
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(
                Mod(duration: 90, period: new HealEffect(Fixed.FromInt(1)), periodTicks: 10)))).Warnings);
        }

        // ── DW-272 / Story 15.12: periodic_stack_mode narrows the footgun and adds an inert-mode warning ──

        private static Modifier PeriodicMod(StackRule rule, int maxStacks, PeriodicStackMode mode,
                                            EffectNode? period, int periodTicks) =>
            new Modifier(11, 90, rule, maxStacks, Fixed.Zero, Fixed.FromInt(3), Fixed.Zero,
                         StatusFlags.None, period, periodTicks, Fixed.Zero, mode);

        [Fact]
        public void StackingPeriodicModifier_WithMultiplyOrRepeat_WarnsAboutNothing_ThePulseScales()
        {
            // DW-272: once the author opts into a scaling mode, the "pulse does not scale" footgun no longer applies.
            var mul = PeriodicMod(StackRule.Stack, 5, PeriodicStackMode.Multiply,
                                  new DamageEffect(Fixed.FromInt(2), DamageType.Magic), 10);
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(mul))).Warnings);
            var rep = PeriodicMod(StackRule.Stack, 5, PeriodicStackMode.Repeat,
                                  new DamageEffect(Fixed.FromInt(2), DamageType.Magic), 10);
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(rep))).Warnings);
        }

        [Fact]
        public void PeriodicMode_OnANonStackingRule_Warns_HasNoEffectHere()
        {
            // Refresh never holds more than one instance, so a periodic_stack_mode can scale nothing.
            var m = PeriodicMod(StackRule.Refresh, 1, PeriodicStackMode.Repeat, new HealEffect(Fixed.FromInt(1)), 10);
            AssertOneWarning(V.Validate(Def(new ApplyModifierEffect(m))),
                "effect.modifier.periodic_stack_mode", "no effect here", "does not stack");
        }

        [Fact]
        public void PeriodicMode_OnStackIndependent_Warns_HasNoEffectHere()
        {
            // Each StackIndependent stack is its OWN single pulse, so the mode is a documented no-op.
            var m = PeriodicMod(StackRule.StackIndependent, 3, PeriodicStackMode.Multiply, new HealEffect(Fixed.FromInt(1)), 10);
            AssertOneWarning(V.Validate(Def(new ApplyModifierEffect(m))),
                "effect.modifier.periodic_stack_mode", "no effect here", "StackIndependent");
        }

        [Fact]
        public void PeriodicMode_WithNoLivePeriod_Warns_HasNoEffectHere()
        {
            // A scaling mode on a modifier with no period_effect at all — nothing to pulse.
            var m = PeriodicMod(StackRule.Stack, 3, PeriodicStackMode.Repeat, period: null, periodTicks: 0);
            AssertOneWarning(V.Validate(Def(new ApplyModifierEffect(m))),
                "effect.modifier.periodic_stack_mode", "no effect here", "no live period_effect");
        }

        [Fact]
        public void PeriodicMode_OnStackWithMaxStacksOne_Warns_HasNoEffectHere()
        {
            // The final inert branch: Stack + a live period, but max_stacks=1 so the scale is always min(1, cap)=1 —
            // the mode can never do anything. The warning interpolates the real max_stacks (never a hardcoded 1).
            var m = PeriodicMod(StackRule.Stack, 1, PeriodicStackMode.Multiply, new HealEffect(Fixed.FromInt(1)), 10);
            AssertOneWarning(V.Validate(Def(new ApplyModifierEffect(m))),
                "effect.modifier.periodic_stack_mode", "no effect here", "max_stacks=1");
        }

        // ── Inert Persistent shapes on a NON-while_alive ability (where nothing gated them) ──

        [Fact]
        public void PhaselessPersistent_OnActiveAbility_Warns_InstallDoesNothing()
        {
            // ValidatePassiveShape hard-rejects this for while_alive; on an ACTIVE ability it was completely ungated —
            // a validated, castable, energy-costing no-op.
            AbilityValidationResult r = V.Validate(Def(new PersistentEffect(null, null, null, 0, 0)));
            AssertOneWarning(r, "effect", "no initial_effect", "does nothing");
        }

        // DW-504: the active-ability Persistent's `period_effect with period_ticks <= 0` warning was promoted to a hard
        // reject too (see AbilityPeriodMismatchRejectTests) — the rule no longer depends on the activation.

        [Fact]
        public void ActivePersistent_WithZeroPeriodCount_Warns_ExpiresBeforeItsFirstPulse()
        {
            // InstallPersistent writes _periodsRemaining = period_count, so 0 expires the instance on tick 1.
            AbilityValidationResult r = V.Validate(Def(
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 15, periodCount: 0)));
            AssertOneWarning(r, "effect.period_count", "period_count=0", "before its first pulse");
        }

        [Fact]
        public void LifelongPersistent_WithZeroPeriodCount_DoesNotWarn_TheReArmCoversIt()
        {
            // Teeth for the exemption: Story 2.13's lifelong re-arm refills the budget on the first Advance, so a 0
            // count is NOT inert here. Warning it anyway would be a false positive.
            Assert.Empty(V.Validate(Def(new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null,
                                                            periodTicks: 15, periodCount: 0, lifelong: true))).Warnings);
        }

        [Fact]
        public void LifelongPersistent_WithNoPeriodEffect_OnActiveAbility_Warns_FlagIgnored()
        {
            // while_alive hard-rejects this (Story 2.13 AC4.2); on an active ability the flag silently does nothing.
            AbilityValidationResult r = V.Validate(Def(
                new PersistentEffect(new HealEffect(Fixed.FromInt(3)), null, null, 0, 0, lifelong: true)));
            AssertOneWarning(r, "effect.lifelong", "lifelong is ignored");
        }

        // ── The channel never competes with, or leaks past, the hard gates ──

        [Fact]
        public void RejectedAbility_ReportsItsErrorOnly_WithNoWarnings()
        {
            // An aura whose grant is a 0-duration modifier: the hard aura rule REJECTS it, so the (otherwise applicable)
            // duration_ticks warning must not also be reported — a failed graph was never walked to completion.
            var aura = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new ApplyModifierEffect(Mod(duration: 0)));
            AbilityValidationResult r = V.Validate(Passive("aura", aura));
            Assert.False(r.Ok);
            Assert.Contains("duration_ticks", r.Error!);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void WhileAlivePersistent_KeepsItsHardReject_NotDowngradedToAWarning()
        {
            // Teeth against a regression that turns the Story 2.6/2.13 hard passive rules into mere warnings.
            var p = new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null, periodTicks: 0, periodCount: 5);
            AbilityValidationResult r = V.Validate(Passive("while_alive", p));
            Assert.False(r.Ok);
            Assert.Contains("period_ticks", r.Error!);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void DefaultResult_ExposesAnEmptyWarningList_NotNull()
        {
            // The struct's zero value has no allocated list; Warnings must still be safely enumerable.
            AbilityValidationResult zero = default;
            Assert.NotNull(zero.Warnings);
            Assert.Empty(zero.Warnings);
            Assert.Empty(AbilityValidationResult.Fail("ability 'x'.effect: boom").Warnings);
        }

        [Fact]
        public void MultipleFootgunsInOneGraph_AllSurface()
        {
            // Two independent modifiers, two independent defects — the channel is a LIST, not a first-fail. (Both are
            // still WARNINGS post-DW-504: a one-tick modifier and a stacking period both run and do something.)
            var seq = new SequenceEffect(
                new ApplyModifierEffect(Mod(duration: 0)),                                        // one-tick surprise
                new ApplyModifierEffect(Mod(duration: 60, period: new DamageEffect(Fixed.FromInt(2), DamageType.Magic),
                                            periodTicks: 20, rule: StackRule.Stack, maxStacks: 4)));  // non-scaling stacked DoT
            AbilityValidationResult r = V.Validate(Def(seq));
            Assert.True(r.Ok, r.Error);
            Assert.Equal(2, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.children[0].modifier.duration_ticks");
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.children[1].modifier.period_effect");
        }

        // ── Shipped content is warning-clean (the channel is signal, not noise) ──

        [Fact]
        public void EveryShippedAbility_ValidatesWithZeroWarnings()
        {
            string dir = ResolveDataDir("abilities");
            string[] files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(files);

            foreach (string file in files)
            {
                AbilityValidationResult r = AbilityLoader.LoadFromFile(file);
                Assert.True(r.Ok, $"{Path.GetFileName(file)}: {r.Error}");
                Assert.True(r.Warnings.Count == 0,
                    $"{Path.GetFileName(file)} raised authoring warnings: {string.Join(" | ", r.Warnings.Select(w => w.Message))}");
            }
        }

        /// <summary>Walk up from the test binary to the repo's <c>resources/data/&lt;sub&gt;</c> (mirrors SignatureMechanicRealContentTests).</summary>
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
