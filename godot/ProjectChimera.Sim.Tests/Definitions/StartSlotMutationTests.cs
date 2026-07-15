#nullable enable
using System.Linq;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.7 (patches 1 &amp; 3) — the Godot-free start-slot mutation helpers on <see cref="ScenarioData"/> that back
    /// the editor's MoveStartPosition / remove-slot / spinner-economy bridges: <see cref="ScenarioData.UpsertStartSlot"/>
    /// (update-in-place vs. append-and-report-created), <see cref="ScenarioData.RemoveStartSlot"/> (remove by exact Slot
    /// VALUE, non-contiguous-safe), and <see cref="ScenarioData.UpdateStartSlotEconomy"/> (economy-only, never appends).
    /// Mirrors the construction style of <see cref="StartPositionSlotTests"/>.
    /// </summary>
    public class StartSlotMutationTests
    {
        private static ScenarioPlayerSlot Slot(int slot, string faction = "res://a.json", float baseX = 0f) =>
            new ScenarioPlayerSlot { Slot = slot, FactionJson = faction, StartOre = 200f, StartCrystal = 0f, BaseX = baseX, BaseZ = 0f };

        private static ScenarioData MapWith(params ScenarioPlayerSlot[] slots) => new ScenarioData
        {
            Id = "m", DisplayName = "Map", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots   = slots,
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings     = System.Array.Empty<ScenarioBuilding>(),
            Units         = System.Array.Empty<ScenarioUnit>(),
            Triggers      = System.Array.Empty<TriggerDefinition>(),
        };

        // ── UpsertStartSlot ─────────────────────────────────────────────────────

        [Fact]
        public void Upsert_ExistingSlot_UpdatesInPlace_ReturnsFalse()
        {
            var m = MapWith(Slot(0), Slot(1));
            bool created = m.UpsertStartSlot(1, baseX: 42f, baseZ: -7f, startOre: 350f, startCrystal: 5f);

            Assert.False(created);                    // updated, not created
            Assert.Equal(2, m.PlayerSlots.Length);    // no append
            var s = m.PlayerSlots.Single(p => p.Slot == 1);
            Assert.Equal(42f, s.BaseX);
            Assert.Equal(-7f, s.BaseZ);
            Assert.Equal(350f, s.StartOre);
            Assert.Equal(5f, s.StartCrystal);
        }

        [Fact]
        public void Upsert_NewInRangeSlot_Appends_InheritsFactionFromSlot0_ReturnsTrue()
        {
            var m = MapWith(Slot(0, faction: "res://alpha.json"), Slot(1, faction: "res://beta.json"));
            bool created = m.UpsertStartSlot(2, baseX: 10f, baseZ: 20f, startOre: 100f, startCrystal: 3f);

            Assert.True(created);
            Assert.Equal(3, m.PlayerSlots.Length);
            var s = m.PlayerSlots.Single(p => p.Slot == 2);
            Assert.Equal("res://alpha.json", s.FactionJson); // inherited from slot 0
            Assert.Equal(10f, s.BaseX);
            Assert.Equal(20f, s.BaseZ);
            Assert.Equal(100f, s.StartOre);
            Assert.Equal(3f, s.StartCrystal);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(8)]
        [InlineData(100)]
        public void Upsert_OutOfRange_IsNoOp_ReturnsFalse(int slot)
        {
            var m = MapWith(Slot(0), Slot(1));
            bool created = m.UpsertStartSlot(slot, 1f, 2f, 3f, 4f);

            Assert.False(created);
            Assert.Equal(2, m.PlayerSlots.Length); // nothing appended
        }

        // ── RemoveStartSlot (by exact Slot VALUE, non-contiguous) ───────────────

        [Fact]
        public void Remove_ExactValue_NonContiguous_Removing3_Leaves0And1()
        {
            var m = MapWith(Slot(0), Slot(1), Slot(3));
            bool removed = m.RemoveStartSlot(3);

            Assert.True(removed);
            Assert.Equal(new[] { 0, 1 }, m.PlayerSlots.Select(s => s.Slot).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Remove_ExactValue_NonContiguous_Removing1_Leaves0And3()
        {
            var m = MapWith(Slot(0), Slot(1), Slot(3));
            bool removed = m.RemoveStartSlot(1);

            Assert.True(removed);
            Assert.Equal(new[] { 0, 3 }, m.PlayerSlots.Select(s => s.Slot).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Remove_MissingValue_ReturnsFalse_NoChange()
        {
            var m = MapWith(Slot(0), Slot(1));
            bool removed = m.RemoveStartSlot(2);

            Assert.False(removed);
            Assert.Equal(2, m.PlayerSlots.Length);
        }

        // ── UpdateStartSlotEconomy (patch 3) ────────────────────────────────────

        [Fact]
        public void UpdateEconomy_ExistingSlot_UpdatesOreCrystal_ReturnsTrue()
        {
            var m = MapWith(Slot(0), Slot(1));
            var before = m.PlayerSlots.Single(p => p.Slot == 1);
            float baseX = before.BaseX;

            bool updated = m.UpdateStartSlotEconomy(1, startOre: 777f, startCrystal: 9f);

            Assert.True(updated);
            var s = m.PlayerSlots.Single(p => p.Slot == 1);
            Assert.Equal(777f, s.StartOre);
            Assert.Equal(9f, s.StartCrystal);
            Assert.Equal(baseX, s.BaseX); // position untouched
        }

        [Fact]
        public void UpdateEconomy_MissingSlot_ReturnsFalse_AppendsNothing()
        {
            var m = MapWith(Slot(0), Slot(1));
            bool updated = m.UpdateStartSlotEconomy(2, 500f, 5f);

            Assert.False(updated);
            Assert.Equal(2, m.PlayerSlots.Length);
        }

        // ── Still valid after a representative upsert/remove sequence ───────────

        [Fact]
        public void AfterUpsertRemoveSequence_ScenarioStillValidates()
        {
            var m = MapWith(Slot(0, baseX: -50f), Slot(1, baseX: 50f));

            Assert.False(m.UpsertStartSlot(1, baseX: 40f, baseZ: 0f, startOre: 200f, startCrystal: 0f)); // update existing
            Assert.True(m.UpsertStartSlot(2, baseX: 10f, baseZ: 10f, startOre: 200f, startCrystal: 0f));  // append new
            Assert.True(m.RemoveStartSlot(2));                                                            // remove it again

            Assert.True(new ScenarioValidator().Validate(m).Ok);
        }
    }
}
