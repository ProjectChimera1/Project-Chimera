#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Nakama matchmaking integration for Project Chimera 1v1.
    ///
    /// Architecture:
    ///   - A dedicated Godot server (DedicatedServer.cs) runs on a known VPS address.
    ///   - Nakama runs on the same VPS as a matchmaking broker — it does NOT relay game data.
    ///   - Flow: Auth → FindMatch → Nakama groups 2 players → OnMatchFound fires →
    ///     caller calls ENetTransport.JoinGame(info.ServerIp, info.ServerPort).
    ///
    /// Threading:
    ///   Nakama SDK fires events on a background thread.
    ///   This class enqueues all events into PendingEvents (a ConcurrentQueue).
    ///   The caller (LobbyUi._Process) must drain PendingEvents each frame on the main thread.
    ///
    /// Usage:
    ///   await service.ConnectAsync(email, password);
    ///   await service.FindMatchAsync();
    ///   // In _Process: service.DrainEvents();
    ///   // OnMatchFound fires → connect ENet
    ///   await service.DisconnectAsync();
    /// </summary>
    public class NakamaService
    {
        // ── Server config (set before ConnectAsync) ───────────────────────────────

        /// <summary>Nakama HTTP host (typically same VPS as dedicated game server).</summary>
        public string NakamaHost    { get; set; } = "localhost";
        public int    NakamaPort    { get; set; } = 7350;
        public string NakamaKey     { get; set; } = "defaultkey";

        /// <summary>Dedicated ENet game server address sent to matched players.</summary>
        public string GameServerIp   { get; set; } = "localhost";
        public int    GameServerPort { get; set; } = 7777;

        // ── Events (fired via DrainEvents on the main thread) ─────────────────────

        /// <summary>Fires when Nakama groups 2 players. Connect ENet immediately after.</summary>
        public event Action<MatchFoundInfo>? OnMatchFound;

        /// <summary>Human-readable status text for lobby UI.</summary>
        public event Action<string>? OnStatusText;

        /// <summary>Fires if the Nakama socket closes unexpectedly.</summary>
        public event Action? OnDisconnected;

        // ── State ─────────────────────────────────────────────────────────────────

        public bool IsConnected => _socket?.IsConnected == true;
        public bool IsSearching => _searchTicket != null;
        public string? Username => _session?.Username;

        private IClient?           _client;
        private ISession?          _session;
        private ISocket?           _socket;
        private IMatchmakerTicket? _searchTicket;

        // Thread-safe queue for background→main-thread event marshaling.
        private readonly ConcurrentQueue<Action> _pending = new();

        // ── Connect / Auth ────────────────────────────────────────────────────────

        /// <summary>
        /// Authenticate with Nakama using email + password.
        /// Creates the account on first login (create: true).
        /// Must be awaited before calling FindMatchAsync.
        /// </summary>
        public async Task ConnectAsync(string email, string password)
        {
            Enqueue(() => OnStatusText?.Invoke("Connecting to matchmaking server…"));

            _client = new Client("http", NakamaHost, NakamaPort, NakamaKey);

            string username = email.Contains('@') ? email.Split('@')[0] : email;
            _session = await _client.AuthenticateEmailAsync(
                email, password, create: true, username: username);

            Enqueue(() => OnStatusText?.Invoke($"Authenticated as {_session.Username}"));

            _socket = Socket.From(_client);
            _socket.ReceivedMatchmakerMatched += HandleMatchmakerMatched;
            _socket.Closed                    += HandleSocketClosed;

            await _socket.ConnectAsync(_session, appearOnline: true);
            Enqueue(() => OnStatusText?.Invoke("Connected. Ready to find a match."));
        }

        /// <summary>
        /// Authenticate anonymously using a device-unique string.
        /// Useful for dev/LAN testing without email registration.
        /// </summary>
        public async Task ConnectDeviceAsync(string deviceId)
        {
            Enqueue(() => OnStatusText?.Invoke("Connecting (device auth)…"));

            _client  = new Client("http", NakamaHost, NakamaPort, NakamaKey);
            _session = await _client.AuthenticateDeviceAsync(deviceId, create: true);

            Enqueue(() => OnStatusText?.Invoke($"Authenticated (device)."));

            _socket = Socket.From(_client);
            _socket.ReceivedMatchmakerMatched += HandleMatchmakerMatched;
            _socket.Closed                    += HandleSocketClosed;

            await _socket.ConnectAsync(_session, appearOnline: true);
            Enqueue(() => OnStatusText?.Invoke("Connected. Ready to find a match."));
        }

        // ── Matchmaking ───────────────────────────────────────────────────────────

        /// <summary>
        /// Adds this player to the Nakama 1v1 matchmaker queue.
        /// OnMatchFound fires (via DrainEvents) when a second player is grouped.
        /// </summary>
        public async Task FindMatchAsync()
        {
            if (_socket == null)
                throw new InvalidOperationException("Not connected to Nakama. Call ConnectAsync first.");
            if (_searchTicket != null)
                return; // already searching

            Enqueue(() => OnStatusText?.Invoke("Searching for opponent…"));

            _searchTicket = await _socket.AddMatchmakerAsync(
                query: "*",
                minCount: 2,
                maxCount: 2,
                stringProperties: new Dictionary<string, string>
                {
                    ["game"] = "chimera_1v1"
                },
                numericProperties: new Dictionary<string, double>()
            );
        }

        /// <summary>Remove from matchmaker queue (user cancelled search).</summary>
        public async Task CancelSearchAsync()
        {
            if (_socket == null || _searchTicket == null) return;
            await _socket.RemoveMatchmakerAsync(_searchTicket);
            _searchTicket = null;
            Enqueue(() => OnStatusText?.Invoke("Search cancelled."));
        }

        // ── Disconnect ────────────────────────────────────────────────────────────

        public async Task DisconnectAsync()
        {
            _searchTicket = null;
            if (_socket != null)
            {
                _socket.ReceivedMatchmakerMatched -= HandleMatchmakerMatched;
                _socket.Closed                    -= HandleSocketClosed;
                await _socket.CloseAsync();
                _socket = null;
            }
            _session = null;
        }

        // ── Main-thread drain ─────────────────────────────────────────────────────

        /// <summary>
        /// Must be called each frame from LobbyUi._Process.
        /// Fires any pending events on the Godot main thread.
        /// </summary>
        public void DrainEvents()
        {
            while (_pending.TryDequeue(out var action))
                action();
        }

        // ── Nakama callbacks (background thread) ──────────────────────────────────

        private void HandleMatchmakerMatched(IMatchmakerMatched matched)
        {
            _searchTicket = null;

            // Determine faction: lexicographically lower userId becomes Player1.
            // Both players connect to the same dedicated server; DedicatedServer.cs
            // assigns factions via Hello packets regardless of this hint.
            string selfId     = _session?.UserId ?? "";
            string opponentId = GetOpponentUserId(matched);
            bool   isLower    = string.Compare(selfId, opponentId, StringComparison.Ordinal) < 0;
            var    hint       = isLower ? Core.Faction.Player1 : Core.Faction.Player2;

            var info = new MatchFoundInfo(GameServerIp, GameServerPort, hint);

            Enqueue(() =>
            {
                OnStatusText?.Invoke($"Match found! Connecting as {hint}…");
                OnMatchFound?.Invoke(info);
            });
        }

        private void HandleSocketClosed()
        {
            _socket       = null;
            _searchTicket = null;
            _session      = null;
            Enqueue(() => OnDisconnected?.Invoke());
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void Enqueue(Action a) => _pending.Enqueue(a);

        private static string GetOpponentUserId(IMatchmakerMatched matched)
        {
            string selfId = matched.Self.Presence.UserId;
            foreach (var user in matched.Users)
                if (user.Presence.UserId != selfId)
                    return user.Presence.UserId;
            return string.Empty;
        }
    }

    // ── Value types ───────────────────────────────────────────────────────────────

    /// <summary>Passed to NakamaService.OnMatchFound.</summary>
    public record MatchFoundInfo(
        string       ServerIp,
        int          ServerPort,
        Core.Faction SuggestedFaction
    );
}
