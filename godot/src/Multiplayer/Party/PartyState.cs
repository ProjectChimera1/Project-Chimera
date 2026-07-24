#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Party
{
    /// <summary>
    /// Story 9.7 (AC1) — the Godot-free, Tier-1-testable pure party model driven by the Nakama <c>IParty</c> adapter
    /// (<c>PartyService</c>): the member set, the leader, per-member readiness, and the capacity, with the
    /// add/remove/leader-change/start-matchmaking DECISIONS the adapter delegates to. Never folded into
    /// <c>SimChecksum</c> (party state is pre-match, not sim state).
    ///
    /// Members are kept in an insertion-ordered <see cref="List{T}"/> (no <see cref="Dictionary{TKey,TValue}"/>
    /// enumeration — determinism-analyzer-safe) with a parallel ready list. The leader is the party host; only the
    /// leader may start matchmaking. When the leader leaves, leadership deterministically passes to the first
    /// remaining member.
    /// </summary>
    public sealed class PartyState
    {
        /// <summary>Default party capacity (a full 4-player match). Architected for 8 (a constant bump).</summary>
        public const int DefaultCapacity = 4;

        private readonly List<string> _members = new();
        private readonly List<bool>   _ready   = new();

        /// <summary>Maximum members this party holds.</summary>
        public int Capacity { get; }

        /// <summary>The current leader's user id, or null for an empty party.</summary>
        public string? LeaderId { get; private set; }

        /// <summary>Number of members currently in the party.</summary>
        public int Count => _members.Count;

        /// <summary>The members, in insertion order.</summary>
        public IReadOnlyList<string> Members => _members;

        public PartyState(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
                throw new System.ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be >= 1.");
            Capacity = capacity;
        }

        /// <summary>True if <paramref name="userId"/> is a member.</summary>
        public bool Contains(string userId) => IndexOf(userId) >= 0;

        /// <summary>Clear all members + leadership (e.g. the adapter resyncing from an authoritative party snapshot,
        /// or the party closing).</summary>
        public void Clear()
        {
            _members.Clear();
            _ready.Clear();
            LeaderId = null;
        }

        /// <summary>
        /// Add a member. Rejected (returns false, no mutation) for an empty id, a duplicate, or a full party
        /// (join beyond capacity). The FIRST member added becomes the leader.
        /// </summary>
        public bool TryAdd(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return false;
            if (_members.Count >= Capacity)    return false; // join beyond capacity → rejected
            if (IndexOf(userId) >= 0)           return false; // already a member

            _members.Add(userId);
            _ready.Add(false);
            LeaderId ??= userId; // first member leads
            return true;
        }

        /// <summary>
        /// Remove a member. Returns false if not a member. If the leader leaves, leadership passes to the first
        /// remaining member (null once the party is empty).
        /// </summary>
        public bool Remove(string userId)
        {
            int idx = IndexOf(userId);
            if (idx < 0) return false;

            _members.RemoveAt(idx);
            _ready.RemoveAt(idx);

            if (LeaderId == userId)
                LeaderId = _members.Count > 0 ? _members[0] : null;
            return true;
        }

        /// <summary>Promote an existing member to leader. Rejected (false) if the id is not a member.</summary>
        public bool TrySetLeader(string userId)
        {
            if (IndexOf(userId) < 0) return false;
            LeaderId = userId;
            return true;
        }

        /// <summary>Set a member's ready flag. Rejected (false) if the id is not a member.</summary>
        public bool SetReady(string userId, bool ready)
        {
            int idx = IndexOf(userId);
            if (idx < 0) return false;
            _ready[idx] = ready;
            return true;
        }

        /// <summary>True if <paramref name="userId"/> is a member and marked ready.</summary>
        public bool IsReady(string userId)
        {
            int idx = IndexOf(userId);
            return idx >= 0 && _ready[idx];
        }

        /// <summary>True iff the party is non-empty and every member is ready.</summary>
        public bool AllReady()
        {
            if (_members.Count == 0) return false;
            for (int i = 0; i < _ready.Count; i++)
                if (!_ready[i]) return false;
            return true;
        }

        /// <summary>
        /// Only the leader may start party matchmaking, and only a non-empty party may start. A non-leader request
        /// (or an empty party) is rejected.
        /// </summary>
        public bool CanStartMatchmaking(string requesterId)
            => LeaderId != null && requesterId == LeaderId && _members.Count >= 1;

        private int IndexOf(string userId)
        {
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] == userId) return i;
            return -1;
        }
    }
}
