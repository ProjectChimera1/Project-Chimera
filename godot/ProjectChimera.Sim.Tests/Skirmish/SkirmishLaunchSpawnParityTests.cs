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
    ///
    /// <para><b>DW-514 (2026-08-06).</b> That slot-0 mage is no longer in the shipped map: reconciling
    /// <c>alpha_map_01</c> with <c>ScenarioApplier.BuildFallbackMirror</c> dropped it (and cleaned the dragged slot
    /// bases), so the map now authors two workers per slot, symmetrically. The assertions here are therefore DERIVED
    /// from the authored map instead of hardcoding "3 and 2" — the property under test was never the number 3, it was
    /// "every authored unit reaches Play at its authored spot", which is now checked per-position and stays honest
    /// across future authoring changes. The non-vacuity fence in <c>AssertAuthoredArmyIsIntact</c> is what stops a
    /// derived expectation from degenerating into a test of nothing.</para>
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

        /// <summary>The authored rows for <paramref name="slot"/> as an ordinally-sorted (x, z) key list.</summary>
        private static (float X, float Z)[] AuthoredSpots(ScenarioData built, int slot) =>
            built.Units.Where(u => u.Slot == slot).Select(u => (u.X, u.Z))
                 .OrderBy(t => t.X).ThenBy(t => t.Z).ToArray();

        /// <summary>The alive entities of <paramref name="f"/> as the same ordinally-sorted (x, z) key list.</summary>
        private static (float X, float Z)[] AliveSpots(SimulationHost host, Faction f) =>
            AliveOf(host, f).Select(u => (u.X, u.Z))
                 .OrderBy(t => t.X).ThenBy(t => t.Z).ToArray();

        /// <summary>
        /// THE DW-463 property: every unit the map authors for a start position is alive at exactly that position.
        /// Derived from the authored source-with-numbers rather than hardcoded counts — DW-514 reconciled the shipped
        /// roster with <c>BuildFallbackMirror</c> (the asymmetric slot-0 mage was dropped), and a hardcoded expectation
        /// has to be hand-edited on every authoring change, which is precisely how a REAL drop gets normalized away
        /// instead of caught. Non-vacuous: each slot must author at least one unit.
        /// </summary>
        private static void AssertAuthoredArmyIsIntact(LaunchResult launch)
        {
            foreach (int slot in new[] { 0, 1 })
            {
                (float X, float Z)[] authored = AuthoredSpots(launch.Built, slot);
                Assert.NotEmpty(authored); // non-vacuity fence — an empty roster must never satisfy this
                Assert.Equal(authored, AliveSpots(launch.Host, (Faction)(slot + 1)));
            }
        }

        [Fact]
        public void SameFactionLaunch_SpawnsEveryAuthoredUnit_ThroughApplyResetAndTicks()
        {
            LaunchResult launch = Launch("alpha");

            // Boot apply: every authored unit of both slots spawns, at its authored spot.
            AssertAuthoredArmyIsIntact(launch);

            // Play entry (the ResetToAuthoredStart spine): ClearForReset + re-apply — nothing may be lost.
            launch.Host.ClearForReset();
            launch.Applier.Apply(launch.Validated);
            AssertAuthoredArmyIsIntact(launch);

            // Live ticks: no tick-0/1 system may despawn an authored starting unit. (Counts only — a tick may
            // legitimately nudge a position via steering.)
            launch.Host.StepOnce();
            launch.Host.StepOnce();
            foreach (int slot in new[] { 0, 1 })
                Assert.Equal(launch.Built.Units.Count(u => u.Slot == slot),
                             AliveOf(launch.Host, (Faction)(slot + 1)).Count);
        }

        [Fact]
        public void CrossFactionLaunch_SpawnsEveryAuthoredUnit_ViaTheRosterRemap()
        {
            LaunchResult launch = Launch("beta");

            // P2 chose beta: its authored alpha workers remap to beta's Worker-role unit — same count, same spots,
            // and P1's slot-0 army is untouched by the other slot's remap.
            Assert.All(launch.Built.Units.Where(u => u.Slot == 1), u => Assert.Equal("forgehand", u.UnitId));
            Assert.All(launch.Built.Units.Where(u => u.Slot == 0), u => Assert.Equal("worker", u.UnitId));
            AssertAuthoredArmyIsIntact(launch);

            launch.Host.ClearForReset();
            launch.Applier.Apply(launch.Validated);
            launch.Host.StepOnce();
            launch.Host.StepOnce();
            foreach (int slot in new[] { 0, 1 })
                Assert.Equal(launch.Built.Units.Count(u => u.Slot == slot),
                             AliveOf(launch.Host, (Faction)(slot + 1)).Count);
        }
    }
}
