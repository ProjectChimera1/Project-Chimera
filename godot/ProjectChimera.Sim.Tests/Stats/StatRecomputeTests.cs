#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Stats
{
    /// <summary>
    /// Story 15-24a — the generalized recompute's behavior suite: legacy bit-parity (the zero-golden-movement
    /// invariant), the percent stage (order-independence, revert exactness, the floored multiplier), the four
    /// new consumer channels (attack_speed / cooldown_reduction / health_regen / vision), MulSaturating, and
    /// the per-stat BEHAVIORAL coverage sweep (every modifier-authorable registry stat must observably move
    /// its declared consumer channel — the tripwire's runtime teeth).
    /// </summary>
    public class StatRecomputeTests
    {
        private static readonly Fixed Dt = Fixed.Zero;

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        private static Modifier VecMod(int id, int duration, params StatDelta[] deltas) =>
            new Modifier(id, duration, StackRule.Refresh, 1,
                StatVocabulary.Canonicalize(new List<StatDelta>(deltas)), StatusFlags.None, null, 0);

        private static StatDelta D(StatId s, Fixed v) => new StatDelta(s, v);

        // ── Fixed.MulSaturating: the percent stage's arithmetic contract ─────────────────────────────────

        [Fact]
        public void MulSaturating_ByOne_IsBitExact_AndSaturatesInsteadOfWrapping()
        {
            foreach (int raw in new[] { 0, 1, -1, 65536, -65536, 123_456_789, -123_456_789, int.MaxValue, int.MinValue })
                Assert.Equal(raw, Fixed.MulSaturating(Fixed.FromRaw(raw), Fixed.One).Raw); // ×1 exact — the parity invariant

            Assert.Equal(int.MaxValue, Fixed.MulSaturating(Fixed.MaxValue, Fixed.FromInt(2)).Raw);  // saturate, not wrap
            Assert.Equal(int.MinValue, Fixed.MulSaturating(Fixed.MaxValue, Fixed.FromInt(-2)).Raw);
        }

        // ── Percent stage semantics ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void MaxHealthPercent_MultipliesTheFlatStage_AndHealsByTheRealizedCeilingGain()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            store.Apply(id, VecMod(1, -1, D(StatId.MaxHealthPercent, Fixed.Half)), id, Faction.Player1);

            Assert.Equal(Fixed.FromInt(150).Raw, world.EffectiveMaxHealth[id].Raw); // 100 × 1.5
            Assert.Equal(Fixed.FromInt(150).Raw, world.Health[id].Raw);             // Decision #3 generalized: heal by the +50 the buff realized
        }

        [Fact]
        public void PercentAndFlat_Compose_FlatFirst_AndAreOrderIndependent()
        {
            var (worldA, _, storeA) = Wire();
            var (worldB, _, storeB) = Wire();
            int a = worldA.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int b = worldB.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            var flat = VecMod(1, -1, D(StatId.MaxHealth, Fixed.FromInt(50)));
            var pct  = VecMod(2, -1, D(StatId.MaxHealthPercent, Fixed.Half));

            storeA.Apply(a, flat, a, Faction.Player1); storeA.Apply(a, pct, a, Faction.Player1);
            storeB.Apply(b, pct, b, Faction.Player1); storeB.Apply(b, flat, b, Faction.Player1);

            // (100 + 50) × 1.5 = 225 — flat sums BEFORE the multiplier, whatever the apply order (AC2 generalized).
            Assert.Equal(Fixed.FromInt(225).Raw, worldA.EffectiveMaxHealth[a].Raw);
            Assert.Equal(worldA.EffectiveMaxHealth[a].Raw, worldB.EffectiveMaxHealth[b].Raw);
        }

        [Fact]
        public void PercentExpiry_RevertsExactly_ToTheFlatStageValue()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(40);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(40);

            store.Apply(id, VecMod(1, 1, D(StatId.AttackDamagePercent, Fixed.FromRaw(Fixed.ONE / 4))), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveAttackDamage[id].Raw); // 40 × 1.25

            sys.Tick(world, Dt); // duration 1 → expires; the summed accumulator returns to exactly 0
            Assert.Equal(Fixed.FromInt(40).Raw, world.EffectiveAttackDamage[id].Raw);
        }

        [Fact]
        public void DeepPercentDebuff_ZeroesTheStat_NeverSignFlipsIt()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseMoveSpeed[id] = Fixed.FromInt(4);
            world.EffectiveMoveSpeed[id] = Fixed.FromInt(4);

            // Σ = −2 → the realized multiplier (1 + Σ) floors at 0: speed 0, never a NEGATIVE (reversed) speed.
            store.Apply(id, VecMod(1, -1, D(StatId.MoveSpeedPercent, Fixed.FromInt(-2))), id, Faction.Player1);
            Assert.Equal(0, world.EffectiveMoveSpeed[id].Raw);
        }

        [Fact]
        public void PercentCeilingCollapse_KillsThroughTheSameRail_AsAFlatCollapse()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // −100% max health: (100 + 0) × max(0, 1 − 1) = 0 — the DW-325 downward transition, carried by a
            // percent delta the old flat-sign gate could never have seen.
            store.Apply(id, VecMod(1, -1, D(StatId.MaxHealthPercent, Fixed.NegOne)), id, Faction.Player1);
            Assert.False(world.IsAlive(id));
        }

        // ── The four new consumer channels ────────────────────────────────────────────────────────────────

        [Fact]
        public void AttackSpeed_DividesTheSwingInterval_WithTheMachineGunFloor()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.AttackSpeed[id] = Fixed.FromInt(2); // authored: 2s between attacks

            Assert.Equal(Fixed.FromInt(2).Raw, world.AttackIntervalOf(id).Raw); // identity factor short-circuit

            store.Apply(id, VecMod(1, -1, D(StatId.AttackSpeed, Fixed.One)), id, Faction.Player1); // +100% faster
            Assert.Equal(Fixed.FromInt(1).Raw, world.AttackIntervalOf(id).Raw); // 2 / (1+1)

            store.Apply(id, VecMod(2, -1, D(StatId.AttackSpeed, Fixed.FromInt(8))), id, Faction.Player1); // Σ=9 (the clamp max)
            Assert.True(world.AttackIntervalOf(id).Raw >= SimulationLoop.FixedDt.Raw,
                "the machine-gun floor: an interval below one sim tick would re-arm already-expired and fire every tick");

            // A non-attacker (authored interval 0) never gains an interval from a speed buff.
            int idle = world.Create(new FixedVec3(Fixed.One, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            store.Apply(idle, VecMod(3, -1, D(StatId.AttackSpeed, Fixed.One)), idle, Faction.Player1);
            Assert.Equal(0, world.AttackIntervalOf(idle).Raw);
        }

        [Fact]
        public void AttackSpeedDebuff_SlowsTheInterval()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.AttackSpeed[id] = Fixed.FromInt(1);

            store.Apply(id, VecMod(1, -1, D(StatId.AttackSpeed, -Fixed.Half)), id, Faction.Player1); // −50%
            Assert.Equal(Fixed.FromInt(2).Raw, world.AttackIntervalOf(id).Raw); // 1 / 0.5
        }

        [Fact]
        public void CooldownReduction_ClampsAtItsRegistryCap()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            store.Apply(id, VecMod(1, -1, D(StatId.CooldownReduction, Fixed.FromInt(2))), id, Faction.Player1);
            Assert.Equal(StatVocabulary.CooldownReductionSumMaxRaw, world.EffectiveCooldownReduction[id].Raw); // 0.8 cap
        }

        [Fact]
        public void HealthRegen_HealsPerTick_ClampsAtCeiling_AndNeverDrains()
        {
            var (world, _, store) = Wire();
            var regen = new HealthRegenSystem();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(90);

            regen.Tick(world, Dt); // no regen anywhere → byte-identical no-op
            Assert.Equal(Fixed.FromInt(90).Raw, world.Health[id].Raw);

            store.Apply(id, VecMod(1, -1, D(StatId.HealthRegen, Fixed.FromInt(4))), id, Faction.Player1);
            regen.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(94).Raw, world.Health[id].Raw);
            regen.Tick(world, Dt); regen.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw); // clamped at EffectiveMaxHealth

            // A regen DEBUFF past the base floors the effective at 0 — never a silent unaudited drain.
            store.Apply(id, VecMod(2, -1, D(StatId.HealthRegen, Fixed.FromInt(-99))), id, Faction.Player1);
            Assert.Equal(0, world.EffectiveHealthRegen[id].Raw);
            regen.Tick(world, Dt);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void VisionStats_MergeInVisionWithElevation()
        {
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            Assert.Equal(Fixed.FromFloat(8f).Raw, world.VisionWithElevation(id).Raw); // the Create default, untouched

            store.Apply(id, VecMod(1, -1, D(StatId.VisionRange, Fixed.FromInt(4))), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(12).Raw, world.VisionWithElevation(id).Raw); // 8 + 4

            store.Apply(id, VecMod(2, -1, D(StatId.VisionPercent, Fixed.Half)), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(18).Raw, world.VisionWithElevation(id).Raw); // (8 + 4) × 1.5
        }

        // ── The behavioral coverage sweep: every modifier-authorable stat moves a declared observable ─────

        [Fact]
        public void EveryModifierAuthorableStat_ObservablyMovesItsConsumerChannel()
        {
            // The observable per stat, hand-mapped ON PURPOSE: this map is the test's own completeness gate —
            // a new modifier-authorable registry stat with no entry here fails the assertion below, forcing the
            // author to wire (and prove) its consumer in the same change. The recipe's step (3), made runtime.
            var observables = new Dictionary<StatId, System.Func<EntityWorld, int, Fixed>>
            {
                [StatId.MaxHealth]           = (w, id) => w.EffectiveMaxHealth[id],
                [StatId.AttackDamage]        = (w, id) => w.EffectiveAttackDamage[id],
                [StatId.Armor]               = (w, id) => w.EffectiveArmor[id],
                [StatId.MoveSpeed]           = (w, id) => w.EffectiveMoveSpeed[id],
                [StatId.AttackSpeed]         = (w, id) => w.AttackIntervalOf(id),
                [StatId.HealthRegen]         = (w, id) => w.EffectiveHealthRegen[id],
                [StatId.VisionRange]         = (w, id) => w.VisionWithElevation(id),
                [StatId.CooldownReduction]   = (w, id) => w.EffectiveCooldownReduction[id],
                [StatId.MaxHealthPercent]    = (w, id) => w.EffectiveMaxHealth[id],
                [StatId.AttackDamagePercent] = (w, id) => w.EffectiveAttackDamage[id],
                [StatId.MoveSpeedPercent]    = (w, id) => w.EffectiveMoveSpeed[id],
                [StatId.VisionPercent]       = (w, id) => w.VisionWithElevation(id),
                // Story 15-24b — the combat dice (chance/bonus channels; the DRAWS are pinned in CritDodgeRollTests).
                [StatId.CritChance]          = (w, id) => w.EffectiveCritChance[id],
                [StatId.DodgeChance]         = (w, id) => w.EffectiveDodgeChance[id],
                [StatId.CritMultiplier]      = (w, id) => w.CritMultiplierOf(id),
            };

            foreach (var def in StatVocabulary.All)
            {
                if (!def.ModifierAuthorable) continue;
                Assert.True(observables.ContainsKey(def.Id),
                    $"stat '{def.JsonName}' is modifier-authorable but this sweep has no observable mapped for it — " +
                    "wire its consumer and add the map entry (the add-a-stat recipe's step 3).");

                var (world, _, store) = Wire();
                int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
                // Give every base-backed channel a non-zero base so a percent/divisor stat has something to move.
                world.BaseAttackDamage[id] = Fixed.FromInt(10); world.EffectiveAttackDamage[id] = Fixed.FromInt(10);
                world.BaseArmor[id] = Fixed.FromInt(2); world.EffectiveArmor[id] = Fixed.FromInt(2);
                world.AttackSpeed[id] = Fixed.FromInt(2);

                Fixed before = observables[def.Id](world, id);
                store.Apply(id, VecMod(1, -1, D(def.Id, Fixed.FromRaw(Fixed.ONE / 4))), id, Faction.Player1);
                Fixed after = observables[def.Id](world, id);

                Assert.True(before.Raw != after.Raw,
                    $"DECLARED-BUT-NOT-CONSUMED (behavioral): a modifier carrying only '{def.JsonName}' moved nothing " +
                    $"at its declared consumer ({def.ConsumerSite}).");
            }
        }

        // ── Save-restore reconstruction: the accumulators (percent lanes included) rebuild from the ring ──

        [Fact]
        public void RestoreSlot_RebuildsPercentAccumulators_SoTheFirstRecomputeReproducesTheSavedEffective()
        {
            // Live host: flat + percent modifiers applied normally.
            var (world, _, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            var mod = VecMod(41, -1, D(StatId.MaxHealth, Fixed.FromInt(20)), D(StatId.MaxHealthPercent, Fixed.Half));
            store.Apply(id, mod, id, Faction.Player1);
            Fixed savedEffective = world.EffectiveMaxHealth[id];
            Assert.Equal(Fixed.FromInt(180).Raw, savedEffective.Raw); // (100+20) × 1.5

            // "Loaded" host: overlay Base/Effective the way the save does, then RestoreSlot + first Tick.
            var (world2, sys2, store2) = Wire();
            int id2 = world2.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world2.EffectiveMaxHealth[id2] = savedEffective; world2.Health[id2] = savedEffective;
            store2.RestoreSlot(id2, 0, mod.Id, ModifierStore.PERMANENT, 0, 0, 1, id2, Faction.Player1, mod, null);
            store2.SetCount(id2, 1);
            sys2.Tick(world2, Dt); // the dirty-flag handoff — must reproduce the saved value EXACTLY (idempotence)
            Assert.Equal(savedEffective.Raw, world2.EffectiveMaxHealth[id2].Raw);
        }
    }
}
