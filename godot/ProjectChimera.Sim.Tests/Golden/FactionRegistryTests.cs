#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;
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

        // ── AC1: the single (Faction)(slot+1) cast site ───────────────────────────

        [Fact]
        public void ToFaction_IsTheOnePlaceTheSlotPlusOneOffsetLives()
        {
            Assert.Equal(Faction.Player1, FactionRegistry.ToFaction(0));
            Assert.Equal(Faction.Player2, FactionRegistry.ToFaction(1));
            Assert.Equal(Faction.Player3, FactionRegistry.ToFaction(2));
            Assert.Equal(Faction.Player4, FactionRegistry.ToFaction(3));
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
    }
}
