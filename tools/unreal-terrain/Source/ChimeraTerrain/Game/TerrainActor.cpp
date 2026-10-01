// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/TerrainActor.h"

#include "ChimeraTerrain.h"
#include "HAL/PlatformTime.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "Render/RmcTerrainRenderer.h"

using namespace ChimeraTerrain;

namespace
{
	/** Default lit grey material until C7's M_ChimeraGround: the engine's BasicShapeMaterial (vector parameter "Color"). */
	const TCHAR* const DefaultMaterialPath = TEXT("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial");
	/** Linear albedo of the grey ground. */
	const FLinearColor DefaultGrey(0.30f, 0.30f, 0.30f, 1.0f);
}

ATerrainActor::ATerrainActor()
{
	PrimaryActorTick.bCanEverTick = false;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
	Root->SetMobility(EComponentMobility::Movable);
}

UMaterialInterface* ATerrainActor::MakeDefaultMaterial()
{
	UMaterialInterface* Base = LoadObject<UMaterialInterface>(nullptr, DefaultMaterialPath);
	if (!Base)
	{
		UE_LOG(LogChimeraTerrain, Error, TEXT("terrain: could not load %s"), DefaultMaterialPath);
		return nullptr;
	}
	GroundMid = UMaterialInstanceDynamic::Create(Base, this, FName(TEXT("MID_TerrainGrey")));
	GroundMid->SetVectorParameterValue(FName(TEXT("Color")), DefaultGrey);
	FLinearColor Read = FLinearColor::Black;
	const bool bHasColor = GroundMid->GetVectorParameterValue(FHashedMaterialParameterInfo(FName(TEXT("Color"))), Read);
	UE_LOG(LogChimeraTerrain, Display, TEXT("terrain: material %s Color param %s (%.2f %.2f %.2f)"), DefaultMaterialPath,
		bHasColor ? TEXT("found") : TEXT("MISSING"), Read.R, Read.G, Read.B);
	return GroundMid;
}

bool ATerrainActor::InitTerrain(int32 HalfExtentM, int32 ChunkQuads, ETerrainDrawType InDrawType)
{
	DrawType = InDrawType;
	HF.Init(HalfExtentM, ChunkQuads);
	Undo.Clear();
	if (!Splat.Create(HF))
	{
		return false;
	}
	UMaterialInterface* Mat = MakeDefaultMaterial();
	Renderer = MakeUnique<FRmcTerrainRenderer>();
	if (!Renderer->Initialize(this, HF, Mat, DrawType))
	{
		return false;
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("terrain: half=%d m chunk=%d quads vertices=%dx%d splat=%dx%d height_fnv=0x%08x splat_fnv=0x%08x"),
		HalfExtentM, ChunkQuads, HF.Width(), HF.Width(), HF.SplatSize(), HF.SplatSize(), HF.HeightFnv(), HF.SplatFnv());
	return true;
}

void ATerrainActor::BeginStroke(const FTerrainBrushParams& InParams, const FVector2D& StartM)
{
	if (bStrokeOpen)
	{
		EndStroke();
	}
	Params = InParams;
	Params.Clamp();
	if (Params.Mode == ETerrainBrushMode::Flatten)
	{
		Params.FlattenTarget = FTerrainBrush::FlattenTargetAt(HF, static_cast<float>(StartM.X), static_cast<float>(StartM.Y));
	}
	Undo.BeginStroke();
	StrokeHeightRect = FTerrainRect();
	StrokeSplatRect = FTerrainRect();
	bStrokeOpen = true;
	if (Renderer)
	{
		Renderer->SetStrokeOpen(true);
	}
}

FTerrainTickResult ATerrainActor::ApplyTick(const FVector2D& CenterM, FTerrainTickTiming* OutTiming)
{
	FTerrainTickTiming T;
	const double T0 = FPlatformTime::Seconds();
	const FTerrainTickResult R = FTerrainBrush::ApplyTick(HF, Params, static_cast<float>(CenterM.X), static_cast<float>(CenterM.Y), bStrokeOpen ? &Undo : nullptr);
	const double T1 = FPlatformTime::Seconds();
	if (Renderer && !R.HeightRect.IsEmpty())
	{
		Renderer->UpdateHeights(HF, R.HeightRect);
	}
	const double T2 = FPlatformTime::Seconds();
	if (!R.SplatRect.IsEmpty())
	{
		Splat.UpdateRect(HF, R.SplatRect);
	}
	const double T3 = FPlatformTime::Seconds();
	StrokeHeightRect.Union(R.HeightRect);
	StrokeSplatRect.Union(R.SplatRect);
	T.ApplyMs = (T1 - T0) * 1000.0;
	T.UploadMs = (T2 - T1) * 1000.0;
	T.SplatMs = (T3 - T2) * 1000.0;
	LastTickMs = T.TotalMs();
	++TicksApplied;
	if (OutTiming)
	{
		*OutTiming = T;
	}
	return R;
}

bool ATerrainActor::EndStroke()
{
	if (!bStrokeOpen)
	{
		return false;
	}
	bStrokeOpen = false;
	if (Renderer)
	{
		// Exact bounds once the stroke is over (plan C 3.3).
		Renderer->RecomputeBounds(HF, StrokeHeightRect);
		Renderer->SetStrokeOpen(false);
	}
	return Undo.EndStroke(HF);
}

void ATerrainActor::ApplyDelta(const FTerrainEditDelta& Delta)
{
	if (Renderer && !Delta.HeightRect.IsEmpty())
	{
		Renderer->UpdateHeights(HF, Delta.HeightRect);
		Renderer->RecomputeBounds(HF, Delta.HeightRect);
	}
	if (!Delta.SplatRect.IsEmpty())
	{
		Splat.UpdateRect(HF, Delta.SplatRect);
	}
}

bool ATerrainActor::UndoLast()
{
	FTerrainEditDelta Delta;
	if (bStrokeOpen || !Undo.Undo(HF, Delta))
	{
		return false;
	}
	ApplyDelta(Delta);
	return true;
}

bool ATerrainActor::RedoLast()
{
	FTerrainEditDelta Delta;
	if (bStrokeOpen || !Undo.Redo(HF, Delta))
	{
		return false;
	}
	ApplyDelta(Delta);
	return true;
}

void ATerrainActor::SetTerrainVisible(bool bVisible)
{
	bVisibleNow = bVisible;
	if (Renderer)
	{
		Renderer->SetVisible(bVisible);
	}
}

bool ATerrainActor::HasPendingWork() const
{
	return (Renderer && Renderer->HasPendingWork()) || Splat.HasPendingWork();
}

void ATerrainActor::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	Renderer.Reset();
	Super::EndPlay(EndPlayReason);
}
