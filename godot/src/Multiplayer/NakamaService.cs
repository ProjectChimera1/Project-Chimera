#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nakama;
using ProjectChimera.Core.Definitions;         // PlayerProfile, AttestationOutcome, ProfileInvalidReason (Story 9.12)
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

        /// <summary>Story 15-14: the live session's auth token — the credential the client presents to the GAME
        /// server's identity gate (<c>TickCommandPacket.MakeAttestation</c>); the server re-proves it against
        /// Nakama (<c>Server.NakamaTokenVerifier</c>), so a forged token never survives. Empty until connected.</summary>
        public string SessionToken => _session?.AuthToken ?? "";

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

        // ── Server-validated online hero profile (Story 9.12, FR-7c / AR-12) ───────────────
        //
        // The online profile is a Nakama STORAGE OBJECT the SERVER owns: written ONLY by the validating server RPC with
        // permissionRead=1 (owner-read) / permissionWrite=0 (no client write). This client READS it (owner-read) but
        // NEVER calls WriteStorageObjects for it — the only write path is WriteHeroProfileViaRpcAsync below. The RPC ids
        // + storage collection/key are the C# half of the contract mirrored by the TS module in
        // docs/server-deploy/nakama-modules (they MUST match on both sides). Every method is fail-soft/fail-closed: NO
        // unguarded exception escapes (a read failure ⇒ null; a write failure ⇒ a Failed result; an attest failure ⇒
        // CallSucceeded=false = fail-closed).

        /// <summary>Nakama storage collection holding each user's single server-owned hero profile object.</summary>
        public const string HeroProfileCollection = "heroes";
        /// <summary>Storage key of the single per-user hero profile object within <see cref="HeroProfileCollection"/>.</summary>
        public const string HeroProfileKey = "profile";
        /// <summary>Server RPC id that validates + writes the owner-read/no-client-write profile object.</summary>
        public const string RpcWriteHeroProfile  = "rpc_write_hero_profile";
        /// <summary>Server RPC id that reads the stored object, re-validates it, and returns an attestation.</summary>
        public const string RpcAttestHeroProfile = "rpc_attest_hero_profile";

        /// <summary>
        /// Read this user's server-owned hero profile via owner-read <c>ReadStorageObjects</c> (NEVER a write). Returns
        /// the deserialized <see cref="PlayerProfile"/>, or <c>null</c> when no object exists / not connected / the
        /// stored value is unparseable / the read throws (fail-soft — mirrors <see cref="LocalProfileSource.LoadAll"/>'s
        /// empty-store row; no unguarded exception escapes).
        /// </summary>
        public async Task<PlayerProfile?> ReadHeroProfileAsync()
        {
            if (_client == null || _session == null) return null;
            try
            {
                IApiStorageObjects result = await _client.ReadStorageObjectsAsync(_session, new IApiReadStorageObjectId[]
                {
                    new StorageObjectId { Collection = HeroProfileCollection, Key = HeroProfileKey, UserId = _session.UserId },
                });

                foreach (IApiStorageObject obj in result.Objects)
                {
                    if (string.IsNullOrEmpty(obj.Value)) continue;
                    try { return JsonSerializer.Deserialize<PlayerProfile>(obj.Value); }
                    catch { return null; } // corrupt stored value → treat as "no profile", never a throw
                }
                return null;
            }
            catch
            {
                return null; // Nakama unreachable / read error → fail-soft (never a throw out of this method)
            }
        }

        /// <summary>
        /// Persist <paramref name="profile"/> through the validating <c>rpc_write_hero_profile</c> server RPC — the ONLY
        /// write path (this method NEVER calls <c>WriteStorageObjects</c>). The server validates the payload and, only
        /// if valid, writes the owner-read/no-client-write object; an invalid payload returns <c>ok:false</c> with a
        /// reason and writes nothing. Any error (not connected / RPC throws / bad reply) ⇒ a <see cref="StorageWriteResult"/>
        /// with <c>Ok = false</c> — no unguarded exception escapes.
        /// </summary>
        public async Task<StorageWriteResult> WriteHeroProfileViaRpcAsync(PlayerProfile profile)
        {
            if (_client == null || _session == null)
                return StorageWriteResult.Failed("not_connected");
            try
            {
                string payload = JsonSerializer.Serialize(profile);
                IApiRpc rpc = await _client.RpcAsync(_session, RpcWriteHeroProfile, payload);
                return AttestationReplyParser.ParseWriteResult(rpc?.Payload);
            }
            catch (Exception e)
            {
                return StorageWriteResult.Failed(e.Message);
            }
        }

        /// <summary>
        /// Ask the server to attest the stored profile for <paramref name="profileId"/> via
        /// <c>rpc_attest_hero_profile</c>: the server reads the owner-read object, re-validates it, and returns
        /// <c>{attested, reason}</c>. Maps to an <see cref="AttestationOutcome"/> the pure
        /// <see cref="OnlineHeroLaunchGate"/> gates on. Not connected / RPC exception / timeout ⇒
        /// <see cref="AttestationOutcome.CallFailed"/> (<c>CallSucceeded = false</c> = FAIL-CLOSED) — never fail-open
        /// into an online match on an attestation error.
        /// </summary>
        public async Task<AttestationOutcome> AttestHeroProfileAsync(string profileId)
        {
            // P10: the server id-binding is only meaningful with a concrete id; a blank id would let the TS handler
            // skip the id-match check and attest whatever is stored. The caller (the hero picker) always passes the
            // selected profile's non-empty ProfileId — guard fail-closed if that contract is ever violated.
            if (string.IsNullOrEmpty(profileId))
                return AttestationOutcome.CallFailed;
            if (_client == null || _session == null)
                return AttestationOutcome.CallFailed;

            string payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["profileId"] = profileId });
            try
            {
                IApiRpc rpc = await _client.RpcAsync(_session, RpcAttestHeroProfile, payload);
                return AttestationReplyParser.ParseAttestation(rpc?.Payload);
            }
            catch
            {
                return AttestationOutcome.CallFailed; // Nakama unreachable / RPC error / timeout → fail-closed
            }
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

    // StorageWriteResult moved to ProjectChimera.Core.Definitions (AttestationReplyParser.cs) so the fail-closed reply
    // parsing is Godot-free/SDK-free and Tier-1 testable (Story 9.12, P4).
}
