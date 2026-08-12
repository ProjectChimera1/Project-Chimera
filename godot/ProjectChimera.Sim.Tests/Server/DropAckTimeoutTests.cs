#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;                 // Faction, FactionRegistry
using ProjectChimera.Multiplayer.Server;   // DropController, DropCoordinator
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-410 — the drop directive/ACK state machine's LIVENESS fallback.
    ///
    /// <para>The defect. The freeze committed only on <see cref="DropController.AllAcked"/> over the survivor set,
    /// with no deadline, re-send, or force-commit. DW-409 already handles a survivor that LEAVES (it is pruned from
    /// the ACK set), but nothing at all handled a survivor that stays transport-connected and hung: it never
    /// disconnects, so it is never pruned, and it never ACKs, so <c>AllAcked</c> can never complete. The freeze then
    /// stays pending FOREVER — <c>FrozenSlotInjector</c> never runs, the merged fan-in never completes another tick,
    /// and every other survivor plus every spectator stalls for the rest of the match. Reachable at N≥3 since
    /// 9-7/9-15 shipped.</para>
    ///
    /// <para>The closure, mirroring <see cref="DelayController.CheckAckTimeout"/>: a tick-bounded escalation that
    /// force-commits the freeze over the survivors that DID ACK and treats the ones that did not as dropped in turn,
    /// so the match always makes progress. Aborting to a match summary instead is explicitly rejected — that hands
    /// one hung client the power to end everyone's game, the hostage dynamic freeze-and-continue exists to remove.</para>
    /// </summary>
    public class DropAckTimeoutTests
    {
        private const uint Base = 1000; // an arbitrary non-zero clock origin — nothing may depend on it being 0

        // ── DropController: the deadline itself ──────────────────────────────────────

        /// <summary>The deadline is armed by the first pump after a directive goes pending and fires only once
        /// <see cref="DropController.ACK_TIMEOUT_TICKS"/> have elapsed on the caller's clock — never a tick early.</summary>
        [Fact]
        public void PendingFreeze_ForceCommitsOnlyAfterTheFullDeadline()
        {
            var c = new DropController(3);
            Assert.True(c.NotifyDrop(2, applyAtTick: 42, new[] { 0, 1 }));

            // First pump arms the clock — it must not fire on the same call it armed on.
            Assert.False(c.CheckAckTimeout(Base, out _, out _, out _));
            Assert.False(c.CheckAckTimeout(Base + DropController.ACK_TIMEOUT_TICKS - 1, out _, out _, out _));
            Assert.True(c.DirectivePending);

            Assert.True(c.CheckAckTimeout(Base + DropController.ACK_TIMEOUT_TICKS,
                                          out int slot, out uint applyAt, out _));
            Assert.Equal(2, slot);
            Assert.Equal(42u, applyAt);
        }

        /// <summary>On expiry the freeze is COMMITTED exactly as the all-ACKed path commits it, and the survivors
        /// that never ACKed are reported ascending so the caller can drop them in turn.</summary>
        [Fact]
        public void ForceCommit_FreezesTheSlot_AndReportsEveryHungSurvivor()
        {
            var c = new DropController(4);
            Assert.True(c.NotifyDrop(3, applyAtTick: 77, new[] { 0, 1, 2 }));
            c.RecordAck(1, droppedSlot: 3, applyAtTick: 77);   // only slot 1 answers; 0 and 2 are hung

            c.CheckAckTimeout(Base, out _, out _, out _);      // arm
            Assert.True(c.CheckAckTimeout(Base + DropController.ACK_TIMEOUT_TICKS,
                                          out int slot, out uint applyAt, out int[] hung));

            Assert.Equal(3, slot);
            Assert.Equal(77u, applyAt);
            Assert.Equal(new[] { 0, 2 }, hung);                // ascending by slot — the deterministic contract
            Assert.True(c.IsFrozen(3));
            Assert.Equal(77u, c.FrozenApplyTick(3));
            Assert.Contains(3, c.FrozenSlots);
            Assert.False(c.DirectivePending);                  // the directive is finished, not still hanging
            Assert.Equal(-1, c.PendingDroppedSlot);
        }

        /// <summary>The normal path is untouched: once every survivor ACKs and the freeze commits, nothing is
        /// pending, so the deadline can never fire — no matter how far the caller's clock runs.</summary>
        [Fact]
        public void NormallyCommittedFreeze_NeverTriggersTheDeadline()
        {
            var c = new DropController(3);
            c.NotifyDrop(2, applyAtTick: 42, new[] { 0, 1 });
            c.CheckAckTimeout(Base, out _, out _, out _);      // arm mid-flight, as the real pump does
            c.RecordAck(0, 2, 42);
            c.RecordAck(1, 2, 42);
            Assert.True(c.Commit());

            Assert.False(c.CheckAckTimeout(Base + 10 * DropController.ACK_TIMEOUT_TICKS, out _, out _, out _));
            Assert.Single(c.FrozenSlots);                      // and no second, phantom commit
        }

        /// <summary>An idle controller (nothing ever pending) is a pure no-op — a pump running every server frame
        /// must not manufacture a freeze.</summary>
        [Fact]
        public void NoPendingDirective_IsANoOp()
        {
            var c = new DropController(2);
            for (uint t = 0; t < 3 * DropController.ACK_TIMEOUT_TICKS; t += 100)
                Assert.False(c.CheckAckTimeout(Base + t, out _, out _, out _));
            Assert.Empty(c.FrozenSlots);
        }

        /// <summary>Each directive gets its OWN deadline: a second one issued long after the first must not inherit
        /// the elapsed clock and fire immediately.</summary>
        [Fact]
        public void EachDirective_GetsAFreshDeadline()
        {
            var c = new DropController(4);
            c.NotifyDrop(3, applyAtTick: 10, new[] { 0, 1, 2 });
            c.CheckAckTimeout(Base, out _, out _, out _);
            uint expiry = Base + DropController.ACK_TIMEOUT_TICKS;
            Assert.True(c.CheckAckTimeout(expiry, out _, out _, out _));

            // A second freeze issued at the moment the first expired must survive its own full window.
            Assert.True(c.NotifyDrop(2, applyAtTick: 20, new[] { 0, 1 }));
            Assert.False(c.CheckAckTimeout(expiry, out _, out _, out _));                 // arm
            Assert.False(c.CheckAckTimeout(expiry + DropController.ACK_TIMEOUT_TICKS - 1, out _, out _, out _));
            Assert.True(c.CheckAckTimeout(expiry + DropController.ACK_TIMEOUT_TICKS, out int slot, out _, out _));
            Assert.Equal(2, slot);
        }

        // ── DropCoordinator: the escalation the adapter pumps ────────────────────────

        /// <summary>The DedicatedServer seams (connectivity, frontier, both action sinks) — the DropCoordinatorTests
        /// rig, rebuilt here so this file stands alone.</summary>
        private sealed class Rig
        {
            public readonly HashSet<int> Connected = new();
            public long EmittedThrough = 41;
            public readonly List<(Faction faction, uint applyAtTick)> Directives = new();
            public readonly List<int> Committed = new();
            public DropCoordinator Co = null!;

            public Rig(int players)
            {
                for (int s = 0; s < players; s++) Connected.Add(s);
                var factions = new Faction[players];
                for (int i = 0; i < players; i++) factions[i] = FactionRegistry.ToFaction(i);
                Co = new DropCoordinator(players, factions,
                    () => EmittedThrough,
                    s => Connected.Contains(s),
                    (f, t) => Directives.Add((f, t)),
                    slot => Committed.Add(slot));
            }

            /// <summary>Disconnect = the transport clears the slot FIRST (as ENet does), then the adapter is told.</summary>
            public DropCoordinator.DisconnectOutcome Disconnect(int slot)
            {
                Connected.Remove(slot);
                return Co.OnPlayerDisconnect(slot);
            }

            public bool AckLastDirective(int ackSlot)
            {
                (Faction f, uint t) = Directives[^1];
                return Co.OnDropAck(ackSlot, (byte)f, t);
            }

            /// <summary>Pump the deadline the way <c>DedicatedServer._Process</c> does: arm, then run the clock out.</summary>
            public bool RunOutTheDeadline()
            {
                Co.CheckAckTimeout(Base);
                return Co.CheckAckTimeout(Base + DropController.ACK_TIMEOUT_TICKS);
            }
        }

        /// <summary>
        /// The end-to-end DW-410 scenario at N=3. Slot 2 drops; survivors {0,1} must ACK; slot 0 ACKs and slot 1 is
        /// HUNG — connected, so DW-409's prune never touches it, and silent, so <c>AllAcked</c> never completes.
        /// Pre-fix this stalled the whole match forever. Now the deadline force-commits slot 2's freeze over slot 0's
        /// ACK (the commit seam runs: quorum drop + injection pump) and slot 1 is dropped in turn, so the merged
        /// fan-in stops waiting on it and the match continues.
        /// </summary>
        [Fact]
        public void HungSurvivor_ExpiresTheDeadline_FreezeCommits_AndTheHungPeerIsDroppedInTurn()
        {
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.Single(rig.Directives);
            Assert.False(rig.AckLastDirective(0));   // slot 0 ACKs; slot 1 never will
            Assert.Empty(rig.Committed);

            rig.EmittedThrough = 60;                 // the frontier the post-commit pump leaves behind
            Assert.True(rig.RunOutTheDeadline());

            // 1. The pending freeze committed over the survivor that DID ACK.
            Assert.Equal(new[] { 2 }, rig.Committed);
            Assert.True(rig.Co.Controller.IsFrozen(2));

            // 2. The hung survivor is now being dropped itself — its directive went out with a FRESH applyAtTick
            //    read after the commit seam ran.
            Assert.Equal(2, rig.Directives.Count);
            Assert.Equal(FactionRegistry.ToFaction(1), rig.Directives[1].faction);
            Assert.Equal(61u, rig.Directives[1].applyAtTick);
            Assert.Equal(1, rig.Co.Controller.PendingDroppedSlot);
            Assert.Empty(rig.Co.QueuedDrops);

            // 3. …and the healthy survivor completes it, so the match ends up with both silent slots frozen and
            //    injected for — progress, not a stall.
            Assert.True(rig.AckLastDirective(0));
            Assert.Equal(new[] { 2, 1 }, rig.Committed);
            Assert.True(rig.Co.Controller.IsFrozen(1));
            Assert.True(rig.Co.Controller.IsFrozen(2));
        }

        /// <summary>A freeze every survivor ACKs must never be touched by the deadline, and no survivor may be
        /// escalated — the guard only fires on a genuinely silent peer.</summary>
        [Fact]
        public void FullyAckedFreeze_IsUnaffectedByThePump_AndEscalatesNobody()
        {
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.False(rig.AckLastDirective(0));
            Assert.True(rig.AckLastDirective(1));    // committed the normal way

            Assert.False(rig.RunOutTheDeadline());
            Assert.False(rig.Co.CheckAckTimeout(Base + 5 * DropController.ACK_TIMEOUT_TICKS));

            Assert.Equal(new[] { 2 }, rig.Committed);
            Assert.Single(rig.Directives);           // nobody was escalated
            Assert.False(rig.Co.Controller.IsFrozen(0));
            Assert.False(rig.Co.Controller.IsFrozen(1));
        }

        /// <summary>A match with no drop at all: the per-frame pump is inert.</summary>
        [Fact]
        public void QuietMatch_PumpChangesNothing()
        {
            var rig = new Rig(3);
            for (uint t = 0; t < 5 * DropController.ACK_TIMEOUT_TICKS; t += 97)
                Assert.False(rig.Co.CheckAckTimeout(Base + t));
            Assert.Empty(rig.Directives);
            Assert.Empty(rig.Committed);
            Assert.Empty(rig.Co.Controller.FrozenSlots.ToArray());
        }
    }
}
