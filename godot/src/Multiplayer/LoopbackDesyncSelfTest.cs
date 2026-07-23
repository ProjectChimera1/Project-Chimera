#if DEBUG
#nullable enable
using Godot;
using ProjectChimera.Core;  // Faction, UnitOrder
using ProjectChimera.UI;    // GodotLogSink — route the server's determinism verdict to the headless console

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 1.9a + 1.9b + 9.6 — in-process loopback SELF-TEST (DEBUG-only, headless-runnable). Stands up the REAL
    /// <see cref="DedicatedServer"/> (ServerHost + quorum collector + Story-9.6 freeze machinery) and TWO real
    /// <see cref="ENetTransport"/> clients over loopback ENet in ONE process, completes the handshake, then:
    ///   • (1.9b) both peers send matching checksums AND per-tick command packets so the server tallies
    ///     ≥<see cref="CleanWindowTarget"/> clean comparison windows (PASS) while the merged fan-in flows; then
    ///   • (9.6) DISCONNECTS one peer and asserts freeze-and-continue: the survivor keeps receiving merged ticks
    ///     (the server injects an empty command for the dropped slot each tick) and <c>Host.WindowsCompared</c>
    ///     keeps incrementing over the REDUCED quorum — no silent stall, no false HALT.
    /// Exit 0 ONLY if BOTH the clean-PASS and the drop-and-continue phases pass. Prints "RESULT: PASS/FAIL …" and quits.
    /// Run: <c>godot --headless -- --loopback-test</c>.
    /// </summary>
    public partial class LoopbackDesyncSelfTest : Node
    {
        private const int  PORT = 49777;
        private const uint GOOD = 0xA11AA11Au;
        /// <summary>Story 9.4: a shared non-zero match-agreement hash both loopback peers send in their Ready so the
        /// fail-closed start-state-agreement gate accepts the smoke itself (agreeing + non-zero + versions match).</summary>
        private const ulong AGREE = 0xC0FFEE_C0FFEEUL;
        private const int  CleanWindowTarget = 5;   // Story 1.9b: prove ≥5 clean comparison windows before dropping a peer
        private const int  ContinueWindowGoal = 3;  // Story 9.6: windows the lone survivor must complete AFTER the drop
        private const int  ContinueMergedGoal = 3;  // Story 9.6: merged ticks the survivor must keep receiving AFTER the drop

        private sealed class Peer
        {
            public int Id;
            public ENetTransport T = null!;
            public bool Started;
            public Faction Faction = Faction.Neutral;
            public int MergedCount;   // TickCommandsMerged packets received
            public bool DropAcked;    // this (surviving) peer received a DropDirective and ACKed it
        }

        private enum Phase { Connecting, Agreeing, DropContinue, Done }

        private DedicatedServer _server = null!;
        private readonly Peer _p0 = new() { Id = 0 };
        private readonly Peer _p1 = new() { Id = 1 };
        private readonly byte[] _ckBuf = new byte[16];
        private readonly byte[] _tcBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
        private static readonly UnitOrder[] EmptyOrders = System.Array.Empty<UnitOrder>();

        private Phase  _phase = Phase.Connecting;
        private double _elapsed, _phaseStart, _lastSend;
        private uint   _tick;
        private int    _cleanWindows;    // server-reported clean windows at the moment we drop a peer
        private int    _windowsAtDrop;   // Host.WindowsCompared captured at the drop
        private int    _mergedAtDrop;    // survivor's MergedCount captured at the drop

        public override void _Ready()
        {
            _server = new DedicatedServer { Log = new GodotLogSink() };  // 1.9b: print the determinism verdict to the console
            AddChild(_server);
            _server.Start(PORT);
            SetupPeer(_p0);
            SetupPeer(_p1);
            GD.Print($"[LoopbackTest] server + 2 clients connecting on 127.0.0.1:{PORT} …");
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
                    // Story 9.6: the survivor ACKs the freeze directive so the server commits and begins injecting.
                    if (TickCommandPacket.TryReadDropDirective(data, len, out byte df, out uint dApplyAt))
                    {
                        p.T.SendReliable(TickCommandPacket.MakeDropAck(df, dApplyAt));
                        p.DropAcked = true;
                    }
                    break;
            }
        }

        public override void _Process(double delta)
        {
            if (_phase == Phase.Done) return;
            _p0.T.Poll();
            _p1.T.Poll();
            _elapsed += delta;

            switch (_phase)
            {
                case Phase.Connecting:
                    if (_p0.Started && _p1.Started) { _phase = Phase.Agreeing; _phaseStart = _elapsed; }
                    else if (_elapsed > 12.0) Finish(false, "handshake never completed (clients did not both start)");
                    break;

                case Phase.Agreeing:
                    if (_elapsed - _lastSend >= 0.05)
                    {
                        _lastSend = _elapsed;
                        _tick++;
                        // Both peers submit their per-tick command packet (drives the merged fan-in) AND a checksum.
                        SendTick(_p0, _tick); SendTick(_p1, _tick);
                        SendChecksum(_p0, _tick, GOOD); SendChecksum(_p1, _tick, GOOD);
                    }
                    // Story 1.9b: wait until the server has TALLIED ≥CleanWindowTarget clean windows, assert PASS,
                    // then (9.6) DROP one peer instead of inducing a divergence.
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
                        _mergedAtDrop  = _p0.MergedCount;
                        GD.Print($"[LoopbackTest] clean phase OK — {windows} windows compared, 0 desync (PASS), " +
                                 $"survivor received {_mergedAtDrop} merged ticks. Now DROPPING peer 1 …");
                        _p1.T.Disconnect(); // the mid-match disconnect — server must freeze-and-continue, NOT end
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
                        _tick++;
                        // ONLY the survivor keeps playing — its tick fans in, the server injects the frozen slot's
                        // empty command so the merged tick completes, and its checksum completes at the reduced quorum.
                        SendTick(_p0, _tick);
                        SendChecksum(_p0, _tick, GOOD);
                    }

                    int windowsNow = _server.Host?.WindowsCompared ?? 0;
                    int mergedNow  = _p0.MergedCount;
                    bool haltedFalsely = _server.Host?.Halted ?? false;

                    if (haltedFalsely)
                    {
                        Finish(false, "server HALTed after a single-peer disconnect — freeze-and-continue must not fail-closed");
                    }
                    else if (_p0.DropAcked &&
                             windowsNow >= _windowsAtDrop + ContinueWindowGoal &&
                             mergedNow  >= _mergedAtDrop + ContinueMergedGoal)
                    {
                        Finish(true, $"clean PASS ({_cleanWindows} windows) + survivor continued after drop " +
                                     $"({mergedNow - _mergedAtDrop} more merged ticks, " +
                                     $"{windowsNow - _windowsAtDrop} more windows over the reduced quorum)");
                    }
                    else if (_elapsed - _phaseStart > 8.0)
                    {
                        Finish(false, $"drop-and-continue: survivor did not advance enough (DropAcked={_p0.DropAcked}, " +
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
            _p0.T.Disconnect();
            _p1.T.Disconnect();
            GetTree().Quit(pass ? 0 : 1);
        }
    }
}
#endif
