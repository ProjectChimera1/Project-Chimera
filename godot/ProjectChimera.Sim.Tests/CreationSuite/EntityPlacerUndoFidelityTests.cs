#nullable enable
using ProjectChimera.Core;
using ProjectChimera.CreationSuite;
using Xunit;

namespace ProjectChimera.Sim.Tests.CreationSuite
{
    /// <summary>
    /// Deferred-work bundle "editor-placement-undo-history-fidelity" (DW-35, DW-173) — Godot-free regression guards for
    /// two <c>EntityPlacer</c> undo/redo closures. <c>EntityPlacer</c> is a Godot <see cref="Godot.Node"/> with no
    /// headless surface (its live behaviour is covered by the in-engine gate), so these tests drive the SAME closure
    /// shapes over the REAL pure-C# stores (<see cref="ItemStore"/>/<see cref="BuildingStore"/>) and the REAL
    /// <see cref="EditorHistory"/> — exactly as <see cref="EditorHistoryTests"/> pins the LIFO contract with synthetic
    /// closures. Each fix is paired with a "…_Regression" test that reproduces the ORIGINAL bug, so the guard has teeth
    /// (it fails if the fix is reverted).
    /// </summary>
    public class EntityPlacerUndoFidelityTests
    {
        private static FixedVec3 P(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int AliveCount(ItemStore s)
        {
            int n = 0;
            for (int i = 0; i < s.Count; i++) if (s.Alive[i]) n++;
            return n;
        }

        // ── DW-35: PlaceItem place→undo→redo→undo must not leak the redone ground item ──────────────────────────────

        /// <summary>DW-35 FIX: the redo closure captures its NEW packed ref in a box, so the paired undo destroys the
        /// LIVE re-created instance. After place→undo→redo→undo no ground item remains alive.</summary>
        [Fact]
        public void PlaceItem_BoxCapturedRedoRef_PlaceUndoRedoUndo_LeavesNoLeak()
        {
            var items = new ItemStore();
            var h     = new EditorHistory();

            // Mirror EntityPlacer.PlaceItem's fixed closure exactly.
            int   packed = items.Create(defId: 3, charges: 2, P(4, 4));
            int[] box    = { packed };
            h.Push(
                redo: () => { int r = items.Create(3, 2, P(4, 4)); if (r >= 0) box[0] = r; },
                undo: () => { if (items.TryResolveRef(box[0], out int slot)) items.Destroy(slot); });

            h.Undo(); // destroy the original
            h.Redo(); // re-create → NEW ref, captured into box[0]
            h.Undo(); // must destroy the LIVE re-created instance, not the dead original ref

            Assert.Equal(0, AliveCount(items));
        }

        /// <summary>DW-35 REGRESSION: the ORIGINAL closure discarded the redo's new ref and always resolved the
        /// now-dead original — so the final undo no-ops on a stale ref and the redone item leaks. Documents the exact
        /// defect the box fix eliminates (this test would be the failing state if the fix were reverted).</summary>
        [Fact]
        public void PlaceItem_NaiveClosureCapturingOriginalRef_LeaksRedoneItem_Regression()
        {
            var items = new ItemStore();
            var h     = new EditorHistory();

            int packed = items.Create(3, 2, P(4, 4));
            h.Push(
                redo: () => items.Create(3, 2, P(4, 4)),                                   // new ref discarded
                undo: () => { if (items.TryResolveRef(packed, out int slot)) items.Destroy(slot); }); // always the original

            h.Undo(); // destroys the original
            h.Redo(); // creates a NEW instance; its ref is thrown away
            h.Undo(); // resolves the DEAD original (stale ref → clean no-op) → the redone instance survives

            Assert.Equal(1, AliveCount(items)); // the leak the DW-35 fix removes
        }

        // ── DW-173: group-move undo of a building must restore its OWN def-derived stats, not the recycled occupant's ─

        // The full def-derived stat set BuildDeleteBuilding captures at delete time (mirrors the diff).
        private readonly struct BStats
        {
            public readonly Fixed Health, MaxHealth, ShopRadius;
            public readonly int SupplyBonus;
            public readonly bool Revives, Sells;
            public readonly string[] ShopStock;
            public BStats(Fixed h, Fixed mh, int sup, bool rev, bool sell, string[] stock, Fixed rad)
            { Health = h; MaxHealth = mh; SupplyBonus = sup; Revives = rev; Sells = sell; ShopStock = stock; ShopRadius = rad; }
        }

        private static BStats Capture(BuildingStore b, int id) =>
            new BStats(b.Health[id], b.MaxHealth[id], b.SupplyBonus[id], b.RevivesHeroes[id],
                       b.SellsItems[id], b.ShopStock[id], b.ShopRadius[id]);

        private static void RestoreFull(BuildingStore b, int id, in BStats s)
        {
            b.Alive[id]         = true;
            b.Health[id]        = s.Health;
            b.MaxHealth[id]     = s.MaxHealth;
            b.SupplyBonus[id]   = s.SupplyBonus;
            b.RevivesHeroes[id] = s.Revives;
            b.SellsItems[id]    = s.Sells;
            b.ShopStock[id]     = s.ShopStock;
            b.ShopRadius[id]    = s.ShopRadius;
        }

        // Two buildings with DISTINCT def-derived stats: A = CommandCenter (500 hp, +10 supply, no shop);
        // B = a Custom shop (250 hp, +3 supply, sells items).
        private static int CreateA(BuildingStore b, int x) =>
            b.Create(P(x, 0), Faction.Player1, BuildingType.CommandCenter);

        private static int CreateB(BuildingStore b, int x) =>
            b.Create(P(x, 0), Faction.Player1, BuildingType.Custom,
                     sellsItems: true, shopStock: new[] { "potion" }, shopRadius: Fixed.FromInt(5),
                     buildingId: "watchtower", health: Fixed.FromFloat(250f), supplyBonus: 3,
                     constructionDuration: Fixed.FromFloat(8f));

        /// <summary>DW-173 FIX: on a group-move the two slots are deleted then re-created cross-wise through the LIFO
        /// free-list, so each slot ends up carrying the OTHER building's stats. The fixed undo restores the full
        /// captured stat set, so each building comes back with its OWN Health/SupplyBonus/shop — no cross-contamination.</summary>
        [Fact]
        public void BuildDeleteBuildingUndo_FullStatRestore_RecycledSlot_KeepsEachBuildingsOwnStats()
        {
            var b = new BuildingStore();
            int a  = CreateA(b, 0);   // slot 0
            int bb = CreateB(b, 10);  // slot 1

            var capA = Capture(b, a);
            var capB = Capture(b, bb);

            // MoveSelection order: delete A, delete B (free-list LIFO top = B), then create moved copies cross-wise.
            b.Destroy(a);
            b.Destroy(bb);
            int movedA = CreateA(b, 1);   // pops B's slot (1) → slot 1 now carries A's stats
            int movedB = CreateB(b, 11);  // pops A's slot (0) → slot 0 now carries B's stats
            Assert.Equal(bb, movedA);
            Assert.Equal(a,  movedB);
            // Residue trap precondition: slot a holds B-shaped supply, slot b holds A-shaped supply.
            Assert.Equal(3,  b.SupplyBonus[a]);
            Assert.Equal(10, b.SupplyBonus[bb]);

            // Undo: reverse the creates, then the deletes with the DW-173 full restore.
            b.Destroy(movedB);
            b.Destroy(movedA);
            RestoreFull(b, bb, capB);
            RestoreFull(b, a,  capA);

            // A resurrected as its own CommandCenter; B as its own shop — no leakage of the recycled occupant's stats.
            Assert.Equal(10, b.SupplyBonus[a]);
            Assert.Equal(Fixed.FromFloat(500f).Raw, b.Health[a].Raw);
            Assert.False(b.SellsItems[a]);

            Assert.Equal(3, b.SupplyBonus[bb]);
            Assert.Equal(Fixed.FromFloat(250f).Raw, b.Health[bb].Raw);
            Assert.True(b.SellsItems[bb]);
            Assert.Equal(new[] { "potion" }, b.ShopStock[bb]);
        }

        /// <summary>DW-173 REGRESSION: the ORIGINAL undo restored identity/timers only. After the same cross-recycle,
        /// resurrecting via <c>Alive[id]=true</c> + identity alone leaves each building carrying the recycled occupant's
        /// def-derived stats — the exact stale-stat bug DW-173 closes.</summary>
        [Fact]
        public void BuildDeleteBuildingUndo_IdentityOnlyRestore_LeavesRecycledOccupantStats_Regression()
        {
            var b = new BuildingStore();
            int a  = CreateA(b, 0);
            int bb = CreateB(b, 10);

            b.Destroy(a);
            b.Destroy(bb);
            int movedA = CreateA(b, 1);   // slot bb
            int movedB = CreateB(b, 11);  // slot a
            b.Destroy(movedB);
            b.Destroy(movedA);

            // OLD undo: identity/timers only — NOT the def-derived stat set.
            b.Alive[bb] = true; b.Type[bb] = BuildingType.Custom;        b.DefinitionId[bb] = "watchtower";
            b.Alive[a]  = true; b.Type[a]  = BuildingType.CommandCenter; b.DefinitionId[a]  = "command_center";

            // Contamination: slot a still carries B's +3 supply and slot bb still carries A's +10 (create residue).
            Assert.Equal(3,  b.SupplyBonus[a]);
            Assert.Equal(10, b.SupplyBonus[bb]);
        }
    }
}
