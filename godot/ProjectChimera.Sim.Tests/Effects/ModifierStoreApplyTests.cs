#nullable enable
using ProjectChimera.Combat; // CombatEventQueue — DW-325 ceiling-collapse-death wiring (MatchStats lives in Core)
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.2b (AC5) — stat-modifier apply/remove through the 2.2a <c>AccumulateBonus</c> seam, plus the
    /// negative-stat Zero-floor and the MaxHealth Health semantics. Proves:
    ///   • apply adds the deltas so <c>Effective* == Base + Σ</c> the same tick; expiry reverts to <c>Base</c>;
    ///   • status flags set on apply, recomputed (NOT blindly cleared) on remove — a flag a second modifier still
    ///     holds survives;
    ///   • a debuff can never drive a stat below <see cref="Fixed.Zero"/> (the Zero-floor — RED without it);
    ///   • MaxHealth semantics (Decision #3 = heal-on-apply, refined in 2.2b review to heal-on-BUFF-apply ONLY): a
    ///     +MaxHealth buff RAISES current Health by the same amount (a burst heal); removal clamps Health DOWN to the
    ///     new ceiling (no phantom HP); a −MaxHealth DEBUFF round-trip restores the ceiling WITHOUT restoring HP (no
    ///     free heal from a wearing-off enemy debuff — RED under the old symmetric model).
    /// Bare worlds via <see cref="EntityWorld.Create"/>; <see cref="Fixed.FromInt"/> only; independently-derived raws.
    /// </summary>
    public class ModifierStoreApplyTests
    {
        private static readonly Fixed Dt = Fixed.Zero; // periods are tick-counted; the dt arg is unused

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        /// <summary>A pure stat/status modifier (no period).</summary>
        private static Modifier StatMod(int id, int duration, StackRule rule, int maxStacks,
            int maxHp, int atk, int move, StatusFlags status = StatusFlags.None) =>
            new Modifier(id, duration, rule, maxStacks, Fixed.FromInt(maxHp), Fixed.FromInt(atk),
                         Fixed.FromInt(move), status, periodEffect: null, periodTicks: 0);

        [Fact]
        public void Apply_AddsDeltas_SameTick_Then_Expiry_RevertsToBase()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.BaseMoveSpeed[id]    = Fixed.FromInt(4);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);
            world.EffectiveMoveSpeed[id]    = Fixed.FromInt(4);

            // duration 1 → expires on the first Advance.
            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: 0, atk: 5, move: 2), id, Faction.Player1);

            // Eager recompute inside Apply: Effective == Base + delta with NO Tick needed (the same-tick guarantee).
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[id].Raw); // 10 + 5
            Assert.Equal(Fixed.FromInt(6).Raw,  world.EffectiveMoveSpeed[id].Raw);    // 4 + 2
            Assert.Equal(1, store.CountAt(id));

            sys.Tick(world, Dt); // duration 1 → expires → RemoveSlot reverts the bonus

            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[id].Raw); // back to Base
            Assert.Equal(Fixed.FromInt(4).Raw,  world.EffectiveMoveSpeed[id].Raw);
            Assert.Equal(0, store.CountAt(id));
        }

        [Fact]
        public void NegativeDebuff_FloorsEffectiveAtZero_NotNegative()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[id] = Fixed.FromInt(10);
            world.EffectiveAttackDamage[id] = Fixed.FromInt(10);

            // A −9999 attack debuff. Without the Zero-floor this would be 10 + (−9999) = −9989 → RED.
            store.Apply(id, StatMod(1, 10, StackRule.Refresh, 1, maxHp: 0, atk: -9999, move: 0), id, Faction.Player1);

            Assert.Equal(Fixed.Zero.Raw, world.EffectiveAttackDamage[id].Raw); // floored at 0, never negative
        }

        [Fact]
        public void MaxHealthBuff_HealsOnApply_And_ClampsDownOnRemove() // Decision #3 (Alec): heal-proportionally-on-apply
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            // Full-HP unit: 100/100.
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);

            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: 50, atk: 0, move: 0), id, Faction.Player1);

            // Heal-on-apply: the rising ceiling raises current Health by the same amount → 150/150.
            Assert.Equal(Fixed.FromInt(150).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(150).Raw, world.Health[id].Raw);

            sys.Tick(world, Dt); // duration 1 → remove

            // Remove clamps Health DOWN to the new (base) ceiling — no phantom HP.
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void MaxHealthBuff_OnDamagedUnit_HealsAdditively_NotToFull()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50); // damaged: 50/100

            store.Apply(id, StatMod(1, 10, StackRule.Refresh, 1, maxHp: 50, atk: 0, move: 0), id, Faction.Player1);

            // Additive heal (current += maxDelta), NOT fill-to-full: 50 + 50 = 100 of a new 150 ceiling → 100/150.
            Assert.Equal(Fixed.FromInt(150).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void MaxHealthDebuff_RoundTrip_RestoresCeiling_WithoutHealing() // 2.2b review (D1): heal-on-BUFF-apply only
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.Health[id] = Fixed.FromInt(50); // damaged: 50/100

            // A −50 MaxHealth debuff (duration 1). Apply drops the ceiling to 50 and clamps Health to 50 → 50/50.
            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: -50, atk: 0, move: 0), id, Faction.Player1);
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, world.Health[id].Raw);

            sys.Tick(world, Dt); // duration 1 → debuff removed

            // Ceiling restored to 100 — but Health is NOT healed up (clamp-only on removal). Old symmetric model
            // would have added +50 here (→ 100/100, a free heal from a wearing-off enemy debuff). RED without the fix.
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw, world.Health[id].Raw);
        }

        [Fact]
        public void StatusFlags_SetOnApply_SurviveRemoveWhileAnotherModifierHoldsThem()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // A: Stunned for 2 ticks. B (distinct id): Stunned for 1 tick.
            store.Apply(id, StatMod(1, 2, StackRule.Refresh, 1, 0, 0, 0, StatusFlags.Stunned), id, Faction.Player1);
            store.Apply(id, StatMod(2, 1, StackRule.Refresh, 1, 0, 0, 0, StatusFlags.Stunned), id, Faction.Player1);
            Assert.True((world.StatusFlagsOf[id] & StatusFlags.Stunned) != 0);

            sys.Tick(world, Dt); // B expires; A still holds Stunned → the flag must SURVIVE (RED if remove blindly clears)
            Assert.True((world.StatusFlagsOf[id] & StatusFlags.Stunned) != 0);
            Assert.Equal(1, store.CountAt(id));

            sys.Tick(world, Dt); // A expires → no modifier holds Stunned → cleared
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]);
            Assert.Equal(0, store.CountAt(id));
        }

        // ── DW-325: modifier-driven ceiling-collapse death (EffectiveMaxHealth → 0 kills the 0-HP "zombie") ──

        [Fact]
        public void MaxHealthDebuff_ZeroesCeiling_KillsHost_ClearsSlots()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            Assert.True(world.IsAlive(id));

            // A −100 MaxHealth debuff drives EffectiveMaxHealth to max(0, 100−100) = 0 → the host would be a 0-HP-alive
            // zombie under the old code; DW-325 raises death the SAME apply through DamageResolver.KillEntity.
            // The modifier ALSO carries a Stunned status: this pins the fresh-install re-entrancy guard
            // (`if (!_world.IsAlive(targetId)) return true;` before `StatusFlagsOf[id] |= mod.Status`). Delete that guard
            // and the `|= Stunned` writes onto the just-killed/recycled slot → StatusFlagsOf[id] == Stunned (RED). The
            // guard makes the status write never happen, so the cleared host stays None.
            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, maxHp: -100, atk: 0, move: 0, status: StatusFlags.Stunned), id, Faction.Player1);

            Assert.False(world.IsAlive(id));    // died this same apply — no zombie
            Assert.Equal(0, store.CountAt(id)); // OnDestroy→ClearEntity wiped the just-installed slot (re-entrancy safe)
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]); // fresh-install guard skipped the status write onto the dead slot
        }

        [Fact]
        public void MaxHealthDebuff_LeavesCeilingAboveZero_DoesNotKill()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // −99 leaves EffectiveMaxHealth = 1 (> 0): Health clamps DOWN but the unit stays alive (unchanged behavior).
            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, maxHp: -99, atk: 0, move: 0), id, Faction.Player1);

            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.FromInt(1).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(1).Raw, world.Health[id].Raw); // clamped into [0, ceiling], no death
            Assert.Equal(1, store.CountAt(id));
        }

        [Fact]
        public void CeilingCollapseDeath_CountsVictimLoss_AndCreditsTheCastersFaction()
        {
            // Fully-wired store so RecordKill is reachable.
            //
            // DW-490 SUPERSEDES this test's original "…ButCreditsNoKill" reading. The DW-325 spec hardcoded the killer
            // to Faction.Neutral, which made a collapse the one lethal path invisible to scoring; the kill now carries
            // the collapsing instance's OWN recorded caster. Here the modifier is SELF-cast by a Player1 unit, so
            // Player1 is credited — the same posture AbilityCastSystem's self-lethal `cost_health` death already has
            // ("a self-lethal cast credits the caster"). The attacker-less form is still reachable and still credits
            // nobody: an instance with no caster records (−1, Faction.Neutral), pinned by
            // ModifierCollapseAttributionTests.RulesDrivenCollapse_WithNoCaster_StaysAttackerLess_ButStillFeedsTheXpRuntime.
            var world  = new EntityWorld();
            var sys    = new ModifierSystem();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            var store  = new ModifierStore(world, sys, events: events, stats: stats);
            sys.AttachStore(store);

            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, maxHp: -100, atk: 0, move: 0), id, Faction.Player1);

            Assert.False(world.IsAlive(id));
            // The victim's LOSS is counted (Player1 lost a unit)…
            Assert.Equal(1, stats.Losses(Faction.Player1));
            // …and the KILL is credited to the caster's faction (DW-490), not silently dropped on Neutral.
            Assert.Equal(1, stats.Kills(Faction.Player1));
            Assert.Equal(0, stats.Kills(Faction.Player2));
            Assert.Equal(0, stats.Kills(Faction.Neutral)); // index 0 is never credited by RecordKill
            Assert.Equal((int)Faction.Player1 - 1, world.KillerFactionOf[id]);
            Assert.Equal(id, world.KillerOf[id]);          // the self-cast caster is the recorded attacker

            // The death goes through the SINGLE combat death sequence, which pushes exactly one UnitKilled event for
            // the victim (its faction + position, captured pre-Destroy). Drain the queue and pin it — proves the death
            // sequence the DW-325 comment claims to emit actually fires (and only once, no phantom/duplicate).
            int unitKilled = 0;
            CombatEvent killed = default;
            for (int e = 0; e < events.Count; e++)
            {
                CombatEvent ev = events.Get(e);
                if (ev.Type == CombatEventType.UnitKilled) { unitKilled++; killed = ev; }
            }
            Assert.Equal(1, unitKilled);
            Assert.Equal(Faction.Player1, killed.Faction);       // victim faction stamped on the event
            Assert.True(killed.Position == FixedVec3.Zero);      // victim died at its spawn position (created at Zero)
        }

        [Fact]
        public void RemovalDrivenCeilingCollapse_KillsHostDuringRemove_SecondUnitSurvives()
        {
            // P2: exercises the REMOVAL/expiry-driven collapse (RemoveSlot → ApplyStatDeltas(isApply:false)) and its
            // `if (!_world.IsAlive(hostId)) return;` guard, which prevents a CompactSlot on a wiped host from corrupting
            // the previous entity's slot. Also pins the mid-Advance re-entrancy: a SECOND (higher-id) plain-alive unit
            // must survive the same expiry Tick, proving Advance's `n = _count[i]` re-read after the death is safe.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int survivor = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            Assert.True(survivor > id); // survivor iterated AFTER id in Advance's ascending-id walk

            // +100 buff (duration 1 → expires first Tick) AND −100 debuff (duration 5 → survives), distinct ids, both
            // Refresh. Net accumulator 0 → EffectiveMaxHealth stays 100 and the unit is alive after both applies.
            store.Apply(id, StatMod(1, 1, StackRule.Refresh, 1, maxHp: 100, atk: 0, move: 0), id, Faction.Player1);
            store.Apply(id, StatMod(2, 5, StackRule.Refresh, 1, maxHp: -100, atk: 0, move: 0), id, Faction.Player1);
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(2, store.CountAt(id));

            // The buff expires → RemoveSlot reverts +100 → ceiling recomputes to max(0, 100−100)=0 → death DURING removal.
            sys.Tick(world, Dt);

            Assert.False(world.IsAlive(id));       // died in the removal path (not an apply)
            Assert.Equal(0, store.CountAt(id));    // OnDestroy→ClearEntity wiped all slots (guard skipped the CompactSlot)
            Assert.True(world.IsAlive(survivor));  // higher-id neighbour untouched by the mid-Advance death
            Assert.Equal(0, store.CountAt(survivor));
        }

        [Fact]
        public void RecomputePipeline_SaturatesMaxHealth_InsteadOfWrappingToZero()
        {
            // P3: drives AddSaturating THROUGH RecomputeEntity (the pipeline), not in isolation. A pathological base plus
            // a large single bonus would wrap negative under the old int operator+ and collapse to the Zero-floor;
            // AddSaturating must clamp to int.MaxValue instead. 30000+10000 in 16.16 raw exceeds int.MaxValue.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseMaxHealth[id]      = Fixed.FromInt(30000);
            world.EffectiveMaxHealth[id] = Fixed.FromInt(30000);

            // A SINGLE +10000 MaxHealth modifier: accumulator = +10000 (no accumulator wrap); the Base+bonus read wraps.
            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, maxHp: 10000, atk: 0, move: 0), id, Faction.Player1);

            Assert.True(world.IsAlive(id)); // saturated ceiling is huge (not 0) → no bogus ceiling-collapse death
            Assert.Equal(int.MaxValue, world.EffectiveMaxHealth[id].Raw); // saturated, NOT wrapped-negative-then-floored-to-0
        }

        [Fact]
        public void RecomputePipeline_SaturatesAttackMoveArmor_InsteadOfWrappingToZero()
        {
            // Pins the OTHER THREE DW-28 recompute swaps — RecomputeEntity's EffectiveAttackDamage (ModifierSystem.cs:90),
            // EffectiveMoveSpeed (:92), and EffectiveArmor (:94) — which the MaxHealth pipeline test above does NOT cover.
            // Revert ANY of those three lines to plain `operator+` and the matching arm here is RED: a pathological base +
            // large bonus wraps negative under the unchecked int add → Zero-floored to 0. AddSaturating must clamp to
            // int.MaxValue instead. Three distinct entities so no cross-stat interference; 30000+10000 raw > int.MaxValue.

            // Attack (line 90)
            var (world, sys, store) = Wire();
            int atkId = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseAttackDamage[atkId]      = Fixed.FromInt(30000);
            world.EffectiveAttackDamage[atkId] = Fixed.FromInt(30000);
            store.Apply(atkId, StatMod(1, 5, StackRule.Refresh, 1, maxHp: 0, atk: 10000, move: 0), atkId, Faction.Player1);
            Assert.Equal(int.MaxValue, world.EffectiveAttackDamage[atkId].Raw); // saturated, not wrapped-then-Zero-floored

            // Move (line 92)
            int moveId = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseMoveSpeed[moveId]      = Fixed.FromInt(30000);
            world.EffectiveMoveSpeed[moveId] = Fixed.FromInt(30000);
            store.Apply(moveId, StatMod(2, 5, StackRule.Refresh, 1, maxHp: 0, atk: 0, move: 10000), moveId, Faction.Player1);
            Assert.Equal(int.MaxValue, world.EffectiveMoveSpeed[moveId].Raw);

            // Armor (line 94) — not exposed by StatMod, so build the Modifier directly with the trailing armorDelta arg.
            int armorId = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseArmor[armorId]      = Fixed.FromInt(30000);
            world.EffectiveArmor[armorId] = Fixed.FromInt(30000);
            var armorMod = new Modifier(3, 5, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                                        StatusFlags.None, periodEffect: null, periodTicks: 0, armorDelta: Fixed.FromInt(10000));
            store.Apply(armorId, armorMod, armorId, Faction.Player1);
            Assert.Equal(int.MaxValue, world.EffectiveArmor[armorId].Raw);

            // None of these touch MaxHealth (maxHealthChange.Raw == 0), so the DW-325 collapse-kill never fires — all alive.
            Assert.True(world.IsAlive(atkId) && world.IsAlive(moveId) && world.IsAlive(armorId));
        }

        [Fact]
        public void MaxHealthBuff_HealNearMaxValue_SaturatesHealth_InsteadOfWrappingToZombie()
        {
            // Pins the OTHER DW-28 saturation site: the +MaxHealth heal-up in ApplyStatDeltas (Health = AddSaturating(
            // Health, maxHealthChange)) — NOT the RecomputeEntity clamp above. Revert that line to `Health[id] +=
            // maxHealthChange` and this is RED: with Health near Fixed.MaxValue a large +MaxHealth heal wraps Health
            // NEGATIVE, the [0, ceiling] clamp drops it to 0, and because the saturated ceiling is huge (!= 0) the
            // DW-325 kill never fires → a live 0-HP zombie with a non-zero ceiling (the exact state DW-28 removes).
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            world.BaseMaxHealth[id]      = Fixed.FromInt(30000);
            world.EffectiveMaxHealth[id] = Fixed.FromInt(30000);
            world.Health[id]             = Fixed.FromInt(30000); // near Fixed.MaxValue (~32767) in 16.16 raw

            // +10000 MaxHealth buff: ceiling saturates to int.MaxValue AND the heal (30000+10000 raw) exceeds int.MaxValue.
            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, maxHp: 10000, atk: 0, move: 0), id, Faction.Player1);

            Assert.True(world.IsAlive(id));                            // no zombie — alive with real HP
            Assert.Equal(int.MaxValue, world.EffectiveMaxHealth[id].Raw); // ceiling saturated high
            Assert.True(world.Health[id].Raw > 0);                    // heal saturated positive; under `+=` this wraps → clamps to 0 (RED)
            Assert.Equal(int.MaxValue, world.Health[id].Raw);         // saturated to the ceiling, not wrapped-then-clamped-to-0
        }

        [Fact]
        public void StackBranch_CollapsingStackKillsHost_NoThrow()
        {
            // P4: the Stack-branch `if (!_world.IsAlive(targetId)) return true;` guard. A net-negative MaxHealth modifier
            // with StackRule.Stack whose per-stack −50 collapses EffectiveMaxHealth to 0 on the SECOND stack must kill
            // the host on the collapsing stack and throw nothing.
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));

            // Rooted status pins the Stack-branch re-entrancy guard specifically (the fresh install is stack 1, which
            // stays alive; only stack 2 collapses, so this exercises the `if (!_world.IsAlive(targetId)) return true;`
            // guard at the Stack branch, before its `StatusFlagsOf[id] |= mod.Status` re-OR). Without the guard the
            // collapsing stack re-ORs Rooted onto the killed/recycled slot → StatusFlagsOf[id] == Rooted (RED).
            var mod = StatMod(1, 10, StackRule.Stack, maxStacks: 2, maxHp: -50, atk: 0, move: 0, status: StatusFlags.Rooted);
            store.Apply(id, mod, id, Faction.Player1); // stack 1 → ceiling 50, alive
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveMaxHealth[id].Raw);

            store.Apply(id, mod, id, Faction.Player1); // stack 2 → ceiling max(0, 100−100)=0 → dies on the collapsing stack

            Assert.False(world.IsAlive(id));
            Assert.Equal(0, store.CountAt(id)); // OnDestroy→ClearEntity wiped the slot; the Stack-branch guard returned safely
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]); // Stack-branch guard skipped the status re-OR onto the dead slot
        }

        [Fact]
        public void Apply_OnDeadOrStaleId_IsNoOp_NoThrow()
        {
            var (world, sys, store) = Wire();
            int id = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.Destroy(id);

            store.Apply(id, StatMod(1, 5, StackRule.Refresh, 1, 0, 5, 0), id, Faction.Player1); // dead target
            store.Apply(9999, StatMod(1, 5, StackRule.Refresh, 1, 0, 5, 0), 0, Faction.Player1); // out-of-range id

            Assert.Equal(0, store.CountAt(id));
        }
    }
}
