#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-814 — a NULL element inside <c>player_slots</c>.
    ///
    /// <para><b>The defect.</b> <see cref="ScenarioValidator"/>'s class doc states "It is pure: it NEVER throws and
    /// NEVER logs", and every sibling collection loop (props / cameras / water / variables / timers / triggers) opens
    /// with an explicit located <c>if (x is null) return Fail(...)</c>. The player-slots loop was the ONLY one
    /// missing it, so JSON <c>"player_slots": [null]</c> — which deserializes to a genuine null element — made
    /// <c>Validate</c> throw a <see cref="NullReferenceException"/> out of the fail-closed load gate every call site
    /// depends on. <see cref="AllianceSeeder"/>'s mapping shared the fault and is reachable UN-validated (the lobby's
    /// slot-grid rebuild, the editor's advisory channel), which is why it needs its own skip rather than relying on
    /// the validator having run first.</para>
    ///
    /// <para>The gap was already BELIEVED closed inside the file: <c>CheckSpawnsNotBlocked</c> carries the comment
    /// "Validate() already located a null element; never NRE here", which was false until now. These tests make it
    /// true. Godot-free / Tier-1: pure model validation, no I/O, no sim.</para>
    /// </summary>
    public class ScenarioNullPlayerSlotTests
    {
        /// <summary>A loadable map whose slot array is supplied verbatim (so a null element can be planted).</summary>
        private static ScenarioData MapWithSlots(params ScenarioPlayerSlot?[] slots) => new ScenarioData
        {
            Id = "m", DisplayName = "Map", MapBounds = 120f,
            WinCondition  = WinCondition.DestroyAllBuildings,
            PlayerSlots   = slots!,
            ResourceNodes = Array.Empty<ScenarioResourceNode>(),
            Buildings     = Array.Empty<ScenarioBuilding>(),
            Units         = Array.Empty<ScenarioUnit>(),
            Triggers      = Array.Empty<TriggerDefinition>(),
        };

        private static ScenarioPlayerSlot Slot(int slot, int team = 0) => new ScenarioPlayerSlot
        {
            Slot = slot, FactionJson = "res://a.json", StartOre = 200f,
            BaseX = -50f + slot * 20f, BaseZ = 0f, Team = team,
        };

        // ── The validator: located failure, never a throw ───────────────────────────────────────────────

        [Fact]
        public void Validate_NullPlayerSlotElement_FailsLocated_NeverThrows()
        {
            ValidationResult r = new ScenarioValidator().Validate(MapWithSlots(Slot(0), null, Slot(1)));

            Assert.False(r.Ok);
            Assert.Equal("scenario.player_slots[1] is null.", r.Error);
        }

        [Fact]
        public void Validate_NullPlayerSlotElement_ArrivingFromRealJson_FailsLocated()
        {
            // The production shape: nothing hand-builds a null element — a malformed authored/downloaded file does.
            const string json = """
            {
              "id": "m", "display_name": "Map", "map_bounds": 120, "win_condition": "DestroyAllBuildings",
              "player_slots": [ null ],
              "resource_nodes": [], "buildings": [], "units": []
            }
            """;
            ScenarioData? m = System.Text.Json.JsonSerializer.Deserialize<ScenarioData>(
                json, ContentJson.ScenarioOptions);
            Assert.NotNull(m);
            Assert.Single(m!.PlayerSlots);
            Assert.Null(m.PlayerSlots[0]);   // the premise: the JSON null really is a null ELEMENT

            ValidationResult r = new ScenarioValidator().Validate(m);

            Assert.False(r.Ok);
            Assert.Equal("scenario.player_slots[0] is null.", r.Error);
        }

        [Fact]
        public void Validate_WellFormedSlots_StillPass()
        {
            // Non-regression: the new guard must reject ONLY a null element.
            Assert.True(new ScenarioValidator().Validate(MapWithSlots(Slot(0), Slot(1))).Ok);
        }

        // ── The advisory channel (runs on UN-validated mid-edit models) ─────────────────────────────────

        [Fact]
        public void CollectAdvisories_WithANullSlot_DoesNotThrow()
        {
            ScenarioData m = MapWithSlots(Slot(0), null, Slot(1));
            m.SuggestedPlayers = 2;

            IReadOnlyList<string> advisories = new ScenarioValidator().CollectAdvisories(m);

            // The surviving slots are in bounds, so the only thing under assertion is that the walk completed.
            Assert.DoesNotContain(advisories, a => a.Contains("outside the current map bounds"));
        }

        // ── The seeder: a null element is skipped exactly like an out-of-range one ──────────────────────

        [Fact]
        public void ComputeTeamIds_SkipsANullSlot_AndStillTeamsTheRest()
        {
            var model = new ScenarioData
            {
                PlayerSlots = new[] { Slot(0, team: 1), null!, Slot(1, team: 1), Slot(2, team: 2) },
            };

            int[] ids = AllianceSeeder.ComputeTeamIds(model);   // pre-fix: NullReferenceException

            // Canonical id = the lowest member faction index; P1 and P2 share it, P3 keeps its own.
            Assert.Equal((int)Faction.Player1, ids[(int)Faction.Player1]);
            Assert.Equal((int)Faction.Player1, ids[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player3, ids[(int)Faction.Player3]);
        }

        [Fact]
        public void ComputeTeamIds_NullSlotInTheMiddleOfATeam_DoesNotLowerTheCanonicalId()
        {
            // The inner canonical scan walks EVERY slot, so it needs the same skip as the outer loop — and a skipped
            // element must not participate in the min, mirroring DW-442's out-of-range-member treatment.
            var model = new ScenarioData
            {
                PlayerSlots = new[] { null!, Slot(1, team: 3), Slot(2, team: 3) },
            };

            int[] ids = AllianceSeeder.ComputeTeamIds(model);

            Assert.Equal((int)Faction.Player2, ids[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player2, ids[(int)Faction.Player3]);
        }

        [Fact]
        public void Seed_WithANullSlot_DoesNotThrow_AndMatchesTheNullFreeModel()
        {
            // Seed writes the SIM's folded alliance mask, so "skipped" must mean byte-identical to the same model
            // without the null element — not merely "did not crash".
            var withNull = new ScenarioData { PlayerSlots = new[] { Slot(0, team: 1), null!, Slot(1, team: 1) } };
            var without  = new ScenarioData { PlayerSlots = new[] { Slot(0, team: 1), Slot(1, team: 1) } };

            var a = new AllianceStore();
            var b = new AllianceStore();
            AllianceSeeder.Seed(a, withNull);   // pre-fix: NullReferenceException
            AllianceSeeder.Seed(b, without);

            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++)
                Assert.Equal(b.TeamId[f], a.TeamId[f]);
        }

        [Fact]
        public void CheckSpawnsNotBlocked_CommentIsNowTrue_NullElementIsSkipped()
        {
            // The comment at that site claims Validate() already located a null element. It now genuinely does (test
            // above), and the site itself must still be independently null-safe.
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(MapWithSlots(Slot(0), null), resolved: null));
        }
    }
}
