// Project Chimera terrain trial (plan C scatter 3.1-3.8, task S4). Original Chimera code.
// ATerrainScatter: the runtime scatter. Presentation only (plan C scatter 0.3): it reads the terrain's heightfield through snapshots and never
// writes it. Each frame, in TG_PostUpdateWork after the script director: poll finished tile jobs, apply ready units under a predictive budget
// (one whole unit per step), dispatch dirty idle tiles (snapshot on the game thread, EvaluateTile + BuildInstance + BuildEditScript on a
// UE::Tasks worker), and keep the metrics. Terrain changes arrive through ATerrainActor::OnTerrainChanged (task S4a) as dirty tile ranges.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Tasks/Task.h"
#include "Dom/JsonObject.h"
#include "Data/TerrainScatter.h"
#include "TerrainScatterActor.generated.h"

class ATerrainActor;
class UChimeraScatterISM;
class UStaticMesh;
struct FTerrainChange;

/** One applied unit's cost (plan C scatter 4 S4 item 5: apply_unit_ms against instance and change counts; the prediction is fitted on these). */
struct FScatterApplyRow
{
	double Ms = 0.0;
	double PredictedMs = 0.0;
	int32 Changes = 0;
	int32 Updates = 0;
	int32 Removes = 0;
	int32 Appends = 0;
	int32 InstancesAfter = 0;
	uint8 Mesh = 0;
	bool bFill = false;
};

/** One frame of scatter's own cost (scatter_gt_ms, scatter_flush_ms of the previous end of frame, queue depth), with its phase. */
struct FScatterFrameRow
{
	uint64 Frame = 0;
	int32 Phase = 0;
	bool bFill = false;
	double GtMs = 0.0;
	double FlushMs = 0.0;
	int32 FlushCalls = 0;
	int32 Ready = 0;
	int32 InFlight = 0;
	int32 Busy = 0;
	int32 UnitsApplied = 0;
};

/** Time from a change (stroke end, undo, redo, load, enable) until every tile is current and flushed. */
struct FScatterLatencyRow
{
	FString Reason;
	int64 Id = 0;
	uint64 StartFrame = 0;
	double StartSeconds = 0.0;
	int32 Frames = -1;
	double Ms = -1.0;
};

/** A scatter_target result (plan C scatter 3.8): the nearest instance of a class, stored by key, for SHADX's strokes ("@name") and verify's key rows. */
struct FScatterTarget
{
	FString Name;
	uint8 Class = 0;
	uint64 Key = 0;
	/** Metres. */
	double X = 0.0;
	double Y = 0.0;
	double Z = 0.0;
	FString Zone;
};

UCLASS()
class CHIMERATERRAIN_API ATerrainScatter : public AActor
{
	GENERATED_BODY()

public:
	ATerrainScatter();

	/**
	 * Call once after ATerrainActor::InitTerrain (GameMode). Parses nothing itself: the options come from FScatterOptions::FromCommandLine. With
	 * scatter requested it subscribes to the terrain's change event, adds its pending-work provider, binds the editor-compile handlers and
	 * enables (load meshes, create every unit's component, dirty every tile, bump the epoch); otherwise it stays dormant: no subscription, no
	 * provider, no delegate, the actor tick disabled, nothing loaded, the process-wide proxy counters untouched.
	 */
	void Setup(ATerrainActor* InTerrain, const ChimeraTerrain::FScatterOptions& InOptions, bool bRequestedOnCommandLine);
	/** The script director ticks first (plan C scatter 3.1): scatter then sees the director's strokes of the same frame. */
	void SetDirector(AActor* Director);

	/** Was scatter enabled for this run at start (-ChimeraTerrainScatter=1 or the unscripted default)? Scatter ops need it. */
	bool WasRequested() const { return bAvailable; }
	bool IsEnabled() const { return bEnabledNow; }
	int32 NumBusy() const { return bAvailable ? Scheduler.NumBusy() : 0; }
	int32 NumInFlight() const { return bAvailable ? Scheduler.NumInFlight() : 0; }

	/** scatter 1 / scatter 0 (plan C scatter 3.8). */
	void Enable();
	void Disable();

	/** Dirty or Busy tiles, ready results, a unit applied last frame (flush pending), or a scene-relevant component holding instances without a proxy. */
	bool HasPendingWork() const;

	/** One scatter frame (Tick calls it; tests call it directly). */
	void StepFrame();

	/** scatter_visible: show or hide layers (bit per EScatterLayer) by cull distance; no proxy change (plan C scatter 3.5, F6, F18). */
	void SetLayersVisible(uint32 LayerMask, bool bVisible);

	/** Synchronous full regeneration over the live heightfield (ReferenceHash) and the fold of the applied unit states. */
	ChimeraTerrain::FScatterHash ReferenceHashNow() const;
	ChimeraTerrain::FScatterHash LiveHash() const { return Scheduler.FoldApplied(); }
	uint64 ScatterFnvOf(const ChimeraTerrain::FScatterHash& H) const;
	uint64 GetConfigFnv() const;

	/** hash op fields: scatter_fnv, scatter_live_fnv, scatter_count, per-class counts. */
	void AddHashFields(FJsonObject& Out) const;

	/**
	 * scatter_verify (plan C scatter 3.8): live == reference, ISM readback == BuildInstance of the unit states, z and up axis from HF. Targets (scatter_target)
	 * add one row each: whether the stored key still exists and its z change (reported; the parser reads it).
	 */
	TSharedRef<FJsonObject> Verify(const FString& Name, bool& bOutPass, const TArray<FScatterTarget>* Targets = nullptr);

	// ---- scatter S5 ops (plan C scatter 3.8) ----
	/** SHA-256 (lowercase hex) of a byte array: the module's own FIPS 180-4 helper (the director hashes its script with it). */
	static FString Sha256HexOf(const TArray<uint8>& Data);
	const ChimeraTerrain::FScatterPalette& GetPalette() const { return *Palette; }
	/** Every applied record (unit order). */
	void CollectLive(TArray<ChimeraTerrain::FScatterRecord>& Out) const;
	/** The nearest applied instance of a class to (XM, YM); Zone >= 0 keeps only trees of that EScatterTreeZone. */
	bool FindNearest(ChimeraTerrain::EScatterClass Class, double XM, double YM, int32 Zone, ChimeraTerrain::FScatterRecord& Out) const;
	/** scatter_dump: the applied records sorted by key in canonical LE binary to FilePath, plus sha256 and counts. */
	TSharedRef<FJsonObject> DumpApplied(const FString& Name, const FString& FilePath, bool& bOutOk) const;
	/** scatter_check: discs are (x, y, R) metres. */
	TSharedRef<FJsonObject> CheckDiscs(const FString& Name, const FString& SetName, const TArray<FVector>& Discs) const;
	/** scatter_counts: per tile and class counts of the applied states. */
	TSharedRef<FJsonObject> CountsJson() const;
	/** The engine transform BuildInstance gives a record (cm), and the mesh's local bounds (cm; empty box when the slot has no mesh). */
	FTransform InstanceTransformOf(const ChimeraTerrain::FScatterRecord& R) const;
	FBox MeshLocalBoundsCm(ChimeraTerrain::EScatterMesh Mesh) const;
	/** Tile size (metres) of a grid. */
	int32 TileSizeOf(ChimeraTerrain::EScatterGrid Grid) const { return Scheduler.GetTileGrid(Grid).SizeM; }
	/** Cull end of a record's unit with the governor and hidden layers applied, metres (a hidden unit gives 0.01). */
	double CullEndMetersOf(const ChimeraTerrain::FScatterRecord& R) const;
	/** scatter_fresh step 1: raise each live component's expected rebuild and mark its render state dirty (proxies rebuilt from the CPU arrays). */
	int32 RebuildAllProxies();
	/** After a settle: unused expectations are dropped (every expected proxy exists by then). */
	void ClearExpectedRebuilds();

	/** results.json options.scatter and the scatter block. */
	TSharedRef<FJsonObject> OptionsJson() const;
	TSharedRef<FJsonObject> ResultsJson() const;

	/** HUD line: instances, dirty tiles, last apply ms. */
	FString HudLine() const;

	/**
	 * Teardown: cancel, wait on every task handle, drain posted results, restore governor cvars, unbind. Idempotent; EndPlay calls it, and
	 * tests call it before destroying their world on every path. Returns the jobs that were in flight before it.
	 */
	int32 Shutdown();

	virtual void Tick(float DeltaSeconds) override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;
	UPROPERTY(Transient)
	TObjectPtr<ATerrainActor> Terrain;
	/** Units per grid: [tile * meshesOfGrid + meshIndexInGrid]; null where the mesh is missing or under nullrhi. */
	UPROPERTY(Transient)
	TArray<TObjectPtr<UChimeraScatterISM>> FineUnits;
	UPROPERTY(Transient)
	TArray<TObjectPtr<UChimeraScatterISM>> CoarseUnits;
	UPROPERTY(Transient)
	TArray<TObjectPtr<UStaticMesh>> Meshes;

	ChimeraTerrain::FScatterOptions Options;
	TSharedPtr<const ChimeraTerrain::FScatterPalette, ESPMode::ThreadSafe> Palette;
	ChimeraTerrain::FScatterScheduler Scheduler;
	TArray<UE::Tasks::FTask> Tasks;
	TArray<FString> MeshPaths;
	TArray<FString> MeshSha256;
	FString MeshErrors;

	bool bAvailable = false;
	bool bRequested = false;
	bool bEnabledNow = false;
	bool bComponentsCreated = false;
	bool bFill = false;
	bool bAppliedLastFrame = false;
	int32 UnitsAppliedThisFrame = 0;
	uint32 LayerVisibleMask = 0;
	double FillStartSeconds = 0.0;
	double InitMs = -1.0;
	TArray<double> FillMs;
	double LastApplyMs = 0.0;
	int64 Enables = 0;
	int64 Disables = 0;
	int64 Loads = 0;
	int64 EventsSeen[8] = {};
	double MarkUsTotal = 0.0;
	int64 MarkCount = 0;
	double MarkUsMax = 0.0;

	/** Governor cvars this actor set, with their values before (restored at disable and EndPlay). */
	TArray<TPair<FString, FString>> GovernorRestore;
	TArray<TPair<FString, FString>> GovernorReadBack;

	/**
	 * Cap on the per-frame and per-unit rows kept (56 B a frame row; about 55 minutes of frames at 60 fps): a soak (S8) must not grow scatter's
	 * own memory or results.json without bound. Rows past the cap are counted (frame_rows_dropped, apply_rows_dropped), not kept.
	 */
	static constexpr int32 MaxSeriesRows = 200000;
	TArray<FScatterApplyRow> ApplyRows;
	TArray<FScatterFrameRow> FrameRows;
	int64 FrameRowsDropped = 0;
	int64 ApplyRowsDropped = 0;
	bool bShutDown = false;
	TArray<FScatterLatencyRow> Latencies;
	int64 LatencyId = 0;
	mutable FCriticalSection GenLock;
	TArray<double> GenMs;

	UChimeraScatterISM* GetUnit(ChimeraTerrain::EScatterGrid Grid, int32 TileIndex, int32 MeshIndexInGrid) const;
	void OnTerrainChanged(const FTerrainChange& Change);
	/** FEditorSupportDelegates::RedrawAllViewports (editor builds): the shader compiler just propagated finished shader maps to primitives. */
	void OnShaderPropagation();
	/** FAssetCompilingManager::OnAssetPostCompileEvent: a texture or static-mesh compile finished and its users were marked dirty. */
	void OnAssetPostCompile(const TArray<struct FAssetCompileData>& Assets);
	/** Flag every scatter component whose render state is dirty right now as dirtied by an editor compile. */
	void NoteCompilePropagationAll();
	void LoadMeshes();
	void CreateComponents();
	void ApplyGovernorCvars();
	void RestoreGovernorCvars();
	/**
	 * Cull distances (cm) of a mesh unit with the governor applied; hidden units get (0, 1). bIgnoreLayerMask skips only the layer mask (results.json
	 * reports the configured ends whatever the layer visibility at the moment it is written); the governor's hide steps still apply.
	 */
	void UnitCullDistances(ChimeraTerrain::EScatterMesh Mesh, int32& OutStart, int32& OutEnd, bool bIgnoreLayerMask = false) const;
	/** False when the layer mask (unless bIgnoreLayerMask) or a governor hide step (1: GrassT1, 4: Flower) hides the unit. */
	bool IsMeshVisible(ChimeraTerrain::EScatterMesh Mesh, bool bIgnoreLayerMask = false) const;
	void ApplyCullDistancesAll();
	void Launch(const ChimeraTerrain::FScatterDispatch& D);
	void ApplyUnit(ChimeraTerrain::EScatterGrid Grid, int32 TileIndex, const ChimeraTerrain::FScatterUnitWork& Unit);
	void PruneTasks(bool bWaitAll);
	void StartLatency(const TCHAR* Reason);
	bool AnyComponentAwaitingProxy() const;
	int64 TotalInstances() const;
};
