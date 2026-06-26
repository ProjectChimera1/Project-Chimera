using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.2b (AC2/AC3) — the MODIFIER-ACTIVE golden scenario: the first golden whose recorded
    /// <see cref="SimChecksum"/> sequence exercises LIVE <see cref="ModifierStore"/> state (the existing 7 goldens
    /// only ever saw the dormant <c>Effective==Base</c> / <c>count==0</c> fold). Four stationary Player1 units are
    /// driven through a fixed <c>(tick → apply)</c> schedule: a finite +damage buff that is REFRESHED mid-run (so the
    /// duration reset is visible in the checksum sequence) then expires; a 2-stack stacking modifier with a later
    /// IGNORE re-apply (no-op); a DoT and a HoT; and an Energy debit. Player2 is left EMPTY so the
    /// <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops — keeping every hashed field integer/<see cref="Fixed"/>,
    /// so this golden is CROSS-PLATFORM SAFE and compared on BOTH CI legs (NOT Windows-gated).
    /// </summary>
    public static class ModifierScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        // ── Stable entity ids (created in ascending order; the host spawns zero entities). ──
        private const int BuffUnit  = 0; // a refreshed finite +damage buff
        private const int StackUnit = 1; // a 2-stack modifier + an ignored re-apply
        private const int DotUnit   = 2; // a DoT
        private const int HotUnit   = 3; // a HoT on a damaged unit

        /// <summary>Construct a fresh, fully-wired sim with four stationary Player1 units (Player2 empty → AI no-op).</summary>
        public static GoldenHarness Build()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (both 0 ore here)
                new FactionDefinition(),
                new FactionDefinition());
            host.ChecksumInterval = 1;        // checksum every tick

            EntityWorld w = host.World;

            // Four stationary, non-fighting Player1 units (no MoveTarget, no enemies → only the modifier schedule
            // and DoT/HoT move the checksum). Authored bases set explicitly; all integer/Fixed.
            int buff = Unit(w, V(-10, 0, 0), baseAtk: 10);
            w.Energy[buff] = Fixed.FromInt(20);   // seed Energy so the debit step is meaningful (folded)

            Unit(w, V(-6, 0, 0), baseAtk: 10);    // StackUnit (id 1)

            Unit(w, V(-2, 0, 0), baseAtk: 0);     // DotUnit (id 2): 100/100

            int hot = Unit(w, V(2, 0, 0), baseAtk: 0); // HotUnit (id 3)
            w.Health[hot] = Fixed.FromInt(50);    // damaged so the HoT heals visibly (100 ceiling)

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, buff);
        }

        /// <summary>
        /// Apply the fixed modifier schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>, so an
        /// apply at index <c>i</c> is reflected in tick <c>i+1</c>'s checksum). All-integer/Fixed; deterministic.
        /// </summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            ModifierStore store = host.Modifiers;
            switch (i)
            {
                case 0:
                    store.Apply(BuffUnit, AtkBuff(id: 1, duration: 60, atk: 5), BuffUnit, Faction.Player1);
                    store.Apply(StackUnit, StackMod(id: 2, duration: 120, atk: 2), StackUnit, Faction.Player1);
                    store.Apply(StackUnit, StackMod(id: 2, duration: 120, atk: 2), StackUnit, Faction.Player1); // second stack
                    store.InstallPersistent(DotUnit, Dot(dmg: 2, periodTicks: 20, periodCount: 5), DotUnit, Faction.Player1);
                    store.InstallPersistent(HotUnit, Hot(heal: 3, periodTicks: 25, periodCount: 4), HotUnit, Faction.Player1);
                    break;
                case 10:
                    store.TryDebitEnergy(BuffUnit, Fixed.FromInt(5)); // 20 → 15
                    break;
                case 30:
                    store.Apply(BuffUnit, AtkBuff(id: 1, duration: 60, atk: 5), BuffUnit, Faction.Player1); // REFRESH → extends to ~tick 90
                    break;
                case 45:
                    store.Apply(StackUnit, IgnoreMod(id: 2, duration: 120, atk: 2), StackUnit, Faction.Player1); // ignored (no-op)
                    break;
            }
        }

        // ── Descriptor factories ──
        private static Modifier AtkBuff(int id, int duration, int atk) =>
            new Modifier(id, duration, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static Modifier StackMod(int id, int duration, int atk) =>
            new Modifier(id, duration, StackRule.Stack, 3, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static Modifier IgnoreMod(int id, int duration, int atk) =>
            new Modifier(id, duration, StackRule.Ignore, 3, Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero,
                         StatusFlags.None, periodEffect: null, periodTicks: 0);

        private static PersistentEffect Dot(int dmg, int periodTicks, int periodCount) =>
            new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-dmg)), null, periodTicks, periodCount);

        private static PersistentEffect Hot(int heal, int periodTicks, int periodCount) =>
            new PersistentEffect(null, new HealEffect(Fixed.FromInt(heal)), null, periodTicks, periodCount);

        private static int Unit(EntityWorld w, FixedVec3 pos, int baseAtk)
        {
            int id = w.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.BaseAttackDamage[id]      = Fixed.FromInt(baseAtk);
            w.EffectiveAttackDamage[id] = Fixed.FromInt(baseAtk);
            return id;
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
