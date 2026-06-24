#if DEBUG
#nullable enable
using Godot;
using ProjectChimera.UI;   // GodotLogSink — route the server's determinism verdict to the headless console

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 1.9a + 1.9b — in-process loopback desync SELF-TEST (DEBUG-only, headless-runnable). Stands up the
    /// REAL <see cref="DedicatedServer"/> (ServerHost + quorum collector) and TWO real <see cref="ENetTransport"/>
    /// clients over loopback ENet in ONE process, completes the handshake, then (1.9b) sends matching checksums and
    /// asserts the server tallies ≥<see cref="CleanWindowTarget"/> clean comparison windows and reports PASS
    /// (<c>Host.Passing</c>) BEFORE (1.9a) inducing a one-peer divergence and asserting BOTH clients receive the
    /// server-broadcast HALT. Exit 0 ONLY if BOTH the clean-PASS and the HALT phases pass — the regression guard
    /// for the full network → verdict → HALT path. Prints "RESULT: PASS/FAIL …" and quits.
    /// Run: <c>godot --headless -- --loopback-test</c>.
    /// </summary>
    public partial class LoopbackDesyncSelfTest : Node
    {
        private const int  PORT = 49777;
        private const uint GOOD = 0xA11AA11Au;
        private const uint BAD  = 0xDEADBEEFu;
        private const int  CleanWindowTarget = 5;   // Story 1.9b: prove ≥5 clean comparison windows (≥300 ticks-equiv) before diverging

        private sealed class Peer
        {
            public int Id;
            public ENetTransport T = null!;
            public bool Started;
            public bool Halted;
        }

        private enum Phase { Connecting, Agreeing, AwaitingHalt, Done }

        private DedicatedServer _server = null!;
        private readonly Peer _p0 = new() { Id = 0 };
        private readonly Peer _p1 = new() { Id = 1 };
        private readonly byte[] _ckBuf = new byte[16];

        private Phase  _phase = Phase.Connecting;
        private double _elapsed, _phaseStart, _lastSend;
        private uint   _tick;
        private int    _cleanWindows;   // Story 1.9b: server-reported clean windows at the moment we diverge

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
                case PacketType.Hello:       p.T.SendReliable(TickCommandPacket.MakeReady(0)); break;
                case PacketType.StartGame:   p.Started = true; break;
                case PacketType.Halt:
                case PacketType.DesyncAlert: p.Halted = true; break;
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
                        SendChecksum(_p0, _tick, GOOD);
                        SendChecksum(_p1, _tick, GOOD);
                    }
                    // Story 1.9b: wait until the server has TALLIED ≥CleanWindowTarget clean windows (both peers
                    // agreeing every tick), then assert the PASS state BEFORE inducing divergence. This proves the
                    // clean-lockstep PASS path over real ENet sockets — not just the HALT path 1.9a already proved.
                    int windows = _server.Host?.WindowsCompared ?? 0;
                    if (windows >= CleanWindowTarget)
                    {
                        if (_server.Host is not { Passing: true, Halted: false })
                        {
                            Finish(false, $"clean phase: expected PASS, got Passing={_server.Host?.Passing} Halted={_server.Host?.Halted} after {windows} windows");
                            break;
                        }
                        _cleanWindows = windows;
                        GD.Print($"[LoopbackTest] clean phase OK — server reports {windows} windows compared, 0 desync (PASS). Now inducing divergence …");
                        _tick++;
                        SendChecksum(_p0, _tick, BAD);   // one-peer divergence → no majority (N=2) → HALT
                        SendChecksum(_p1, _tick, GOOD);
                        GD.Print($"[LoopbackTest] divergence injected at tick {_tick} (p0={BAD:X8}, p1={GOOD:X8}).");
                        _phase = Phase.AwaitingHalt; _phaseStart = _elapsed;
                    }
                    else if (_elapsed - _phaseStart > 8.0)
                    {
                        Finish(false, $"clean phase: only {windows}/{CleanWindowTarget} windows compared after 8s");
                    }
                    break;

                case Phase.AwaitingHalt:
                    if (_p0.Halted && _p1.Halted)
                        Finish(true, $"clean PASS ({_cleanWindows} windows, 0 desync) + both clients HALTed after divergence");
                    else if (_elapsed - _phaseStart > 4.0)
                        Finish(false, $"HALT not received within 4s (p0.halted={_p0.Halted}, p1.halted={_p1.Halted})");
                    break;
            }
        }

        private void SendChecksum(Peer p, uint tick, uint hash)
        {
            int n = TickCommandPacket.WriteChecksum(_ckBuf, tick, hash);
            p.T.SendReliable(_ckBuf[..n]);
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
