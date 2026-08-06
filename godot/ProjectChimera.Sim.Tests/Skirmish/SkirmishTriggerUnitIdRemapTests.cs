#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core;               // Fixed, Faction
using ProjectChimera.Core.Definitions;   // ScenarioData, TriggerDefinition, ScenarioValidator, FactionDefinition
using ProjectChimera.Core.Skirmish;      // SkirmishSetup, SetupSlot, SlotKind, FactionEntry, SkirmishSetupToScenario
using ProjectChimera.Dsl;                // TriggerGraph, ActionNode, EventNode
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-665 — <c>SkirmishSetupToScenario.Build</c> remapped a map's PRE-PLACED unit ids across factions
    /// (<c>SkirmishRosterMap.MapUnitId</c>: alpha's "worker" becomes beta's "forgehand") but copied a trigger
    /// action's <c>unit_id</c> VERBATIM, and on the identity-slot path never rewrote the base triggers at all.
    ///
    /// <para>Before DW-240 that cost one skipped spawn plus a runtime log line. Since DW-240 the ScenarioValidator
    /// fail-closes on an unresolvable <c>spawn_unit</c> <c>unit_id</c> in BOTH channels, and
    /// <c>ScenarioLoadPhase</c> discards the ENTIRE scenario on a failed validate and boots the flat fallback map.
    /// So one authored <c>{"type":"spawn_unit","unit_id":"worker","faction":0}</c> trigger turned every
    /// cross-faction skirmish launch of that map into "wrong map loaded" — a far larger blast radius than the
    /// defect DW-240 closed. These tests pin the root-cause fix: the ids are simply WRONG for the chosen faction,
    /// validator or not, so they are translated through the same role remap the pre-placed units use.</para>
    /// </summary>
    public class SkirmishTriggerUnitIdRemapTests
    {
        // ── Synthetic catalog: the 8-unit role skeleton, disjoint ids, mirroring the shipped alpha/beta pair ──

        private const string AlphaRes = "res://factions/alpha_faction.json";
        private const string BetaRes  = "res://factions/beta_faction.json";

        private static IReadOnlyList<FactionEntry> Factions() => new List<FactionEntry>
        {
            new()
            {
                Id = "alpha", DisplayName = "alpha", ResPath = AlphaRes,
                Units = new List<FactionUnitEntry>
                {
                    new() { Id = "worker",   Category = "Worker" },
                    new() { Id = "infantry", Category = "Melee"  },
                    new() { Id = "archer",   Category = "Ranged" },
                    new() { Id = "mage",     Category = "Ranged" },
                },
            },
            new()
            {
                Id = "beta", DisplayName = "beta", ResPath = BetaRes,
                Units = new List<FactionUnitEntry>
                {
                    new() { Id = "forgehand",   Category = "Worker" },
                    new() { Id = "footsoldier", Category = "Melee"  },
                    new() { Id = "crossbowman", Category = "Ranged" },
                    new() { Id = "rune_caster", Category = "Ranged" },
                },
            },
            // A legitimately SELECTABLE faction with no Ranged role at all — FactionValidator.ValidateComplete
            // only guarantees a Worker plus one combat unit, so this is the unmappable-role case.
            new()
            {
                Id = "gamma", DisplayName = "gamma", ResPath = "res://factions/gamma_faction.json",
                Units = new List<FactionUnitEntry>
                {
                    new() { Id = "drudge",  Category = "Worker" },
                    new() { Id = "brawler", Category = "Melee"  },
                },
            },
        };

        private static SetupSlot Human(int slot, string faction = "alpha") =>
            new() { Slot = slot, Kind = SlotKind.Human, FactionId = faction };

        private static SetupSlot Ai(int slot, string faction) =>
            new() { Slot = slot, Kind = SlotKind.Ai, FactionId = faction, Ai = AiDifficulty.Normal };

        /// <summary>A 2-player launch: P1 alpha, P2 <paramref name="p2"/>.</summary>
        private static SkirmishSetup Setup(string p2) =>
            new() { MapId = "m1", Slots = new List<SetupSlot> { Human(0), Ai(1, p2) } };

        /// <summary>A base map with the given start-position ORDINALS, every one authored against alpha.</summary>
        private static ScenarioData BaseMap(params int[] slotOrdinals)
        {
            var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
            m.PlayerSlots = slotOrdinals.Select(o => new ScenarioPlayerSlot
            {
                Slot = o, FactionJson = AlphaRes, StartOre = 200f, BaseX = -45f + o * 15f, BaseZ = 0f,
            }).ToArray();
            return m;
        }

        private static TriggerAction Spawn(string unitId, int faction) => new()
        {
            Type = "spawn_unit", UnitId = unitId, Faction = faction,
            X = Fixed.FromInt(10), Z = Fixed.Zero, Count = 2,
        };

        private static TriggerDefinition Trigger(string name, params TriggerAction[] actions) => new()
        {
            Name = name,
            Events  = new[] { new TriggerEvent { Type = "match_start" } },
            Actions = actions,
        };

        private static string GraphWith(params NodeBase[] nodes)
        {
            var g = new TriggerGraph();
            g.Nodes.AddRange(nodes);
            return g.ToCanonicalJson();
        }

        private static IReadOnlyList<NodeBase> NodesOf(string? json) =>
            TriggerGraph.FromJson(json!).Nodes.OrderBy(n => n.Id).ToList();

        private static TriggerAction OnlyAction(ScenarioData built) =>
            Assert.Single(Assert.Single(built.Triggers).Actions);

        // ── The identity-SLOT path (the shipped 2-start map launched 1v1) ───────────────────────────────────────
        //    Every ordinal survives unchanged, so the DW-458/DW-609 reconcile is correctly skipped — and before
        //    this fix that meant the base triggers were never rewritten AT ALL, faction swap or not.

        [Fact]
        public void IdentitySlots_CrossFaction_FlatSpawnUnitId_IsTranslatedToTheChosenRoster()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[] { Trigger("reinforce", Spawn("worker", faction: 1)) };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            // Slot 1 plays beta, whose Worker-role unit is "forgehand". Pre-fix this stayed "worker", which
            // resolves in no beta roster → DW-240 rejects → the whole map is discarded at boot.
            Assert.Equal("forgehand", OnlyAction(built).UnitId);
            Assert.Equal(1, OnlyAction(built).Faction); // the ordinal itself never moves on this path
        }

        [Fact]
        public void IdentitySlots_CrossFaction_TranslatesByROLE_NotByPosition()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            // alpha's "mage" is (Ranged, ordinal 1) → beta's "rune_caster", NOT beta's first Ranged unit.
            baseMap.Triggers = new[] { Trigger("summon", Spawn("mage", faction: 1)) };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            Assert.Equal("rune_caster", OnlyAction(built).UnitId);
        }

        [Fact]
        public void IdentitySlots_CrossFaction_OtherSlotsSpawnIsUntouched()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[]
            {
                Trigger("p1", Spawn("worker", faction: 0)),   // P1 kept alpha → identity
                Trigger("p2", Spawn("worker", faction: 1)),   // P2 chose beta → translated
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            Assert.Equal(2, built.Triggers.Length);
            Assert.Equal("worker",    built.Triggers[0].Actions[0].UnitId);
            Assert.Equal("forgehand", built.Triggers[1].Actions[0].UnitId);
        }

        [Fact]
        public void IdentitySlots_CrossFaction_GraphSpawnUnitId_IsTranslatedToo()
        {
            // The graph channel merges into the SAME execution walk and hits the SAME DW-240 gate
            // (ScenarioValidator :941-944), so it must be translated identically.
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.TriggerGraphJson = GraphWith(
                new ActionNode { Id = 0, Kind = "spawn_unit", UnitId = "archer", Faction = 1 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            var act = Assert.IsType<ActionNode>(NodesOf(built.TriggerGraphJson)[0]);
            Assert.Equal("crossbowman", act.UnitId); // alpha (Ranged,0) → beta (Ranged,0)
            Assert.Equal(1, act.Faction);
        }

        [Fact]
        public void IdentitySlots_CrossFaction_NonSpawnActionsAndOtherFieldsSurviveIntact()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            var spawn = Spawn("worker", faction: 1);
            spawn.Text = "reinforcements"; spawn.TimerName = "t1"; spawn.Variable = "v"; spawn.Value = 7;
            spawn.SoundId = "s"; spawn.Amount = Fixed.FromInt(9); spawn.Duration = Fixed.FromInt(3);
            spawn.TimerSeconds = Fixed.FromInt(11);
            var msg = new TriggerAction { Type = "display_message", Text = "hello", UnitId = "worker", Faction = 1 };
            baseMap.Triggers = new[] { Trigger("mixed", spawn, msg) };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            TriggerAction[] acts = Assert.Single(built.Triggers).Actions;
            Assert.Equal(2, acts.Length);
            // Every non-id field of the rewritten action rides across (the clone is field-complete).
            Assert.Equal("forgehand", acts[0].UnitId);
            Assert.Equal("reinforcements", acts[0].Text);
            Assert.Equal("t1", acts[0].TimerName);
            Assert.Equal("v", acts[0].Variable);
            Assert.Equal(7, acts[0].Value);
            Assert.Equal("s", acts[0].SoundId);
            Assert.Equal(2, acts[0].Count);
            Assert.Equal(Fixed.FromInt(10), acts[0].X);
            Assert.Equal(Fixed.FromInt(9), acts[0].Amount);
            Assert.Equal(Fixed.FromInt(3), acts[0].Duration);
            Assert.Equal(Fixed.FromInt(11), acts[0].TimerSeconds);
            // A non-spawn action's UnitId is inert — the remap is scoped to spawn_unit and must not touch it.
            Assert.Same(msg, acts[1]);
        }

        // ── The identity-FACTION guard: a same-faction launch must stay byte-identical ──────────────────────────

        [Fact]
        public void IdentitySlots_SameFaction_LeavesTriggersAndGraph_ReferenceIdentical()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[] { Trigger("reinforce", Spawn("worker", faction: 1)) };
            baseMap.TriggerGraphJson = GraphWith(
                new ActionNode { Id = 0, Kind = "spawn_unit", UnitId = "worker", Faction = 1 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("alpha"), baseMap, Factions());

            // No slot moved AND no faction moved → the remap must not run at all: same references, so the launch
            // serializes byte-identically to the base map (the DW-458 identity-path contract, preserved).
            Assert.Same(baseMap.Triggers, built.Triggers);
            Assert.Same(baseMap.TriggerGraphJson, built.TriggerGraphJson);
        }

        [Fact]
        public void IdentitySlots_CrossFaction_GraphWithNoSpawnNode_IsReturnedVerbatim()
        {
            // The remap arms on the faction swap, but a graph it does not actually change must be re-emitted
            // VERBATIM — never re-canonicalized, which would move an authored-but-uncanonical graph's bytes.
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.TriggerGraphJson = GraphWith(new EventNode { Id = 0, Kind = "unit_dies", Faction = 1 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            Assert.Same(baseMap.TriggerGraphJson, built.TriggerGraphJson);
        }

        // ── The renumbering (DW-458/DW-609) path: `UnitId = a.UnitId` at the ReconcileTriggers clone ────────────

        [Fact]
        public void DroppedSlots_CrossFaction_FlatSpawnUnitId_IsTranslated()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2, 3); // 4-start map launched 1v1 → slots 2/3 dropped
            baseMap.Triggers = new[] { Trigger("reinforce", Spawn("worker", faction: 1)) };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            Assert.Equal("forgehand", OnlyAction(built).UnitId);
        }

        [Fact]
        public void RenumberedSurvivor_CrossFaction_SpawnId_FollowsThatPlayersChosenFaction()
        {
            // Authored {0,2,5}; a 1v1 pairs the two LOWEST ordinals, so authored slot 2 renumbers to ordinal 1 —
            // the launch index whose player chose beta. The translation must key on the player, not the ordinal.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.Triggers = new[] { Trigger("reinforce", Spawn("infantry", faction: 2)) };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            TriggerAction a = OnlyAction(built);
            Assert.Equal(1, a.Faction);                 // DW-458: the ordinal follows the player
            Assert.Equal("footsoldier", a.UnitId);      // DW-665: so does the roster the id is read in
        }

        [Fact]
        public void DroppedSlots_CrossFaction_GraphSpawnUnitId_IsTranslatedBeforeTheSlotRedirect()
        {
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(
                new ActionNode { Id = 0, Kind = "spawn_unit", UnitId = "mage", Faction = 2 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            var act = Assert.IsType<ActionNode>(NodesOf(built.TriggerGraphJson)[0]);
            // Reading the id in the POST-redirect frame (ordinal 1 → whatever now sits there) would be wrong for
            // any map where the renumber moves a slot; pinning both halves at once is what proves the ordering.
            Assert.Equal(1, act.Faction);
            Assert.Equal("rune_caster", act.UnitId);
        }

        [Fact]
        public void DroppedSlotSpawnAction_IsStillStripped_AndItsUnitIdNeverConsulted()
        {
            // The DW-458 rule runs first: an action owned by a player who is not in this match is stripped, so its
            // id is never translated (and a trigger left with no actions is dropped).
            ScenarioData baseMap = BaseMap(0, 1, 2, 3);
            baseMap.Triggers = new[]
            {
                Trigger("ghost", Spawn("worker", faction: 3)),
                Trigger("live",  Spawn("worker", faction: 1)),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("beta"), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            Assert.Equal("live", t.Name);
            Assert.Equal("forgehand", t.Actions[0].UnitId);
        }

        // ── Unmappable role: the chosen faction fields no unit of that category at all ──────────────────────────

        [Fact]
        public void UnmappableRole_FlatSpawnAction_IsStripped_RatherThanRejectingTheWholeMap()
        {
            // gamma has no Ranged unit, so alpha's "mage" has no equivalent. Emitting it would fail-close the
            // ENTIRE scenario at the DW-240 gate; stripping the one action restores the pre-DW-240 blast radius
            // (exactly the pre-placed-unit rule at the top of Build).
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[]
            {
                Trigger("mixed", Spawn("mage", faction: 1), new TriggerAction { Type = "display_message", Text = "hi" }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("gamma"), baseMap, Factions());

            TriggerDefinition t = Assert.Single(built.Triggers);
            TriggerAction kept = Assert.Single(t.Actions);
            Assert.Equal("display_message", kept.Type);
        }

        [Fact]
        public void UnmappableRole_LastActionStripped_DropsTheWholeTrigger()
        {
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.Triggers = new[]
            {
                Trigger("only_spawn", Spawn("mage", faction: 1)),
                Trigger("survivor", new TriggerAction { Type = "display_message", Text = "hi" }),
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("gamma"), baseMap, Factions());

            Assert.Equal("survivor", Assert.Single(built.Triggers).Name);
        }

        [Fact]
        public void UnmappableRole_GraphSpawnNode_IsLeftVerbatimForTheValidator()
        {
            // The graph channel may only REWRITE — DW-609's rule is that graph STRUCTURE is never altered, since
            // pruning a node severs exec/data edges. So an unmappable id stays put and the validator reports it
            // with its located message rather than this transform silently severing the graph.
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.TriggerGraphJson = GraphWith(
                new ActionNode { Id = 0, Kind = "spawn_unit", UnitId = "mage", Faction = 1 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup("gamma"), baseMap, Factions());

            var act = Assert.IsType<ActionNode>(NodesOf(built.TriggerGraphJson)[0]);
            Assert.Equal("mage", act.UnitId);
            Assert.Single(NodesOf(built.TriggerGraphJson)); // the node is still there — nothing was pruned
        }

        // ── End to end over the REAL shipped map + factions: the whole-map reject is what this closes ───────────

        private static string GodotDir([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

        private static string ScenariosAbs => Path.Combine(GodotDir(), "resources", "data", "scenarios");
        private static string FactionsAbs  => Path.Combine(GodotDir(), "resources", "data", "factions");

        /// <summary>The boot pre-pass, verbatim: per-slot defs from the REAL faction files + ability resolve.</summary>
        private static FactionDefinition?[] SlotDefs(string p2FactionId)
        {
            FactionDefinition LoadDef(string file)
            {
                var def = FactionDefinition.LoadFromFile(Path.Combine(FactionsAbs, file));
                foreach (var u in def.Units) u.ResolveAbilities(AbilityRegistry.Empty);
                UnitTagValidator.ValidateAndDropUnits(def);
                return def;
            }
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = LoadDef("alpha_faction.json");
            slotDefs[(int)Faction.Player2] = LoadDef($"{p2FactionId}_faction.json");
            return slotDefs;
        }

        /// <summary>The real shipped <c>alpha_map_01</c> (2 alpha-authored start positions) plus ONE authored
        /// <c>spawn_unit</c> trigger for slot 1 — the map shape the ledger entry describes.</summary>
        private static ScenarioData ShippedMapWithSpawnTrigger()
        {
            ScenarioData baseMap = ScenarioSerializer.LoadFromFile(Path.Combine(ScenariosAbs, "alpha_map_01.json"))!;
            baseMap.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "reinforce",
                    Events  = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[]
                    {
                        new TriggerAction
                        {
                            Type = "spawn_unit", UnitId = "worker", Faction = 1,
                            X = Fixed.FromInt(40), Z = Fixed.FromInt(6), Count = 1,
                        },
                    },
                },
            };
            return baseMap;
        }

        private static SkirmishSetup RealSetup(string p2) => new()
        {
            MapId = "alpha_map_01",
            Slots = new List<SetupSlot> { Human(0), Ai(1, p2) },
        };

        [Fact]
        public void RealCrossFactionLaunch_WithASpawnUnitTrigger_ClearsTheBootValidator()
        {
            IReadOnlyList<FactionEntry> factions =
                SkirmishCatalog.ScanFactions(FactionsAbs, "res://resources/data/factions");
            Assert.Contains(factions, f => f.Id == "beta"); // the real catalog actually scanned

            ScenarioData built = SkirmishSetupToScenario.Build(
                RealSetup("beta"), ShippedMapWithSpawnTrigger(), factions);

            // The root-cause assertion: the id now names a unit in the roster slot 1 actually plays.
            Assert.Equal("forgehand", built.Triggers[0].Actions[0].UnitId);

            // …and therefore the built scenario clears the SAME fail-closed gate ScenarioLoadPhase runs, instead
            // of being discarded whole in favour of the flat fallback map.
            ValidationResult r = new ScenarioValidator().Validate(built, SlotDefs("beta"));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void TheUnRemappedId_StillFailsThatValidator_SoTheAssertionAboveIsLoadBearing()
        {
            // The control for the test above: force the pre-fix value back and watch DW-240's gate reject the
            // WHOLE scenario. Without this, a remap that quietly did nothing would still look green.
            ScenarioData built = SkirmishSetupToScenario.Build(
                RealSetup("beta"), ShippedMapWithSpawnTrigger(),
                SkirmishCatalog.ScanFactions(FactionsAbs, "res://resources/data/factions"));
            built.Triggers[0].Actions[0].UnitId = "worker"; // as the verbatim copy left it

            ValidationResult r = new ScenarioValidator().Validate(built, SlotDefs("beta"));

            Assert.False(r.Ok);
            Assert.Contains("unit_id", r.Error);
        }

        [Fact]
        public void RealSameFactionLaunch_WithASpawnUnitTrigger_IsUnchangedAndStillValidates()
        {
            ScenarioData baseMap = ShippedMapWithSpawnTrigger();
            TriggerDefinition[] authored = baseMap.Triggers;

            ScenarioData built = SkirmishSetupToScenario.Build(
                RealSetup("alpha"), baseMap,
                SkirmishCatalog.ScanFactions(FactionsAbs, "res://resources/data/factions"));

            Assert.Same(authored, built.Triggers); // no faction moved → nothing rewritten
            ValidationResult r = new ScenarioValidator().Validate(built, SlotDefs("alpha"));
            Assert.True(r.Ok, r.Error);
        }
    }
}
