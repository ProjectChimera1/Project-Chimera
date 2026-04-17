namespace ProjectChimera.Core
{
    /// <summary>
    /// Per-faction resource balances and faction base positions.
    /// Indexed by (int)Faction (0 = Neutral, 1 = Player1 … 4 = Player4).
    /// </summary>
    public class ResourceStore
    {
        private const int FACTION_COUNT = 5;

        // Current balances
        public readonly Fixed[] Ore;
        public readonly Fixed[] Crystal;

        // Supply
        /// <summary>Supply currently consumed per faction (recalculated by SupplySystem each tick).</summary>
        public readonly int[] SupplyUsed;
        /// <summary>Max supply per faction. Increased by buildings; starts at STARTING_SUPPLY_CAP.</summary>
        public readonly int[] SupplyCap;

        public const int STARTING_SUPPLY_CAP = 10;

        /// <summary>
        /// World position where workers of each faction return to deposit.
        /// Set by MainScene before the simulation starts.
        /// </summary>
        public readonly FixedVec3[] FactionBase;

        public ResourceStore(Fixed startingOre)
        {
            Ore         = new Fixed[FACTION_COUNT];
            Crystal     = new Fixed[FACTION_COUNT];
            SupplyUsed  = new int[FACTION_COUNT];
            SupplyCap   = new int[FACTION_COUNT];
            FactionBase = new FixedVec3[FACTION_COUNT];

            Ore[(int)Faction.Player1] = startingOre;
            Ore[(int)Faction.Player2] = startingOre;

            SupplyCap[(int)Faction.Player1] = STARTING_SUPPLY_CAP;
            SupplyCap[(int)Faction.Player2] = STARTING_SUPPLY_CAP;
        }

        // ── Convenience methods ────────────────────────────────────────────────

        public void AddOre(Faction faction, Fixed amount) =>
            Ore[(int)faction] = Ore[(int)faction] + amount;

        public bool CanAffordOre(Faction faction, Fixed cost) =>
            Ore[(int)faction] >= cost;

        public bool HasSupply(Faction faction, int cost = 1) =>
            SupplyUsed[(int)faction] + cost <= SupplyCap[(int)faction];

        /// <summary>Deduct ore cost. Returns false (and does nothing) if insufficient.</summary>
        public bool SpendOre(Faction faction, Fixed cost)
        {
            if (!CanAffordOre(faction, cost)) return false;
            Ore[(int)faction] = Ore[(int)faction] - cost;
            return true;
        }
    }
}
