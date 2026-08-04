#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;                 // Faction, FactionRegistry
using ProjectChimera.Multiplayer;          // PlayerCountPolicy (ServerTransport.MAX_PLAYERS pins to it)
using ProjectChimera.Multiplayer.Server;   // SlotFactionTable, DropCoordinator
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-412 — the SLOT_FACTION injectivity pin. <see cref="DropCoordinator.FactionToSlot"/> inverts the
    /// slot→faction table by scanning it and returning the FIRST slot that matches, so the table must be a genuine
    /// bijection over the player prefix. It is today (<c>ToFaction(i) == (Faction)(i+1)</c>) but NOTHING asserted it:
    /// a duplicate faction would resolve a survivor's DropAck to the wrong slot, <c>DropController.RecordAck</c>
    /// would discard it as a pending-mismatch with no log, and the freeze would never commit — the merged fan-in
    /// stalling on the departed peer forever. These tests pin both halves of well-formedness (injective, and
    /// player-faction-only so an unknown byte still resolves to −1) at BOTH gates: the table's construction site and
    /// the moment <see cref="DropCoordinator"/> takes ownership of one.
    /// </summary>
    public class SlotFactionInjectivityTests
    {
        // ── The construction site (DedicatedServer.BuildSlotFactions delegates here) ───────────────────

        [Fact]
        public void Build_ProducesAnInjectivePlayerOnlyTable_AtEveryLegalSize()
        {
            for (int n = 1; n <= FactionRegistry.PLAYER_COUNT; n++)
            {
                Faction[] table = SlotFactionTable.Build(n);
                Assert.Equal(n, table.Length);

                var seen = new HashSet<Faction>();
                for (int s = 0; s < n; s++)
                {
                    Assert.Equal(FactionRegistry.ToFaction(s), table[s]); // the authoritative mapping, unchanged
                    Assert.NotEqual(Faction.Neutral, table[s]);
                    Assert.True(seen.Add(table[s]), $"faction {table[s]} appeared twice at size {n}");
                }
            }
        }

        [Fact]
        public void Build_TheLiveServerTable_IsWellFormed()
        {
            // The exact table DedicatedServer.SLOT_FACTION is built from (ServerTransport.MAX_PLAYERS pins to
            // PlayerCountPolicy.MpSeatCeiling). Asserted here because the node itself is Godot-coupled.
            Faction[] live = SlotFactionTable.Build(PlayerCountPolicy.MpSeatCeiling);
            Assert.True(SlotFactionTable.TryValidate(live, PlayerCountPolicy.MpSeatCeiling, out string? err), err);
            Assert.Null(err);
        }

        [Fact]
        public void Build_RejectsASlotCountThatWouldLeaveTheFactionEnum()
        {
            // ToFaction(PLAYER_COUNT) == (Faction)(PLAYER_COUNT+1), which is not a declared player faction — a table
            // containing it is numerically "distinct" but still un-invertible. Fail closed rather than build it.
            Assert.Throws<ArgumentOutOfRangeException>(() => SlotFactionTable.Build(FactionRegistry.PLAYER_COUNT + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SlotFactionTable.Build(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SlotFactionTable.Build(-1));
        }

        // ── The validator itself ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void TryValidate_RejectsADuplicateFaction_AndNamesBothSlots()
        {
            // THE defect shape: slot 1 was mis-mapped to Player1. FactionToSlot(Player1) would return 0 — so a
            // DropAck naming the dropped slot 1 resolves to slot 0 and is silently thrown away.
            var duplicated = new[] { Faction.Player1, Faction.Player1, Faction.Player3 };

            Assert.False(SlotFactionTable.TryValidate(duplicated, 3, out string? error));
            Assert.NotNull(error);
            Assert.Contains("slot 0", error!);
            Assert.Contains("slot 1", error!);
            Assert.Contains("injective", error!);
        }

        [Fact]
        public void TryValidate_RejectsNeutralOrOutOfEnum_SoAnUnknownByteStillResolvesToMinusOne()
        {
            Assert.False(SlotFactionTable.TryValidate(new[] { Faction.Player1, Faction.Neutral }, 2, out string? neutralErr));
            Assert.NotNull(neutralErr);
            Assert.Contains("slot 1", neutralErr!);

            // A byte past Player8 — reachable only from a hand-built/derived table, never from ToFaction.
            var past = new[] { Faction.Player1, (Faction)(FactionRegistry.PLAYER_COUNT + 1) };
            Assert.False(SlotFactionTable.TryValidate(past, 2, out string? pastErr));
            Assert.NotNull(pastErr);
            Assert.Contains("slot 1", pastErr!);
        }

        [Fact]
        public void TryValidate_RejectsAShortOrNullTable()
        {
            Assert.False(SlotFactionTable.TryValidate(new[] { Faction.Player1 }, 2, out _));
            Assert.False(SlotFactionTable.TryValidate(null, 2, out _));
            Assert.False(SlotFactionTable.TryValidate(new[] { Faction.Player1 }, 0, out _));
        }

        [Fact]
        public void TryValidate_OnlyInspectsThePlayerPrefix()
        {
            // The server's table is sized to the transport seat ceiling while a match quorums over its own connected
            // prefix, so a duplicate BEYOND `count` is out of this match's inverse and must not fail the guard.
            var table = new[] { Faction.Player1, Faction.Player2, Faction.Player2 };
            Assert.True(SlotFactionTable.TryValidate(table, 2, out _));
            Assert.False(SlotFactionTable.TryValidate(table, 3, out _));
        }

        // ── The consumer gate (DropCoordinator owns FactionToSlot) ────────────────────────────────────

        [Fact]
        public void DropCoordinator_RefusesANonInjectiveTable_SoTheAckMisResolutionIsUnreachable()
        {
            // Pre-fix this constructed happily; the stall only showed up as a match that never resumed.
            var ex = Assert.Throws<ArgumentException>(() => NewCoordinator(3,
                new[] { Faction.Player1, Faction.Player1, Faction.Player3 }));
            Assert.Equal("slotFaction", ex.ParamName);
            Assert.Contains("injective", ex.Message);
        }

        [Fact]
        public void DropCoordinator_RefusesANeutralPlayerSlot()
        {
            var ex = Assert.Throws<ArgumentException>(() => NewCoordinator(2,
                new[] { Faction.Player1, Faction.Neutral }));
            Assert.Equal("slotFaction", ex.ParamName);
        }

        [Fact]
        public void DropCoordinator_StillAcceptsTheAuthoritativeTable_AndInvertsIt()
        {
            // The guard must not have narrowed the legitimate path: the real table constructs and round-trips.
            Faction[] table = SlotFactionTable.Build(4);
            DropCoordinator co = NewCoordinator(4, table);
            for (int s = 0; s < 4; s++) Assert.Equal(s, co.FactionToSlot(table[s]));
            Assert.Equal(-1, co.FactionToSlot(Faction.Neutral));
        }

        /// <summary>A coordinator over inert seams — only the constructor's table validation is under test.</summary>
        private static DropCoordinator NewCoordinator(int players, Faction[] slotFaction)
            => new DropCoordinator(players, slotFaction,
                   () => 0L,
                   _ => true,
                   (_, _) => { },
                   _ => { });
    }
}
