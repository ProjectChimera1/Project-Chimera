#nullable enable
using ProjectChimera.Core;              // Faction, Fixed, FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioData & co, RevivalRule, SupplyConfig, ScenarioValidator, Validated<T>
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Builder
{
    /// <summary>
    /// DW-361 (Story 7.7 follow-up) — pins the documented behavior change Story 7.7 made in
    /// <see cref="ScenarioApplier.Apply"/>: the <c>RevivalRuntime.Configure</c> / <c>Resources.ConfigureSupply</c>
    /// calls sit BELOW the null-model guard, so consuming a failed/default <see cref="Validated{T}"/> token is a
    /// PURE no-op — it must NOT reset an already-configured revival/supply baseline back to defaults.
    ///
    /// The existing <c>Apply_MalformedRegions_…</c> test proves the failed-token apply doesn't throw and applies
    /// nothing, but it never establishes a NON-DEFAULT baseline first — so moving the two Configure calls back
    /// above the guard (null-tolerantly, the pre-7.7 clobber shape) kept every test green. This test turns that
    /// regression RED: configure non-default revival + supply through a real validated Apply, consume a
    /// <c>default</c> token, and assert BOTH survive unchanged.
    /// </summary>
    public class ScenarioApplierFailedTokenTests
    {
        private static FactionDefinition AlphaFaction() => new FactionDefinition
        {
            Id = "alpha", DisplayName = "Alpha",
        };

        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;
            return defs;
        }

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            var faction = AlphaFaction();
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, SlotDefs(faction));
            return (host, applier);
        }

        /// <summary>A minimal valid model whose revival rule AND supply config are non-default in every field the
        /// runtimes expose — so a silent reset to <see cref="RevivalRule.Default"/> / compile supply defaults is
        /// observable on each assertion below.</summary>
        private static ScenarioData ModelWithNonDefaultRevivalAndSupply() => new ScenarioData
        {
            Id = "cfgmap", DisplayName = "Config Map", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            RevivalRule = new RevivalRule
            {
                Enabled             = false,   // default: true
                CostOreBase         = 777,     // default: 100
                CostOrePerLevel     = 11,      // default: 25
                CostCrystalBase     = 33,      // default: 0
                CostCrystalPerLevel = 5,       // default: 0
                TimeBaseSeconds     = 42f,     // default: 10
                TimePerLevelSeconds = 3f,      // default: 2
                ReviveHpFraction    = 0.25f,   // default: 0.5 (0.25 is exact in 16.16)
            },
            Supply = new SupplyConfig
            {
                StartingCap = 42,              // default: ResourceStore.STARTING_SUPPLY_CAP (10)
                HardCeiling = 137,             // default: null (uncapped)
                Enabled     = false,           // default: true (gating on)
            },
        };

        [Fact]
        public void Apply_DefaultToken_DoesNotClobberTheConfiguredRevivalAndSupplyBaseline()
        {
            var (host, applier) = NewHostAndApplier();

            // 1. Establish the non-default baseline through the REAL pipeline: validate → Apply.
            ValidationResult r = new ScenarioValidator().Validate(ModelWithNonDefaultRevivalAndSupply());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // Baseline sanity — the authored config took effect (float→Fixed resolved once, at apply).
            Assert.False(host.RevivalRuntime.Enabled);
            Assert.Equal(Fixed.FromInt(777), host.RevivalRuntime.CostOre(0));
            Assert.Equal(42,   host.Resources.StartingSupplyCap);
            Assert.Equal(137,  host.Resources.SupplyHardCeiling);
            Assert.False(host.Resources.SupplyGatingEnabled);

            // 2. Consume a DEFAULT token — the exact shape a failed validation hands back (null model).
            var ex = Record.Exception(() => applier.Apply(default(Validated<ScenarioData>)));
            Assert.Null(ex); // the null-model guard: a logged skip, never a throw

            // 3. The 7.7 contract: the failed-token apply was a PURE no-op — every non-default value SURVIVES.
            //    (Pre-7.7, Configure/ConfigureSupply ran before the guard, so this consumed token silently reset
            //    revival to RevivalRule.Default and supply to compile defaults — each assert below caught that.)
            Assert.False(host.RevivalRuntime.Enabled);                              // would reset to true
            Assert.Equal(Fixed.FromInt(777),      host.RevivalRuntime.CostOre(0));  // would reset to 100
            Assert.Equal(Fixed.FromInt(777 + 11), host.RevivalRuntime.CostOre(1));  // per-level 11, not 25
            Assert.Equal(Fixed.FromInt(33),       host.RevivalRuntime.CostCrystal(0));      // would reset to 0
            Assert.Equal(Fixed.FromInt(33 + 5),   host.RevivalRuntime.CostCrystal(1));
            Assert.Equal(Fixed.FromInt(42),       host.RevivalRuntime.TimeSeconds(0));      // would reset to 10
            Assert.Equal(Fixed.FromInt(42 + 3),   host.RevivalRuntime.TimeSeconds(1));
            Assert.Equal(Fixed.FromFloat(0.25f),  host.RevivalRuntime.HpFraction);          // would reset to 0.5

            Assert.Equal(42,  host.Resources.StartingSupplyCap);   // would reset to 10
            Assert.Equal(137, host.Resources.SupplyHardCeiling);   // would reset to null (uncapped)
            Assert.False(host.Resources.SupplyGatingEnabled);      // would reset to true
        }
    }
}
