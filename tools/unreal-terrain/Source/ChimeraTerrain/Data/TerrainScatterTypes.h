// Project Chimera terrain trial (plan C scatter 3.1, task S2). Original Chimera code.
// Scatter data types: classes, meshes, tiles, the integer-only record, the immutable snapshot a worker reads, the keyed edit script and the
// per-unit state. Pure C++ (no UObjects, no Game/ or Render/ includes): the generator is a pure function of the height bytes, the splat
// bytes, the palette and the seed (plan C scatter 0.2).
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	/** The two tile grids: fine (grass layer, 32 m default) and coarse (trees, shrubs, ferns, rocks, 80 m default). */
	enum class EScatterGrid : uint8
	{
		Fine = 0,
		Coarse = 1
	};
	constexpr int32 ScatterGridCount = 2;

	/** Candidate classes (plan C scatter 3.4). The numeric value is stored in a record and hashed; never reorder, only append. */
	enum class EScatterClass : uint8
	{
		Grass = 0,
		Tussock,
		Flower,
		NearCard,
		Tree,
		Sapling,
		Shrub,
		Fern,
		Rock,
		Count
	};
	constexpr int32 ScatterClassCount = static_cast<int32>(EScatterClass::Count);

	/** Visibility layers (plan C scatter 3.4): grass = T0, T1, near card; groundcover = tussock, flower, fern; shrubs; trees = tree + sapling; rocks. */
	enum class EScatterLayer : uint8
	{
		Grass = 0,
		Groundcover,
		Shrubs,
		Trees,
		Rocks,
		Count
	};
	constexpr int32 ScatterLayerCount = static_cast<int32>(EScatterLayer::Count);

	/**
	 * Mesh slots: one ISM unit per (tile, mesh). The numeric value is stored in a record and hashed; never reorder, only append.
	 * Fine grid: GrassT0..NearCard (5). Coarse grid: TreeBroadA..RockB (9). 100 x 5 + 16 x 9 = 644 primitives at E = 160 (plan C scatter 3.1).
	 */
	enum class EScatterMesh : uint8
	{
		GrassT0 = 0,
		GrassT1,
		Tussock,
		Flower,
		NearCard,
		TreeBroadA,
		TreeBroadB,
		TreeConiferA,
		TreeConiferB,
		ShrubA,
		ShrubB,
		Fern,
		RockA,
		RockB,
		Count
	};
	constexpr int32 ScatterMeshCount = static_cast<int32>(EScatterMesh::Count);

	/** Asset level (plan C scatter 3.6): L0 procedural (no near card), L1 the Poly Haven candidates. */
	enum class EScatterLevel : uint8
	{
		L0 = 0,
		L1 = 1
	};

	EScatterGrid ScatterGridOfClass(EScatterClass Class);
	EScatterGrid ScatterGridOfMesh(EScatterMesh Mesh);
	EScatterLayer ScatterLayerOfClass(EScatterClass Class);
	/** ANSI names (stable, hashed into config_fnv): "Grass", "GrassT0", "grass" for layers. */
	const char* ScatterClassName(EScatterClass Class);
	const char* ScatterMeshName(EScatterMesh Mesh);
	const char* ScatterLayerName(EScatterLayer Layer);
	/** The meshes of one grid in slot order (fine: 5, coarse: 9). */
	void ScatterMeshesOfGrid(EScatterGrid Grid, TArray<EScatterMesh>& Out);
	/** Index of a mesh inside its grid's unit list (0-based). */
	int32 ScatterMeshIndexInGrid(EScatterMesh Mesh);
	/** Number of mesh units per tile of a grid. */
	int32 ScatterMeshCountOfGrid(EScatterGrid Grid);
	/** Whether a mesh slot exists at this level (L0 has no near card). */
	bool ScatterMeshExistsAtLevel(EScatterMesh Mesh, EScatterLevel Level);

	/** 64-bit candidate id (FScatterRecord::Key): stream, slot and the cell; unique per candidate, independent of the mesh it currently shows. */
	using FScatterKey = uint64;

	/** Tile coordinates on the origin-anchored lattice of one grid (tile (TX, TY) covers metres [TX*SizeM, (TX+1)*SizeM) x [TY*SizeM, ...)). */
	struct FScatterTileKey
	{
		EScatterGrid Grid = EScatterGrid::Fine;
		int32 TX = 0;
		int32 TY = 0;
		int32 SizeM = 32;

		bool operator==(const FScatterTileKey& O) const { return Grid == O.Grid && TX == O.TX && TY == O.TY && SizeM == O.SizeM; }
	};

	/** The clipped tile table of one grid: tile indices TXMin..TXMin+Count-1 per axis (row-major index = (TY - Min) * Count + (TX - Min)). */
	struct FScatterTileGrid
	{
		int32 SizeM = 32;
		int32 Min = 0;
		int32 Count = 0;

		int32 NumTiles() const { return Count * Count; }
		int32 IndexOf(int32 TX, int32 TY) const { return (TY - Min) * Count + (TX - Min); }
		int32 TXOf(int32 Index) const { return Min + Index % Count; }
		int32 TYOf(int32 Index) const { return Min + Index / Count; }
	};

	/** Inclusive tile box. Empty when MaxX < MinX or MaxY < MinY. */
	struct FScatterTileRange
	{
		int32 MinX = 0;
		int32 MinY = 0;
		int32 MaxX = -1;
		int32 MaxY = -1;

		bool IsEmpty() const { return MaxX < MinX || MaxY < MinY; }
	};

	/**
	 * One scatter instance, integers only (plan C scatter 3.5). xq, yq, zq are Q16 metres from the map centre; gxq, gyq the cell-triangle
	 * gradient in Q16 (HQ16 difference per metre); yaw is a turn fraction in 1/65536; scale and zscale are Q12 multipliers; tilt is the hashed
	 * tilt fraction (0..65535 of the class maximum); cd0 and cd1 are the custom-data words (dryness / variation, tint / flower colour / stand).
	 */
	struct FScatterRecord
	{
		uint8 Class = 0;
		uint8 Mesh = 0;
		uint16 Stream = 0;
		int32 IX = 0;
		int32 IY = 0;
		uint8 Slot = 0;
		int32 XQ = 0;
		int32 YQ = 0;
		int32 ZQ = 0;
		int32 GXQ = 0;
		int32 GYQ = 0;
		uint16 Yaw = 0;
		uint16 Scale = 0;
		uint16 ZScale = 0;
		uint16 Tilt = 0;
		uint16 CD0 = 0;
		uint16 CD1 = 0;

		/** Field-wise equality (never memcmp: struct padding is indeterminate). */
		bool operator==(const FScatterRecord& O) const
		{
			return Class == O.Class && Mesh == O.Mesh && Stream == O.Stream && IX == O.IX && IY == O.IY && Slot == O.Slot && XQ == O.XQ && YQ == O.YQ
				&& ZQ == O.ZQ && GXQ == O.GXQ && GYQ == O.GYQ && Yaw == O.Yaw && Scale == O.Scale && ZScale == O.ZScale && Tilt == O.Tilt && CD0 == O.CD0
				&& CD1 == O.CD1;
		}
		bool operator!=(const FScatterRecord& O) const { return !(*this == O); }

		/** 64-bit candidate id: stream, slot and the cell. Unique per candidate, independent of the mesh the candidate currently shows. */
		uint64 Key() const
		{
			return (static_cast<uint64>(Stream) << 48) | (static_cast<uint64>(Slot) << 40)
				| ((static_cast<uint64>(static_cast<uint32>(IX + 524288)) & 0xFFFFFull) << 20) | (static_cast<uint64>(static_cast<uint32>(IY + 524288)) & 0xFFFFFull);
		}
	};

	/**
	 * The immutable input of one tile generation: the tile's region of quantised heights (HQ16 = round(h * 65536)) and splat bytes, plus an
	 * apron, copied from the live heightfield on the game thread (memcpy) and read by any worker. Reads clamp to the MAP first (the
	 * heightfield's own edge behaviour) and then to the stored region; a read outside the stored region counts a miss (a test expects zero).
	 */
	struct FScatterSnapshot
	{
		/** Half extent E of the map (metres). */
		int32 HalfExtentM = 0;
		/** Vertices per side (2E + 1) and splat texels per side (4E) of the whole map. */
		int32 Verts = 0;
		int32 SplatTexels = 0;
		/** Stored vertex region and its heights (HQ16, row-major over VRect). */
		FTerrainRect VRect;
		TArray<int32> HQ16;
		/** Stored splat texel region and its RGBA8 bytes (row-major over TRect). */
		FTerrainRect TRect;
		TArray<uint8> Splat;

		/** HQ16 of vertex (VX, VY), indices clamped to the map. */
		int32 HeightAt(int32 VX, int32 VY) const;
		/** The 4 bytes of splat texel (TX, TY), indices clamped to the map (null never returned). */
		const uint8* SplatAt(int32 TX, int32 TY) const;
	};

	/** Thread-local count of snapshot reads that fell outside the stored region (tests read it on the generating thread). */
	int32& ScatterSnapshotMisses();

	/** One entry of a unit's key-sorted view: key and its index in the ISM order. */
	struct FScatterKeyIndex
	{
		uint64 Key = 0;
		int32 Index = 0;
	};

	/** What one unit (tile, mesh) holds: records in the ISM's index order plus a key-sorted view. Immutable once published. */
	struct FScatterUnitState
	{
		TArray<FScatterRecord> Records;
		TArray<FScatterKeyIndex> Sorted;

		/** Rebuild Sorted from Records (keys are unique per unit). */
		void RebuildSorted();
		/** Index of the record with this key, or INDEX_NONE. */
		int32 Find(uint64 Key) const;
	};

	/** One in-place update of an existing instance. */
	struct FScatterUpdate
	{
		int32 Index = 0;
		FScatterRecord Record;
	};

	/**
	 * The keyed edit script of one unit (plan C scatter 3.5). Executed in this order on the old index space: updates by index; removals in
	 * strictly descending index order with remove-at-swap; appends at the end in key order.
	 */
	struct FScatterEditScript
	{
		TArray<FScatterUpdate> Updates;
		/** Old indices to remove, strictly descending. */
		TArray<int32> Removes;
		TArray<FScatterRecord> Appends;

		bool IsEmpty() const { return Updates.Num() == 0 && Removes.Num() == 0 && Appends.Num() == 0; }
		int32 Changes() const { return Updates.Num() + Removes.Num() + Appends.Num(); }
	};

	/** The worker's result for one unit: the script and the unit state it leads to (ISM order after the script). */
	struct FScatterUnitWork
	{
		uint8 Mesh = 0;
		FScatterEditScript Script;
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> Next;
		/** Instances in the unit before the script (for the apply cost prediction). */
		int32 InstancesBefore = 0;
		int32 InstancesAfter() const { return Next.IsValid() ? Next->Records.Num() : 0; }
	};

	/** Per-class fold of the record hashes: count, sum mod 2^64 and xor (order independent). */
	struct FScatterClassHash
	{
		int64 Count = 0;
		uint64 Sum = 0;
		uint64 Xor = 0;
	};

	/** The per-class hash triples of a whole record set. */
	struct FScatterHash
	{
		FScatterClassHash Classes[ScatterClassCount];

		int64 TotalCount() const;
		bool operator==(const FScatterHash& O) const;
	};

	/**
	 * A built instance: what is shown for a record (plan C scatter 3.5). Metres and radians; the renderer converts to cm. The only source of
	 * transforms and custom data. Doubles live here only (not in any accept, select, position or Z decision).
	 */
	// SCATTER_FP_BEGIN: the output of BuildInstance (what is shown), never an input of a decision.
	struct FScatterInstance
	{
		double Pos[3] = {0, 0, 0};
		/** Rotation quaternion (x, y, z, w). */
		double Quat[4] = {0, 0, 0, 1};
		double Scale[3] = {1, 1, 1};
		/** Unit up axis after alignment, tilt (before yaw about it). */
		double Up[3] = {0, 0, 1};
		float Custom[4] = {0, 0, 0, 0};
		/** 4 on the fine grid, 2 on the coarse grid. */
		int32 NumCustom = 0;
	};
	// SCATTER_FP_END
}
