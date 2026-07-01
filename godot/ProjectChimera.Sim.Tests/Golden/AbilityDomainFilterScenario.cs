using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.9a (AC6 / AC3.4) — the ABILITY-DOMAIN-FILTER golden scenario. A stationary Player1 caster repeatedly
    /// casts a Self ability whose effect is a <c>SearchArea(filter = Air)</c> → Damage: the domain filter (now
    /// EVALUATED by <c>TargetMatcher</c> via the shared <c>DomainClassifier</c>) means the AoE damages ONLY the flier
    /// in range and SPARES the co-located ground unit. Over the run the checksum captures the flier's health dropping
    /// each cast while the ground unit stays untouched — pinning the deterministic filtered targeting. If the domain
    /// check regressed (hitting the ground unit too), the sequence would diverge.
    ///
    /// CROSS-PLATFORM SAFE: both candidates are Neutral (allegiance-agnostic <c>Air</c> filter, so no Enemy bit is
    /// needed) and Player2 is EMPTY → the float-scoring AI no-ops; every hashed field is integer/<see cref="Fixed"/>.
    /// The cast is free (0 cost) and recurs on cooldown so the sequence evolves throughout. NOT Windows-gated.
    /// </summary>
    public static class AbilityDomainFilterScenario
    {
        public const int DefaultTicks = 300;

        private const int Caster = 0;

        /// <summary>A Self ability whose effect fans a domain-filtered SearchArea from the caster: Air-only AoE damage.</summary>
        private static AbilityDefinition DomainNuke() => new AbilityDefinition
        {
            Id = "domain_nuke", DisplayName = "Domain Nuke", Targeting = "Self",
            CostEnergy = Fixed.Zero, Cooldown = Fixed.FromInt(1), // free + short cd → recurs, keeping the sequence non-vacuous
            EffectGraph = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Air,
                              new DamageEffect(Fixed.FromInt(20), DamageType.Magic)),
        };

        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] { DomainNuke() });
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                registry: registry);
            host.ChecksumInterval = 1;

            EntityWorld w = host.World;

            // id 0 — the caster (Player1). Ability slot 0 + energy wired directly (integer/Fixed → cross-platform safe).
            int caster = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("domain_nuke");
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.FromInt(100);
            w.Energy[caster]    = Fixed.FromInt(100);

            // id 1 — a flier in range (Neutral, CategoryOf = Air) — the ONLY valid target of the Air filter. High HP so
            //         it survives repeated casts and the sequence keeps evolving.
            int air = w.Create(V(3, 0, 0), Faction.Neutral, Fixed.FromInt(600), Fixed.FromInt(3));
            w.CategoryOf[air] = UnitCategory.Air;

            // id 2 — a co-located ground unit (Neutral, CategoryOf = Melee). MUST be spared by the Air filter.
            w.Create(V(3, 0, 1), Faction.Neutral, Fixed.FromInt(600), Fixed.FromInt(3));

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, caster);
        }

        /// <summary>Re-cast every 40 ticks (safely &gt; the 30-tick cooldown) so the domain-filtered AoE recurs.</summary>
        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i % 40 == 0)
            {
                OrderApplier.Apply(host.World,
                    new UnitOrder(Caster, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                    Faction.Player1);
            }
        }

        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
}
