#nullable enable
using ProjectChimera.Combat;            // DamageResolver, CombatEventQueue, CombatEventType, DamageEffect's DamageType
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, MatchStats
using ProjectChimera.Core.Definitions;  // AbilityDefinition
using ProjectChimera.Effects;           // StatusFlags, Modifier, StackRule, ModifierSystem/Store, DamageEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-620 (recorded decision 2026-08-05 — <b>"Invulnerable = DEATH-immunity, not merely damage-immunity"</b>).
    ///
    /// <para>DW-266 wired <see cref="StatusFlags.Invulnerable"/> to <see cref="DamageResolver.Apply"/>, the single
    /// entity-DAMAGE path, which stops every damage source. But two callers reach the death sequence WITHOUT going
    /// through Apply, so an invulnerable unit could still be killed outright:</para>
    /// <list type="bullet">
    /// <item><c>ModifierStore</c>'s DW-325 ceiling collapse — a net-negative −MaxHealth debuff that drives
    ///   <c>EffectiveMaxHealth</c> from above zero to zero calls <see cref="DamageResolver.KillEntity"/> directly; and</item>
    /// <item><c>AbilityCastSystem</c>'s deferred self-lethal <c>cost_health</c> death.</item>
    /// </list>
    /// <para>The ruling puts the guard on the single death PRIMITIVE, so both — and any future direct caller — are
    /// refused in one place. Self-costs stay SPEND-ABLE: only the DEATH is refused, never the HP debit.</para>
    ///
    /// <para>Every test here is RED without the guard (the invulnerable host/caster dies), and each carries its own
    /// teeth so the file cannot go vacuous by simply disabling the DW-325 kill: a non-immune twin in the same world
    /// still dies, and the immune host dies too once the flag drops.</para>
    ///
    /// Godot-free, <see cref="Fixed"/>-only, ascending-id — runs on every OS leg including the WSL cross-platform gate.
    /// </summary>
    public class InvulnerableDeathImmunityTests
    {
        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        /// <summary>A pure MaxHealth modifier (no status, no period) — the DW-325 collapse driver.</summary>
        private static Modifier MaxHpMod(int id, Fixed maxHealthDelta, int duration = 5) =>
            new Modifier(id, duration, StackRule.Refresh, 1, maxHealthDelta, Fixed.Zero, Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        /// <summary>A pure status modifier granting Invulnerable (no stat deltas, so it never touches the ceiling).</summary>
        private static Modifier InvulnMod(int id, int duration) =>
            new Modifier(id, duration, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                         StatusFlags.Invulnerable, periodEffect: null, periodTicks: 0);

        private static int Unit(EntityWorld w, int health = 100) =>
            w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(health), Fixed.FromInt(4));

        // ── The primitive itself: a refused kill is a STRICT no-op ─────────────────────────────────────

        [Fact]
        public void KillEntity_OnAnInvulnerableEntity_IsARefusedNoOp_ThenKillsOnceTheFlagDrops()
        {
            var w = new EntityWorld();
            var events = new CombatEventQueue();
            var stats = new MatchStats();
            int id = Unit(w);
            int killer0 = w.KillerOf[id], killerFaction0 = w.KillerFactionOf[id];
            w.StatusFlagsOf[id] = StatusFlags.Invulnerable;

            DamageResolver.KillEntity(w, id, Faction.Player2, events, stats);

            // Alive — and NOTHING else moved either: the guard sits ahead of every write in the death sequence, so
            // there is no killer attribution, no death-log record (the trigger DSL's unit_dies source), no UnitKilled
            // event and no stats mutation. A refused kill must be indistinguishable from "never called".
            Assert.True(w.IsAlive(id));
            Assert.Equal(killer0, w.KillerOf[id]);
            Assert.Equal(killerFaction0, w.KillerFactionOf[id]);
            Assert.Equal(0, w.DeathLog.Count);
            Assert.Equal(0, events.Count);
            Assert.Equal(0, stats.Kills(Faction.Player2));
            Assert.Equal(0, stats.Losses(Faction.Player1));

            // Teeth / PAUSE-not-cancel: the identical call kills once the flag is gone.
            w.StatusFlagsOf[id] = StatusFlags.None;
            DamageResolver.KillEntity(w, id, Faction.Player2, events, stats);

            Assert.False(w.IsAlive(id));
            Assert.Equal(1, w.DeathLog.Count);
            Assert.Equal(1, events.Count);
            Assert.Equal(CombatEventType.UnitKilled, events.Get(0).Type);
            Assert.Equal(1, stats.Kills(Faction.Player2));
            Assert.Equal(1, stats.Losses(Faction.Player1));
        }

        // ── The ceiling-collapse path (the entry's headline case) ──────────────────────────────────────

        [Fact]
        public void InvulnerableHost_CeilingCollapse_SurvivesWhileANonImmuneTwinDies()
        {
            // Both units take the SAME −100 MaxHealth debuff, in one world, processed by ascending id: the collapse is
            // genuine for both (ceiling 100 → 0, net-negative change), so the only difference is the status flag.
            var (world, _, store) = Wire();
            int immune = Unit(world);
            int mortal = Unit(world);
            Assert.True(store.Apply(immune, InvulnMod(900, duration: 20), immune, Faction.Player1));
            Assert.Equal(StatusFlags.Invulnerable, world.StatusFlagsOf[immune]);

            store.Apply(immune, MaxHpMod(901, Fixed.FromInt(-100)), immune, Faction.Player1);
            store.Apply(mortal, MaxHpMod(901, Fixed.FromInt(-100)), mortal, Faction.Player1);

            // The immune host: the collapse still HAPPENS (ceiling 0, Health clamped to 0) — only the death is refused.
            Assert.True(world.IsAlive(immune));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[immune].Raw); // non-vacuous: a real collapse
            Assert.Equal(Fixed.Zero.Raw, world.Health[immune].Raw);
            Assert.Equal(2, store.CountAt(immune));                             // both modifiers survived with it
            // Teeth: the same collapse is still lethal without the flag (the DW-325 contract is intact, not disabled).
            Assert.False(world.IsAlive(mortal));
        }

        [Fact]
        public void CeilingCollapse_KillsTheFormerlyImmuneHost_OnceTheInvulnerabilityExpires()
        {
            // PAUSE, not cancel — through the REAL authored path: the flag arrives on a modifier and leaves on expiry.
            var (world, sys, store) = Wire();
            int id = Unit(world);
            store.Apply(id, InvulnMod(910, duration: 3), id, Faction.Player1);
            store.Apply(id, MaxHpMod(911, Fixed.FromInt(-100), duration: 1), id, Faction.Player1);
            Assert.True(world.IsAlive(id));                                     // survived the collapse (death-immune)

            // The debuff expires first: the revert restores the ceiling (a positive change is never lethal) and, per
            // the 2.2b heal-only-on-apply rule, does NOT restore Health — the host sits at 0 HP under a 100 ceiling.
            sys.Tick(world, Fixed.Zero);
            Assert.True(world.IsAlive(id));
            Assert.Equal(Fixed.FromInt(100).Raw, world.EffectiveMaxHealth[id].Raw);

            // Drain the invulnerability.
            for (int t = 0; t < 8 && world.StatusFlagsOf[id] != StatusFlags.None; t++) sys.Tick(world, Fixed.Zero);
            Assert.Equal(StatusFlags.None, world.StatusFlagsOf[id]);
            Assert.Equal(0, store.CountAt(id));

            // A FRESH collapse on the now-mortal host is lethal again.
            store.Apply(id, MaxHpMod(912, Fixed.FromInt(-100)), id, Faction.Player1);
            Assert.False(world.IsAlive(id));
        }

        // ── The self-lethal cost_health path: the cost is SPENT, the death is refused ──────────────────

        /// <summary>cost_health 200 &gt; any caster's HP with allow_self_lethal — the SelfLethalCastTests shape.</summary>
        private static AbilityDefinition SuicideBomb() => new AbilityDefinition
        {
            Id = "suicide_bomb", DisplayName = "Suicide Bomb", Targeting = "TargetUnit",
            Cooldown = Fixed.FromInt(1), CostHealth = 200, AllowSelfLethal = true,
            EffectGraph = new DamageEffect(Fixed.FromInt(50), DamageType.Magic),
        };

        [Fact]
        public void InvulnerableCaster_SelfLethalCast_SpendsTheHealthAndSurvivesAtZero()
        {
            // The control arm (an ordinary caster DIES here) is SelfLethalCastTests.SelfLethalCast_EffectRunsThenCasterDies.
            var h = new CastHarness(SuicideBomb());
            int caster = h.Caster("suicide_bomb", energy: 0); // Health 100 < cost_health 200 ⇒ the debit is lethal
            int target = h.Target(health: 400);
            h.World.StatusFlagsOf[caster] = StatusFlags.Invulnerable;
            Fixed targetHp0 = h.World.Health[target];

            h.IssueAndTick(caster, target);

            Assert.True(h.World.IsAlive(caster));                                   // death-immune (RED without the guard)
            Assert.True(h.World.Health[target].Raw < targetHp0.Raw);                // the effect still fired
            // The cost was SPENT (not refused like damage) but FLOORED at 0 — never the raw −100 the debit computes.
            Assert.Equal(Fixed.Zero.Raw, h.World.Health[caster].Raw);
            // …and the surviving caster fell through to the normal tail, so it started its cooldown instead of being
            // able to re-cast every tick. That matters: an uncooled repeat would walk Health toward the Fixed underflow.
            Assert.True(h.Cooldown(caster) > 0);

            // Repeat the whole cast after the cooldown drains: the floor holds, so HP cannot ratchet below 0.
            h.TickCast(60);
            h.IssueAndTick(caster, target);
            Assert.True(h.World.IsAlive(caster));
            Assert.Equal(Fixed.Zero.Raw, h.World.Health[caster].Raw);
        }
    }
}
