#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-662 — the TARGET half of the mid-<see cref="ModifierStore.Advance"/> dead-end guard.
    ///
    /// <para><see cref="LethalPeriodMidAdvanceTests"/> is the teeth of the DW-267 HOST bail: the post-condition
    /// <c>if (!_world.IsAlive(i)) break;</c> that stops the walk from rewriting/compacting a ring
    /// <c>ClearEntity</c> already wiped (the <c>CompactSlot</c>/<c>_count</c> corruption class — an
    /// <c>IndexOutOfRangeException</c> at owner id 0, a <c>_count</c> of −1 at higher ids). It covers only the case
    /// where the pulse kills its OWN host. Nothing covered a pulse resolving against an entity that is NOT its host,
    /// because nothing can produce one today: <c>ModifierStore</c> builds every effect context with
    /// <c>spatial: null</c> and <c>targetId == hostId</c>, so a <c>SearchArea</c> inside a period fans out to nobody
    /// and every period leaf is direct-target.</para>
    ///
    /// <para>These are the teeth of the companion PRE-condition (<c>RunEffectAgainst</c>), driven through the
    /// <c>RunSlotEffectAgainst</c> seam — the entry a future <c>SpatialHash</c>-threaded AoE period will fan out
    /// through, one call per matched entity. The guard must DISCRIMINATE, not blanket-skip: a live non-host target
    /// resolves normally (with the slot's caster still owning attribution), a dead one is refused before the executor
    /// is entered and tallied on <see cref="ModifierStore.SkippedPulseCount"/>.</para>
    ///
    /// <para>The last test is the golden-neutrality pin: across a rich production schedule (DoT, HoT, lifelong
    /// persistent, expire effect, a LETHAL period that kills its host mid-walk) the tally stays 0 — i.e. the new
    /// guard never fires on a shipped path, so it cannot have moved a checksum or a golden. Godot-free, Fixed-only.</para>
    /// </summary>
    public class PeriodTargetAliveGuardTests
    {
        private static readonly Fixed Dt = Fixed.Zero; // periods are tick-counted; the dt arg is unused

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store,
                        CombatEventQueue events, MatchStats stats) Wire()
        {
            var world  = new EntityWorld();
            var sys    = new ModifierSystem();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            var store  = new ModifierStore(world, sys, DamageTable.Default, events, stats);
            sys.AttachStore(store);
            return (world, sys, store, events, stats);
        }

        private static FixedVec3 V(int x) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero);

        private static int Unit(EntityWorld w, Faction f, int maxHp, int x = 0) =>
            w.Create(V(x), f, Fixed.FromInt(maxHp), Fixed.FromInt(4));

        /// <summary>A periodic modifier with a real matrix <see cref="DamageEffect"/> pulse (the lethal-capable leaf).</summary>
        private static Modifier DamagePeriodic(int id, int duration, int periodTicks, int damage) =>
            new Modifier(id, duration, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None,
                         periodEffect: new DamageEffect(Fixed.FromInt(damage), DamageType.Magic), periodTicks: periodTicks);

        /// <summary>A benign clamped drain — never destroys its host.</summary>
        private static Modifier DrainPeriodic(int id, int duration, int periodTicks, int hpPerPulse) =>
            new Modifier(id, duration, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None,
                         periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(hpPerPulse)), periodTicks: periodTicks);

        // ── 1. The guard DISCRIMINATES: a LIVE non-host target resolves, and the slot's caster keeps attribution ──

        [Fact]
        public void LiveNonHostTarget_ResolvesAgainstThatTarget_WithTheSlotsCasterAttribution()
        {
            var (world, _, store, _, stats) = Wire();
            int caster = Unit(world, Faction.Player2, maxHp: 100, x: 0);
            int host   = Unit(world, Faction.Player1, maxHp: 100, x: 5);
            int bystander = Unit(world, Faction.Player1, maxHp: 40, x: 9);

            // A real installed instance, so slot 0 carries the recorded caster id/faction the pulse resolves through.
            Assert.True(store.Apply(host, DamagePeriodic(950, duration: 100, periodTicks: 4, damage: 3),
                                    casterId: caster, casterFaction: Faction.Player2));

            store.RunSlotEffectAgainst(host, slotIndex: 0, targetId: bystander,
                                       new DamageEffect(Fixed.FromInt(12), DamageType.Magic));

            // The pulse landed on the FAN-OUT target, not on the host, and never on the caster.
            Assert.Equal(Fixed.FromInt(28).Raw, world.Health[bystander].Raw); // 40 − 12 magic (x1.00 vs Unarmored)
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[host].Raw);
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[caster].Raw);
            Assert.Equal(0, store.SkippedPulseCount); // RED if the guard blanket-skips every non-host target

            // Attribution rides the SLOT's caster, exactly like a direct-target period pulse.
            store.RunSlotEffectAgainst(host, slotIndex: 0, targetId: bystander,
                                       new DamageEffect(Fixed.FromInt(500), DamageType.Magic));
            Assert.False(world.IsAlive(bystander));
            Assert.Equal(caster, world.KillerOf[bystander]);
            Assert.Equal((int)Faction.Player2 - 1, world.KillerFactionOf[bystander]);
            Assert.Equal(1, stats.Kills(Faction.Player2));
            Assert.Equal(0, store.SkippedPulseCount);
        }

        // ── 2. A DEAD non-host target is refused before the executor runs — and tallied ──

        [Fact]
        public void DeadNonHostTarget_IsRefusedBeforeTheExecutor_AndTallied()
        {
            var (world, _, store, events, stats) = Wire();
            int host   = Unit(world, Faction.Player1, maxHp: 100, x: 0);
            int victim = Unit(world, Faction.Player2, maxHp: 10, x: 5);

            store.Apply(host, DamagePeriodic(951, duration: 100, periodTicks: 4, damage: 3), host, Faction.Player1);
            Assert.True(store.Apply(victim, DrainPeriodic(952, duration: 100, periodTicks: 1, hpPerPulse: -1),
                                    host, Faction.Player1));

            world.Destroy(victim);            // the corpse a fanned-out AoE period would still hold an id for
            Assert.False(world.IsAlive(victim));
            Assert.Equal(0, store.SkippedPulseCount);

            int eventsBefore = events.Count;
            store.RunSlotEffectAgainst(host, slotIndex: 0, targetId: victim,
                                       new DamageEffect(Fixed.FromInt(500), DamageType.Magic));

            Assert.Equal(1, store.SkippedPulseCount);          // refused, and OBSERVABLE (the DW-83 precedent)
            Assert.Equal(eventsBefore, events.Count);          // no death sequence fired over the freed slot
            Assert.Equal(0, stats.Kills(Faction.Player1));     // and no phantom kill was credited
            Assert.Equal(0, stats.Losses(Faction.Player2));
            Assert.Equal(0, store.CountAt(victim));            // ClearEntity's ring stays empty — nothing re-touched it

            // A modifier install through a dead-target pulse is refused for the same reason, and tallies again.
            store.RunSlotEffectAgainst(host, slotIndex: 0, targetId: victim,
                                       new ApplyModifierEffect(DrainPeriodic(953, 100, 1, -1)));
            Assert.Equal(2, store.SkippedPulseCount);
            Assert.Equal(0, store.CountAt(victim));

            // The HOST is untouched by either refusal — its own ring and schedule are intact.
            Assert.Equal(1, store.CountAt(host));
            Assert.Equal(951, store.ModifierIdAt(host, 0));
            Assert.Equal(Fixed.FromInt(100).Raw, world.Health[host].Raw);
        }

        // ── 3. A DEAD HOST is refused too — the pre-condition's other conjunct, and the one with real state teeth ──

        [Fact]
        public void DeadHost_IsRefused_EvenWhenTheTargetIsAlive()
        {
            var (world, _, store, _, stats) = Wire();
            int host      = Unit(world, Faction.Player1, maxHp: 100, x: 0);
            int bystander = Unit(world, Faction.Player2, maxHp: 40, x: 5);

            store.Apply(host, DamagePeriodic(954, duration: 100, periodTicks: 4, damage: 3), host, Faction.Player1);
            world.Destroy(host);
            Assert.False(world.IsAlive(host));
            Assert.Equal(0, store.CountAt(host)); // ClearEntity wiped the ring; the slot's caster fields are zeroed

            store.RunSlotEffectAgainst(host, slotIndex: 0, targetId: bystander,
                                       new DamageEffect(Fixed.FromInt(500), DamageType.Magic));

            // The substantive half, asserted FIRST: without the guard a CORPSE's cleared slot pulses a live entity —
            // the bystander dies here, credited to the zeroed slot's caster (entity 0 / Faction.Neutral), which is a
            // real kill nobody dealt. The leaves' own IsAlive guards cannot catch this: the TARGET is alive; it is the
            // HOST that no longer exists.
            Assert.True(world.IsAlive(bystander));
            Assert.Equal(Fixed.FromInt(40).Raw, world.Health[bystander].Raw);
            Assert.Equal(0, stats.Losses(Faction.Player2));
            Assert.Equal(1, store.SkippedPulseCount);
        }

        // ── 4. Out-of-range host / slot is a programmer error, not a skip: no throw, no tally, no mutation ──

        [Fact]
        public void OutOfRangeHostOrSlot_IsAHarmlessNoOp_AndNeverTallies()
        {
            var (world, _, store, _, _) = Wire();
            int host   = Unit(world, Faction.Player1, maxHp: 100, x: 0);
            int target = Unit(world, Faction.Player1, maxHp: 40, x: 5);
            store.Apply(host, DamagePeriodic(955, duration: 100, periodTicks: 4, damage: 3), host, Faction.Player1);

            var lethal = new DamageEffect(Fixed.FromInt(500), DamageType.Magic);
            store.RunSlotEffectAgainst(-1, 0, target, lethal);
            store.RunSlotEffectAgainst(EntityWorld.MAX_ENTITIES, 0, target, lethal);
            store.RunSlotEffectAgainst(host, -1, target, lethal);
            store.RunSlotEffectAgainst(host, EffectCaps.MaxModifiersPerEntity, target, lethal);

            Assert.Equal(0, store.SkippedPulseCount);                       // rejected upstream of the dead-end guard
            Assert.Equal(Fixed.FromInt(40).Raw, world.Health[target].Raw);  // and nothing was applied
            Assert.True(world.IsAlive(target));
        }

        // ── 5. A refused pulse leaves the walk intact: the host's own schedule keeps firing on later ticks ──

        [Fact]
        public void RefusedPulse_LeavesTheHostRingAndTheOngoingWalkIntact()
        {
            var (world, _, store, _, _) = Wire();
            int host   = Unit(world, Faction.Player1, maxHp: 100, x: 0);
            int victim = Unit(world, Faction.Player2, maxHp: 10, x: 5);

            store.Apply(host, DrainPeriodic(956, duration: 100, periodTicks: 2, hpPerPulse: -5), host, Faction.Player1);
            world.Destroy(victim);

            for (int t = 0; t < 3; t++)
            {
                store.RunSlotEffectAgainst(host, 0, victim, new DamageEffect(Fixed.FromInt(500), DamageType.Magic));
                store.Advance(world, Dt); // the real walk, interleaved with the refused fan-out pulses
            }

            Assert.Equal(3, store.SkippedPulseCount);
            Assert.True(world.IsAlive(host));
            Assert.Equal(1, store.CountAt(host));
            Assert.Equal(956, store.ModifierIdAt(host, 0));
            Assert.Equal(Fixed.FromInt(95).Raw, world.Health[host].Raw); // exactly one boundary in 3 ticks (period 2)
            Assert.Equal(1, store.TicksUntilPeriodAt(host, 0));          // schedule un-perturbed by the refusals
        }

        // ── 6. Clear() resets the tally — same per-match contract as the DW-83 refusal tally ──

        [Fact]
        public void Clear_ResetsTheSkipTally()
        {
            var (world, _, store, _, _) = Wire();
            int host   = Unit(world, Faction.Player1, maxHp: 100, x: 0);
            int victim = Unit(world, Faction.Player1, maxHp: 10, x: 5);
            store.Apply(host, DrainPeriodic(957, duration: 100, periodTicks: 2, hpPerPulse: -1), host, Faction.Player1);
            world.Destroy(victim);

            store.RunSlotEffectAgainst(host, 0, victim, new DamageEffect(Fixed.FromInt(5), DamageType.Magic));
            Assert.Equal(1, store.SkippedPulseCount);

            store.Clear();
            Assert.Equal(0, store.SkippedPulseCount);
        }

        // ── 7. GOLDEN-NEUTRALITY PIN: no shipped path trips the guard, and the fold is bit-stable across runs ──

        [Fact]
        public void ShippedPeriodPaths_NeverTripTheGuard_AndTheFoldStaysDeterministic()
        {
            var a = RunRichSchedule(out int skipsA);
            var b = RunRichSchedule(out int skipsB);

            // The whole golden-neutrality claim in one assertion: across a schedule that exercises every production
            // RunEffect call site — a persistent InitialEffect, DoT/HoT period pulses, a lifelong re-arm, a lethal
            // period that kills its host mid-walk, and a lethal-capable expire effect — the new pre-condition NEVER
            // fires. It cannot have changed a single pulse, so no checksum and no golden can have moved.
            Assert.Equal(0, skipsA);
            Assert.Equal(0, skipsB);

            Assert.Equal(40, a.Count);
            Assert.True(a.SequenceEqual(b), "Two identical schedules diverged — nondeterminism on the guarded pulse path.");
            Assert.True(a.Distinct().Count() > 1, "Checksum sequence is constant — the schedule is not exercising the store (vacuous).");
        }

        /// <summary>
        /// 40 ticks over four hosts: a cross-faction DoT that KILLS its host mid-<see cref="ModifierStore.Advance"/>
        /// (12 magic every 4 ticks vs 30 HP), a long benign drain, a <see cref="PersistentEffect"/> HoT with both an
        /// InitialEffect and a lethal-capable ExpireEffect, and a LIFELONG persistent that re-arms. Records the
        /// per-tick <see cref="SimChecksum"/> and reports the store's DW-662 skip tally.
        /// </summary>
        private static List<uint> RunRichSchedule(out int skippedPulses)
        {
            var world = new EntityWorld();
            var sys   = new ModifierSystem();
            var store = new ModifierStore(world, sys, DamageTable.Default, new CombatEventQueue(), new MatchStats());
            sys.AttachStore(store);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry  = new FactionRegistry(2);

            int doomed    = world.Create(V(0),  Faction.Player1, Fixed.FromInt(30),  Fixed.FromInt(4));
            int drained   = world.Create(V(5),  Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(4));
            int expiring  = world.Create(V(10), Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(4));
            int lifelong  = world.Create(V(15), Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(4));

            var hashes = new List<uint>(40);
            for (int t = 0; t < 40; t++)
            {
                if (t == 0)
                {
                    store.Apply(doomed,  DamagePeriodic(960, duration: 100, periodTicks: 4, damage: 12), drained, Faction.Player2);
                    store.Apply(drained, DrainPeriodic(961, duration: 100, periodTicks: 3, hpPerPulse: -2), drained, Faction.Player1);
                    store.InstallPersistent(expiring, new PersistentEffect(
                                                initialEffect: new DirectHpDeltaEffect(Fixed.FromInt(-20)),
                                                periodEffect:  new DirectHpDeltaEffect(Fixed.FromInt(-3)),
                                                expireEffect:  new DamageEffect(Fixed.FromInt(9), DamageType.Magic),
                                                periodTicks: 2, periodCount: 4),
                                            casterId: expiring, casterFaction: Faction.Player1);
                    store.InstallPersistent(lifelong, new PersistentEffect(
                                                initialEffect: null,
                                                periodEffect:  new HealEffect(Fixed.FromInt(1)),
                                                expireEffect:  null,
                                                periodTicks: 5, periodCount: 2, lifelong: true),
                                            casterId: lifelong, casterFaction: Faction.Player1);
                }
                sys.Tick(world, Dt);
                hashes.Add(SimChecksum.Compute(world, buildings, resources, registry, store));
            }

            skippedPulses = store.SkippedPulseCount;
            return hashes;
        }
    }
}
