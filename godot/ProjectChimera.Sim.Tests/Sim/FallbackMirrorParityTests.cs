#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.7 — the FALLBACK BEHAVIOR-PARITY PIN. The legacy un-tokened <c>ScenarioApplier.ApplyFallback()</c>
    /// writer is retired in favor of <c>Apply(Validate(BuildFallbackMirror()).Value)</c> — one writer path, one
    /// token type. This test pins that the mirror-applied world EQUALS the legacy writer's world: the legacy write
    /// sequence (faction bases, ore/crystal, 8 nodes, 2 command centres, 4 workers — copied VERBATIM from the
    /// deleted method body) is replayed inline against one host, the validated mirror is applied to another, and
    /// the two must agree on key world facts AND produce a byte-identical per-tick SimChecksum run. If this ever
    /// diverges, fix the MIRROR (the spec's Block-If posture), never the legacy sequence recorded here.
    /// </summary>
    public class FallbackMirrorParityTests
    {
        private const int Ticks = 90;

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction(); // carries the "worker" unit both paths spawn
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1;
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        /// <summary>The DELETED legacy ApplyFallback body, replayed verbatim (the parity reference).</summary>
        private static void LegacyApplyFallback(SimulationHost host, ScenarioApplier applier,
            FactionDefinition?[] slotDefs)
        {
            applier.SetFactionBase(Faction.Player1, new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero));
            applier.SetFactionBase(Faction.Player2, new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero));

            host.Resources.AddOre(Faction.Player1, Fixed.FromFloat(200f));
            host.Resources.AddOre(Faction.Player2, Fixed.FromFloat(200f));
            host.Resources.AddCrystal(Faction.Player1, Fixed.FromFloat(100f));
            host.Resources.AddCrystal(Faction.Player2, Fixed.FromFloat(100f));

            var rate = Fixed.FromFloat(5f);
            foreach (var (x, z, supply) in new (float, float, float)[]
            {
                ( -20f, -15f, 600f ), ( -20f,  15f, 600f ),
                (  20f, -15f, 600f ), (  20f,  15f, 600f ),
                (   0f, -25f, 400f ), (   0f,  25f, 400f ),
                ( -35f,   0f, 300f ), (  35f,   0f, 300f ),
            })
            {
                host.Nodes.Create(
                    new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z)),
                    Fixed.FromFloat(supply), rate, maxGatherers: 4);
            }

            host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), preBuilt: true);
            host.BuildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2,
                new FixedVec3(Fixed.FromFloat(+45f), Fixed.Zero, Fixed.Zero), preBuilt: true);

            var workerDef  = slotDefs[(int)Faction.Player1]?.GetUnitByCategory("Worker");
            var workerDef2 = slotDefs[(int)Faction.Player2]?.GetUnitByCategory("Worker") ?? workerDef;
            if (workerDef != null)
            {
                applier.SpawnUnit(workerDef,  Faction.Player1, -42f, -3f);
                applier.SpawnUnit(workerDef,  Faction.Player1, -42f, +3f);
            }
            if (workerDef2 != null)
            {
                applier.SpawnUnit(workerDef2, Faction.Player2, +42f, -3f);
                applier.SpawnUnit(workerDef2, Faction.Player2, +42f, +3f);
            }
        }

        private static List<(uint tick, uint hash)> RunTicks(SimulationHost host, int ticks)
        {
            var seq = new List<(uint, uint)>(ticks);
            host.SetChecksumSink((t, h) => seq.Add((t, h)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        [Fact]
        public void MirrorApplied_EqualsLegacyApplyFallback_WorldAndChecksumRun()
        {
            // Legacy reference world.
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var legacyDefs = new FactionDefinition?[5];
            legacyDefs[(int)Faction.Player1] = faction;
            legacyDefs[(int)Faction.Player2] = faction;
            var legacyHost = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            legacyHost.ChecksumInterval = 1;
            var legacyApplier = new ScenarioApplier(legacyHost, NullLogSink.Instance, legacyDefs);
            LegacyApplyFallback(legacyHost, legacyApplier, legacyDefs);

            // Mirror world (the one production path).
            var (mirrorHost, mirrorApplier) = NewHostAndApplier();
            ValidationResult r = new ScenarioValidator().Validate(ScenarioApplier.BuildFallbackMirror());
            Assert.True(r.Ok, r.Error);
            mirrorApplier.Apply(r.Value);

            // Key world facts agree at t0.
            Assert.Equal(legacyHost.Resources.Ore[(int)Faction.Player1].Raw,     mirrorHost.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(legacyHost.Resources.Crystal[(int)Faction.Player2].Raw, mirrorHost.Resources.Crystal[(int)Faction.Player2].Raw);
            Assert.Equal(CountAlive(legacyHost), CountAlive(mirrorHost));

            // And the two worlds evolve BYTE-IDENTICALLY (the SimChecksum parity core).
            List<(uint, uint)> legacyRun = RunTicks(legacyHost, Ticks);
            List<(uint, uint)> mirrorRun = RunTicks(mirrorHost, Ticks);
            Assert.Equal(legacyRun, mirrorRun);
        }

        [Fact]
        public void FallbackMirror_AlwaysValidates() // the "build defect, not a runtime path" guard
        {
            ValidationResult r = new ScenarioValidator().Validate(ScenarioApplier.BuildFallbackMirror());
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void FallbackMirror_HashesNonzero() // the fallback boot publishes a REAL wire hash (never 0)
        {
            ulong h = CanonicalModelHash.Compute(ScenarioApplier.BuildFallbackMirror());
            Assert.NotEqual(0UL, h);
            Assert.NotEqual(0u, CanonicalModelHash.ToWire(h));
        }

        [Fact]
        public void FallbackMirror_DeclaresNoTriggersAndNoGraph()
        {
            // The legacy writer's "no match_start fire on a fallback boot" contract holds today ONLY because the
            // mirror is trigger-free — pin it, so a trigger/graph quietly added to the mirror turns this red
            // instead of silently changing fallback-boot behavior.
            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror();
            Assert.Empty(mirror.Triggers);
            Assert.Null(mirror.TriggerGraphJson);
            Assert.Null(mirror.Variables);
            Assert.Null(mirror.Timers);
        }

        [Fact]
        public void FallbackMirror_ResolvesWorkerIdByCategory_FromTheSlotFactionDefs()
        {
            // Review follow-up: the legacy writer resolved workers by CATEGORY (GetUnitByCategory("Worker")); the
            // mirror resolves each slot's worker unit_id the same way when defs are threaded, so a custom faction
            // whose worker is not literally id'd "worker" still spawns workers on the fallback boot.
            var custom = new FactionDefinition
            {
                Id = "custom", DisplayName = "Custom",
                Units = { WorkerDefWithId("drone_mk1") },
            };
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = custom;                              // slot 0 → custom worker id
            // slot 1 has no def threaded → conventional "worker" fallback.

            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror(defs);
            Assert.Equal("drone_mk1", mirror.Units[0].UnitId);
            Assert.Equal("drone_mk1", mirror.Units[1].UnitId);
            Assert.Equal("worker",    mirror.Units[2].UnitId);
            Assert.Equal("worker",    mirror.Units[3].UnitId);

            // And the no-args mirror keeps the conventional id everywhere (the parity/golden baseline).
            foreach (ScenarioUnit u in ScenarioApplier.BuildFallbackMirror().Units)
                Assert.Equal("worker", u.UnitId);
        }

        // ── DW-324 doc-debt sweep: turn two COMMENT-COUPLED duplications into asserted couplings ──────────────

        /// <summary>
        /// DW-324 — <c>ScenarioLoadPhase.SetupStartPositionBridge</c>'s no-scenario branch hardcodes the flag-pole
        /// marker positions <c>(-45,0)</c>/<c>(+45,0)</c> under a comment that says only "matching
        /// ScenarioApplier.BuildFallbackMirror". Nothing enforced that. This does: it pins the MIRROR's slot-0/slot-1
        /// bases to the exact literals that phase duplicates, so moving the mirror's bases without moving the markers
        /// turns Tier-1 RED instead of silently drifting the fallback boot's markers off its own bases.
        ///
        /// The literals are written out deliberately (not read from the mirror on both sides) — a self-comparison
        /// would be vacuous, which is the whole failure mode being closed.
        /// </summary>
        [Fact]
        public void FallbackMirror_StartPositions_MatchTheScenarioLoadPhaseMarkerFallback()
        {
            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror();

            Assert.Equal(2, mirror.PlayerSlots.Length);          // the marker branch sizes its array to 2
            ScenarioPlayerSlot s0 = SlotOf(mirror, 0);
            ScenarioPlayerSlot s1 = SlotOf(mirror, 1);

            Assert.Equal(-45f, s0.BaseX);
            Assert.Equal(0f,   s0.BaseZ);
            Assert.Equal(+45f, s1.BaseX);
            Assert.Equal(0f,   s1.BaseZ);
        }

        /// <summary>
        /// DW-324 (the "optional FallbackMirror-vs-alpha_map_01 agreement test" the sweep list records, closing
        /// DW-222's accepted residual) — <c>BuildFallbackMirror</c>'s doc once claimed "keep these literal values in
        /// sync with alpha_map_01.json" with nothing enforcing it. This asserts the agreement that IS real: the shared
        /// economy/board seed — map bounds, win condition, per-slot start ore/crystal, the 8 resource nodes
        /// (position/supply/rate/max-gatherers) and the 2 pre-built command centres.
        ///
        /// <para>DELIBERATELY NOT asserted: start positions and the unit roster. Those have legitimately diverged —
        /// the shipped map's bases were moved in the editor to ±38.9 with non-zero Z and it gained a `mage` — so
        /// asserting them would either be red on arrival or force a shipped map to be edited to satisfy a test. That
        /// divergence is now stated in the mirror's own doc-comment instead of being implied to not exist.</para>
        /// </summary>
        [Fact]
        public void FallbackMirror_AgreesWithAlphaMap01_OnTheSharedEconomyLiterals()
        {
            ScenarioData mirror = ScenarioApplier.BuildFallbackMirror();
            ScenarioData? shipped = ScenarioSerializer.LoadFromFile(
                Path.Combine(ScenariosDir(), "alpha_map_01.json"));
            Assert.NotNull(shipped);

            Assert.Equal(shipped!.MapBounds,    mirror.MapBounds);
            Assert.Equal(shipped.WinCondition,  mirror.WinCondition);

            // Per-slot starting economy (slot-keyed, not index-keyed — slot order is not a guarantee).
            for (int slot = 0; slot <= 1; slot++)
            {
                ScenarioPlayerSlot m = SlotOf(mirror, slot);
                ScenarioPlayerSlot s = SlotOf(shipped, slot);
                Assert.Equal(s.StartOre,     m.StartOre);
                Assert.Equal(s.StartCrystal, m.StartCrystal);
            }

            // The 8-node resource board, in order.
            Assert.Equal(shipped.ResourceNodes.Length, mirror.ResourceNodes.Length);
            for (int i = 0; i < mirror.ResourceNodes.Length; i++)
            {
                ScenarioResourceNode m = mirror.ResourceNodes[i];
                ScenarioResourceNode s = shipped.ResourceNodes[i];
                Assert.Equal(s.X, m.X);
                Assert.Equal(s.Z, m.Z);
                Assert.Equal(s.Supply, m.Supply);
                Assert.Equal(s.Rate, m.Rate);
                Assert.Equal(s.MaxGatherers, m.MaxGatherers);
            }

            // The 2 pre-built command centres.
            Assert.Equal(shipped.Buildings.Length, mirror.Buildings.Length);
            for (int i = 0; i < mirror.Buildings.Length; i++)
            {
                ScenarioBuilding m = mirror.Buildings[i];
                ScenarioBuilding s = shipped.Buildings[i];
                Assert.Equal(s.Type, m.Type);
                Assert.Equal(s.Slot, m.Slot);
                Assert.Equal(s.X, m.X);
                Assert.Equal(s.Z, m.Z);
                Assert.Equal(s.PreBuilt, m.PreBuilt);
            }
        }

        /// <summary>The player slot declaring <paramref name="slot"/> (slot-keyed lookup, never array position).</summary>
        private static ScenarioPlayerSlot SlotOf(ScenarioData s, int slot)
        {
            foreach (ScenarioPlayerSlot p in s.PlayerSlots)
                if (p.Slot == slot) return p;
            Assert.Fail($"scenario '{s.Id}' declares no player slot {slot}.");
            return null!; // unreachable
        }

        // <repo>/godot/ProjectChimera.Sim.Tests/Sim/THIS.cs → <repo>/godot/resources/data/scenarios
        private static string ScenariosDir([CallerFilePath] string thisFile = "")
        {
            string godot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
            return Path.Combine(godot, "resources", "data", "scenarios");
        }

        /// <summary>A minimal Worker-category unit def with a custom id (helper for the category-resolution pin).</summary>
        private static UnitDefinition WorkerDefWithId(string id) => new UnitDefinition
        {
            Id = id, DisplayName = "Drone", Category = "Worker", Hp = 50f, Speed = 3.5f, Supply = 1,
        };

        private static int CountAlive(SimulationHost host)
        {
            int n = 0;
            for (int i = 0; i < host.World.HighWaterMark; i++)
                if (host.World.IsAlive(i)) n++;
            return n;
        }
    }
}
