using System.Collections.Generic;

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

        // Story 3.10: retained so Clear() can reproduce the EXACT ctor state (the P1/P2 starting-ore seed) for the
        // Edit↔Play reset — the inverse of the ctor without reallocating.
        private readonly Fixed _startingOre;

        public ResourceStore(Fixed startingOre)
        {
            _startingOre = startingOre;
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

        /// <summary>
        /// Story 3.10 (UX-DR62): restore this store to its EXACT post-construction state for the Edit↔Play reset —
        /// zero ore/crystal/supply-used/faction-base, then re-seed the ctor's starting ore and P1/P2 supply cap. A
        /// cleared store is byte-for-byte equal to <c>new ResourceStore(_startingOre)</c>. The re-apply's additive
        /// <see cref="AddOre"/>/<see cref="AddCrystal"/> writes require this pre-clear (they append, never overwrite).
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(Ore);
            System.Array.Clear(Crystal);
            System.Array.Clear(SupplyUsed);
            System.Array.Clear(SupplyCap);
            System.Array.Clear(FactionBase);

            // Reproduce the ctor seed exactly (the reset must equal a freshly-constructed store).
            Ore[(int)Faction.Player1] = _startingOre;
            Ore[(int)Faction.Player2] = _startingOre;
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

        // ── Crystal API (Story 2.4a) ────────────────────────────────────────────
        // Mirrors the Ore API exactly. Crystal[] already existed (the scarce second resource) and is already folded
        // into SimChecksum, but had no spend/afford path — a documented dead path until abilities can cost crystal.
        // No checksum change from adding these (Crystal is already hashed).

        public void AddCrystal(Faction faction, Fixed amount) =>
            Crystal[(int)faction] = Crystal[(int)faction] + amount;

        public bool CanAffordCrystal(Faction faction, Fixed cost) =>
            Crystal[(int)faction] >= cost;

        /// <summary>Deduct crystal cost. Returns false (and does nothing — atomic refuse) if insufficient.</summary>
        public bool SpendCrystal(Faction faction, Fixed cost)
        {
            if (!CanAffordCrystal(faction, cost)) return false;
            Crystal[(int)faction] = Crystal[(int)faction] - cost;
            return true;
        }

        // ── Sparse cost-map API (Story 4.3) ─────────────────────────────────────
        // Generalizes the per-resource Ore/Crystal API to an authored sparse `{resourceId: amount}` map (e.g.
        // UnitDefinition.ResolvedCost) WITHOUT restructuring the underlying Ore[]/Crystal[] arrays: "ore"/"crystal"
        // route to the existing per-resource methods; any other key is fail-closed (unreachable for validated
        // content — ResourceCostValidator rejects any other key at import time). Every existing call site
        // (AbilityCastSystem, BuyItemCommand, ReviveHeroCommand, AiOpponentSystem) keeps using the per-resource
        // methods directly and is untouched by this addition.

        /// <summary>True iff the faction can afford EVERY entry in <paramref name="cost"/>. An unknown resource id
        /// (no runtime backing) fails closed — CanAfford returns false rather than silently ignoring the key.
        /// Amounts quantize via <see cref="Fixed.FromInt"/> (exact for the <c>int</c> cost type).</summary>
        public bool CanAfford(Faction faction, IReadOnlyDictionary<string, int> cost)
        {
            foreach (var (key, amount) in cost)
            {
                switch (key)
                {
                    case "ore":     if (!CanAffordOre(faction, Fixed.FromInt(amount)))     return false; break;
                    case "crystal": if (!CanAffordCrystal(faction, Fixed.FromInt(amount))) return false; break;
                    default:        return false; // unregistered resource id — fail closed
                }
            }
            return true;
        }

        /// <summary>Atomic spend of every entry in <paramref name="cost"/>: <see cref="CanAfford"/> first, then
        /// spend every key only if all afford (check-all-then-spend-all — mirrors every existing cost site in this
        /// file). Returns false (and spends nothing) if any key is unaffordable or unregistered.</summary>
        public bool Spend(Faction faction, IReadOnlyDictionary<string, int> cost)
        {
            if (!CanAfford(faction, cost)) return false;
            foreach (var (key, amount) in cost)
            {
                switch (key)
                {
                    case "ore":     SpendOre(faction, Fixed.FromInt(amount));     break;
                    case "crystal": SpendCrystal(faction, Fixed.FromInt(amount)); break;
                    // No default: CanAfford already fails closed on an unknown key, so this loop never reaches
                    // one — Spend is only ever called after CanAfford has passed.
                }
            }
            return true;
        }

        /// <summary>Add every entry in <paramref name="amounts"/> (e.g. a placement-failure refund). An unknown
        /// resource id is silently ignored (there is nothing to credit it to) — refunds are best-effort restitution
        /// of a prior Spend, never a new fail-closed gate.</summary>
        public void Add(Faction faction, IReadOnlyDictionary<string, int> amounts)
        {
            foreach (var (key, amount) in amounts)
            {
                switch (key)
                {
                    case "ore":     AddOre(faction, Fixed.FromInt(amount));     break;
                    case "crystal": AddCrystal(faction, Fixed.FromInt(amount)); break;
                }
            }
        }
    }
}
