#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-329 — Tier-1 teeth for <see cref="MapWritePipeline"/>, the ordered Godot-free write sequence the editor's
    /// map Export (<c>WinConditionPhase.ExportMapPackage</c>) and New-Map (<c>WinConditionPhase.CreateNewMap</c>)
    /// paths route through. <see cref="MapWriteGateTests"/> pins the GATE DECISION; these tests pin the ORDERING
    /// property the DW-164 defect violated and DW-329 found untested: the hard gate runs BEFORE any write delegate,
    /// and on a rejection NOTHING fires — no terrain region files (the export sequence's first disk write), no
    /// scenario.json overwrite, no .chimera.zip.
    ///
    /// RED-teeth proof: reorder <c>RunExportAsync</c>'s gate below the <c>saveTerrain()</c> call (the pre-14.7
    /// write-then-validate shape) and the rejection tests below turn RED (a delegate fired); delete the abort and
    /// they turn RED too. The delegates here only record — the pipeline owns order/abort, the phase owns IO.
    /// </summary>
    public class MapWritePipelineTests
    {
        /// <summary>A minimal VALID model (the <see cref="MapWriteGateTests"/> fixture shape).</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
        };

        /// <summary>The same model made gate-rejecting: a resource node stranded past MapBounds — the exact class
        /// that hard-fails CheckCoord on reload (the DW-164 unloadable-export defect).</summary>
        private static ScenarioData RejectedModel()
        {
            var m = ValidModel();
            m.ResourceNodes[0].X = 200f; // inside the Fixed range but outside map_bounds 120
            return m;
        }

        // ── Export path: gate → terrain-save → scenario-save → pack ─────────────────────────────────────────────

        [Fact]
        public async Task Export_GateRejection_FiresNoWriteDelegate_AndReturnsTheLocatedError()
        {
            var calls = new List<string>();
            string? verdict = await MapWritePipeline.RunExportAsync(
                RejectedModel(), null,
                saveTerrain:  () => { calls.Add("terrain");  return "terrain-dir"; },
                saveScenario: () => { calls.Add("scenario"); return true; },
                packAsync:    _  => { calls.Add("pack");     return Task.CompletedTask; });

            Assert.NotNull(verdict);                       // the located error surfaces to the caller…
            Assert.Contains("map_bounds", verdict!);
            Assert.Contains("resource_nodes[0].x", verdict!);
            Assert.Empty(calls);                           // …and NOTHING was written — the DW-164/DW-329 property.
        }

        [Fact]
        public async Task Export_ValidScenario_RunsTerrainThenScenarioThenPack_ThreadingTheTerrainDir()
        {
            var calls = new List<string>();
            string? packedTerrainDir = "unset";
            string? verdict = await MapWritePipeline.RunExportAsync(
                ValidModel(), null,
                saveTerrain:  () => { calls.Add("terrain");  return "terrain-dir"; },
                saveScenario: () => { calls.Add("scenario"); return true; },
                packAsync:    dir => { calls.Add("pack"); packedTerrainDir = dir; return Task.CompletedTask; });

            Assert.Null(verdict);
            // The as-built Story 6.2 order: terrain BEFORE scenario-save (TerrainRef is stamped so the saved JSON
            // carries it), pack last. Reordering any pair is a regression.
            Assert.Equal(new[] { "terrain", "scenario", "pack" }, calls);
            Assert.Equal("terrain-dir", packedTerrainDir); // step 2's result reaches the pack stage
        }

        [Fact]
        public async Task Export_ScenarioSaveAborts_PackNeverFires()
        {
            var calls = new List<string>();
            string? verdict = await MapWritePipeline.RunExportAsync(
                ValidModel(), null,
                saveTerrain:  () => { calls.Add("terrain");  return null; },
                saveScenario: () => { calls.Add("scenario"); return false; }, // save failed; delegate surfaced it
                packAsync:    _  => { calls.Add("pack");     return Task.CompletedTask; });

            Assert.Null(verdict); // not a gate rejection — the save delegate owned its own failure surfacing
            Assert.Equal(new[] { "terrain", "scenario" }, calls); // no package from a scenario that failed to save
        }

        [Fact]
        public async Task Export_ThreadsSlotFactionDefsToTheGate()
        {
            // A pre-placed CUSTOM building resolves only through the owner faction's defs (the Export path forwards
            // SceneContext.SlotFactionDefs). The SAME model must be REJECTED with null defs yet run the full
            // sequence once the defs are threaded — proving the pipeline forwards them to MapWriteGate.Check
            // (mirrors MapWriteGateTests' two-arg pair; fixture shape from there).
            var faction = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            faction.Buildings.Add(new BuildingDefinition
            {
                Id = "watchtower", DisplayName = "Watch Tower", Category = "Structure",
                Hp = 150f, ConstructionTime = 8f, SupplyBonus = 3, ProducesCategory = "Melee",
            });
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;

            var model = new ScenarioData
            {
                MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
                ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
                Buildings = new[] { new ScenarioBuilding { Type = "watchtower", Slot = 0, X = -40f, Z = 5f, PreBuilt = true } },
                Units = System.Array.Empty<ScenarioUnit>(),
            };

            int fired = 0;
            System.Func<string?> terrain = () => { fired++; return null; };
            System.Func<bool> save = () => { fired++; return true; };
            System.Func<string?, Task> pack = _ => { fired++; return Task.CompletedTask; };

            string? withoutDefs = await MapWritePipeline.RunExportAsync(model, null, terrain, save, pack);
            Assert.NotNull(withoutDefs); // custom id unresolvable enum-name-only → blocked, nothing fired
            Assert.Equal(0, fired);

            string? withDefs = await MapWritePipeline.RunExportAsync(model, defs, terrain, save, pack);
            Assert.Null(withDefs);       // defs threaded → resolvable → the full sequence ran
            Assert.Equal(3, fired);
        }

        // ── New-Map path: gate → scenario-save ──────────────────────────────────────────────────────────────────

        [Fact]
        public void NewMap_GateRejection_SaveNeverFires()
        {
            int saves = 0;
            string? verdict = MapWritePipeline.RunNewMap(RejectedModel(), () => saves++);

            Assert.NotNull(verdict);
            Assert.Contains("resource_nodes[0].x", verdict!);
            Assert.Equal(0, saves); // a rejected blank never reaches ScenarioSerializer.SaveToFile
        }

        [Fact]
        public void NewMap_ValidScenario_SavesExactlyOnce()
        {
            int saves = 0;
            string? verdict = MapWritePipeline.RunNewMap(ValidModel(), () => saves++);

            Assert.Null(verdict);
            Assert.Equal(1, saves);
        }
    }
}
