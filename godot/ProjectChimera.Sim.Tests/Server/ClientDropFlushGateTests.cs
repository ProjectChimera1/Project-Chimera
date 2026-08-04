#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;                 // Faction, UnitOrder
using ProjectChimera.Multiplayer;          // MergedStreamGate, TickCommandPacket, MergedTickPacket
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, DropController, FrozenSlotInjector
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-417 — the surviving CLIENT's Flush-gate unstall across a mid-match drop: the literal center of Story
    /// 9.6's problem statement ("every surviving client's Flush gate stalls permanently"). Previously verified only
    /// transitively ("merged packets arrive ⇒ Flush would fill"); this drives the PRODUCTION gate —
    /// <see cref="MergedStreamGate"/>, the extracted ring <c>LockstepManager.Flush</c> itself gates on — through a
    /// real drop:
    ///   1. a faithful client step loop (issue at exec+delay, gate on the merged packet for exec) STALLS the gate
    ///      when the peer vanishes at the drop tick, and STAYS stalled for arbitrarily many frames;
    ///   2. the server freeze commits (real <see cref="DropController"/> directive/ACK/commit) and the real
    ///      <see cref="FrozenSlotInjector.Drain"/> resumes the merged stream;
    ///   3. the client RESUMES stepping from arriving packets alone — <see cref="MergedStreamGate.SeedEmpty"/> is
    ///      NEVER called after the match-start bootstrap (the asymmetry with a delay change, which DOES pre-seed,
    ///      is exactly the claim the HandleDropDirective doc makes and this test pins) — and every post-drop tick
    ///      it consumes is a REAL server-built merged packet (length &gt; 0), not a self-seeded empty.
    /// </summary>
    public class ClientDropFlushGateTests
    {
        /// <summary>Mirrors LockstepManager.INPUT_DELAY (the client's issue-ahead distance).</summary>
        private const int Delay = 4;
        private const int DropTick = 40;

        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        /// <summary>
        /// A faithful client+server drop harness over the production pieces. The client models exactly what
        /// LockstepManager.Flush does around the gate: submit its own single-faction packet for
        /// <c>exec + Delay</c> once per exec tick, then advance only when <see cref="MergedStreamGate.TryConsume"/>
        /// yields the merged packet for exec. The server is the real <see cref="MergedTickBuilder"/>; every built
        /// merged packet is delivered straight into the gate (<see cref="MergedStreamGate.TryReceive"/> — the
        /// HandleMergedTick path).
        /// </summary>
        private sealed class Harness
        {
            public readonly MergedTickBuilder Builder = new(2, SlotFaction);
            public readonly MergedStreamGate Gate = new();
            public readonly DropController Drops = new(2);
            public readonly List<(uint tick, int len)> Consumed = new();

            public uint ClientTick;             // the client's exec frontier (next tick to consume)
            public uint Frontier;               // highest tick fanned in (the server's _latestSeenTick)
            private uint _nextIssue;            // next issueTick the client will send (single send per tick)
            private readonly byte[] _packBuf   = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            private readonly byte[] _injectBuf = new byte[TickCommandPacket.HEADER_BYTES];
            private readonly UnitOrder[] _noOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];

            public Harness()
            {
                // Match-start bootstrap (SeedInitialTicks): ticks 0..Delay-1 are pre-seeded empty. This is the ONLY
                // seeding the whole test ever performs — the drop must unstall WITHOUT any further seed.
                for (uint t = 0; t < Delay; t++) Gate.SeedEmpty(t);
                _nextIssue = Delay; // the first real issueTick both clients send
            }

            /// <summary>Fan a slot's (empty-payload) packet for <paramref name="tick"/> into the builder, deliver
            /// any completed merged packet into the client gate, and track the frontier.</summary>
            public void Submit(int slot, uint tick)
            {
                int len = TickCommandPacket.Write(_packBuf, tick, SlotFaction[slot], _noOrders, 0);
                if (Builder.Submit(slot, _packBuf, len, out uint decoded))
                {
                    if (decoded > Frontier) Frontier = decoded;
                    if (Builder.TryBuild(decoded, out byte[] merged, out int mergedLen))
                        Gate.TryReceive(merged, mergedLen); // server → client wire, straight into the arrival ring
                }
            }

            /// <summary>
            /// One client frame (the Flush shape): send the local bundle for the current issueTick (once), have the
            /// REMOTE peer submit too while it is still alive (pre-drop), then gate on exec. Returns true when the
            /// client stepped (gate yielded exec's merged packet), false when it stalled this frame.
            /// </summary>
            public bool Frame(bool remoteAlive)
            {
                uint issue = ClientTick + Delay;
                if (_nextIssue <= issue)
                {
                    // The survivor (slot 0) submits every issueTick through the current one; the remote (slot 1)
                    // submits only while alive. Mirrors "a command issued at T executes at T+delay".
                    for (uint t = _nextIssue; t <= issue; t++)
                    {
                        Submit(0, t);
                        if (remoteAlive) Submit(1, t);
                    }
                    _nextIssue = issue + 1;
                }

                if (!Gate.TryConsume(ClientTick, out _, out int len)) return false; // ── the Flush stall gate ──
                Consumed.Add((ClientTick, len));
                ClientTick++;
                return true;
            }

            /// <summary>The real post-commit injection pump (DedicatedServer.PumpFrozenInjection's shape).</summary>
            public void PumpInjection() =>
                FrozenSlotInjector.Drain(Builder, Drops.FrozenSlots, SlotFaction, Frontier, _injectBuf,
                    (buf, len) => Gate.TryReceive(buf, len));
        }

        [Fact]
        public void FlushGate_StallsOnTheDrop_ThenResumesFromTheInjectedStream_WithoutAnyPreSeed()
        {
            var h = new Harness();

            // ── Phase 1: both peers alive — the client steps in lockstep to the drop tick. ──
            int guard = 0;
            while (h.ClientTick < DropTick)
            {
                Assert.True(h.Frame(remoteAlive: h.ClientTick + Delay < DropTick + Delay),
                    $"Client stalled pre-drop at tick {h.ClientTick} — the two-peer merge should flow freely.");
                Assert.True(++guard < 10_000, "runaway loop");
            }

            // Slot 1 vanishes: from now on it submits nothing. The client keeps issuing ahead but must STALL at
            // some tick ≥ DropTick, where the merge is missing the dead slot — and stay stalled indefinitely.
            // (The last pre-drop frames already stopped remote submits for ticks ≥ DropTick + Delay via the
            // remoteAlive expression above, so the stall lands exactly at the first uncovered tick.)
            int stalledFrames = 0;
            uint stallTick = 0;
            for (int frame = 0; frame < 200; frame++)
            {
                if (!h.Frame(remoteAlive: false))
                {
                    if (stalledFrames == 0) stallTick = h.ClientTick;
                    stalledFrames++;
                    Assert.Equal(stallTick, h.ClientTick); // pinned — no progress while stalled
                }
            }
            Assert.True(stalledFrames >= 150,
                $"Expected a persistent stall after the drop; only {stalledFrames} stalled frames observed.");
            Assert.True(stallTick >= DropTick,
                $"The stall began at tick {stallTick}, before the drop at {DropTick} — the pre-drop stream broke.");

            // ── Phase 2: the server freeze commits (real directive → survivor ACK → commit)… ──
            uint applyAtTick = (uint)(h.Builder.EmittedThrough + 1);
            Assert.True(h.Drops.NotifyDrop(1, applyAtTick, new[] { 0 }));
            h.Drops.RecordAck(0, 1, applyAtTick);
            Assert.True(h.Drops.AllAcked() && h.Drops.Commit());

            // … and the REAL injector resumes the merged stream (the commit-time pump). NOTE: no Gate.SeedEmpty —
            // the gate must fill from ARRIVING packets alone.
            h.PumpInjection();

            // ── Phase 3: the client unstalls and keeps stepping (survivor submits + per-frame pump, mirroring
            //    FanInTickCommands → PumpFrozenInjection). ──
            guard = 0;
            while (h.ClientTick < DropTick + 100)
            {
                bool stepped = h.Frame(remoteAlive: false);
                h.PumpInjection();
                Assert.True(stepped || ++guard < 500,
                    $"Client failed to unstall/advance past tick {h.ClientTick} after the freeze committed.");
            }

            // The client crossed the drop and ran 100 ticks beyond it purely on the injected stream.
            Assert.True(h.ClientTick >= DropTick + 100);

            // And every post-bootstrap tick it consumed was a REAL server-built merged packet — never a local
            // pre-seed (a seeded tick has length 0; ticks 0..Delay-1 are the only allowed zeros).
            foreach ((uint tick, int len) in h.Consumed)
            {
                if (tick < Delay)
                    Assert.Equal(0, len);                       // the match-start bootstrap gap
                else
                    Assert.True(len >= MergedTickPacket.HEADER_BYTES,
                        $"Tick {tick} was consumed with length {len} — a pre-seeded empty, not a server merged packet. " +
                        "The drop path must fill the Flush gate from the injected stream alone.");
            }
        }

        [Fact]
        public void WithoutInjection_TheStallIsPermanent_TheControlArm()
        {
            // The control that gives the unstall test its teeth: commit or no commit, if nothing injects the dead
            // slot's empties the gate NEVER fills — exactly the pre-9.6 permanent stall. (This also proves the
            // unstall above came from the injector, not from some accidental self-seed in the harness.)
            var h = new Harness();
            int guard = 0;
            while (h.ClientTick < DropTick)
            {
                Assert.True(h.Frame(remoteAlive: h.ClientTick + Delay < DropTick + Delay));
                Assert.True(++guard < 10_000, "runaway loop");
            }

            for (int frame = 0; frame < 300; frame++)
                h.Frame(remoteAlive: false); // no commit, no injection, no seed

            Assert.True(h.ClientTick >= DropTick);
            Assert.True(h.ClientTick <= DropTick + Delay,
                $"Client advanced to {h.ClientTick} with the peer dead and no injection — something filled the gate.");
        }
    }
}
