// Project Chimera terrain trial (plan C 4 C9, plan C scatter 3.10). Original Chimera code. See TerrainUnitsActor.h.

#include "Game/TerrainUnitsActor.h"

#include "ChimeraTerrain.h"
#include "Engine/StaticMesh.h"
#include "Engine/World.h"
#include "Game/TerrainActor.h"
#include "HAL/PlatformTime.h"
#include "UObject/UObjectGlobals.h"

using namespace ChimeraTerrain;

namespace
{
	FString UnitsHex64(uint64 V)
	{
		return FString::Printf(TEXT("0x%016llx"), static_cast<unsigned long long>(V));
	}

	/** p in [0, 100] by nearest rank over a copy. */
	template <typename T>
	double UnitsPercentile(const TArray<T>& Values, double P)
	{
		if (Values.Num() == 0)
		{
			return 0.0;
		}
		TArray<T> Sorted = Values;
		Sorted.Sort();
		const int32 Rank = FMath::Clamp(static_cast<int32>(FMath::CeilToDouble(P * 0.01 * Sorted.Num())) - 1, 0, Sorted.Num() - 1);
		return static_cast<double>(Sorted[Rank]);
	}

	template <typename T>
	TSharedRef<FJsonObject> UnitsStatsJson(const TArray<T>& Values)
	{
		TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
		J->SetNumberField(TEXT("n"), Values.Num());
		J->SetNumberField(TEXT("p50"), UnitsPercentile(Values, 50.0));
		J->SetNumberField(TEXT("p99"), UnitsPercentile(Values, 99.0));
		J->SetNumberField(TEXT("max"), UnitsPercentile(Values, 100.0));
		return J;
	}
}

UChimeraUnitsISM::UChimeraUnitsISM()
{
	PrimaryComponentTick.bCanEverTick = false;
	bDisableCollision = true;
	SetCollisionEnabled(ECollisionEnabled::NoCollision);
	SetCanEverAffectNavigation(false);
	SetGenerateOverlapEvents(false);
}

FPrimitiveSceneProxy* UChimeraUnitsISM::CreateSceneProxy()
{
	FPrimitiveSceneProxy* Proxy = Super::CreateSceneProxy();
	if (Proxy != nullptr)
	{
		ProxyCreateCount.fetch_add(1);
	}
	return Proxy;
}

void UChimeraUnitsISM::SendRenderInstanceData_Concurrent()
{
	const uint64 T0 = FPlatformTime::Cycles64();
	Super::SendRenderInstanceData_Concurrent();
	FlushCycles.fetch_add(FPlatformTime::Cycles64() - T0);
	FlushCalls.fetch_add(1);
}

void UChimeraUnitsISM::TakeFlush(double& OutMs, int64& OutCalls)
{
	OutMs = static_cast<double>(FlushCycles.exchange(0)) * FPlatformTime::GetSecondsPerCycle64() * 1000.0;
	OutCalls = FlushCalls.exchange(0);
}

ATerrainUnits::ATerrainUnits()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = false;   // Setup enables the tick only when the layer is active
	PrimaryActorTick.TickGroup = TG_PostUpdateWork;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	Root->SetMobility(EComponentMobility::Movable);
	SetRootComponent(Root);
	SetReplicates(false);
}

void ATerrainUnits::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	StepFrame();
}

void ATerrainUnits::SetDirector(AActor* Director)
{
	if (Director)
	{
		AddTickPrerequisiteActor(Director);
	}
}

void ATerrainUnits::Setup(ATerrainActor* InTerrain, int32 InCount)
{
	Terrain = InTerrain;
	MeshPath = UnitMeshPath;
	if (Terrain == nullptr || !Terrain->GetHeightfield().IsInitialized() || InCount <= 0)
	{
		LoadError = TEXT("no terrain or no units requested");
		UE_LOG(LogChimeraTerrain, Error, TEXT("units: not started (%s)"), *LoadError);
		SetActorTickEnabled(false);
		return;
	}
	UStaticMesh* Mesh = LoadObject<UStaticMesh>(nullptr, *MeshPath);
	if (Mesh == nullptr)
	{
		LoadError = FString::Printf(TEXT("cannot load %s"), *MeshPath);
		UE_LOG(LogChimeraTerrain, Error, TEXT("units: %s"), *LoadError);
		SetActorTickEnabled(false);
		return;
	}
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	BuildUnitLayout(InCount, HF.HalfExtentM(), Slots);
	Pushed.Reset();
	Pushed.Reserve(Slots.Num());
	for (const FUnitSlot& S : Slots)
	{
		Pushed.Add(UnitTransform(HF, S));
	}
	Ism = NewObject<UChimeraUnitsISM>(this, TEXT("Units"));
	Ism->SetMobility(EComponentMobility::Movable);
	Ism->SetStaticMesh(Mesh);
	Ism->InstancingRandomSeed = 4099;   // fixed and non-zero
	Ism->bDisableCollision = true;
	Ism->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Ism->SetCanEverAffectNavigation(false);
	Ism->SetGenerateOverlapEvents(false);
	Ism->bAffectDistanceFieldLighting = false;
	Ism->bAffectDynamicIndirectLighting = false;
	Ism->bReceivesDecals = false;
	Ism->bEvaluateWorldPositionOffset = false;
	Ism->CastShadow = true;
	Ism->bCastDynamicShadow = true;
	Ism->bUseAsOccluder = true;
	Ism->ComponentTags.Add(FName(TEXT("ChimeraUnits")));
	Ism->SetupAttachment(Root);
	Ism->RegisterComponent();
	Ism->AddInstances(Pushed, false, false, false);
	ChangedHandle = Terrain->OnTerrainChanged.AddUObject(this, &ATerrainUnits::OnTerrainChanged);
	TWeakObjectPtr<ATerrainUnits> Weak(this);
	Terrain->AddPendingWorkProvider([Weak]() { return Weak.IsValid() && Weak->HasPendingWork(); });
	bActive = true;
	SetActorTickEnabled(true);
	UE_LOG(LogChimeraTerrain, Display, TEXT("units: on (count=%d mesh=%s scale=%.6f units_fnv=%s)"), Slots.Num(), *MeshPath, UnitScale, *UnitsHex64(UnitsFnv(Pushed)));
}

void ATerrainUnits::OnTerrainChanged(const FTerrainChange& Change)
{
	++EventsSeen;
	switch (Change.Kind)
	{
	case ETerrainChangeKind::Init:
	case ETerrainChangeKind::Load:
		bAllDirty = true;
		PendingRects.Reset();
		++EventsMarked;
		break;
	case ETerrainChangeKind::StrokeEnd:
		// The stroke's union (informational): every tick of the stroke already marked its own rect, and a stroke end changes no height.
		break;
	default:
		// Tick, Undo, Redo: the rects of this change only (never a stroke union).
		if (!bAllDirty && !Change.HeightRect.IsEmpty())
		{
			if (PendingRects.Num() >= 64)
			{
				// Many ticks before one flush (the catch-up cap is 15 per frame): fold the tail into one rect rather than grow without bound.
				PendingRects.Last().Union(Change.HeightRect);
			}
			else
			{
				PendingRects.Add(Change.HeightRect);
			}
			++EventsMarked;
		}
		break;
	}
}

void ATerrainUnits::StepFrame()
{
	if (!bActive || !HasPendingWork() || Terrain == nullptr)
	{
		return;
	}
	const double T0 = FPlatformTime::Seconds();
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	const int32 E = HF.HalfExtentM();
	TArray<int32> Changed;
	TArray<FTransform> Next;
	for (int32 I = 0; I < Slots.Num(); ++I)
	{
		bool bDirty = bAllDirty;
		if (!bDirty)
		{
			for (const FTerrainRect& R : PendingRects)
			{
				if (UnitDependsOnVertexRect(Slots[I], E, R))
				{
					bDirty = true;
					break;
				}
			}
		}
		if (!bDirty)
		{
			continue;
		}
		++UnitsExamined;
		const FTransform T = UnitTransform(HF, Slots[I]);
		const FTransform& Old = Pushed[I];
		if (T.GetLocation() == Old.GetLocation() && T.GetRotation() == Old.GetRotation() && T.GetScale3D() == Old.GetScale3D())
		{
			++UnitsSkippedIdentical;
			continue;
		}
		Changed.Add(I);
		Next.Add(T);
	}
	const bool bWasAll = bAllDirty;
	if (!bWasAll && !bFirstEditSeen)
	{
		// The first flush of a real edit (Init and Load flush the whole grid): proxies made from here on were made by or after an edit.
		bFirstEditSeen = true;
		ProxyCreatesAtFirstEdit = Ism->GetProxyCreates();
	}
	bAllDirty = false;
	PendingRects.Reset();
	// One engine call per run of consecutive indices; no render-state dirty mark (the instance data manager updates the proxy in place).
	int32 K = 0;
	while (K < Changed.Num())
	{
		int32 L = K + 1;
		while (L < Changed.Num() && Changed[L] == Changed[L - 1] + 1)
		{
			++L;
		}
		Ism->BatchUpdateInstancesTransforms(Changed[K], TArrayView<const FTransform>(Next.GetData() + K, L - K), false, false, false);
		K = L;
	}
	for (int32 J = 0; J < Changed.Num(); ++J)
	{
		Pushed[Changed[J]] = Next[J];
	}
	const double Ms = (FPlatformTime::Seconds() - T0) * 1000.0;
	++Flushes;
	if (bWasAll)
	{
		++FlushesAll;
	}
	UnitsUpdated += Changed.Num();
	MaxUpdatedPerFlush = FMath::Max(MaxUpdatedPerFlush, Changed.Num());
	TotalStepMs += Ms;
	MaxStepMs = FMath::Max(MaxStepMs, Ms);
	const FString Phase = Terrain->GetPhase();
	StepMsByPhase.FindOrAdd(Phase).Add(static_cast<float>(Ms));
	UpdatedByPhase.FindOrAdd(Phase).Add(Changed.Num());
	// The engine's own flush of the previous end of frame (summed SendRenderInstanceData_Concurrent time) belongs to the frames after an update.
	double FlushMs = 0.0;
	int64 FlushCalls = 0;
	Ism->TakeFlush(FlushMs, FlushCalls);
	EngineFlushMs += FlushMs;
	EngineFlushCalls += FlushCalls;
	MaxEngineFlushMs = FMath::Max(MaxEngineFlushMs, FlushMs);
}

TSharedRef<FJsonObject> ATerrainUnits::Verify(const FString& Name, bool& bOutPass) const
{
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetNumberField(TEXT("frame"), static_cast<double>(GFrameCounter));
	const bool bPending = HasPendingWork() || (Terrain != nullptr && Terrain->HasPendingWork());
	J->SetBoolField(TEXT("pending"), bPending);
	J->SetNumberField(TEXT("count"), Slots.Num());
	int32 Mismatch = 0;
	int32 Missing = 0;
	double MaxPosErrCm = 0.0;
	double MaxRotErr = 0.0;
	double MaxZErrCm = 0.0;
	double MaxScaleErr = 0.0;
	double MinZCm = TNumericLimits<double>::Max();
	double MaxZCm = -TNumericLimits<double>::Max();
	double MaxTiltDeg = 0.0;
	if (bActive && Terrain != nullptr && Ism != nullptr)
	{
		const FTerrainHeightfield& HF = Terrain->GetHeightfield();
		if (Ism->GetNumInstances() != Slots.Num())
		{
			Missing = FMath::Abs(Ism->GetNumInstances() - Slots.Num());
		}
		for (int32 I = 0; I < Slots.Num() && I < Ism->GetNumInstances(); ++I)
		{
			const FTransform Want = UnitTransform(HF, Slots[I]);
			if (!(Want.GetLocation() == Pushed[I].GetLocation() && Want.GetRotation() == Pushed[I].GetRotation()))
			{
				++Mismatch;
			}
			FTransform Got;
			if (!Ism->GetInstanceTransform(I, Got, false))
			{
				++Missing;
				continue;
			}
			MaxPosErrCm = FMath::Max(MaxPosErrCm, (Got.GetLocation() - Want.GetLocation()).Size());
			const FQuat A = Got.GetRotation();
			const FQuat B = Want.GetRotation();
			MaxRotErr = FMath::Max(MaxRotErr, FMath::Min((A - B).Size(), (A + B).Size()));
			MaxScaleErr = FMath::Max(MaxScaleErr, (Got.GetScale3D() - Want.GetScale3D()).GetAbsMax());
			// On the drawn surface: z of the instance against HF.SampleSurface at its own x, y (metres -> cm).
			const FVector L = Got.GetLocation();
			const double SurfaceCm = static_cast<double>(HF.SampleSurface(L.X * 0.01, L.Y * 0.01)) * 100.0;
			MaxZErrCm = FMath::Max(MaxZErrCm, FMath::Abs(L.Z - SurfaceCm));
			MinZCm = FMath::Min(MinZCm, L.Z);
			MaxZCm = FMath::Max(MaxZCm, L.Z);
			const FVector Up = A.GetUpVector();
			MaxTiltDeg = FMath::Max(MaxTiltDeg, FMath::RadiansToDegrees(FMath::Acos(FMath::Clamp(Up.Z, -1.0, 1.0))));
		}
	}
	J->SetNumberField(TEXT("pushed_vs_full_mismatch"), Mismatch);
	J->SetNumberField(TEXT("missing"), Missing);
	J->SetNumberField(TEXT("max_pos_err_cm"), MaxPosErrCm);
	J->SetNumberField(TEXT("max_rot_err"), MaxRotErr);
	J->SetNumberField(TEXT("max_scale_err"), MaxScaleErr);
	J->SetNumberField(TEXT("max_z_err_cm"), MaxZErrCm);
	J->SetNumberField(TEXT("min_z_cm"), Slots.Num() > 0 ? MinZCm : 0.0);
	J->SetNumberField(TEXT("max_z_cm"), Slots.Num() > 0 ? MaxZCm : 0.0);
	J->SetNumberField(TEXT("max_tilt_deg"), MaxTiltDeg);
	J->SetStringField(TEXT("units_fnv"), UnitsHex64(UnitsFnv(Pushed)));
	// Float32 storage of cm values up to 12,800: a position error of one 1/200 cm and a rotation error of 1e-4 are float rounding, not a stale pose.
	constexpr double PosTolCm = 0.01;
	constexpr double RotTol = 1.0e-4;
	bOutPass = bActive && !bPending && Mismatch == 0 && Missing == 0 && MaxPosErrCm <= PosTolCm && MaxRotErr <= RotTol && MaxScaleErr <= 1.0e-4 && MaxZErrCm <= PosTolCm;
	J->SetBoolField(TEXT("pass"), bOutPass);
	UE_LOG(LogChimeraTerrain, Display, TEXT("units_verify %s: pass=%d count=%d pending=%d mismatch=%d max_pos_err_cm=%.5f max_z_err_cm=%.5f max_rot_err=%.2e max_tilt_deg=%.1f"), *Name,
		bOutPass ? 1 : 0, Slots.Num(), bPending ? 1 : 0, Mismatch, MaxPosErrCm, MaxZErrCm, MaxRotErr, MaxTiltDeg);
	return J;
}

void ATerrainUnits::AddHashFields(FJsonObject& Out) const
{
	Out.SetStringField(TEXT("units_fnv"), UnitsHex64(UnitsFnv(Pushed)));
	Out.SetNumberField(TEXT("units_count"), Slots.Num());
}

TSharedRef<FJsonObject> ATerrainUnits::ResultsJson() const
{
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetBoolField(TEXT("active"), bActive);
	J->SetStringField(TEXT("error"), LoadError);
	J->SetStringField(TEXT("mesh"), MeshPath);
	J->SetNumberField(TEXT("count"), Slots.Num());
	J->SetNumberField(TEXT("scale"), UnitScale);
	J->SetNumberField(TEXT("events_seen"), static_cast<double>(EventsSeen));
	J->SetNumberField(TEXT("events_marked"), static_cast<double>(EventsMarked));
	J->SetNumberField(TEXT("flushes"), static_cast<double>(Flushes));
	J->SetNumberField(TEXT("flushes_whole_grid"), static_cast<double>(FlushesAll));
	J->SetNumberField(TEXT("units_examined"), static_cast<double>(UnitsExamined));
	J->SetNumberField(TEXT("units_updated"), static_cast<double>(UnitsUpdated));
	J->SetNumberField(TEXT("units_skipped_identical"), static_cast<double>(UnitsSkippedIdentical));
	J->SetNumberField(TEXT("max_updated_per_flush"), MaxUpdatedPerFlush);
	J->SetNumberField(TEXT("step_ms_total"), TotalStepMs);
	J->SetNumberField(TEXT("step_ms_max"), MaxStepMs);
	J->SetNumberField(TEXT("engine_flush_calls_seen"), static_cast<double>(EngineFlushCalls));
	J->SetNumberField(TEXT("engine_flush_ms_seen"), EngineFlushMs);
	J->SetNumberField(TEXT("engine_flush_ms_max"), MaxEngineFlushMs);
	J->SetNumberField(TEXT("proxy_creates"), Ism != nullptr ? Ism->GetProxyCreates() : 0);
	J->SetNumberField(TEXT("proxy_creates_at_first_edit"), bFirstEditSeen ? ProxyCreatesAtFirstEdit : -1);
	J->SetNumberField(TEXT("proxy_creates_after_first_edit"), (bFirstEditSeen && Ism != nullptr) ? Ism->GetProxyCreates() - ProxyCreatesAtFirstEdit : 0);
	J->SetStringField(TEXT("units_fnv"), UnitsHex64(UnitsFnv(Pushed)));
	TSharedRef<FJsonObject> Phases = MakeShared<FJsonObject>();
	for (const TPair<FString, TArray<float>>& Pr : StepMsByPhase)
	{
		TSharedRef<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetObjectField(TEXT("step_ms"), UnitsStatsJson(Pr.Value));
		if (const TArray<int32>* U = UpdatedByPhase.Find(Pr.Key))
		{
			P->SetObjectField(TEXT("updated_per_flush"), UnitsStatsJson(*U));
		}
		Phases->SetObjectField(Pr.Key, P);
	}
	J->SetObjectField(TEXT("phases"), Phases);
	return J;
}

void ATerrainUnits::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	if (Terrain != nullptr && ChangedHandle.IsValid())
	{
		Terrain->OnTerrainChanged.Remove(ChangedHandle);
		ChangedHandle.Reset();
	}
	Super::EndPlay(EndPlayReason);
}
