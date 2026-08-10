#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Fog of War simulation system.
    ///
    /// Maintains a 128×128 byte grid covering the playable area.
    /// Cell states:
    ///   0 = Unexplored  (black)
    ///   1 = Explored    (dark tint — seen before, not currently visible)
    ///   2 = Visible     (fully lit — at least one friendly unit has line of sight)
    ///
    /// Each tick:
    ///   1. Demote all Visible → Explored.
    ///   2. For each alive unit of the viewer faction, stamp a circle of radius VisionRange as Visible.
    ///
    /// The grid is exposed as a public byte array; FogOfWarBridge uploads it to GPU.
    ///
    /// World bounds: X ∈ [-128, +128], Z ∈ [-128, +128]  →  256×256 world units total.
    /// Each cell = 2 world units wide.
    /// </summary>
    public class FogOfWarSystem : ISimSystem
    {
        // ── Grid configuration ────────────────────────────────────────────────

        public const int   GRID_SIZE        = 128;
        public const float WORLD_HALF_EXTENT = 128f; // world coords: -128 … +128
        public const float CELL_SIZE         = WORLD_HALF_EXTENT * 2f / GRID_SIZE; // = 2.0

        public const byte UNEXPLORED = 0;
        public const byte EXPLORED   = 1;
        public const byte VISIBLE    = 2;

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Row-major [row * GRID_SIZE + col] byte array of cell states.
        /// Uploaded to GPU by FogOfWarBridge each frame.
        /// </summary>
        public readonly byte[] Grid = new byte[GRID_SIZE * GRID_SIZE];

        /// <summary>Which faction this fog instance reveals for. Retargetable per-client via <see cref="SetViewer"/>.</summary>
        private Faction _faction;

        /// <summary>Story 9.14 — the sim-owned alliance mask, so shared-team vision can union allied units' sight.
        /// Optional (null ⇒ no allied faction, single-viewer fog exactly as pre-9.14).</summary>
        private readonly AllianceStore? _alliances;

        /// <summary>Story 9.14 — when true (default) AND an alliance mask is present, the local fog UNIONS the vision of
        /// every ALLIED faction's units (a teammate's scouted area is revealed on your fog). When false, only the
        /// viewer's own faction lights the fog. Presentation-only — the fog Grid is NOT folded into
        /// <c>SimChecksum</c>, so a per-client shared-vision setting can NEVER desync the sim.</summary>
        public bool SharedTeamVision { get; set; } = true;

        public FogOfWarSystem(Faction faction = Faction.Player1, AllianceStore? alliances = null)
        {
            _faction   = faction;
            _alliances = alliances;
        }

        /// <summary>
        /// Story 9.5: retarget which faction this per-client fog reveals for. Called at match start
        /// (<c>MatchLifecycleController.OnMatchStart</c>) with the server-assigned local faction so a client in slot
        /// Player2..Player8 sees its OWN units' vision instead of Player1's. Presentation-only — the fog Grid is read
        /// solely by the minimap/fog bridges and is NOT folded into <c>SimChecksum</c>, so a per-client differing fog
        /// is correct RTS behaviour. Pure enum-field assignment (no float/Dictionary/Random) — stays determinism-safe.
        /// </summary>
        public void SetViewer(Faction faction) => _faction = faction;

        /// <summary>
        /// True when <paramref name="f"/>'s entities are the VIEWER'S OWN to see unconditionally — the viewer's
        /// faction, or (with shared-team vision and a mask present) an allied one. Anything else is an enemy whose
        /// entities must be occluded by the fog.
        ///
        /// <para>DW-920: this is the single predicate for "whose stuff do I see". It was previously inlined in the
        /// vision-stamp loop only, so the fog knew who to reveal FOR but no renderer knew who to hide — enemy units
        /// and buildings drew at full brightness through unexplored fog, which is why a player could watch the
        /// opponent's whole build order. Presentation-only (the Grid is unfolded), so using it in a renderer can
        /// never affect <c>SimChecksum</c>.</para>
        /// </summary>
        public bool RevealsFaction(Faction f) =>
            f == _faction
            || (SharedTeamVision && _alliances != null && _alliances.AreAllied(_faction, f));

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            // Step 1: demote Visible → Explored
            for (int i = 0; i < Grid.Length; i++)
                if (Grid[i] == VISIBLE) Grid[i] = EXPLORED;

            // Step 2: stamp vision circles for each alive friendly unit
            int cap = world.HighWaterMark;
            for (int id = 0; id < cap; id++)
            {
                if ((world.Flags[id] & EntityFlags.Alive) == 0) continue;
                // Story 9.14: stamp the viewer's own faction always; additionally stamp ALLIED factions' units when
                // shared-team vision is enabled and a mask is present (union of teammate sight). Null mask / toggle off
                // ⇒ only the own-faction path, byte-identical to pre-9.14. Presentation-only (unfolded), so this NEVER
                // affects any SimChecksum.
                if (!RevealsFaction(world.FactionOf[id])) continue;

                float wx = world.Position[id].X.ToFloat();
                float wz = world.Position[id].Z.ToFloat();
                // Story 6.3: the height-advantage bonus is computed entirely in Fixed and merged into the base
                // VisionRange INSIDE EffectiveVisionRange, BEFORE this single .ToFloat() boundary — so the StampCircle
                // float math below is the unchanged, verified-not-rewritten path. Toggle OFF ⇒ this equals
                // VisionRange[id] exactly, so the stamped Grid is byte-for-byte identical to pre-feature.
                float radius = world.EffectiveVisionRange(id).ToFloat();

                StampCircle(wx, wz, radius);
            }
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): restore the fog to its post-construction state for the Edit↔Play reset — every cell
        /// back to <see cref="UNEXPLORED"/> (a fresh <see cref="Grid"/> is all-zero). Not folded into SimChecksum, but
        /// the reset must return the board — including visibility — to the authored start. Pure array wipe.
        /// </summary>
        public void Reset() => System.Array.Clear(Grid);

        // ── Helpers ───────────────────────────────────────────────────────────

        private void StampCircle(float worldX, float worldZ, float radius)
        {
            // Convert world-space centre to grid coords
            float cx = (worldX + WORLD_HALF_EXTENT) / CELL_SIZE;
            float cz = (worldZ + WORLD_HALF_EXTENT) / CELL_SIZE;

            int cellRadius = (int)(radius / CELL_SIZE) + 1;
            int minRow = System.Math.Max(0, (int)(cz - cellRadius));
            int maxRow = System.Math.Min(GRID_SIZE - 1, (int)(cz + cellRadius));
            int minCol = System.Math.Max(0, (int)(cx - cellRadius));
            int maxCol = System.Math.Min(GRID_SIZE - 1, (int)(cx + cellRadius));

            float radiusSqr = (radius / CELL_SIZE) * (radius / CELL_SIZE);

            for (int row = minRow; row <= maxRow; row++)
            {
                float dz = row + 0.5f - cz;
                float dzSqr = dz * dz;
                if (dzSqr > radiusSqr) continue;

                float dxMax = System.MathF.Sqrt(radiusSqr - dzSqr);
                int colStart = System.Math.Max(minCol, (int)(cx - dxMax));
                int colEnd   = System.Math.Min(maxCol, (int)(cx + dxMax));

                for (int col = colStart; col <= colEnd; col++)
                    Grid[row * GRID_SIZE + col] = VISIBLE;
            }
        }

        /// <summary>Returns the grid cell (col, row) for a given world position.</summary>
        public static (int col, int row) WorldToCell(float worldX, float worldZ)
        {
            int col = (int)((worldX + WORLD_HALF_EXTENT) / CELL_SIZE);
            int row = (int)((worldZ + WORLD_HALF_EXTENT) / CELL_SIZE);
            col = System.Math.Clamp(col, 0, GRID_SIZE - 1);
            row = System.Math.Clamp(row, 0, GRID_SIZE - 1);
            return (col, row);
        }

        /// <summary>Returns true if the given world position is currently visible.</summary>
        public bool IsVisible(float worldX, float worldZ)
        {
            var (col, row) = WorldToCell(worldX, worldZ);
            return Grid[row * GRID_SIZE + col] == VISIBLE;
        }

        /// <summary>Returns true if the cell has been explored (seen at some point).</summary>
        public bool IsExplored(float worldX, float worldZ)
        {
            var (col, row) = WorldToCell(worldX, worldZ);
            return Grid[row * GRID_SIZE + col] >= EXPLORED;
        }
    }
}
