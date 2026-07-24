#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using ProjectChimera.Multiplayer.Matchmaking; // MatchmakerConfig
using ProjectChimera.Multiplayer.Party;        // PartyService

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Nakama matchmaking integration for Project Chimera (N-player, Story 9.7).
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

        /// <summary>Story 9.7: the target match size the matchmaker groups for (2..8). Drives
        /// <see cref="MatchmakerConfig"/> — no longer the hardcoded 1v1 pin. Defaults to 2 (1v1).</summary>
        public int    TargetPlayerCount { get; set; } = 2;

        /// <summary>Story 9.7: the matchmaker game-key prefix (parameterized per player count as
        /// <c>{GameKey}_{P}p</c>). Kept configurable so distinct rulesets/queues don't cross-match.</summary>
        public string GameKey { get; set; } = MatchmakerConfig.DefaultGameKey;

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

        /// <summary>The authenticated user's Nakama id (for party leadership checks). Empty until connected.</summary>
        public string UserId => _session?.UserId ?? "";

        /// <summary>Story 9.7: the parties API adapter (over Nakama <see cref="IParty"/>), created on connect. Null
        /// until <see cref="ConnectAsync"/>/<see cref="ConnectDeviceAsync"/> establishes the socket. Its events are
        /// drained on the main thread by this service's <see cref="DrainEvents"/> (shared queue).</summary>
        public PartyService? Party { get; private set; }

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
            // Story 9.7: stand up the parties adapter over the same socket; its events ride this service's drain.
            Party = new PartyService(_socket, Enqueue);

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
            // Story 9.7: stand up the parties adapter over the same socket; its events ride this service's drain.
            Party = new PartyService(_socket, Enqueue);

            await _socket.ConnectAsync(_session, appearOnline: true);
            Enqueue(() => OnStatusText?.Invoke("Connected. Ready to find a match."));
        }

        // ── Matchmaking ───────────────────────────────────────────────────────────

        /// <summary>
        /// Adds this player to the Nakama N-player matchmaker queue (Story 9.7). The match size + game-key are
        /// parameterized by <see cref="TargetPlayerCount"/>/<see cref="GameKey"/> via <see cref="MatchmakerConfig"/>
        /// — no longer the hardcoded 1v1 / <c>chimera_1v1</c> pin. OnMatchFound fires (via DrainEvents) when the
        /// matchmaker groups the target count.
        /// </summary>
        public async Task FindMatchAsync()
        {
            if (_socket == null)
                throw new InvalidOperationException("Not connected to Nakama. Call ConnectAsync first.");
            if (_searchTicket != null)
                return; // already searching

            var config = MatchmakerConfig.ForPlayerCount(TargetPlayerCount, GameKey);

            Enqueue(() => OnStatusText?.Invoke(
                config.MaxCount == 2 ? "Searching for opponent…" : $"Searching for a {config.MaxCount}-player match…"));

            _searchTicket = await _socket.AddMatchmakerAsync(
                query: config.Query,
                minCount: config.MinCount,
                maxCount: config.MaxCount,
                stringProperties: new Dictionary<string, string>(config.StringProperties()),
                numericProperties: new Dictionary<string, double>(config.NumericProperties())
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
            Party?.Detach();
            Party = null;
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

            // Story 9.7: the client-side lexicographic faction hint is DELETED. Slot/faction is now
            // server-authoritative — every matched player connects to the same dedicated server, which assigns the
            // faction from the transport accept-slot via its frozen AssignedRoster (the Hello packet). MatchFoundInfo
            // therefore carries the endpoint ONLY.
            var info = new MatchFoundInfo(GameServerIp, GameServerPort);

            Enqueue(() =>
            {
                OnStatusText?.Invoke($"Match found! Joining server {info.ServerIp}:{info.ServerPort}…");
                OnMatchFound?.Invoke(info);
            });
        }

        private void HandleSocketClosed()
        {
            Party?.Detach();
            Party         = null;
            _socket       = null;
            _searchTicket = null;
            _session      = null;
            Enqueue(() => OnDisconnected?.Invoke());
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void Enqueue(Action a) => _pending.Enqueue(a);
    }

    // ── Value types ───────────────────────────────────────────────────────────────

    /// <summary>Passed to NakamaService.OnMatchFound. Story 9.7: endpoint ONLY — the faction is server-assigned
    /// (the lexicographic client hint was deleted).</summary>
    public record MatchFoundInfo(
        string ServerIp,
        int    ServerPort
    );
}
