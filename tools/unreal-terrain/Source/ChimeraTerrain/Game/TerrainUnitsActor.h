// Project Chimera terrain trial (plan C 4 C9, plan C scatter 3.10). Original Chimera code.
// ATerrainUnits: the unit layer, a stand-in for the army the sim will drive. One instanced static mesh (crucible_mortar, copied from the look test with
// the same path) with N instances laid out by Data/TerrainUnits, each standing on the DRAWN surface (HF.SampleSurface) and tilted to its slope.
// Units subscribe to ATerrainActor::OnTerrainChanged (S4a) and are re-posed only where the terrain changed: the event marks the vertex rect, the
// actor's tick (after the director) re-evaluates the units that read vertices inside it and pushes exactly those instances with one
// BatchUpdateInstancesTransforms per run of indices, without marking the render state dirty (the proxy is updated in place, as scatter's units are).
// Presentation only: it reads the heightfield and never writes it. Dormant unless -ChimeraTerrainUnits=N with N > 0: nothing is spawned.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Dom/JsonObject.h"
#include "Data/TerrainUnits.h"
#include <atomic>
#include "TerrainUnitsActor.generated.h"

class ATerrainActor;
struct FTerrainChange;

/**
 * The unit layer's one component: a plain ISM that counts its scene-proxy creations and times its end-of-frame instance flush, so results.json can
 * show that terrain edits never rebuilt the proxy (the same two counters scatter's units keep, F5).
 */
UCLASS(ClassGroup = (Rendering))
class CHIMERATERRAIN_API UChimeraUnitsISM : public UInstancedStaticMeshComponent
{
	GENERATED_BODY()

public:
	UChimeraUnitsISM();

	int32 GetProxyCreates() const { return ProxyCreateCount.load(); }
	/** Summed SendRenderInstanceData_Concurrent time and calls since the last TakeFlush. */
	void TakeFlush(double& OutMs, int64& OutCalls);

	//~ UPrimitiveComponent
	virtual FPrimitiveSceneProxy* CreateSceneProxy() override;
	//~ UActorComponent
	virtual void SendRenderInstanceData_Concurrent() override;

private:
	std::atomic<int32> ProxyCreateCount{0};
	std::atomic<uint64> FlushCycles{0};
	std::atomic<int64> FlushCalls{0};
};

UCLASS()
class CHIMERATERRAIN_API ATerrainUnits : public AActor
{
	GENERATED_BODY()

public:
	ATerrainUnits();

	/**
	 * Call once after ATerrainActor::InitTerrain with Count > 0: loads the mesh, lays the units out, creates the component with every instance posed on the
	 * current surface, subscribes to the terrain's change event and adds a pending-work provider (settle, shot and verify wait for unflushed edits).
	 * A mesh that cannot be loaded logs an error and leaves the layer inactive (IsActive() false; the director's ops then fail).
	 */
	void Setup(ATerrainActor* InTerrain, int32 InCount);
	virtual void Tick(float DeltaSeconds) override;
	/** The script director ticks first: the units then see the director's strokes of the same frame. */
	void SetDirector(AActor* Director);

	bool IsActive() const { return bActive; }
	int32 NumUnits() const { return Slots.Num(); }
	/** Marked rects or a whole-grid change not yet pushed to the component. */
	bool HasPendingWork() const { return bAllDirty || PendingRects.Num() > 0; }

	/** One frame of the unit layer (Tick calls it; tests may too): re-pose the units the pending changes can affect and push them. */
	void StepFrame();

	/**
	 * units_verify: every unit's transform recomputed from the heightfield equals the pushed state (exact), the component's instance transforms read
	 * back equal it within float precision, and each unit's z equals HF.SampleSurface at its x, y (the drawn surface). Fails with anything pending.
	 */
	TSharedRef<FJsonObject> Verify(const FString& Name, bool& bOutPass) const;

	/** hash op fields: units_fnv (of the pushed transforms) and units_count. */
	void AddHashFields(FJsonObject& Out) const;
	/** results.json "units" block (counts, flush statistics per phase, proxy creates). */
	TSharedRef<FJsonObject> ResultsJson() const;

	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	UPROPERTY(Transient)
	TObjectPtr<ATerrainActor> Terrain;
	UPROPERTY(Transient)
	TObjectPtr<UChimeraUnitsISM> Ism;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	bool bActive = false;
	TArray<ChimeraTerrain::FUnitSlot> Slots;
	/** What the component holds (the last pushed transform of each unit), in instance order. */
	TArray<FTransform> Pushed;
	/** Vertex rects of the changes since the last flush (a tick's own rects), and the whole-grid flag (Init, Load). */
	TArray<ChimeraTerrain::FTerrainRect> PendingRects;
	bool bAllDirty = false;
	FDelegateHandle ChangedHandle;
	FString MeshPath;
	FString LoadError;

	// ---- metrics ----
	int64 EventsSeen = 0;
	int64 EventsMarked = 0;
	int64 Flushes = 0;
	int64 UnitsUpdated = 0;
	int64 UnitsSkippedIdentical = 0;
	int64 UnitsExamined = 0;
	int32 MaxUpdatedPerFlush = 0;
	int64 FlushesAll = 0;
	double TotalStepMs = 0.0;
	double MaxStepMs = 0.0;
	bool bFirstEditSeen = false;
	int32 ProxyCreatesAtFirstEdit = 0;
	int64 EngineFlushCalls = 0;
	double EngineFlushMs = 0.0;
	double MaxEngineFlushMs = 0.0;
	/** Step cost (ms) of every flush that had work, by phase name (the director's phase label at the time). */
	TMap<FString, TArray<float>> StepMsByPhase;
	/** Units updated per flush by phase. */
	TMap<FString, TArray<int32>> UpdatedByPhase;

	void OnTerrainChanged(const FTerrainChange& Change);
};
