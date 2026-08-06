#nullable enable
using System.Linq;
using ProjectChimera.Combat;             // DamageType
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // AbilityValidator, AbilityValidationResult
using ProjectChimera.Effects;            // Modifier, StatusFlags, StatusPolarity, TargetFilter, leaves
using ProjectChimera.UI;                 // ModifierPolarity — the classifier this lint must agree with
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-618 — the ability validator's CROSS-DESCRIPTOR status-polarity lint.
    ///
    /// <para>Every other validator rule reads one descriptor in isolation, and that is precisely how the
    /// self-stunning Guardian Aura shipped: <c>aura_guard.json</c> authored a <c>SearchArea(filter: Ally) →
    /// ApplyModifier(+5 armor, status: Stunned)</c>. A filter of <c>Ally</c> is fine. A status of <c>Stunned</c> is
    /// fine. Only the PAIRING is wrong, and nothing cross-checked the two — so a buff that stunned its own team
    /// passed every gate. DW-266 corrected that one content file; this lint closes the authoring hole, and the flags
    /// are now live (AbilityCastSystem/MovementSystem/CombatSystem honour them), so it has teeth.</para>
    ///
    /// <para>Every test below is RED without the lint: the warning list would simply be empty. The teeth run in both
    /// directions — the shapes that must STAY silent (an Enemy-filtered debuff, an Ally-filtered
    /// <see cref="StatusFlags.Invulnerable"/> grant, a friendly-fire <c>Ally|Enemy</c> AoE, a bare
    /// <see cref="TargetFilter.None"/> search) outnumber the ones that warn, because a warning channel is only worth
    /// having while it stays signal. Shipped-content cleanliness is already pinned by
    /// <c>AbilityInertContentWarningTests.EveryShippedAbility_ValidatesWithZeroWarnings</c>.</para>
    /// </summary>
    public class AbilityStatusPolarityLintTests
    {
        private static readonly AbilityValidator V = new();

        private static AbilityDefinition Def(EffectNode graph, string id = "ptest") =>
            new AbilityDefinition { Id = id, Targeting = "Self", EffectGraph = graph };

        private static AbilityDefinition Aura(EffectNode graph, string id = "ptest") =>
            new AbilityDefinition { Id = id, Targeting = "None", Activation = "aura", EffectGraph = graph };

        /// <summary>A short Refresh grant (the aura-legal shape) carrying <paramref name="status"/> and +5 armor.</summary>
        private static Modifier Grant(StatusFlags status, int duration = 2) =>
            new Modifier(2001, duration, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: status, periodEffect: null, periodTicks: 0, armorDelta: Fixed.FromInt(5));

        private static EffectNode Search(TargetFilter filter, EffectNode child) =>
            new SearchAreaEffect(Fixed.FromInt(5), filter, child);

        /// <summary>Assert the result PASSED and carries exactly one polarity warning at the expected field path.</summary>
        private static void AssertPolarityWarning(AbilityValidationResult r, string expectedFieldPath,
                                                  params string[] fragments)
        {
            Assert.True(r.Ok, r.Error);                    // a polarity warning must NEVER fail the gate
            Assert.NotNull(r.Value.Value);                 // the proof-of-validation token is still minted
            Assert.Single(r.Warnings);
            (string FieldPath, string Message) w = r.Warnings[0];
            Assert.Equal(expectedFieldPath, w.FieldPath);
            Assert.Contains("ptest", w.Message);           // located: id …
            Assert.Contains(expectedFieldPath, w.Message); // … + field path
            foreach (string fragment in fragments) Assert.Contains(fragment, w.Message);
        }

        // ── The exact shipped defect ──

        [Fact]
        public void TheGuardianAuraShape_AllyFilteredSearchGrantingStun_Warns_ButStillPasses()
        {
            // Byte-for-byte the pre-DW-266 aura_guard.json graph: SearchArea(Ally) → ApplyModifier(+5 armor, Stunned).
            // It passed every gate before this lint — including the hard aura rules, which it satisfies (finite
            // positive duration, stacking Refresh).
            AbilityValidationResult r = V.Validate(Aura(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(StatusFlags.Stunned)))));

            AssertPolarityWarning(r, "effect.child.modifier.status",
                "Stunned", "filter Ally", "only friendly units");
        }

        [Fact]
        public void TheFixedGuardianAuraShape_StatusNone_WarnsAboutNothing()
        {
            // Teeth: the DW-266 CONTENT fix (status → None) must be silent, or the lint is noise on shipped content.
            Assert.Empty(V.Validate(Aura(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(StatusFlags.None))))).Warnings);
        }

        // ── Polarity, not "any status" ──

        [Theory]
        [InlineData(StatusFlags.Stunned)]
        [InlineData(StatusFlags.Rooted)]
        [InlineData(StatusFlags.Silenced)]
        [InlineData(StatusFlags.Disarmed)]
        public void EveryHarmfulStatus_OnAnAllyFilteredGrant_Warns(StatusFlags harmful)
        {
            // All four capability-removing flags are covered — not just the one that shipped.
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(harmful)))));
            AssertPolarityWarning(r, "effect.child.modifier.status", harmful.ToString());
        }

        [Fact]
        public void InvulnerableOnAnAllyFilteredGrant_WarnsAboutNothing()
        {
            // The load-bearing teeth: this is a POLARITY check, not "no status flags on an ally grant". Buffing
            // allies with Invulnerable is the entire point of such an ability — warning about it would be a false
            // positive on the most obvious beneficial-status design there is.
            Assert.Empty(V.Validate(Def(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(StatusFlags.Invulnerable))))).Warnings);
        }

        [Fact]
        public void AHarmfulFlagCombinedWithInvulnerable_StillWarns_AboutTheHarmfulHalfOnly()
        {
            // A mixed grant is still a debuff on the ally who gets rooted; the message must name the harmful half
            // and NOT the beneficial one (that would misdirect the author).
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(StatusFlags.Rooted | StatusFlags.Invulnerable)))));
            AssertPolarityWarning(r, "effect.child.modifier.status", "Rooted");
            Assert.DoesNotContain("Invulnerable", r.Warnings[0].Message);
        }

        // ── Allegiance teeth: which filters are "friendly only" ──

        [Fact]
        public void EnemyFilteredSearchGrantingStun_WarnsAboutNothing()
        {
            // The normal, correct authoring of a stun. If this warned, the channel would be useless.
            Assert.Empty(V.Validate(Def(Search(TargetFilter.Enemy,
                new ApplyModifierEffect(Grant(StatusFlags.Stunned))))).Warnings);
        }

        [Fact]
        public void AllyAndEnemyFilter_GrantingStun_WarnsAboutNothing()
        {
            // A friendly-fire AoE stun is a legitimate (if brutal) design: the search demonstrably reaches enemies,
            // so the harmful status has a reading the author plainly intended. Only a provably friendly-ONLY search
            // warns.
            Assert.Empty(V.Validate(Def(Search(TargetFilter.Ally | TargetFilter.Enemy,
                new ApplyModifierEffect(Grant(StatusFlags.Stunned))))).Warnings);
        }

        [Fact]
        public void BareNoneFilter_GrantingStun_WarnsAboutNothing()
        {
            // TargetFilter.None means "every allegiance is eligible" — enemies included. The predicate requires the
            // Ally bit to be SET precisely so this does not trip.
            Assert.Empty(V.Validate(Def(Search(TargetFilter.None,
                new ApplyModifierEffect(Grant(StatusFlags.Stunned))))).Warnings);
        }

        [Fact]
        public void AllyPlusNeutralFilter_GrantingStun_StillWarns()
        {
            // Neutral is not a hostile faction, so this search still hands the debuff to the caster's own side.
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally | TargetFilter.Neutral | TargetFilter.Alive,
                new ApplyModifierEffect(Grant(StatusFlags.Silenced)))));
            AssertPolarityWarning(r, "effect.child.modifier.status", "Silenced");
        }

        [Fact]
        public void SelfTargetedGrantWithNoSearch_WarnsAboutNothing()
        {
            // A self-imposed root/silence as a cost (siege-mode immobility, berserk self-silence) is a well-established
            // design and reaches no ally. The lint only fires under a SearchArea that selects allies.
            Assert.Empty(V.Validate(Def(new ApplyModifierEffect(Grant(StatusFlags.Rooted)))).Warnings);
        }

        // ── Propagation through the walk ──

        [Fact]
        public void AllyFilteredSearch_ThroughASequence_StillWarns_AtTheRightPath()
        {
            // The flag is inherited down Sequence children, and the located path names the offending leaf — not the
            // search — so an author can find it in a large graph.
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally, new SequenceEffect(
                new HealEffect(Fixed.FromInt(10)),
                new ApplyModifierEffect(Grant(StatusFlags.Disarmed))))));
            AssertPolarityWarning(r, "effect.child.children[1].modifier.status", "Disarmed");
        }

        [Fact]
        public void NestedSearch_TheINNERMOSTFilterDecides_EnemyOutsideAllyInside_Warns()
        {
            // EffectContext.WithTarget re-centres a nested search but keeps the ORIGINAL CasterFaction, so the inner
            // Ally bit still resolves against the caster: the grant lands on the caster's own team even though the
            // outer search selected enemies. Inheriting the outer verdict instead of overriding would miss this.
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Enemy,
                Search(TargetFilter.Ally, new ApplyModifierEffect(Grant(StatusFlags.Stunned))))));
            AssertPolarityWarning(r, "effect.child.child.modifier.status", "Stunned");
        }

        [Fact]
        public void NestedSearch_TheINNERMOSTFilterDecides_AllyOutsideEnemyInside_WarnsAboutNothing()
        {
            // The mirror: the inner Enemy search reaches hostiles, so the debuff is correctly authored. An
            // inherit-and-AND propagation would false-positive here.
            Assert.Empty(V.Validate(Def(Search(TargetFilter.Ally,
                Search(TargetFilter.Enemy, new ApplyModifierEffect(Grant(StatusFlags.Stunned)))))).Warnings);
        }

        [Fact]
        public void TwoOffendingGrantsInOneGraph_BothSurface()
        {
            // The channel is a LIST, not a first-fail — an author fixing one must still see the other.
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally, new SequenceEffect(
                new ApplyModifierEffect(Grant(StatusFlags.Stunned)),
                new ApplyModifierEffect(Grant(StatusFlags.Rooted))))));
            Assert.True(r.Ok, r.Error);
            Assert.Equal(2, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.child.children[0].modifier.status");
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.child.children[1].modifier.status");
        }

        // ── Interaction with the rest of the gate ──

        [Fact]
        public void ARejectedAbility_ReportsItsErrorOnly_NoPolarityWarning()
        {
            // A permanent grant fails the hard aura rule. The (otherwise applicable) polarity warning must not also
            // be reported — a failed graph was never walked to completion, so its warnings would be arbitrary.
            AbilityValidationResult r = V.Validate(Aura(Search(TargetFilter.Ally,
                new ApplyModifierEffect(Grant(StatusFlags.Stunned, duration: -1)))));
            Assert.False(r.Ok);
            Assert.Contains("duration_ticks", r.Error!);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void ThePolarityWarning_CoexistsWithTheDW278Warnings()
        {
            // Two independent rules, two independent warnings on ONE descriptor: the stacked-period footgun (DW-278)
            // and the friendly-harmful grant (DW-618). Neither may swallow the other.
            var mod = new Modifier(2002, durationTicks: 90, StackRule.Stack, maxStacks: 5,
                                   maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero,
                                   moveSpeedDelta: Fixed.Zero, status: StatusFlags.Silenced,
                                   periodEffect: new DamageEffect(Fixed.FromInt(2), DamageType.Magic), periodTicks: 10);
            AbilityValidationResult r = V.Validate(Def(Search(TargetFilter.Ally, new ApplyModifierEffect(mod))));

            Assert.True(r.Ok, r.Error);
            Assert.Equal(2, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.child.modifier.period_effect");
            Assert.Contains(r.Warnings, w => w.FieldPath == "effect.child.modifier.status");
        }

        // ── One classification, two consumers ──

        [Fact]
        public void TheLintAgreesWithTheBuffDebuffIconClassifier_ForEveryStatusFlag()
        {
            // DW-618's whole premise is that ModifierPolarity ALREADY encodes the needed partition, so the fix must
            // reuse it rather than hand-copy it. This is the anti-drift pin: for every single flag, "the icon paints
            // it as a Debuff" and "an Ally-filtered grant of it warns" must be the same answer. Add a sixth flag and
            // classify it in only one of the two places and this test fails.
            foreach (StatusFlags flag in new[]
                     {
                         StatusFlags.Stunned, StatusFlags.Rooted, StatusFlags.Silenced,
                         StatusFlags.Disarmed, StatusFlags.Invulnerable,
                     })
            {
                bool iconSaysDebuff =
                    ModifierPolarity.Classify(Grant(flag)) == ModifierPolarity.Polarity.Debuff;
                bool lintWarns = V.Validate(Def(Search(TargetFilter.Ally, new ApplyModifierEffect(Grant(flag)))))
                                  .Warnings.Any(w => w.FieldPath == "effect.child.modifier.status");

                Assert.True(iconSaysDebuff == lintWarns,
                    $"{flag}: icon classifier says Debuff={iconSaysDebuff} but the validator lint warns={lintWarns} — " +
                    "the two consumers of StatusPolarity.Harmful have drifted.");
            }
        }

        [Fact]
        public void HarmfulAndBeneficialPartitionTheWholeFlagSet_WithNoOverlap()
        {
            // Guards the constants themselves: every non-None flag must be classified exactly once. A new flag added
            // to StatusFlags without a polarity decision fails here rather than silently defaulting to "not harmful"
            // (which would make the lint quietly blind to it).
            Assert.Equal(StatusFlags.None, StatusPolarity.Harmful & StatusPolarity.Beneficial);

            StatusFlags all = StatusFlags.None;
            foreach (StatusFlags flag in System.Enum.GetValues<StatusFlags>()) all |= flag;
            Assert.Equal(StatusPolarity.Harmful | StatusPolarity.Beneficial, all);
        }
    }
}
