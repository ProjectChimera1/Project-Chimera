#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectChimera.AI;                    // AiDifficulty
using ProjectChimera.Core;                   // DW-740: Faction, AllianceStore, AllianceSeeder (the sim-side alliance mask)
using ProjectChimera.Core.Bootstrap;         // ISetupPhase, ScenePhaseRunner, ScenePhaseOrder
using ProjectChimera.Core.Definitions;       // ScenarioData, ScenarioPlayerSlot, ScenarioSerializer, FactionDefinition, UnitTagValidator, FactionValidator
using ProjectChimera.Core.Skirmish;          // SkirmishSetup, SetupSlot, SlotKind, MapEntry, FactionEntry, SkirmishCatalog, SkirmishSetupValidator, SkirmishSetupToScenario
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// Story 11.1 — Tier-1 verification of the whole Godot-free skirmish core: the validator (each I/O-matrix rule +
    /// all-errors aggregation), the transform (correct PlayerSlots, Open/Closed dropped, determinism, no Player1
    /// assumption), the catalog (temp-dir map + faction scan incl. empty dir), the ScenePhaseRunner progress seam, and
    /// the DW-121 path-route closure. Everything here is Godot-free (writes real files to temp dirs, mirroring
    /// <c>FactionDiscoveryTests</c>).
    /// </summary>
    public class SkirmishSetupTests
    {
        // ── Builders ────────────────────────────────────────────────────────────────

        private static MapEntry Map(int startPositions, string id = "m1") => new()
        {
            Id = id, DisplayName = id, ResPath = $"res://maps/{id}.json",
            MapBounds = 120f, SuggestedPlayers = 2, StartPositionCount = startPositions, Author = "",
        };

        /// <summary>Faction entries carrying a real role skeleton, because the production catalog always populates one
        /// and the cross-faction unit remap resolves against it. "alpha" keeps the bare ids the shipped maps author
        /// (<c>worker</c>/<c>infantry</c>/<c>archer</c>); every other faction gets DISJOINT ids at the same roles —
        /// exactly the alpha-vs-beta shape that broke in-engine.</summary>
        private static IReadOnlyList<FactionEntry> Factions(params string[] ids) =>
            ids.Select(i => new FactionEntry
            {
                Id = i, DisplayName = i, ResPath = $"res://factions/{i}_faction.json", Units = Roster(i),
            }).ToList();

        private static IReadOnlyList<FactionUnitEntry> Roster(string factionId)
        {
            string p = factionId == "alpha" ? "" : factionId + "_";
            return new List<FactionUnitEntry>
            {
                new() { Id = p + "worker",   Category = "Worker" },
                new() { Id = p + "infantry", Category = "Melee"  },
                new() { Id = p + "archer",   Category = "Ranged" },
            };
        }

        /// <summary>A faction entry with a hand-authored roster, for the role-mapping edge cases.</summary>
        private static FactionEntry FactionWith(string id, params (string Id, string Category)[] units) => new()
        {
            Id = id, DisplayName = id, ResPath = $"res://factions/{id}_faction.json",
            Units = units.Select(u => new FactionUnitEntry { Id = u.Id, Category = u.Category }).ToList(),
        };

        private static SetupSlot Slot(int slot, SlotKind kind, string? faction = "alpha", int team = 0,
                                      AiDifficulty ai = AiDifficulty.Normal) =>
            new() { Slot = slot, Kind = kind, FactionId = faction, Team = team, Ai = ai };

        private static SkirmishSetup Setup(string mapId, params SetupSlot[] slots) =>
            new() { MapId = mapId, Slots = slots.ToList() };

        private static ScenarioData BaseMap(int slots)
        {
            var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
            var ps = new ScenarioPlayerSlot[slots];
            for (int i = 0; i < slots; i++)
                ps[i] = new ScenarioPlayerSlot
                {
                    // Authored against "alpha" — the shipped-map shape: pre-placed ids come from ONE faction's roster.
                    Slot = i, FactionJson = "res://factions/alpha_faction.json",
                    StartOre = 200f + i, StartCrystal = 10f + i, BaseX = -45f + i * 90f, BaseZ = i * 2f,
                };
            m.PlayerSlots = ps;
            return m;
        }

        // ── Validator: I/O-matrix rows ────────────────────────────────────────────────

        [Fact]
        public void Valid1v1_NoErrors()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", 0), Slot(1, SlotKind.Ai, "beta", 0));
            Assert.Empty(v.Validate(s, Map(2), Factions("alpha", "beta")));
        }

        [Fact]
        public void NoHumanSlot_Blocked()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Ai, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            IReadOnlyList<string> errs = v.Validate(s, Map(2), Factions("alpha", "beta"));
            Assert.Contains(errs, e => e.Contains("Human"));
        }

        [Fact]
        public void NoOpponent_Blocked()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Open, null));
            IReadOnlyList<string> errs = v.Validate(s, Map(2), Factions("alpha"));
            Assert.Contains(errs, e => e.Contains("AI opponent is required"));
        }

        [Fact]
        public void MoreThanOneAi_Blocked_HonestMessage()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"),
                                          Slot(1, SlotKind.Ai, "beta"), Slot(2, SlotKind.Ai, "alpha"));
            IReadOnlyList<string> errs = v.Validate(s, Map(3), Factions("alpha", "beta"));
            Assert.Contains(errs, e => e.Contains("Only one AI opponent is supported"));
        }

        [Fact]
        public void ActiveSlotsExceedMap_Blocked()
        {
            var v = new SkirmishSetupValidator();
            // 3 active on a 2-start map (also trips the >1 AI rule — both are reported, all-errors).
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"),
                                          Slot(1, SlotKind.Ai, "beta"), Slot(2, SlotKind.Ai, "alpha"));
            IReadOnlyList<string> errs = v.Validate(s, Map(2), Factions("alpha", "beta"));
            Assert.Contains(errs, e => e.Contains("supports 2 start positions"));
        }

        [Fact]
        public void UnknownFaction_Blocked()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "ghost"), Slot(1, SlotKind.Ai, "beta"));
            IReadOnlyList<string> errs = v.Validate(s, Map(2), Factions("alpha", "beta"));
            Assert.Contains(errs, e => e.Contains("Unknown faction: ghost"));
        }

        [Fact]
        public void ReturnsAllErrors_NotFirstFail()
        {
            var v = new SkirmishSetupValidator();
            // No human, no ai, unknown faction — three independent failures at once.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Open, null), Slot(1, SlotKind.Closed, null));
            IReadOnlyList<string> errs = v.Validate(s, Map(2), Factions("alpha"));
            Assert.Contains(errs, e => e.Contains("Human"));
            Assert.Contains(errs, e => e.Contains("AI opponent is required"));
            Assert.True(errs.Count >= 2);
        }

        // ── Validator: team-range rule (PATCH 8) ──────────────────────────────────────

        [Fact]
        public void TeamRange_AboveActiveCount_Blocked()
        {
            var v = new SkirmishSetupValidator();
            // activeCount = 2 → valid team range [0,2]. Team=3 is out of range (ai kept on its own side so only the
            // team-range rule trips).
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 0),
                                          Slot(1, SlotKind.Ai, "beta", team: 3));
            Assert.Contains(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("team must be between"));
        }

        [Fact]
        public void TeamRange_Negative_Blocked()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 0),
                                          Slot(1, SlotKind.Ai, "beta", team: -1));
            Assert.Contains(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("team must be between"));
        }

        [Fact]
        public void TeamRange_BoundaryEqualsActiveCount_Passes()
        {
            var v = new SkirmishSetupValidator();
            // activeCount = 2, Team = 2 is the inclusive upper bound. Human on side t2, AI on FFA side → opposing.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 2),
                                          Slot(1, SlotKind.Ai, "beta", team: 0));
            Assert.DoesNotContain(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("team must be between"));
        }

        [Fact]
        public void NullFaction_ActiveSlot_Blocked()
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, faction: null), Slot(1, SlotKind.Ai, "beta"));
            Assert.Contains(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("choose a faction"));
        }

        // ── Validator: allied-opponent rule (PATCH 2) ──────────────────────────────────

        [Fact]
        public void AlliedOpponent_SameTeam_Blocked()
        {
            var v = new SkirmishSetupValidator();
            // Human and AI both on positive team 1 → one side → no real opponent.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 1),
                                          Slot(1, SlotKind.Ai, "beta", team: 1));
            Assert.Contains(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("Opposing sides"));
        }

        [Theory]
        [InlineData(0, 0)] // both FFA → distinct sides
        [InlineData(0, 1)] // one FFA, one teamed → distinct sides
        [InlineData(1, 2)] // two distinct positive teams → distinct sides
        public void OpposingSides_Configs_NoAlliedError(int humanTeam, int aiTeam)
        {
            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: humanTeam),
                                          Slot(1, SlotKind.Ai, "beta", team: aiTeam));
            Assert.DoesNotContain(v.Validate(s, Map(2), Factions("alpha", "beta")), e => e.Contains("Opposing sides"));
        }

        // ── DW-740: the lobby team assignment must survive the transform INTO the sim alliance mask ──────────

        /// <summary>
        /// DW-740 — the end-to-end link the ai-alliance-awareness bundle (DW-439/DW-445) never verified: a skirmish
        /// LOBBY that puts an AI on the human's team must actually reach <see cref="AllianceSeeder.Seed"/> with the
        /// right per-slot Team ordinals. If <see cref="SkirmishSetupToScenario.Build"/> dropped or mis-keyed
        /// <c>SetupSlot.Team</c>, every skirmish slot would keep the FFA default (<c>TeamId[f]==f</c>), the new
        /// IsHostile path would never engage, and teamed-AI skirmish would silently behave exactly as it did before
        /// the fix — while the ledger read closed. The sim-side seeding rules have their own oracles
        /// (<c>AllianceSeederTests</c>); what is pinned HERE is the lobby→scenario→mask handoff, on the shape the
        /// entry names: a 3-slot lobby with the human and one AI on team 1 and a second AI on team 2.
        /// </summary>
        [Fact]
        public void Build_ThenSeed_AlliesTheTeamedAiWithItsHumanTeammate_AndNotTheOpposingAi()
        {
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 1),
                                          Slot(1, SlotKind.Ai,    "beta",  team: 1),
                                          Slot(2, SlotKind.Ai,    "gamma", team: 2));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(3), Factions("alpha", "beta", "gamma"));

            // The transform carried each lobby ordinal onto its contiguous scenario slot (the seeder's only input).
            Assert.Equal(new[] { 1, 1, 2 }, built.PlayerSlots.Select(p => p.Team).ToArray());

            var alliances = new AllianceStore();
            AllianceSeeder.Seed(alliances, built);

            // Team 1 = {Player1, Player2} → canonical id 1 (the lowest member faction slot); team 2 = {Player3}.
            Assert.True(alliances.AreAllied(Faction.Player1, Faction.Player2),
                        "The lobby put the AI on the human's team, but the seeded mask left them hostile — the team " +
                        "ordinal did not survive SkirmishSetupToScenario.Build into AllianceSeeder.Seed (DW-740).");
            Assert.False(alliances.AreAllied(Faction.Player1, Faction.Player3)); // the opposing AI stays hostile
            Assert.False(alliances.AreAllied(Faction.Player2, Faction.Player3));
            Assert.Equal(1, alliances.TeamOf(Faction.Player1));
            Assert.Equal(1, alliances.TeamOf(Faction.Player2));
            Assert.Equal(3, alliances.TeamOf(Faction.Player3)); // a team of one seeds the FFA (own-slot) default
        }

        /// <summary>
        /// DW-740, the negative control: the SAME three-slot lobby with every slot on FFA (team 0) must seed the
        /// untouched default mask. Without this arm the assertion above could pass on a seeder that allied everyone.
        /// </summary>
        [Fact]
        public void Build_ThenSeed_FfaLobby_LeavesEverySlotHostile()
        {
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 0),
                                          Slot(1, SlotKind.Ai,    "beta",  team: 0),
                                          Slot(2, SlotKind.Ai,    "gamma", team: 0));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(3), Factions("alpha", "beta", "gamma"));

            var alliances = new AllianceStore();
            AllianceSeeder.Seed(alliances, built);

            Assert.False(alliances.AreAllied(Faction.Player1, Faction.Player2));
            Assert.False(alliances.AreAllied(Faction.Player1, Faction.Player3));
            Assert.False(alliances.AreAllied(Faction.Player2, Faction.Player3));
        }

        // ── Transform ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Build_Emits_CorrectPlayerSlots_For1v1()
        {
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 1),
                                          Slot(1, SlotKind.Ai, "beta", team: 2));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(2), Factions("alpha", "beta"));

            Assert.Equal(2, built.PlayerSlots.Length);

            ScenarioPlayerSlot p0 = built.PlayerSlots[0];
            Assert.Equal(0, p0.Slot);
            Assert.Equal("res://factions/alpha_faction.json", p0.FactionJson);
            Assert.Equal(1, p0.Team);
            Assert.Equal(-45f, p0.BaseX);      // carried from the base map slot 0
            Assert.Equal(0f, p0.BaseZ);        // carried from the base map slot 0 (BaseZ = i*2)
            Assert.Equal(200f, p0.StartOre);
            Assert.Equal(10f, p0.StartCrystal); // carried from the base map slot 0 (StartCrystal = 10 + i)

            ScenarioPlayerSlot p1 = built.PlayerSlots[1];
            Assert.Equal(1, p1.Slot);
            Assert.Equal("res://factions/beta_faction.json", p1.FactionJson);
            Assert.Equal(2, p1.Team);
            Assert.Equal(45f, p1.BaseX);       // carried from the base map slot 1
            Assert.Equal(2f, p1.BaseZ);        // carried from the base map slot 1
            Assert.Equal(11f, p1.StartCrystal); // carried from the base map slot 1
        }

        [Fact]
        public void Build_HumanSortsToContiguousIndex0_EvenWhenAiInLowerSlot()
        {
            // Review PATCH (11.1 follow-up): the AI occupies the lower setup slot (0) and the Human a higher one (1).
            // Offline the local human is Player1 (contiguous index 0) and the AI is Player2 (index 1). The Human MUST
            // still land on index 0 with ITS faction/team — otherwise the player would silently control the AI's
            // configured faction and the AI would pilot the player's. Human-first ordering guarantees this.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Ai, "beta", team: 2),
                                          Slot(1, SlotKind.Human, "alpha", team: 1));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(2), Factions("alpha", "beta"));

            Assert.Equal(2, built.PlayerSlots.Length);

            ScenarioPlayerSlot p0 = built.PlayerSlots[0]; // Player1 = local human → the Human's config
            Assert.Equal(0, p0.Slot);
            Assert.Equal("res://factions/alpha_faction.json", p0.FactionJson);
            Assert.Equal(1, p0.Team);

            ScenarioPlayerSlot p1 = built.PlayerSlots[1]; // Player2 = AI → the AI's config
            Assert.Equal(1, p1.Slot);
            Assert.Equal("res://factions/beta_faction.json", p1.FactionJson);
            Assert.Equal(2, p1.Team);
        }

        [Fact]
        public void Build_Drops_OpenAndClosedSlots()
        {
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"),
                                          Slot(2, SlotKind.Open, null), Slot(3, SlotKind.Closed, null));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(4), Factions("alpha", "beta"));

            Assert.Equal(2, built.PlayerSlots.Length);
            Assert.All(built.PlayerSlots, ps => Assert.True(ps.Slot == 0 || ps.Slot == 1));
        }

        [Fact]
        public void Build_IsDeterministic()
        {
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha", team: 1),
                                          Slot(1, SlotKind.Ai, "beta", team: 2));
            ScenarioData a = SkirmishSetupToScenario.Build(s, BaseMap(2), Factions("alpha", "beta"));
            ScenarioData b = SkirmishSetupToScenario.Build(s, BaseMap(2), Factions("alpha", "beta"));
            Assert.Equal(ScenarioSerializer.Serialize(a), ScenarioSerializer.Serialize(b));
        }

        [Fact]
        public void Build_ContiguousRenumber_PositionPairsBasePositions()
        {
            // PATCH 1: slot0 Open, Human=slot1, AI=slot2 on a 3-start base map. The active slots are renumbered to
            // CONTIGUOUS indices {0,1} (so the built scenario is Player1..Playerk contiguous — aligning with the
            // FactionRegistry span + ResolveSlotFactionDefs' per-ordinal writes), and each active slot is paired BY
            // POSITION with the base map's slots ordered by Slot: the human (original slot 1) → base slot index 0,
            // the AI (original slot 2) → base slot index 1.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Open, null),
                                          Slot(1, SlotKind.Human, "alpha"), Slot(2, SlotKind.Ai, "beta"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(3), Factions("alpha", "beta"));

            Assert.Equal(new[] { 0, 1 }, built.PlayerSlots.Select(p => p.Slot).ToArray());

            // BaseMap(3): slot i has BaseX = -45 + i*90, StartOre = 200 + i. Position-paired i-th active → i-th base.
            ScenarioPlayerSlot p0 = built.PlayerSlots[0]; // human, contiguous index 0 ← base slot 0
            Assert.Equal("res://factions/alpha_faction.json", p0.FactionJson);
            Assert.Equal(-45f, p0.BaseX);
            Assert.Equal(200f, p0.StartOre);

            ScenarioPlayerSlot p1 = built.PlayerSlots[1]; // ai, contiguous index 1 ← base slot 1
            Assert.Equal("res://factions/beta_faction.json", p1.FactionJson);
            Assert.Equal(45f, p1.BaseX);
            Assert.Equal(201f, p1.StartOre);
        }

        [Fact]
        public void Build_DoesNotMutate_BaseMap()
        {
            ScenarioData baseMap = BaseMap(2);
            int originalLen = baseMap.PlayerSlots.Length;
            string originalFaction = baseMap.PlayerSlots[0].FactionJson;

            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            SkirmishSetupToScenario.Build(s, baseMap, Factions("alpha", "beta"));

            Assert.Equal(originalLen, baseMap.PlayerSlots.Length);
            Assert.Equal(originalFaction, baseMap.PlayerSlots[0].FactionJson);
        }

        /// <summary>Builds a base map with `slots` start positions and one pre-placed building + one pre-placed unit
        /// per slot, each keyed to that slot's ORIGINAL ordinal (mirrors the shipped quad_map_01 authoring).</summary>
        private static ScenarioData BaseMapWithEntities(int slots)
        {
            ScenarioData m = BaseMap(slots);
            m.Buildings = Enumerable.Range(0, slots)
                .Select(i => new ScenarioBuilding { Type = "CommandCenter", Slot = i, X = i * 10f, Z = i })
                .ToArray();
            m.Units = Enumerable.Range(0, slots)
                .Select(i => new ScenarioUnit { UnitId = "worker", Slot = i, X = i * 10f, Z = i })
                .ToArray();
            return m;
        }

        [Fact]
        public void Build_DropsAndRemaps_PrePlacedEntities_ForDroppedSlots()
        {
            // Review PATCH (11.1 follow-up): launch the honest 1v1 on a 4-start map that ships pre-placed buildings/
            // units for all 4 slots (the shipped quad_map_01 shape). The two active slots pair to base positions 0/1;
            // the entities for the dropped base slots 2/3 must NOT survive into the built scenario (they would spawn as
            // ghost Player3/Player4 bases). Kept entities are re-keyed to the new contiguous owner index.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMapWithEntities(4), Factions("alpha", "beta"));

            // Only the two paired slots' entities survive, each re-keyed to the contiguous active index {0,1}.
            Assert.Equal(new[] { 0, 1 }, built.Buildings.Select(b => b.Slot).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 0, 1 }, built.Units.Select(u => u.Slot).OrderBy(x => x).ToArray());
            // No entity keyed to a slot beyond the active set survives.
            Assert.DoesNotContain(built.Buildings, b => b.Slot >= built.PlayerSlots.Length);
            Assert.DoesNotContain(built.Units, u => u.Slot >= built.PlayerSlots.Length);
        }

        [Fact]
        public void Build_KeepsAllEntities_When2SlotMapLaunched1v1()
        {
            // A 2-start map launched 1v1 drops nothing: every pre-placed entity is kept with an identity slot remap.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMapWithEntities(2), Factions("alpha", "beta"));

            Assert.Equal(new[] { 0, 1 }, built.Buildings.Select(b => b.Slot).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { 0, 1 }, built.Units.Select(u => u.Slot).OrderBy(x => x).ToArray());
        }

        // ── Cross-faction role remap (in-engine gate regression, 2026-07-28) ─────────────

        [Fact]
        public void Build_RemapsPrePlacedUnitIds_IntoTheChosenFactionRoster()
        {
            // THE REGRESSION. The map authors "worker" (alpha's id) for BOTH slots. Slot 1 chooses beta, whose roster is
            // disjoint. Before the fix the id survived untranslated, resolved to no UnitDefinition, and the applier's
            // def==null skip dropped it silently — in-engine that shipped an AI opponent with P2: 0 units.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMapWithEntities(2), Factions("alpha", "beta"));

            // Nothing is lost: both players keep their starting army.
            Assert.Equal(2, built.Units.Length);
            Assert.Equal("worker",      built.Units.Single(u => u.Slot == 0).UnitId); // alpha → identity
            Assert.Equal("beta_worker", built.Units.Single(u => u.Slot == 1).UnitId); // beta  → same role, its own id
        }

        [Fact]
        public void Build_SameFactionLaunch_LeavesPrePlacedUnitIdsUntouched()
        {
            // The identity path: when both slots pick the faction the map was authored against, the remap must be a
            // no-op so a same-faction launch stays byte-identical to the base map.
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "alpha"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMapWithEntities(2), Factions("alpha", "beta"));

            Assert.Equal(2, built.Units.Length);
            Assert.All(built.Units, u => Assert.Equal("worker", u.UnitId));
        }

        [Fact]
        public void MapUnitId_ResolvesByCategoryOrdinal_NotRosterIndex()
        {
            // alpha's SECOND Ranged unit must map to beta's SECOND Ranged unit even though the rosters interleave
            // categories differently — the role key is (Category, ordinal-within-category), not the raw list index.
            FactionEntry a = FactionWith("a", ("worker", "Worker"), ("archer", "Ranged"), ("mage", "Ranged"));
            FactionEntry b = FactionWith("b", ("bolt", "Ranged"), ("cantor", "Ranged"), ("thrall", "Worker"));

            Assert.Equal("bolt",   SkirmishRosterMap.MapUnitId("archer", a, b)); // (Ranged, 0)
            Assert.Equal("cantor", SkirmishRosterMap.MapUnitId("mage",   a, b)); // (Ranged, 1)
            Assert.Equal("thrall", SkirmishRosterMap.MapUnitId("worker", a, b)); // (Worker, 0)
        }

        [Fact]
        public void MapUnitId_ClampsToLastOfCategory_WhenTargetRosterIsShallower()
        {
            // ValidateComplete only guarantees a Worker + one combat unit, so a selectable faction may have fewer units
            // in a category. Field the closest analogue rather than costing that player a starting unit.
            FactionEntry a = FactionWith("a", ("m1", "Melee"), ("m2", "Melee"), ("m3", "Melee"));
            FactionEntry b = FactionWith("b", ("brute", "Melee"));

            Assert.Equal("brute", SkirmishRosterMap.MapUnitId("m3", a, b));
        }

        [Fact]
        public void MapUnitId_ReturnsNull_WhenTargetHasNoUnitInThatCategory()
        {
            // Unmappable → null, so the validator can block Launch instead of the applier dropping it silently.
            FactionEntry a = FactionWith("a", ("griffin", "Air"));
            FactionEntry b = FactionWith("b", ("thrall", "Worker"), ("brute", "Melee"));

            Assert.Null(SkirmishRosterMap.MapUnitId("griffin", a, b));
        }

        [Fact]
        public void Validate_BlocksLaunch_WhenChosenFactionCannotFieldAPrePlacedRole()
        {
            // The honesty backstop: a config whose starting army cannot be fielded must not launch. Map position 1
            // pre-places an Air unit; the chosen faction has no Air unit at all.
            var factions = new List<FactionEntry>
            {
                FactionWith("alpha", ("worker", "Worker"), ("griffin", "Air")),
                FactionWith("beta",  ("forgehand", "Worker")),
            };
            MapEntry map = new()
            {
                Id = "m1", DisplayName = "m1", ResPath = "res://maps/m1.json",
                MapBounds = 120f, SuggestedPlayers = 2, StartPositionCount = 2, Author = "",
                SlotFactionResPaths = new[] { "res://factions/alpha_faction.json", "res://factions/alpha_faction.json" },
                PrePlacedUnits = new[]
                {
                    new MapPrePlacedUnit { Position = 0, UnitId = "worker" },
                    new MapPrePlacedUnit { Position = 1, UnitId = "griffin" },
                },
            };

            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            IReadOnlyList<string> errors = v.Validate(s, map, factions);

            Assert.Contains(errors, e => e.Contains("no Air unit", StringComparison.Ordinal));
            // The mappable Worker role on the same slot must NOT also raise an error.
            Assert.DoesNotContain(errors, e => e.Contains("Worker unit", StringComparison.Ordinal));
        }

        [Fact]
        public void Validate_AllowsLaunch_WhenEveryPrePlacedRoleMaps()
        {
            var factions = new List<FactionEntry>
            {
                FactionWith("alpha", ("worker", "Worker"), ("mage", "Ranged")),
                FactionWith("beta",  ("forgehand", "Worker"), ("cantor", "Ranged")),
            };
            MapEntry map = new()
            {
                Id = "m1", DisplayName = "m1", ResPath = "res://maps/m1.json",
                MapBounds = 120f, SuggestedPlayers = 2, StartPositionCount = 2, Author = "",
                SlotFactionResPaths = new[] { "res://factions/alpha_faction.json", "res://factions/alpha_faction.json" },
                PrePlacedUnits = new[]
                {
                    new MapPrePlacedUnit { Position = 0, UnitId = "mage" },
                    new MapPrePlacedUnit { Position = 1, UnitId = "mage" },
                },
            };

            var v = new SkirmishSetupValidator();
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            Assert.Empty(v.Validate(s, map, factions));
        }

        [Fact]
        public void ScanFactions_PopulatesRoster_AndScanMaps_PopulatesPrePlacedUnits()
        {
            // The remap is only as good as the data the catalog hands it — assert both halves are actually populated.
            using var factionDir = new TempDir();
            WriteFaction(factionDir.Path, "alpha_faction.json", ValidFaction("alpha"));
            IReadOnlyList<FactionEntry> factions =
                SkirmishCatalog.ScanFactions(factionDir.Path, "res://factions");

            FactionEntry alpha = Assert.Single(factions);
            Assert.Equal(new[] { "worker", "melee" }, alpha.Units.Select(u => u.Id).ToArray());
            Assert.Equal(new[] { "Worker", "Melee" }, alpha.Units.Select(u => u.Category).ToArray());

            using var mapDir = new TempDir();
            ScenarioData map = BaseMapWithEntities(2);
            map.Id = "m1";
            WriteMap(mapDir.Path, "m1.json", map);
            MapEntry entry = Assert.Single(SkirmishCatalog.ScanMaps(mapDir.Path, "res://maps"));

            Assert.Equal(new[] { 0, 1 }, entry.PrePlacedUnits.Select(p => p.Position).OrderBy(x => x).ToArray());
            Assert.All(entry.PrePlacedUnits, p => Assert.Equal("worker", p.UnitId));
            Assert.Equal(2, entry.SlotFactionResPaths.Count);
            Assert.All(entry.SlotFactionResPaths, p => Assert.Equal("res://factions/alpha_faction.json", p));
        }

        [Fact]
        public void Build_DoesNotMutate_BaseMapEntities()
        {
            // The pre-placed entity drop/remap must not touch the caller's baseMap (whose arrays ShallowClone shares).
            ScenarioData baseMap = BaseMapWithEntities(4);
            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "beta"));
            SkirmishSetupToScenario.Build(s, baseMap, Factions("alpha", "beta"));

            Assert.Equal(4, baseMap.Buildings.Length);
            Assert.Equal(4, baseMap.Units.Length);
            Assert.Equal(new[] { 0, 1, 2, 3 }, baseMap.Buildings.Select(b => b.Slot).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, baseMap.Units.Select(u => u.Slot).ToArray());
        }

        // ── Catalog: temp-dir scan ────────────────────────────────────────────────────

        [Fact]
        public void ScanMaps_ReadsProperties_OrderedById()
        {
            using var dir = new TempDir();
            WriteMap(dir.Path, "b.json", MapData("bravo", 4, bounds: 200f, author: "me", suggestedPlayers: 4));
            WriteMap(dir.Path, "a.json", MapData("alpha", 2, bounds: 120f, author: "", suggestedPlayers: 2));

            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(dir.Path, "res://maps");

            Assert.Equal(2, maps.Count);
            Assert.Equal("alpha", maps[0].Id);           // ordinal-by-id
            Assert.Equal("bravo", maps[1].Id);
            Assert.Equal(2, maps[0].StartPositionCount);
            Assert.Equal(4, maps[1].StartPositionCount);
            Assert.Equal(2, maps[0].SuggestedPlayers);   // review patch: SuggestedPlayers flows through the scan
            Assert.Equal(4, maps[1].SuggestedPlayers);
            Assert.Equal(200f, maps[1].MapBounds);
            Assert.Equal("me", maps[1].Author);
            Assert.Equal("res://maps/b.json", maps[1].ResPath);
        }

        [Fact]
        public void ScanMaps_EmptyDir_ReturnsEmpty()
        {
            using var dir = new TempDir();
            Assert.Empty(SkirmishCatalog.ScanMaps(dir.Path, "res://maps"));
        }

        [Fact]
        public void ScanMaps_MissingDir_ReturnsEmpty_NoThrow()
        {
            string absent = Path.Combine(Path.GetTempPath(), "chimera_skirmish_absent_" + Guid.NewGuid().ToString("N"));
            Assert.Empty(SkirmishCatalog.ScanMaps(absent, "res://maps"));
        }

        [Fact]
        public void ScanFactions_ValidatesComplete_OrderedById_WithResPath()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "beta_faction.json", ValidFaction("beta"));
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            WriteFaction(dir.Path, "broken_faction.json", IncompleteFaction("broken")); // dropped (no Worker)

            IReadOnlyList<FactionEntry> factions = SkirmishCatalog.ScanFactions(dir.Path, "res://factions");

            Assert.Equal(2, factions.Count);
            Assert.Equal("alpha", factions[0].Id);
            Assert.Equal("beta", factions[1].Id);
            Assert.Equal("res://factions/alpha_faction.json", factions[0].ResPath);
        }

        [Fact]
        public void ScanFactions_EmptyDir_ReturnsEmpty()
        {
            using var dir = new TempDir();
            Assert.Empty(SkirmishCatalog.ScanFactions(dir.Path, "res://factions"));
        }

        [Fact]
        public void ScanMaps_SkipsMalformedJson_NoThrow()
        {
            using var dir = new TempDir();
            WriteMap(dir.Path, "a.json", MapData("alpha", 2));
            File.WriteAllText(Path.Combine(dir.Path, "garbage.json"), "{ this is not valid json ]]]");

            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(dir.Path, "res://maps");

            MapEntry only = Assert.Single(maps);
            Assert.Equal("alpha", only.Id);
        }

        [Fact]
        public void ScanFactions_SkipsMalformedJson_NoThrow()
        {
            // Review patch (verification gap): the lenient `catch { continue; }` on a malformed *_faction.json — the twin
            // of ScanMaps_SkipsMalformedJson — was previously exercised by no test. A corrupt faction file must be skipped,
            // never thrown out of ScanFactions (which would blank the whole setup-screen faction catalog).
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            File.WriteAllText(Path.Combine(dir.Path, "garbage_faction.json"), "{ not valid faction json ]]]");

            IReadOnlyList<FactionEntry> factions = SkirmishCatalog.ScanFactions(dir.Path, "res://factions");

            FactionEntry only = Assert.Single(factions);
            Assert.Equal("alpha", only.Id);
        }

        [Fact]
        public void ScanMaps_SkipsMapWithNoStartPositions()
        {
            // Review patch (finding D): a *.json in the scenarios dir with no start positions is not a launchable map and
            // must not list as a phantom, permanently-unlaunchable entry.
            using var dir = new TempDir();
            WriteMap(dir.Path, "real.json", MapData("real", 2));
            WriteMap(dir.Path, "phantom.json", MapData("phantom", 0)); // zero start positions → skipped

            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(dir.Path, "res://maps");

            MapEntry only = Assert.Single(maps);
            Assert.Equal("real", only.Id);
        }

        [Fact]
        public void ScanMaps_DedupesById_FirstFileWins()
        {
            using var dir = new TempDir();
            // Two files sharing map Id "dup"; ordinal filename order = a.json before b.json → a.json wins.
            WriteMap(dir.Path, "a.json", MapData("dup", 2));
            WriteMap(dir.Path, "b.json", MapData("dup", 4));

            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(dir.Path, "res://maps");

            MapEntry only = Assert.Single(maps);
            Assert.Equal("dup", only.Id);
            Assert.Equal("res://maps/a.json", only.ResPath); // ordinally-first ResPath
            Assert.Equal(2, only.StartPositionCount);        // a.json's payload, not b.json's
        }

        [Fact]
        public void ScanFactions_DedupesById_FirstFileWins()
        {
            using var dir = new TempDir();
            // Two *_faction.json sharing Id "dup"; a_faction.json precedes b_faction.json ordinally → it wins.
            WriteFaction(dir.Path, "a_faction.json", ValidFaction("dup"));
            WriteFaction(dir.Path, "b_faction.json", ValidFaction("dup"));

            IReadOnlyList<FactionEntry> factions = SkirmishCatalog.ScanFactions(dir.Path, "res://factions");

            FactionEntry only = Assert.Single(factions);
            Assert.Equal("dup", only.Id);
            Assert.Equal("res://factions/a_faction.json", only.ResPath); // ordinally-first ResPath
        }

        // ── ScenePhaseRunner progress seam ────────────────────────────────────────────

        [Fact]
        public void ProgressSeam_FiresOncePerPhase_InCanonicalOrder()
        {
            var log = new List<string>();
            var fires = new List<(int index, int total, string name)>();
            new ScenePhaseRunner(CanonicalStubs(log)).Run((i, n, name) => fires.Add((i, n, name)));

            Assert.Equal(ScenePhaseOrder.Canonical.Length, fires.Count);
            for (int i = 0; i < fires.Count; i++)
            {
                Assert.Equal(i + 1, fires[i].index);                              // 1-based
                Assert.Equal(ScenePhaseOrder.Canonical.Length, fires[i].total);   // total == canonical count
                Assert.Equal(ScenePhaseOrder.Canonical[i], fires[i].name);        // canonical order, before each run
            }
            Assert.Equal(ScenePhaseOrder.Canonical, log.ToArray());               // every phase still ran, in order
        }

        [Fact]
        public void ProgressSeam_Null_StillRunsEveryPhase()
        {
            var log = new List<string>();
            new ScenePhaseRunner(CanonicalStubs(log)).Run(null);
            Assert.Equal(ScenePhaseOrder.Canonical, log.ToArray());
        }

        // ── DW-121: discovered faction routed as a FactionJson PATH (not an in-memory def) ─

        [Fact]
        public void DiscoveredFaction_CommittedAsPath_ResolvesAtLoad()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));

            IReadOnlyList<FactionEntry> factions = SkirmishCatalog.ScanFactions(dir.Path, "res://factions");
            Assert.Single(factions);

            SkirmishSetup s = Setup("m1", Slot(0, SlotKind.Human, "alpha"), Slot(1, SlotKind.Ai, "alpha"));
            ScenarioData built = SkirmishSetupToScenario.Build(s, BaseMap(2), factions);

            // DW-121 closure: the slot carries the faction's res:// PATH (a string), never an in-memory FactionDefinition
            // — so the existing ResolveSlotFactionDefs (LoadFromFile → ResolveAbilities → ValidateAndDropUnits) runs.
            Assert.All(built.PlayerSlots, ps => Assert.Equal("res://factions/alpha_faction.json", ps.FactionJson));

            // Prove the committed path resolves to a real, resolution-ready faction (the exact resolution DW-121 warned
            // would be skipped if a raw def were assigned): load it from the file the catalog scanned and run the tag
            // pre-pass ResolveSlotFactionDefs performs — no units dropped, roster complete.
            FactionDefinition? def = FactionDefinition.LoadFromFile(Path.Combine(dir.Path, "alpha_faction.json"));
            Assert.NotNull(def);
            Assert.Empty(UnitTagValidator.ValidateAndDropUnits(def!));
            Assert.True(FactionValidator.ValidateComplete(def!).Ok);
        }

        // ── Helpers (mirror FactionDiscoveryTests) ────────────────────────────────────

        private static ScenarioData MapData(string id, int slots, float bounds = 120f, string author = "", int suggestedPlayers = 0)
        {
            var m = new ScenarioData { Id = id, DisplayName = id, MapBounds = bounds, SuggestedPlayers = suggestedPlayers };
            if (!string.IsNullOrEmpty(author)) m.Author = author;
            var ps = new ScenarioPlayerSlot[slots];
            for (int i = 0; i < slots; i++) ps[i] = new ScenarioPlayerSlot { Slot = i, FactionJson = "res://x.json" };
            m.PlayerSlots = ps;
            return m;
        }

        private static void WriteMap(string dir, string fileName, ScenarioData map)
            => File.WriteAllText(Path.Combine(dir, fileName), ScenarioSerializer.Serialize(map));

        private static UnitDefinition Worker(string id = "worker") => new()
        { Id = id, DisplayName = id, Category = "Worker", MeshPath = "res://assets/worker.glb", Hp = 50f };

        private static UnitDefinition Melee(string id = "melee") => new()
        { Id = id, DisplayName = id, Category = "Melee", MeshPath = "res://assets/melee.glb", Hp = 50f };

        private static BuildingDefinition ValidBuilding() => new()
        {
            Id = "command_center", DisplayName = "command_center", Category = "Structure",
            MeshPath = "res://assets/command_center.glb", Hp = 100f, ConstructionTime = 10f,
            SupplyBonus = 0, ProducesCategory = "Worker",
        };

        private static FactionDefinition ValidFaction(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Worker());
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        private static FactionDefinition IncompleteFaction(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Melee()); // no Worker → fails ValidateComplete
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        private static void WriteFaction(string dir, string fileName, FactionDefinition def)
            => File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(def, FactionDefinition.JsonOptions));

        private sealed class StubPhase : ISetupPhase
        {
            private readonly List<string> _log;
            public StubPhase(string name, List<string> log) { Name = name; _log = log; }
            public string Name { get; }
            public void Run() => _log.Add(Name);
        }

        private static List<ISetupPhase> CanonicalStubs(List<string> log)
        {
            var phases = new List<ISetupPhase>(ScenePhaseOrder.Canonical.Length);
            foreach (string name in ScenePhaseOrder.Canonical) phases.Add(new StubPhase(name, log));
            return phases;
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_skirmish_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
            }
        }
    }
}
