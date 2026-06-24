#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Low-level ENet transport wrapper for deterministic 1v1 lockstep networking.
    ///
    /// Usage:
    ///   Host: call HostGame(port) → wait for OnPeerConnected → call SendReady()
    ///   Client: call JoinGame(ip, port) → wait for OnPeerConnected → call SendReady()
    ///
    ///   Each frame: call Poll() to process incoming events.
    ///   OnPacketReceived fires for each arriving packet.
    ///
    /// Channels:
    ///   0 — Reliable (lobby handshake, checksum, desync alerts)
    ///   1 — Unreliable-sequenced (tick command packets — tolerate one lost tick, not out-of-order)
    ///
    /// This class does NOT contain lockstep logic — it is a pure send/receive pipe.
    /// </summary>
    public class ENetTransport : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires when the remote peer connects (host: client arrived; client: connected to host).</summary>
        public event Action? OnPeerConnected;
        /// <summary>Fires when the remote peer disconnects.</summary>
        public event Action? OnPeerDisconnected;
        /// <summary>Fires for each packet received. Args: (data, length, channel).</summary>
        public event Action<byte[], int, int>? OnPacketReceived;

        // ── State ─────────────────────────────────────────────────────────────────

        public bool IsHost        { get; private set; }
        public bool IsConnected   { get; private set; }
        public bool IsInitialised { get; private set; }

        private const int CHANNEL_RELIABLE   = 0;
        private const int CHANNEL_COMMANDS   = 1;
        private const int CHANNEL_COUNT      = 2;
        private const int MAX_PACKET_SIZE    = 2048;

        private ENetConnection?   _host;
        private ENetPacketPeer?   _peer;

        // ── Init ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Open a server socket on <paramref name="port"/>.
        /// Waits for exactly one inbound connection before OnPeerConnected fires.
        /// </summary>
        public Error HostGame(int port)
        {
            Cleanup();
            _host = new ENetConnection();
            var err = _host.CreateHostBound("*", port, maxPeers: 1, maxChannels: CHANNEL_COUNT);
            if (err != Error.Ok) { _host = null; return err; }
            IsHost = true;
            IsInitialised = true;
            GD.Print($"[ENet] Hosting on port {port}");
            return Error.Ok;
        }

        /// <summary>
        /// Connect to a host at <paramref name="ip"/>:<paramref name="port"/>.
        /// OnPeerConnected fires once the connection is established.
        /// </summary>
        public Error JoinGame(string ip, int port)
        {
            Cleanup();
            _host = new ENetConnection();
            var err = _host.CreateHost(maxPeers: 1, maxChannels: CHANNEL_COUNT);
            if (err != Error.Ok) { _host = null; return err; }

            _peer = _host.ConnectToHost(ip, port, CHANNEL_COUNT);
            if (_peer == null)
            {
                _host.Destroy();
                _host = null;
                return Error.CantConnect;
            }

            IsHost = false;
            IsInitialised = true;
            GD.Print($"[ENet] Connecting to {ip}:{port}");
            return Error.Ok;
        }

        // ── Frame pump ────────────────────────────────────────────────────────────

        /// <summary>
        /// Process all pending ENet events. Call once per _Process frame.
        /// Fires OnPeerConnected / OnPeerDisconnected / OnPacketReceived as appropriate.
        /// </summary>
        public void Poll()
        {
            if (_host == null) return;

            // Drain all queued events this frame
            while (true)
            {
                var result = _host.Service(0); // non-blocking
                var eventType = (ENetConnection.EventType)(int)result[0];

                if (eventType == ENetConnection.EventType.None) break;

                var peer    = result[1].As<ENetPacketPeer>();
                var data    = result[2].AsByteArray();
                var channel = (int)result[3];

                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                        _peer = peer;
                        IsConnected = true;
                        GD.Print("[ENet] Peer connected.");
                        OnPeerConnected?.Invoke();
                        break;

                    case ENetConnection.EventType.Disconnect:
                        IsConnected = false;
                        _peer = null;
                        GD.Print("[ENet] Peer disconnected.");
                        OnPeerDisconnected?.Invoke();
                        break;

                    case ENetConnection.EventType.Receive:
                        if (data != null && data.Length > 0)
                            OnPacketReceived?.Invoke(data, data.Length, channel);
                        break;
                }
            }
        }

        // ── Send ──────────────────────────────────────────────────────────────────

        /// <summary>Send a reliable ordered packet (lobby messages, checksums).</summary>
        public Error SendReliable(byte[] data)
            => SendRaw(data, data.Length, CHANNEL_RELIABLE, ENetPacketPeer.FlagReliable);

        /// <summary>
        /// Send a tick command packet.
        /// Uses reliable delivery — commands must not be lost for deterministic lockstep.
        /// (Unreliable-sequenced would require a recovery/resend protocol; reliable keeps it simple.)
        /// </summary>
        public Error SendCommands(byte[] data, int length)
            => SendRaw(data, length, CHANNEL_COMMANDS, ENetPacketPeer.FlagReliable);

        // ENetPacketPeer.Send takes (int channel, byte[] packet, int flags).
        // FlagReliable is exposed as a long constant — cast to int when calling Send.
        private Error SendRaw(byte[] data, int length, int channel, long flags)
        {
            if (_peer == null || !IsConnected) { GD.PrintErr($"[ENet] Send DROPPED (peer={( _peer==null?"null":"ok")}, connected={IsConnected})"); return Error.Unconfigured; }
            if (length <= 0 || length > data.Length) return Error.InvalidParameter;
            byte[] payload = length == data.Length ? data : data[..length];
            var e = _peer.Send(channel, payload, (int)flags);
#if DEBUG
            // Story 1.9a loopback diagnostic: surface a failing client->server send (the lobby-stuck smoking gun).
            GD.Print($"[ENet] Send ch{channel} len{length} type=0x{(length>0?data[0]:0):X2} -> {e} (peerState={_peer.GetState()})");
#endif
            return e;
        }

        // ── Cleanup ───────────────────────────────────────────────────────────────

        public void Disconnect()
        {
            _peer?.PeerDisconnect();
            _peer = null;
            IsConnected = false;
        }

        private void Cleanup()
        {
            Disconnect();
            _host?.Destroy();
            _host = null;
            IsInitialised = false;
            IsHost = false;
        }

        public void Dispose() => Cleanup();
    }
}
