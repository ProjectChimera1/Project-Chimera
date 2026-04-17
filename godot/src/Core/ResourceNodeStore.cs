namespace ProjectChimera.Core
{
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
        public readonly Fixed[]      GatherRate;        // Ore per second per assigned gatherer
        public readonly int[]        MaxGatherers;
        public readonly int[]        AssignedGatherers; // Workers currently at this node

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
        }

        /// <summary>
        /// Create a new resource node. Returns the node index, or -1 if full.
        /// </summary>
        public int Create(FixedVec3 position, Fixed supply, Fixed gatherRate, int maxGatherers)
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
            return id;
        }
    }
}
