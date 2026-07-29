namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 4.7 — the per-node collection model. GATHER (default) is today's sole behavior: a worker carries a
    /// load round-trip to the faction base. Income credits a flat amount every <see cref="ResourceNodeStore.IncomePeriodTicks"/>
    /// ticks with zero assigned workers (<c>GatheringSystem.FindBestNode</c> always skips it). Streaming behaves
    /// like GATHER for worker assignment/extraction but credits the gathering worker's faction directly at the
    /// node each tick — no carry, no base trip.
    /// </summary>
    public enum ResourceCollectionModel
    {
        Gather    = 0,
        Income    = 1,
        Streaming = 2,
    }

    /// <summary>
    /// Story 4.7 — which per-faction balance a node's credit routes to (<see cref="ResourceStore.AddOre"/> vs
    /// <see cref="ResourceStore.AddCrystal"/>).
    /// </summary>
    public enum ResourceKind
    {
        Ore     = 0,
        Crystal = 1,
    }

    /// <summary>
    /// Stores all resource deposit nodes on the map.
    /// Separate from EntityWorld — nodes are map objects, not combat entities.
    /// Max 64 nodes per map; allocation-free after setup.
    /// </summary>
    public class ResourceNodeStore
    {
        public const int MAX_NODES = 64;

        public readonly bool[]       Active;
        public readonly FixedVec3[]  Position;
        public readonly Fixed[]      SupplyRemaining;
        public readonly Fixed[]      SupplyTotal;       // For visual scale: remaining / total
        public readonly Fixed[]      GatherRate;        // Ore per second per assigned gatherer (Income: amount per period — see ScenarioResourceNode.Rate)
        public readonly int[]        MaxGatherers;
        public readonly int[]        AssignedGatherers; // Workers currently at this node

        // ── Story 4.7: per-resource collection models + requires_structure gate + Crystal production ─────────
        /// <summary>The collection model this node uses. Default <see cref="ResourceCollectionModel.Gather"/> —
        /// every pre-4.7 <see cref="Create"/> call (the 4-arg legacy signature) reproduces today's node exactly.</summary>
        public readonly ResourceCollectionModel[] CollectionModel;
        /// <summary>Which per-faction balance this node's credit routes to. Default <see cref="ResourceKind.Ore"/>.</summary>
        public readonly ResourceKind[]            ResourceType;
        /// <summary>The <c>BuildingStore.DefinitionId</c> a faction must own within <see cref="RequiresStructureRadius"/>
        /// of this node before it becomes eligible. Empty string ("", the <see cref="Create"/> default) ⇒ no gate.
        /// Never null after <see cref="Create"/> (the SoA-recycle contract — mirrors <c>BuildingStore.DefinitionId</c>).</summary>
        public readonly string[]                  RequiresStructureId;
        /// <summary>World-unit radius of the <see cref="RequiresStructureId"/> proximity check. Only consulted
        /// when <see cref="RequiresStructureId"/> is non-empty.</summary>
        public readonly Fixed[]                   RequiresStructureRadius;
        /// <summary>The faction this node's Income credits belong to (resolved from <c>ScenarioResourceNode.OwnerSlot</c>
        /// at scenario-apply). Default <see cref="Faction.Neutral"/> — inert for GATHER/Streaming, which credit the
        /// gathering worker's own faction; consulted only by the Income tick pass and (for an Income node) the
        /// requires_structure gate.</summary>
        public readonly Faction[]                 OwnerFaction;
        /// <summary>Whole simulation ticks between Income credits. Only consulted when <see cref="CollectionModel"/>
        /// is <see cref="ResourceCollectionModel.Income"/>. Default 0 (inert for every other model).</summary>
        public readonly int[]                     IncomePeriodTicks;
        /// <summary>MUTABLE — whole ticks elapsed since this Income node's last credit (or since <see cref="Create"/>).
        /// Never dt-accumulated, never wall-clock. Folded into <see cref="SimChecksum"/> (v13) — the first mutable
        /// per-node state.</summary>
        public readonly int[]                     IncomeTicksElapsed;

        private int _count;

        /// <summary>Number of nodes created (includes depleted ones).</summary>
        public int Count => _count;

        public ResourceNodeStore()
        {
            Active             = new bool[MAX_NODES];
            Position           = new FixedVec3[MAX_NODES];
            SupplyRemaining    = new Fixed[MAX_NODES];
            SupplyTotal        = new Fixed[MAX_NODES];
            GatherRate         = new Fixed[MAX_NODES];
            MaxGatherers       = new int[MAX_NODES];
            AssignedGatherers  = new int[MAX_NODES];

            CollectionModel         = new ResourceCollectionModel[MAX_NODES];
            ResourceType            = new ResourceKind[MAX_NODES];
            RequiresStructureId     = new string[MAX_NODES];
            RequiresStructureRadius = new Fixed[MAX_NODES];
            OwnerFaction            = new Faction[MAX_NODES];
            IncomePeriodTicks       = new int[MAX_NODES];
            IncomeTicksElapsed      = new int[MAX_NODES];
        }

        /// <summary>
        /// Create a new resource node. Returns the node index, or -1 if full.
        ///
        /// Story 4.7 appends 6 optional trailing params (all defaulted to reproduce today's GATHER node exactly
        /// when omitted — the 4-arg legacy call sites across the codebase/tests are unaffected).
        /// </summary>
        public int Create(FixedVec3 position, Fixed supply, Fixed gatherRate, int maxGatherers,
            ResourceCollectionModel collectionModel = ResourceCollectionModel.Gather,
            ResourceKind resourceType = ResourceKind.Ore,
            string? requiresStructureId = null,
            // Defaults to 0, NOT ScenarioResourceNode's 15f schema default (a compile-time-constant default param
            // can't call Fixed.FromFloat) — safe because it's only ever consulted when requiresStructureId is also
            // non-empty (StructureGateOpen short-circuits "no id -> no gate" before reading this), and the sole
            // caller (ScenarioApplier) always passes the DTO's resolved value explicitly either way.
            Fixed requiresStructureRadius = default,
            Faction ownerFaction = Faction.Neutral,
            int incomePeriodTicks = 0)
        {
            if (_count >= MAX_NODES) return -1;
            int id = _count++;

            Active[id]            = true;
            Position[id]          = position;
            SupplyRemaining[id]   = supply;
            SupplyTotal[id]       = supply;
            GatherRate[id]        = gatherRate;
            MaxGatherers[id]      = maxGatherers;
            AssignedGatherers[id] = 0;

            CollectionModel[id]         = collectionModel;
            ResourceType[id]            = resourceType;
            RequiresStructureId[id]     = requiresStructureId ?? "";
            RequiresStructureRadius[id] = requiresStructureRadius;
            OwnerFaction[id]            = ownerFaction;
            IncomePeriodTicks[id]       = incomePeriodTicks;
            IncomeTicksElapsed[id]      = 0;
            return id;
        }

        /// <summary>
        /// Story 11.3 (SP save/load): restore the private <see cref="Count"/> after the persistence layer has written
        /// the SoA arrays directly (nodes are append-only, no free-list). Godot-free integer bookkeeping only.
        /// </summary>
        public void RestoreCount(int count) => _count = count < 0 ? 0 : (count > MAX_NODES ? MAX_NODES : count);

        /// <summary>
        /// Story 3.10 (UX-DR62): restore this store to its EXACT post-construction state for the Edit↔Play reset —
        /// zero every SoA array and reset <see cref="Count"/> to 0. A cleared store is byte-for-byte equal to
        /// <c>new ResourceNodeStore()</c>. The re-apply's <see cref="Create"/> loop re-adds the authored nodes.
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(Active);
            System.Array.Clear(Position);
            System.Array.Clear(SupplyRemaining);
            System.Array.Clear(SupplyTotal);
            System.Array.Clear(GatherRate);
            System.Array.Clear(MaxGatherers);
            System.Array.Clear(AssignedGatherers);

            System.Array.Clear(CollectionModel);
            System.Array.Clear(ResourceType);
            System.Array.Clear(RequiresStructureId);
            System.Array.Clear(RequiresStructureRadius);
            System.Array.Clear(OwnerFaction);
            System.Array.Clear(IncomePeriodTicks);
            System.Array.Clear(IncomeTicksElapsed);
            _count = 0;
        }
    }
}
