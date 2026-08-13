using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Core.Stats;
using ProjectChimera.Effects;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15-24b — the COMBAT-DICE golden scenario: the first golden whose recorded checksum sequence
    /// carries LIVE SimRng draws from combat (every prior golden's stream sat untouched at its seed). A
    /// crit-capable melee fighter (50% crit, +0.5 crit damage) pounds a dodge-capable tank (50% dodge)
    /// for 300 ticks — every swing draws crit-then-dodge on the shared stream, so the folded
    /// <c>SimRng.State</c> AND the victim's folded Health both pin the exact roll sequence. SplitMix64 and
    /// the whole roll path are integer/<see cref="Fixed"/>-only — CROSS-PLATFORM SAFE, compared on BOTH CI
    /// legs (a Win↔Linux divergence in the dice = a checksum drift here, never a silent LAN desync).
    /// </summary>
    public static class CritDodgeScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>Fixed match seed — the stream origin every peer/replay would share.</summary>
        public const ulong Seed = 0xC0DE_D1CEUL;

        private const int Fighter = 0;
        private const int Victim  = 1;

        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;
            w.Rng.Seed(Seed); // deterministic stream origin (the LiveMatchSeed shape)

            // The fighter: melee, 1s interval, parked in range — swings ~every 30 ticks for the whole run.
            int fighter = w.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1,
                                   Fixed.FromInt(100), Fixed.FromInt(3));
            w.BaseAttackDamage[fighter] = Fixed.FromInt(5);
            w.EffectiveAttackDamage[fighter] = Fixed.FromInt(5);
            w.AttackRange[fighter] = Fixed.FromInt(3);
            w.AttackSpeed[fighter] = Fixed.One;

            // The victim: big stationary P2 tank (0 base damage → the AI conscripts nothing).
            int victim = w.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero), Faction.Player2,
                                  Fixed.FromInt(5000), Fixed.FromInt(3));
            w.BaseAttackDamage[victim] = Fixed.Zero;
            w.EffectiveAttackDamage[victim] = Fixed.Zero;

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, fighter);
        }

        /// <summary>The fixed schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>).</summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            EntityWorld w = host.World;
            switch (i)
            {
                case 0:
                    // Engage (the 1.12 force-attack shape — a pure sim-input schedule).
                    w.CommandState[Fighter] = UnitCommand.AttackTarget;
                    w.AttackTarget[Fighter] = w.PackRef(Victim);
                    w.CommandTarget[Fighter] = w.PackRef(Victim);
                    // Arm the dice: 50% crit + half-again crit damage on the fighter; 50% dodge on the tank.
                    host.Modifiers.Apply(Fighter, Vec(21, D(StatId.CritChance, Fixed.Half), D(StatId.CritMultiplier, Fixed.Half)),
                                         Fighter, Faction.Player1);
                    host.Modifiers.Apply(Victim, Vec(22, D(StatId.DodgeChance, Fixed.Half)),
                                         Victim, Faction.Player2);
                    break;
            }
        }

        private static Modifier Vec(int id, params StatDelta[] deltas) =>
            new Modifier(id, -1, StackRule.Refresh, 1,
                StatVocabulary.Canonicalize(new List<StatDelta>(deltas)), StatusFlags.None, null, 0);

        private static StatDelta D(StatId s, Fixed v) => new StatDelta(s, v);
    }
}
