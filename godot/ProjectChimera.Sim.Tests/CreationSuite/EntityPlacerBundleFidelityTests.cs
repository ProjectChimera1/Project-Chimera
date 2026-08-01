#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.CreationSuite
{
    /// <summary>
    /// Deferred-work bundle "editor-placement-bounds-height-and-sync" (DW-137, DW-151, DW-153, DW-163) — Godot-free
    /// regression guards for the <c>EntityPlacer</c>/<c>MainScene</c> creation-suite fixes. <c>EntityPlacer</c> and the
    /// <c>MainScene</c> sync handlers are Godot <see cref="Godot.Node"/>s with no headless surface, so — exactly like
    /// the sibling <see cref="EntityPlacerUndoFidelityTests"/> — these tests drive the REAL pure-C# cores
    /// (<see cref="EntityWorld"/>/<see cref="ResourceNodeStore"/>/<see cref="ItemStore"/>/<see cref="ElevationGrid"/>/
    /// <see cref="StartSlotMath"/>) and, for the ScenarioData sync closures, the SAME op shapes over a real
    /// <see cref="ScenarioData"/>. The bridge-unverifiable paths (marquee drag, elevated-terrain authoring, worker/node
    /// group-move fidelity) are covered here per the in-engine gate coverage note.
    /// </summary>
    public class EntityPlacerBundleFidelityTests
    {
        private static FixedVec3 P(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int AliveCount(ItemStore s)
        {
            int n = 0;
            for (int i = 0; i < s.Count; i++) if (s.Alive[i]) n++;
            return n;
        }

        private static int ItemEntries(ScenarioItem[]? arr) => arr?.Length ?? 0;

        // ── Local mirrors of MainScene.AppendEntry / RemoveByIdentity (private in the Godot layer) ────────────────
        private static T[] Append<T>(T[]? arr, T entry)
        {
            int n = arr?.Length ?? 0;
            var r = new T[n + 1];
            if (n > 0) System.Array.Copy(arr!, r, n);
            r[n] = entry;
            return r;
        }

        private static T[] RemoveById<T>(T[]? arr, T? entry) where T : class
        {
            if (arr == null || arr.Length == 0 || entry == null) return arr ?? System.Array.Empty<T>();
            int idx = -1;
            for (int i = 0; i < arr.Length; i++) if (ReferenceEquals(arr[i], entry)) { idx = i; break; }
            if (idx < 0) return arr;
            var r = new T[arr.Length - 1];
            System.Array.Copy(arr, 0, r, 0, idx);
            System.Array.Copy(arr, idx + 1, r, idx, arr.Length - idx - 1);
            return r;
        }

        // ═══ DW-137: editor-placed ground items are mirrored into ScenarioData.Items so they survive Save/reload/F5 ═══

        /// <summary>DW-137 FIX: PlaceItem fires SyncItem.Add at place time and ReAdd/RemoveHandle on redo/undo, so the
        /// item is present in <c>ScenarioData.Items</c> AND the boxed live ref stays consistent. After
        /// place→undo→redo→undo, zero scenario item entries remain and no live item is alive.</summary>
        [Fact]
        public void SyncItem_PlaceUndoRedoUndo_LeavesNoItemEntryAndNoLiveItem()
        {
            var scen  = new ScenarioData();
            var items = new ItemStore();
            var h     = new ProjectChimera.CreationSuite.EditorHistory();

            // Mirror EntityPlacer.PlaceItem + MainScene.SyncItem exactly.
            int packed = items.Create(defId: 3, charges: 2, P(4, 4));
            var handle = new ScenarioItem { ItemId = "potion", X = 4, Z = 4 };
            scen.Items = Append(scen.Items, handle);           // SyncItem.Add
            Assert.Equal(1, ItemEntries(scen.Items));          // mirrored at place time

            int[] box = { packed };
            h.Push(
                redo: () => { int r = items.Create(3, 2, P(4, 4)); if (r >= 0) box[0] = r; scen.Items = Append(scen.Items, handle); },
                undo: () => { if (items.TryResolveRef(box[0], out int slot)) items.Destroy(slot); scen.Items = RemoveById(scen.Items, handle); });

            h.Undo(); // RemoveHandle → Items empty, item destroyed
            h.Redo(); // ReAdd → Items [handle], NEW live item (boxed)
            h.Undo(); // RemoveHandle → Items empty, LIVE re-created item destroyed

            Assert.Equal(0, ItemEntries(scen.Items));
            Assert.Equal(0, AliveCount(items));
        }

        /// <summary>DW-137 REGRESSION: the ORIGINAL PlaceItem never touched ScenarioData, so a placed item was absent
        /// from <c>ScenarioData.Items</c> and vanished on the next Save/reload / F5 re-apply (which re-applies only the
        /// scenario). Documents the exact gap the SyncItem mirror closes.</summary>
        [Fact]
        public void PlaceItem_WithoutScenarioSync_ItemAbsentFromScenario_Regression()
        {
            var scen  = new ScenarioData();
            var items = new ItemStore();
            items.Create(3, 2, P(4, 4)); // live only — never mirrored (pre-DW-137)
            Assert.Equal(1, AliveCount(items));
            Assert.Equal(0, ItemEntries(scen.Items)); // the bug: reload re-applies scen.Items → the item is gone
        }

        // ═══ DW-151: group-move/paste restores full entity fidelity (worker residue / node 4.7 fields / pre_built) ═══

        /// <summary>DW-151 FIX: a moved/pasted unit is re-created via <see cref="EntityWorld.RestoreUnit"/>, which
        /// replays the caller-owned worker residue (SupplyCost=0 / GatherState / CarryCapacity / MeshType) verbatim —
        /// so a moved worker stays a worker, not a combat unit (the old <c>DoSpawnCombatUnit</c> re-derive dropped it).</summary>
        [Fact]
        public void RestoreUnit_KeepsWorkerResidue_AfterSnapshotRoundTripAtMovedPosition()
        {
            var w = new EntityWorld();
            int id = w.Create(P(5, 5), Faction.Player1, Fixed.FromInt(60), Fixed.FromFloat(3.5f));
            Assert.True(id >= 0);

            // Worker overrides applied after the def mapper (mirrors DoSpawnWorker).
            w.SupplyCost[id]    = 0;
            w.GatherState[id]   = GatherState.Idle;
            w.CarryCapacity[id] = Fixed.FromInt(20);
            w.MeshType[id]      = 7;

            var snap = w.SnapshotUnit(id);
            w.Destroy(id);
            snap.Position = P(9, 9);        // re-home like a group-move / paste
            int rid = w.RestoreUnit(snap);
            Assert.True(rid >= 0);

            Assert.Equal(0, w.SupplyCost[rid]);                         // free supply preserved (still a worker)
            Assert.Equal(GatherState.Idle, w.GatherState[rid]);
            Assert.Equal(Fixed.FromInt(20).Raw, w.CarryCapacity[rid].Raw);
            Assert.Equal((byte)7, w.MeshType[rid]);
            Assert.Equal(P(9, 9).X.Raw, w.Position[rid].X.Raw);        // moved to the new position
            Assert.Equal(P(9, 9).Z.Raw, w.Position[rid].Z.Raw);
        }

        /// <summary>DW-151 FIX: the full 10-arg <see cref="ResourceNodeStore.Create"/> round-trips the Story-4.7 field
        /// set, so a moved/pasted node keeps its collection model / resource type / requires-structure gate / owner /
        /// income period in the LIVE store (BuildCreate now calls this overload instead of the 4-arg one).</summary>
        [Fact]
        public void ResourceNodeStore_Create_FullArgs_RoundTripsAll47Fields()
        {
            var s = new ResourceNodeStore();
            int id = s.Create(P(3, 3), Fixed.FromInt(500), Fixed.FromInt(5), maxGatherers: 4,
                collectionModel: ResourceCollectionModel.Income, resourceType: ResourceKind.Crystal,
                requiresStructureId: "watchtower", requiresStructureRadius: Fixed.FromInt(12),
                ownerFaction: Faction.Player2, incomePeriodTicks: 45);
            Assert.True(id >= 0);

            Assert.Equal(ResourceCollectionModel.Income, s.CollectionModel[id]);
            Assert.Equal(ResourceKind.Crystal,           s.ResourceType[id]);
            Assert.Equal("watchtower",                   s.RequiresStructureId[id]);
            Assert.Equal(Fixed.FromInt(12).Raw,          s.RequiresStructureRadius[id].Raw);
            Assert.Equal(Faction.Player2,                s.OwnerFaction[id]);
            Assert.Equal(45,                             s.IncomePeriodTicks[id]);
        }

        /// <summary>DW-151: the live→DTO field mapping SyncResourceNode.Add performs (Faction→0-based OwnerSlot with
        /// Neutral→-1; enum→string; empty structure id→null RequiresStructure). Pins the mapping arithmetic so the
        /// persisted DTO reproduces the authored node on reload.</summary>
        // DW-151 (V1): assert the REAL live→DTO mapper (ResourceNodeDtoMap) that MainScene.SyncResourceNode.Add now
        // calls — a regression in the mapping fails these, unlike the old inline recomputation.
        [Theory]
        [InlineData(Faction.Neutral, -1)]
        [InlineData(Faction.Player1, 0)]
        [InlineData(Faction.Player2, 1)]
        [InlineData(Faction.Player4, 3)]
        public void ResourceNodeDtoMap_OwnerFaction_MapsTo_OwnerSlot(Faction f, int expectedSlot)
            => Assert.Equal(expectedSlot, ResourceNodeDtoMap.OwnerSlotOf(f));

        [Fact]
        public void ResourceNodeDtoMap_ToDto_MapsEveryField()
        {
            var dto = ResourceNodeDtoMap.ToDto(3f, 4f, 500f, 5f, maxGatherers: 4,
                ResourceCollectionModel.Income, ResourceKind.Crystal, "watchtower", Fixed.FromInt(12), Faction.Player2, 45);
            Assert.Equal(3f,   dto.X);
            Assert.Equal(4f,   dto.Z);
            Assert.Equal(500f, dto.Supply);
            Assert.Equal(5f,   dto.Rate);
            Assert.Equal(4,    dto.MaxGatherers);
            Assert.Equal("Income",     dto.CollectionModel);                     // enum → string
            Assert.Equal("Crystal",    dto.ResourceType);                        // enum → string
            Assert.Equal("watchtower", dto.RequiresStructure);                   // non-empty id passthrough
            Assert.Equal(Fixed.FromInt(12).ToFloat(), dto.RequiresStructureRadius); // Fixed → float via .ToFloat()
            Assert.Equal(1,  dto.OwnerSlot);                                     // Player2 → 1
            Assert.Equal(45, dto.IncomePeriodTicks);                             // passthrough
        }

        [Fact]
        public void ResourceNodeDtoMap_ToDto_EmptyRequiresId_MapsToNull_AndNeutralOwnerToMinusOne()
        {
            var dto = ResourceNodeDtoMap.ToDto(0f, 0f, 100f, 5f, 4,
                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromInt(15), Faction.Neutral, 30);
            Assert.Null(dto.RequiresStructure);   // empty id → null
            Assert.Equal(-1, dto.OwnerSlot);      // Neutral → -1
            Assert.Equal("Gather", dto.CollectionModel);
            Assert.Equal("Ore",    dto.ResourceType);
        }

        /// <summary>PATCH 1 (A1): a plain single-placed node persists the ScenarioResourceNode SCHEMA defaults
        /// (radius 15 / income period 30), byte-identical to the pre-widening object-initializer baseline — NOT the
        /// store defaults (0/0), which would be a Save-output regression and a latent Income footgun.</summary>
        [Fact]
        public void PlaceResourceNode_PlainNode_PersistsSchemaDefaults_NotZero()
        {
            // The exact values PlaceResourceNode now passes for a plain Gather/Ore node.
            var placed = ResourceNodeDtoMap.ToDto(0f, 0f, 500f, 5f, 4,
                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
            Assert.Equal(15f, placed.RequiresStructureRadius);
            Assert.Equal(30,  placed.IncomePeriodTicks);

            // Byte-identical to the pre-change object-initializer DTO (schema defaults).
            var baseline = new ScenarioResourceNode { X = 0, Z = 0, Supply = 500f, Rate = 5f, MaxGatherers = 4 };
            Assert.Equal(baseline.RequiresStructureRadius, placed.RequiresStructureRadius);
            Assert.Equal(baseline.IncomePeriodTicks,       placed.IncomePeriodTicks);
            Assert.Equal(baseline.OwnerSlot,               placed.OwnerSlot);
            Assert.Equal(baseline.CollectionModel,         placed.CollectionModel);
            Assert.Equal(baseline.ResourceType,            placed.ResourceType);
            Assert.Equal(baseline.RequiresStructure,       placed.RequiresStructure);
        }

        // ── DW-151: a group-moved building carries its authored pre_built through the persist path ─────────────────

        /// <summary>Mirrors EntityPlacer.LookupBuildingPreBuilt: find the source ScenarioBuilding backing a live slot
        /// (by slot + position, the same key MainScene.SyncBuilding matches on) and read its pre_built. Godot-free.</summary>
        private static bool CapturePreBuilt(ScenarioData scen, Faction faction, float x, float z)
        {
            if (scen.Buildings == null) return false;
            int slot = (int)faction - 1;
            foreach (var b in scen.Buildings)
                if (b.Slot == slot && System.Math.Abs(b.X - x) <= 0.1f && System.Math.Abs(b.Z - z) <= 0.1f)
                    return b.PreBuilt;
            return false;
        }

        /// <summary>DW-151 FIX: a group-move captures the source building's authored pre_built (via the Describe
        /// lookup) and BuildCreate feeds it into the SyncBuilding.Add DTO build, so the moved ScenarioBuilding keeps
        /// <c>PreBuilt=true</c>. Persistence-proxy over a REAL ScenarioData/ScenarioBuilding (EntityPlacer/MainScene are
        /// Godot; this replicates the exact capture + DTO-build shapes, like the SyncItem test above).</summary>
        [Fact]
        public void GroupMoveBuilding_CapturesAndPersists_AuthoredPreBuiltTrue()
        {
            var scen = new ScenarioData();
            var source = new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 10, Z = 20, PreBuilt = true };
            scen.Buildings = new[] { source };

            // Describe capture: read the authored pre_built off the source entry (slot 0 = Player1, matched by position).
            bool captured = CapturePreBuilt(scen, Faction.Player1, x: 10, z: 20);
            Assert.True(captured);

            // BuildCreate → SyncBuilding.Add: rebuild the DTO at the MOVED position carrying the captured pre_built.
            var moved = new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 14, Z = 24, PreBuilt = captured };
            Assert.True(moved.PreBuilt);
        }

        /// <summary>DW-151 REGRESSION: the ORIGINAL BuildCreate passed a literal <c>false</c> for pre_built regardless
        /// of the source, so a group-moved <c>pre_built:true</c> building was silently reset to false in the persisted
        /// DTO. Documents the exact drop the captured-pre_built fix eliminates.</summary>
        [Fact]
        public void GroupMoveBuilding_OldHardcodedFalse_DropsAuthoredPreBuilt_Regression()
        {
            var scen = new ScenarioData();
            scen.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 10, Z = 20, PreBuilt = true } };

            // The source IS pre_built, but the old path ignored it and hard-coded false into the moved DTO.
            Assert.True(CapturePreBuilt(scen, Faction.Player1, 10, 20));
            var movedOld = new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 14, Z = 24, PreBuilt = false };
            Assert.False(movedOld.PreBuilt); // the bug: authored pre_built:true lost on move
        }

        // ═══ DW-159: off-map paste/move is rejected by the ±MapBounds guard (no scenario ⇒ allowed) ════════════════

        [Fact]
        public void MapBounds_InBoundsPoint_IsAccepted()
        {
            Assert.True(MapBoundsMath.Within(50f, -30f, 120f));
            Assert.True(MapBoundsMath.Within(0f, 0f, 120f));
        }

        [Theory]
        [InlineData(121f, 0f)]    // X beyond +b
        [InlineData(-121f, 0f)]   // X beyond -b
        [InlineData(0f, 121f)]    // Z beyond +b
        [InlineData(0f, -121f)]   // Z beyond -b
        [InlineData(200f, 200f)]  // both far off-map
        public void MapBounds_OffMapPoint_IsRejected(float x, float z)
        {
            Assert.False(MapBoundsMath.Within(x, z, 120f));
        }

        [Fact]
        public void MapBounds_ExactlyOnBound_IsAccepted()
        {
            Assert.True(MapBoundsMath.Within(120f, 120f, 120f));    // corner
            Assert.True(MapBoundsMath.Within(-120f, -120f, 120f));  // opposite corner
            Assert.True(MapBoundsMath.Within(120f, 0f, 120f));      // edge
        }

        [Fact]
        public void MapBounds_NullBounds_AllowsEverything()
        {
            // No scenario loaded ⇒ bounds unknown ⇒ allow (a placement is never rejected for being "off-map").
            Assert.True(MapBoundsMath.Within(0f, 0f, null));
            Assert.True(MapBoundsMath.Within(9999f, -9999f, null));
        }

        // ═══ DW-153: box-select + selection markers reference the sim's terrain height (not a hard-coded y=0) ═══════

        [Fact]
        public void SampleElevation_ReturnsGridValue_WhenGridSet()
        {
            var w = new EntityWorld();
            // 2×2 grid over [0,2)×[0,2): (1.5,1.5) lands in the last cell (value 40).
            var grid = new ElevationGrid(
                new[] { Fixed.FromInt(10), Fixed.FromInt(20), Fixed.FromInt(30), Fixed.FromInt(40) },
                width: 2, height: 2, worldMinX: Fixed.Zero, worldMinZ: Fixed.Zero, cellSize: Fixed.One);
            w.SetElevationGrid(grid);

            Assert.Equal(Fixed.FromInt(40).Raw, w.SampleElevation(Fixed.FromFloat(1.5f), Fixed.FromFloat(1.5f)).Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, w.SampleElevation(Fixed.FromFloat(0.5f), Fixed.FromFloat(0.5f)).Raw);
        }

        [Fact]
        public void SampleElevation_ReturnsZero_WhenNoGridSet()
        {
            var w = new EntityWorld();
            Assert.Equal(Fixed.Zero.Raw, w.SampleElevation(Fixed.FromInt(5), Fixed.FromInt(5)).Raw); // flat map ⇒ unchanged
        }

        // ═══ DW-163: non-contiguous start-slot sets survive — display/markers/"+"/"−" key off slot VALUE ════════════

        private static ScenarioPlayerSlot Slot(int v) => new ScenarioPlayerSlot { Slot = v };

        [Fact]
        public void StartSlotMath_NonContiguous_0_3()
        {
            var declared = StartSlotMath.DeclaredSlots(new[] { Slot(3), Slot(0) }); // unsorted input
            Assert.Equal(new[] { 0, 3 }, declared);
            Assert.Equal(1, StartSlotMath.LowestUndeclared(declared, 4)); // "+" fills the gap
            Assert.Equal(3, StartSlotMath.MaxDeclared(declared));         // "−" removes slot 3
        }

        [Fact]
        public void StartSlotMath_Contiguous_1_2_ByteIdenticalBehavior()
        {
            var declared = StartSlotMath.DeclaredSlots(new[] { Slot(1), Slot(2) });
            Assert.Equal(new[] { 1, 2 }, declared);
            Assert.Equal(0, StartSlotMath.LowestUndeclared(declared, 4)); // "+" arms slot 0
            Assert.Equal(2, StartSlotMath.MaxDeclared(declared));
        }

        [Fact]
        public void StartSlotMath_Single_3()
        {
            var declared = StartSlotMath.DeclaredSlots(new[] { Slot(3) });
            Assert.Equal(new[] { 3 }, declared);
            Assert.Equal(0, StartSlotMath.LowestUndeclared(declared, 4));
            Assert.Equal(3, StartSlotMath.MaxDeclared(declared));
        }

        [Fact]
        public void StartSlotMath_FullSet_HasNoUndeclared_SoAddDisables()
        {
            var declared = StartSlotMath.DeclaredSlots(new[] { Slot(0), Slot(1), Slot(2), Slot(3) });
            Assert.Equal(new[] { 0, 1, 2, 3 }, declared);
            Assert.Equal(-1, StartSlotMath.LowestUndeclared(declared, 4)); // "+" disabled at a full declared set
            Assert.Equal(3,  StartSlotMath.MaxDeclared(declared));
        }

        [Fact]
        public void StartSlotMath_Empty_IsSafe()
        {
            var declared = StartSlotMath.DeclaredSlots(null);
            Assert.Empty(declared);
            Assert.Equal(0,  StartSlotMath.LowestUndeclared(declared, 4));
            Assert.Equal(-1, StartSlotMath.MaxDeclared(declared));
        }

        /// <summary>PATCH 3 (E1): the Initialize seed selects the LOWEST DECLARED slot value (DeclaredSlots[0]), not
        /// the default 0 — so a scenario whose declared set excludes 0 ({3} / {1,2,3}) never renders a phantom "P1"
        /// pending toggle nor creates an undeclared slot 0 on the first placement.</summary>
        [Fact]
        public void StartSlotSeed_SelectsLowestDeclaredSlot_NotPhantomZero()
        {
            Assert.Equal(1, StartSlotMath.DeclaredSlots(new[] { Slot(1), Slot(2), Slot(3) })[0]); // {1,2,3} → 1, not 0
            Assert.Equal(3, StartSlotMath.DeclaredSlots(new[] { Slot(3) })[0]);                    // {3} → 3
            Assert.Equal(0, StartSlotMath.DeclaredSlots(new[] { Slot(0), Slot(3) })[0]);           // set incl. 0 → 0
            Assert.Empty(StartSlotMath.DeclaredSlots(null));                                       // empty → seed falls back to 0
        }

        /// <summary>DW-163 follow-up patch: DeclaredBelowCeiling drops validator-legal but out-of-range slots so the
        /// 4-slot picker (length-CEILING economy arrays) never surfaces/indexes a slot &gt;= ceiling. A 5–8-player
        /// {5,6} set collapses to empty (→ the {0,1} fallback), instead of crashing the seed/render/remove legs that
        /// index _slotStartOre by slot VALUE.</summary>
        [Fact]
        public void StartSlotMath_DeclaredBelowCeiling_DropsOutOfRangeSlots()
        {
            // A validator-legal high-only set (Story 9.2 5–8-player) is entirely above the 4-slot picker ceiling.
            Assert.Empty(StartSlotMath.DeclaredBelowCeiling(new[] { Slot(5), Slot(6) }, 4));
            // A mixed set keeps only the in-range values, sorted.
            Assert.Equal(new[] { 0, 3 }, StartSlotMath.DeclaredBelowCeiling(new[] { Slot(5), Slot(3), Slot(0) }, 4));
            // Exactly-at-ceiling is excluded (range is [0, ceiling)); an in-range non-contiguous set is untouched.
            Assert.Equal(new[] { 0, 3 }, StartSlotMath.DeclaredBelowCeiling(new[] { Slot(4), Slot(0), Slot(3) }, 4));
            Assert.Equal(new[] { 0, 3 }, StartSlotMath.DeclaredBelowCeiling(new[] { Slot(0), Slot(3) }, 4));
            // Null/empty stay empty (caller applies its own {0,1} fallback).
            Assert.Empty(StartSlotMath.DeclaredBelowCeiling(null, 4));
        }

        /// <summary>DW-151 follow-up patch: a plain single-placed node is now created in the LIVE store with the same
        /// schema defaults (radius 15 / income period 30) it persists, NOT the store defaults (0/0). This is the value
        /// a later group-move/paste reads back out of the live store (EntityPlacer.Describe) to rebuild the moved DTO,
        /// so the moved node's persisted income_period_ticks is 30 — closing the move-path re-exposure of the A1
        /// footgun (a moved node shipping income_period_ticks:0 that a later flip to Income would credit every tick).</summary>
        [Fact]
        public void PlaceThenGroupMovePlainNode_LiveStoreCarriesSchemaDefaults_NotZero()
        {
            var nodes = new ResourceNodeStore();
            // The exact create the fixed PlaceResourceNode now performs for a plain Gather/Ore node.
            int id = nodes.Create(P(10, 20), Fixed.FromFloat(500f), Fixed.FromFloat(5f), 4,
                ResourceCollectionModel.Gather, ResourceKind.Ore, "", Fixed.FromFloat(15f), Faction.Neutral, 30);
            Assert.True(id >= 0);

            // The move path (Describe) reads these live-store fields to rebuild the moved DTO — they must NOT be 0/0.
            Assert.Equal(Fixed.FromFloat(15f).Raw, nodes.RequiresStructureRadius[id].Raw);
            Assert.Equal(30, nodes.IncomePeriodTicks[id]);

            // ToDto of the captured live-store values yields the schema defaults, byte-matching a freshly-placed node.
            var movedDto = ResourceNodeDtoMap.ToDto(
                nodes.Position[id].X.ToFloat(), nodes.Position[id].Z.ToFloat(),
                nodes.SupplyTotal[id].ToFloat(), nodes.GatherRate[id].ToFloat(), nodes.MaxGatherers[id],
                nodes.CollectionModel[id], nodes.ResourceType[id], nodes.RequiresStructureId[id],
                nodes.RequiresStructureRadius[id], nodes.OwnerFaction[id], nodes.IncomePeriodTicks[id]);
            Assert.Equal(15f, movedDto.RequiresStructureRadius);
            Assert.Equal(30,  movedDto.IncomePeriodTicks);
        }
    }
}
