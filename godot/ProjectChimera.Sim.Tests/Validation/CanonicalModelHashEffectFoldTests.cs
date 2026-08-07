#nullable enable
using ProjectChimera.Core;              // Fixed, UnitTag
using ProjectChimera.Core.Definitions;  // ScenarioData, CanonicalModelHash
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Dsl;               // TriggerGraph
using ProjectChimera.Effects;           // effect kinds, Modifier, StackRule, StatusFlags, TargetFilter
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 (follow-up review) — per-field SENSITIVITY pins for the v8 embedded <c>run_effect</c> sub-fold
    /// (<see cref="CanonicalModelHash"/> <c>MixEffect</c> / <c>MixModifier</c>). The declaration-fold suite only
    /// pinned <see cref="DirectHpDeltaEffect.Delta"/>, so a silent drop or reorder inside any OTHER effect/modifier
    /// field would reopen a handshake hole (two peers with a semantically divergent effect payload passing the lobby
    /// and desyncing in-sim) without turning a test red. Each case below mutates exactly ONE folded field of an
    /// <c>apply_modifier</c>/<c>damage</c>/… embed and asserts the canonical hash MOVES — the same discipline the
    /// region/trigger/declaration sensitivity suites use.
    /// </summary>
    public class CanonicalModelHashEffectFoldTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        /// <summary>Wrap each effect into the standard single-trigger run_effect graph and assert the two embeds
        /// hash differently — i.e. the mutated field is genuinely folded.</summary>
        private static void AssertFolded(EffectNode a, EffectNode b)
        {
            var ma = BaseModel();
            ma.TriggerGraphJson = TriggerGraph.BuildRunEffectTrigger("t", "match_start", a).ToCanonicalJson();
            var mb = BaseModel();
            mb.TriggerGraphJson = TriggerGraph.BuildRunEffectTrigger("t", "match_start", b).ToCanonicalJson();
            Assert.NotEqual(CanonicalModelHash.Compute(ma), CanonicalModelHash.Compute(mb));
        }

        private static Modifier Mod(
            int id = 1, int durationTicks = 30, StackRule stacking = StackRule.Refresh, int maxStacks = 1,
            Fixed maxHealthDelta = default, Fixed attackDamageDelta = default, Fixed moveSpeedDelta = default,
            StatusFlags status = StatusFlags.None, EffectNode? periodEffect = null, int periodTicks = 0,
            Fixed armorDelta = default, PeriodicStackMode periodicStacking = PeriodicStackMode.None)
            => new Modifier(id, durationTicks, stacking, maxStacks, maxHealthDelta, attackDamageDelta,
                            moveSpeedDelta, status, periodEffect, periodTicks, armorDelta, periodicStacking);

        // ── DirectHpDelta: RequireTag (Delta is already pinned in the declaration-fold suite) ──
        [Fact] public void DirectHpDelta_RequireTag_Folds() =>
            AssertFolded(new DirectHpDeltaEffect(Fixed.FromInt(-1), UnitTag.None),
                         new DirectHpDeltaEffect(Fixed.FromInt(-1), UnitTag.Organic));

        // ── Heal: Amount, RequireTag ──
        [Fact] public void Heal_Amount_Folds() =>
            AssertFolded(new HealEffect(Fixed.FromInt(5)), new HealEffect(Fixed.FromInt(6)));
        [Fact] public void Heal_RequireTag_Folds() =>
            AssertFolded(new HealEffect(Fixed.FromInt(5), UnitTag.None),
                         new HealEffect(Fixed.FromInt(5), UnitTag.Mechanical));

        // ── Damage: Amount, Type, RequireTag ──
        [Fact] public void Damage_Amount_Folds() =>
            AssertFolded(new DamageEffect(Fixed.FromInt(10), DamageType.Normal),
                         new DamageEffect(Fixed.FromInt(11), DamageType.Normal));
        [Fact] public void Damage_Type_Folds() =>
            AssertFolded(new DamageEffect(Fixed.FromInt(10), DamageType.Normal),
                         new DamageEffect(Fixed.FromInt(10), DamageType.Pierce));
        [Fact] public void Damage_RequireTag_Folds() =>
            AssertFolded(new DamageEffect(Fixed.FromInt(10), DamageType.Normal, UnitTag.None),
                         new DamageEffect(Fixed.FromInt(10), DamageType.Normal, UnitTag.Magical));

        // ── ApplyModifier: every Modifier field ──
        [Fact] public void Modifier_Id_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(id: 1)), new ApplyModifierEffect(Mod(id: 2)));
        [Fact] public void Modifier_DurationTicks_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(durationTicks: 30)), new ApplyModifierEffect(Mod(durationTicks: 31)));
        [Fact] public void Modifier_Stacking_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(stacking: StackRule.Refresh)),
                         new ApplyModifierEffect(Mod(stacking: StackRule.Stack)));
        [Fact] public void Modifier_MaxStacks_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(maxStacks: 1)), new ApplyModifierEffect(Mod(maxStacks: 3)));
        [Fact] public void Modifier_MaxHealthDelta_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(maxHealthDelta: Fixed.FromInt(1))),
                         new ApplyModifierEffect(Mod(maxHealthDelta: Fixed.FromInt(2))));
        [Fact] public void Modifier_AttackDamageDelta_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(attackDamageDelta: Fixed.FromInt(1))),
                         new ApplyModifierEffect(Mod(attackDamageDelta: Fixed.FromInt(2))));
        [Fact] public void Modifier_MoveSpeedDelta_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(moveSpeedDelta: Fixed.FromInt(1))),
                         new ApplyModifierEffect(Mod(moveSpeedDelta: Fixed.FromInt(2))));
        [Fact] public void Modifier_ArmorDelta_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(armorDelta: Fixed.FromInt(1))),
                         new ApplyModifierEffect(Mod(armorDelta: Fixed.FromInt(2))));
        [Fact] public void Modifier_Status_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(status: StatusFlags.None)),
                         new ApplyModifierEffect(Mod(status: StatusFlags.Stunned)));
        [Fact] public void Modifier_PeriodEffect_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(periodEffect: null, periodTicks: 5)),
                         new ApplyModifierEffect(Mod(periodEffect: new HealEffect(Fixed.FromInt(1)), periodTicks: 5)));
        [Fact] public void Modifier_PeriodTicks_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(periodTicks: 5)), new ApplyModifierEffect(Mod(periodTicks: 6)));
        [Fact] public void Modifier_RequireTag_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(), UnitTag.None),
                         new ApplyModifierEffect(Mod(), UnitTag.Organic));
        // DW-272 / Story 15.12 — the new PeriodicStacking field folds (CanonicalModelHash AlgoVersion 14→15).
        [Fact] public void Modifier_PeriodicStacking_Folds() =>
            AssertFolded(new ApplyModifierEffect(Mod(periodicStacking: PeriodicStackMode.None)),
                         new ApplyModifierEffect(Mod(periodicStacking: PeriodicStackMode.Multiply)));

        // ── Sequence: child count + child content ──
        [Fact] public void Sequence_ChildCount_Folds() =>
            AssertFolded(new SequenceEffect(new HealEffect(Fixed.FromInt(1))),
                         new SequenceEffect(new HealEffect(Fixed.FromInt(1)), new HealEffect(Fixed.FromInt(1))));
        [Fact] public void Sequence_ChildContent_Folds() =>
            AssertFolded(new SequenceEffect(new HealEffect(Fixed.FromInt(1))),
                         new SequenceEffect(new HealEffect(Fixed.FromInt(2))));

        // ── SearchArea: Radius, Filter, RequireTag, Child ──
        [Fact] public void SearchArea_Radius_Folds() =>
            AssertFolded(new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1))),
                         new SearchAreaEffect(Fixed.FromInt(6), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1))));
        [Fact] public void SearchArea_Filter_Folds() =>
            AssertFolded(new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1))),
                         new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,  new HealEffect(Fixed.FromInt(1))));
        [Fact] public void SearchArea_RequireTag_Folds() =>
            AssertFolded(new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1)), UnitTag.None),
                         new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1)), UnitTag.Organic));
        [Fact] public void SearchArea_Child_Folds() =>
            AssertFolded(new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(1))),
                         new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Enemy, new HealEffect(Fixed.FromInt(2))));

        // ── Persistent: InitialEffect, PeriodEffect, ExpireEffect, PeriodTicks, PeriodCount, Lifelong ──
        [Fact] public void Persistent_InitialEffect_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3),
                         new PersistentEffect(new HealEffect(Fixed.FromInt(1)), new HealEffect(Fixed.FromInt(1)), null, 5, 3));
        [Fact] public void Persistent_PeriodEffect_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3),
                         new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, 5, 3));
        [Fact] public void Persistent_ExpireEffect_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3),
                         new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), new HealEffect(Fixed.FromInt(1)), 5, 3));
        [Fact] public void Persistent_PeriodTicks_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3),
                         new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 6, 3));
        [Fact] public void Persistent_PeriodCount_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3),
                         new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 4));
        [Fact] public void Persistent_Lifelong_Folds() =>
            AssertFolded(new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3, lifelong: false),
                         new PersistentEffect(null, new HealEffect(Fixed.FromInt(1)), null, 5, 3, lifelong: true));

        // ── The effect KIND discriminator itself must fold (two different leaves must not alias) ──
        [Fact] public void EffectKindDiscriminator_Folds() =>
            AssertFolded(new HealEffect(Fixed.FromInt(1)), new DamageEffect(Fixed.FromInt(1), DamageType.Normal));

        // ── Story 15.13 (DW-248): the four new leaves ──────────────────────────────────────────────────
        //
        // (a) RequireTag folds for each new kind (the one semantic field they carry). Teleport folds RequireTag as a
        //     sim leaf; the three presentation cues fold kind+RequireTag ONLY (their Feedback is consciously excluded).

        [Fact] public void Teleport_RequireTag_Folds() =>
            AssertFolded(new TeleportEffect(UnitTag.None), new TeleportEffect(UnitTag.Organic));
        [Fact] public void PlayVfx_RequireTag_Folds() =>
            AssertFolded(new PlayVfxEffect(null, UnitTag.None), new PlayVfxEffect(null, UnitTag.Organic));
        [Fact] public void PlaySound_RequireTag_Folds() =>
            AssertFolded(new PlaySoundEffect(null, UnitTag.None), new PlaySoundEffect(null, UnitTag.Organic));
        [Fact] public void ShakeScreen_RequireTag_Folds() =>
            AssertFolded(new ShakeScreenEffect(null, UnitTag.None), new ShakeScreenEffect(null, UnitTag.Organic));

        // (b) The KIND discriminator: all four new kinds hash DISTINCTLY from one another AND from existing leaves.
        //     Catches a copy-paste like ShakeScreenEffect mixing "play_vfx" — that would collide two of these hashes.
        [Fact]
        public void NewLeafKinds_HashDistinctly_FromEachOther_AndFromExistingLeaves()
        {
            EffectNode[] leaves =
            {
                new TeleportEffect(),
                new PlayVfxEffect(null),
                new PlaySoundEffect(null),
                new ShakeScreenEffect(null),
                new HealEffect(Fixed.FromInt(1)),                        // existing leaf
                new DamageEffect(Fixed.FromInt(1), DamageType.Normal),   // existing leaf
            };
            var hashes = new ulong[leaves.Length];
            for (int i = 0; i < leaves.Length; i++) hashes[i] = HashOf(leaves[i]);
            for (int i = 0; i < hashes.Length; i++)
                for (int j = i + 1; j < hashes.Length; j++)
                    Assert.True(hashes[i] != hashes[j],
                        $"effect leaves at indices {i} and {j} hash identically ({hashes[i]}) — a kind discriminator collision.");
        }

        // (c) CHECKSUM-NEUTRALITY TEETH: two presentation leaves of the SAME kind carrying DIFFERENT Feedback fold to
        //     the SAME hash — proving the float-bearing Feedback is excluded from the fold VALUE (not merely classified
        //     as excluded structurally). If a regression started folding Feedback, these turn RED.
        [Fact] public void PlayVfx_Feedback_DoesNotFold() =>
            AssertSameFold(new PlayVfxEffect(FeedbackA()), new PlayVfxEffect(FeedbackB()));
        [Fact] public void PlaySound_Feedback_DoesNotFold() =>
            AssertSameFold(new PlaySoundEffect(FeedbackA()), new PlaySoundEffect(FeedbackB()));
        [Fact] public void ShakeScreen_Feedback_DoesNotFold() =>
            AssertSameFold(new ShakeScreenEffect(FeedbackA()), new ShakeScreenEffect(FeedbackB()));

        /// <summary>The canonical hash of the standard single-trigger run_effect graph wrapping <paramref name="e"/>.</summary>
        private static ulong HashOf(EffectNode e)
        {
            var m = BaseModel();
            m.TriggerGraphJson = TriggerGraph.BuildRunEffectTrigger("t", "match_start", e).ToCanonicalJson();
            return CanonicalModelHash.Compute(m);
        }

        /// <summary>Assert the two embeds hash IDENTICALLY — the mutated field is genuinely NOT folded (the inverse of
        /// <see cref="AssertFolded"/>).</summary>
        private static void AssertSameFold(EffectNode a, EffectNode b) => Assert.Equal(HashOf(a), HashOf(b));

        // Two feedback payloads that differ in every folded-if-it-were-folded field (sound id + shake).
        private static CombatFeedbackProfile FeedbackA() => new CombatFeedbackProfile
        {
            ImpactSoundId = "alpha",
            Shake = new ShakeSpec { DurationSec = 0.10f, Strength = 0.20f },
            HitFlash = new FlashSpec { ColorRgb = new[] { 0.1f, 0.2f, 0.3f }, Scale = 1.0f, DurationSec = 0.2f },
        };
        private static CombatFeedbackProfile FeedbackB() => new CombatFeedbackProfile
        {
            ImpactSoundId = "omega",
            Shake = new ShakeSpec { DurationSec = 0.90f, Strength = 0.80f },
            HitFlash = new FlashSpec { ColorRgb = new[] { 0.9f, 0.8f, 0.7f }, Scale = 2.5f, DurationSec = 0.7f },
        };
    }
}
