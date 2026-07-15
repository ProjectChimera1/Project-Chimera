#nullable enable
using ProjectChimera.Core;              // Faction, Fixed, FixedVec3, FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioData & co, FactionDefinition, UnitDefinition, ScenarioValidator
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Builder
{
    /// <summary>
    /// Story 6.4 — <see cref="ScenarioApplier.Apply"/> builds the <c>RegionStore</c> from <c>ScenarioData.Regions</c>
    /// (the SINGLE float→Fixed boundary) and hands it to the <see cref="ScenarioDirector"/> BEFORE
    /// <c>LoadScenario</c>, so a <c>unit_in_region</c> trigger evaluates against Fixed-resolved bounds. Proven
    /// end-to-end through the real host: a scenario-placed unit inside/on-edge/outside the resolved rect fires (or
    /// not) the victory action, which is impossible unless Apply resolved the bounds and wired the store correctly.
    /// </summary>
    public class ScenarioApplierRegionTests
    {
        private static UnitDefinition WorkerDef() => new UnitDefinition
        {
            Id = "worker", DisplayName = "Worker", Category = "Worker", Hp = 50f, Speed = 3.5f, Supply = 1,
        };

        private static FactionDefinition AlphaFaction() => new FactionDefinition
        {
            Id = "alpha", DisplayName = "Alpha", Units = { WorkerDef() },
        };

        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;
            defs[(int)Faction.Player2] = faction;
            return defs;
        }

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            var faction = AlphaFaction();
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, SlotDefs(faction));
            return (host, applier);
        }

        /// <summary>A model with a "hill" region [-10,-10 → 10,10], one P1 worker at (ux,uz), and a
        /// match_start → unit_in_region(hill, faction 0) → victory trigger.</summary>
        private static ScenarioData ModelWithUnitAt(float ux, float uz) => new ScenarioData
        {
            Id = "regionmap", DisplayName = "Region Map", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = ux, Z = uz } },
            Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
            },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "hill" } },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                },
            },
        };

        private static bool ApplyAndTickFiresVictory(float ux, float uz)
        {
            var (host, applier) = NewHostAndApplier();
            ValidationResult r = new ScenarioValidator().Validate(ModelWithUnitAt(ux, uz));
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            bool fired = false;
            host.ScenarioDirector.OnVictory = _ => fired = true;
            host.ScenarioDirector.Tick(host.World, Fixed.FromInt(1));
            return fired;
        }

        [Fact]
        public void Apply_BuildsStore_UnitInsideResolvedRect_FiresVictory()
        {
            Assert.True(ApplyAndTickFiresVictory(0f, 0f));
        }

        [Fact]
        public void Apply_BuildsStore_UnitOutsideResolvedRect_DoesNotFire()
        {
            Assert.False(ApplyAndTickFiresVictory(50f, 50f));
        }

        [Fact]
        public void Apply_ResolvesBoundsExactly_EdgeUnitIsInclusive()
        {
            // A unit exactly on the resolved MaxX boundary (10) fires — proving the corner float 10f resolved to the
            // exact Fixed the inclusive test admits (not shrunk/swapped/degenerate).
            Assert.True(ApplyAndTickFiresVictory(10f, 0f));
            Assert.False(ApplyAndTickFiresVictory(10.5f, 0f)); // just past the resolved edge
        }

        [Fact]
        public void Apply_WithNoRegions_LeavesDirectorWithEmptyStore_UnitInRegionFalse()
        {
            // Review patch P11: apply a VALID region-less model (clean input — no dangling ref, no laundering through
            // a FAILED validation via shadow mode) and assert the applier's no-regions path leaves the director with
            // RegionStore.Empty. A region-less model must NOT carry a unit_in_region condition (the validator rejects
            // the dangling ref), so the applied model uses no triggers — keeping the input strictly valid.
            var (host, applier) = NewHostAndApplier();
            var model = ModelWithUnitAt(0f, 0f); // one live P1 worker at the origin (where a "hill" region would be)
            model.Regions  = null;                                        // no regions → RegionStore.Empty
            model.Triggers = System.Array.Empty<TriggerDefinition>();     // region-less ⇒ no unit_in_region ⇒ VALID
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error); // clean, valid input — applied on the PASS path, not shadow mode
            applier.Apply(r.Value);     // builds RegionStore.Empty for the region-less model

            // Observe the empty store behaviorally: load a unit_in_region trigger onto the SAME director (LoadScenario
            // does NOT touch the region store — only Apply's SetRegionStore does), then tick with the applied unit
            // sitting at the origin. An Empty store cannot resolve "hill", so the condition — and the victory it gates
            // — must NOT fire (whereas a correctly-built store WOULD fire, per the tests above).
            bool fired = false;
            host.ScenarioDirector.OnVictory = _ => fired = true;
            host.ScenarioDirector.LoadScenario(new ScenarioData
            {
                Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "koth",
                        Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "hill" } },
                        Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                    },
                },
            });
            host.ScenarioDirector.Tick(host.World, Fixed.FromInt(1));
            Assert.False(fired); // empty store ⇒ unit_in_region false ⇒ no victory
        }

        [Fact]
        public void Apply_ShadowMode_SkipsMalformedRegions_KeepsIndexAlignment_NoThrow()
        {
            // Review patch (follow-up): shadow mode (D3) applies a model that FAILED validation, so malformed region
            // rows reach BuildRegionStore's defensive skip logic. Prove that a null element, a non-finite corner, and
            // a rect that collapses at the float→Fixed (16.16) boundary are each DROPPED without throwing (an NRE on
            // the null row would abort the whole scene load), and — critically — that dropping them does NOT shift the
            // surviving "hill" region's parallel-array index: a unit_in_region(hill) trigger must still resolve against
            // hill's OWN rect. An ids/rects alignment regression would map "hill" to the wrong rect (or none) and the
            // unit at the origin would no longer fire victory.
            var (host, applier) = NewHostAndApplier();
            var model = ModelWithUnitAt(0f, 0f); // one live P1 worker at the origin (inside "hill" [-10..10])
            model.Regions = new[]
            {
                null!,                                                                                            // (a) null row
                new ScenarioRegion { Id = "nan", Name = "NaN", MinX = float.NaN, MinZ = 0f, MaxX = 5f, MaxZ = 5f }, // (b) non-finite corner
                new ScenarioRegion { Id = "sub", Name = "Sub", MinX = 0f, MinZ = 0f, MaxX = 1e-6f, MaxZ = 1e-6f },  // (c) collapses at 16.16
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f }, // GOOD — its index must survive
            };
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.False(r.Ok); // malformed regions ⇒ validation fails; this exercises the SHADOW-mode apply path

            bool fired = false;
            var ex = Record.Exception(() =>
            {
                applier.Apply(r.Value); // must NOT throw on the null / NaN / degenerate rows
                host.ScenarioDirector.OnVictory = _ => fired = true;
                host.ScenarioDirector.Tick(host.World, Fixed.FromInt(1));
            });
            Assert.Null(ex);    // no NRE / OOB from the three malformed rows
            Assert.True(fired); // "hill" kept its correct rect despite the earlier drops ⇒ unit at origin ⇒ victory
        }
    }
}
