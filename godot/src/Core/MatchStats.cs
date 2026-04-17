#nullable enable

namespace ProjectChimera.Core
{
    /// <summary>
    /// Lightweight per-match statistics gathered by simulation systems.
    /// Pure C# — no Godot dependency.
    ///
    /// Indexed by <see cref="Faction"/> cast to int (0–4).
    /// Faction.None (0) is ignored; Player1 = 1, Player2 = 2.
    /// </summary>
    public class MatchStats
    {
        private const int FACTION_COUNT = 5; // matches Faction enum range

        /// <summary>Units killed BY each faction (kills[1] = P1 kill count).</summary>
        private readonly int[] _kills = new int[FACTION_COUNT];

        /// <summary>Units lost BY each faction (losses[1] = P1 units destroyed).</summary>
        private readonly int[] _losses = new int[FACTION_COUNT];

        /// <summary>Units trained/spawned by each faction this match.</summary>
        private readonly int[] _unitsBuilt = new int[FACTION_COUNT];

        /// <summary>Total ore deposited by each faction's workers this match (integer units).</summary>
        private readonly int[] _oreMined = new int[FACTION_COUNT];

        // ── Kill tracking ─────────────────────────────────────────────────────

        /// <summary>
        /// Record a unit kill.
        /// </summary>
        /// <param name="killedFaction">Faction the destroyed unit belonged to.</param>
        /// <param name="killerFaction">Faction that landed the killing blow.</param>
        public void RecordKill(Faction killedFaction, Faction killerFaction)
        {
            int killed = (int)killedFaction;
            int killer = (int)killerFaction;
            if (killed > 0 && killed < FACTION_COUNT) _losses[killed]++;
            if (killer > 0 && killer < FACTION_COUNT) _kills[killer]++;
        }

        /// <summary>Units killed by the given faction this match.</summary>
        public int Kills(Faction f) => (int)f < FACTION_COUNT ? _kills[(int)f] : 0;

        /// <summary>Units lost by the given faction this match.</summary>
        public int Losses(Faction f) => (int)f < FACTION_COUNT ? _losses[(int)f] : 0;

        // ── Production tracking ───────────────────────────────────────────────

        /// <summary>Record one unit trained/spawned for the given faction.</summary>
        public void RecordUnitBuilt(Faction faction)
        {
            int f = (int)faction;
            if (f > 0 && f < FACTION_COUNT) _unitsBuilt[f]++;
        }

        /// <summary>Units trained by the given faction this match.</summary>
        public int UnitsBuilt(Faction f) => (int)f < FACTION_COUNT ? _unitsBuilt[(int)f] : 0;

        // ── Economy tracking ──────────────────────────────────────────────────

        /// <summary>Record ore deposited by a worker returning to base.</summary>
        /// <param name="amount">Amount in Fixed-point units — converted to int for storage.</param>
        public void RecordOreMined(Faction faction, Fixed amount)
        {
            int f = (int)faction;
            if (f > 0 && f < FACTION_COUNT) _oreMined[f] += amount.ToInt();
        }

        /// <summary>Total ore mined by the given faction this match (integer units).</summary>
        public int OreMined(Faction f) => (int)f < FACTION_COUNT ? _oreMined[(int)f] : 0;

        // ── Reset ─────────────────────────────────────────────────────────────

        /// <summary>Reset all counters (called when returning to Edit mode).</summary>
        public void Reset()
        {
            for (int i = 0; i < FACTION_COUNT; i++)
            {
                _kills[i]      = 0;
                _losses[i]     = 0;
                _unitsBuilt[i] = 0;
                _oreMined[i]   = 0;
            }
        }
    }
}
