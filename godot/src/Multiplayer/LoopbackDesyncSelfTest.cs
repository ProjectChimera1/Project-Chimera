#if DEBUG
#nullable enable
using Godot;
using ProjectChimera.Core;  // Faction, UnitOrder
using ProjectChimera.UI;    // GodotLogSink — route the server's determinism verdict to the headless console

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 1.9a + 1.9b + 9.6 + 9.7 — in-process loopback SELF-TEST (DEBUG-only, headless-runnable). Stands up the
    /// REAL <see cref="DedicatedServer"/> (ServerHost + quorum collector + Story-9.6 freeze machinery) and
    /// <see cref="PlayerCount"/> real <see cref="ENetTransport"/> clients over loopback ENet in ONE process,
    /// completes the N-player handshake (join → ready → start), then:
    ///   • (1.9b) every peer sends checksums AND per-tick command packets so the server tallies
    ///     ≥<see cref="CleanWindowTarget"/> clean comparison windows (PASS) while the N-way merged fan-in flows; then
    ///   • (9.6) DISCONNECTS one peer and asserts freeze-and-continue: the survivors keep receiving merged ticks
    ///     (the server injects an empty command for the dropped slot each tick) and <c>Host.WindowsCompared</c>
    ///     keeps incrementing over the REDUCED quorum — no silent stall, no false HALT.
    /// Story 9.7 raised this from 2 to <see cref="PlayerCount"/>=3 in-process peers (N≥3), exercising the REAL
    /// N-player transport/lockstep merge over the raised count.
    ///
    /// DW-447: every checksum a peer sends is now its OWN sim's REAL computed hash — each peer steps an
    /// INDEPENDENT <see cref="LoopbackPeerSim"/> (its own <c>SimulationHost</c> + stores, deterministic scripted
    /// orders) and reports that sim's per-tick <c>SimChecksum</c>, replacing the former hardcoded <c>GOOD</c>
    /// constant (under which the quorum was unanimous BY CONSTRUCTION and a real desync was structurally
    /// invisible). The clean phase's <c>Passing</c> assertion is therefore now a genuine 4-independent-sims
    /// zero-desync check over the real transport; a divergence between the peer sims FAILS the self-test.
    /// Exit 0 ONLY if BOTH the clean-PASS and the drop-and-continue phases pass. Prints "RESULT: PASS/FAIL …" and quits.
    /// Run: <c>godot --headless -- --loopback-test</c>.
    /// </summary>
    public partial class LoopbackDesyncSelfTest : Node
    {
        private const int  PORT = 49777;
        /// <summary>Story 9.7 (P10): the N=4 ship ceiling in-process peers — exercises join→ready→start→merged
        /// agreement + drop-and-continue over the REAL transport/state machine at the full seat count (not just the
        /// in-process golden merge math).</summary>
        private const int  PlayerCount = 4;
        /// <summary>Story 9.4: a shared non-zero match-agreement hash every loopback peer sends in its Ready so the
        /// fail-closed start-state-agreement gate accepts the smoke (agreeing + non-zero + versions match).</summary>
        private const ulong AGREE = 0xC0FFEE_C0FFEEUL;
        private const int  CleanWindowTarget = 5;   // Story 1.9b: prove ≥5 clean comparison windows before dropping a peer
        private const int  ContinueWindowGoal = 3;  // Story 9.6: windows the survivors must complete AFTER the drop
        private const int  ContinueMergedGoal = 3;  // Story 9.6: merged ticks a survivor must keep receiving AFTER the drop

        private sealed class Peer
        {
            public int Id;
            public ENetTransport T = null!;
            public bool Started;
            public Faction Faction = Faction.Neutral;
            public int MergedCount;   // TickCommandsMerged packets received
            public bool DropAcked;    // this (surviving) peer received a DropDirective and ACKed it
            // DW-447: this peer's OWN independent simulation — the source of the real checksums it reports
            // (fresh SimulationHost + stores per peer; nothing shared between the four).
            public LoopbackPeerSim Sim = null!;
        }

        private enum Phase { Connecting, Agreeing, DropContinue, Done }

        private DedicatedServer _server = null!;
        private readonly Peer[] _peers = new Peer[PlayerCount];
        private readonly byte[] _ckBuf = new byte[16];
        private readonly byte[] _tcBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
        private static readonly UnitOrder[] EmptyOrders = System.Array.Empty<UnitOrder>();

        private Phase  _phase = Phase.Connecting;
        private double _elapsed, _phaseStart, _lastSend;
        private int    _cleanWindows;    // server-reported clean windows at the moment we drop a peer
        private int    _windowsAtDrop;   // Host.WindowsCompared captured at the drop
        private int    _mergedAtDrop;    // survivor's MergedCount captured at the drop
        private int    _droppedIndex = -1;

        public override void _Ready()
        {
            // Story 9.7: tell the server how many players THIS match expects (matches the peer count) — with the
            // raised MAX_PLAYERS ceiling the server no longer defaults to the match size.
            _server = new DedicatedServer { Log = new GodotLogSink(), ExpectedPlayerCount = PlayerCount };
            AddChild(_server);
            _server.Start(PORT);
            for (int i = 0; i < PlayerCount; i++)
            {
                _peers[i] = new Peer { Id = i, Sim = new LoopbackPeerSim() }; // DW-447: one independent sim per peer
                SetupPeer(_peers[i]);
            }
            GD.Print($"[LoopbackTest] server + {PlayerCount} clients connecting on 127.0.0.1:{PORT} …");
        }

        private void SetupPeer(Peer p)
        {
            p.T = new ENetTransport();
            p.T.OnPacketReceived += (data, len, _) => OnPeerPacket(p, data, len);
            var err = p.T.JoinGame("127.0.0.1", PORT);
            if (err != Error.Ok) GD.PrintErr($"[LoopbackTest] client {p.Id} JoinGame failed: {err}");
        }

        private void OnPeerPacket(Peer p, byte[] data, int len)
        {
            if (len < 1) return;
            switch ((PacketType)data[0])
            {
                case PacketType.Hello:
                    if (TickCommandPacket.TryReadHello(data, len, out Faction f)) p.Faction = f;
                    p.T.SendReliable(TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, AGREE));
                    break;
                case PacketType.StartGame:
                    p.Started = true;
                    break;
                case PacketType.TickCommandsMerged:
                    p.MergedCount++;
                    break;
                case PacketType.DropDirective:
                    // Story 9.6: a survivor ACKs the freeze directive so the server commits and begins injecting.
                    if (TickCommandPacket.TryReadDropDirective(data, len, out byte df, out uint dApplyAt))
                    {
                        p.T.SendReliable(TickCommandPacket.MakeDropAck(df, dApplyAt));
                        p.DropAcked = true;
                    }
                    break;
            }
        }

        private bool AllStarted()
        {
            for (int i = 0; i < PlayerCount; i++) if (!_peers[i].Started) return false;
            return true;
        }

        public override void _Process(double delta)
        {
            if (_phase == Phase.Done) return;
            for (int i = 0; i < PlayerCount; i++) _peers[i].T.Poll();
            _elapsed += delta;

            switch (_phase)
            {
                case Phase.Connecting:
                    if (AllStarted()) { _phase = Phase.Agreeing; _phaseStart = _elapsed; }
                    else if (_elapsed > 12.0) Finish(false, "handshake never completed (not all clients started)");
                    break;

                case Phase.Agreeing:
                    if (_elapsed - _lastSend >= 0.05)
                    {
                        _lastSend = _elapsed;
                        // Every peer submits its per-tick command packet (drives the N-way merged fan-in) AND a
                        // checksum. DW-447: each peer advances its OWN independent sim one tick and reports that
                        // sim's REAL computed hash — the four sims stay in lockstep (same tick) only by actually
                        // being deterministic, so the server's clean-window PASS is now a genuine zero-desync proof.
                        for (int i = 0; i < PlayerCount; i++)
                        {
                            (uint tick, uint hash) = _peers[i].Sim.Step();
                            SendTick(_peers[i], tick);
                            SendChecksum(_peers[i], tick, hash);
                        }
                    }
                    int windows = _server.Host?.WindowsCompared ?? 0;
                    if (windows >= CleanWindowTarget)
                    {
                        if (_server.Host is not { Passing: true, Halted: false })
                        {
                            Finish(false, $"clean phase: expected PASS, got Passing={_server.Host?.Passing} Halted={_server.Host?.Halted} after {windows} windows");
                            break;
                        }
                        _cleanWindows  = windows;
                        _windowsAtDrop = windows;
                        _droppedIndex  = PlayerCount - 1;              // drop the last peer; the rest are survivors
                        _mergedAtDrop  = _peers[0].MergedCount;         // survivor 0 tracks continuation
                        GD.Print($"[LoopbackTest] clean phase OK — {windows} windows compared, 0 desync (PASS). " +
                                 $"Now DROPPING peer {_droppedIndex} (of {PlayerCount}) …");
                        _peers[_droppedIndex].T.Disconnect(); // the mid-match disconnect — server must freeze-and-continue
                        _phase = Phase.DropContinue; _phaseStart = _elapsed;
                    }
                    else if (_elapsed - _phaseStart > 8.0)
                    {
                        Finish(false, $"clean phase: only {windows}/{CleanWindowTarget} windows compared after 8s");
                    }
                    break;

                case Phase.DropContinue:
                    if (_elapsed - _lastSend >= 0.05)
                    {
                        _lastSend = _elapsed;
                        // Only the SURVIVORS keep playing — their ticks fan in, the server injects the frozen slot's
                        // empty command so the merged tick completes, and their checksums complete at reduced quorum.
                        // DW-447: the survivors keep stepping their OWN sims and reporting real hashes; the dropped
                        // peer's sim simply stops advancing (it reports nothing, exactly like a real disconnect).
                        for (int i = 0; i < PlayerCount; i++)
                        {
                            if (i == _droppedIndex) continue;
                            (uint tick, uint hash) = _peers[i].Sim.Step();
                            SendTick(_peers[i], tick);
                            SendChecksum(_peers[i], tick, hash);
                        }
                    }

                    int windowsNow = _server.Host?.WindowsCompared ?? 0;
                    int mergedNow  = _peers[0].MergedCount;
                    bool haltedFalsely = _server.Host?.Halted ?? false;

                    if (haltedFalsely)
                    {
                        Finish(false, "server HALTed after a single-peer disconnect — freeze-and-continue must not fail-closed");
                    }
                    else if (_peers[0].DropAcked &&
                             windowsNow >= _windowsAtDrop + ContinueWindowGoal &&
                             mergedNow  >= _mergedAtDrop + ContinueMergedGoal)
                    {
                        Finish(true, $"clean PASS ({_cleanWindows} windows, N={PlayerCount}) + survivors continued after drop " +
                                     $"({mergedNow - _mergedAtDrop} more merged ticks, " +
                                     $"{windowsNow - _windowsAtDrop} more windows over the reduced quorum)");
                    }
                    else if (_elapsed - _phaseStart > 8.0)
                    {
                        Finish(false, $"drop-and-continue: survivors did not advance enough (DropAcked={_peers[0].DropAcked}, " +
                                      $"+{mergedNow - _mergedAtDrop} merged, +{windowsNow - _windowsAtDrop} windows in 8s)");
                    }
                    break;
            }
        }

        private void SendChecksum(Peer p, uint tick, uint hash)
        {
            int n = TickCommandPacket.WriteChecksum(_ckBuf, tick, hash);
            p.T.SendReliable(_ckBuf[..n]);
        }

        private void SendTick(Peer p, uint tick)
        {
            int n = TickCommandPacket.Write(_tcBuf, tick, p.Faction, EmptyOrders, 0);
            p.T.SendCommands(_tcBuf, n);
        }

        private void Finish(bool pass, string detail)
        {
            _phase = Phase.Done;
            GD.Print($"[LoopbackTest] RESULT: {(pass ? "PASS" : "FAIL")} — {detail}");
            for (int i = 0; i < PlayerCount; i++) _peers[i].T.Disconnect();
            GetTree().Quit(pass ? 0 : 1);
        }
    }
}
#endif
