#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;                 // EntityWorld, Faction, UnitOrder, UnitCommand, Fixed, EntityFlags
using ProjectChimera.Multiplayer;           // TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;    // MergedTickBuilder, MergedTickApplier
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.3 — the single deterministic apply core <see cref="MergedTickApplier"/>. Proves a merged packet's
    /// sub-bundles apply per faction (a Move lands on the right unit, and the anti-cheat ownership guard drops an
    /// order against another faction's unit), that the per-sub-bundle recorder hook fires in ascending order, and
    /// that an empty/malformed merged packet is a deterministic no-op.
    /// </summary>
    public class MergedTickApplierTests
    {
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        private static UnitOrder Move(int unitId, int rx, int rz) =>
            new UnitOrder(unitId, UnitCommand.Move, Fixed.FromRaw(rx), Fixed.FromRaw(rz));

        private static UnitOrder AMove(int unitId, int rx, int rz) =>
            new UnitOrder(unitId, UnitCommand.AttackMove, Fixed.FromRaw(rx), Fixed.FromRaw(rz));

        private static (byte[] data, int len) Tick(uint tick, Faction faction, params UnitOrder[] orders)
        {
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, tick, faction, orders, orders.Length);
            return (buf, len);
        }

        /// <summary>Build a real merged packet (through the builder) from a P1 + P2 order set for one tick.</summary>
        private static byte[] BuildMerged(uint tick, UnitOrder p1, UnitOrder p2, out int len)
        {
            var b = new MergedTickBuilder(2, SlotFaction);
            var (d0, l0) = Tick(tick, Faction.Player1, p1);
            var (d1, l1) = Tick(tick, Faction.Player2, p2);
            b.Submit(0, d0, l0, out _);
            b.Submit(1, d1, l1, out _);
            Assert.True(b.TryBuild(tick, out byte[] merged, out len));
            var copy = new byte[len];
            System.Array.Copy(merged, copy, len);
            return copy;
        }

        [Fact]
        public void AppliesEachSubBundle_ToItsOwnFaction()
        {
            var world = new EntityWorld();
            int p1Unit = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));

            byte[] merged = BuildMerged(1u,
                Move(p1Unit, Fixed.FromInt(20).Raw, Fixed.FromInt(5).Raw),
                Move(p2Unit, Fixed.FromInt(-7).Raw, Fixed.FromInt(9).Raw), out int len);

            MergedTickApplier.Apply(merged, len, world);

            Assert.Equal(Fixed.FromInt(20), world.MoveTarget[p1Unit].X);
            Assert.Equal(Fixed.FromInt(5),  world.MoveTarget[p1Unit].Z);
            Assert.True((world.Flags[p1Unit] & EntityFlags.Moving) != 0);

            Assert.Equal(Fixed.FromInt(-7), world.MoveTarget[p2Unit].X);
            Assert.Equal(Fixed.FromInt(9),  world.MoveTarget[p2Unit].Z);
            Assert.True((world.Flags[p2Unit] & EntityFlags.Moving) != 0);
        }

        [Fact]
        public void AntiCheatGuard_DropsOrderAgainstAnotherFactionsUnit()
        {
            var world = new EntityWorld();
            int p1Unit = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));

            // The Player2 sub-bundle names the Player1 unit — OrderApplier's ownership guard must drop it.
            byte[] merged = BuildMerged(1u,
                Move(p1Unit, 0, 0),
                Move(p1Unit, Fixed.FromInt(99).Raw, Fixed.FromInt(99).Raw), out int len);

            MergedTickApplier.Apply(merged, len, world);

            // The P1 order (target 0,0) applied; the P2-issued order against the P1 unit did NOT overwrite it.
            Assert.Equal(Fixed.Zero, world.MoveTarget[p1Unit].X);
            Assert.Equal(Fixed.Zero, world.MoveTarget[p1Unit].Z);
        }

        [Fact]
        public void RecorderHook_FiresPerSubBundle_Ascending()
        {
            var world = new EntityWorld();
            int p1Unit = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));

            byte[] merged = BuildMerged(1u, Move(p1Unit, 1, 1), Move(p2Unit, 2, 2), out int len);

            var seen = new List<Faction>();
            MergedTickApplier.Apply(merged, len, world,
                onSubBundle: (f, buf, baseIdx, count) => seen.Add(f));

            Assert.Equal(new[] { Faction.Player1, Faction.Player2 }, seen);
        }

        [Fact]
        public void ForwardsPresentationHooks_ToTheRightFactionsUnit()
        {
            // The applier forwards a long positional tail of delegates to OrderApplier.Apply; a transposition (e.g.
            // onRequestPath ↔ onRequestAttackMove) would silently break live Build/attack-move while every faction-
            // level test stays green. Pin that a Move routes to onRequestPath for the P1 unit and an AttackMove
            // routes to onRequestAttackMove for the P2 unit — and onCancelPath never fires.
            var world = new EntityWorld();
            int p1Unit = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));

            byte[] merged = BuildMerged(1u,
                Move(p1Unit, Fixed.FromInt(4).Raw, Fixed.FromInt(6).Raw),
                AMove(p2Unit, Fixed.FromInt(-2).Raw, Fixed.FromInt(8).Raw), out int len);

            int pathId = -1, amoveId = -1; bool cancelFired = false;
            MergedTickApplier.Apply(merged, len, world,
                onRequestPath:       (id, x, z) => pathId = id,
                onRequestAttackMove: (id, x, z) => amoveId = id,
                onCancelPath:        id => cancelFired = true);

            Assert.Equal(p1Unit, pathId);   // Move → onRequestPath, on the P1 unit
            Assert.Equal(p2Unit, amoveId);  // AttackMove → onRequestAttackMove, on the P2 unit
            Assert.False(cancelFired);       // neither command cancels a path
        }

        [Fact]
        public void RecorderHook_ForwardsCorrectSlice_PerSubBundle()
        {
            // The single recorder hook replaced the old dual local/remote RecordTick calls; if it passed the wrong
            // buffer slice or count, replays would capture the wrong orders while the golden (no recorder) stayed
            // green. Assert the hook sees each faction's OWN order at its baseIdx with the right count.
            var world = new EntityWorld();
            int p1Unit = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));

            byte[] merged = BuildMerged(1u, Move(p1Unit, 1, 1), Move(p2Unit, 2, 2), out int len);

            var seen = new List<(Faction f, int firstUnit, int count)>();
            MergedTickApplier.Apply(merged, len, world,
                onSubBundle: (f, buf, baseIdx, count) => seen.Add((f, buf[baseIdx].UnitId, count)));

            Assert.Equal(2, seen.Count);
            Assert.Equal((Faction.Player1, p1Unit, 1), seen[0]);
            Assert.Equal((Faction.Player2, p2Unit, 1), seen[1]);
        }

        [Fact]
        public void ConcedeSubBundle_WithWinState_LatchesLostForThatFactionOnly()
        {
            // Story 11.2 — proves the online-merge apply path THREADS the WinStateStore into OrderApplier: a P1
            // sub-bundle carrying UnitCommand.Concede latches P1's verdict LOST (and only P1's). Without the threading
            // (winState never forwarded to OrderApplier.Apply), the concede would silently no-op on every online peer.
            var world = new EntityWorld();
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            var win = new WinStateStore();

            byte[] merged = BuildMerged(1u,
                new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero), // P1 concedes (faction re-stamped from slot)
                Move(p2Unit, 1, 1), out int len);

            MergedTickApplier.Apply(merged, len, world, winState: win);

            Assert.Equal(WinStateStore.VERDICT_LOST, win.Verdict[(int)Faction.Player1]);
            Assert.Equal(WinStateStore.VERDICT_NONE, win.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void ConcedeSubBundle_NullWinState_IsDeterministicNoOp()
        {
            // The golden/spectator path passes no winState → a Concede in the stream must be a harmless no-op (no throw).
            var world = new EntityWorld();
            int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero), Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            byte[] merged = BuildMerged(1u,
                new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero),
                Move(p2Unit, 1, 1), out int len);

            MergedTickApplier.Apply(merged, len, world); // winState omitted (null)

            Assert.True((world.Flags[p2Unit] & EntityFlags.Moving) != 0); // the co-bundled Move still applied
        }

        [Fact]
        public void EmptyOrMalformed_IsNoOp()
        {
            var world = new EntityWorld();
            int u = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            FixedVec3 before = world.MoveTarget[u];

            MergedTickApplier.Apply(System.Array.Empty<byte>(), 0, world);          // empty
            MergedTickApplier.Apply(new byte[MergedTickPacket.MERGED_MAX_BYTES], 0, world); // len 0
            var garbage = new byte[3]; garbage[0] = 0xFF;
            MergedTickApplier.Apply(garbage, garbage.Length, world);                 // wrong type

            Assert.Equal(before.X, world.MoveTarget[u].X);
            Assert.Equal(before.Z, world.MoveTarget[u].Z);
        }
    }
}
