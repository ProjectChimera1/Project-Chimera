#nullable enable
using ProjectChimera.Combat;            // AttackDelivery / DamageType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;  // AbilityRegistry / FactionDefinition
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;       // OrderApplier / UnitOrder
using ProjectChimera.Sim.Tests.Effects; // AbilityTestAbilities (in-code battle_fury)

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// DW-20 — the COMBAT reset fixture: a re-appliable *fighting* start state composing the three per-match paths the
    /// existing reset keystone never exercises (its golden fixture is an economy scenario in which nothing ever
    /// fights): <c>ProjectileSystem</c> (projectiles in flight across the reset boundary), <c>AbilityCastSystem</c>
    /// (a live cast: energy debit + installed modifier + ticking cooldown) and <c>CombatSystem</c> (two-sided damage).
    ///
    /// <para><b>Why it exists.</b> Investigation confirms none of those three systems holds mutable per-match state
    /// TODAY — their owned <c>SpatialHash</c>/<c>EffectExecutor</c> self-refresh at the top of each <c>Tick</c>, and
    /// <c>EffectExecutor.LastPeakStackDepth</c> is diagnostic-only and unfolded. So the guard built on this fixture
    /// cannot fail against current production code. Its value is entirely as a <b>tripwire</b>: the moment a future
    /// story folds a per-match field into one of those systems without adding it to <c>ClearForReset</c>, the
    /// reproduce-run assertion goes RED at the first divergent tick.</para>
    ///
    /// <para><b>Two traps this fixture is shaped around.</b> (1) <see cref="EntityWorld.Create"/> defaults
    /// <see cref="EntityWorld.Delivery"/> to <see cref="AttackDelivery.Hitscan"/>, so <c>Delivery = Projectile</c>
    /// MUST be set explicitly or zero projectiles ever spawn and the whole test is vacuous. (2) The projectile speed is
    /// tuned against BOTH the engagement distance and the attack cadence, because the two things the fixture needs pull
    /// in opposite directions. <c>ProjectileStore</c> is not folded into <c>SimChecksum</c> — a projectile reaches
    /// folded state only through the damage it LANDS on <c>Health</c> — so a speed slow enough that nothing ever
    /// impacts inside the sampled window makes the projectile half of the guard pin nothing at all. Speed 20 over the
    /// distance-10 engagement lands the first shot in ~15 ticks, while <c>AttackSpeed = 1</c> (one shot per 30 ticks)
    /// keeps a LATER shot airborne at reset. Landed-AND-in-flight, both asserted.</para>
    ///
    /// <para>Every value THIS FIXTURE authors is integer/<see cref="Fixed"/>, and Player2 owns no buildings and no ore
    /// so the float-scoring <c>AiOpponentSystem</c> can afford nothing — the fixture is cross-platform safe like the
    /// other Tier-1 fixtures. Precisely: <see cref="EntityWorld.Create"/> itself performs one
    /// <c>Fixed.FromFloat(8f)</c> for <c>VisionRange</c>, shared by every fixture in the suite and constant-folded from
    /// a literal, so it is deterministic across platforms — the guarantee is "no float ARITHMETIC on authored values",
    /// not "no float literal anywhere on the path".</para>
    /// </summary>
    public static class CombatResetScenario
    {
        /// <summary>Entity ids, in <see cref="Populate"/> creation order (ascending, deterministic).</summary>
        public const int Attacker1 = 0;
        public const int Attacker2 = 1;
        public const int Target1   = 2;
        public const int Target2   = 3;
        public const int Caster    = 4;

        /// <summary>Ability slot 0 of <see cref="Caster"/> — the flat index into the per-unit ability SoA.</summary>
        public const int CasterAbilitySlot0 = Caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0;

        /// <summary>A FRESH single-ability registry, one per caller — mirroring <c>Golden/AbilityCastScenario</c>.
        /// Deliberately NOT a shared static: <see cref="AbilityDefinition"/> is a mutable class and carries an
        /// <c>EffectGraph</c> node tree, so a shared instance would be handed to every host and to every xUnit class
        /// running in parallel with nothing establishing that those nodes are stateless. Per-call construction is
        /// cheap and removes the question entirely. <c>IndexOf("battle_fury")</c> is 0 in every instance (one def), so
        /// indices resolved from one registry are valid against another built the same way.</summary>
        private static AbilityRegistry NewRegistry() =>
            new AbilityRegistry(new[] { AbilityTestAbilities.BattleFury() });

        /// <summary>Construct a fresh host wired with the battle_fury registry and per-tick checksums, already
        /// populated with the fighting start state.</summary>
        public static SimulationHost Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                registry: NewRegistry());
            host.ChecksumInterval = 1; // checksum every tick → an exactly located divergence

            Populate(host);
            return host;
        }

        /// <summary>
        /// Write the authored fighting start state into <paramref name="host"/>'s (fresh or just-cleared) stores. Safe
        /// to call again after <see cref="SimulationHost.ClearForReset"/> — it only ever writes, mirroring the shape
        /// of <c>SimResetTests.PopulateAiExpansion</c>, and finishes with the <c>LoadScenario</c> the real re-apply
        /// path performs.
        /// </summary>
        public static void Populate(SimulationHost host)
        {
            EntityWorld w = host.World;

            // INVARIANT: Populate resolves the ability INDEX from its own NewRegistry(), while the cast executes
            // against the registry Build() handed the host. Those are separate instances, so they agree only because
            // both come from NewRegistry() — the index is positional, and SimulationHost does not expose its registry
            // for a direct cross-check. Consequence: Populate is only valid on a Build()-produced host. If a future
            // change gives the host a different registry (or NewRegistry() gains entries in a caller-dependent order),
            // furyIndex silently addresses a DIFFERENT ability and the -1 guard below will not catch it, because the
            // failure mode is "wrong ability", not "no ability".
            AbilityRegistry registry = NewRegistry();

            // ── ids 0-1 — P1 PROJECTILE attackers. AttackRange 12 covers the distance-10 engagement; Delivery must be
            //    set EXPLICITLY (Create defaults to Hitscan). Speed 20 u/s over 10 units ≈ 15 ticks of flight at 30
            //    tps, while AttackSpeed 1 fires one shot per 30 ticks — so within the sampled 40-tick window the FIRST
            //    shot lands (folding projectile damage into the checksum, which ProjectileStore itself never does) and
            //    a LATER shot is still airborne at reset. Both halves are asserted by the test. ──
            var attackers = new int[2];
            var targets   = new int[2];

            for (int i = 0; i < 2; i++)
            {
                int a = attackers[i] = w.Create(V(0, 0, i * 20), Faction.Player1, Fixed.FromInt(500), Fixed.FromInt(3));
                // Base AND Effective: ModifierSystem.RecomputeEntity derives Effective = max(0, Base + bonus), so a
                // Base left at 0 would silently ZERO this attacker's damage the moment anything recomputes it (an
                // aura, a debuff, a global pass) and collapse the fight. Today only dirty entities are recomputed —
                // do not let the fixture depend on that.
                w.BaseAttackDamage[a]      = Fixed.FromInt(9);
                w.EffectiveAttackDamage[a] = Fixed.FromInt(9);
                w.AttackRange[a]           = Fixed.FromInt(12);
                w.AttackSpeed[a]           = Fixed.FromInt(1);
                w.DamageTypeOf[a]          = DamageType.Normal;
                w.Delivery[a]              = AttackDelivery.Projectile; // TRAP (1): Create defaults to Hitscan, which
                                                                        // spawns ZERO projectiles and makes the guard vacuous.
                w.ProjectileSpeed[a]       = Fixed.FromInt(20);         // Not a trap — Create already defaults this to
                                                                        // ProjectileSystem.PROJECTILE_SPEED (18). Pinned
                                                                        // explicitly so the ~15-tick flight the sampling
                                                                        // window depends on cannot drift with that constant.
                w.CommandState[a]          = UnitCommand.Idle;          // Already Create's default; restated for symmetry
                                                                        // with the P2 block. Idle is what lets CombatSystem
                                                                        // auto-acquire the nearest enemy.
            }

            // ── ids 2-3 — P2 targets, in range and shooting BACK (Hitscan) so CombatSystem is exercised two-sided.
            //    High HP on both sides: nobody dies over the short reset runs, so the fight is still live at reset. ──
            for (int i = 0; i < 2; i++)
            {
                int t = targets[i] = w.Create(V(10, 0, i * 20), Faction.Player2, Fixed.FromInt(999), Fixed.FromInt(3));
                w.BaseAttackDamage[t]      = Fixed.FromInt(4);          // see the Base/Effective note above
                w.EffectiveAttackDamage[t] = Fixed.FromInt(4);
                w.AttackRange[t]           = Fixed.FromInt(12);
                w.AttackSpeed[t]           = Fixed.FromInt(1);
                w.DamageTypeOf[t]          = DamageType.Normal;
                w.Delivery[t]              = AttackDelivery.Hitscan;
                w.CommandState[t]          = UnitCommand.Idle;
            }

            // ── id 4 — the P1 CASTER holding battle_fury (Self: +12 atk / +1 move for 150 ticks, 12s cooldown) with an
            //    energy pool. Parked far from the fight and unarmed, so its only sim activity is the cast. The ability
            //    slot is wired directly (integer/Fixed only), exactly like AbilityCastScenario. ──
            int caster = w.Create(V(-40, 0, 0), Faction.Player1, Fixed.FromInt(200), Fixed.FromInt(3));

            int furyIndex = registry.IndexOf("battle_fury");
            if (furyIndex < 0)
                throw new System.InvalidOperationException(
                    "CombatResetScenario: AbilityRegistry.IndexOf(\"battle_fury\") returned the -1 \"no such ability\" " +
                    "sentinel. Written into AbilityId that reads as NO ABILITY, so the cast becomes a silent no-op and " +
                    "the ability half of the reset guard would pass vacuously. Check AbilityTestAbilities.BattleFury()'s Id.");
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = furyIndex;

            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster]    = Fixed.FromInt(50);
            w.Energy[caster]       = Fixed.FromInt(50);
            // A real Base, so the test's "Effective > Base ⇒ battle_fury is installed" precondition is a genuine buff
            // check (+12 on top of 2) rather than a 12-vs-0 comparison that would hold for any nonzero Effective.
            w.BaseAttackDamage[caster]      = Fixed.FromInt(2);
            w.EffectiveAttackDamage[caster] = Fixed.FromInt(2);

            // The public id constants are HARDCODED (Attacker1 = 0 … Caster = 4). If a creation order/recycling change
            // ever shifts them, every precondition assertion in the reset tests silently retargets a different entity
            // and keeps passing. Fail here, at the cause.
            RequireId(Attacker1, attackers[0], nameof(Attacker1));
            RequireId(Attacker2, attackers[1], nameof(Attacker2));
            RequireId(Target1,   targets[0],   nameof(Target1));
            RequireId(Target2,   targets[1],   nameof(Target2));
            RequireId(Caster,    caster,       nameof(Caster));

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror the re-apply lifecycle (empty → no-op)
        }

        /// <summary>Assert a <see cref="EntityWorld.Create"/> returned the id its public constant hardcodes.</summary>
        private static void RequireId(int expected, int actual, string name)
        {
            if (expected != actual)
                throw new System.InvalidOperationException(
                    $"CombatResetScenario.{name} is {expected} but EntityWorld.Create returned {actual}. The public id " +
                    "constants no longer match creation order, so every assertion using them targets the wrong entity. " +
                    "Fix the constants (or the population order) rather than letting the guards silently mis-aim.");
        }

        /// <summary>Issue the Self cast of the caster's slot 0 through the SAME shared <see cref="OrderApplier"/> the
        /// live/replay/offline paths use. Call BEFORE a <c>StepOnce</c> so the cast lands in that tick's checksum.</summary>
        public static void CastAt(SimulationHost host) =>
            OrderApplier.Apply(host.World,
                new UnitOrder(Caster, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                Faction.Player1);

        /// <summary>Number of projectiles currently alive in the store (the in-flight precondition).</summary>
        public static int LiveProjectiles(SimulationHost host)
        {
            int n = 0;
            // Bound by Alive.Length, not HighWaterMark — the sibling post-clear assertion in SimResetTests scans the
            // full backing array for exactly this reason, and a slot somehow alive past the high-water mark must be
            // COUNTED here (under-counting would weaken the in-flight precondition), not skipped.
            for (int i = 0; i < host.Projectiles.Alive.Length; i++)
                if (host.Projectiles.Alive[i]) n++;
            return n;
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
