#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 7.8 — the presentation read rail <see cref="DslVarReadback"/>: a version-stamped, double-buffered COPY
    /// of already-checksummed <see cref="DslVarTable"/> state. A changed raw bumps that variable's monotonic
    /// version; an unchanged tick leaves it; a captured snapshot stays tear-free after a later publish. The readback
    /// is DERIVED from — and never folded into — <c>SimChecksum</c> (Compute has no readback parameter, so it is
    /// excluded by construction).
    /// </summary>
    public class DslVarReadbackTests
    {
        private static (DslVarTable table, DslVarReadback rb) Build(params DslVarDecl[] decls)
        {
            var table = new DslVarTable();
            table.InitFromDeclarations(decls, Array.Empty<DslTimerDecl>());
            var rb = new DslVarReadback();
            rb.InitFromDeclarations(decls);
            return (table, rb);
        }

        [Fact]
        public void Change_BumpsVersion_UnchangedDoesNot()
        {
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0));

            rb.Publish(table, 1);
            Assert.True(rb.TryGetScalar("score", 0, out _, out int v0, out _, out uint ver0));
            Assert.Equal(0, v0);
            Assert.Equal(1u, ver0); // initial published version

            // Unchanged tick — version must NOT move.
            rb.Publish(table, 2);
            rb.TryGetScalar("score", 0, out _, out _, out _, out uint verUnchanged);
            Assert.Equal(1u, verUnchanged);

            // A real change bumps the version and updates the value.
            table.SetInt("score", 0, 42);
            rb.Publish(table, 3);
            Assert.True(rb.TryGetScalar("score", 0, out _, out int v1, out _, out uint ver1));
            Assert.Equal(42, v1);
            Assert.Equal(2u, ver1);
        }

        [Fact]
        public void PerPlayer_TracksPerSlot()
        {
            var (table, rb) = Build(new DslVarDecl("gold", DslValueType.Int, VarScope.PerPlayer, 0));
            rb.Publish(table, 1);

            table.SetInt("gold", 3, 500); // slot 3 only
            rb.Publish(table, 2);

            rb.TryGetScalar("gold", 3, out _, out int slot3, out _, out uint ver3);
            rb.TryGetScalar("gold", 0, out _, out int slot0, out _, out _);
            Assert.Equal(500, slot3);
            Assert.Equal(0, slot0);
            Assert.Equal(2u, ver3);
        }

        [Fact]
        public void PlayerSlotForFaction_MapsEngineFactionToZeroBasedSlot()
        {
            // The engine Faction enum is 1-based (Neutral=0, Player1=1 … Player4=4) but the DSL per-player store is
            // 0-based with slot 0 = Player1 (set_variable passes the trigger's 0-based Faction field straight through).
            // The local player's own slot is engineFaction-1; Neutral/spectator has no player slot → slot 0.
            Assert.Equal(0, DslVarReadback.PlayerSlotForFaction((int)Faction.Neutral)); // 0 → 0 (default)
            Assert.Equal(0, DslVarReadback.PlayerSlotForFaction((int)Faction.Player1)); // 1 → slot 0
            Assert.Equal(1, DslVarReadback.PlayerSlotForFaction((int)Faction.Player2)); // 2 → slot 1
            Assert.Equal(2, DslVarReadback.PlayerSlotForFaction((int)Faction.Player3)); // 3 → slot 2
            Assert.Equal(3, DslVarReadback.PlayerSlotForFaction((int)Faction.Player4)); // 4 → slot 3
        }

        [Fact]
        public void PerPlayer_LocalPlayerReadsOwnSlot_NotNextPlayers()
        {
            // Regression net for the F1 off-by-one: a PerPlayer var written for Player1 (the DSL slot set_variable
            // uses for a Faction-0 trigger action == slot 0) MUST be read back through the local-faction path as
            // Player1's value — not Player2's. Passing the raw engine int (Player1==1) would read slot 1 (Player2).
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.PerPlayer, 0));
            table.SetInt("score", 0, 111); // Player1's slot (0-based, as set_variable writes it)
            table.SetInt("score", 1, 222); // Player2's slot
            rb.Publish(table, 1);

            int p1Slot = DslVarReadback.PlayerSlotForFaction((int)Faction.Player1);
            rb.TryGetScalar("score", p1Slot, out _, out int localValue, out _, out _);
            Assert.Equal(111, localValue); // Player1's own value, NOT 222 (Player2)
        }

        [Fact]
        public void Array_TracksCountAndElements()
        {
            var (table, rb) = Build(new DslVarDecl("board", DslValueType.Array, VarScope.Global, 0,
                elementType: DslValueType.Int, capacity: 8));
            rb.Publish(table, 1);
            Assert.True(rb.TryGetArray("board", out _, out int c0, out _));
            Assert.Equal(0, c0);

            table.ArrayPush("board", 10);
            table.ArrayPush("board", 20);
            rb.Publish(table, 2);
            Assert.True(rb.TryGetArray("board", out _, out int c1, out uint aver));
            Assert.Equal(2, c1);
            Assert.Equal(10, rb.ArrayGet("board", 0));
            Assert.Equal(20, rb.ArrayGet("board", 1));
            Assert.Equal(2u, aver); // bumped from 1 → 2 by the change
        }

        [Fact]
        public void DoubleBuffer_CapturedSnapshotIsTearFree()
        {
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0));
            rb.Publish(table, 1);

            DslVarReadback.Snapshot captured = rb.Published; // grab the front buffer
            int scoreBefore = captured.GRaw0[0];
            uint verBefore = captured.GVer[0];

            // A later publish must NOT mutate the captured snapshot (it fills+swaps the OTHER buffer).
            table.SetInt("score", 0, 777);
            rb.Publish(table, 2);

            Assert.Equal(scoreBefore, captured.GRaw0[0]); // still the old value
            Assert.Equal(verBefore, captured.GVer[0]);
            rb.TryGetScalar("score", 0, out _, out int now, out _, out _);
            Assert.Equal(777, now); // the live published value moved
        }

        [Fact]
        public void UnknownName_ReturnsFalse()
        {
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0));
            rb.Publish(table, 1);
            Assert.False(rb.TryGetScalar("ghost", 0, out _, out _, out _, out _));
            Assert.False(rb.TryGetArray("ghost", out _, out _, out _));
        }

        [Fact]
        public void Clear_ResetsToEmpty()
        {
            var (table, rb) = Build(new DslVarDecl("score", DslValueType.Int, VarScope.Global, 0));
            rb.Publish(table, 1);
            rb.Clear();
            Assert.False(rb.TryGetScalar("score", 0, out _, out _, out _, out _));
        }
    }
}
