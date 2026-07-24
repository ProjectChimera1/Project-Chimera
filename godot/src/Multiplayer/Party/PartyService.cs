#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using ProjectChimera.Multiplayer.Matchmaking; // MatchmakerConfig

namespace ProjectChimera.Multiplayer.Party
{
    /// <summary>
    /// Story 9.7 (AC1) — the Godot-coupled Nakama <c>IParty</c> adapter (a sibling of <c>NakamaService</c>). Wraps
    /// the party socket calls (create / join / leave / promote-leader / party matchmaking) and drives the Godot-free
    /// <see cref="PartyState"/> from the SDK's party events. The Nakama SDK fires events on a BACKGROUND thread, so
    /// every state mutation + notification is marshalled to the main thread through the same
    /// <c>ConcurrentQueue&lt;Action&gt;</c> drain as <c>NakamaService</c> (the injected <c>enqueue</c> callback →
    /// <c>NakamaService.DrainEvents</c>).
    ///
    /// This is the minimal parties API + entry (SD-9 deferrable slice) — a full polished parties lobby UI is a
    /// documented fast-follow. All matchmaking decisions (leader-only start, capacity) live in the pure
    /// <see cref="PartyState"/>.
    /// </summary>
    public class PartyService
    {
        private readonly ISocket        _socket;
        private readonly Action<Action> _enqueue; // marshal a background-thread action onto the main-thread drain

        private IParty? _party;

        /// <summary>The pure party model kept in sync with the authoritative Nakama party. Reconstructed to the
        /// party's <c>MaxSize</c> when a snapshot arrives (Story 9.7 P8), so a party larger than the local default
        /// never silently drops members past the old fixed capacity.</summary>
        public PartyState State { get; private set; } = new();

        /// <summary>The current party id, or null when not in a party.</summary>
        public string? PartyId => _party?.Id;

        /// <summary>True while this client is a member of a party.</summary>
        public bool InParty => _party != null;

        // ── Events (fired on the main thread via the drain) ────────────────────────

        /// <summary>Fires (on the main thread) whenever <see cref="State"/> changes — for a party-panel refresh.</summary>
        public event Action<PartyState>? OnPartyChanged;

        /// <summary>Human-readable status text for the lobby/party UI.</summary>
        public event Action<string>? OnStatusText;

        /// <summary>Fires when a party matchmaker ticket is issued (the leader started party matchmaking).</summary>
        public event Action<IPartyMatchmakerTicket>? OnPartyMatchmakerTicket;

        public PartyService(ISocket socket, Action<Action> enqueue)
        {
            _socket  = socket ?? throw new ArgumentNullException(nameof(socket));
            _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));

            _socket.ReceivedParty                 += HandleParty;
            _socket.ReceivedPartyPresence         += HandlePresence;
            _socket.ReceivedPartyLeader           += HandleLeader;
            _socket.ReceivedPartyMatchmakerTicket += HandleMatchmakerTicket;
            _socket.ReceivedPartyClose            += HandleClose;
        }

        // ── Party lifecycle (leader / member actions) ──────────────────────────────

        /// <summary>Create a new party (this client becomes the leader). Open parties accept joins without approval.</summary>
        public async Task CreateAsync(bool open = true, int maxSize = PartyState.DefaultCapacity)
        {
            _party = await _socket.CreatePartyAsync(open, maxSize);
            SyncFromParty(_party);
            Enqueue(() => OnStatusText?.Invoke($"Party created ({_party?.Id})."));
        }

        /// <summary>Join an existing party by id.</summary>
        public Task JoinAsync(string partyId) => _socket.JoinPartyAsync(partyId);

        /// <summary>Leave the current party (if any).</summary>
        public async Task LeaveAsync()
        {
            if (_party == null) return;
            string id = _party.Id;
            await _socket.LeavePartyAsync(id);
            _party = null;
            Enqueue(() =>
            {
                State.Clear();
                OnPartyChanged?.Invoke(State);
                OnStatusText?.Invoke("Left the party.");
            });
        }

        /// <summary>Promote a party member to leader (leader-only, enforced server-side by Nakama).</summary>
        public Task PromoteAsync(IUserPresence member)
            => _party != null ? _socket.PromotePartyMemberAsync(_party.Id, member) : Task.CompletedTask;

        /// <summary>
        /// Start party matchmaking with the given <see cref="MatchmakerConfig"/>. Rejected (returns false, no socket
        /// call) unless this client is the party leader (<see cref="PartyState.CanStartMatchmaking"/>) — only the
        /// leader may matchmake the party.
        /// </summary>
        public async Task<bool> StartMatchmakingAsync(MatchmakerConfig config, string selfUserId)
        {
            if (_party == null) return false;
            if (!State.CanStartMatchmaking(selfUserId)) return false;

            await _socket.AddMatchmakerPartyAsync(
                _party.Id, config.Query, config.MinCount, config.MaxCount,
                new Dictionary<string, string>(config.StringProperties()),
                new Dictionary<string, double>(config.NumericProperties()),
                config.CountMultiple);
            return true;
        }

        /// <summary>Unsubscribe from the socket party events (call on disconnect/dispose).</summary>
        public void Detach()
        {
            _socket.ReceivedParty                 -= HandleParty;
            _socket.ReceivedPartyPresence         -= HandlePresence;
            _socket.ReceivedPartyLeader           -= HandleLeader;
            _socket.ReceivedPartyMatchmakerTicket -= HandleMatchmakerTicket;
            _socket.ReceivedPartyClose            -= HandleClose;
        }

        // ── Nakama callbacks (background thread → marshalled) ──────────────────────

        private void HandleParty(IParty party)
        {
            _party = party;
            Enqueue(() =>
            {
                SyncFromParty(party);
                OnPartyChanged?.Invoke(State);
            });
        }

        private void HandlePresence(IPartyPresenceEvent ev)
        {
            Enqueue(() =>
            {
                if (ev.Leaves != null)
                    foreach (var p in ev.Leaves) State.Remove(p.UserId);
                if (ev.Joins != null)
                    foreach (var p in ev.Joins) State.TryAdd(p.UserId);
                OnPartyChanged?.Invoke(State);
            });
        }

        private void HandleLeader(IPartyLeader leader)
        {
            Enqueue(() =>
            {
                if (leader.Presence != null) State.TrySetLeader(leader.Presence.UserId);
                OnPartyChanged?.Invoke(State);
            });
        }

        private void HandleMatchmakerTicket(IPartyMatchmakerTicket ticket)
            => Enqueue(() => OnPartyMatchmakerTicket?.Invoke(ticket));

        private void HandleClose(IPartyClose _)
        {
            _party = null;
            Enqueue(() =>
            {
                State.Clear();
                OnPartyChanged?.Invoke(State);
                OnStatusText?.Invoke("Party closed.");
            });
        }

        // ── Helpers ────────────────────────────────────────────────────────────────

        /// <summary>Resync <see cref="State"/> from an authoritative party snapshot (members ascending presence order).</summary>
        private void SyncFromParty(IParty party)
        {
            // Story 9.7 (P8): size the local model to the AUTHORITATIVE party MaxSize (not a fixed local const), so a
            // party whose maxSize exceeds our default never silently drops members past index 4.
            int cap = party.MaxSize > 0 ? party.MaxSize : PartyState.DefaultCapacity;
            if (cap != State.Capacity) State = new PartyState(cap);
            State.Clear();
            if (party.Presences != null)
                foreach (var p in party.Presences) State.TryAdd(p.UserId);
            // The party's own presence + leader may not be in Presences on some SDK paths — ensure both are members.
            if (party.Self != null) State.TryAdd(party.Self.UserId);
            if (party.Leader != null)
            {
                State.TryAdd(party.Leader.UserId);
                State.TrySetLeader(party.Leader.UserId);
            }
        }

        private void Enqueue(Action a) => _enqueue(a);
    }
}
