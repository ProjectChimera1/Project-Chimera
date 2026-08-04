#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core;               // Faction, FactionRegistry
using ProjectChimera.Core.Definitions;   // ScenarioData, ScenarioSerializer, FactionDefinition, ScenarioValidator, UnitTagValidator, AbilityRegistry
using ProjectChimera.Core.Sim;           // SimulationHost, ScenarioApplier, NullLogSink
using ProjectChimera.Core.Skirmish;      // SkirmishCatalog, SkirmishSetup, SetupSlot, SlotKind, SkirmishSetupToScenario, SkirmishSetupValidator
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-463 — the applier-level check the ledger entry demands. The 2026-07-28 in-engine gate observed a skirmish
    /// launch of the REAL shipped <c>alpha_map_01</c> reaching Play with only 2 of the 3 units the map authors for
    /// start position 0 (the mage at x=-40,z=0 dropped), and the entry's open question was whether the loss lives in
    /// the Godot-free core (transform → validator → applier → Play-entry reset) or downstream in the Godot-coupled
    /// presentation path. This suite drives the ENTIRE Godot-free chain over the REAL shipped map + faction files —
    /// catalog scan, setup validation, transform, scenario validation, apply, ClearForReset + re-apply (the Play-entry
    /// F5 spine), and live ticks — and pins that EVERY authored unit spawns and survives at each stage, same-faction
    /// AND cross-faction. Conclusion recorded 2026-08-04: the Godot-free chain preserves all 3 slot-0 units at every
    /// stage, so any recurrence of the in-engine symptom is in the Godot-coupled layer (or was transient) — and any
    /// FUTURE sim-side drop of an authored unit turns this red instead of needing an engine drive to notice.
    /// </summary>
    public class SkirmishLaunchSpawnParityTests
    {
        private static string GodotDir([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

        private static string ScenariosAbs => Path.Combine(GodotDir(), "resources", "data", "scenarios");
        private static string FactionsAbs  => Path.Combine(GodotDir(), "resources", "data", "factions");

        private sealed class LaunchResult
        {
            public SimulationHost Host = null!;
            public ScenarioApplier Applier = null!;
            public Validated<ScenarioData> Validated;
            public ScenarioData Built = null!;
        }

        /// <summary>Drive the full Godot-free launch chain for the real <c>alpha_map_01</c>: scan the real catalogs,
        /// validate the setup, build, resolve the real faction defs (LoadFromFile → ResolveAbilities → tag-drop —
        /// the ScenarioLoadPhase pre-pass), validate the built scenario, and apply it to a fresh host.</summary>
        private static LaunchResult Launch(string p2FactionId)
        {
            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(ScenariosAbs, "res://resources/data/scenarios");
            IReadOnlyList<FactionEntry> factions = SkirmishCatalog.ScanFactions(FactionsAbs, "res://resources/data/factions");
            MapEntry map = maps.First(m => m.Id == "alpha_map_01");

            var setup = new SkirmishSetup
            {
                MapId = "alpha_map_01",
                Slots = new List<SetupSlot>
                {
                    new() { Slot = 0, Kind = SlotKind.Human, FactionId = "alpha" },
                    new() { Slot = 1, Kind = SlotKind.Ai, FactionId = p2FactionId, Ai = AiDifficulty.Normal },
                },
            };
            Assert.Empty(new SkirmishSetupValidator().Validate(setup, map, factions)); // the launch gate passes

            ScenarioData baseMap = ScenarioSerializer.LoadFromFile(Path.Combine(ScenariosAbs, "alpha_map_01.json"))!;
            ScenarioData built = SkirmishSetupToScenario.Build(setup, baseMap, factions);

            // The boot pre-pass, verbatim: per-slot defs from the REAL faction files + ability resolve + tag-drop.
            FactionDefinition LoadDef(string file)
            {
                var def = FactionDefinition.LoadFromFile(Path.Combine(FactionsAbs, file));
                foreach (var u in def.Units) u.ResolveAbilities(AbilityRegistry.Empty);
                Assert.Empty(UnitTagValidator.ValidateAndDropUnits(def)); // the shipped factions are tag-clean
                return def;
            }
            FactionDefinition p1 = LoadDef("alpha_faction.json");
            FactionDefinition p2 = LoadDef($"{p2FactionId}_faction.json");
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = p1;
            slotDefs[(int)Faction.Player2] = p2;

            ValidationResult r = new ScenarioValidator().Validate(built, slotDefs);
            Assert.True(r.Ok, r.Error); // the built scenario clears the same fail-closed gate the boot runs

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), p1, p2);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            applier.Apply(r.Value);
            return new LaunchResult { Host = host, Applier = applier, Validated = r.Value, Built = built };
        }

        private static List<(int Id, float X, float Z)> AliveOf(SimulationHost host, Faction f)
        {
            var list = new List<(int, float, float)>();
            var world = host.World;
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == f)
                    list.Add((i, world.Position[i].X.ToFloat(), world.Position[i].Z.ToFloat()));
            return list;
        }

        [Fact]
        public void SameFactionLaunch_SpawnsEveryAuthoredUnit_ThroughApplyResetAndTicks()
        {
            LaunchResult launch = Launch("alpha");

            // The shipped map authors 3 units for start position 0 (worker/worker/MAGE) and 2 for position 1 —
            // assert against the authored source-with-numbers, not a screenshot impression.
            Assert.Equal(3, launch.Built.Units.Count(u => u.Slot == 0));
            Assert.Equal(2, launch.Built.Units.Count(u => u.Slot == 1));
            Assert.Contains(launch.Built.Units, u => u.UnitId == "mage" && u.Slot == 0);

            // Boot apply: all 3 + 2 spawn (the mage at x=-40,z=0 included).
            Assert.Equal(3, AliveOf(launch.Host, Faction.Player1).Count);
            Assert.Equal(2, AliveOf(launch.Host, Faction.Player2).Count);
            Assert.Contains(AliveOf(launch.Host, Faction.Player1), u => u.X == -40f && u.Z == 0f);

            // Play entry (the ResetToAuthoredStart spine): ClearForReset + re-apply — nothing may be lost.
            launch.Host.ClearForReset();
            launch.Applier.Apply(launch.Validated);
            Assert.Equal(3, AliveOf(launch.Host, Faction.Player1).Count);
            Assert.Equal(2, AliveOf(launch.Host, Faction.Player2).Count);
            Assert.Contains(AliveOf(launch.Host, Faction.Player1), u => u.X == -40f && u.Z == 0f);

            // Live ticks: no tick-0/1 system may despawn an authored starting unit.
            launch.Host.StepOnce();
            launch.Host.StepOnce();
            Assert.Equal(3, AliveOf(launch.Host, Faction.Player1).Count);
            Assert.Equal(2, AliveOf(launch.Host, Faction.Player2).Count);
        }

        [Fact]
        public void CrossFactionLaunch_SpawnsEveryAuthoredUnit_ViaTheRosterRemap()
        {
            LaunchResult launch = Launch("beta");

            // P2 chose beta: the 2 authored alpha workers remap to beta's Worker-role unit — still 2 units, and
            // P1's slot-0 army (incl. the mage) is untouched by the other slot's remap.
            Assert.Equal(3, launch.Built.Units.Count(u => u.Slot == 0));
            Assert.Equal(2, launch.Built.Units.Count(u => u.Slot == 1));
            Assert.All(launch.Built.Units.Where(u => u.Slot == 1), u => Assert.Equal("forgehand", u.UnitId));

            Assert.Equal(3, AliveOf(launch.Host, Faction.Player1).Count);
            Assert.Equal(2, AliveOf(launch.Host, Faction.Player2).Count);

            launch.Host.ClearForReset();
            launch.Applier.Apply(launch.Validated);
            launch.Host.StepOnce();
            launch.Host.StepOnce();
            Assert.Equal(3, AliveOf(launch.Host, Faction.Player1).Count);
            Assert.Equal(2, AliveOf(launch.Host, Faction.Player2).Count);
        }
    }
}
