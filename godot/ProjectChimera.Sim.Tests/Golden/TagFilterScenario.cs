using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.11 (AC6.2) — the TAG-FILTER golden scenario. A stationary Player1 caster repeatedly casts a Self
    /// ability whose effect graph exercises ALL THREE tag-consumption shapes over a fixed mixed-tag set of Neutral
    /// units:
    ///   (1) a <c>require_tag: Mechanical</c> SearchArea → Damage (only the Mechanical unit is hit);
    ///   (2) a <c>require_tag: Organic</c> SearchArea → Heal (only the pre-damaged Organic unit is healed);
    ///   (3) a plain SearchArea whose child is a single-target <c>require_tag: Mechanical</c> DAMAGE leaf (D-4) — the
    ///       search fans to EVERY Neutral, but the leaf gate applies the bonus ONLY to the Mechanical unit.
    /// The untagged (None) unit is the control: matched by NO tag predicate, it stays byte-constant (back-compat, AC5).
    ///
    /// Over the run the checksum captures the Mechanical unit's HP dropping each cast (search 1 + the leaf-gated bonus 3),
    /// the pre-damaged Organic unit's HP rising (heal 2), and the None unit untouched — pinning the deterministic
    /// tag-filtered targeting. If the tag check regressed (the leaf gate leaked onto a non-Mechanical unit, or a
    /// require_tag SearchArea were ignored), the recorded sequence would diverge.
    ///
    /// CROSS-PLATFORM SAFE: every target is Neutral (so no Player2 units exist → the float-scoring AI no-ops) and every
    /// hashed field is integer/<see cref="Fixed"/>. The cast is free (0 cost) and recurs on cooldown so the sequence
    /// evolves throughout. NOT Windows-gated — compared on both CI legs (mirrors <see cref="AbilityDomainFilterScenario"/>).
    /// </summary>
    public static class TagFilterScenario
    {
        public const int DefaultTicks = 300;

        private const int Caster = 0;

        /// <summary>A Self ability exercising the three tag shapes (two require_tag SearchAreas + one single-target leaf gate).</summary>
        private static AbilityDefinition TagCounter() => new AbilityDefinition
        {
            Id = "tag_counter", DisplayName = "Tag Counter", Targeting = "Self",
            CostEnergy = Fixed.Zero, Cooldown = Fixed.FromInt(1), // free + short cd → recurs, keeping the sequence non-vacuous
            EffectGraph = new SequenceEffect(new EffectNode[]
            {
                // (1) require_tag SearchArea: only Mechanical Neutrals take this damage.
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Neutral,
                    new DamageEffect(Fixed.FromInt(15), DamageType.Magic), UnitTag.Mechanical),
                // (2) require_tag SearchArea: only Organic Neutrals are healed (the pre-damaged one shows a rising delta).
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Neutral,
                    new HealEffect(Fixed.FromInt(10)), UnitTag.Organic),
                // (3) single-target leaf gate (D-4): the search fans to EVERY Neutral, but the require_tag DAMAGE leaf
                //     applies the bonus ONLY to the Mechanical unit (the Organic + None units no-op at the leaf gate).
                new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Neutral,
                    new DamageEffect(Fixed.FromInt(5), DamageType.Magic, UnitTag.Mechanical)),
            }),
        };

        /// <summary>Construct a fresh, fully-wired sim: one stationary Player1 caster + a mixed-tag Neutral set.</summary>
        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] { TagCounter() });
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
            w.AbilityId[caster * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("tag_counter");
            w.AbilityCount[caster] = 1;
            w.MaxEnergy[caster] = Fixed.FromInt(100);
            w.Energy[caster]    = Fixed.FromInt(100);

            // id 1 — Mechanical Neutral in range. High HP so it survives repeated casts and the sequence keeps evolving.
            int mech = w.Create(V(3, 0, 0), Faction.Neutral, Fixed.FromInt(600), Fixed.FromInt(3));
            w.TagsOf[mech] = UnitTag.Mechanical;

            // id 2 — Organic Neutral, PRE-DAMAGED (Health 100 < MaxHealth 600) so the heal shows a rising delta.
            int organic = w.Create(V(3, 0, 1), Faction.Neutral, Fixed.FromInt(600), Fixed.FromInt(3));
            w.TagsOf[organic] = UnitTag.Organic;
            w.Health[organic] = Fixed.FromInt(100);

            // id 3 — UNTAGGED (None) Neutral control: matched by no tag predicate → stays byte-constant (back-compat AC5).
            w.Create(V(3, 0, 2), Faction.Neutral, Fixed.FromInt(600), Fixed.FromInt(3));

            host.ScenarioDirector.LoadScenario(new ScenarioData()); // mirror MainScene lifecycle (empty → no-op)
            return new GoldenHarness(host, caster);
        }

        /// <summary>Re-cast every 40 ticks (safely &gt; the cooldown) so the tag-filtered effects recur.</summary>
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
