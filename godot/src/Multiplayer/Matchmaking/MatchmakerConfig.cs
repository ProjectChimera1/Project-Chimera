#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Matchmaking
{
    /// <summary>
    /// Story 9.7 — the Godot-free, Tier-1-testable N-player matchmaker parameterization. Replaces the hardcoded
    /// <c>minCount/maxCount=2</c> + <c>game=chimera_1v1</c> pin in <c>NakamaService.FindMatchAsync</c> with a value
    /// object built from a target player count: <see cref="MinCount"/>/<see cref="MaxCount"/>/<see cref="CountMultiple"/>,
    /// the matchmaker <see cref="Query"/>, and the <see cref="StringProperties"/> (the game key is now player-count
    /// parameterized — <c>chimera_2p</c>/<c>chimera_4p</c> — so a 1v1 and a 4-player queue never cross-match).
    ///
    /// Pure C# (no Godot, no Nakama) so the parameterization is unit-testable and the adapter (<c>NakamaService</c>)
    /// just consumes it. Architected for 8 (a constant bump) — <see cref="ForPlayerCount"/> accepts any P in
    /// [<see cref="MinPlayers"/>, <see cref="Core.FactionRegistry.PLAYER_COUNT"/>].
    /// </summary>
    public sealed class MatchmakerConfig
    {
        /// <summary>The smallest legal match size. Multiplayer needs at least two players — P&lt;2 is rejected.</summary>
        public const int MinPlayers = 2;

        /// <summary>The canonical game-key prefix (was implicitly <c>chimera</c> in the old <c>chimera_1v1</c> pin).</summary>
        public const string DefaultGameKey = "chimera";

        /// <summary>Minimum players the matchmaker groups before signalling a match.</summary>
        public int MinCount { get; }

        /// <summary>Maximum players the matchmaker groups into one match.</summary>
        public int MaxCount { get; }

        /// <summary>Optional Nakama <c>countMultiple</c> (group size must be a multiple of this). Null = unconstrained.</summary>
        public int? CountMultiple { get; }

        /// <summary>Nakama matchmaker query string (<c>*</c> = any — the endpoint is a single static configured server).</summary>
        public string Query { get; }

        /// <summary>The player-count-parameterized game key (e.g. <c>chimera_2p</c>), the value of the <c>game</c> property.</summary>
        public string GameKey { get; }

        private MatchmakerConfig(int minCount, int maxCount, int? countMultiple, string query, string gameKey)
        {
            MinCount      = minCount;
            MaxCount      = maxCount;
            CountMultiple = countMultiple;
            Query         = query;
            GameKey       = gameKey;
        }

        /// <summary>
        /// Build a config for a fixed-size P-player match. <paramref name="playerCount"/> must be &gt;=
        /// <see cref="MinPlayers"/> (P&lt;2 → <see cref="ArgumentOutOfRangeException"/>: an invalid config is
        /// rejected, never silently coerced). The game key is parameterized as <c>{gameKey}_{P}p</c> so distinct
        /// player counts form distinct matchmaker pools.
        /// </summary>
        public static MatchmakerConfig ForPlayerCount(
            int playerCount, string gameKey = DefaultGameKey, string query = "*", int? countMultiple = null)
        {
            if (playerCount < MinPlayers)
                throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount,
                    $"playerCount must be >= {MinPlayers} (multiplayer needs at least two players).");
            // Story 9.7 (P3): never queue/group MORE players than the transport can SEAT — the matchmaker ceiling is
            // the transport seat ceiling (PlayerCountPolicy.MpSeatCeiling == ServerTransport.MAX_PLAYERS), NOT the
            // larger sim faction ceiling (PLAYER_COUNT). Raising the seat ceiling to 8 is the documented constant bump.
            if (playerCount > PlayerCountPolicy.MpSeatCeiling)
                throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount,
                    $"playerCount must be <= {PlayerCountPolicy.MpSeatCeiling} (the transport seat ceiling).");
            if (countMultiple is int cm && (cm < 1 || playerCount % cm != 0))
                throw new ArgumentOutOfRangeException(nameof(countMultiple), countMultiple,
                    "countMultiple must be >= 1 and evenly divide playerCount.");

            string key = string.IsNullOrEmpty(gameKey) ? DefaultGameKey : gameKey;
            return new MatchmakerConfig(playerCount, playerCount, countMultiple, query ?? "*", $"{key}_{playerCount}p");
        }

        /// <summary>The Nakama string properties for this config (the <c>game</c> pool key).</summary>
        public IReadOnlyDictionary<string, string> StringProperties()
            => new Dictionary<string, string> { ["game"] = GameKey };

        /// <summary>The Nakama numeric properties for this config (none today — reserved for MMR/region, post-1.0).</summary>
        public IReadOnlyDictionary<string, double> NumericProperties()
            => new Dictionary<string, double>();
    }
}
