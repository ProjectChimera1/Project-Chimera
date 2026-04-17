namespace ProjectChimera.Core
{
    /// <summary>
    /// Computes a deterministic FNV-1a checksum over the full simulation world state.
    ///
    /// Used for desync detection in deterministic lockstep multiplayer (P2.4).
    /// Both peers compute this every N ticks and compare; a mismatch indicates divergence.
    ///
    /// Hashed state (in order, ascending entity ID):
    ///   - EntityWorld: Position (X, Y, Z) and Health for every alive entity
    ///   - BuildingStore: Alive flag, Health for every building slot
    ///   - ResourceStore: Ore balance for each faction
    ///
    /// All values are Fixed (int Raw) — platform-independent, no float arithmetic.
    /// </summary>
    public static class SimChecksum
    {
        // FNV-1a 32-bit constants
        private const uint FNV_OFFSET = 2166136261u;
        private const uint FNV_PRIME  = 16777619u;

        /// <summary>
        /// Compute a full-state checksum for desync detection.
        /// Call after all systems have ticked for the current frame.
        /// </summary>
        public static uint Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources)
        {
            uint hash = FNV_OFFSET;

            // ── Entity positions and health ───────────────────────────────────────
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;

                hash = Mix(hash, world.Position[i].X.Raw);
                hash = Mix(hash, world.Position[i].Y.Raw);
                hash = Mix(hash, world.Position[i].Z.Raw);
                hash = Mix(hash, world.Health[i].Raw);
            }

            // ── Building state ────────────────────────────────────────────────────
            int bCount = buildings.Count;
            for (int i = 0; i < bCount; i++)
            {
                hash = Mix(hash, buildings.Alive[i] ? 1 : 0);
                hash = Mix(hash, buildings.Health[i].Raw);
                hash = Mix(hash, buildings.ConstructionTimer[i].Raw);
            }

            // ── Faction resources ─────────────────────────────────────────────────
            // Factions 1 and 2 only (Neutral=0 is unused; 3/4 reserved)
            hash = Mix(hash, resources.Ore[(int)Faction.Player1].Raw);
            hash = Mix(hash, resources.Ore[(int)Faction.Player2].Raw);

            return hash;
        }

        /// <summary>
        /// FNV-1a mix: feed a single int (4 bytes, little-endian) into the hash.
        /// </summary>
        private static uint Mix(uint hash, int value)
        {
            uint v = (uint)value;
            hash ^= v & 0xFF;         hash *= FNV_PRIME;
            hash ^= (v >> 8) & 0xFF;  hash *= FNV_PRIME;
            hash ^= (v >> 16) & 0xFF; hash *= FNV_PRIME;
            hash ^= (v >> 24) & 0xFF; hash *= FNV_PRIME;
            return hash;
        }
    }
}
