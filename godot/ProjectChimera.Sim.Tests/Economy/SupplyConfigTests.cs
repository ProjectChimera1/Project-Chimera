#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Sim.Tests.Golden; // GoldenApplierScenario
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 4.4 — the data-driven per-scenario supply/cap model: <see cref="SupplyConfig"/> (authored) resolved via
    /// <see cref="ResourceStore.ConfigureSupply"/> into <see cref="ResourceStore.StartingSupplyCap"/>/
    /// <see cref="ResourceStore.SupplyHardCeiling"/>/<see cref="ResourceStore.SupplyGatingEnabled"/>, read by
    /// <see cref="BuildingSystem"/>'s per-tick <c>RecalculateSupplyCaps</c> and <see cref="ResourceStore.HasSupply"/>'s
    /// gate. Proves: null-means-default resolution, the <c>Clear()</c> reset, the starting-cap/hard-ceiling/gating
    /// I/O matrix, the <see cref="ScenarioApplier"/> wiring, and the Edit↔Play reset round-trip invariant.
    /// </summary>
    public class SupplyConfigTests
    {
        // ── ResourceStore.ConfigureSupply — resolution ──────────────────────────────────────────────────────────

        [Fact]
        public void ConfigureSupply_NullConfig_ResolvesCompileDefaults()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(null);
            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, resources.StartingSupplyCap);
            Assert.Null(resources.SupplyHardCeiling);
            Assert.True(resources.SupplyGatingEnabled);
        }

        [Fact]
        public void ConfigureSupply_ExplicitConfig_ResolvesGivenValues()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 30, HardCeiling = 45, Enabled = false });
            Assert.Equal(30, resources.StartingSupplyCap);
            Assert.Equal(45, resources.SupplyHardCeiling);
            Assert.False(resources.SupplyGatingEnabled);
        }

        [Fact]
        public void ConfigureSupply_ExplicitAllDefaultConfig_ResolvesSameAsNull()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig
            {
                StartingCap = ResourceStore.STARTING_SUPPLY_CAP,
                HardCeiling = null,
                Enabled = true,
            });
            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, resources.StartingSupplyCap);
            Assert.Null(resources.SupplyHardCeiling);
            Assert.True(resources.SupplyGatingEnabled);
        }

        [Fact]
        public void ConfigureSupply_NegativeAuthoredValues_ClampToZero()
        {
            // Review-pass-2 regression: ScenarioValidator rejects a negative StartingCap/HardCeiling at import, but
            // this codebase's DEFAULT posture is shadow mode (fail-closed is opt-in), so a rejected model can still
            // reach ConfigureSupply. Defensive clamping (mirroring RevivalRuleRuntime.LinearSat's own precedent)
            // guarantees a negative authored value can never produce a negative runtime SupplyCap.
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = -5, HardCeiling = -1, Enabled = true });
            Assert.Equal(0, resources.StartingSupplyCap);
            Assert.Equal(0, resources.SupplyHardCeiling);
        }

        // ── ResourceStore.Clear() — resets the three supply-config properties to compile defaults ──────────────

        [Fact]
        public void Clear_ResetsSupplyConfigToCompileDefaults()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 99, HardCeiling = 5, Enabled = false });
            resources.Clear();
            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, resources.StartingSupplyCap);
            Assert.Null(resources.SupplyHardCeiling);
            Assert.True(resources.SupplyGatingEnabled);
        }

        // ── ResourceStore.HasSupply — the gating bypass ─────────────────────────────────────────────────────────

        [Fact]
        public void HasSupply_GatingEnabled_BlocksAtCap()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.SupplyCap[(int)Faction.Player1]  = 10;
            resources.SupplyUsed[(int)Faction.Player1] = 10;
            Assert.False(resources.HasSupply(Faction.Player1));
        }

        [Fact]
        public void HasSupply_GatingDisabled_NeverBlocked_EvenAtOrAboveCap()
        {
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { Enabled = false });
            resources.SupplyCap[(int)Faction.Player1]  = 10;
            resources.SupplyUsed[(int)Faction.Player1] = 25; // well above cap
            Assert.True(resources.HasSupply(Faction.Player1));
        }

        // ── BuildingSystem.RecalculateSupplyCaps — starting-cap / hard-ceiling I/O matrix ─────────────────────────

        [Fact]
        public void RecalculateSupplyCaps_OmittedConfig_UsesCompileDefault_10()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var sys = new BuildingSystem(buildings, resources);
            sys.Tick(new EntityWorld(), Fixed.Zero);
            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, resources.SupplyCap[(int)Faction.Player1]);
            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, resources.SupplyCap[(int)Faction.Player2]);
        }

        [Fact]
        public void RecalculateSupplyCaps_AuthoredStartingCap_SeedsEachFaction()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 30 });
            var sys = new BuildingSystem(buildings, resources);
            sys.Tick(new EntityWorld(), Fixed.Zero);
            Assert.Equal(30, resources.SupplyCap[(int)Faction.Player1]);
            Assert.Equal(30, resources.SupplyCap[(int)Faction.Player2]);
        }

        [Fact]
        public void RecalculateSupplyCaps_StartingCapPlusBuildingBonuses_Uncapped_WhenNoHardCeiling()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 10 });
            var sys = new BuildingSystem(buildings, resources);
            int b = sys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            buildings.SupplyBonus[b] = 30; // pushes total to 40

            sys.Tick(new EntityWorld(), Fixed.Zero);
            Assert.Equal(40, resources.SupplyCap[(int)Faction.Player1]);
        }

        [Fact]
        public void RecalculateSupplyCaps_HardCeilingBelowBuildingBonusTotal_ClampsEveryTick()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 10, HardCeiling = 15 });
            var sys = new BuildingSystem(buildings, resources);
            int b = sys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            buildings.SupplyBonus[b] = 30; // 10 + 30 = 40, but ceiling is 15

            var world = new EntityWorld();
            sys.Tick(world, Fixed.Zero);
            Assert.Equal(15, resources.SupplyCap[(int)Faction.Player1]);

            // ...and stays clamped on every subsequent tick, not just the first.
            sys.Tick(world, Fixed.Zero);
            Assert.Equal(15, resources.SupplyCap[(int)Faction.Player1]);
        }

        [Fact]
        public void RecalculateSupplyCaps_HardCeiling_ClampsUnconditionallyOfGatingEnabled()
        {
            // The ceiling shapes the cap VALUE regardless of whether the gate enforces it (spec Design Notes) —
            // toggling `enabled` must not silently change the displayed/computed cap too.
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 10, HardCeiling = 15, Enabled = false });
            var sys = new BuildingSystem(buildings, resources);
            int b = sys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            buildings.SupplyBonus[b] = 30;

            sys.Tick(new EntityWorld(), Fixed.Zero);
            Assert.Equal(15, resources.SupplyCap[(int)Faction.Player1]);
        }

        // ── BuildingSystem.TrainUnit — ceiling-block / gating-bypass ────────────────────────────────────────────

        private static FactionDefinition OneUnitFaction() => new FactionDefinition
        {
            Id = "supply_test", DisplayName = "SupplyTest",
            Units = { new UnitDefinition { Id = "grunt", Category = "Melee", Hp = 10f, Supply = 1 } },
        };

        [Fact]
        public void TrainUnit_BlocksOnceSupplyUsedReachesHardCeiling()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 10, HardCeiling = 15 });
            var sys = new BuildingSystem(buildings, resources, OneUnitFaction());
            // Barracks (not CommandCenter — TrainUnit unconditionally rejects CommandCenter) trains the "Melee"
            // category, matching OneUnitFaction's unit.
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            buildings.SupplyBonus[b] = 30; // building bonus would push cap to 40, but ceiling is 15

            sys.Tick(new EntityWorld(), Fixed.Zero); // resolve SupplyCap = 15
            resources.SupplyUsed[(int)Faction.Player1] = 15; // at the ceiling

            Assert.False(sys.TrainUnit(b, resources));
        }

        [Fact]
        public void TrainUnit_GatingDisabled_NeverBlockedOnSupply_EvenAtOrAboveCap()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.ConfigureSupply(new SupplyConfig { StartingCap = 10, Enabled = false });
            var sys = new BuildingSystem(buildings, resources, OneUnitFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            sys.Tick(new EntityWorld(), Fixed.Zero); // resolve SupplyCap = 10
            resources.SupplyUsed[(int)Faction.Player1] = 25; // well above cap

            Assert.True(sys.TrainUnit(b, resources));
        }

        // ── ScenarioApplier.Apply — resolution wiring ───────────────────────────────────────────────────────────

        private static (SimulationHost host, ScenarioApplier applier) BuildHostAndApplier()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        private static void ApplyValidated(ScenarioApplier applier, ScenarioData model)
        {
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
        }

        [Fact]
        public void Apply_OmittedSupply_ResolvesCompileDefaults()
        {
            var (host, applier) = BuildHostAndApplier();
            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Supply = null;
            ApplyValidated(applier, model);

            Assert.Equal(ResourceStore.STARTING_SUPPLY_CAP, host.Resources.StartingSupplyCap);
            Assert.Null(host.Resources.SupplyHardCeiling);
            Assert.True(host.Resources.SupplyGatingEnabled);
        }

        [Fact]
        public void Apply_AuthoredSupply_ResolvesThroughToResourceStore()
        {
            var (host, applier) = BuildHostAndApplier();
            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Supply = new SupplyConfig { StartingCap = 25, HardCeiling = 40, Enabled = false };
            ApplyValidated(applier, model);

            Assert.Equal(25, host.Resources.StartingSupplyCap);
            Assert.Equal(40, host.Resources.SupplyHardCeiling);
            Assert.False(host.Resources.SupplyGatingEnabled);
        }

        // ── ClearForReset → Apply round-trip byte-equality ──────────────────────────────────────────────────────

        [Fact]
        public void ClearForResetThenApply_OmittedSupply_ByteIdenticalToFreshLoad()
        {
            var (host, applier) = BuildHostAndApplier();
            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Supply = null;

            ApplyValidated(applier, model);
            host.ClearForReset();
            ApplyValidated(applier, model); // re-apply the SAME model after reset

            var (freshHost, freshApplier) = BuildHostAndApplier();
            ApplyValidated(freshApplier, model);

            Assert.Equal(freshHost.Resources.StartingSupplyCap,   host.Resources.StartingSupplyCap);
            Assert.Equal(freshHost.Resources.SupplyHardCeiling,   host.Resources.SupplyHardCeiling);
            Assert.Equal(freshHost.Resources.SupplyGatingEnabled, host.Resources.SupplyGatingEnabled);
            Assert.Equal(freshHost.Resources.SupplyCap,           host.Resources.SupplyCap);
        }

        [Fact]
        public void ClearForResetThenApply_AuthoredSupply_ByteIdenticalToFreshLoad()
        {
            var (host, applier) = BuildHostAndApplier();
            ScenarioData model = GoldenApplierScenario.BuildModel();
            model.Supply = new SupplyConfig { StartingCap = 22, HardCeiling = 33, Enabled = false };

            ApplyValidated(applier, model);
            host.ClearForReset();
            ApplyValidated(applier, model); // re-apply the SAME model after reset

            var (freshHost, freshApplier) = BuildHostAndApplier();
            ApplyValidated(freshApplier, model);

            Assert.Equal(freshHost.Resources.StartingSupplyCap,   host.Resources.StartingSupplyCap);
            Assert.Equal(freshHost.Resources.SupplyHardCeiling,   host.Resources.SupplyHardCeiling);
            Assert.Equal(freshHost.Resources.SupplyGatingEnabled, host.Resources.SupplyGatingEnabled);
            Assert.Equal(freshHost.Resources.SupplyCap,           host.Resources.SupplyCap);
        }
    }
}
