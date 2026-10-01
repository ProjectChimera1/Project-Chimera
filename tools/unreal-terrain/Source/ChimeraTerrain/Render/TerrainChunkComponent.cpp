// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Render/TerrainChunkComponent.h"

#include "HAL/PlatformTime.h"

int64 UTerrainChunkComponent::ProxyCreatesDuringStrokes = 0;
bool UTerrainChunkComponent::bStrokeOpen = false;

UTerrainChunkComponent::UTerrainChunkComponent()
{
	PrimaryComponentTick.bCanEverTick = false;
	// Plan C 2.9: Movable, so VSM caches the chunk as dynamic and re-renders its shadow instead of keeping it stale.
	Mobility = EComponentMobility::Movable;
	// Trial terrain is a local presentation object: never replicate it (the RMC component defaults to replicated).
	SetIsReplicatedByDefault(false);
	SetReplicateMeshData(false);
	// Collision is added by C5 (custom complex geometry at stroke end); the render sections carry none.
	SetCollisionEnabled(ECollisionEnabled::NoCollision);
	SetCanEverAffectNavigation(false);
	bUpdateNavigationOnCollisionUpdate = false;
}

void UTerrainChunkComponent::SetFootprint(int32 InChunkId, const FVector2D& MinCm, const FVector2D& MaxCm)
{
	ChunkId = InChunkId;
	FootMinCm = MinCm;
	FootMaxCm = MaxCm;
}

void UTerrainChunkComponent::SetExactZRange(float MinZM, float MaxZM)
{
	ZLoM = MinZM - ExactMarginM;
	ZHiM = MaxZM + ExactMarginM;
	bHasRange = true;
	PushBounds();
}

bool UTerrainChunkComponent::NotifyTickZRange(float MinZM, float MaxZM)
{
	if (!bHasRange)
	{
		SetExactZRange(MinZM, MaxZM);
		return true;
	}
	bool bChanged = false;
	if (MaxZM > ZHiM)
	{
		ZHiM = MaxZM + WidenHeadroomM;
		bChanged = true;
	}
	if (MinZM < ZLoM)
	{
		ZLoM = MinZM - WidenHeadroomM;
		bChanged = true;
	}
	if (bChanged)
	{
		PushBounds();
	}
	return bChanged;
}

void UTerrainChunkComponent::PushBounds()
{
	// Bounds are read through CalcBounds; refresh the component's copy now (tests read it) and mark the render transform
	// dirty so the scene's copy follows at the end of the frame (PrimitiveComponent.cpp:655-664).
	UpdateBounds();
	if (IsRegistered())
	{
		MarkRenderTransformDirty();
	}
}

FBoxSphereBounds UTerrainChunkComponent::CalcBounds(const FTransform& LocalToWorld) const
{
	if (!bHasRange)
	{
		return Super::CalcBounds(LocalToWorld);
	}
	const FBox Box(FVector(FootMinCm.X, FootMinCm.Y, static_cast<double>(ZLoM) * 100.0),
		FVector(FootMaxCm.X, FootMaxCm.Y, static_cast<double>(ZHiM) * 100.0));
	return FBoxSphereBounds(Box).TransformBy(LocalToWorld);
}

FPrimitiveSceneProxy* UTerrainChunkComponent::CreateSceneProxy()
{
	FPrimitiveSceneProxy* Proxy = Super::CreateSceneProxy();
	if (Proxy)
	{
		++ProxyCreates;
		if (ProxyCreates > 1 && bStrokeOpen)
		{
			++ProxyCreatesDuringStrokes;
		}
	}
	return Proxy;
}

void UTerrainChunkComponent::OnCreatePhysicsState()
{
	const double T0 = FPlatformTime::Seconds();
	Super::OnCreatePhysicsState();
	PhysicsStateSeconds += FPlatformTime::Seconds() - T0;
	++PhysicsStateCreates;
}
