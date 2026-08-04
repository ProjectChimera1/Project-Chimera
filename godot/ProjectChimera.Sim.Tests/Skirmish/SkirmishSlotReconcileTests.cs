#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // ScenarioData, TriggerDefinition, WinConditionSpec, ScenarioValidator
using ProjectChimera.Core.Skirmish;      // SkirmishSetup, SetupSlot, SlotKind, FactionEntry, SkirmishSetupToScenario
using ProjectChimera.Dsl;                // VarScope, DslValueType
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-458 (decision 2026-07-30: prune-and-reconcile) — <c>SkirmishSetupToScenario.Build</c> must strip/rewrite
    /// every per-slot reference left dangling when a launch drops base start positions: trigger events/conditions/
    /// actions, the win-condition preset spec, and Income resource-node owners. Before the fix, Build left the base
    /// map's triggers/win-spec/nodes byte-identical, so a 3–4-start shipped map with per-slot logic launched 1v1
    /// booted with dangling references — a TimedSurvival preset or Income node naming a dropped slot even REJECTED
    /// at the boot validator, silently substituting the fallback map. The identity path (every base slot kept with
    /// its own ordinal) must stay REFERENCE-identical so a plain 1v1 serializes byte-identically to before.
    /// </summary>
    public class SkirmishSlotReconcileTests
    {
        // ── Builders ────────────────────────────────────────────────────────────────

        private static IReadOnlyList<FactionEntry> Factions() => new List<FactionEntry>
        {
            new()
            {
                Id = "alpha", DisplayName = "alpha", ResPath = "res://factions/alpha_faction.json",
                Units = new List<FactionUnitEntry> { new() { Id = "worker", Category = "Worker" } },
            },
        };

        private static SetupSlot Human(int slot) => new() { Slot = slot, Kind = SlotKind.Human, FactionId = "alpha" };
        private static SetupSlot Ai(int slot)    => new() { Slot = slot, Kind = SlotKind.Ai,    FactionId = "alpha", Ai = AiDifficulty.Normal };

        private static SkirmishSetup Setup1v1() => new() { MapId = "m1", Slots = new List<SetupSlot> { Human(0), Ai(1) } };

        /// <summary>A base map whose start positions carry the given ORDINALS (contiguous or sparse).</summary>
        private static ScenarioData BaseMap(params int[] slotOrdinals)
        {
            var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
            m.PlayerSlots = slotOrdinals.Select(o => new ScenarioPlayerSlot
            {
                Slot = o, FactionJson = "res://factions/alpha_faction.json",
                StartOre = 200f, BaseX = -45f + o * 30f, BaseZ = 0f,
            }).ToArray();
            return m;
        }

        private static TriggerEvent Ev(string type, int faction) => new() { Type = type, Faction = faction };
        private static TriggerCondition Cond(string type, int faction) => new() { Type = type, Faction = faction };
        private static TriggerAction Act(string type, int faction) => new() { Type = type, Faction = faction, UnitId = "worker", Text = "t", Amount = Fixed.FromInt(5) };

        private static TriggerDefinition Trigger(string name, TriggerEvent[]? ev = null, TriggerCondition[]? cond = null, TriggerAction[]? act = null) => new()
        {
            Name = name,
            Events     = ev   ?? new[] { Ev("match_start", 0) },
            Conditions = cond ?? System.Array.Empty<TriggerCondition>(),
            Actions    = act  ?? new[] { Act("display_message", 0) },
        };

        // ── Identity path: nothing reconciled, references untouched ─────────────────

        [Fact]
        public void Identity1v1_LeavesTriggersWinSpecAndNodes_ReferenceIdentical()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[] { Trigger("t", ev: new[] { Ev("unit_dies", 1) }, act: new[] { Act("victory", 0) }) };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 900 };
            baseMap.ResourceNodes = new[] { new ScenarioResourceNode { X = 0f, Z = 0f, CollectionModel = "Income", OwnerSlot = 1, IncomePeriodTicks = 30 } };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            // Every base slot survives with its own ordinal → the reconcile must not run at all: the ShallowClone's
            // shared references stay, so a plain 1v1 launch serializes byte-identically to the pre-DW-458 transform.
            Assert.Same(baseMap.Triggers, built.Triggers);
            Assert.Same(baseMap.WinConditionSpec, built.WinConditionSpec);
            Assert.Same(baseMap.ResourceNodes, built.ResourceNodes);
        }

        // ── Events ──────────────────────────────────────────────────────────────────

        [Fact]
        public void DroppedSlotEvent_IsStripped_TriggerKeptWhenAnotherEventRemains()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3); // 4-start map launched 1v1 → slots 2/3 dropped
            baseMap.Triggers = new[]
            {
                Trigger("multi", ev: new[] { Ev("unit_dies", 2), Ev("unit_dies", 0) }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            TriggerEvent kept = Assert.Single(t.Events);
            Assert.Equal(0, kept.Faction); // the dropped-slot event is gone; the kept one is untouched
        }

        [Fact]
        public void DroppedSlotEvent_WasTheOnlyEvent_DropsTheWholeTrigger()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("dead", ev: new[] { Ev("resource_threshold", 3) }),
                Trigger("alive"), // match_start — untouched
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            Assert.Equal("alive", t.Name);
        }

        [Fact]
        public void SystemKeyedEvents_NeverStripped_EvenWithDefaultFactionZero()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("sys", ev: new[]
                {
                    Ev("match_start", 0), Ev("timer_expires", 0), Ev("custom_event", 0),
                }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal(3, Assert.Single(built.Triggers).Events.Length);
        }

        // ── Conditions ──────────────────────────────────────────────────────────────

        [Fact]
        public void DroppedSlotCondition_DropsTheWholeTrigger()
        {
            // The author gated the trigger on a player who is not in this match. Stripping the guard would let the
            // trigger fire when it must not — so the whole trigger is pruned instead.
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("guarded", cond: new[] { Cond("unit_count", 2) }),
                Trigger("alive"),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal("alive", Assert.Single(built.Triggers).Name);
        }

        [Fact]
        public void VariableComparison_PerPlayerVar_IsSlotKeyed_GlobalVarIsNot()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Variables = new[]
            {
                new ScenarioVariable { Name = "pp", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
                new ScenarioVariable { Name = "gg", Type = DslValueType.Int, Scope = VarScope.Global },
            };
            baseMap.Triggers = new[]
            {
                Trigger("perPlayerDropped", cond: new[] { new TriggerCondition { Type = "variable_comparison", Variable = "pp", Faction = 3 } }),
                Trigger("globalUntouched",  cond: new[] { new TriggerCondition { Type = "variable_comparison", Variable = "gg", Faction = 3 } }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            // The PerPlayer comparison names a dropped slot → its trigger is pruned; the Global comparison's faction
            // field is inert (DslVarTable ignores it for Global vars) → its trigger survives untouched.
            TriggerDefinition t = Assert.Single(built.Triggers);
            Assert.Equal("globalUntouched", t.Name);
            Assert.Equal(3, Assert.Single(t.Conditions).Faction); // inert field NOT rewritten
        }

        // ── Actions ─────────────────────────────────────────────────────────────────

        [Fact]
        public void DroppedSlotAction_IsStripped_TriggerDroppedWhenNoActionsLeft()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("partial", act: new[] { Act("add_resources", 2), Act("display_message", 0) }),
                Trigger("emptied", act: new[] { Act("spawn_unit", 3) }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            Assert.Equal("partial", t.Name);
            TriggerAction kept = Assert.Single(t.Actions);
            Assert.Equal("display_message", kept.Type); // the dropped-slot credit is gone, the message stays
        }

        // ── Sparse base map: kept references REWRITTEN to the new contiguous ordinals ─

        [Fact]
        public void SparseBaseMap_KeptReferences_RewrittenToContiguousOrdinals()
        {
            // Base map authors start positions 0 and 3 (validator-legal sparse ordinals). A 1v1 pairs the two active
            // slots positionally → base slot 3 becomes contiguous ordinal 1. Every kept per-slot reference must follow.
            ScenarioData baseMap = BaseMap(0, 3);
            baseMap.Triggers = new[]
            {
                Trigger("t",
                    ev:   new[] { Ev("unit_dies", 3) },
                    cond: new[] { Cond("resource_comparison", 3) },
                    act:  new[] { Act("victory", 3) }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            Assert.Equal(1, Assert.Single(t.Events).Faction);
            Assert.Equal(1, Assert.Single(t.Conditions).Faction);
            Assert.Equal(1, Assert.Single(t.Actions).Faction);
            // And the base map's own trigger objects were never mutated (fresh instances throughout).
            Assert.Equal(3, baseMap.Triggers[0].Events[0].Faction);
        }

        // ── Win-condition preset spec ───────────────────────────────────────────────

        [Fact]
        public void TimedSurvival_DesignatedSlotDropped_PrunesSpecToBuiltInFallback()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.WinCondition = WinCondition.DestroyAllBuildings;
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 2, SurviveTicks = 900 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Null(built.WinConditionSpec); // honest degradation → the built-in WinCondition enum path
            Assert.Equal(WinCondition.DestroyAllBuildings, built.WinCondition);
        }

        [Fact]
        public void TimedSurvival_DesignatedSlotKept_RemappedOnSparseMap()
        {
            ScenarioData baseMap = BaseMap(0, 3);
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 3, SurviveTicks = 900 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.NotNull(built.WinConditionSpec);
            Assert.Equal(WinPresetKind.TimedSurvival, built.WinConditionSpec!.Preset);
            Assert.Equal(1, built.WinConditionSpec.FactionSlot);
            Assert.Equal(900, built.WinConditionSpec.SurviveTicks);
            Assert.Equal(3, baseMap.WinConditionSpec!.FactionSlot); // base spec untouched
        }

        [Fact]
        public void Assassination_LeaderIndexFollowsTheFilteredUnitsArray()
        {
            // Units authored: [slot2-worker, slot0-worker, slot1-worker]. The slot2 leader-carrier launch drops
            // index 0, so the designated leader at authored index 1 (slot0) becomes rebuilt index 0.
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 2, X = 1f, Z = 1f },
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = 2f, Z = 2f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X = 3f, Z = 3f },
            };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 1 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal(2, built.Units.Length);
            Assert.NotNull(built.WinConditionSpec);
            Assert.Equal(0, built.WinConditionSpec!.LeaderUnitIndex);
            Assert.Equal(0, built.Units[built.WinConditionSpec.LeaderUnitIndex].Slot); // still the same authored unit
        }

        [Fact]
        public void Assassination_LeaderOnDroppedSlot_PrunesSpecToNull()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = 1f, Z = 1f },
                new ScenarioUnit { UnitId = "worker", Slot = 3, X = 2f, Z = 2f }, // the designated leader — dropped
            };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 1 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Null(built.WinConditionSpec);
        }

        [Fact]
        public void LandmarkDestruction_StructureIndexFollowsTheFilteredBuildingsArray()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 3, X = 1f, Z = 1f }, // dropped
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X = 2f, Z = 2f }, // → rebuilt index 0
            };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 1 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            ScenarioBuilding only = Assert.Single(built.Buildings);
            Assert.NotNull(built.WinConditionSpec);
            Assert.Equal(0, built.WinConditionSpec!.StructureIndex);
            Assert.Equal(1, only.Slot);
        }

        [Fact]
        public void LandmarkDestruction_LandmarkOnDroppedSlot_PrunesSpecToNull()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 2, X = 1f, Z = 1f } };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Null(built.WinConditionSpec);
        }

        [Fact]
        public void KingOfTheHill_IsRegionKeyed_PassesThroughUntouched()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "hill", HoldTicks = 300 };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Same(baseMap.WinConditionSpec, built.WinConditionSpec);
        }

        // ── Resource-node owners ────────────────────────────────────────────────────

        [Fact]
        public void IncomeNode_OwnedByDroppedSlot_IsDropped()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = 1f, Z = 1f, CollectionModel = "Income", OwnerSlot = 2, IncomePeriodTicks = 30 },
                new ScenarioResourceNode { X = 2f, Z = 2f }, // plain Gather — untouched
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            ScenarioResourceNode kept = Assert.Single(built.ResourceNodes);
            Assert.Equal("Gather", kept.CollectionModel);
            Assert.Same(baseMap.ResourceNodes[1], kept); // an untouched node keeps its original reference
        }

        [Fact]
        public void IncomeNode_OwnerRemapped_OnSparseMap_AndInertGatherOwnerNormalized()
        {
            ScenarioData baseMap = BaseMap(0, 3);
            baseMap.ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = 1f, Z = 1f, CollectionModel = "Income", OwnerSlot = 3, IncomePeriodTicks = 30 },
                new ScenarioResourceNode { X = 2f, Z = 2f, CollectionModel = "Gather", OwnerSlot = 7 }, // inert, dangling
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal(2, built.ResourceNodes.Length);
            Assert.Equal(1, built.ResourceNodes[0].OwnerSlot);   // Income owner follows the renumber
            Assert.Equal(-1, built.ResourceNodes[1].OwnerSlot);  // inert dangling owner normalized to unset
            Assert.Equal(3, baseMap.ResourceNodes[0].OwnerSlot); // base map untouched
            Assert.Equal(7, baseMap.ResourceNodes[1].OwnerSlot);
        }

        // ── The end-to-end property: a reconciled launch passes the boot validator ──

        [Fact]
        public void FourStartMapWithPerSlotLogic_Launched1v1_PassesTheBootValidator()
        {
            // THE REGRESSION. A shipped-shaped 4-start map with per-slot triggers, a TimedSurvival preset on slot 3,
            // and an Income node owned by slot 2, launched as the honest 1v1. Before DW-458 the built scenario kept
            // all three dangling references — and the TimedSurvival faction_slot=3 / Income owner_slot=2 rejects at
            // ScenarioLoadPhase's fail-closed gate, silently substituting the FALLBACK map for the chosen one.
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("p3 dies", ev: new[] { Ev("unit_dies", 3) }, act: new[] { Act("add_resources", 3) }),
                Trigger("p1 dies", ev: new[] { Ev("unit_dies", 0) }, act: new[] { Act("add_resources", 1) }),
            };
            baseMap.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 3, SurviveTicks = 900 };
            baseMap.ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = 1f, Z = 1f, CollectionModel = "Income", OwnerSlot = 2, IncomePeriodTicks = 30 },
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            ValidationResult r = new ScenarioValidator().Validate(built);
            Assert.True(r.Ok, r.Error); // dangling refs reconciled away → the chosen map actually boots

            // And the reconcile was surgical: the P1-keyed trigger survived intact.
            TriggerDefinition kept = Assert.Single(built.Triggers);
            Assert.Equal("p1 dies", kept.Name);
            Assert.Equal(1, kept.Actions[0].Faction);
        }
    }
}
