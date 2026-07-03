using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;
using ProjectChimera.Sim.Tests.Effects; // AbilityTestAbilities.EqualExchange (in-code Sequence[apply_modifier, direct_hp_delta])

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.10 (AC1.5 / D-5) — the EQUAL EXCHANGE golden scenario: the first golden whose recorded
    /// <see cref="SimChecksum"/> (v8) sequence exercises the <c>Sequence[apply_modifier, direct_hp_delta]</c> shape —
    /// a beneficial self-buff followed by a FLAT, armor-INDEPENDENT HP self-cost (the vitality-price Equal Exchange).
    /// One stationary Player1 caster (id 0) holds a Self <c>equal_exchange</c> ability (ApplyModifier +15 atk /
    /// 120-tick Refresh, then a −25 flat HP cost; no matter/energy price); a <see cref="UnitCommand.CastAbility"/>
    /// <see cref="UnitOrder"/> is issued via the shared <see cref="OrderApplier"/> at tick 1. Over the run the checksum
    /// captures the tick-1 Health 100→75 drop (clamped to <c>[0, EffectiveMaxHealth]</c>, NOT routed through the damage
    /// matrix), the modifier install + same-tick EffectiveAttack recompute, the modifier expiring at ~tick 121, and the
    /// v7 ability-cooldown ticking down.
    ///
    /// Player2 is left EMPTY so the <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops — keeping every hashed field
    /// integer/<see cref="Fixed"/>, so this golden is CROSS-PLATFORM SAFE and compared on BOTH CI legs (NOT
    /// Windows-gated). The ability slot + energy pool are wired DIRECTLY (not via <c>ApplyUnitDefinition</c>) to keep the
    /// scenario free of any float-derived value — exactly like <see cref="AbilityCastScenario"/>.
    /// </summary>
    public static class EqualExchangeScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        private const int Caster     = 0; // the only entity; created first → id 0
        private const int CastAtStep = 0; // issue the cast before the first StepOnce → consumed in tick 1's checksum

        /// <summary>Construct a fresh, fully-wired sim with one stationary Player1 caster holding equal_exchange.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] { AbilityTestAbilities.EqualExchange() });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (both 0 ore here)
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;        // checksum every tick

            EntityWorld w = host.World;

            // One stationary Player1 caster (id 0) at full HP (100). Wire ability slot 0 + the energy pool directly
            // (integer/Fixed only → cross-platform safe). No energy is needed — equal_exchange costs 0 energy; HP is the
            // sole price (paid by the direct_hp_delta child), mirroring the shipped Transmuter's max_energy: 0.
            int caster = w.Create(V(-10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("equal_exchange"); // = 0
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.Zero;
            w.Energy[caster]    = Fixed.Zero;

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, caster);
        }

        /// <summary>
        /// Issue the fixed cast schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>, so the cast
        /// at index 0 is consumed in tick 1's checksum). A Self cast of slot 0: slot in TargetX, target -1 (Self) in
        /// TargetZ — through the SAME shared <see cref="OrderApplier"/> live/replay/offline use (the cast spine).
        /// </summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i == CastAtStep)
            {
                OrderApplier.Apply(host.World,
                    new UnitOrder(Caster, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                    Faction.Player1);
            }
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
