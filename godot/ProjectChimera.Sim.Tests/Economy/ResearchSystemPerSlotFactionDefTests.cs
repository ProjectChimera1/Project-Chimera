#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-386 — <see cref="ResearchSystem"/>'s per-faction definition array has been sized
    /// <see cref="FactionRegistry.FACTION_ARRAY_SIZE"/> (9) since Story 9.2, but its constructor only ever populated
    /// Player1/Player2 and — unlike <see cref="BuildingSystem"/> — it had NO runtime per-slot override at all. So a
    /// Player3..Player8 researcher resolved a NULL faction def and had no research options whatsoever, while the SAME
    /// slot's buildings resolved correctly because <c>ScenarioApplier</c> threaded the def into
    /// <c>BuildingSystem.SetFactionDef</c>. That asymmetry — one system wired, its twin not — is the defect.
    ///
    /// <para><b>Determinism note.</b> The new seam is assignment-only, exactly like
    /// <c>BuildingSystem.SetFactionDef</c>. <c>ResearchStore</c>'s per-faction row LENGTH is folded into
    /// <c>SimChecksum</c> (v14), and every consumer already grows it lazily, so an eager
    /// <c>EnsureCapacity</c> here would move the fold for any slot whose def carries research and buy nothing. The
    /// last test pins that the applier leaves those rows untouched.</para>
    /// </summary>
    public class ResearchSystemPerSlotFactionDefTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A faction whose lab offers one cheap, fast research — enough to prove a high slot can actually
        /// START research once its def is threaded.</summary>
        private static FactionDefinition ResearchFaction(string id) => new FactionDefinition
        {
            Id = id, DisplayName = id,
            Units = new List<UnitDefinition>
            {
                new UnitDefinition { Id = "worker", DisplayName = "worker", Category = "Worker",
                                     MeshPath = "res://assets/worker.glb", Hp = 50f },
            },
            Buildings = new List<BuildingDefinition>
            {
                new BuildingDefinition
                {
                    Id = "lab", DisplayName = "lab", Category = "Structure",
                    MeshPath = "res://assets/lab.glb", Hp = 100f, ConstructionTime = 10f,
                    SupplyBonus = 0, ProducesCategory = "Worker",
                    AvailableResearch = new[] { "armor_up" },
                },
            },
            Research = new List<ResearchDefinition>
            {
                new ResearchDefinition
                {
                    Id = "armor_up", Prerequisites = Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 100 } }, TimeTicks = 5,
                                            ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f } },
                    },
                },
            },
        };

        /// <summary>A 3-slot model; slot 2 (⇒ Player3) is the one the ctor never populates.</summary>
        private static ScenarioData ThreeSlotModel() => new ScenarioData
        {
            Id = "m", DisplayName = "Map", TerrainRef = "",
            MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://a.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 2, FactionJson = "res://a.json", StartOre = 500f, BaseX =   0f, BaseZ = 40f },
            },
            ResourceNodes = Array.Empty<ScenarioResourceNode>(),
            Buildings     = Array.Empty<ScenarioBuilding>(),
            Units         = Array.Empty<ScenarioUnit>(),
            Triggers      = Array.Empty<TriggerDefinition>(),
        };

        private static (SimulationHost host, ScenarioApplier applier, FactionDefinition slot3Def) NewThreeSlotSim()
        {
            FactionDefinition shared = ResearchFaction("shared");
            FactionDefinition slot3  = ResearchFaction("slot3");   // a DISTINCT instance, so identity is meaningful

            var slotDefs = new FactionDefinition?[FactionRegistry.SLOT_DEFINITIONS_SIZE];
            slotDefs[(int)Faction.Player1] = shared;
            slotDefs[(int)Faction.Player2] = shared;
            slotDefs[(int)Faction.Player3] = slot3;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(3), shared, shared);
            return (host, new ScenarioApplier(host, NullLogSink.Instance, slotDefs), slot3);
        }

        private static Validated<ScenarioData> Gate(ScenarioData m)
        {
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
            return r.Value;
        }

        // ── The seam itself ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SetFactionDef_ResolvesAHighSlotThatTheConstructorNeverPopulates()
        {
            FactionDefinition def = ResearchFaction("f");
            var sys = new ResearchSystem(new BuildingStore(), new ResourceStore(Fixed.Zero),
                                         new ResearchStore(), NewModifierStore());

            Assert.Null(sys.GetFactionDefinition(Faction.Player5));   // the pre-fix state for EVERY slot past 2

            sys.SetFactionDef(Faction.Player5, def);

            Assert.Same(def, sys.GetFactionDefinition(Faction.Player5));
        }

        [Fact]
        public void SetFactionDef_OutOfRangeFaction_IsASilentNoOp()
        {
            var sys = new ResearchSystem(new BuildingStore(), new ResourceStore(Fixed.Zero),
                                         new ResearchStore(), NewModifierStore());

            sys.SetFactionDef((Faction)99, ResearchFaction("f"));      // must not throw (mirrors BuildingSystem's guard)

            Assert.Null(sys.GetFactionDefinition(Faction.Player8));
        }

        // ── The applier wiring (the actual DW-386 gap) ──────────────────────────────────────────────────

        [Fact]
        public void Apply_ThreadsThePerSlotFactionDef_IntoResearchSystem_NotOnlyBuildingSystem()
        {
            var (host, applier, slot3Def) = NewThreeSlotSim();

            Assert.Null(host.ResearchSys.GetFactionDefinition(Faction.Player3));  // premise

            applier.Apply(Gate(ThreeSlotModel()));

            Assert.Same(slot3Def, host.ResearchSys.GetFactionDefinition(Faction.Player3));
        }

        [Fact]
        public void Apply_ThenAPlayer3Lab_CanActuallyStartResearch()
        {
            // The user-visible half: pre-fix the def resolved null, Gate (1) denied with InvalidTarget, and a
            // Player3 lab offered NO research at all.
            var (host, applier, _) = NewThreeSlotSim();
            applier.Apply(Gate(ThreeSlotModel()));

            int lab = host.BuildSys.PlaceBuildingDirectById(
                "lab", Faction.Player3, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.FromInt(40)), preBuilt: true);
            Assert.True(lab >= 0);

            Assert.True(host.ResearchSys.StartResearchCommand(lab, Faction.Player3, researchIndex: 0));
            Assert.Equal(0, host.Research.InProgressIndex[(int)Faction.Player3]);
            Assert.Equal(500 - 100, host.Resources.Ore[(int)Faction.Player3].ToInt());
        }

        [Fact]
        public void Apply_LeavesEveryResearchStoreRowLengthUntouched()
        {
            // DETERMINISM GUARD: ResearchStore's per-faction row LENGTH drives SimChecksum's inner fold loop, so the
            // new seam must NOT grow it. Capacity is still allocated lazily by the first consumer that needs it.
            var (host, applier, _) = NewThreeSlotSim();
            applier.Apply(Gate(ThreeSlotModel()));

            for (int f = 0; f < FactionRegistry.FACTION_ARRAY_SIZE; f++)
                Assert.Equal(f == (int)Faction.Player1 || f == (int)Faction.Player2 ? 1 : 0,
                             host.Research.CompletedLevels[f].Length);
        }

        private static ProjectChimera.Effects.ModifierStore NewModifierStore()
        {
            var world  = new EntityWorld();
            var modSys = new ProjectChimera.Effects.ModifierSystem();
            var store  = new ProjectChimera.Effects.ModifierStore(world, modSys);
            modSys.AttachStore(store);
            return store;
        }
    }
}
