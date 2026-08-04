#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;   // TriggerGraph / ActionNode / EventNode / TriggerNode / ExecEdge
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-240 — teeth for the unknown-<c>unit_id</c> half of the fail-closed scenario gate.
    ///
    /// <para><b>The defect.</b> <see cref="ScenarioValidator.Validate"/> gated a pre-placed BUILDING's
    /// <c>type</c> against its owner faction (Story 6.8) but never gated a pre-placed UNIT's <c>unit_id</c>, nor a
    /// trigger <c>spawn_unit</c> action's. Both resolve at runtime through <c>FactionDefinition.GetUnit</c> and both
    /// fail SILENTLY on a miss: <c>ScenarioApplier</c> logs a warning and <c>continue</c>s (the unit never exists);
    /// <c>ScenarioDelegateBinder.OnSpawnUnit</c> warns and returns (the trigger fires forever, spawning nothing).
    /// That is exactly how a cross-faction launch shipped an AI opponent with zero workers (Story 11.1's in-engine
    /// gate, 2026-07-28) — an unplayable match with no load-time diagnostic anywhere.</para>
    ///
    /// <para><b>RED-teeth proof.</b> Delete the three <c>IsKnownUnitId</c> guards in
    /// <c>ScenarioValidator</c> and every <c>…IsRejected…</c> row below turns RED; the amnesty and accept rows stay
    /// GREEN either way (they pin that the fix changes NOTHING on the paths that fed the pre-fix behavior).</para>
    ///
    /// <para><b>The amnesty.</b> The check resolves against the OWNER faction's roster, so it can only run when
    /// per-slot faction defs are threaded — exactly the Story 6.8 building-type precedent. With null defs (the
    /// default for every legacy caller and test) the gate is byte-identical to the pre-DW-240 gate.</para>
    /// </summary>
    public class ScenarioValidatorUnitIdTests
    {
        private static ScenarioValidator NewValidator() => new();

        // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A faction whose roster is exactly {worker, mage} — the resolution domain the gate searches.</summary>
        private static FactionDefinition AlphaFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "mage",   Category = "Ranged", Hp = 60f });
            return f;
        }

        /// <summary>A faction with a DISJOINT roster {forgehand, rune_caster} — the cross-faction mismatch case.</summary>
        private static FactionDefinition BetaFaction()
        {
            var f = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            f.Units.Add(new UnitDefinition { Id = "forgehand",   Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "rune_caster", Category = "Ranged", Hp = 60f });
            return f;
        }

        /// <summary>Per-slot defs indexed by <c>(int)Faction</c> (= slot + 1), the array shape the applier holds.</summary>
        private static FactionDefinition?[] SlotDefs(FactionDefinition? p1, FactionDefinition? p2 = null)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            defs[(int)Faction.Player1] = p1;
            defs[(int)Faction.Player2] = p2;
            return defs;
        }

        /// <summary>A minimal VALID two-slot model with ONE pre-placed unit of <paramref name="unitId"/> on
        /// <paramref name="slot"/> (mirrors the NegativeValidationTests / MapWriteGateTests fixture shape).</summary>
        private static ScenarioData ModelWithUnit(string unitId, int slot) => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings = System.Array.Empty<ScenarioBuilding>(),
            Units = new[] { new ScenarioUnit { UnitId = unitId, Slot = slot, X = 10f, Z = 10f } },
        };

        /// <summary>The same minimal model with NO pre-placed units and ONE flat trigger carrying a single
        /// <c>spawn_unit</c> action for <paramref name="faction"/>.</summary>
        private static ScenarioData ModelWithSpawnAction(string? unitId, int faction)
        {
            ScenarioData m = ModelWithUnit("worker", 0);
            m.Units = System.Array.Empty<ScenarioUnit>();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "wave",
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[]
                    {
                        new TriggerAction
                        {
                            Type = "spawn_unit", UnitId = unitId, Faction = faction,
                            X = Fixed.FromInt(20), Z = Fixed.FromInt(5), Count = 2,
                        },
                    },
                },
            };
            return m;
        }

        /// <summary>A canonical trigger graph whose single action leaf is a <c>spawn_unit</c> (the second execution
        /// channel — it merges into the same walk and hits the identical OnSpawnUnit resolution).</summary>
        private static string SpawnUnitGraph(string? unitId, int faction)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ActionNode
            {
                Id = 2, Kind = "spawn_unit", UnitId = unitId, Faction = faction,
                X = Fixed.FromInt(20), Z = Fixed.FromInt(5), Count = 2,
            });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            return g.ToCanonicalJson();
        }

        // ── Pre-placed units ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void PrePlacedUnitId_ResolvingInOwnerFactionRoster_IsAccepted()
        {
            // The happy path: the owner faction declares the authored id, so the applier's GetUnit will resolve it.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("mage", 0), SlotDefs(AlphaFaction()));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void PrePlacedUnitId_NotInOwnerFactionRoster_IsRejected_LocatingTheField()
        {
            // The core defect: an id the owner faction does not declare resolves to no UnitDefinition, so the applier
            // skips the unit and the match starts short an entity with only a log line. Reject it at the gate.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("ghost_unit", 0), SlotDefs(AlphaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("units[0].unit_id", r.Error!);
            Assert.Contains("ghost_unit", r.Error!);
            Assert.Contains("alpha", r.Error!);   // names WHICH roster was searched
        }

        [Fact]
        public void PrePlacedUnitId_AuthoredAgainstAnotherFaction_IsRejected()
        {
            // The 2026-07-28 in-engine defect in miniature: the map authors alpha's "worker" for slot 1, but slot 1
            // chose beta, whose roster is disjoint ("forgehand"). Pre-fix this launched an opponent with no workers.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("worker", 1), SlotDefs(AlphaFaction(), BetaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("units[0].unit_id", r.Error!);
            Assert.Contains("beta", r.Error!);
        }

        [Fact]
        public void PrePlacedUnitId_ThatOtherFactionDeclares_IsAcceptedForItsOwnSlot()
        {
            // The mirror of the row above — beta's own id on beta's slot resolves. Proves the gate keys on the OWNER
            // slot's roster rather than accepting any id known to any threaded faction.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("forgehand", 1), SlotDefs(AlphaFaction(), BetaFaction()));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void PrePlacedUnitId_Unknown_WithNullFactionDefs_IsAccepted_LegacyAmnesty()
        {
            // No defs threaded ⇒ nothing to resolve against ⇒ the gate is a no-op, byte-identical to pre-DW-240.
            // This is the row that keeps every legacy caller/test unchanged (the Story 6.8 building-type precedent).
            ValidationResult r = NewValidator().Validate(ModelWithUnit("ghost_unit", 0));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void PrePlacedUnitId_Unknown_WithUnresolvedOwnerSlotDef_IsAccepted()
        {
            // Defs threaded but the OWNER slot's entry is null (an empty/missing faction_json keeps a null slot):
            // there is still no roster to resolve against, so the same amnesty applies.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("ghost_unit", 1), SlotDefs(AlphaFaction(), p2: null));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void PrePlacedUnitId_Blank_IsRejected_WhenOwnerFactionResolves()
        {
            // A blank id can never resolve — GetUnit("") is null — so it is the same silently-dropped unit.
            ValidationResult r = NewValidator().Validate(ModelWithUnit("", 0), SlotDefs(AlphaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("units[0].unit_id", r.Error!);
        }

        // ── Flat-channel trigger spawn_unit ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void SpawnUnitAction_UnitIdInSpawningFactionRoster_IsAccepted()
        {
            ValidationResult r = NewValidator().Validate(ModelWithSpawnAction("mage", faction: 0), SlotDefs(AlphaFaction()));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void SpawnUnitAction_UnknownUnitId_IsRejected_LocatingTheAction()
        {
            // OnSpawnUnit resolves the id against the SPAWNING faction's roster and returns on a miss — the trigger
            // fires forever and spawns nothing, diagnosable pre-fix only from a runtime warning.
            ValidationResult r = NewValidator().Validate(ModelWithSpawnAction("ghost_unit", faction: 0), SlotDefs(AlphaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[0].unit_id", r.Error!);
            Assert.Contains("ghost_unit", r.Error!);
        }

        [Fact]
        public void SpawnUnitAction_UnitIdOfAnotherFaction_IsRejected_ForTheSpawningSlot()
        {
            // Spawning beta's "forgehand" for faction slot 0 (alpha) resolves to nothing — the same silent dead action.
            ValidationResult r = NewValidator().Validate(
                ModelWithSpawnAction("forgehand", faction: 0), SlotDefs(AlphaFaction(), BetaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[0].unit_id", r.Error!);
            Assert.Contains("alpha", r.Error!);
        }

        [Fact]
        public void SpawnUnitAction_NullUnitId_IsRejected_WhenSpawningFactionResolves()
        {
            // An omitted unit_id is a spawn_unit that can never spawn anything.
            ValidationResult r = NewValidator().Validate(ModelWithSpawnAction(null, faction: 0), SlotDefs(AlphaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[0].unit_id", r.Error!);
        }

        [Fact]
        public void SpawnUnitAction_UnknownUnitId_WithNullFactionDefs_IsAccepted_LegacyAmnesty()
        {
            // Same amnesty as the pre-placed half — this is why every pre-existing trigger test (which threads no
            // defs and authors ids like "soldier" that match no faction) keeps passing unchanged.
            ValidationResult r = NewValidator().Validate(ModelWithSpawnAction("ghost_unit", faction: 0));
            Assert.True(r.Ok, r.Error);
        }

        // ── Graph-channel spawn_unit ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GraphChannelSpawnUnit_KnownUnitId_IsAccepted()
        {
            ScenarioData m = ModelWithUnit("worker", 0);
            m.TriggerGraphJson = SpawnUnitGraph("mage", faction: 0);
            ValidationResult r = NewValidator().Validate(m, SlotDefs(AlphaFaction()));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void GraphChannelSpawnUnit_UnknownUnitId_IsRejected_LocatingTheNode()
        {
            // The graph channel merges into the SAME execution walk, so it must get the same gate as the flat pass —
            // otherwise the fix is trivially bypassable by authoring the spawn in the graph IR instead.
            ScenarioData m = ModelWithUnit("worker", 0);
            m.TriggerGraphJson = SpawnUnitGraph("ghost_unit", faction: 0);
            ValidationResult r = NewValidator().Validate(m, SlotDefs(AlphaFaction()));
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph action node 2.unit_id", r.Error!);
            Assert.Contains("ghost_unit", r.Error!);
        }

        [Fact]
        public void GraphChannelSpawnUnit_UnknownUnitId_WithNullFactionDefs_IsAccepted_LegacyAmnesty()
        {
            ScenarioData m = ModelWithUnit("worker", 0);
            m.TriggerGraphJson = SpawnUnitGraph("ghost_unit", faction: 0);
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        // ── Shipped-content guard ───────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void EveryShippedScenario_PassesTheGate_AgainstItsOwnAuthoredFactions()
        {
            // The content half of DW-240: every shipped map's pre-placed unit ids must resolve in the roster of the
            // faction that map's OWN player_slots[].faction_json names. This is the guard that would have caught a
            // future authored map (or a faction roster rename) re-introducing the silently-dropped-army class — and
            // it proves the new gate does not reject anything we ship today.
            int checkedMaps = 0, checkedUnits = 0;
            foreach (string path in Directory.EnumerateFiles(ScenariosDir(), "*.json"))
            {
                ScenarioData? s = ScenarioSerializer.LoadFromFile(path);
                if (s?.PlayerSlots == null) continue;

                var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
                foreach (ScenarioPlayerSlot slot in s.PlayerSlots)
                {
                    string abs = ResPathToAbs(slot.FactionJson);
                    int fIdx = slot.Slot + 1;
                    if (abs.Length == 0 || !File.Exists(abs) || fIdx < 0 || fIdx >= defs.Length) continue;
                    defs[fIdx] = FactionDefinition.LoadFromFile(abs);
                }

                ValidationResult r = new ScenarioValidator().Validate(s, defs);
                Assert.True(r.Ok, $"{Path.GetFileName(path)}: {r.Error}");
                checkedMaps++;
                checkedUnits += s.Units?.Length ?? 0;
            }
            Assert.True(checkedMaps > 0, "no shipped scenarios were checked — the scenarios directory resolution broke.");
            Assert.True(checkedUnits > 0, "no shipped pre-placed units were checked — the guard would be vacuous.");
        }

        /// <summary>Map a <c>res://…</c> content path onto this checkout's <c>godot/</c> directory; "" when the path
        /// is empty or not a res:// reference.</summary>
        private static string ResPathToAbs(string? resPath)
        {
            const string prefix = "res://";
            if (string.IsNullOrEmpty(resPath) || !resPath!.StartsWith(prefix, System.StringComparison.Ordinal))
                return "";
            return Path.Combine(GodotDir(), resPath.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Resolve <c>godot/</c> from this file's compile-time path (mirrors ScenarioValidatorGridExtentTests).</summary>
        private static string GodotDir([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

        private static string ScenariosDir() => Path.Combine(GodotDir(), "resources", "data", "scenarios");
    }
}
