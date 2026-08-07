using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;           // SearchAreaEffect, DamageEffect, TargetFilter
using ProjectChimera.Multiplayer;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 15.11 (DW-280) — the GROUND-CAST golden scenario: the first golden whose recorded <see cref="SimChecksum"/>
    /// sequence exercises a LIVE <see cref="AbilityTargeting.GroundPoint"/> cast through the deterministic engine. One
    /// stationary Player1 caster (id 0), far from the fray, holds a GroundPoint <c>ground_nuke</c>
    /// (<c>SearchArea(radius 4, Neutral) → Damage 60 Magic</c>, 60 energy, 8s cooldown). A
    /// <see cref="UnitCommand.CastAbility"/> order carrying the two Fixed GROUND COORDS (not a target id) is issued via
    /// the shared <see cref="OrderApplier"/> at tick 1; the effect resolves centered on that ground point, damaging the
    /// three Neutral dummies clustered there. Over the run the checksum captures the energy debit, the three targets'
    /// health drop, and the ability cooldown ticking down.
    ///
    /// <para>Cross-platform safe: the targets are NEUTRAL (no AI drives Neutral) and Player2 is left EMPTY, so the
    /// float-scoring <see cref="ProjectChimera.AI.AiOpponentSystem"/> no-ops and every hashed field stays
    /// integer/<see cref="Fixed"/> — compared on BOTH CI legs, NOT Windows-gated. The ability slot + energy are wired
    /// DIRECTLY (no float-derived value), exactly like <see cref="AbilityCastScenario"/>.</para>
    /// </summary>
    public static class GroundCastScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval = 1 → 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        private const int Caster     = 0; // created first → id 0
        private const int CastAtStep = 0; // issue before the first StepOnce → consumed in tick 1's checksum

        // The ground point the nuke lands on (the Neutral dummy cluster centre), as Fixed world coords.
        private static readonly Fixed GroundX = Fixed.FromInt(10);
        private static readonly Fixed GroundZ = Fixed.FromInt(0);

        /// <summary>ground_nuke: GroundPoint, 60 energy, 8s cd, SearchArea(radius 4, Neutral) → Damage 60 Magic.</summary>
        private static AbilityDefinition GroundNuke() => new AbilityDefinition
        {
            Id = "ground_nuke", DisplayName = "Ground Nuke", Targeting = "GroundPoint",
            CostEnergy = Fixed.FromInt(60), Cooldown = Fixed.FromInt(8),
            EffectGraph = new SearchAreaEffect(Fixed.FromInt(4), TargetFilter.Neutral,
                new DamageEffect(Fixed.FromInt(60), DamageType.Magic)),
        };

        /// <summary>Construct a fresh, fully-wired sim: one far-away P1 caster + three Neutral dummies at the ground point.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] { GroundNuke() });

            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),       // P1 + P2 active (P2 empty → AI no-ops)
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // The caster, well away from the impact so it never auto-acquires the dummies (Idle → no aggro; they are
            // 30+ units away regardless). Ability slot 0 + energy wired directly (integer/Fixed → cross-platform safe).
            int caster = w.Create(V(-40, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("ground_nuke"); // = 0
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.FromInt(60);
            w.Energy[caster]    = Fixed.FromInt(60);

            // Three stationary Neutral dummies clustered inside the 4u nuke radius around (10,0,0). High HP so they
            // survive the single blast (the golden then evolves via the folded cooldown tick-down, not just tick 1).
            w.Create(V(10, 0, 0),  Faction.Neutral, Fixed.FromInt(300), Fixed.FromInt(3));
            w.Create(V(12, 0, 1),  Faction.Neutral, Fixed.FromInt(300), Fixed.FromInt(3));
            w.Create(V(9,  0, -2), Faction.Neutral, Fixed.FromInt(300), Fixed.FromInt(3));

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, caster);
        }

        /// <summary>
        /// Issue the fixed cast schedule for loop index <paramref name="i"/> (run BEFORE <c>StepOnce</c>). A GroundPoint
        /// cast of slot 0: the slot rides the wire's dedicated byte, and the ground point rides TargetX/TargetZ (the two
        /// Fixed coords) — through the SAME shared <see cref="OrderApplier"/> live/replay/offline use.
        /// </summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i == CastAtStep)
            {
                OrderApplier.Apply(host.World,
                    new UnitOrder(Caster, UnitCommand.CastAbility, GroundX, GroundZ, slot: 0),
                    Faction.Player1);
            }
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
