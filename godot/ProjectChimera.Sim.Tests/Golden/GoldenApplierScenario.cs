#nullable enable
using System;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.8b (AC5, D7 — realizes <see cref="GoldenScenario"/>'s <c>TODO(1.8b)</c>): the applier-DRIVEN golden.
    /// Where <see cref="GoldenScenario"/> hand-populates the stores via <c>world.Create</c>, this builds an in-code
    /// <see cref="ScenarioData"/> mirroring <c>alpha_map_01.json</c>, runs it through the 1.7 validator, and applies
    /// it via the net-new Godot-free <see cref="ScenarioApplier"/> (<c>Apply(Validated&lt;ScenarioData&gt;)</c>) — so
    /// the recorded SimChecksum sequence pins the APPLIER's deterministic output, not a hand-populated mirror.
    ///
    /// <para>The scenario is built in code (not loaded from JSON) on purpose: the file path is Godot-coupled, but the
    /// applier is now Godot-free, so the whole record→replay round-trip is Tier-1. The applier reuses the as-built
    /// <c>Fixed.FromFloat</c> load-conversions (D2), so the values are deterministic across runs.</para>
    /// </summary>
    public static class GoldenApplierScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval=1 → 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>The NEW golden file — distinct from the two existing goldens, which are NEVER re-recorded here.</summary>
        public const string GoldenFileName = "golden-applier-scenario.golden.txt";

        /// <summary>Self-identifying header so the file declares its own re-baseline recipe (its own test filter).</summary>
        public static GoldenChecksumReplay.GoldenHeader Header => new(
            "applier-driven golden replay baseline (Story 1.8b)",
            "Pins the SimChecksum sequence for an alpha_map_01-mirroring ScenarioData applied through the Godot-free " +
            "ScenarioApplier (Validated<ScenarioData> -> Apply), stepped via StepOnce at ChecksumInterval=1.",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~GoldenApplier`, " +
            "then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit. NEVER record the existing two goldens.");

        /// <summary>
        /// Construct a fresh, fully-wired sim and apply the in-code scenario through the <see cref="ScenarioApplier"/>.
        /// Allocates brand-new stores/systems on EVERY call (no shared state), so a fresh process reproduces the
        /// committed golden exactly.
        /// </summary>
        public static GoldenHarness Build()
        {
            FactionDefinition faction = BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1; // checksum every tick so a drift's located tick is exact

            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ScenarioData model = BuildModel();
            ValidationResult r = new ScenarioValidator().Validate(model);
            if (!r.Ok)
                throw new InvalidOperationException($"GoldenApplierScenario model failed validation: {r.Error}");
            applier.Apply(r.Value);

            // No perturbation hook is used by this golden, so any valid id satisfies the harness contract; the first
            // unit the applier spawns is entity id 0 (the host itself creates zero entities).
            return new GoldenHarness(host, 0);
        }

        /// <summary>A faction whose worker drives ore gathering so the recorded checksum sequence EVOLVES over time.
        /// Public so Story 1.9a's ServerBootstrapDeterminismTests feeds the EXACT same faction the client-path
        /// applier golden uses (proving the server sim path == the client sim path, not merely a similar one).</summary>
        public static FactionDefinition BuildFaction() => new FactionDefinition
        {
            Id = "alpha", DisplayName = "Alpha",
            Units =
            {
                new UnitDefinition { Id = "worker", DisplayName = "Worker", Category = "Worker", Hp = 50f, Speed = 4f },
            },
        };

        /// <summary>An in-code mirror of alpha_map_01.json: 2 slots, 8 nodes, 2 pre-built CommandCenters, 4 workers.
        /// Public so Story 1.9a's ServerBootstrapDeterminismTests builds its server host from the identical model.</summary>
        public static ScenarioData BuildModel() => new ScenarioData
        {
            Id = "alpha_map_01", DisplayName = "Alpha Skirmish", TerrainRef = "",
            MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://resources/data/factions/alpha_faction.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://resources/data/factions/alpha_faction.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z = -25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z =  25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
            },
            Units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z =  3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z =  3f },
            },
        };
    }
}
