// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 2.4, 3.3: one RMC component per terrain chunk. It owns its bounds (RMC's in-place path never refreshes them) and counts
// scene-proxy recreations (each one after the first is an RMC fallback publish).
#pragma once

#include "CoreMinimal.h"
#include "RealtimeMeshComponent.h"
#include "TerrainChunkComponent.generated.h"

/**
 * A terrain chunk. Bounds = the chunk's XY footprint x [ZLo, ZHi] metres, kept from the CPU height array by the renderer:
 * exact range +-2 m after a full recompute; widened with 16 m headroom when a stroke tick moves a vertex outside it.
 * Each change is pushed to the scene with MarkRenderTransformDirty() (ActorComponent.h:1134), whose SendRenderTransform_Concurrent
 * calls UpdateBounds() and UpdatePrimitiveTransform (PrimitiveComponent.cpp:655-664).
 */
UCLASS(ClassGroup = (Rendering), meta = (BlueprintSpawnableComponent))
class CHIMERATERRAIN_API UTerrainChunkComponent : public URealtimeMeshComponent
{
	GENERATED_BODY()

public:
	UTerrainChunkComponent();

	/** Margin added around the exact Z range (metres). */
	static constexpr float ExactMarginM = 2.0f;
	/** Headroom added when a stroke tick widens the range (metres). */
	static constexpr float WidenHeadroomM = 16.0f;

	/** Chunk id and its XY footprint in Unreal cm (render rect corners). */
	void SetFootprint(int32 InChunkId, const FVector2D& MinCm, const FVector2D& MaxCm);

	/** Exact range from the CPU array: bounds become [MinZ - 2, MaxZ + 2] m. Pushes to the scene. */
	void SetExactZRange(float MinZM, float MaxZM);

	/**
	 * A tick wrote heights spanning [MinZM, MaxZM] into this chunk. Widens the tracked range (with headroom) only when a value left
	 * it. Returns true when the bounds changed (and were pushed).
	 */
	bool NotifyTickZRange(float MinZM, float MaxZM);

	float GetTrackedZLo() const { return ZLoM; }
	float GetTrackedZHi() const { return ZHiM; }
	int32 GetChunkId() const { return ChunkId; }
	bool HasTrackedBounds() const { return bHasRange; }

	/** Scene proxies created by this component so far. */
	int32 GetProxyCreates() const { return ProxyCreates; }
	/** XY centre of the footprint (cm, Z = 0). */
	FVector GetFootprintCenterCm() const { return FVector(0.5 * (FootMinCm.X + FootMaxCm.X), 0.5 * (FootMinCm.Y + FootMaxCm.Y), 0.0); }

	/**
	 * Seconds spent in OnDestroyPhysicsState + OnCreatePhysicsState: the game-thread part of applying a new collision body (plan C 3.3;
	 * RMC's body broadcast calls RecreatePhysicsState, RealtimeMeshComponent.cpp:392-403). Measured in C5.
	 */
	double GetPhysicsStateSeconds() const { return PhysicsStateSeconds; }
	int32 GetPhysicsStateCreates() const { return PhysicsStateCreates; }

	//~ USceneComponent
	virtual FBoxSphereBounds CalcBounds(const FTransform& LocalToWorld) const override;
	//~ UPrimitiveComponent
	virtual FPrimitiveSceneProxy* CreateSceneProxy() override;

	/** Global count of proxy creations made while any terrain stroke was open (set by the renderer). */
	static int64 ProxyCreatesDuringStrokes;
	static bool bStrokeOpen;

	//~ UActorComponent (public in UPrimitiveComponent, PrimitiveComponent.h:2553-2554)
	virtual void OnCreatePhysicsState() override;
	virtual void OnDestroyPhysicsState() override;

private:
	int32 ChunkId = INDEX_NONE;
	FVector2D FootMinCm = FVector2D::ZeroVector;
	FVector2D FootMaxCm = FVector2D::ZeroVector;
	float ZLoM = -ExactMarginM;
	float ZHiM = ExactMarginM;
	bool bHasRange = false;

	int32 ProxyCreates = 0;
	int32 PhysicsStateCreates = 0;
	double PhysicsStateSeconds = 0.0;

	void PushBounds();
};
