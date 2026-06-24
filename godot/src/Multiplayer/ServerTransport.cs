#nullable enable
using System;
using Godot;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Low-level ENet transport for the dedicated server role.
    /// Accepts up to <see cref="MAX_SLOTS"/> peers, assigns them numbered slots (0 = P1, 1 = P2),
    /// and provides per-slot send / broadcast helpers.
    ///
    /// The server never connects outward — it only accepts inbound connections.
    ///
    /// Channels (same as <see cref="ENetTransport"/>):
    ///   0 — Reliable  (lobby, checksum, alerts)
    ///   1 — Reliable  (tick command packets)
    /// </summary>
    public sealed class ServerTransport : IDisposable
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        /// <summary>Slots 0–1 are the two players. Slots 2–3 are spectators.</summary>
        public const int MAX_SLOTS      = 4;
        public const int MAX_PLAYERS    = 2;
        public const int CHANNEL_COUNT  = 2;
        public const int CH_RELIABLE    = 0;
        public const int CH_COMMANDS    = 1;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires when a peer connects and gets assigned a slot. Args: (slotIndex 0..MAX_SLOTS-1).</summary>
        public event Action<int>? OnSlotConnected;
        /// <summary>Fires when a peer disconnects. Args: (slotIndex).</summary>
        public event Action<int>? OnSlotDisconnected;
        /// <summary>Fires for each arriving packet. Args: (slotIndex, data, length, channel).</summary>
        public event Action<int, byte[], int, int>? OnPacketReceived;

        // ── State ─────────────────────────────────────────────────────────────────

        /// <summary>Number of currently connected peers.</summary>
        public int ConnectedCount { get; private set; }
        public bool IsListening   { get; private set; }

        private ENetConnection? _host;
        private readonly ENetPacketPeer?[] _slots = new ENetPacketPeer?[MAX_SLOTS];

        // ── Init ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Open a server socket and begin accepting connections.
        /// </summary>
        public Error Listen(int port)
        {
            Cleanup();
            _host = new ENetConnection();
            var err = _host.CreateHostBound("*", port, maxPeers: MAX_SLOTS, maxChannels: CHANNEL_COUNT);
            if (err != Error.Ok) { _host = null; return err; }
            IsListening = true;
            GD.Print($"[Server] Listening on port {port} (max {MAX_SLOTS} peers).");
            return Error.Ok;
        }

        // ── Per-frame pump ────────────────────────────────────────────────────────

        /// <summary>
        /// Drain all ENet events. Call once per _Process frame.
        /// </summary>
        public void Poll()
        {
            if (_host == null) return;

            while (true)
            {
                var result    = _host.Service(0);
                var eventType = (ENetConnection.EventType)(int)result[0];
                if (eventType == ENetConnection.EventType.None) break;

                var peer    = result[1].As<ENetPacketPeer>();
                var data    = result[2].AsByteArray();
                var channel = (int)result[3];

                switch (eventType)
                {
                    case ENetConnection.EventType.Connect:
                    {
                        int slot = FindFreeSlot();
                        if (slot < 0)
                        {
                            GD.PrintErr("[Server] No free slot — rejecting peer.");
                            peer.PeerDisconnect();
                            break;
                        }
                        _slots[slot] = peer;
                        ConnectedCount++;
                        GD.Print($"[Server] Peer connected → slot {slot}.");
                        OnSlotConnected?.Invoke(slot);
                        break;
                    }

                    case ENetConnection.EventType.Disconnect:
                    {
                        int slot = FindSlot(peer);
                        if (slot >= 0)
                        {
                            _slots[slot] = null;
                            ConnectedCount--;
                            GD.Print($"[Server] Slot {slot} disconnected.");
                            OnSlotDisconnected?.Invoke(slot);
                        }
                        break;
                    }

                    case ENetConnection.EventType.Receive:
                    {
                        int slot = FindSlot(peer);
#if DEBUG
                        // Story 1.9a loopback diagnostic: did a client packet actually reach the server, and did
                        // FindSlot resolve its slot? (slot=-1 ⇒ peer-wrapper mismatch dropped it.)
                        GD.Print($"[ServerTransport] RX slot={slot} len={(data?.Length ?? -1)} type=0x{((data!=null && data.Length>0)?data[0]:0):X2} ch={channel}");
#endif
                        if (slot >= 0 && data != null && data.Length > 0)
                            OnPacketReceived?.Invoke(slot, data, data.Length, channel);
                        break;
                    }
                }
            }
        }

        // ── Send helpers ──────────────────────────────────────────────────────────

        /// <summary>Send a reliable packet to a specific slot.</summary>
        public Error SendReliableTo(int slot, byte[] data, int length = -1)
            => SendReliableTo(slot, data, length < 0 ? data.Length : length, CH_RELIABLE);

        /// <summary>Send a command packet to a specific slot (reliable delivery).</summary>
        public Error SendCommandsTo(int slot, byte[] data, int length)
            => SendReliableTo(slot, data, length, CH_COMMANDS);

        /// <summary>Send a reliable packet to all connected peers.</summary>
        public void BroadcastReliable(byte[] data, int length = -1)
        {
            int len = length < 0 ? data.Length : length;
            for (int s = 0; s < MAX_SLOTS; s++)
                if (_slots[s] != null) SendReliableTo(s, data, len, CH_RELIABLE);
        }

        /// <summary>Send a command packet (tick commands) to all spectator slots (2+).</summary>
        public void BroadcastCommandsToSpectators(byte[] data, int length)
        {
            for (int s = MAX_PLAYERS; s < MAX_SLOTS; s++)
                if (_slots[s] != null) SendReliableTo(s, data, length, CH_COMMANDS);
        }

        /// <summary>Returns true if the given slot has a connected peer.</summary>
        public bool IsSlotConnected(int slot)
            => (uint)slot < MAX_SLOTS && _slots[slot] != null;

        // All server sends use reliable delivery — no need for a flags parameter.
        private Error SendReliableTo(int slot, byte[] data, int length, int channel)
        {
            if ((uint)slot >= MAX_SLOTS || _slots[slot] == null) return Error.Unconfigured;
            byte[] payload = length == data.Length ? data : data[..length];
            return _slots[slot]!.Send(channel, payload, (int)ENetPacketPeer.FlagReliable);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private int FindFreeSlot()
        {
            for (int i = 0; i < MAX_SLOTS; i++)
                if (_slots[i] == null) return i;
            return -1;
        }

        private int FindSlot(ENetPacketPeer peer)
        {
            for (int i = 0; i < MAX_SLOTS; i++)
                if (_slots[i] == peer) return i;
            return -1;
        }

        // ── Cleanup ───────────────────────────────────────────────────────────────

        private void Cleanup()
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                _slots[i]?.PeerDisconnect();
                _slots[i] = null;
            }
            ConnectedCount = 0;
            _host?.Destroy();
            _host = null;
            IsListening = false;
        }

        public void Dispose() => Cleanup();
    }
}
