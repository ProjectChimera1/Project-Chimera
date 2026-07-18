#nullable enable
using ProjectChimera.Core;              // Faction, Fixed, FactionRegistry, BuildingType
using ProjectChimera.Core.Definitions;  // ScenarioData & co, FactionDefinition, UnitDefinition, ScenarioValidator, Validated
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink, SimulationLoop
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.11 (T2) — the win-condition presets resolve through the REAL <see cref="ScenarioApplier"/> path: the
    /// applier's own <c>unitEntityIds</c>/<c>buildingSlots</c> construction feeds <c>WinConditionSystem.Configure</c>
    /// (not a hand-injected map), then destroying the designated asset resolves the correct winner. Also covers P3:
    /// a designated leader that FAILS to spawn (unknown unit_id → the applier stores -1) makes its owner lose
    /// deterministically instead of a silently un-winnable match.
    /// </summary>
    public class WinConditionApplierResolutionTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static UnitDefinition LeaderDef() => new UnitDefinition
        {
            Id = "leader", DisplayName = "Leader", Category = "Ranged",
            Hp = 50f, Speed = 3.5f, VisionRange = 7f, AttackRange = 2f, AttackDamage = 4f,
            AttackSpeed = 1.5f, Supply = 1, DamageType = "Pierce", ArmorType = "Light",
        };

        private static FactionDefinition Faction2() => new FactionDefinition
        {
            Id = "alpha", DisplayName = "Alpha", Units = { LeaderDef() },
        };

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            var faction = Faction2();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        private static ScenarioData BlankTwoPlayer()
        {
            var s = ScenarioData.CreateBlank("wincon-applier", suggestedPlayers: 2);
            return s;
        }

        private static Validated<ScenarioData> Validate(ScenarioData s)
        {
            ValidationResult r = new ScenarioValidator().Validate(s);
            Assert.True(r.Ok, r.Error);
            return r.Value;
        }

        private static void TickPastGrace(SimulationHost h)
        {
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
        }

        [Fact]
        public void Assassination_ThroughApplier_LeaderDestroyed_OtherFactionWins()
        {
            var (host, applier) = NewHostAndApplier();
            var s = BlankTwoPlayer();
            s.Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } }; // P1 leader, index 0
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 };

            applier.Apply(Validate(s));

            // Leader alive → no resolution yet.
            TickPastGrace(host);
            Assert.Equal(0, host.WinState.WinnerFaction());

            // Destroy the applier-spawned leader (entity id 0 — first and only unit). Owner P1 loses → P2 wins.
            host.World.Destroy(0);
            host.WinCon.Tick(host.World, Dt);
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
        }

        [Fact]
        public void Landmark_ThroughApplier_StructureDestroyed_OtherFactionWins()
        {
            var (host, applier) = NewHostAndApplier();
            var s = BlankTwoPlayer();
            s.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 0, Z = 0 } }; // P1 landmark
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 };

            applier.Apply(Validate(s));
            Assert.Equal(1, host.Buildings.Count);

            TickPastGrace(host);
            Assert.Equal(0, host.WinState.WinnerFaction()); // landmark intact

            // Destroy the applier-placed landmark (BuildingStore slot 0 — the only building). Owner P1 loses → P2 wins.
            host.Buildings.Destroy(0);
            host.WinCon.Tick(host.World, Dt);
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
        }

        [Fact]
        public void Assassination_ThroughApplier_LeaderFailsToSpawn_OwnerLosesDeterministically()
        {
            // The leader unit_id does not exist in the faction def → the applier stores unitEntityIds[0] == -1
            // (the "passed param-validation but failed to spawn" case P3 addresses). The owner (P1) must lose.
            var (host, applier) = NewHostAndApplier();
            var s = BlankTwoPlayer();
            s.Units = new[] { new ScenarioUnit { UnitId = "ghost-not-in-faction", Slot = 0, X = 0, Z = 0 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 };

            applier.Apply(Validate(s));
            Assert.Equal(0, host.World.AliveCount); // the leader never spawned

            TickPastGrace(host);
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction()); // owner P1 loses deterministically
            Assert.Equal(WinStateStore.VERDICT_LOST, host.WinState.Verdict[(int)Faction.Player1]);
        }

        // ── Review P7: KotH / TimedSurvival through the REAL applier + the REAL host loop ────────────────────────
        // These two are the only coverage that would fail if ScenarioApplier.Apply stopped passing the real
        // regionStore (or the placement maps) into WinConditionSystem.Configure: replacing that argument with
        // RegionStore.Empty makes Configure fall back to the built-in path (review P5), so no KotH win can latch
        // at hold_ticks and this test fails loudly. Advancing via host.StepOnce() (the SimResetTests.RunTicks
        // pattern) drives the full 16-system spine, not a hand-called WinCon.Tick.

        [Fact]
        public void KotH_ThroughApplier_SoleHolder_Wins()
        {
            const int holdTicks = 10;
            var (host, applier) = NewHostAndApplier();
            var s = BlankTwoPlayer();
            s.Regions = new[] { new ScenarioRegion { Id = "zone", Name = "Zone", MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 } };
            s.Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } }; // P1 inside the zone
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = holdTicks };

            applier.Apply(Validate(s));

            // Presets are not grace-gated on the WIN path: the sole holder's counter advances from tick 1 and the
            // win latches at exactly hold_ticks — well inside the grace window, proving the REAL region store (not
            // an empty fallback) reached Configure through the applier.
            for (int t = 0; t < holdTicks + 2; t++) host.StepOnce();

            Assert.Equal((int)Faction.Player1, host.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_LOST, host.WinState.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void TimedSurvival_ThroughApplier_SurvivorWins()
        {
            const int surviveTicks = 10;
            var (host, applier) = NewHostAndApplier();
            var s = BlankTwoPlayer();
            s.Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } }; // the survivor
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = surviveTicks };

            applier.Apply(Validate(s));

            // Real-loop advance past the deadline: the countdown Configure seeded through the applier reaches 0 at
            // tick 10 and the surviving designated faction wins (again inside the grace window — win paths are
            // never grace-gated).
            for (int t = 0; t < surviveTicks + 2; t++) host.StepOnce();

            Assert.Equal((int)Faction.Player1, host.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_LOST, host.WinState.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void Assassination_UnresolvedLeader_DirectConfigureMinusOneMap_OwnerLoses()
        {
            // P3 through a direct Configure with an explicit -1 map (the belt-and-suspenders path the instructions
            // allow): leader_unit_index resolves to -1 → owner loses without a hand-injected alive entity.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            var s = new ScenarioData
            {
                Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 },
            };
            host.WinCon.Configure(s, RegionStore.Empty, new[] { -1 }, null); // unitEntityIds[0] == -1

            TickPastGrace(host);
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_LOST, host.WinState.Verdict[(int)Faction.Player1]);
        }
    }
}
