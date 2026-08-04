#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core;               // Faction, FactionRegistry
using ProjectChimera.Core.Definitions;   // ScenarioData, ScenarioSerializer, CanonicalModelHash, FactionDefinition
using ProjectChimera.Core.Persistence;   // SaveGameFile, SaveGameState, SaveGameHeaderData
using ProjectChimera.Core.Sim;           // ScenarioApplier
using ProjectChimera.Core.Skirmish;      // SaveGameColdBoot, ColdBootPlan, SkirmishCatalog, SkirmishSetupToScenario
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario (host fixture), NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-465 — cold-boot load-from-menu: <see cref="SaveGameColdBoot.TryPlan"/> must rebuild the saved match's
    /// scenario from nothing but the save file + the shipped catalogs (parse fail-closed → MapId resolution →
    /// <see cref="SkirmishSetupToScenario.Build"/> from the persisted launch record → the CanonicalModelHash gate),
    /// so a save is loadable from the main menu without a running match. Before this, the header's launch record
    /// was persisted but NOTHING consumed it — a save could only be loaded mid-match on the identical scenario.
    /// </summary>
    public class SaveGameColdBootTests
    {
        // ── Fixture: a temp shipped-content tree + a real captured save ─────────────

        private sealed class Fixture : IDisposable
        {
            public string MapsDir { get; }
            public string FactionsDir { get; }
            public IReadOnlyList<MapEntry> Maps => SkirmishCatalog.ScanMaps(MapsDir, "res://maps");
            public IReadOnlyList<FactionEntry> Factions => SkirmishCatalog.ScanFactions(FactionsDir, "res://factions");

            private readonly string _root;

            public Fixture()
            {
                _root = Path.Combine(Path.GetTempPath(), "chimera_coldboot_" + Guid.NewGuid().ToString("N"));
                MapsDir = Path.Combine(_root, "maps");
                FactionsDir = Path.Combine(_root, "factions");
                Directory.CreateDirectory(MapsDir);
                Directory.CreateDirectory(FactionsDir);

                var faction = new FactionDefinition { Id = "alpha", DisplayName = "alpha" };
                faction.Units.Add(new UnitDefinition { Id = "worker", DisplayName = "worker", Category = "Worker", MeshPath = "res://w.glb", Hp = 50f });
                faction.Units.Add(new UnitDefinition { Id = "melee", DisplayName = "melee", Category = "Melee", MeshPath = "res://m.glb", Hp = 50f });
                faction.Buildings.Add(new BuildingDefinition
                {
                    Id = "command_center", DisplayName = "command_center", Category = "Structure",
                    MeshPath = "res://cc.glb", Hp = 100f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Worker",
                });
                File.WriteAllText(Path.Combine(FactionsDir, "alpha_faction.json"),
                    JsonSerializer.Serialize(faction, FactionDefinition.JsonOptions));

                WriteBaseMap(startOre: 200f);
            }

            /// <summary>(Re)write the base map — bumping <paramref name="startOre"/> simulates a post-save map edit.</summary>
            public void WriteBaseMap(float startOre)
            {
                var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
                m.PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://factions/alpha_faction.json", StartOre = startOre, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://factions/alpha_faction.json", StartOre = startOre, BaseX =  45f, BaseZ = 0f },
                };
                m.Units = new[]
                {
                    new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = 0f },
                    new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z = 0f },
                };
                File.WriteAllText(Path.Combine(MapsDir, "m1.json"), ScenarioSerializer.Serialize(m));
            }

            /// <summary>The res:// → temp-file map loader the Godot layer would implement via GlobalizePath.</summary>
            public ScenarioData? LoadMap(string resPath)
                => ScenarioSerializer.LoadFromFile(Path.Combine(MapsDir, Path.GetFileName(resPath)));

            public void Dispose()
            {
                try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            }
        }

        private static SkirmishSetup LaunchRecord(bool withAi = true)
        {
            var s = new SkirmishSetup { MapId = "m1" };
            s.Slots.Add(new SetupSlot { Slot = 0, Kind = SlotKind.Human, FactionId = "alpha" });
            s.Slots.Add(withAi
                ? new SetupSlot { Slot = 1, Kind = SlotKind.Ai, FactionId = "alpha", Ai = AiDifficulty.Hard }
                : new SetupSlot { Slot = 1, Kind = SlotKind.Human, FactionId = "alpha" });
            return s;
        }

        /// <summary>Write a real <c>.chsav</c> blob: a genuinely captured world body under a header whose
        /// CanonicalModelHash stamps the scenario the launch record rebuilds (exactly what IssueSave stamps).</summary>
        private static byte[] SaveBlob(Fixture fx, SkirmishSetup record, out SaveGameHeaderData header)
        {
            // A real captured state (contents are irrelevant to the plan — the body just round-trips).
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            ValidationResult r = new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel());
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            for (int i = 0; i < 10; i++) host.StepOnce();
            var table = CanonicalEffectDescriptorTable.Build(host.AbilityRegistry, host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(host, table);

            ScenarioData baseMap = fx.LoadMap("res://maps/m1.json")!;
            ScenarioData built = SkirmishSetupToScenario.Build(record, baseMap, fx.Factions);
            header = new SaveGameHeaderData
            {
                CanonicalModelHash = CanonicalModelHash.Compute(built),
                ContentHash        = 0xC0FFEEUL, // the content gate is the Godot layer's half — inert here
                Tick               = host.CurrentTick,
                MapId              = record.MapId,
                Slots              = record.Slots,
            };
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, header);
            return ms.ToArray();
        }

        // ── Tests ───────────────────────────────────────────────────────────────────

        [Fact]
        public void TryPlan_RebuildsTheSavedScenario_FromTheHeaderLaunchRecord()
        {
            using var fx = new Fixture();
            byte[] blob = SaveBlob(fx, LaunchRecord(), out SaveGameHeaderData written);

            string? err = SaveGameColdBoot.TryPlan(blob, "0", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan);

            Assert.Null(err);
            Assert.NotNull(plan);
            // The rebuilt scenario is IDENTICAL to the one the original launch built (the round-trip contract).
            ScenarioData expected = SkirmishSetupToScenario.Build(
                written.ToSkirmishSetup(), fx.LoadMap("res://maps/m1.json")!, fx.Factions);
            Assert.Equal(ScenarioSerializer.Serialize(expected), ScenarioSerializer.Serialize(plan!.Built));
            Assert.Equal(CanonicalModelHash.Compute(plan.Built), plan.Header.CanonicalModelHash);
            Assert.Equal(written.Tick, plan.Header.Tick);
            Assert.NotNull(plan.State);
            Assert.Equal("m1", plan.Setup.MapId);
        }

        [Fact]
        public void TryPlan_AiDifficulty_ComesFromTheLaunchRecordsAiSlot()
        {
            using var fx = new Fixture();
            byte[] blob = SaveBlob(fx, LaunchRecord(withAi: true), out _);

            Assert.Null(SaveGameColdBoot.TryPlan(blob, "0", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan));
            Assert.Equal(AiDifficulty.Hard, plan!.AiLevel);
        }

        [Fact]
        public void TryPlan_NoAiSlotInTheRecord_DefaultsToNormal()
        {
            using var fx = new Fixture();
            byte[] blob = SaveBlob(fx, LaunchRecord(withAi: false), out _);

            Assert.Null(SaveGameColdBoot.TryPlan(blob, "0", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan));
            Assert.Equal(AiDifficulty.Normal, plan!.AiLevel);
        }

        [Fact]
        public void TryPlan_MapNoLongerInstalled_RejectsLocated()
        {
            using var fx = new Fixture();
            SkirmishSetup record = LaunchRecord();
            record.MapId = "ghost_map";
            byte[] blob = SaveBlob(fx, record, out _);

            string? err = SaveGameColdBoot.TryPlan(blob, "2", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan);

            Assert.Null(plan);
            Assert.NotNull(err);
            Assert.Contains("ghost_map", err);
            Assert.Contains("no longer installed", err);
        }

        [Fact]
        public void TryPlan_MapEditedSinceTheSave_RejectsOnTheModelHashGate()
        {
            using var fx = new Fixture();
            byte[] blob = SaveBlob(fx, LaunchRecord(), out _);

            fx.WriteBaseMap(startOre: 500f); // the map was retuned after the save — a DIFFERENT scenario now rebuilds

            string? err = SaveGameColdBoot.TryPlan(blob, "0", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan);

            Assert.Null(plan);
            Assert.NotNull(err);
            Assert.Contains("has changed", err);
        }

        [Fact]
        public void TryPlan_CorruptBytes_RejectFailClosed()
        {
            using var fx = new Fixture();

            string? err = SaveGameColdBoot.TryPlan(
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "1", fx.Maps, fx.Factions, fx.LoadMap, out ColdBootPlan? plan);

            Assert.Null(plan);
            Assert.NotNull(err);
            Assert.Contains("not a Chimera save file", err);
        }

        [Fact]
        public void TryPlan_MapFileUnreadable_RejectsLocated()
        {
            using var fx = new Fixture();
            byte[] blob = SaveBlob(fx, LaunchRecord(), out _);

            string? err = SaveGameColdBoot.TryPlan(blob, "0", fx.Maps, fx.Factions, _ => null, out ColdBootPlan? plan);

            Assert.Null(plan);
            Assert.NotNull(err);
            Assert.Contains("could not be loaded", err);
        }
    }
}
