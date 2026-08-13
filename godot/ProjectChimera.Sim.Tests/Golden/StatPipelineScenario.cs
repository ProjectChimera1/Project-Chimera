using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15-24a — the STAT-PIPELINE golden scenario: the first golden whose recorded
    /// <see cref="SimChecksum"/> sequence exercises the v26 bounded fold and every NEW consumer channel with
    /// LIVE state — an attack_speed buff changing a fighter's swing cadence (the folded Health of its victim
    /// records the cadence), a health_regen buff healing a damaged unit per tick, cooldown_reduction held
    /// non-zero on a unit (the folded v26 term itself), max_health_percent + flat composing on one host, and
    /// a mid-run EXPIRY of the attack-speed buff (the interval returns to authored — visible as the victim's
    /// damage cadence slows). Player2 hosts the stationary victim; combat is a single melee attacker so
    /// every hashed field stays integer/<see cref="Fixed"/> — CROSS-PLATFORM SAFE, compared on BOTH CI legs.
    /// </summary>
    public static class StatPipelineScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        // ── Stable entity ids (created in ascending order; the host spawns zero entities). ──
        private const int Fighter   = 0; // P1 melee attacker — attack_speed buff (expires mid-run) + CDR term
        private const int RegenUnit = 1; // P1 damaged unit — health_regen buff (folded Health climbs per tick)
        private const int PctUnit   = 2; // P1 unit — max_health_percent × flat composition (folded ceiling)
        private const int Victim    = 3; // P2 stationary tank — the fighter's swing cadence writes its Health

        /// <summary>Construct a fresh, fully-wired sim (P1 fighter adjacent to a P2 victim; no AI armies).</summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // The fighter: melee, 2s authored interval, parked in attack range of the victim.
            int fighter = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.BaseAttackDamage[fighter] = Fixed.FromInt(5);
            w.EffectiveAttackDamage[fighter] = Fixed.FromInt(5);
            w.AttackRange[fighter]  = Fixed.FromInt(3);
            w.AttackSpeed[fighter]  = Fixed.FromInt(2); // 2s between swings at the identity factor

            int regen = w.Create(V(-8, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Health[regen] = Fixed.FromInt(40); // damaged → the regen buff heals visibly (folded Health)

            w.Create(V(-12, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // PctUnit (id 2)

            // The victim: big stationary P2 tank in the fighter's range (no attack back — 0 base damage).
            int victim = w.Create(V(2, 0, 0), Faction.Player2, Fixed.FromInt(2000), Fixed.FromInt(3));
            w.BaseAttackDamage[victim] = Fixed.Zero;
            w.EffectiveAttackDamage[victim] = Fixed.Zero;

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, fighter);
        }

        /// <summary>
        /// The fixed schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>, so an apply at
        /// index <c>i</c> lands in tick <c>i+1</c>'s checksum). All-integer/Fixed; deterministic.
        /// </summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            ModifierStore store = host.Modifiers;
            EntityWorld w = host.World;
            switch (i)
            {
                case 0:
                    // The fighter engages: a force-attack state on the SoA directly (the 1.12 AttackTarget
                    // command shape) — a pure sim-input schedule, no UI path involved.
                    w.CommandState[Fighter] = UnitCommand.AttackTarget;
                    w.AttackTarget[Fighter] = w.PackRef(Victim);
                    w.CommandTarget[Fighter] = w.PackRef(Victim);
                    break;
                case 20:
                    // +100% attack speed for 120 ticks: swings double their cadence until ~tick 140, then the
                    // EXPIRY reverts the factor to One (both transitions visible in the victim's folded Health
                    // cadence AND the v26 bounded factor fold itself).
                    store.Apply(Fighter, Vec(11, 120, D(StatId.AttackSpeed, Fixed.One)), Fighter, Faction.Player1);
                    break;
                case 30:
                    // Health regen 1 HP/tick, permanent — RegenUnit's folded Health climbs 40 → 100 and clamps.
                    store.Apply(RegenUnit, Vec(12, -1, D(StatId.HealthRegen, Fixed.One)), RegenUnit, Faction.Player1);
                    break;
                case 40:
                    // +25% max health × +20 flat on one host — the percent stage's composition in the folded
                    // ceiling: (100 + 20) × 1.25 = 150 (and the realized-gain heal on apply).
                    store.Apply(PctUnit, Vec(13, -1,
                        D(StatId.MaxHealth, Fixed.FromInt(20)),
                        D(StatId.MaxHealthPercent, Fixed.FromRaw(Fixed.ONE / 4))), PctUnit, Faction.Player1);
                    break;
                case 50:
                    // 30% cooldown reduction, permanent — holds the v26 CDR term non-zero for the rest of the
                    // run (the bounded fold's own lane; no ability needs to cast for the fold to see it).
                    store.Apply(Fighter, Vec(14, -1, D(StatId.CooldownReduction, Fixed.FromRaw(Fixed.ONE * 3 / 10))), Fighter, Faction.Player1);
                    break;
            }
        }

        private static Modifier Vec(int id, int duration, params StatDelta[] deltas) =>
            new Modifier(id, duration, StackRule.Refresh, 1,
                StatVocabulary.Canonicalize(new List<StatDelta>(deltas)), StatusFlags.None, null, 0);

        private static StatDelta D(StatId s, Fixed v) => new StatDelta(s, v);

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
