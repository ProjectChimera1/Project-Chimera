#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.3a — unit tests for <see cref="FactionRegistry"/> (the source of truth for faction-count /
    /// slot knowledge) and a non-tautological proof that <see cref="SimChecksum"/>'s faction-resource loop
    /// reads EXACTLY the registry's active factions.
    ///
    /// (The Godot-free boundary — no <c>using Godot</c> anywhere in the sim source — is already proven
    /// structurally by Story 1.1's GodotFreeBoundaryTest over the whole shared source set; FactionRegistry is
    /// compiled into that set, so this file adds no separate boundary assertion.)
    /// </summary>
    public class FactionRegistryTests
    {
        // ── AC1: the canonical constants ──────────────────────────────────────────

        [Fact]
        public void Constants_AreTheForwardNPlayerValues()
        {
            Assert.Equal(8, FactionRegistry.PLAYER_COUNT);       // playable, excl. Neutral
            Assert.Equal(9, FactionRegistry.FACTION_ARRAY_SIZE); // incl. Neutral slot 0
        }

        /// <summary>
        /// Story 9.2 (follow-up hardening) — the load-bearing invariant every per-faction store and the
        /// ScenarioDirector threshold poll silently rely on:
        /// FACTION_ARRAY_SIZE == PLAYER_COUNT + 1 == (int)Faction.Player8 + 1 == SLOT_DEFINITIONS_SIZE.
        /// A future story that bumps PLAYER_COUNT without extending the enum (or adds an enum member without
        /// resizing the arrays) makes ToFaction/(int)Faction emit a slot the length-9 arrays cannot hold — a
        /// runtime IndexOutOfRange in the poll/stores that no other unit test catches until an N&gt;8 match runs.
        /// Tying the four numbers together here fails that divergence at build/unit time instead.
        /// </summary>
        [Fact]
        public void Constants_EnumAndArraySizesAgree_TheLoadBearingInvariant()
        {
            Assert.Equal(FactionRegistry.PLAYER_COUNT + 1, FactionRegistry.FACTION_ARRAY_SIZE);
            Assert.Equal((int)Faction.Player8 + 1, FactionRegistry.FACTION_ARRAY_SIZE);
            Assert.Equal(FactionRegistry.FACTION_ARRAY_SIZE, FactionRegistry.SLOT_DEFINITIONS_SIZE);
        }

        // ── AC1: the single (Faction)(slot+1) cast site ───────────────────────────

        [Fact]
        public void ToFaction_IsTheOnePlaceTheSlotPlusOneOffsetLives()
        {
            Assert.Equal(Faction.Player1, FactionRegistry.ToFaction(0));
            Assert.Equal(Faction.Player2, FactionRegistry.ToFaction(1));
            Assert.Equal(Faction.Player3, FactionRegistry.ToFaction(2));
            Assert.Equal(Faction.Player4, FactionRegistry.ToFaction(3));
            // Story 9.2 — the four new slots map to the newly-added enum members.
            Assert.Equal(Faction.Player5, FactionRegistry.ToFaction(4));
            Assert.Equal(Faction.Player6, FactionRegistry.ToFaction(5));
            Assert.Equal(Faction.Player7, FactionRegistry.ToFaction(6));
            Assert.Equal(Faction.Player8, FactionRegistry.ToFaction(7));
        }

        // ── AC1: the ascending active-faction list ────────────────────────────────

        [Fact]
        public void ActiveFactions_AreAscendingPlayer1ThroughN()
        {
            var reg = new FactionRegistry(4);

            Assert.Equal(4, reg.ActiveCount);
            Assert.Equal(
                new[] { Faction.Player1, Faction.Player2, Faction.Player3, Faction.Player4 },
                reg.ActiveFactions.ToArray());
        }

        [Fact]
        public void ActiveFactions_TwoActive_IsExactlyPlayer1AndPlayer2()
        {
            var reg = new FactionRegistry(2);

            Assert.Equal(2, reg.ActiveCount);
            Assert.Equal(new[] { Faction.Player1, Faction.Player2 }, reg.ActiveFactions.ToArray());
        }

        [Theory]
        [InlineData(0)]                              // below the floor
        [InlineData(9)]                              // above PLAYER_COUNT (=8)
        [InlineData(-1)]
        [InlineData(100)]
        public void Ctor_RejectsOutOfRangeActiveCount(int activePlayerCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FactionRegistry(activePlayerCount));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]                              // the ceiling is valid
        public void Ctor_AcceptsTheInclusiveBounds(int activePlayerCount)
        {
            var reg = new FactionRegistry(activePlayerCount);
            Assert.Equal(activePlayerCount, reg.ActiveCount);
        }

        // ── AC3 core: the span proof — the ore loop reads EXACTLY the active factions ──

        /// <summary>
        /// Proves, without tautology, that the registry's active span controls which factions' ore enters the
        /// hash: changing Player3's ore VALUE moves the checksum under FactionRegistry(3) (Player3 is read) but
        /// CANNOT move it under FactionRegistry(2) (the 2-active loop never reaches Player3).
        ///
        /// Deliberately NOT `Compute(…,(2)) != Compute(…,(3))` — that differs merely from one extra Mix call
        /// even when Ore[P3]==0, which would NOT prove Player3's value is read (the 1.2 "no tautological
        /// assert" lesson).
        /// </summary>
        [Fact]
        public void OreLoop_SpansExactlyTheActiveFactions_NotATautology()
        {
            var world     = new EntityWorld();          // empty — isolates the faction-resource section
            var buildings = new BuildingStore();        // empty
            var resources = new ResourceStore(Fixed.Zero);
            var reg2      = new FactionRegistry(2);
            var reg3      = new FactionRegistry(3);

            // Player3 ore = value A
            resources.Ore[(int)Faction.Player3] = Fixed.FromInt(100);
            uint h3a = SimChecksum.Compute(world, buildings, resources, reg3);
            uint h2a = SimChecksum.Compute(world, buildings, resources, reg2);

            // Player3 ore = value B
            resources.Ore[(int)Faction.Player3] = Fixed.FromInt(250);
            uint h3b = SimChecksum.Compute(world, buildings, resources, reg3);
            uint h2b = SimChecksum.Compute(world, buildings, resources, reg2);

            // Player3's ore value IS hashed when 3 factions are active…
            Assert.NotEqual(h3a, h3b);
            // …and is NEVER read when only 2 are active (its change cannot move the hash).
            Assert.Equal(h2a, h2b);
        }

        /// <summary>
        /// Story 9.2 — the SAME non-tautological span proof for the TOP new slot (Player8, the highest of the
        /// enum members and array indices added by 9.2). Changing Ore[Player8] moves the checksum under
        /// FactionRegistry(8) (Player8 is the 8th active faction, read) but CANNOT move it under FactionRegistry(7)
        /// (the 7-active loop stops at Player7). A regression capping the fold span at 4 — which the N=8 two-run
        /// determinism test would NOT catch (it passes on P1 evolution + self-equality alone) — fails here.
        /// </summary>
        [Fact]
        public void OreLoop_SpansExactlyTheActiveFactions_TopSlotPlayer8_NotATautology()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var reg8      = new FactionRegistry(8);
            var reg7      = new FactionRegistry(7);

            resources.Ore[(int)Faction.Player8] = Fixed.FromInt(100);
            uint h8a = SimChecksum.Compute(world, buildings, resources, reg8);
            uint h7a = SimChecksum.Compute(world, buildings, resources, reg7);

            resources.Ore[(int)Faction.Player8] = Fixed.FromInt(250);
            uint h8b = SimChecksum.Compute(world, buildings, resources, reg8);
            uint h7b = SimChecksum.Compute(world, buildings, resources, reg7);

            // Player8's ore value IS folded when 8 factions are active…
            Assert.NotEqual(h8a, h8b);
            // …and is NEVER read when only 7 are active (its change cannot move the hash).
            Assert.Equal(h7a, h7b);
        }

        // ── AR-3 / Story 5.1: SlotDefinitions storage ─────────────────────────────

        [Fact]
        public void SlotDefinitions_FreshRegistry_IsSizeNineAllNull()
        {
            var reg = new FactionRegistry(2);

            // Story 9.2: SLOT_DEFINITIONS_SIZE was unified with FACTION_ARRAY_SIZE (9) so the slot array agrees
            // with the widened per-faction stores (Neutral + Player1..Player8).
            Assert.Equal(FactionRegistry.SLOT_DEFINITIONS_SIZE, reg.SlotDefinitions.Length);
            Assert.Equal(9, reg.SlotDefinitions.Length);
            Assert.All(reg.SlotDefinitions, def => Assert.Null(def));
        }

        [Fact]
        public void SlotDefinitions_SetThenRead_RoundTripsSameReference()
        {
            var reg = new FactionRegistry(2);
            var def = new FactionDefinition();

            reg.SlotDefinitions[(int)Faction.Player1] = def;

            Assert.Same(def, reg.SlotDefinitions[(int)Faction.Player1]);
        }

        // ── AR-3 / Story 5.1 (AC3): GetSlotDefinition bounds-checked accessor ─────

        [Fact]
        public void GetSlotDefinition_AssignedInRangeSlot_ReturnsSameReference()
        {
            var reg = new FactionRegistry(2);
            var def = new FactionDefinition();
            reg.SlotDefinitions[(int)Faction.Player1] = def;

            Assert.Same(def, reg.GetSlotDefinition(Faction.Player1));
        }

        [Fact]
        public void GetSlotDefinition_UnassignedInRangeSlot_ReturnsNull()
        {
            var reg = new FactionRegistry(2);

            Assert.Null(reg.GetSlotDefinition(Faction.Player2));
        }

        [Fact]
        public void GetSlotDefinition_OutOfRangeFaction_ReturnsNullNeverThrows()
        {
            var reg = new FactionRegistry(2);

            var ex = Record.Exception(() => reg.GetSlotDefinition((Faction)99));

            Assert.Null(ex);
            Assert.Null(reg.GetSlotDefinition((Faction)99));
        }

        [Fact]
        public void GetSlotDefinition_ExactlyAtBoundary_ReturnsNullNeverThrows()
        {
            // (Faction)SLOT_DEFINITIONS_SIZE is the first genuinely invalid index — pins the off-by-one
            // boundary (< vs <=) that GetSlotDefinition_OutOfRangeFaction_ReturnsNullNeverThrows's far-out-of-range
            // (Faction)99 case cannot catch.
            var reg = new FactionRegistry(2);
            var boundary = (Faction)FactionRegistry.SLOT_DEFINITIONS_SIZE;

            var ex = Record.Exception(() => reg.GetSlotDefinition(boundary));

            Assert.Null(ex);
            Assert.Null(reg.GetSlotDefinition(boundary));
        }
    }
}
