#nullable enable

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 9.7 (AC2) — the Godot-free, Tier-1-testable N-slot lobby readiness model. Generalizes the two-peer
    /// <c>_readyConfirmed</c>/<c>_peerReadyConfirmed</c> booleans in <c>LobbyUi</c> into per-slot occupied/ready
    /// state plus an all-ready gate. The client/lobby-side analogue of the server's
    /// <c>ServerLobbyPolicy.ShouldStart</c>: the host <b>Start</b> button is enabled only when every player slot is
    /// occupied AND ready. Spectator slots (index &gt;= the match's player count) never contribute to — and never
    /// block — the all-ready gate.
    /// </summary>
    public sealed class LobbyReadyModel
    {
        private readonly bool[] _occupied;
        private readonly bool[] _ready;

        /// <summary>Total slot capacity (players + spectator headroom) this model tracks.</summary>
        public int Capacity { get; }

        public LobbyReadyModel(int capacity)
        {
            if (capacity < 1)
                throw new System.ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be >= 1.");
            Capacity  = capacity;
            _occupied = new bool[capacity];
            _ready    = new bool[capacity];
        }

        /// <summary>Mark a slot occupied/vacated (a vacated slot is also cleared of readiness).</summary>
        public void SetOccupied(int slot, bool occupied)
        {
            if ((uint)slot >= (uint)Capacity) return;
            _occupied[slot] = occupied;
            if (!occupied) _ready[slot] = false;
        }

        /// <summary>Set a slot's ready flag (ignored for an unoccupied slot — an empty slot can never be ready).</summary>
        public void SetReady(int slot, bool ready)
        {
            if ((uint)slot >= (uint)Capacity) return;
            if (!_occupied[slot]) return;
            _ready[slot] = ready;
        }

        public bool IsOccupied(int slot) => (uint)slot < (uint)Capacity && _occupied[slot];
        public bool IsReady(int slot)    => (uint)slot < (uint)Capacity && _ready[slot];

        /// <summary>Reset all occupied/ready state (lobby close / disconnect-all).</summary>
        public void Reset()
        {
            for (int i = 0; i < Capacity; i++) { _occupied[i] = false; _ready[i] = false; }
        }

        /// <summary>
        /// True iff every one of the <paramref name="playerCount"/> player slots (0..playerCount-1) is BOTH occupied
        /// and ready — the client-side mirror of <c>ServerLobbyPolicy.ShouldStart(connected==playerCount,
        /// ready==playerCount, playerCount)</c>. A spectator slot (index &gt;= playerCount) is never inspected, so it
        /// can neither satisfy nor block the gate. A non-positive <paramref name="playerCount"/> is never all-ready.
        /// </summary>
        public bool AllReady(int playerCount)
        {
            if (playerCount < 1 || playerCount > Capacity) return false;
            for (int s = 0; s < playerCount; s++)
                if (!_occupied[s] || !_ready[s]) return false;
            return true;
        }

        /// <summary>The host <b>Start</b> button is enabled iff <see cref="AllReady"/> — the single start gate.</summary>
        public bool StartEnabled(int playerCount) => AllReady(playerCount);
    }
}
