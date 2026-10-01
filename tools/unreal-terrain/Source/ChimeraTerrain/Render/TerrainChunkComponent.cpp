// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Render/TerrainChunkComponent.h"

#include "HAL/PlatformTime.h"
#include "ChimeraTerrain.h"

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
	// Plan C 3.6: physics collision for engine traces only. The body comes from custom complex geometry rewritten at stroke end, undo and
	// redo (the render sections carry none); query-only, every channel ignored except ECC_Visibility, no navigation.
	SetCollisionEnabled(ECollisionEnabled::QueryOnly);
	SetCollisionObjectType(ECC_WorldStatic);
	SetCollisionResponseToAllChannels(ECR_Ignore);
	SetCollisionResponseToChannel(ECC_Visibility, ECR_Block);
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
		if (ProxyCreates > 1)
		{
			// C5 rework: every recreation is logged with its frame, so a run's extra whole-terrain recreations can be placed between the
			// director's "op i/n" lines (the C4 runs all had 75, the first C5 S1 runs 75/100/125).
			UE_LOG(LogChimeraTerrain, Display, TEXT("proxy recreate chunk=%d create=%d frame=%llu stroke_open=%d"), ChunkId, ProxyCreates,
				static_cast<unsigned long long>(GFrameCounter), bStrokeOpen ? 1 : 0);
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

void UTerrainChunkComponent::OnDestroyPhysicsState()
{
	const double T0 = FPlatformTime::Seconds();
	Super::OnDestroyPhysicsState();
	PhysicsStateSeconds += FPlatformTime::Seconds() - T0;
}
