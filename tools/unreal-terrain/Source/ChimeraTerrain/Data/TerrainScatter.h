// Project Chimera terrain trial (plan C scatter 3.1-3.5, task S2). Original Chimera code.
// The scatter generator: a pure function of the height bytes, the splat bytes, the palette and the seed. Tile snapshots, tile evaluation,
// records -> instances (BuildInstance), the keyed edit script, record and set hashes, the dirty-tile maths, the scheduler (tile states, the
// Busy rule, dispatch order, the apply budget; executor and clock injected so tests run it with a fake) and a pure model of an ISM's index
// order under the edit script (FFakeIsm). No UObjects; never includes Game/, Render/ or TerrainSimExport.h.
#pragma once

#include "CoreMinimal.h"
#include "HAL/CriticalSection.h"
#include "Misc/ScopeLock.h"
#include "Data/TerrainHeightfield.h"
#include "Data/TerrainScatterTypes.h"
#include "Data/TerrainScatterPalette.h"

namespace ChimeraTerrain
{
	// ---- tiles ------------------------------------------------------------------------------------------------------------

	/** The clipped tile table of a grid for map half extent E and tile size SizeM (origin anchored: tile TX covers [TX*SizeM, (TX+1)*SizeM) metres). */
	FScatterTileGrid MakeScatterTileGrid(int32 HalfExtentM, int32 SizeM);
	FScatterTileKey ScatterTileKeyAt(EScatterGrid Grid, const FScatterTileGrid& TileGrid, int32 Index);

	/** Dirty-tile apron per grid (plan C scatter 3.5): 2.5 m fine, 5 m coarse, in Q16 metres. Covers every read of a candidate (rings, probes, bilinear reach). */
	int64 ScatterApronQ16(EScatterGrid Grid);
	/** Tiles whose candidates can see a change of vertex rect [X0,X1) (world [X0-E-1-A, X1-E+A]). */
	FScatterTileRange ScatterTileRangeForVertexRect(const FScatterTileGrid& TileGrid, int32 HalfExtentM, const FTerrainRect& VertexRect, int64 ApronQ16);
	/** Tiles whose candidates can see a change of splat texel rect [T0,T1) (world [T0/2-E-0.5-A, T1/2-E+0.5+A]). */
	FScatterTileRange ScatterTileRangeForSplatRect(const FScatterTileGrid& TileGrid, int32 HalfExtentM, const FTerrainRect& TexelRect, int64 ApronQ16);
	/** Union of the two ranges as unique tile indices (ascending), clipped to the table. Test and tooling convenience; the scheduler marks ranges directly. */
	void ScatterDirtyTiles(const FScatterTileGrid& TileGrid, EScatterGrid Grid, int32 HalfExtentM, const FTerrainRect& VertexRect, const FTerrainRect& SplatRect, TArray<int32>& OutIndices);

	// ---- snapshots and evaluation -----------------------------------------------------------------------------------------

	/** Game thread: copy the tile's region (plus an apron larger than any read) of heights and splat bytes out of the live heightfield. */
	FScatterSnapshot MakeSnapshot(const FTerrainHeightfield& HF, const FScatterTileKey& Tile);
	/** A snapshot of the whole map (tests; the same reads give the same records as any tile snapshot). */
	FScatterSnapshot MakeFullSnapshot(const FTerrainHeightfield& HF);

	/** The records of one tile, per mesh slot, each array sorted by key. */
	struct FScatterTileRecords
	{
		TArray<FScatterRecord> PerMesh[ScatterMeshCount];
	};

	/** Any thread: every accepted candidate of the tile's grid whose cell lies in the tile (palette level decides whether the near card exists). */
	void EvaluateTile(const FScatterSnapshot& S, const FScatterPalette& P, const FScatterTileKey& Tile, FScatterTileRecords& Out);

	/** The one function from a record to what is shown (transform, up axis, custom data). Doubles live only here. */
	FScatterInstance BuildInstance(const FScatterRecord& R, const FScatterPalette& P);

	/** The z the record's class rule gives on the integer surface of the snapshot (grass: surface - sink; tree: lowest of centre and 4 probes - sink; rock: sink scales with the record's scale). */
	int64 ScatterRecordZRuleQ16(const FScatterSnapshot& S, const FScatterPalette& P, const FScatterRecord& R);

	// ---- fields (exposed for tests, coverage prints and S5's scatter_target) -------------------------------------------------

	enum class EScatterField : uint8
	{
		/** W: woodland before the border bias. */
		Woodland,
		/** W': woodland with the 128-160 m border ring forested (plan C scatter 3.4). */
		WoodlandBiased,
		/** M: dryness. */
		Dryness,
		/** T: tussock islands. */
		Tussock,
		/** F: flower drifts. */
		Drift,
		/** Fc: drift colour. */
		DriftColour,
		/** C: broadleaf vs conifer stands. */
		Stand,
		/** Fs: shrub clumps. */
		ShrubClump,
		/** Fh: which path stretches get hedges. */
		Hedge,
		/** Fr: rare boulders. */
		Boulder,
		Count
	};
	/** A field value 0..65535 (Q16 fraction) at a world position in Q16 metres. */
	int64 ScatterFieldQ16(const FScatterPalette& P, EScatterField Field, int64 XQ, int64 YQ);

	/** K: inside a grove disc? bOutFringe is set when the position lies in the grove's outer fringe ring. */
	bool ScatterInGrove(const FScatterPalette& P, int64 XQ, int64 YQ, bool* bOutFringe = nullptr);

	enum class EScatterTreeZone : uint8
	{
		Core,
		Edge,
		Grove,
		Lone
	};
	/** The tree acceptance zone of a position (the zone with the largest probability; plan C scatter 3.4). */
	EScatterTreeZone ScatterTreeZoneAt(const FScatterPalette& P, int64 XQ, int64 YQ);

	// ---- hashes -----------------------------------------------------------------------------------------------------------

	/** FNV-1a 64 over the fields in record order, each little-endian at its fixed width (never the struct bytes). */
	uint64 HashRecord(const FScatterRecord& R);
	/** Bytes of a record in canonical form: the HashRecord fields in order, each little-endian at its fixed width (scatter_dump's format). */
	constexpr int32 ScatterRecordBytes = 45;
	void AppendScatterRecordBytes(TArray<uint8>& Out, const FScatterRecord& R);
	void ScatterFoldRecord(FScatterHash& H, const FScatterRecord& R);
	void ScatterFoldState(FScatterHash& H, const FScatterUnitState& State);
	void ScatterCombine(FScatterHash& Into, const FScatterHash& Add);
	/** scatter_fnv = Fnv1a64(config_fnv, E, level, the per-class triples in class order). */
	uint64 ScatterFnv(uint64 ConfigFnv, int32 HalfExtentM, EScatterLevel Level, const FScatterHash& H);

	/**
	 * The reference: a synchronous full regeneration of every tile of both grids (tile sizes only change how the work is cut, never the result).
	 * bParallel runs the tiles on the engine's ParallelFor; the result is order independent.
	 */
	FScatterHash ReferenceHash(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 FineTileM = 32, int32 CoarseTileM = 80, bool bParallel = true);
	/** Every record of the reference, sorted by key (tests, dumps). */
	void ReferenceRecords(const FTerrainHeightfield& HF, const FScatterPalette& P, int32 FineTileM, int32 CoarseTileM, TArray<FScatterRecord>& Out);

	// ---- edit script -----------------------------------------------------------------------------------------------------

	/** Execute a script on a record array in ISM order: updates by index, removals descending with swap-remove, appends. The one model of the apply. */
	void ApplyScatterEditScript(TArray<FScatterRecord>& Items, const FScatterEditScript& Script);
	/**
	 * Worker side: merge the key-sorted new records with the unit's applied state into a script, and the unit state the script leads to
	 * (OutNext, the ISM's index order after the script). Unchanged records produce nothing.
	 */
	FScatterEditScript BuildEditScript(const FScatterUnitState& Old, const TArray<FScatterRecord>& NewSorted, TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>& OutNext);

	/** A pure model of one ISM unit under the edit script (index order, remove-at-swap). Tests compare it with a real ISM (Chimera.Terrain.Scatter.EngineIsm). */
	class FFakeIsm
	{
	public:
		TArray<FScatterRecord> Items;

		void Apply(const FScatterEditScript& Script) { ApplyScatterEditScript(Items, Script); }
		/** Replace the content with the state's records (the first fill). */
		void Fill(const FScatterUnitState& State) { Items = State.Records; }
	};

	// ---- scheduler -------------------------------------------------------------------------------------------------------

	/** What a finished tile job returns: one unit work per mesh of the grid. */
	struct FScatterTileResult
	{
		EScatterGrid Grid = EScatterGrid::Fine;
		int32 TileIndex = 0;
		uint32 Gen = 0;
		uint32 Epoch = 0;
		TArray<FScatterUnitWork> Units;
	};

	/** One dispatch decision: the owner snapshots the tile on the game thread and launches the job. */
	struct FScatterDispatch
	{
		EScatterGrid Grid = EScatterGrid::Fine;
		int32 TileIndex = 0;
		uint32 Gen = 0;
		uint32 Epoch = 0;
		bool bMidStroke = false;
		/** ScatterThreads=0: the owner runs this job inline on the game thread and calls PostResult before the next PollResults. */
		bool bGameThread = false;
	};

	/** Counters of plan C scatter 3.8 (results.json "scatter"): every dispatched job ends in exactly one of applied, skipped_identical, discarded_epoch, cancelled. */
	struct FScatterCounters
	{
		int64 Dispatched = 0;
		int64 Applied = 0;
		int64 SkippedIdentical = 0;
		int64 DiscardedEpoch = 0;
		int64 Cancelled = 0;
		int64 MidStrokeDispatches = 0;
		int64 StaleResultRedispatches = 0;
		int32 InFlightMax = 0;
		int32 BusyMax = 0;
		/** Jobs handed out for inline game-thread generation (ScatterThreads=0; never counted in InFlight or InFlightMax). */
		int64 GtGenerations = 0;
		int64 UnitsApplied = 0;
	};

	// SCATTER_FP_BEGIN: the scheduler works in milliseconds (timing and budgets, never records); its file TerrainScatterScheduler.cpp is timing only.
	struct FScatterSchedulerConfig
	{
		int32 HalfExtentM = 160;
		int32 FineTileM = 32;
		int32 CoarseTileM = 80;
		/** Jobs in flight at once (ScatterThreads; 2 default). 0 = the game-thread mode: at most one tile per Dispatch call, generated inline by the owner. */
		int32 MaxInFlight = 2;
		/** Fine tiles during a stroke: at most one dispatch per tile per this many ms (0 = stroke end only). */
		double DuringStrokeMs = 150.0;
		/** Coarse (caster) tiles during a stroke (0 = stroke end only). */
		double CasterDuringStrokeMs = 0.0;
		/** Predicted apply cost of a unit: a + b * changes + c * instances_after (ms; fitted in S4). */
		double PredictA = 0.02;
		double PredictB = 0.0005;
		double PredictC = 0.00002;
	};

	/**
	 * The owner's loop each frame (ATerrainScatter, task S4): OnTerrainChanged -> MarkRects (no allocation); then in TG_PostUpdateWork after the director:
	 * PollResults, ApplyReady(budget, clock, apply) with the engine calls in `apply` (update runs, remove-at-swap, append, SetCustomData), Dispatch(now, brush,
	 * launch) where `launch` takes MakeSnapshot on the game thread and starts a task that runs EvaluateTile + BuildEditScript per mesh unit against
	 * GetUnitState captured at dispatch and ends with PostResult (with MaxInFlight = 0 the dispatch carries bGameThread and `launch` runs the job
	 * inline). Enable and load call BumpEpochAndDirtyAll; disable calls Disable, ClearUnitStates and clears the ISMs, and keeps calling PollResults
	 * (stragglers still in flight are discarded there) until HasPendingWork is false.
	 *
	 * Tile states, the Busy rule and the dispatch order (plan C scatter 3.5). Per tile {DirtyGen, DispatchedGen, AppliedGen, Busy}. A tile is Busy
	 * from dispatch until the last unit of its result has been applied and its unit states swapped; a Busy tile is never dispatched again, and new
	 * dirt only raises its DirtyGen. A tile is current when !Busy && AppliedGen >= DirtyGen. Game thread only, except PostResult.
	 */
	class FScatterScheduler
	{
	public:
		/** Launches the job of a dispatch (owner: snapshot on the game thread, then the task). The job ends by calling PostResult. */
		using FLaunchFn = TFunction<void(const FScatterDispatch&)>;
		/** Applies one unit (the engine calls). Returns nothing; the cost is read from the clock. */
		using FApplyFn = TFunction<void(EScatterGrid Grid, int32 TileIndex, const FScatterUnitWork& Unit)>;
		using FClockFn = TFunction<double()>;

		/** Allocate the tile tables; every tile starts current with empty unit states. */
		void Init(const FScatterSchedulerConfig& InConfig);
		const FScatterSchedulerConfig& GetConfig() const { return Config; }
		const FScatterTileGrid& GetTileGrid(EScatterGrid Grid) const { return TileGrids[static_cast<int32>(Grid)]; }

		/** Mark a tile box dirty (no allocation). */
		void MarkRange(EScatterGrid Grid, const FScatterTileRange& Range);
		void MarkTile(EScatterGrid Grid, int32 TileIndex);
		/**
		 * Mark every tile (both grids) whose candidates can see a change of the given vertex rect and splat texel rect (either may be empty), with each
		 * grid's apron (plan C scatter 3.5). Call it per tick with that tick's own rects, never with a stroke's union box. No allocation.
		 */
		void MarkRects(const FTerrainRect& VertexRect, const FTerrainRect& SplatRect);
		/** The key (grid, tile coordinates, size) of a tile index. */
		FScatterTileKey GetTileKey(EScatterGrid Grid, int32 TileIndex) const { return ScatterTileKeyAt(Grid, TileGrids[static_cast<int32>(Grid)], TileIndex); }
		/** Bump the epoch (results of older epochs are discarded), dirty every tile and (re)enable marking and dispatch: enable, load. */
		void BumpEpochAndDirtyAll();
		/**
		 * Disable (scatter 0): bump the epoch, drop every ready result (each counts as discarded_epoch), mark every tile clean, and ignore marks and
		 * dispatch nothing until BumpEpochAndDirtyAll. A job still in flight keeps its tile Busy until its result is polled and discarded; after
		 * that nothing is pending. The owner clears the unit states (ClearUnitStates) and the ISMs.
		 */
		void Disable();
		bool IsEnabled() const { return bEnabled; }
		/** Teardown: every job still running or waiting counts as cancelled; nothing is dispatched afterwards until Init. */
		void CancelAll();

		void SetStrokeOpen(bool bOpen) { bStrokeOpen = bOpen; }
		bool IsStrokeOpen() const { return bStrokeOpen; }
		uint32 GetEpoch() const { return Epoch; }

		/** Thread-safe: a finished job posts its result; the game thread polls it. */
		void PostResult(FScatterTileResult&& Result);

		/**
		 * Game thread: move posted results into the ready queue (a result of an old epoch is discarded here and its tile stops being Busy).
		 * Returns the number moved.
		 */
		int32 PollResults();

		/**
		 * Dispatch dirty, idle tiles nearest the brush first (ties by index) while jobs in flight < MaxInFlight, honouring the stroke throttles.
		 * Returns the number dispatched.
		 */
		int32 Dispatch(double NowMs, double BrushXM, double BrushYM, const FLaunchFn& Launch);

		/**
		 * Apply ready units under a budget: one whole unit per step, never half a unit. A unit starts only when elapsed + predicted cost <= BudgetMs,
		 * except the first unit of the call, which always runs. BudgetMs <= 0 applies everything ready. Returns the units applied.
		 */
		int32 ApplyReady(double BudgetMs, const FClockFn& Clock, const FApplyFn& Apply);

		/** Dirty or Busy tiles exist, or results wait to be applied. */
		bool HasPendingWork() const;
		int32 NumDirty() const;
		int32 NumBusy() const;
		int32 NumInFlight() const { return InFlight; }
		int32 NumReady() const { return Ready.Num(); }
		bool IsBusy(EScatterGrid Grid, int32 TileIndex) const { return Tiles[static_cast<int32>(Grid)][TileIndex].bBusy; }
		bool IsCurrent(EScatterGrid Grid, int32 TileIndex) const;
		uint32 GetDirtyGen(EScatterGrid Grid, int32 TileIndex) const { return Tiles[static_cast<int32>(Grid)][TileIndex].DirtyGen; }
		uint32 GetAppliedGen(EScatterGrid Grid, int32 TileIndex) const { return Tiles[static_cast<int32>(Grid)][TileIndex].AppliedGen; }

		/** The unit state a tile's mesh unit currently holds (null = empty). Workers read these through the shared pointer. */
		TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe> GetUnitState(EScatterGrid Grid, int32 TileIndex, int32 MeshIndexInGrid) const;
		/** Drop every unit state (disable, load): the units' instances are cleared by the owner. */
		void ClearUnitStates();

		/** The fold of every applied unit state (scatter_live_fnv's input). */
		FScatterHash FoldApplied() const;

		const FScatterCounters& GetCounters() const { return Counters; }
		/** Predicted cost of one unit (ms). */
		double PredictUnitMs(const FScatterUnitWork& Unit) const;

	private:
		struct FTile
		{
			uint32 DirtyGen = 0;
			uint32 DispatchedGen = 0;
			uint32 AppliedGen = 0;
			bool bBusy = false;
			/** A worker job is running or its result was posted and not yet polled (counted in InFlight; never set for game-thread jobs). */
			bool bInFlight = false;
			/** The last applied result was already stale when it was applied (counts as a stale redispatch when the tile is dispatched next). */
			bool bAppliedStale = false;
			double LastDispatchMs = -1.0e18;
		};

		void SetBusyMax();
		void EndResult(FTile& Tile);

		FScatterSchedulerConfig Config;
		FScatterTileGrid TileGrids[ScatterGridCount];
		TArray<FTile> Tiles[ScatterGridCount];
		/** Unit states per grid: [tile * meshesOfGrid + meshIndex]. */
		TArray<TSharedPtr<const FScatterUnitState, ESPMode::ThreadSafe>> States[ScatterGridCount];
		bool bStrokeOpen = false;
		bool bCancelled = false;
		bool bEnabled = true;
		uint32 Epoch = 1;
		uint32 GenCounter = 0;
		int32 InFlight = 0;
		FScatterCounters Counters;

		FCriticalSection PostedLock;
		TArray<FScatterTileResult> Posted;

		/** Ready results (FIFO) and how many units of the front one are already applied. */
		TArray<FScatterTileResult> Ready;
		int32 FrontUnitsApplied = 0;
		bool bFrontAnyChange = false;
	};
	// SCATTER_FP_END
}
