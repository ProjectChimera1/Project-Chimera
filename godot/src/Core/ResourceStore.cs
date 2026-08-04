#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions; // SupplyConfig (Story 4.4)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Per-faction resource balances and faction base positions.
    /// Indexed by (int)Faction (0 = Neutral, 1 = Player1 … 8 = Player8).
    /// </summary>
    public class ResourceStore
    {
        private const int FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE; // 9: Neutral + Player1..Player8

        // Current balances
        public readonly Fixed[] Ore;
        public readonly Fixed[] Crystal;

        // Supply
        /// <summary>Supply currently consumed per faction (recalculated by SupplySystem each tick).</summary>
        public readonly int[] SupplyUsed;
        /// <summary>Max supply per faction. Increased by buildings; starts at STARTING_SUPPLY_CAP.</summary>
        public readonly int[] SupplyCap;

        public const int STARTING_SUPPLY_CAP = 10;

        // ── Supply config (Story 4.4) ────────────────────────────────────────────
        // Resolved once per scenario-apply via ConfigureSupply (mirrors RevivalRuleRuntime.Configure) — the
        // instance-state home BuildingSystem.RecalculateSupplyCaps reads as its per-tick base/ceiling, and
        // HasSupply reads as its enable gate. Defaults reproduce today's hardcoded behavior exactly.

        /// <summary>Starting supply cap per faction before building bonuses. Defaults to
        /// <see cref="STARTING_SUPPLY_CAP"/> (today's hardcoded value).</summary>
        public int StartingSupplyCap { get; private set; } = STARTING_SUPPLY_CAP;

        /// <summary>Optional hard ceiling <c>SupplyCap</c> is clamped to after building bonuses. <c>null</c> ⇒
        /// uncapped (today's behavior).</summary>
        public int? SupplyHardCeiling { get; private set; } = null;

        /// <summary>Whether the supply gate blocks training once <c>SupplyUsed</c> reaches <c>SupplyCap</c>.
        /// Defaults to <c>true</c> (today's behavior).</summary>
        public bool SupplyGatingEnabled { get; private set; } = true;

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
        /// Story 4.4: resolve <paramref name="config"/> (or compile defaults when null) into
        /// <see cref="StartingSupplyCap"/>/<see cref="SupplyHardCeiling"/>/<see cref="SupplyGatingEnabled"/> — the
        /// single float-free load boundary, called unconditionally from <see cref="ProjectChimera.Core.Sim.ScenarioApplier.Apply"/>
        /// right after <c>RevivalRuntime.Configure</c> (mirrors that unconditional-call idiom: the resolver, not the
        /// call site, owns the null-means-default logic). Never called inside a tick.
        /// </summary>
        public void ConfigureSupply(SupplyConfig? config)
        {
            (StartingSupplyCap, SupplyHardCeiling, SupplyGatingEnabled) = SupplyConfig.Resolve(config);
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): restore this store to its EXACT post-construction state for the Edit↔Play reset —
        /// zero ore/crystal/supply-used/faction-base, then re-seed the ctor's starting ore and P1/P2 supply cap. A
        /// cleared store is byte-for-byte equal to <c>new ResourceStore(_startingOre)</c>. The re-apply's additive
        /// <see cref="AddOre"/>/<see cref="AddCrystal"/> writes require this pre-clear (they append, never overwrite).
        /// Story 4.4: also resets the three supply-config properties to compile defaults (the same defensive-in-depth
        /// idiom as the <c>_startingOre</c> reseed above) so every ClearForReset → Apply cycle reproduces a fresh
        /// load byte-for-byte — no scenario's config may leak into the next (Apply's unconditional ConfigureSupply
        /// call already guarantees this in the tested path; this is belt-and-suspenders for the narrow Clear-without-
        /// a-following-Apply window).
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

            StartingSupplyCap   = STARTING_SUPPLY_CAP;
            SupplyHardCeiling   = null;
            SupplyGatingEnabled = true;
        }

        // ── Convenience methods ────────────────────────────────────────────────

        public void AddOre(Faction faction, Fixed amount) =>
            Ore[(int)faction] = Ore[(int)faction] + amount;

        public bool CanAffordOre(Faction faction, Fixed cost) =>
            Ore[(int)faction] >= cost;

        /// <summary>Story 4.4: gated on <see cref="SupplyGatingEnabled"/> — when the config disables gating, this
        /// returns <c>true</c> unconditionally (training is never supply-blocked), though <c>SupplyCap</c>/
        /// <c>SupplyUsed</c> are still computed, displayed, and folded identically to the enabled case.</summary>
        public bool HasSupply(Faction faction, int cost = 1) =>
            !SupplyGatingEnabled || SupplyUsed[(int)faction] + cost <= SupplyCap[(int)faction];

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
