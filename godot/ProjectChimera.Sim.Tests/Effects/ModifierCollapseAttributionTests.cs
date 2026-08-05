#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-490 + DW-492 — the DW-325/DW-491 ceiling-collapse death is a FULL death, on EVERY path that can cause it.
    ///
    /// <para><b>DW-490 (attribution).</b> The collapse kill used to be hardcoded
    /// <c>KillEntity(world, id, Faction.Neutral, events, stats)</c> — killer Neutral, the <see cref="DeathFeed"/>
    /// argument every other lethal path passes simply omitted. A creator CAN author a lethal −MaxHealth debuff whose
    /// caster is a real player, and that kill was then the ONLY one in the game invisible to scoring and to hero XP.
    /// The instance's own recorded caster (<c>_casterId</c>/<c>_casterFaction</c> — the same pair its period pulses
    /// resolve with) is now threaded through <c>ApplyStatDeltas</c>, and the victim is recorded into the shared feed.</para>
    ///
    /// <para><b>DW-492 (coverage).</b> The death only ever ran on the apply/stack/remove paths through
    /// <c>ApplyStatDeltas</c>. <c>ModifierStore.RestoreSlot</c> (SP load) re-accumulates every saved bonus without
    /// recomputing, and any bonus dirtied outside the store lands in <c>ModifierSystem.Tick</c>'s catch-all recompute —
    /// neither carried the check, so a loaded or externally-dirtied unit could stand at ceiling 0: the exact zombie
    /// DW-325 advertises as impossible, and a divergence between a freshly-applied and a loaded match. The catch-all
    /// now reconciles both through <c>ModifierStore.RaiseExternalCeilingCollapse</c> — at the FIRST tick after a load,
    /// never per restored slot (which would kill a host before its offsetting +MaxHealth slot is back).</para>
    ///
    /// Every test is RED against the pre-fix code. Godot-free: bare <see cref="EntityWorld"/>s,
    /// <see cref="Fixed.FromInt"/> only, no float anywhere.
    /// </summary>
    public class ModifierCollapseAttributionTests
    {
        private sealed class Rig
        {
            public EntityWorld World = null!;
            public ModifierSystem Sys = null!;
            public ModifierStore Store = null!;
            public CombatEventQueue Events = null!;
            public MatchStats Stats = null!;
            public DeathFeed Deaths = null!;
        }

        /// <summary>A fully-wired store: the same event / stats / death sinks the live SimulationHost threads in.</summary>
        private static Rig Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var events = new CombatEventQueue();
            var stats = new MatchStats();
            var deaths = new DeathFeed();
            var store = new ModifierStore(world, sys, DamageTable.Default, events, stats, log: null, deaths: deaths);
            sys.AttachStore(store);
            return new Rig { World = world, Sys = sys, Store = store, Events = events, Stats = stats, Deaths = deaths };
        }

        /// <summary>A pure MaxHealth modifier (no period, no status).</summary>
        private static Modifier MaxHpMod(int id, Fixed maxHealthDelta, int duration = 5) =>
            new Modifier(id, duration, StackRule.Refresh, 1, maxHealthDelta, Fixed.Zero, Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        // ────────────────────────────── DW-490: the collapse credits its real caster ──────────────────────────────

        [Fact]
        public void AbilityDrivenCollapse_CreditsTheCastersFaction_NotNeutral()
        {
            // The content-gated case DW-490 names: a Player2 ability whose −MaxHealth debuff collapses a Player1 unit's
            // ceiling. Pre-fix the killer was the hardcoded Faction.Neutral, so RecordKill counted the victim's LOSS but
            // credited no kill (killer index 0 is skipped) and the killer-attribution SoA / DeathLog read "nobody".
            var r = Wire();
            int victim = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int caster = r.World.Create(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero),
                                        Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(4));

            r.Store.Apply(victim, MaxHpMod(1, Fixed.FromInt(-100)), caster, Faction.Player2);

            Assert.False(r.World.IsAlive(victim));                 // the collapse really killed (non-vacuous)
            Assert.Equal(1, r.Stats.Kills(Faction.Player2));       // RED pre-fix: Neutral killer credited nobody
            Assert.Equal(1, r.Stats.Losses(Faction.Player1));      // the victim's loss was always counted
            Assert.Equal(0, r.Stats.Kills(Faction.Player1));       // and the victim never credits itself
            Assert.Equal((int)Faction.Player2 - 1, r.World.KillerFactionOf[victim]); // RED pre-fix: −1 (Neutral)
            Assert.Equal(caster, r.World.KillerOf[victim]);        // RED pre-fix: −1 (attackerId defaulted)
            Assert.Equal(1, r.World.DeathLog.Count);
            Assert.Equal(victim, r.World.DeathLog.VictimAt(0));
            Assert.Equal(caster, r.World.DeathLog.KillerAt(0));                      // RED pre-fix: −1
            Assert.Equal((int)Faction.Player2 - 1, r.World.DeathLog.KillerSlotAt(0)); // RED pre-fix: −1
        }

        [Fact]
        public void AbilityDrivenCollapse_RecordsTheVictimInTheDeathFeed_SoHeroesEarnItsBounty()
        {
            // The other half of DW-490: the DeathFeed argument was omitted entirely, so HeroXpSystem — which credits
            // hostile heroes in range by draining exactly this feed — never saw the death. Hero XP/Level (and the growth
            // modifier stacks they drive) ARE folded into SimChecksum, so a silently unfed death is a real outcome loss.
            var r = Wire();
            var pos = new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(7));
            int victim = r.World.Create(pos, Faction.Player1, Fixed.FromInt(60), Fixed.FromInt(4));
            r.World.XpBounty[victim] = Fixed.FromInt(25);
            int caster = r.World.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(4));

            r.Store.Apply(victim, MaxHpMod(2, Fixed.FromInt(-60)), caster, Faction.Player2);

            Assert.False(r.World.IsAlive(victim));
            Assert.Equal(1, r.Deaths.Count);                                  // RED pre-fix: 0 — the feed was never passed
            DeathRecord rec = r.Deaths.Get(0);
            Assert.Equal(Faction.Player1, rec.Faction);                       // the VICTIM's faction (hostility test)
            Assert.Equal(Fixed.FromInt(25).Raw, rec.Bounty.Raw);              // the victim's own bounty — no new policy
            Assert.Equal(pos.X.Raw, rec.Position.X.Raw);                      // snapshotted pre-Destroy
            Assert.Equal(pos.Z.Raw, rec.Position.Z.Raw);
        }

        [Fact]
        public void CollapseOnExpiryRevert_CreditsTheGrantingInstancesCaster()
        {
            // The removal arm. Attribution follows the INSTANCE whose stat change collapsed the ceiling, read from its
            // slot BEFORE the swap-compact: here the expiring +100 grant (cast by Player2), not the co-resident debuff.
            var r = Wire();
            int victim = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            int granter = r.World.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(4));

            // +100 from Player2 expiring on the first Advance, and a self-cast −100: net ceiling 100 while both live.
            r.Store.Apply(victim, MaxHpMod(3, Fixed.FromInt(100), duration: 1), granter, Faction.Player2);
            r.Store.Apply(victim, MaxHpMod(4, Fixed.FromInt(-100), duration: 9), victim, Faction.Player1);
            Assert.True(r.World.IsAlive(victim));
            Assert.Equal(Fixed.FromInt(100).Raw, r.World.EffectiveMaxHealth[victim].Raw);

            r.Sys.Tick(r.World, Fixed.Zero); // the grant expires → revert −100 → ceiling 100 → 0

            Assert.False(r.World.IsAlive(victim));
            Assert.Equal(1, r.Stats.Kills(Faction.Player2));   // RED pre-fix: 0 (Neutral)
            Assert.Equal(granter, r.World.KillerOf[victim]);   // RED pre-fix: −1
            Assert.Equal(1, r.Deaths.Count);                   // RED pre-fix: 0
        }

        [Fact]
        public void RulesDrivenCollapse_WithNoCaster_StaysAttackerLess_ButStillFeedsTheXpRuntime()
        {
            // Backwards compatibility: when the instance genuinely has no caster the recorded pair IS
            // (−1, Faction.Neutral) — the slot default ClearSlotFields writes — so the attacker-less rules death the
            // DW-325 spec described is unchanged. Only the DeathFeed push is new.
            var r = Wire();
            int victim = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(4));

            r.Store.Apply(victim, MaxHpMod(5, Fixed.FromInt(-40)), casterId: -1, casterFaction: Faction.Neutral);

            Assert.False(r.World.IsAlive(victim));
            Assert.Equal(0, r.Stats.Kills(Faction.Player1));
            Assert.Equal(0, r.Stats.Kills(Faction.Player2));
            Assert.Equal(1, r.Stats.Losses(Faction.Player1));
            Assert.Equal(-1, r.World.KillerFactionOf[victim]);
            Assert.Equal(-1, r.World.KillerOf[victim]);
            Assert.Equal(1, r.Deaths.Count);                   // RED pre-fix: 0
        }

        // ─────────────── DW-492: the same rule on the external-recompute + SP-load paths ───────────────

        [Fact]
        public void ExternallyDirtiedCeilingCollapse_KillsTheZombieAtTheNextTick()
        {
            // A MaxHealth bonus pushed through the 2.2a accumulator seam WITHOUT the store (the "dirtied outside the
            // store" case). Pre-fix ModifierSystem.Tick recomputed the ceiling to 0 and left the unit standing at 0 HP.
            var r = Wire();
            int id = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            r.Sys.AccumulateBonus(id, Fixed.Zero, Fixed.FromInt(-100), Fixed.Zero);

            // Non-vacuous precondition: accumulating alone neither recomputes nor kills.
            Assert.True(r.World.IsAlive(id));
            Assert.Equal(Fixed.FromInt(100).Raw, r.World.EffectiveMaxHealth[id].Raw);

            r.Sys.Tick(r.World, Fixed.Zero);

            Assert.False(r.World.IsAlive(id));               // RED pre-fix: alive at ceiling 0 — the zombie
            Assert.Equal(1, r.Stats.Losses(Faction.Player1));
            Assert.Equal(1, r.Deaths.Count);
        }

        [Fact]
        public void ExternalCeilingDrop_ClampsHealthUnderTheNewCeiling()
        {
            // The clamp half of the catch-all: ApplyStatDeltas re-clamps Health into [0, EffectiveMaxHealth] on every
            // MaxHealth change, but the Tick recompute did not — so an externally-lowered ceiling left phantom HP above
            // it. A non-collapsing drop must clamp and must NOT kill.
            var r = Wire();
            int id = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            r.Sys.AccumulateBonus(id, Fixed.Zero, Fixed.FromInt(-60), Fixed.Zero);

            r.Sys.Tick(r.World, Fixed.Zero);

            Assert.True(r.World.IsAlive(id));                                   // 100 → 40 is not a collapse
            Assert.Equal(Fixed.FromInt(40).Raw, r.World.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(40).Raw, r.World.Health[id].Raw);        // RED pre-fix: still 100, above the ceiling
            Assert.Equal(0, r.Deaths.Count);
        }

        [Fact]
        public void HostLegitimatelyAtCeilingZero_SurvivesTheExternalRecompute()
        {
            // DW-491's ruling, re-pinned on the NEW path: ceiling 0 at rest is a legal state (a base-0 / item-sustained
            // host), so the catch-all must test the same downward TRANSITION the apply path does, never an absolute
            // `== 0` reading. A further negative bonus that leaves the floored ceiling at 0 is 0 → 0: nothing collapsed.
            var r = Wire();
            int id = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.Zero, Fixed.FromInt(4));
            Assert.Equal(Fixed.Zero.Raw, r.World.EffectiveMaxHealth[id].Raw);

            r.Sys.AccumulateBonus(id, Fixed.Zero, Fixed.FromInt(-50), Fixed.Zero);
            r.Sys.Tick(r.World, Fixed.Zero);

            Assert.True(r.World.IsAlive(id));
            Assert.Equal(Fixed.Zero.Raw, r.World.EffectiveMaxHealth[id].Raw);
            Assert.Equal(0, r.Deaths.Count);
        }

        [Fact]
        public void LoadRestoredRingThatFloorsTheCeiling_KillsAtTheFirstResumedTick()
        {
            // The SP-load half of DW-492. RestoreSlot re-accumulates a saved −MaxHealth instance without recomputing;
            // pre-fix nothing downstream re-checked, so the loaded match diverged from the freshly-applied one — the
            // same content that kills on apply produced a living 0-ceiling unit on load.
            var r = Wire();
            int id = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            var saved = MaxHpMod(6, Fixed.FromInt(-100), duration: 9);

            r.Store.RestoreSlot(id, slot: 0, modifierId: saved.Id, remainingTicks: 9, ticksUntilPeriod: 0,
                                periodsRemaining: 0, stackCount: 1, casterId: -1, casterFaction: Faction.Neutral,
                                modifier: saved, persistent: null);
            r.Store.SetCount(id, 1);

            // The restore itself stays deliberately non-lethal (a partially-rebuilt ring is not a collapse).
            Assert.True(r.World.IsAlive(id));

            r.Sys.Tick(r.World, Fixed.Zero);

            Assert.False(r.World.IsAlive(id));               // RED pre-fix: a loaded zombie at ceiling 0
            Assert.Equal(1, r.Stats.Losses(Faction.Player1));
            Assert.Equal(1, r.Deaths.Count);
        }

        [Fact]
        public void LoadRestoredRingWhoseSlotsNetPositive_SurvivesEvenThoughOneSlotAloneWouldFloorIt()
        {
            // The teeth for WHERE the check runs. Restoring slot 0 (−150) alone floors this host's ceiling at 0; only
            // slot 1 (+150) brings it back. A per-slot check inside RestoreSlot would therefore destroy a unit the save
            // shows alive — which is exactly why the reconciliation is deferred to the first tick after the whole ring
            // (and the entity overlay) is restored.
            var r = Wire();
            int id = r.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(4));
            var debuff = MaxHpMod(7, Fixed.FromInt(-150), duration: 9);
            var buff = MaxHpMod(8, Fixed.FromInt(150), duration: 9);

            r.Store.RestoreSlot(id, 0, debuff.Id, 9, 0, 0, 1, -1, Faction.Neutral, debuff, null);
            Assert.True(r.World.IsAlive(id)); // mid-restore: the ring is incomplete, nothing may die
            r.Store.RestoreSlot(id, 1, buff.Id, 9, 0, 0, 1, -1, Faction.Neutral, buff, null);
            r.Store.SetCount(id, 2);

            r.Sys.Tick(r.World, Fixed.Zero);

            Assert.True(r.World.IsAlive(id));
            Assert.Equal(Fixed.FromInt(100).Raw, r.World.EffectiveMaxHealth[id].Raw); // net 0 bonus → the base ceiling
            Assert.Equal(2, r.Store.CountAt(id));
            Assert.Equal(0, r.Deaths.Count);
        }
    }
}
