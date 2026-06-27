using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;
using ProjectChimera.Sim.Tests.Effects; // AbilityTestAbilities (in-code battle_fury)

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.4a (AC3) — the ABILITY-CAST golden scenario: the first golden whose recorded <see cref="SimChecksum"/>
    /// (v7) sequence exercises a LIVE ability cast through the deterministic engine. One stationary Player1 caster
    /// (id 0) is given a Self <c>battle_fury</c> (ApplyModifier +12 atk / +1 move, 150-tick duration, 12s cooldown)
    /// and a 50-energy pool; a <see cref="UnitCommand.CastAbility"/> <see cref="UnitOrder"/> is issued via the shared
    /// <see cref="OrderApplier"/> at tick 1. Over the run the checksum captures the energy debit, the modifier install
    /// + same-tick recompute, the modifier expiring at ~tick 151, and the v7 ability-cooldown ticking down.
    ///
    /// Player2 is left EMPTY so the <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops — keeping every hashed
    /// field integer/<see cref="Fixed"/>, so this golden is CROSS-PLATFORM SAFE and compared on BOTH CI legs (NOT
    /// Windows-gated, unlike the float-scoring AI golden). The ability slot is wired DIRECTLY (not via
    /// <c>ApplyUnitDefinition</c>) to keep the scenario free of any float-derived value — exactly like
    /// <see cref="ModifierScenario"/> seeds Energy directly; the <c>ApplyUnitDefinition</c> attach path is proven
    /// separately by <c>ApplyUnitDefinitionGuardTest</c>.
    /// </summary>
    public static class AbilityCastScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        private const int Caster     = 0; // the only entity; created first → id 0
        private const int CastAtStep = 0; // issue the cast before the first StepOnce → consumed in tick 1's checksum

        /// <summary>Construct a fresh, fully-wired sim with one stationary Player1 caster holding battle_fury.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] { AbilityTestAbilities.BattleFury() });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (both 0 ore here)
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;        // checksum every tick

            EntityWorld w = host.World;

            // One stationary Player1 caster (id 0). Wire ability slot 0 + the energy pool directly (integer/Fixed
            // only → cross-platform safe), mirroring how ModifierScenario seeds Energy directly.
            int caster = w.Create(V(-10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("battle_fury"); // = 0
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.FromInt(50);
            w.Energy[caster]    = Fixed.FromInt(50);

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
