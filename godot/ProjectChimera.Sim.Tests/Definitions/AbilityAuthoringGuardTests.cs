#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Combat;             // DamageType
using ProjectChimera.Core;               // Fixed, UnitTag
using ProjectChimera.Core.Definitions;   // AbilityValidator / AbilityDefinition / AbilityDraft / ContentJson / AbilityLoader
using ProjectChimera.Effects;            // the closed effect vocabulary + Modifier + EffectBounds + EffectCaps
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// The 2026-08-12 ability AUTHORING-GUARD sweep — seven ledger entries that all name the same seam (what a creator
    /// may author, and what silently happens when they author something the runtime cannot honour). Each region below
    /// is RED without its fix:
    ///
    /// <list type="bullet">
    /// <item><b>DW-562</b> — the ability id gate was a bare non-empty test, so an id of <c>con</c>/<c>nul</c>/<c>com1</c>
    ///   (all of which pass the <c>[a-z0-9_]</c> charset) reached a <c>&lt;id&gt;.json</c> write. The ONE authoring
    ///   surface DW-454's shared filename-safe convention never reached.</item>
    /// <item><b>DW-746</b> — an authored <c>period_count</c> above <c>EffectCaps.MaxPersistentPeriods</c> loaded clean,
    ///   validated clean, and was then SILENTLY CLAMPED to 256 at install.</item>
    /// <item><b>DW-855</b> — an all-zero modifier descriptor (the authorable twin of DW-678's minted one) validated
    ///   silently while consuming a ring slot for nothing.</item>
    /// <item><b>DW-888</b> — no ceiling on a period leaf's magnitude, which a <c>periodic_stack_mode: Multiply</c> pulse
    ///   multiplies by up to <c>MaxPeriodicStackScale</c> through a NON-saturating <c>Fixed</c> multiply: past the
    ///   bound the product wraps negative and a scaled DoT HEALS its victim.</item>
    /// <item><b>DW-892</b> — a <c>teleport</c> with no reachable destination (or an unpassable <c>require_tag</c>) under
    ///   its ability's targeting spent cost + cooldown for nothing, with no diagnostic.</item>
    /// <item><b>DW-293</b> — <c>EffectNodeJsonConverter.Write</c> emitted <c>damage_type: "COUNT"</c> that its own
    ///   <c>Read</c> hard-rejects: a "Saved" ability that cannot be re-opened.</item>
    /// <item><b>DW-323</b> — the Advanced composer's draft model had no <c>lifelong</c> field, so opening a lifelong
    ///   persistent and saving STRIPPED the flag, re-introducing the 256-pulse defect Story 2.13 fixed.</item>
    /// </list>
    ///
    /// Teeth in both directions throughout: every guard is paired with the neighbouring shape it must NOT touch, and
    /// the shipped <c>resources/data/abilities/*.json</c> set is re-asserted clean at the end (all seven guards are
    /// free by construction — nothing shipped trips one).
    /// </summary>
    public class AbilityAuthoringGuardTests
    {
        private static readonly AbilityValidator V = new();

        private static EffectNode Leaf() => new HealEffect(Fixed.FromInt(1));

        private static AbilityDefinition Def(EffectNode? graph, string id = "gtest", string targeting = "Self") =>
            new AbilityDefinition { Id = id, Targeting = targeting, EffectGraph = graph };

        private static AbilityDefinition Passive(string activation, EffectNode graph, string targeting = "None",
                                                 string id = "gtest") =>
            new AbilityDefinition { Id = id, Targeting = targeting, Activation = activation, EffectGraph = graph };

        /// <summary>A modifier carrying ONE real stat delta (the well-formed, non-inert baseline).</summary>
        private static Modifier Mod(int duration = 60, EffectNode? period = null, int periodTicks = 0) =>
            new Modifier(77, duration, StackRule.Refresh, 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(3), moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: period, periodTicks: periodTicks);

        private static void AssertRejected(AbilityValidationResult r, params string[] fragments)
        {
            Assert.False(r.Ok, "expected a hard reject, but the ability validated");
            Assert.NotNull(r.Error);
            Assert.Null(r.Value.Value);          // nothing runnable escaped the gate
            Assert.Empty(r.Warnings);            // a rejected graph publishes its error alone
            foreach (string fragment in fragments) Assert.Contains(fragment, r.Error!);
        }

        private static void AssertOneWarning(AbilityValidationResult r, string expectedFieldPath, params string[] fragments)
        {
            Assert.True(r.Ok, r.Error);          // a warning must NEVER fail the gate
            Assert.NotNull(r.Value.Value);       // the proof-of-validation token is still minted
            Assert.Single(r.Warnings);
            (string FieldPath, string Message) w = r.Warnings[0];
            Assert.Equal(expectedFieldPath, w.FieldPath);
            Assert.Contains("gtest", w.Message);           // located: id …
            Assert.Contains(expectedFieldPath, w.Message); // … + field path
            foreach (string fragment in fragments) Assert.Contains(fragment, w.Message);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-562 — the id gate: the shared filename-safe convention, referenced (never re-declared)
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("con")]
        [InlineData("nul")]
        [InlineData("aux")]
        [InlineData("prn")]
        [InlineData("com1")]
        [InlineData("lpt9")]
        public void ReservedDeviceBasenameId_IsRejected(string id)
        {
            // Every one of these passes the [a-z0-9_] charset — that is exactly why the charset gate alone was not
            // enough, and why a `con` ability wrote res://…/abilities/con.json and threw an opaque IO error.
            AbilityValidationResult r = V.Validate(Def(Leaf(), id));
            AssertRejected(r, id, ".id", "reserved device name", UnitDefinitionValidator.ReservedPipeList);
        }

        [Theory]
        [InlineData("console")]     // merely CONTAINS a device name
        [InlineData("con_2")]       // the MakeUniqueId escape suffix
        [InlineData("nullify")]
        [InlineData("com0")]        // deliberately NOT a reserved device
        [InlineData("lpt0")]
        [InlineData("minor_heal")]  // an ordinary shipped id
        public void OrdinaryIds_StayAuthorable(string id)
        {
            // Teeth: the reject is on the WHOLE basename, not a substring — over-rejecting would make legitimate ids
            // unauthorable and push creators onto the raw-JSON hatch.
            AbilityValidationResult r = V.Validate(Def(Leaf(), id));
            Assert.True(r.Ok, r.Error);
        }

        [Theory]
        [InlineData("Bad Id!")]
        [InlineData("CON")]          // uppercase → not charset-clean, so the charset rule owns it (not the device rule)
        [InlineData("../../foo")]
        [InlineData("fire-ball")]
        public void NonFilenameSafeId_IsRejected_ByTheCharsetRule(string id)
        {
            AbilityValidationResult r = V.Validate(Def(Leaf(), id));
            Assert.False(r.Ok);
            Assert.Contains(".id", r.Error!);
            Assert.Contains("[a-z0-9_]", r.Error!);
        }

        [Fact]
        public void TheIdGate_ReadsTheSharedRule_NotAPrivateCopy()
        {
            // The load-bearing half of DW-562: the four surfaces (item / unit / building / ability) must share ONE
            // convention. Pin the ability verdict against the shared predicates themselves, so a divergent private
            // copy in AbilityValidator would fail here even if its own message looked right.
            foreach (string id in new[] { "con", "COM1", "ok_id", "console", "Bad Id!" })
            {
                bool sharedRuleAccepts = UnitDefinitionValidator.SanitizeId(id) == id
                                         && !UnitDefinitionValidator.IsReservedDeviceName(id);
                Assert.Equal(sharedRuleAccepts, V.Validate(Def(Leaf(), id)).Ok);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-746 — period_count above MaxPersistentPeriods is a reject, so the silent clamp is unreachable
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(257)]
        [InlineData(1000)]
        [InlineData(100000)]
        public void PersistentPeriodCount_AboveTheCap_IsRejected(int periodCount)
        {
            // Before this, ModifierStore.InstallPersistent clamped to 256 and the creator was told nothing — an
            // ability authored for 100000 pulses quietly ran 256 (the DW-227/DW-537 fail-silent class).
            AbilityValidationResult r = V.Validate(Def(
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, periodTicks: 15, periodCount: periodCount)));
            AssertRejected(r, "gtest", "effect.period_count", $"period_count={periodCount}",
                           $"MaxPersistentPeriods={EffectCaps.MaxPersistentPeriods}");
        }

        [Fact]
        public void PersistentPeriodCount_AtTheCap_StillValidates()
        {
            // Teeth on the boundary: the cap itself is authorable and IS shipped (furnace_pour/furnace_trickle both
            // declare period_count 256), so the gate must be `>` and never `>=`.
            Assert.True(V.Validate(Def(new PersistentEffect(
                null, new HealEffect(Fixed.FromInt(2)), null,
                periodTicks: 15, periodCount: EffectCaps.MaxPersistentPeriods))).Ok);
        }

        [Fact]
        public void OverCapPeriodCount_IsRejected_OnAWhileAlivePassiveToo()
        {
            // The rule is activation-independent (the DW-504 posture): the while_alive shape rules only check
            // period_count <= 0, so before this an over-cap while_alive HoT passed every gate.
            var p = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, periodTicks: 15, periodCount: 900);
            AssertRejected(V.Validate(Passive("while_alive", p, targeting: "Self")),
                           "effect.period_count", $"MaxPersistentPeriods={EffectCaps.MaxPersistentPeriods}");
        }

        [Fact]
        public void OverCapPeriodCount_WithNoPeriodEffect_IsNotRejected()
        {
            // Teeth against over-reach: with nothing to pulse, period_count is inert rather than silently clamped —
            // there is no data loss to report, and rejecting would break content the runtime honours exactly.
            Assert.True(V.Validate(Def(
                new PersistentEffect(new HealEffect(Fixed.FromInt(2)), null, null, periodTicks: 0, periodCount: 5000))).Ok);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-855 — the all-zero modifier descriptor warning (non-fatal, DW-278 channel)
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>A descriptor with NO observable payload at all — Modifier.HasNoEffect() is true.</summary>
        private static Modifier InertMod(int duration = 60) =>
            new Modifier(78, duration, StackRule.Refresh, 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None, periodEffect: null, periodTicks: 0);

        [Fact]
        public void AllZeroModifier_Warns_ButStillPasses()
        {
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(InertMod())));
            AssertOneWarning(r, "effect.modifier", "changes nothing", "modifier slots");
            Assert.True(InertMod().HasNoEffect());   // the warning's predicate IS the runtime one, not a restatement
        }

        [Fact]
        public void AllZeroModifier_IsNeverFatal_EvenPermanentOrNested()
        {
            // The DW-678 closure note's reason for the WARNING channel: a zero-stat instance is still observable (a
            // buff-bar row) and is a legitimate marker for a later RemoveByModifierId. Rejecting would break content.
            Assert.True(V.Validate(Def(new ApplyModifierEffect(InertMod(duration: -1)))).Ok);
            AbilityValidationResult nested = V.Validate(Def(new SequenceEffect(
                new HealEffect(Fixed.FromInt(5)),
                new ApplyModifierEffect(InertMod()))));
            Assert.True(nested.Ok, nested.Error);
            Assert.Contains(nested.Warnings, w => w.FieldPath == "effect.children[1].modifier");
        }

        [Fact]
        public void AModifierWithAnyRealPayload_WarnsAboutNothing()
        {
            // Teeth on all six channels HasNoEffect() reads — the lint must stay signal.
            Modifier[] live =
            {
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.FromInt(5), Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0),
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero, StatusFlags.None, null, 0),
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.FromInt(1), StatusFlags.None, null, 0),
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(2)),
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.Invulnerable, null, 0),
                new Modifier(78, 60, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None,
                             new HealEffect(Fixed.FromInt(1)), 10),
            };
            foreach (Modifier m in live)
            {
                Assert.False(m.HasNoEffect());
                Assert.Empty(V.Validate(Def(new ApplyModifierEffect(m))).Warnings);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-888 — the period-leaf magnitude ceiling (FATAL; the DW-488 posture applied to a leaf)
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The smallest raw magnitude whose ×MaxPeriodicStackScale product no longer fits in the 16.16 int.</summary>
        private static Fixed OverBound() => Fixed.FromRaw(EffectBounds.MaxPeriodicPulseMagnitudeRaw + 1);

        private static Fixed AtBound() => Fixed.FromRaw(EffectBounds.MaxPeriodicPulseMagnitudeRaw);

        [Fact]
        public void TheBound_IsExactlyTheWrapThreshold()
        {
            // The arithmetic the whole gate exists for: at the bound the scaled product still fits; one raw tick past
            // it, the SAME multiply the leaves perform wraps NEGATIVE — which is what turns a Multiply DoT into a heal.
            Fixed scale = Fixed.FromInt(EffectCaps.MaxPeriodicStackScale);
            Assert.True((AtBound() * scale).Raw > 0);
            Assert.True((OverBound() * scale).Raw < 0);
            // Derived from the named caps, never a hand-copied literal (CHM0004).
            Assert.Equal(int.MaxValue / EffectCaps.MaxPeriodicStackScale, EffectBounds.MaxPeriodicPulseMagnitudeRaw);
        }

        [Fact]
        public void OverBoundDamageAmount_InAModifierPeriod_IsRejected()
        {
            AbilityValidationResult r = V.Validate(Def(new ApplyModifierEffect(
                Mod(period: new DamageEffect(OverBound(), DamageType.Magic), periodTicks: 10))));
            AssertRejected(r, "gtest", "effect.modifier.period_effect.amount",
                           $"MaxPeriodicPulseMagnitudeRaw={EffectBounds.MaxPeriodicPulseMagnitudeRaw}",
                           $"MaxPeriodicStackScale={EffectCaps.MaxPeriodicStackScale}");
        }

        [Theory]
        [InlineData(true)]   // heal   → amount
        [InlineData(false)]  // direct → delta
        public void OverBoundMagnitude_InAPersistentPeriod_IsRejected(bool heal)
        {
            EffectNode leaf = heal ? new HealEffect(OverBound()) : new DirectHpDeltaEffect(OverBound());
            AbilityValidationResult r = V.Validate(Def(
                new PersistentEffect(null, leaf, null, periodTicks: 15, periodCount: 5)));
            AssertRejected(r, "gtest", heal ? "effect.period_effect.amount" : "effect.period_effect.delta",
                           "wraps the 16.16 product negative");
        }

        [Fact]
        public void NegativeOverBoundMagnitude_IsRejectedToo()
        {
            // The wrap is a magnitude property, not a sign one — |delta| is what the multiply scales.
            AbilityValidationResult r = V.Validate(Def(new PersistentEffect(
                null, new DirectHpDeltaEffect(Fixed.FromRaw(-(EffectBounds.MaxPeriodicPulseMagnitudeRaw + 1))), null,
                periodTicks: 15, periodCount: 5)));
            AssertRejected(r, "effect.period_effect.delta");
        }

        [Fact]
        public void OverBoundMagnitude_IsFound_AtAnyDepthUnderThePeriod()
        {
            // The bound rides the same walk as the AC4/AC5 gates, so a leaf buried under a Sequence inside the period
            // is reached — not just a bare period root.
            AbilityValidationResult r = V.Validate(Def(new PersistentEffect(
                null,
                new SequenceEffect(new HealEffect(Fixed.FromInt(3)), new DamageEffect(OverBound(), DamageType.Normal)),
                null, periodTicks: 15, periodCount: 5)));
            AssertRejected(r, "effect.period_effect.children[1].amount");
        }

        [Fact]
        public void AtBoundMagnitude_InAPeriod_StillValidates()
        {
            // Boundary teeth: the largest magnitude whose scaled product is representable stays authorable.
            Assert.True(V.Validate(Def(new PersistentEffect(
                null, new HealEffect(AtBound()), null, periodTicks: 15, periodCount: 5))).Ok);
        }

        [Fact]
        public void TheBound_IsScopedToPeriodSubtrees_NotEveryLeaf()
        {
            // PulseScale is > 1 ONLY inside a period pulse, so a non-period leaf carries no wrap risk from this
            // multiply — bounding it here would be an unrelated content-breaking change.
            Assert.True(V.Validate(Def(new DamageEffect(OverBound(), DamageType.Magic), targeting: "TargetUnit")).Ok);
            Assert.True(V.Validate(Def(new PersistentEffect(
                new HealEffect(OverBound()), null, null, periodTicks: 0, periodCount: 0))).Ok);   // initial_effect, not period
        }

        [Fact]
        public void TheCheckIgnoresNodeKindsWithNoScalableMagnitude()
        {
            // CheckPeriodicPulseMagnitude must be total AND quiet: only the three leaves that read ctx.PulseScale can
            // ever trip it, so a teleport / presentation cue / composition node inside a period stays clean.
            Assert.Null(EffectBounds.CheckPeriodicPulseMagnitude(new TeleportEffect()));
            Assert.Null(EffectBounds.CheckPeriodicPulseMagnitude(new PlayVfxEffect(null)));
            Assert.Null(EffectBounds.CheckPeriodicPulseMagnitude(new SequenceEffect(new HealEffect(OverBound()))));
            Assert.Null(EffectBounds.CheckPeriodicPulseMagnitude(null));
            Assert.NotNull(EffectBounds.CheckPeriodicPulseMagnitude(new HealEffect(OverBound())));
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-892 — the teleport inertness warnings (non-fatal, DW-278 channel)
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("Self")]
        [InlineData("None")]
        public void Teleport_OnASelfOrNoneTargetedAbility_Warns_NoDestination(string targeting)
        {
            // AbilityCastSystem.TryCast sets `target = id` for Self/None, so TeleportEffect's destination rule finds
            // neither a ground point nor a non-caster target and returns — after cost + cooldown were already spent.
            AssertOneWarning(V.Validate(Def(new TeleportEffect(), targeting: targeting)),
                             "effect", "no destination", "cost and cooldown");
        }

        [Fact]
        public void Teleport_OnAnOnHitRider_DoesNotWarn()
        {
            // Teeth: an on_hit passive is targeting None but CombatSystem.RunOnHit runs it with the STRUCK unit as the
            // primary target, so the teleport is a working charge. Warning here would be a false positive on a
            // legitimate design — and the lint's value depends entirely on staying signal.
            Assert.Empty(V.Validate(Passive("on_hit", new TeleportEffect())).Warnings);
        }

        [Theory]
        [InlineData("TargetUnit")]
        [InlineData("GroundPoint")]
        public void Teleport_OnATargetedOrGroundAbility_DoesNotWarn(string targeting)
        {
            // The two modes that DO give a blink a destination — the shipped blink_strike is the GroundPoint case.
            Assert.Empty(V.Validate(Def(new TeleportEffect(), targeting: targeting)).Warnings);
        }

        [Fact]
        public void Teleport_UnderASearchArea_DoesNotWarn_EvenOnASelfCast()
        {
            // A SearchArea re-centres the context on each match (EffectContext.WithTarget), so the primary target below
            // it is a matched unit, not the caster — the "no destination" premise does not hold there.
            Assert.Empty(V.Validate(Def(
                new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new TeleportEffect()))).Warnings);
        }

        [Fact]
        public void Teleport_InsideAPersistentPhase_DoesNotWarn()
        {
            // ModifierStore runs a persistent/modifier phase against the instance's HOST, which need not be the caster,
            // so the cast-context premise does not hold there either.
            Assert.Empty(V.Validate(Def(
                new PersistentEffect(new TeleportEffect(), null, null, periodTicks: 0, periodCount: 0))).Warnings);
        }

        [Fact]
        public void GroundPointTeleport_WithARequireTag_Warns_TheGateCanNeverPass()
        {
            // A ground cast deliberately leaves PrimaryTargetId at -1, and TagGate.Passes needs a LIVE target — so the
            // executor's leaf gate fails every time and the blink silently never runs.
            AssertOneWarning(V.Validate(Def(new TeleportEffect(UnitTag.Mechanical), targeting: "GroundPoint")),
                             "effect", "require_tag", "Mechanical", "-1");
        }

        [Fact]
        public void GroundPointTeleport_WithoutARequireTag_DoesNotWarn()
        {
            // Teeth: the shipped blink_strike shape (a teleport in a GroundPoint sequence, no tag) stays clean.
            Assert.Empty(V.Validate(Def(new SequenceEffect(
                new TeleportEffect(),
                new PlayVfxEffect(null)), targeting: "GroundPoint")).Warnings);
        }

        [Fact]
        public void RequireTaggedTeleport_UnderASearchArea_DoesNotWarn()
        {
            // Wrapping it in a SearchArea is exactly the remedy the warning names, so the remedy must be silent.
            Assert.Empty(V.Validate(Def(
                new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new TeleportEffect(UnitTag.Mechanical)),
                targeting: "GroundPoint")).Warnings);
        }

        [Fact]
        public void TargetUnitTeleport_WithARequireTag_DoesNotWarn()
        {
            // The tag gate is only unpassable because a GROUND cast has no entity target; on a TargetUnit charge it
            // works exactly as authored.
            Assert.Empty(V.Validate(Def(new TeleportEffect(UnitTag.Organic), targeting: "TargetUnit")).Warnings);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-293 — Write must be the exact inverse of Read for the COUNT sentinel
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void SerializingADamageLeafWithTheCountSentinel_Throws_LikeReadRejectsIt()
        {
            // Read hard-rejects damage_type COUNT (an internal matrix-sizing sentinel). Without the Write guard the
            // converter happily emitted "COUNT" — a file the loader refuses, i.e. a "Saved" ability that will not
            // re-open. Unreachable from the composer by design (DraftVocabulary excludes COUNT); this is the backstop.
            EffectNode node = new DamageEffect(Fixed.FromInt(10), DamageType.COUNT);
            JsonException ex = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(node, ContentJson.Options));
            Assert.Contains("COUNT", ex.Message);
            Assert.Contains("internal sentinel", ex.Message);
        }

        [Fact]
        public void SerializingAWholeAbilityCarryingTheCountSentinel_Throws()
        {
            // The path the editor actually takes (Show-JSON / Save serializes the whole AbilityDefinition).
            AbilityDefinition def = Def(new SequenceEffect(
                new HealEffect(Fixed.FromInt(1)),
                new DamageEffect(Fixed.FromInt(10), DamageType.COUNT)), targeting: "TargetUnit");
            Assert.ThrowsAny<JsonException>(() => JsonSerializer.Serialize(def, ContentJson.Options));
        }

        [Fact]
        public void EveryAuthorableDamageType_StillSerializes()
        {
            // Teeth: the guard is narrow — only the sentinel is blocked, and DraftVocabulary's five real types
            // (including Hero, which is easy to mistake for a reserved value) all still round-trip.
            foreach (DamageType t in DraftVocabulary.DamageTypes)
            {
                EffectNode node = new DamageEffect(Fixed.FromInt(10), t);
                string json = JsonSerializer.Serialize(node, ContentJson.Options);
                Assert.Contains(t.ToString(), json);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // DW-323 — the composer draft must carry persistent.lifelong through a round trip
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void LifelongPersistent_SurvivesTheDraftRoundTrip()
        {
            // The exact defect: open a lifelong ability in Advanced, save it, and the flag was gone — silently
            // re-introducing the 256-pulse expiry Story 2.13 fixed, with the validator unable to notice (it only
            // rejects a lifelong WITHOUT a period, never a period that LOST its lifelong).
            var original = new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null,
                                                periodTicks: 30, periodCount: 12, lifelong: true);

            DraftNode draft = DraftNode.FromEffectNode(original);
            Assert.Equal(DraftKind.Persistent, draft.Kind);
            Assert.True(draft.Lifelong);                                  // captured on the way IN

            var rebuilt = (PersistentEffect)draft.ToEffectNode();
            Assert.True(rebuilt.Lifelong);                                // …and carried on the way OUT
            EffectGraphAssert.Equal(original, rebuilt);
        }

        [Fact]
        public void NonLifelongPersistent_StaysNonLifelong_ThroughTheDraft()
        {
            // Teeth in the other direction: the fix must not flip the flag ON for everything (which would silently
            // make every authored persistent permanent).
            var original = new PersistentEffect(null, new HealEffect(Fixed.FromInt(3)), null,
                                                periodTicks: 30, periodCount: 12);
            Assert.False(original.Lifelong);
            var rebuilt = (PersistentEffect)DraftNode.FromEffectNode(original).ToEffectNode();
            Assert.False(rebuilt.Lifelong);
        }

        [Fact]
        public void ResetKind_ClearsLifelong_LikeEveryOtherPersistentSlot()
        {
            // ResetKind's contract is "preserve nothing across an incompatible kind switch" — a stale lifelong left
            // behind would silently ride onto the next composed node.
            var n = new DraftNode { Kind = DraftKind.Persistent, Lifelong = true, PeriodTicks = 5, PeriodCount = 3 };
            n.ResetKind(DraftKind.Persistent);
            Assert.False(n.Lifelong);
            n.Lifelong = true;
            n.ResetKind(DraftKind.Heal);
            Assert.False(n.Lifelong);
        }

        [Fact]
        public void AShippedLifelongAbility_RoundTripsThroughTheDraft_WithTheFlagIntact()
        {
            // The end-to-end shape the ledger names by file: furnace_trickle / furnace_pour are the shipped lifelong
            // HoTs, and opening either in the composer used to strip the flag.
            foreach (string file in new[] { "furnace_trickle.json", "furnace_pour.json" })
            {
                AbilityValidationResult loaded = AbilityLoader.LoadFromFile(Path.Combine(ResolveDataDir("abilities"), file));
                Assert.True(loaded.Ok, $"{file}: {loaded.Error}");

                AbilityDefinition def = loaded.Value.Value;
                var persistent = Assert.IsType<PersistentEffect>(def.EffectGraph!);
                Assert.True(persistent.Lifelong, $"{file} is expected to be a lifelong HoT — the fixture no longer covers DW-323");

                AbilityDefinition rebuilt = AbilityDraft.FromDefinition(def).ToDefinition();
                EffectGraphAssert.Equal(def.EffectGraph, rebuilt.EffectGraph);
                Assert.True(((PersistentEffect)rebuilt.EffectGraph!).Lifelong);

                // …and the re-composed ability still loads (the editor's save-time round-trip self-check).
                AbilityValidationResult reloaded = AbilityLoader.Load(
                    JsonSerializer.Serialize(rebuilt, ContentJson.Options), rebuilt.Id);
                Assert.True(reloaded.Ok, reloaded.Error);
                Assert.True(((PersistentEffect)reloaded.Value.Value.EffectGraph!).Lifelong);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════════
        // The sweep is FREE: nothing shipped trips any of the seven guards
        // ══════════════════════════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void EveryShippedAbility_StillLoadsCleanAndWarningFree_UnderAllSevenGuards()
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
