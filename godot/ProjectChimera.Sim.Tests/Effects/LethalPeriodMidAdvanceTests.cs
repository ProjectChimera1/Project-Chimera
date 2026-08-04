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
    /// DW-267 — the LETHAL PERIOD path through <see cref="ModifierStore.Advance"/>. A modifier/persistent whose
    /// <c>period_effect</c> is a <see cref="DamageEffect"/> (authorable content — <c>AbilityValidator</c> accepts it)
    /// can KILL its own host mid-pulse: <see cref="DamageResolver.Apply"/> runs the full death sequence, which fires
    /// <see cref="EntityWorld.OnDestroy"/> → <see cref="ModifierStore.ClearEntity"/> and wipes the very slots the walk
    /// is standing on. Every shipped period was non-lethal (DirectHpDelta/Heal clamp at 0 and never destroy), so the
    /// three defensive bails that make this safe were UNEXERCISED — <c>SelfLethalCastTests</c> covers cast-time
    /// self-death only, never a period. These are their teeth:
    ///
    /// <list type="number">
    /// <item><b><c>Advance</c> step 1</b> — <c>if (!_world.IsAlive(i)) break;</c> right after the period
    ///   <c>RunEffect</c>. Without it the walk writes <c>_ticksUntilPeriod</c>/<c>_periodsRemaining</c> onto a cleared
    ///   slot, then expires it and <c>CompactSlot</c>s a ring whose <c>_count</c> is already 0 — indexing
    ///   <c>base − 1</c> (an <c>IndexOutOfRangeException</c> at host id 0; a <c>_count = −1</c> that lands the NEXT
    ///   install in the previous entity's ring at any higher id).</item>
    /// <item><b>It is a <c>break</c>, not a <c>return</c></b> — only the dead host's slot loop ends; every
    ///   higher-id entity must still pulse on that same tick.</item>
    /// <item><b><c>RemoveSlot</c></b> — the sibling bail after a lethal <c>expire_effect</c>, on the same walk.</item>
    /// </list>
    ///
    /// Attribution rides the slot's stored caster (<c>_casterId</c>/<c>_casterFaction</c>), so a DoT kill credits the
    /// CASTER exactly like a hitscan kill. Driven straight through <see cref="ModifierStore.Advance"/> (one call ==
    /// one tick). Godot-free, Fixed-only, no golden touched (a lethal period is authorable content, not shipped
    /// content — no existing scenario changes).
    /// </summary>
    public class LethalPeriodMidAdvanceTests
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

        /// <summary>A modifier whose period is a real matrix <see cref="DamageEffect"/> — the lethal-capable leaf.</summary>
        private static Modifier DamagePeriodic(int id, int duration, int periodTicks, int damage,
                                               DamageType type = DamageType.Magic,
                                               StatusFlags status = StatusFlags.None, int atk = 0) =>
            new Modifier(id, duration, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.FromInt(atk), moveSpeedDelta: Fixed.Zero,
                         status: status,
                         periodEffect: new DamageEffect(Fixed.FromInt(damage), type), periodTicks: periodTicks);

        /// <summary>A benign clamped drain (the 2.2b period vocabulary) — never destroys its host.</summary>
        private static Modifier DrainPeriodic(int id, int duration, int periodTicks, int hpPerPulse) =>
            new Modifier(id, duration, StackRule.Refresh, maxStacks: 1,
                         maxHealthDelta: Fixed.Zero, attackDamageDelta: Fixed.Zero, moveSpeedDelta: Fixed.Zero,
                         status: StatusFlags.None,
                         periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(hpPerPulse)), periodTicks: periodTicks);

        // ── 1. The kill itself: the host dies inside Advance and leaves NO residual slot ──

        [Fact]
        public void LethalPeriodPulse_KillsTheHostMidAdvance_LeavingNoResidualSlot()
        {
            var (world, _, store, _, _) = Wire();
            int host = Unit(world, Faction.Player1, maxHp: 10);
            Assert.Equal(0, host); // ring base 0 — the id at which the un-guarded CompactSlot indexes −1 and THROWS

            Assert.True(store.Apply(host, DamagePeriodic(900, duration: 100, periodTicks: 2, damage: 25),
                                    casterId: host, casterFaction: Faction.Player1));

            store.Advance(world, Dt); // tick 1 — no boundary yet
            Assert.True(world.IsAlive(host));
            Assert.Equal(Fixed.FromInt(10).Raw, world.Health[host].Raw);

            store.Advance(world, Dt); // tick 2 — the lethal pulse fires MID-walk

            Assert.False(world.IsAlive(host));                          // 25 magic vs 10 HP ⇒ the death sequence ran
            Assert.Equal(0, store.CountAt(host));                       // ClearEntity emptied the ring — and the walk bailed
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[host]);

            for (int t = 0; t < 5; t++) store.Advance(world, Dt);       // and the walk stays safe on every later tick
            Assert.Equal(0, store.CountAt(host));
        }

        // ── 2. The kill takes the host's SIBLING slots with it; none of them is processed after the bail ──

        [Fact]
        public void LethalPeriodPulse_TakesTheHostsSiblingSlotsWithIt_AndTheyNeverPulseAgain()
        {
            var (world, _, store, _, _) = Wire();
            int host = Unit(world, Faction.Player1, maxHp: 50);

            // slot 0 — a benign 1 HP/tick drain; slot 1 — the lethal DamageEffect, first boundary at tick 3.
            Assert.True(store.Apply(host, DrainPeriodic(901, duration: 100, periodTicks: 1, hpPerPulse: -1),
                                    host, Faction.Player1));
            Assert.True(store.Apply(host, DamagePeriodic(902, duration: 100, periodTicks: 3, damage: 60,
                                                         status: StatusFlags.Stunned), host, Faction.Player1));
            Assert.Equal(2, store.CountAt(host));
            Assert.Equal(StatusFlags.Stunned, world.StatusFlagsOf[host]);

            store.Advance(world, Dt); // 49
            store.Advance(world, Dt); // 48
            Assert.True(world.IsAlive(host));
            Assert.Equal(Fixed.FromInt(48).Raw, world.Health[host].Raw);

            store.Advance(world, Dt); // slot 0 drains to 47, then slot 1's 60 magic kills mid-walk

            Assert.False(world.IsAlive(host));
            Assert.Equal(0, store.CountAt(host));                       // BOTH slots gone, not just the killing one
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[host]);

            Fixed hpAtDeath = world.Health[host];
            for (int t = 0; t < 10; t++) store.Advance(world, Dt);
            Assert.Equal(hpAtDeath.Raw, world.Health[host].Raw);        // the sibling drain never pulses on a corpse
        }

        // ── 3. Attribution — the kill credits the modifier's CASTER, not the host ──

        [Fact]
        public void LethalPeriodPulse_AttributesTheKillToTheModifiersCaster()
        {
            var (world, _, store, events, stats) = Wire();
            int caster = Unit(world, Faction.Player2, maxHp: 100, x: 0);
            int host   = Unit(world, Faction.Player1, maxHp: 10, x: 5);
            Assert.Equal(1, host); // NOT ring base 0 — the id at which an un-guarded walk corrupts _count instead of throwing

            store.Apply(host, DamagePeriodic(903, duration: 100, periodTicks: 1, damage: 25),
                        casterId: caster, casterFaction: Faction.Player2);

            store.Advance(world, Dt); // tick 1 — lethal pulse

            Assert.False(world.IsAlive(host));
            Assert.True(world.IsAlive(caster));                                  // the caster is untouched by its own DoT
            Assert.Equal(0, store.CountAt(host));

            // The slot's stored caster id/faction ARE the damage attribution (EffectContext is built from them).
            Assert.Equal(caster, world.KillerOf[host]);
            Assert.Equal((int)Faction.Player2 - 1, world.KillerFactionOf[host]);
            Assert.Equal(1, stats.Kills(Faction.Player2));
            Assert.Equal(1, stats.Losses(Faction.Player1));
            Assert.Equal(0, stats.Kills(Faction.Player1));                       // never self-credited

            // The SHARED death sequence ran exactly once (one UnitKilled, one death-log record).
            int killedEvents = 0;
            for (int i = 0; i < events.Count; i++)
                if (events.Get(i).Type == CombatEventType.UnitKilled) killedEvents++;
            Assert.Equal(1, killedEvents);

            Assert.Equal(1, world.DeathLog.Count);
            Assert.Equal(host, world.DeathLog.VictimAt(0));
            Assert.Equal((int)Faction.Player1 - 1, world.DeathLog.VictimSlotAt(0));
            Assert.Equal(caster, world.DeathLog.KillerAt(0));
            Assert.Equal((int)Faction.Player2 - 1, world.DeathLog.KillerSlotAt(0));
        }

        // ── 4. The bail is a BREAK, not a RETURN — higher-id entities still pulse on the killing tick ──

        [Fact]
        public void LethalPeriodPulse_DoesNotAbortTheWalk_HigherIdsStillPulseThisTick()
        {
            var (world, _, store, _, _) = Wire();
            int doomed   = Unit(world, Faction.Player1, maxHp: 10, x: 0);  // id 0 — pulses (and dies) FIRST
            int survivor = Unit(world, Faction.Player1, maxHp: 100, x: 5); // id 1 — must still pulse the SAME tick
            Assert.True(doomed < survivor);

            store.Apply(doomed,   DamagePeriodic(904, duration: 100, periodTicks: 1, damage: 25), doomed,   Faction.Player1);
            store.Apply(survivor, DrainPeriodic(905, duration: 100, periodTicks: 1, hpPerPulse: -7), survivor, Faction.Player1);

            store.Advance(world, Dt);

            Assert.False(world.IsAlive(doomed));
            Assert.True(world.IsAlive(survivor));
            // RED if the mid-Advance death aborted the WALK instead of just the dead host's slot loop.
            Assert.Equal(Fixed.FromInt(93).Raw, world.Health[survivor].Raw);
            Assert.Equal(1, store.CountAt(survivor));
        }

        // ── 5. The freed ring is EXACTLY empty — a recycled slot installs into its own slot 0, not the neighbour's ──

        [Fact]
        public void LethalPeriodPulse_LeavesTheRingCleanForARecycledSlot()
        {
            var (world, _, store, _, _) = Wire();
            int neighbour = Unit(world, Faction.Player1, maxHp: 100, x: 0); // keeps the doomed host off ring base 0
            int host      = Unit(world, Faction.Player1, maxHp: 10, x: 5);
            world.BaseAttackDamage[host] = Fixed.FromInt(10);

            store.Apply(neighbour, DrainPeriodic(906, duration: 500, periodTicks: 50, hpPerPulse: -1), neighbour, Faction.Player1);
            store.Apply(host, DamagePeriodic(907, duration: 100, periodTicks: 1, damage: 25, atk: 5), host, Faction.Player1);
            Assert.Equal(Fixed.FromInt(15).Raw, world.EffectiveAttackDamage[host].Raw); // 10 + 5, live from install

            store.Advance(world, Dt);
            Assert.False(world.IsAlive(host));
            // EXACTLY 0 — a −1 count here would land the next install at `base − 1`, i.e. in the NEIGHBOUR's ring.
            Assert.Equal(0, store.CountAt(host));
            Assert.Equal(1, store.CountAt(neighbour));

            int reborn = Unit(world, Faction.Player1, maxHp: 40, x: 5);
            Assert.Equal(host, reborn);                     // the freed slot was recycled
            Assert.Equal(0, store.CountAt(reborn));
            world.BaseAttackDamage[reborn] = Fixed.FromInt(10);

            Assert.True(store.Apply(reborn, DrainPeriodic(908, duration: 100, periodTicks: 10, hpPerPulse: -1),
                                    reborn, Faction.Player1));
            Assert.Equal(1, store.CountAt(reborn));         // landed in ITS ring at slot 0
            Assert.Equal(908, store.ModifierIdAt(reborn, 0));
            Assert.Equal(1, store.CountAt(neighbour));      // the neighbour's ring is untouched
            // The dead host's +5 accumulator was zeroed by ClearEntity — the recycled slot inherits no bonus.
            Assert.Equal(Fixed.FromInt(10).Raw, world.EffectiveAttackDamage[reborn].Raw);
        }

        // ── 6. The guard does not over-trigger: a NON-lethal DamageEffect period keeps its schedule ──

        [Fact]
        public void NonLethalPeriodDamage_KeepsItsSchedule_AndResolvesThroughTheDamageMatrix()
        {
            var (world, _, store, _, _) = Wire();
            int host = Unit(world, Faction.Player1, maxHp: 100);
            world.ArmorTypeOf[host] = ArmorType.Heavy;    // Normal vs Heavy = x0.50
            world.BaseArmor[host]   = Fixed.FromInt(2);   // Story 2.6 flat post-matrix subtraction

            store.Apply(host, DamagePeriodic(909, duration: 100, periodTicks: 5, damage: 20, type: DamageType.Normal),
                        host, Faction.Player1);
            Assert.Equal(Fixed.FromInt(2).Raw, world.EffectiveArmor[host].Raw); // the install recompute picked the armor up

            for (int t = 1; t <= 15; t++) store.Advance(world, Dt); // boundaries at 5 / 10 / 15

            // 20 x 0.50 − 2 = 8 per pulse: the period rides the SINGLE damage path, not a flat DirectHpDelta.
            Assert.True(world.IsAlive(host));
            Assert.Equal(Fixed.FromInt(100 - 24).Raw, world.Health[host].Raw);
            Assert.Equal(1, store.CountAt(host));
        }

        // ── 7. The sibling bail: a lethal EXPIRE effect kills the host inside RemoveSlot, on the same walk ──

        [Fact]
        public void LethalExpireEffect_KillsTheHostMidAdvance_WithoutCorruptingTheRing()
        {
            var (world, _, store, _, stats) = Wire();
            int host = Unit(world, Faction.Player1, maxHp: 100);
            Assert.Equal(0, host); // ring base 0 — an un-guarded RemoveSlot compacts at index −1 and THROWS

            store.InstallPersistent(host, new PersistentEffect(
                                        initialEffect: null,
                                        periodEffect: new DirectHpDeltaEffect(Fixed.FromInt(-1)),
                                        expireEffect: new DamageEffect(Fixed.FromInt(500), DamageType.Magic),
                                        periodTicks: 1, periodCount: 3),
                                    casterId: host, casterFaction: Faction.Player1);

            store.Advance(world, Dt); // 99
            store.Advance(world, Dt); // 98
            Assert.True(world.IsAlive(host));

            store.Advance(world, Dt); // 97, budget drains → RemoveSlot runs the lethal expire effect

            Assert.False(world.IsAlive(host));
            Assert.Equal(0, store.CountAt(host));
            Assert.Equal(1, stats.Losses(Faction.Player1));

            for (int t = 0; t < 5; t++) store.Advance(world, Dt);
            Assert.Equal(0, store.CountAt(host));
        }

        // ── 8. Determinism — a schedule that KILLS mid-Advance still hashes identically across two runs ──

        [Fact]
        public void LethalPeriodSchedule_TwoIdenticalRuns_ProduceIdenticalChecksumSequences()
        {
            var a = RunLethalSchedule();
            var b = RunLethalSchedule();
            Assert.Equal(30, a.Count);
            Assert.True(a.SequenceEqual(b), "Two identical lethal-period schedules diverged — nondeterminism on the mid-Advance death path.");
            Assert.True(a.Distinct().Count() > 1, "Checksum sequence is constant — the schedule is not exercising the store (vacuous).");
        }

        /// <summary>
        /// 30 ticks in which entity <c>a</c> is killed mid-<see cref="ModifierStore.Advance"/> by its own
        /// cross-faction DoT (12 magic every 4 ticks against 30 HP ⇒ dead on the third pulse, tick 12) while
        /// entity <c>b</c> keeps draining. Records the per-tick <see cref="SimChecksum"/>.
        /// </summary>
        private static List<uint> RunLethalSchedule()
        {
            var world = new EntityWorld();
            var sys   = new ModifierSystem();
            var store = new ModifierStore(world, sys, DamageTable.Default, new CombatEventQueue(), new MatchStats());
            sys.AttachStore(store);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry  = new FactionRegistry(2);

            int a = world.Create(V(0), Faction.Player1, Fixed.FromInt(30),  Fixed.FromInt(4));
            int b = world.Create(V(5), Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(4));

            var hashes = new List<uint>(30);
            for (int t = 0; t < 30; t++)
            {
                if (t == 0)
                {
                    store.Apply(a, DamagePeriodic(910, duration: 100, periodTicks: 4, damage: 12), b, Faction.Player2);
                    store.Apply(b, DrainPeriodic(911, duration: 100, periodTicks: 3, hpPerPulse: -2), b, Faction.Player1);
                }
                sys.Tick(world, Dt);
                hashes.Add(SimChecksum.Compute(world, buildings, resources, registry, store));
            }
            return hashes;
        }
    }
}
