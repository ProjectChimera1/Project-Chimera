#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions; // ContentHash, FactionDefinition, ...
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Effects;          // DirectHpDeltaEffect, SequenceEffect
using Xunit;
using Xunit.Abstractions;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.16 — a LOAD-BUDGET recorder for <see cref="ContentHash.Compute"/> on a max-content fixture (many
    /// factions × full rosters + effect graphs, plus large ability/item registries). ContentHash is computed ONCE
    /// per load (folded into <c>MatchAgreementHash</c> in <c>MainScene</c>), never on the Start button — so this test
    /// EMITS the median compute ms (via <see cref="ITestOutputHelper"/>) and asserts only a GENEROUS ceiling.
    ///
    /// <para><b>Deliberately no tight gate</b> (heeds the <c>CanonicalModelHashPerf</c> CPU-contention flaky lesson —
    /// a lone full-suite fail that passes in isolation is a timing flake, not a regression). The ceiling here is far
    /// above any real content so hardware noise can never red it, while a genuine pathological regression (orders of
    /// magnitude) still trips it and stays visible.</para>
    /// </summary>
    public class ContentHashPerfTests
    {
        private readonly ITestOutputHelper _out;
        public ContentHashPerfTests(ITestOutputHelper output) => _out = output;

        /// <summary>Generous one-time-load ceiling (a real content set folds in well under a millisecond; this only
        /// bounds a pathological regression). Scales via <c>CHIMERA_PERF_CEILING_SCALE</c> on noisy CI runners.</summary>
        private const int LoadCeilingMillis = 500;
        private const int Runs = 5;

        private static readonly double CeilingScale =
            double.TryParse(Environment.GetEnvironmentVariable("CHIMERA_PERF_CEILING_SCALE"),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1.0;

        private static (List<FactionDefinition>, AbilityRegistry, ItemRegistry, DamageTable) BuildMaxContent()
        {
            var factions = new List<FactionDefinition>();
            for (int fi = 0; fi < 8; fi++) // 8 factions
            {
                var units = new List<UnitDefinition>();
                for (int u = 0; u < 24; u++) // 24 units each
                    units.Add(new UnitDefinition
                    {
                        Id = $"f{fi}_unit{u}", Category = "Melee", Hp = 100f + u, AttackDamage = 10f + u,
                        AttackRange = 1f + u * 0.1f, CostOre = 50 + u, CostCrystal = u,
                        Prerequisites = new[] { "barracks" }, Abilities = new[] { $"ab{u % 40}" },
                        Tags = new[] { "Organic" }, AttackDomains = new[] { "Ground" },
                    });
                var buildings = new List<BuildingDefinition>();
                for (int b = 0; b < 12; b++) // 12 buildings each
                    buildings.Add(new BuildingDefinition
                    {
                        Id = $"f{fi}_bld{b}", Category = "Structure", Hp = 800f + b,
                        ConstructionTime = 30f + b, SupplyBonus = b, ProducesCategory = "Melee",
                        AvailableResearch = new[] { $"f{fi}_res{b % 6}" },
                    });
                var research = new List<ResearchDefinition>();
                for (int r = 0; r < 6; r++) // 6 research each
                    research.Add(new ResearchDefinition
                    {
                        Id = $"f{fi}_res{r}", CancelRefundFraction = 0.5f,
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 100 + r }, { "crystal", r } }, TimeTicks = 300 + r, ModifierDelta = new ResearchModifierDelta { AttackDamageDelta = r } },
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 200 + r } }, TimeTicks = 600 + r, ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = r * 10 } },
                        },
                    });
                factions.Add(new FactionDefinition { Id = $"faction{fi}", Units = units, Buildings = buildings, Research = research, StartingOre = 200f });
            }

            var abilities = new List<AbilityDefinition>();
            for (int a = 0; a < 40; a++) // 40 abilities with effect graphs
                abilities.Add(new AbilityDefinition
                {
                    Id = $"ab{a}", Targeting = "TargetUnit", Activation = "active",
                    CostEnergy = Fixed.FromInt(10 + a), Cooldown = Fixed.FromInt(5 + a),
                    EffectGraph = new SequenceEffect(new EffectNode[]
                    {
                        new DirectHpDeltaEffect(Fixed.FromInt(-20 - a)),
                        new HealEffect(Fixed.FromInt(a)),
                    }),
                });

            var items = new List<ItemDefinition>();
            for (int i = 0; i < 40; i++) // 40 items
                items.Add(new ItemDefinition
                {
                    Id = $"item{i}", Charges = i % 3, AttackDamageDelta = Fixed.FromInt(i),
                    EffectGraph = i % 2 == 0 ? new DirectHpDeltaEffect(Fixed.FromInt(-i)) : null,
                });

            return (factions, new AbilityRegistry(abilities), new ItemRegistry(items), DamageTable.Default);
        }

        [Fact]
        public void MaxContent_ComputeMedian_RecordedAndUnderGenerousCeiling()
        {
            (List<FactionDefinition> factions, AbilityRegistry abilities, ItemRegistry items, DamageTable damage) = BuildMaxContent();

            // JIT warm-up so the measured samples pay fold cost, not first-call compilation.
            for (int i = 0; i < 3; i++) Assert.NotEqual(0UL, ContentHash.Compute(factions, abilities, items, damage));

            var samples = new List<double>(Runs);
            for (int i = 0; i < Runs; i++)
            {
                var sw = Stopwatch.StartNew();
                ulong h = ContentHash.Compute(factions, abilities, items, damage);
                sw.Stop();
                Assert.NotEqual(0UL, h);
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            double median = samples[Runs / 2];

            _out.WriteLine($"ContentHash.Compute max-content median-of-{Runs}: {median:F3} ms " +
                           $"({factions.Count} factions, {abilities.Count} abilities, {items.Count} items).");

            Assert.True(median <= LoadCeilingMillis * CeilingScale,
                $"ContentHash.Compute median {median:F3} ms exceeds the generous {LoadCeilingMillis * CeilingScale:F0} ms " +
                "one-time-load ceiling — a pathological regression (this is a load-time compute, never on the Start button).");
        }
    }
}
