#nullable enable
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction
using ProjectChimera.Effects;           // StatusFlags, Modifier, StackRule, ModifierSystem/Store
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-687 — the DW-620 <see cref="StatusFlags.Invulnerable"/> death-immunity guard read a STALE status union on
    /// BOTH <c>ModifierStore</c> paths, so a ceiling collapse was judged against a flag word that did not describe the
    /// post-operation host.
    ///
    /// <para>DW-620 put the flag check inside <c>DamageResolver.KillEntity</c> — deliberately, so every direct-kill
    /// caller is covered in one place. But <c>ModifierStore</c> mutated stats on ONE SIDE of the status-union write on
    /// both of its paths, and the DW-325/DW-491 ceiling-collapse kill fires from INSIDE that stat mutation:</para>
    /// <list type="bullet">
    ///   <item><b>REMOVE (wrongly refused).</b> <c>RemoveSlot</c> ran <c>ApplyStatDeltas</c> BEFORE
    ///     <c>RecomputeStatusUnion</c>, so a collapse caused by reverting the very modifier that GRANTED
    ///     <c>Invulnerable</c> was refused by that expiring flag — and the union was recomputed to <c>None</c> one line
    ///     later. Result: a live host at <c>EffectiveMaxHealth</c> 0 / <c>Health</c> 0 / <c>StatusFlags.None</c> — the
    ///     0-ceiling zombie DW-325 exists to eliminate, and unreachable by DW-620's own "a FRESH collapse once the flag
    ///     drops" recovery, because <c>ceilingBefore &gt; Fixed.Zero</c> can never hold again once the ceiling is
    ///     pinned at zero.</item>
    ///   <item><b>APPLY (wrongly allowed — the mirror image).</b> <c>InstallNewSlot</c> ran <c>ApplyStatDeltas</c>
    ///     BEFORE OR-ing <c>mod.Status</c>, so a modifier granting <c>Invulnerable</c> TOGETHER with a net-negative
    ///     <c>max_health_delta</c> collapsed the ceiling while its own flag was not yet installed — and KILLED the host
    ///     it was authored to make death-immune.</item>
    /// </list>
    ///
    /// <para>Distinct from DW-676, which is the definitional pause-vs-cancel question for a host that is GENUINELY
    /// immune at collapse time. Here the immunity is STALE (remove) or NOT YET INSTALLED (apply), and a deferred-death
    /// re-check would not touch the apply arm at all.</para>
    ///
    /// <para>Both arms are RED on the pre-fix ordering. Each carries its own teeth so the file cannot go vacuous:
    /// a control host with the same numbers and NO status flag behaves the opposite way in both arms, which pins that
    /// the DW-325 collapse contract itself is intact and only the ORDERING moved.</para>
    ///
    /// <para>Godot-free, <see cref="Fixed"/>-only, ascending-id. Nothing here is folded differently than before — the
    /// change is purely the order of two writes within one tick, and no shipped ability authors a status, so every
    /// recorded golden leaves <c>StatusFlagsOf</c> at <see cref="StatusFlags.None"/> and no checksum moves.</para>
    /// </summary>
    public class ModifierStatusUnionOrderingTests
    {
        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire()
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        private static int Unit(EntityWorld w, int health = 100) =>
            w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(health), Fixed.FromInt(4));

        /// <summary>A modifier carrying BOTH a MaxHealth delta and a status — the shape neither arm could handle.</summary>
        private static Modifier Mod(int id, int duration, int maxHealthDelta, StatusFlags status) =>
            new Modifier(id, duration, StackRule.Refresh, 1, Fixed.FromInt(maxHealthDelta), Fixed.Zero, Fixed.Zero,
                         status, periodEffect: null, periodTicks: 0);

        // ── ARM 1 (REMOVE): the collapse caused by dropping Invulnerable must not be refused by it ─────────

        [Fact]
        public void ExpiringTheModifierThatGrantedInvulnerability_KillsTheHostItsOwnRemovalCollapses()
        {
            // The ledger's exact fixture. Base MaxHealth 100.
            //   B {status Invulnerable, +40 max health, duration 2}  -> ceiling 140
            //   A {-100 max health, PERMANENT}                       -> ceiling 40, Health clamped to 40
            // When B expires, RemoveSlot reverts −40: the ceiling goes 40 -> 0 and the DW-491 collapse gate is
            // satisfied. The ONLY thing that should decide the death is whether the host is still immune — and it is
            // not, because the expiring modifier B is the sole source of that immunity.
            var (world, sys, store) = Wire();
            int id = Unit(world);

            Assert.True(store.Apply(id, Mod(910, duration: 2, maxHealthDelta: +40, StatusFlags.Invulnerable), id, Faction.Player1));
            Assert.Equal(Fixed.FromInt(140).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(StatusFlags.Invulnerable, world.StatusFlagsOf[id]);

            Assert.True(store.Apply(id, Mod(911, duration: -1, maxHealthDelta: -100, StatusFlags.None), id, Faction.Player1));
            Assert.True(world.IsAlive(id));                                          // fixture: not lethal yet (140 -> 40)
            Assert.Equal(Fixed.FromInt(40).Raw, world.EffectiveMaxHealth[id].Raw);
            Assert.Equal(Fixed.FromInt(40).Raw, world.Health[id].Raw);                // clamped under the new ceiling

            // Drain B's duration. Its removal is the collapse.
            for (int t = 0; t < 4 && world.IsAlive(id); t++) sys.Tick(world, Fixed.Zero);

            // PRE-FIX: alive, ceiling 0, Health 0, StatusFlags None — the permanent 0-ceiling zombie. It could never
            // recover (the permanent −100 keeps the ceiling pinned, so ceilingBefore > 0 never holds again), so it
            // kept its faction alive for elimination win conditions indefinitely.
            Assert.False(world.IsAlive(id));
        }

        [Fact]
        public void AnExpiryCollapse_IsStillRefused_WhileANOTHERModifierKeepsTheHostInvulnerable()
        {
            // The teeth for the arm above, and the reason the fix RECOMPUTES the union rather than blindly clearing
            // it: exclude only the expiring slot. A second, independent Invulnerable grant must still protect the host
            // through the identical collapse — otherwise the fix would have turned DW-620 off for every expiry.
            var (world, sys, store) = Wire();
            int id = Unit(world);

            // A long-lived, stat-free immunity from a DIFFERENT modifier id…
            Assert.True(store.Apply(id, Mod(920, duration: 60, maxHealthDelta: 0, StatusFlags.Invulnerable), id, Faction.Player1));
            // …plus the same +40/Invulnerable + permanent −100 pair as the arm above.
            Assert.True(store.Apply(id, Mod(910, duration: 2, maxHealthDelta: +40, StatusFlags.Invulnerable), id, Faction.Player1));
            Assert.True(store.Apply(id, Mod(911, duration: -1, maxHealthDelta: -100, StatusFlags.None), id, Faction.Player1));
            Assert.Equal(Fixed.FromInt(40).Raw, world.EffectiveMaxHealth[id].Raw);

            for (int t = 0; t < 4; t++) sys.Tick(world, Fixed.Zero);

            Assert.True(world.IsAlive(id));                                          // still genuinely immune
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[id].Raw);          // the collapse itself DID happen
            Assert.Equal(StatusFlags.Invulnerable, world.StatusFlagsOf[id]);         // …held by the surviving modifier
        }

        // ── ARM 2 (APPLY): a modifier must not be killed by the collapse it grants immunity to ────────────

        [Fact]
        public void InstallingInvulnerableTogetherWithALethalMaxHealthDebuff_DoesNotKillTheHostItProtects()
        {
            // ONE modifier carries both halves: Invulnerable + a net-negative max_health_delta that collapses the
            // ceiling from 100 to 0. PRE-FIX the stat apply ran first, so KillEntity read a StatusFlagsOf that did not
            // yet carry this modifier's own Invulnerable and killed the host outright.
            var (world, _, store) = Wire();
            int protectedHost = Unit(world);
            int control       = Unit(world);

            Assert.True(store.Apply(protectedHost, Mod(930, duration: 20, maxHealthDelta: -100, StatusFlags.Invulnerable),
                                    protectedHost, Faction.Player1));

            Assert.True(world.IsAlive(protectedHost));                                // RED pre-fix
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[protectedHost].Raw); // the collapse still HAPPENED…
            Assert.Equal(Fixed.Zero.Raw, world.Health[protectedHost].Raw);            // …only the death was refused
            Assert.Equal(StatusFlags.Invulnerable, world.StatusFlagsOf[protectedHost]);
            Assert.Equal(1, store.CountAt(protectedHost));                            // the slot survived with its host

            // Teeth: the identical numbers WITHOUT the status flag are still lethal. The fix reorders two writes; it
            // does not weaken the DW-325 collapse kill.
            Assert.True(store.Apply(control, Mod(931, duration: 20, maxHealthDelta: -100, StatusFlags.None),
                                    control, Faction.Player1));
            Assert.False(world.IsAlive(control));
        }

        [Fact]
        public void StackingUpToALethalCollapse_HonoursTheStacksOwnInvulnerable()
        {
            // The Stack arm of the apply path: the collapse is reached by the SECOND application, not the first, so
            // the kill fires from the StackRule.Stack branch rather than from InstallNewSlot. Same rule — the flag word
            // must describe the host as this operation leaves it — and the same teeth on a status-free control.
            //
            // Honest scope: this one is GREEN on the pre-fix ordering too, because the FIRST install had already OR-ed
            // this id's Invulnerable in and the Stack branch's re-OR is idempotent. It is coverage for the branch the
            // other three arms do not reach — the one that becomes load-bearing the moment a caller stacks a Modifier
            // instance carrying a status the installed same-id instance did not — not a second reproduction.
            var (world, _, store) = Wire();
            int protectedHost = Unit(world);
            int control       = Unit(world);

            var immuneStack = new Modifier(940, 20, StackRule.Stack, 2, Fixed.FromInt(-50), Fixed.Zero, Fixed.Zero,
                                           StatusFlags.Invulnerable, periodEffect: null, periodTicks: 0);
            var plainStack  = new Modifier(941, 20, StackRule.Stack, 2, Fixed.FromInt(-50), Fixed.Zero, Fixed.Zero,
                                           StatusFlags.None, periodEffect: null, periodTicks: 0);

            store.Apply(protectedHost, immuneStack, protectedHost, Faction.Player1);   // ceiling 100 -> 50
            store.Apply(control,       plainStack,  control,       Faction.Player1);
            Assert.Equal(Fixed.FromInt(50).Raw, world.EffectiveMaxHealth[protectedHost].Raw);
            Assert.True(world.IsAlive(control));

            store.Apply(protectedHost, immuneStack, protectedHost, Faction.Player1);   // ceiling 50 -> 0 (the collapse)
            store.Apply(control,       plainStack,  control,       Faction.Player1);

            Assert.True(world.IsAlive(protectedHost));
            Assert.Equal(Fixed.Zero.Raw, world.EffectiveMaxHealth[protectedHost].Raw);
            Assert.False(world.IsAlive(control));                                      // teeth: still lethal unprotected
        }
    }
}
