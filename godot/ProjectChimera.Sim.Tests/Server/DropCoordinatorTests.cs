#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;                 // Faction
using ProjectChimera.Multiplayer.Server;   // DropCoordinator, DropController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-411 — the DedicatedServer freeze adapter's DECISION GLUE, now Godot-free and unit-tested: the
    /// survivors&gt;0 vs survivors&lt;=0 ("match truly over") gating, the applyAtTick capture (emittedThrough+1 at
    /// ISSUE time), the DropAck faction-byte→slot mapping, and the ACK→commit→(DropReporter+inject) chain — plus
    /// DW-409's queue-or-escalate for a CONCURRENT drop and the survivor-ACK-set reconcile on any in-match
    /// disconnect. Each seam is captured, so every wire action the adapter would take is asserted, ENet/Godot-free.
    /// </summary>
    public class DropCoordinatorTests
    {
        /// <summary>A test double for the DedicatedServer seams: connectivity, frontier, and both action sinks.</summary>
        private sealed class Rig
        {
            public readonly HashSet<int> Connected = new();
            public long EmittedThrough = 41; // arbitrary non-zero frontier → applyAtTick should read 42
            public readonly List<(Faction faction, uint applyAtTick)> Directives = new();
            public readonly List<int> Committed = new();
            public DropCoordinator Co = null!;

            public Rig(int players)
            {
                for (int s = 0; s < players; s++) Connected.Add(s);
                Co = new DropCoordinator(players, SlotFactions(players),
                    () => EmittedThrough,
                    s => Connected.Contains(s),
                    (f, t) => Directives.Add((f, t)),
                    slot => Committed.Add(slot));
            }

            /// <summary>Disconnect = transport clears the slot FIRST (as ENet does), then the adapter is told.</summary>
            public DropCoordinator.DisconnectOutcome Disconnect(int slot)
            {
                Connected.Remove(slot);
                return Co.OnPlayerDisconnect(slot);
            }

            /// <summary>ACK from <paramref name="ackSlot"/> for the LAST issued directive (its faction + applyAtTick).</summary>
            public bool AckLastDirective(int ackSlot)
            {
                (Faction f, uint t) = Directives[^1];
                return Co.OnDropAck(ackSlot, (byte)f, t);
            }

            private static Faction[] SlotFactions(int n)
            {
                var a = new Faction[n];
                for (int i = 0; i < n; i++) a[i] = FactionRegistry.ToFaction(i);
                return a;
            }
        }

        [Fact]
        public void Disconnect_WithSurvivors_IssuesDirective_WithFreshApplyTick()
        {
            var rig = new Rig(2);
            var outcome = rig.Disconnect(1);

            Assert.Equal(DropCoordinator.DisconnectOutcome.DirectiveIssued, outcome);
            var d = Assert.Single(rig.Directives);
            Assert.Equal(FactionRegistry.ToFaction(1), d.faction);   // the DROPPED slot's authoritative faction
            Assert.Equal(42u, d.applyAtTick);                        // emittedThrough(41) + 1, captured at issue time
            Assert.True(rig.Co.Controller.DirectivePending);
            Assert.Equal(1, rig.Co.Controller.PendingDroppedSlot);
        }

        [Fact]
        public void Disconnect_SurvivorLess_IsMatchOver_NoDirective()
        {
            // DW-411 named this the branch "unverified anywhere": the survivors<=0 'match truly over' arm.
            var rig = new Rig(2);
            rig.Disconnect(1);              // first drop → directive pending on survivor 0
            var outcome = rig.Disconnect(0); // the LAST player leaves

            Assert.Equal(DropCoordinator.DisconnectOutcome.MatchOver, outcome);
            Assert.Single(rig.Directives);   // no second directive — nobody is left to ACK one
            Assert.Empty(rig.Committed);     // and nothing ever committed
        }

        [Fact]
        public void AckChain_AllSurvivorsAck_CommitsOnce_AndRunsTheCommitSeam()
        {
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.False(rig.AckLastDirective(0));   // 1 of 2 — not yet
            Assert.Empty(rig.Committed);
            Assert.True(rig.AckLastDirective(1));    // 2 of 2 → commit

            Assert.Equal(new[] { 2 }, rig.Committed);            // DropReporter+pump seam ran for the dropped slot
            Assert.True(rig.Co.Controller.IsFrozen(2));
            Assert.Equal(42u, rig.Co.Controller.FrozenApplyTick(2));
            Assert.False(rig.Co.Controller.DirectivePending);
        }

        [Fact]
        public void DropAck_UnknownFactionByte_IsIgnored()
        {
            var rig = new Rig(2);
            rig.Disconnect(1);
            // A garbage faction byte maps to no player slot → no ACK recorded, nothing commits.
            Assert.False(rig.Co.OnDropAck(0, (byte)Faction.Neutral, 42u));
            Assert.False(rig.Co.Controller.AllAcked());
            Assert.Empty(rig.Committed);
        }

        [Fact]
        public void ConcurrentDrop_IsQueued_ThenIssuedAfterTheCommit()
        {
            // DW-409 (queue half): N=3 — slot 2 drops (directive 1 pending on {0,1}); slot 1 drops WHILE it is
            // pending. Pre-fix NotifyDrop returned false and the second freeze was silently LOST: slot 1 was never
            // frozen, the fan-in waited on it forever, and every survivor stalled with no MATCH SUMMARY.
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.Single(rig.Directives);

            var outcome = rig.Disconnect(1);
            Assert.Equal(DropCoordinator.DisconnectOutcome.Queued, outcome);
            Assert.Equal(new[] { 1 }, rig.Co.QueuedDrops.ToArray());
            Assert.Single(rig.Directives);           // not issued yet — one directive in flight at a time

            // The lone remaining survivor ACKs directive 1 → commit slot 2 → the QUEUED directive for slot 1 is
            // issued automatically, with a FRESH applyAtTick read AFTER the commit seam (the injection pump) ran.
            rig.EmittedThrough = 60;                 // the pump advanced the frontier
            Assert.True(rig.AckLastDirective(0));

            Assert.Equal(new[] { 2 }, rig.Committed);
            Assert.Equal(2, rig.Directives.Count);
            Assert.Equal(FactionRegistry.ToFaction(1), rig.Directives[1].faction);
            Assert.Equal(61u, rig.Directives[1].applyAtTick);   // fresh capture — not the stale first marker
            Assert.Empty(rig.Co.QueuedDrops);

            // And the queued freeze completes end-to-end: survivor 0 ACKs directive 2 → slot 1 frozen too.
            Assert.True(rig.AckLastDirective(0));
            Assert.Equal(new[] { 2, 1 }, rig.Committed);
            Assert.True(rig.Co.Controller.IsFrozen(1));
            Assert.True(rig.Co.Controller.IsFrozen(2));
        }

        [Fact]
        public void UnAckedSurvivorDisconnects_PendingCommitsFromRemainingAcks_ThenItsOwnDirectiveIssues()
        {
            // DW-409 (prune half): N=3 — slot 2 drops, survivors {0,1}; slot 0 ACKs; slot 1 disconnects BEFORE
            // ACKing. Pre-fix the pending _isSurvivor snapshot was never reconciled, so AllAcked() could never
            // complete → deadlock. Now: slot 1 is pruned → the remaining ACK set is complete → freeze 2 commits
            // IN the disconnect call → and slot 1's OWN directive issues immediately (nothing left pending).
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.False(rig.AckLastDirective(0));   // slot 0 ACKs directive 1

            var outcome = rig.Disconnect(1);
            Assert.Equal(DropCoordinator.DisconnectOutcome.DirectiveIssued, outcome);

            Assert.Equal(new[] { 2 }, rig.Committed);            // the pending freeze committed on the prune
            Assert.True(rig.Co.Controller.IsFrozen(2));
            Assert.Equal(2, rig.Directives.Count);               // and slot 1's own directive went out directly
            Assert.Equal(FactionRegistry.ToFaction(1), rig.Directives[1].faction);
            Assert.Equal(1, rig.Co.Controller.PendingDroppedSlot);
            Assert.Empty(rig.Co.QueuedDrops);                    // nothing needed queueing

            Assert.True(rig.AckLastDirective(0));                // survivor 0 ACKs directive 2
            Assert.True(rig.Co.Controller.IsFrozen(1));
        }

        [Fact]
        public void StaleAck_FromAPrunedSurvivor_NeverCounts()
        {
            // A late ACK from the disconnected survivor (in flight when it dropped) must not complete a directive
            // it is no longer part of.
            var rig = new Rig(3);
            rig.Disconnect(2);
            rig.Disconnect(1);                       // queued + pruned from directive 1's ACK set
            Assert.False(rig.AckLastDirective(1));   // slot 1's stale ACK → ignored (not a recorded survivor)
            Assert.Empty(rig.Committed);
            Assert.True(rig.AckLastDirective(0));    // only the genuine survivor completes it
            Assert.Equal(new[] { 2 }, rig.Committed);
        }

        [Fact]
        public void DuplicateDisconnect_OfPendingOrQueuedSlot_IsIgnored()
        {
            var rig = new Rig(3);
            rig.Disconnect(2);
            Assert.Equal(DropCoordinator.DisconnectOutcome.Ignored, rig.Co.OnPlayerDisconnect(2)); // pending already
            rig.Disconnect(1);
            Assert.Equal(DropCoordinator.DisconnectOutcome.Ignored, rig.Co.OnPlayerDisconnect(1)); // queued already
            Assert.Equal(new[] { 1 }, rig.Co.QueuedDrops.ToArray());                               // no duplicate
            Assert.Single(rig.Directives);
        }

        [Fact]
        public void SpectatorOrOutOfRangeSlot_IsIgnored()
        {
            var rig = new Rig(2);
            Assert.Equal(DropCoordinator.DisconnectOutcome.Ignored, rig.Co.OnPlayerDisconnect(5));
            Assert.Equal(DropCoordinator.DisconnectOutcome.Ignored, rig.Co.OnPlayerDisconnect(-1));
            Assert.Empty(rig.Directives);
        }

        [Fact]
        public void FactionToSlot_MapsAuthoritatively_MinusOneForUnknown()
        {
            var rig = new Rig(3);
            Assert.Equal(0, rig.Co.FactionToSlot(FactionRegistry.ToFaction(0)));
            Assert.Equal(2, rig.Co.FactionToSlot(FactionRegistry.ToFaction(2)));
            Assert.Equal(-1, rig.Co.FactionToSlot(Faction.Neutral));
            Assert.Equal(-1, rig.Co.FactionToSlot(FactionRegistry.ToFaction(3))); // beyond this match's players
        }
    }
}
