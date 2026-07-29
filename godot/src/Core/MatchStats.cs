#nullable enable

namespace ProjectChimera.Core
{
    /// <summary>
    /// Lightweight per-match statistics gathered by simulation systems.
    /// Pure C# — no Godot dependency.
    ///
    /// Indexed by <see cref="Faction"/> cast to int (0–4).
    /// Faction.None (0) is ignored; Player1 = 1, Player2 = 2.
    ///
    /// <para><b>Class note (deliberately unfolded):</b> MatchStats is the observational scoreboard — it is NOT folded
    /// into <c>SimChecksum</c> (see <c>SimChecksum.cs</c>). Adding a counter here therefore moves no golden and needs no
    /// re-baseline (the checksum-fold-timing rule: only mid-match-mutable SIM state folds).</para>
    /// </summary>
    public class MatchStats
    {
        private const int FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE; // 9: Neutral + Player1..Player8

        /// <summary>Units killed BY each faction (kills[1] = P1 kill count).</summary>
        private readonly int[] _kills = new int[FACTION_COUNT];

        /// <summary>Units lost BY each faction (losses[1] = P1 units destroyed).</summary>
        private readonly int[] _losses = new int[FACTION_COUNT];

        /// <summary>Units trained/spawned by each faction this match.</summary>
        private readonly int[] _unitsBuilt = new int[FACTION_COUNT];

        /// <summary>Total ore deposited by each faction's workers this match (integer units).</summary>
        private readonly int[] _oreMined = new int[FACTION_COUNT];

        /// <summary>Story 11.2 — total crystal credited to each faction this match (integer units). The observational
        /// twin of <see cref="_oreMined"/>, closing the score-screen's Crystal column. Unfolded (see class note).</summary>
        private readonly int[] _crystalMined = new int[FACTION_COUNT];

        /// <summary>Story 11.2 — enemy buildings RAZED BY each faction this match (razed[1] = P1's building-kill count).
        /// The building twin of <see cref="_kills"/> (which counts unit kills). Unfolded (see class note).</summary>
        private readonly int[] _buildingsRazed = new int[FACTION_COUNT];

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

        /// <summary>Record ore credited to a faction's balance from a resource node — a GATHER worker's deposit on
        /// arrival at base, or (Story 4.7) a Streaming node's credit-in-place, or an Income node's periodic trickle.
        /// Ore-only: Crystal-type node credits do not call this (mirrors every other AddCrystal call site — none of
        /// them record stats either).</summary>
        /// <param name="amount">Amount in Fixed-point units — converted to int for storage.</param>
        public void RecordOreMined(Faction faction, Fixed amount)
        {
            int f = (int)faction;
            if (f > 0 && f < FACTION_COUNT) _oreMined[f] += amount.ToInt();
        }

        /// <summary>Total ore mined by the given faction this match (integer units).</summary>
        public int OreMined(Faction f) => (int)f < FACTION_COUNT ? _oreMined[(int)f] : 0;

        /// <summary>Story 11.2 — record crystal credited to a faction's balance from a resource node (mirrors
        /// <see cref="RecordOreMined"/> exactly, on the Crystal-kind credit path). Crystal-only.</summary>
        /// <param name="amount">Amount in Fixed-point units — converted to int for storage.</param>
        public void RecordCrystalMined(Faction faction, Fixed amount)
        {
            int f = (int)faction;
            if (f > 0 && f < FACTION_COUNT) _crystalMined[f] += amount.ToInt();
        }

        /// <summary>Total crystal mined by the given faction this match (integer units).</summary>
        public int CrystalMined(Faction f) => (int)f < FACTION_COUNT ? _crystalMined[(int)f] : 0;

        // ── Building-razing tracking ──────────────────────────────────────────

        /// <summary>Story 11.2 — record one enemy building razed BY <paramref name="razerFaction"/> (mirrors the killer
        /// side of <see cref="RecordKill"/>). A Neutral/out-of-range razer is a no-op (buildings razed by nobody count
        /// for nobody).</summary>
        public void RecordBuildingRazed(Faction razerFaction)
        {
            int f = (int)razerFaction;
            if (f > 0 && f < FACTION_COUNT) _buildingsRazed[f]++;
        }

        /// <summary>Enemy buildings razed by the given faction this match.</summary>
        public int BuildingsRazed(Faction f) => (int)f < FACTION_COUNT ? _buildingsRazed[(int)f] : 0;

        // ── Reset ─────────────────────────────────────────────────────────────

        /// <summary>Reset all counters (called when returning to Edit mode).</summary>
        public void Reset()
        {
            for (int i = 0; i < FACTION_COUNT; i++)
            {
                _kills[i]         = 0;
                _losses[i]        = 0;
                _unitsBuilt[i]    = 0;
                _oreMined[i]      = 0;
                _crystalMined[i]  = 0;
                _buildingsRazed[i] = 0;
            }
        }
    }
}
