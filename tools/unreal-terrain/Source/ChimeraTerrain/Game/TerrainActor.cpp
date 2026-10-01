// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/TerrainActor.h"

#include "ChimeraTerrain.h"
#include "Data/TerrainIO.h"
#include "Dom/JsonObject.h"
#include "HAL/IConsoleManager.h"
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

	/** Plan C 3.6: 0 = collision is rewritten at stroke end only (the default); N > 0 = also at most every N ms while a stroke is open. */
	TAutoConsoleVariable<int32> CVarCollisionDuringStroke(
		TEXT("chimera.terrain.CollisionDuringStroke"),
		0,
		TEXT("Terrain physics collision during strokes: 0 = rewrite at stroke end only; N > 0 = also every N ms of an open stroke (the soak's worst case)."),
		ECVF_Default);
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
	Renderer->SetCollisionOptions(CollisionOptions);
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
	MidStrokeCollisionRect = FTerrainRect();
	LastMidStrokeCollisionSeconds = FPlatformTime::Seconds();
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
	int32 Uploaded = 0;
	if (Renderer && !R.HeightRect.IsEmpty())
	{
		Renderer->UpdateHeights(HF, R.HeightRect);
		const FTerrainUpdateTiming U = Renderer->GetLastUpdateTiming();
		T.NormalsMs = U.NormalsMs;
		T.UploadMs = U.SubmitMs;
		Uploaded = U.Vertices;
	}
	const double T2 = FPlatformTime::Seconds();
	if (!R.SplatRect.IsEmpty())
	{
		Splat.UpdateRect(HF, R.SplatRect);
	}
	const double T3 = FPlatformTime::Seconds();
	StrokeHeightRect.Union(R.HeightRect);
	StrokeSplatRect.Union(R.SplatRect);
	// Throttled mid-stroke collision (plan C 3.6 cvar; off by default). Not part of the tick timing: it is the collision path's cost.
	const int32 DuringMs = CVarCollisionDuringStroke.GetValueOnGameThread();
	if (bStrokeOpen && DuringMs > 0 && !R.HeightRect.IsEmpty())
	{
		MidStrokeCollisionRect.Union(R.HeightRect);
		const double Now = FPlatformTime::Seconds();
		if ((Now - LastMidStrokeCollisionSeconds) * 1000.0 >= DuringMs)
		{
			SubmitCollision(MidStrokeCollisionRect, ETerrainCollisionReason::MidStroke);
			MidStrokeCollisionRect = FTerrainRect();
			LastMidStrokeCollisionSeconds = Now;
		}
	}
	T.ApplyMs = (T1 - T0) * 1000.0;
	// The height path's wall time is normals + submit; any residue (a few timer reads) stays in upload.
	if (!R.HeightRect.IsEmpty())
	{
		T.UploadMs = FMath::Max(T.UploadMs, (T2 - T1) * 1000.0 - T.NormalsMs);
	}
	T.SplatMs = (T3 - T2) * 1000.0;
	LastTickMs = T.TotalMs();

	FTerrainTickSample S;
	S.Index = TicksApplied;
	S.Frame = GFrameCounter;
	S.PhaseIndex = CurrentPhase;
	S.Mode = static_cast<uint8>(Params.Mode);
	S.DiameterM = Params.DiameterM;
	S.Strength = Params.Strength;
	S.Timing = T;
	S.VerticesChanged = R.VerticesChanged;
	S.TexelsChanged = R.TexelsChanged;
	S.UploadedVertices = Uploaded;
	TickSamples.Add(S);
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
	// Plan C 3.6: each touched chunk's collision is rewritten from the final heights of the whole stroke.
	SubmitCollision(StrokeHeightRect, ETerrainCollisionReason::StrokeEnd);
	MidStrokeCollisionRect = FTerrainRect();
	return Undo.EndStroke(HF);
}

void ATerrainActor::SubmitCollision(const FTerrainRect& Rect, ETerrainCollisionReason Reason)
{
	if (Renderer && !Rect.IsEmpty() && Renderer->UpdateCollision(HF, Rect, Reason) > 0)
	{
		CollisionRects.Add(Rect);
	}
}

TArray<FTerrainRect> ATerrainActor::ConsumeCollisionRects()
{
	TArray<FTerrainRect> Out = MoveTemp(CollisionRects);
	CollisionRects.Reset();
	return Out;
}

void ATerrainActor::ApplyDelta(const FTerrainEditDelta& Delta, ETerrainCollisionReason Reason)
{
	if (Renderer && !Delta.HeightRect.IsEmpty())
	{
		Renderer->UpdateHeights(HF, Delta.HeightRect);
		Renderer->RecomputeBounds(HF, Delta.HeightRect);
	}
	SubmitCollision(Delta.HeightRect, Reason);
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
	ApplyDelta(Delta, ETerrainCollisionReason::Undo);
	return true;
}

bool ATerrainActor::RedoLast()
{
	FTerrainEditDelta Delta;
	if (bStrokeOpen || !Undo.Redo(HF, Delta))
	{
		return false;
	}
	ApplyDelta(Delta, ETerrainCollisionReason::Redo);
	return true;
}

bool ATerrainActor::SaveTo(const FString& Dir, FString& OutError)
{
	if (bStrokeOpen)
	{
		EndStroke();
	}
	return TerrainIO::Save(HF, Dir, OutError);
}

bool ATerrainActor::LoadFrom(const FString& Dir, FString& OutError)
{
	if (bStrokeOpen)
	{
		EndStroke();
	}
	FTerrainHeightfield Loaded;
	if (!TerrainIO::Load(Loaded, HF.ChunkQuads(), Dir, OutError))
	{
		return false;
	}
	if (Loaded.HalfExtentM() != HF.HalfExtentM())
	{
		OutError = FString::Printf(TEXT("loaded half extent %d m differs from the running terrain's %d m"), Loaded.HalfExtentM(), HF.HalfExtentM());
		return false;
	}
	HF = MoveTemp(Loaded);
	Undo.Clear();
	if (Renderer)
	{
		Renderer->RebuildAll(HF);
	}
	SubmitCollision(FTerrainRect(0, 0, HF.Width(), HF.Width()), ETerrainCollisionReason::Load);
	Splat.UpdateAll(HF);
	return true;
}

void ATerrainActor::SetPhase(const FString& Name)
{
	int32 Idx = PhaseNames.IndexOfByKey(Name);
	if (Idx == INDEX_NONE)
	{
		Idx = PhaseNames.Add(Name);
	}
	CurrentPhase = Idx;
}

void ATerrainActor::AddMouseStrokeRecord(const TSharedRef<FJsonObject>& Record)
{
	MouseStrokeRecords.Add(Record);
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
