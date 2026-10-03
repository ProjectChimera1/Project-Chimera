// Project Chimera terrain trial (plan C scatter 3.8, task S5). Original Chimera code.
// The director's scatter ops of S5: scatter_dump, scatter_check, scatter_counts, scatter_view_counts, scatter_target, scatter_mask. They live in
// their own file so TerrainScriptDirector.cpp keeps its size; they are members of ATerrainScriptDirector (declared in its header) and every one
// fails with "needs -ChimeraTerrainScatter=1" unless the run started with scatter (NeedScatter). All results go to results.json (scatter block,
// AddScatterOpResults) or footprints.json / scatter_counts.json / scatter_<name>.bin in the run folder.

#include "Test/TerrainScriptDirector.h"

#include "ChimeraTerrain.h"
#include "Camera/PlayerCameraManager.h"
#include "Data/TerrainScatter.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/LocalPlayer.h"
#include "Engine/World.h"
#include "Game/RtsCameraPawn.h"
#include "Game/TerrainActor.h"
#include "Game/TerrainLighting.h"
#include "Game/TerrainScatterActor.h"
#include "GameFramework/PlayerController.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "SceneView.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnrealClient.h"

using namespace ChimeraTerrain;

namespace
{
	double OpNum(const FJsonObject& O, const TCHAR* Name, double Default)
	{
		double V = Default;
		return O.TryGetNumberField(Name, V) ? V : Default;
	}

	FString OpStr(const FJsonObject& O, const TCHAR* Name, const FString& Default = FString())
	{
		FString V;
		return O.TryGetStringField(Name, V) ? V : Default;
	}

	/** The player's view-projection (one matrix for a whole op, so a quarter of a million projections stay cheap). */
	struct FViewProj
	{
		FMatrix VP = FMatrix::Identity;
		FIntRect Rect;
		bool bValid = false;

		bool Init(APlayerController* PC)
		{
			ULocalPlayer* LP = PC ? PC->GetLocalPlayer() : nullptr;
			if (!LP || !LP->ViewportClient || !LP->ViewportClient->Viewport)
			{
				return false;
			}
			FSceneViewProjectionData Data;
			if (!LP->GetProjectionData(LP->ViewportClient->Viewport, Data))
			{
				return false;
			}
			VP = Data.ComputeViewProjectionMatrix();
			Rect = Data.GetConstrainedViewRect();
			bValid = true;
			return true;
		}

		/** Screen position (viewport pixels) of a world point in cm; false behind the camera. */
		bool Project(const FVector& WorldCm, FVector2D& Out) const
		{
			return FSceneView::ProjectWorldToScreen(WorldCm, Rect, VP, Out, false);
		}
	};

	/** Convex hull (Andrew's monotone chain) of screen points. */
	void ConvexHull(TArray<FVector2D>& P, TArray<FVector2D>& Hull)
	{
		Hull.Reset();
		P.Sort([](const FVector2D& A, const FVector2D& B) { return A.X != B.X ? A.X < B.X : A.Y < B.Y; });
		if (P.Num() < 3)
		{
			Hull = P;
			return;
		}
		auto Cross = [](const FVector2D& O, const FVector2D& A, const FVector2D& B) { return (A.X - O.X) * (B.Y - O.Y) - (A.Y - O.Y) * (B.X - O.X); };
		TArray<FVector2D> H;
		H.SetNum(2 * P.Num());
		int32 K = 0;
		for (int32 I = 0; I < P.Num(); ++I)
		{
			while (K >= 2 && Cross(H[K - 2], H[K - 1], P[I]) <= 0.0)
			{
				--K;
			}
			H[K++] = P[I];
		}
		for (int32 I = P.Num() - 2, T = K + 1; I >= 0; --I)
		{
			while (K >= T && Cross(H[K - 2], H[K - 1], P[I]) <= 0.0)
			{
				--K;
			}
			H[K++] = P[I];
		}
		H.SetNum(FMath::Max(1, K - 1));
		Hull = H;
	}

	/** Set the pixels of a convex screen polygon (every pixel whose centre is inside; a degenerate hull sets its bounding pixels). */
	void FillHull(TArray<FVector2D>& Pts, int32 W, int32 H, TArray<uint8>& Bits)
	{
		if (Pts.Num() == 0)
		{
			return;
		}
		double MinX = TNumericLimits<double>::Max();
		double MinY = MinX;
		double MaxX = -MinX;
		double MaxY = -MinX;
		for (const FVector2D& P : Pts)
		{
			MinX = FMath::Min(MinX, P.X);
			MaxX = FMath::Max(MaxX, P.X);
			MinY = FMath::Min(MinY, P.Y);
			MaxY = FMath::Max(MaxY, P.Y);
		}
		if (MaxX < 0.0 || MaxY < 0.0 || MinX >= W || MinY >= H)
		{
			return;
		}
		TArray<FVector2D> Hull;
		ConvexHull(Pts, Hull);
		const int32 Y0 = FMath::Clamp(FMath::FloorToInt(MinY), 0, H - 1);
		const int32 Y1 = FMath::Clamp(FMath::FloorToInt(MaxY), 0, H - 1);
		auto SetRow = [&](int32 Y, double Xl, double Xr)
		{
			const int32 X0 = FMath::Clamp(FMath::FloorToInt(Xl), 0, W - 1);
			const int32 X1 = FMath::Clamp(FMath::FloorToInt(Xr), 0, W - 1);
			for (int32 X = X0; X <= X1; ++X)
			{
				Bits[Y * W + X] = 1;
			}
		};
		if (Hull.Num() < 3)
		{
			for (int32 Y = Y0; Y <= Y1; ++Y)
			{
				SetRow(Y, MinX, MaxX);
			}
			return;
		}
		bool bAny = false;
		for (int32 Y = Y0; Y <= Y1; ++Y)
		{
			const double Yc = static_cast<double>(Y) + 0.5;
			double Xl = TNumericLimits<double>::Max();
			double Xr = -Xl;
			for (int32 I = 0; I < Hull.Num(); ++I)
			{
				const FVector2D& A = Hull[I];
				const FVector2D& B = Hull[(I + 1) % Hull.Num()];
				if (A.Y == B.Y)
				{
					continue;
				}
				const double Lo = FMath::Min(A.Y, B.Y);
				const double Hi = FMath::Max(A.Y, B.Y);
				if (Yc >= Lo && Yc < Hi)
				{
					const double X = A.X + (Yc - A.Y) * (B.X - A.X) / (B.Y - A.Y);
					Xl = FMath::Min(Xl, X);
					Xr = FMath::Max(Xr, X);
				}
			}
			if (Xl <= Xr)
			{
				SetRow(Y, Xl, Xr);
				bAny = true;
			}
		}
		if (!bAny)
		{
			// A sliver thinner than a pixel row crosses no pixel centre: take the pixels its own points fall in so no instance is lost.
			for (const FVector2D& P : Pts)
			{
				const int32 X = FMath::FloorToInt(P.X);
				const int32 Y = FMath::FloorToInt(P.Y);
				if (X >= 0 && Y >= 0 && X < W && Y < H)
				{
					Bits[Y * W + X] = 1;
				}
			}
		}
	}

	FIntPoint OpViewportSize()
	{
		if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
		{
			return GEngine->GameViewport->Viewport->GetSizeXY();
		}
		return FIntPoint(0, 0);
	}

	TSharedPtr<FJsonValue> Vec3Json(const FVector& V)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(V.X));
		A.Add(MakeShared<FJsonValueNumber>(V.Y));
		A.Add(MakeShared<FJsonValueNumber>(V.Z));
		return MakeShared<FJsonValueArray>(A);
	}

	bool IsCasterClass(EScatterClass C)
	{
		return C == EScatterClass::Tree || C == EScatterClass::Sapling || C == EScatterClass::Shrub || C == EScatterClass::Rock;
	}

	bool ClassFromName(const FString& Name, EScatterClass& Out)
	{
		for (int32 C = 0; C < ScatterClassCount; ++C)
		{
			if (Name.Equals(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), ESearchCase::IgnoreCase))
			{
				Out = static_cast<EScatterClass>(C);
				return true;
			}
		}
		return false;
	}
}

bool ATerrainScriptDirector::ParseLayerMask(const FString& Spec, uint32& OutMask)
{
	OutMask = 0;
	if (Spec.Equals(TEXT("all"), ESearchCase::IgnoreCase))
	{
		OutMask = (1u << ScatterLayerCount) - 1u;
		return true;
	}
	TArray<FString> Names;
	Spec.ParseIntoArray(Names, TEXT(","), true);
	for (FString N : Names)
	{
		N.TrimStartAndEndInline();
		bool bFound = false;
		for (int32 L = 0; L < ScatterLayerCount; ++L)
		{
			if (N.Equals(UTF8_TO_TCHAR(ScatterLayerName(static_cast<EScatterLayer>(L))), ESearchCase::IgnoreCase))
			{
				OutMask |= 1u << L;
				bFound = true;
			}
		}
		if (!bFound)
		{
			OpError = FString::Printf(TEXT("unknown layer '%s' (grass, groundcover, shrubs, trees, rocks or all)"), *N);
			return false;
		}
	}
	return OutMask != 0;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterDump(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString Name = OpStr(Op, TEXT("name"), FString::Printf(TEXT("dump%d"), OpIndex + 1));
	FString Path = FPaths::ConvertRelativePathToFull(OutPath(FString::Printf(TEXT("scatter_%s.bin"), *Name)));
	FPaths::NormalizeFilename(Path);
	bool bOk = false;
	const TSharedRef<FJsonObject> J = Scatter->DumpApplied(Name, Path, bOk);
	J->SetBoolField(TEXT("pending"), Scatter->HasPendingWork());
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	ScatterDumps.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_dump %s: %.0f records, %.0f bytes, sha256 %s, written=%d, pending=%d"), *Name, J->GetNumberField(TEXT("records")),
		J->GetNumberField(TEXT("bytes")), *J->GetStringField(TEXT("sha256")), bOk ? 1 : 0, Scatter->HasPendingWork() ? 1 : 0);
	if (!bOk)
	{
		OpError = FString::Printf(TEXT("cannot write %s"), *Path);
		return EStep::Failed;
	}
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterCheck(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString Name = OpStr(Op, TEXT("name"), FString::Printf(TEXT("check%d"), OpIndex + 1));
	const FString SetName = OpStr(Op, TEXT("fp"));
	const TArray<FVector>* Discs = FootprintSets.Find(SetName);
	if (!Discs || Discs->Num() == 0)
	{
		OpError = FString::Printf(TEXT("no footprint set '%s' (strokes need \"fp\": \"%s\")"), *SetName, *SetName);
		return EStep::Failed;
	}
	const TSharedRef<FJsonObject> J = Scatter->CheckDiscs(Name, SetName, *Discs);
	J->SetBoolField(TEXT("pending"), Scatter->HasPendingWork());
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	ScatterChecks.Add(MakeShared<FJsonValueObject>(J));
	const TSharedPtr<FJsonObject>* Cls = nullptr;
	if (J->TryGetObjectField(TEXT("classes"), Cls) && Cls)
	{
		FString Line;
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pr : (*Cls)->Values)
		{
			const TSharedPtr<FJsonObject>& One = Pr.Value->AsObject();
			if (One->GetNumberField(TEXT("core_pure")) > 0 || One->GetNumberField(TEXT("core_live")) > 0 || One->GetNumberField(TEXT("band_live")) > 0)
			{
				Line += FString::Printf(TEXT(" %s core %.0f/%.0f band %.0f/%.0f"), *Pr.Key, One->GetNumberField(TEXT("core_live")), One->GetNumberField(TEXT("core_pure")),
					One->GetNumberField(TEXT("band_live")), One->GetNumberField(TEXT("band_pure")));
			}
		}
		UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_check %s set %s (%d discs), live/pure:%s"), *Name, *SetName, Discs->Num(), *Line);
	}
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterCounts(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString Name = OpStr(Op, TEXT("name"), FString::Printf(TEXT("counts%d"), OpIndex + 1));
	ScatterCountsFile->SetObjectField(Name, Scatter->CountsJson());
	if (!WriteJsonFile(TEXT("scatter_counts.json"), ScatterCountsFile))
	{
		OpError = TEXT("cannot write scatter_counts.json");
		return EStep::Failed;
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetStringField(TEXT("file"), OutPath(TEXT("scatter_counts.json")));
	J->SetBoolField(TEXT("pending"), Scatter->HasPendingWork());
	ScatterCountsRows.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_counts %s written"), *Name);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterViewCounts(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString PoseName = OpStr(Op, TEXT("pose"), TEXT("rts80"));
	const FString Name = OpStr(Op, TEXT("name"), PoseName);
	if (OpFrame == 0)
	{
		if (!ApplyPose(PoseName))
		{
			OpError = FString::Printf(TEXT("unknown pose '%s'"), *PoseName);
			return EStep::Failed;
		}
		return EStep::Running;
	}
	if (OpFrame < 3)
	{
		return EStep::Running;
	}
	APlayerController* PC = GetPC();
	FViewProj View;
	if (!PC || !PC->PlayerCameraManager || !View.Init(PC))
	{
		OpError = TEXT("no camera or projection data");
		return EStep::Failed;
	}
	const FVector CamM = PC->PlayerCameraManager->GetCameraLocation() / 100.0;
	const FIntPoint VP = OpViewportSize();
	TArray<FScatterRecord> All;
	Scatter->CollectLive(All);
	struct FCls
	{
		int64 Total = 0;
		int64 InView = 0;
		int64 Drawn = 0;
	};
	FCls Cls[ScatterClassCount];
	int64 MeshDrawn[ScatterMeshCount] = {};
	TSet<int64> UnitsDrawn;
	TSet<int64> UnitsAll;
	for (const FScatterRecord& R : All)
	{
		const EScatterClass C = static_cast<EScatterClass>(R.Class);
		++Cls[R.Class].Total;
		const EScatterGrid Grid = ScatterGridOfClass(C);
		const int32 Size = Scatter->TileSizeOf(Grid);
		const int64 Unit = (static_cast<int64>(R.Mesh) << 40) ^ (static_cast<int64>(FMath::FloorToInt((static_cast<double>(R.XQ) / 65536.0) / Size) + 4096) << 20)
			^ static_cast<int64>(FMath::FloorToInt((static_cast<double>(R.YQ) / 65536.0) / Size) + 4096);
		UnitsAll.Add(Unit);
		const FVector P(static_cast<double>(R.XQ) / 65536.0, static_cast<double>(R.YQ) / 65536.0, static_cast<double>(R.ZQ) / 65536.0);
		FVector2D S;
		if (!View.Project(P * 100.0, S) || S.X < 0.0 || S.Y < 0.0 || S.X >= VP.X || S.Y >= VP.Y)
		{
			continue;
		}
		++Cls[R.Class].InView;
		if (FVector::Dist(P, CamM) <= Scatter->CullEndMetersOf(R))
		{
			++Cls[R.Class].Drawn;
			++MeshDrawn[R.Mesh];
			UnitsDrawn.Add(Unit);
		}
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetStringField(TEXT("pose"), PoseName);
	J->SetBoolField(TEXT("analytic"), true);
	J->SetStringField(TEXT("note"), TEXT("analytic: instance origins inside the frustum and inside their cull end; not what the GPU drew (no occlusion, no LOD, no Nanite culling)"));
	J->SetField(TEXT("camera_m"), Vec3Json(CamM));
	TSharedRef<FJsonObject> Cj = MakeShared<FJsonObject>();
	int64 Drawn = 0;
	for (int32 C = 0; C < ScatterClassCount; ++C)
	{
		TSharedRef<FJsonObject> One = MakeShared<FJsonObject>();
		One->SetNumberField(TEXT("total"), static_cast<double>(Cls[C].Total));
		One->SetNumberField(TEXT("in_view"), static_cast<double>(Cls[C].InView));
		One->SetNumberField(TEXT("drawn"), static_cast<double>(Cls[C].Drawn));
		Cj->SetObjectField(UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(C))), One);
		Drawn += Cls[C].Drawn;
	}
	J->SetObjectField(TEXT("classes"), Cj);
	TSharedRef<FJsonObject> Mj = MakeShared<FJsonObject>();
	for (int32 M = 0; M < ScatterMeshCount; ++M)
	{
		Mj->SetNumberField(UTF8_TO_TCHAR(ScatterMeshName(static_cast<EScatterMesh>(M))), static_cast<double>(MeshDrawn[M]));
	}
	J->SetObjectField(TEXT("meshes_drawn"), Mj);
	J->SetNumberField(TEXT("drawn_total"), static_cast<double>(Drawn));
	J->SetNumberField(TEXT("primitives_in_view"), UnitsDrawn.Num());
	J->SetNumberField(TEXT("primitives_with_instances"), UnitsAll.Num());
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	ScatterViewCounts.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_view_counts %s (%s): analytic drawn %lld in %d primitives of %d with instances"), *Name, *PoseName, Drawn, UnitsDrawn.Num(), UnitsAll.Num());
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterTarget(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString ClassName = OpStr(Op, TEXT("class"));
	const FString As = OpStr(Op, TEXT("as"));
	const FString ZoneName = OpStr(Op, TEXT("zone"));
	EScatterClass Class;
	if (!ClassFromName(ClassName, Class) || As.IsEmpty())
	{
		OpError = FString::Printf(TEXT("scatter_target needs class (Grass, Tussock, Flower, NearCard, Tree, Sapling, Shrub, Fern, Rock) and as; got class '%s' as '%s'"), *ClassName, *As);
		return EStep::Failed;
	}
	int32 Zone = -1;
	if (!ZoneName.IsEmpty())
	{
		static const TCHAR* const Zones[] = {TEXT("core"), TEXT("edge"), TEXT("grove"), TEXT("lone")};
		for (int32 Z = 0; Z < 4; ++Z)
		{
			if (ZoneName.Equals(Zones[Z], ESearchCase::IgnoreCase))
			{
				Zone = Z;
			}
		}
		if (Zone < 0 || (Class != EScatterClass::Tree && Class != EScatterClass::Sapling))
		{
			OpError = FString::Printf(TEXT("scatter_target zone '%s' (core|edge|grove|lone) applies to trees only"), *ZoneName);
			return EStep::Failed;
		}
	}
	const TArray<TSharedPtr<FJsonValue>>* Near = nullptr;
	if (!Op.TryGetArrayField(TEXT("near"), Near) || Near->Num() != 2)
	{
		OpError = TEXT("scatter_target needs near: [x, y] (metres)");
		return EStep::Failed;
	}
	const double X = (*Near)[0]->AsNumber();
	const double Y = (*Near)[1]->AsNumber();
	FScatterRecord R;
	if (!Scatter->FindNearest(Class, X, Y, Zone, R))
	{
		OpError = FString::Printf(TEXT("no applied %s instance%s (scatter_wait first?)"), *ClassName, ZoneName.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(" in zone %s"), *ZoneName));
		return EStep::Failed;
	}
	TSharedPtr<FScatterTarget> T = MakeShared<FScatterTarget>();
	T->Name = As;
	T->Class = R.Class;
	T->Key = R.Key();
	T->X = static_cast<double>(R.XQ) / 65536.0;
	T->Y = static_cast<double>(R.YQ) / 65536.0;
	T->Z = static_cast<double>(R.ZQ) / 65536.0;
	T->Zone = ZoneName;
	ScatterTargets.Add(As, T);
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_target %s: %s at (%.2f, %.2f) z %.3f m, %.2f m from (%.1f, %.1f), key 0x%016llx"), *As, *ClassName, T->X, T->Y, T->Z,
		FMath::Sqrt((T->X - X) * (T->X - X) + (T->Y - Y) * (T->Y - Y)), X, Y, static_cast<unsigned long long>(T->Key));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterMask(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString PoseName = OpStr(Op, TEXT("pose"), TEXT("rts80"));
	const FString Name = OpStr(Op, TEXT("name"));
	if (Name.IsEmpty())
	{
		OpError = TEXT("scatter_mask needs a name");
		return EStep::Failed;
	}
	if (OpFrame == 0)
	{
		if (!ApplyPose(PoseName))
		{
			OpError = FString::Printf(TEXT("unknown pose '%s'"), *PoseName);
			return EStep::Failed;
		}
		return EStep::Running;
	}
	if (OpFrame < 3)
	{
		return EStep::Running;
	}
	uint32 LayerBits = 0;
	if (!ParseLayerMask(OpStr(Op, TEXT("layers"), TEXT("all")), LayerBits))
	{
		return EStep::Failed;
	}
	APlayerController* PC = GetPC();
	FViewProj View;
	if (!PC || !PC->PlayerCameraManager || !View.Init(PC) || !Lighting)
	{
		OpError = TEXT("no camera, projection data or lighting");
		return EStep::Failed;
	}
	const FIntPoint VP = OpViewportSize();
	if (VP.X <= 0 || VP.Y <= 0)
	{
		OpError = TEXT("no viewport");
		return EStep::Failed;
	}
	const FVector CamM = PC->PlayerCameraManager->GetCameraLocation() / 100.0;
	const FVector L = Lighting->GetDirectionToSun().GetSafeNormal();
	// Optional locality: instances within `radius` metres of a stored target.
	bool bNear = false;
	FVector2D NearXY = FVector2D::ZeroVector;
	const double Radius = OpNum(Op, TEXT("radius"), 40.0);
	const FString NearRef = OpStr(Op, TEXT("near"));
	if (!NearRef.IsEmpty())
	{
		const TSharedPtr<FScatterTarget>* T = NearRef.StartsWith(TEXT("@")) ? ScatterTargets.Find(NearRef.Mid(1)) : nullptr;
		if (!T || !T->IsValid())
		{
			OpError = FString::Printf(TEXT("scatter_mask near '%s' is not a stored scatter_target (\"@<as>\")"), *NearRef);
			return EStep::Failed;
		}
		bNear = true;
		NearXY = FVector2D((*T)->X, (*T)->Y);
	}
	// Optional single instance: only="@<as>" keeps just the stored target's instance (by key), e.g. SHADX's t0-only mask beside the caster union.
	bool bOnly = false;
	uint64 OnlyKey = 0;
	const FString OnlyRef = OpStr(Op, TEXT("only"));
	if (!OnlyRef.IsEmpty())
	{
		const TSharedPtr<FScatterTarget>* T = OnlyRef.StartsWith(TEXT("@")) ? ScatterTargets.Find(OnlyRef.Mid(1)) : nullptr;
		if (!T || !T->IsValid())
		{
			OpError = FString::Printf(TEXT("scatter_mask only '%s' is not a stored scatter_target (\"@<as>\")"), *OnlyRef);
			return EStep::Failed;
		}
		bOnly = true;
		OnlyKey = (*T)->Key;
	}
	const FTerrainHeightfield* HF = Terrain ? &Terrain->GetHeightfield() : nullptr;
	TArray<uint8> Bits;
	Bits.Init(0, VP.X * VP.Y);
	TArray<FScatterRecord> All;
	Scatter->CollectLive(All);
	int64 Used = 0;
	int64 Casters = 0;
	for (const FScatterRecord& R : All)
	{
		const EScatterClass C = static_cast<EScatterClass>(R.Class);
		if ((LayerBits & (1u << static_cast<uint32>(ScatterLayerOfClass(C)))) == 0)
		{
			continue;
		}
		const FVector P(static_cast<double>(R.XQ) / 65536.0, static_cast<double>(R.YQ) / 65536.0, static_cast<double>(R.ZQ) / 65536.0);
		if (bNear && FVector2D::Distance(FVector2D(P.X, P.Y), NearXY) > Radius)
		{
			continue;
		}
		if (bOnly && R.Key() != OnlyKey)
		{
			continue;
		}
		if (FVector::Dist(P, CamM) > Scatter->CullEndMetersOf(R))
		{
			continue;
		}
		const FBox Box = Scatter->MeshLocalBoundsCm(static_cast<EScatterMesh>(R.Mesh));
		if (!Box.IsValid)
		{
			continue;
		}
		const FTransform Xf = Scatter->InstanceTransformOf(R);
		const double ZBase = Xf.GetTranslation().Z / 100.0;
		TArray<FVector2D> Pts;
		const bool bCaster = IsCasterClass(C);
		for (int32 Corner = 0; Corner < 8; ++Corner)
		{
			const FVector Local((Corner & 1) ? Box.Max.X : Box.Min.X, (Corner & 2) ? Box.Max.Y : Box.Min.Y, (Corner & 4) ? Box.Max.Z : Box.Min.Z);
			const FVector WorldCm = Xf.TransformPosition(Local);
			FVector2D S;
			if (View.Project(WorldCm, S))
			{
				Pts.Add(S);
			}
			if (bCaster && L.Z > 0.05)
			{
				// The analytic sun shadow of the corner on the plane of the instance's base: P - L * (height above the base / L.z).
				const FVector Pm = WorldCm / 100.0;
				const FVector Ground = Pm.Z > ZBase ? Pm - L * ((Pm.Z - ZBase) / L.Z) : Pm;
				FVector2D G;
				if (View.Project(Ground * 100.0, G))
				{
					Pts.Add(G);
				}
				// And where the sun ray really meets HF's drawn surface (FTerrainHeightfield::SampleSurface): on a slope falling away from the sun (the
				// down-sun flank of SHADX's raised hill) the shadow reaches further than on the base plane. March along the ray in 0.25 m steps, then
				// bisect the crossing; both points join the hull, so the mask covers the shadow on either surface.
				if (HF && Pm.Z > static_cast<double>(HF->SampleSurface(Pm.X, Pm.Y)))
				{
					constexpr double StepM = 0.25;
					constexpr double MaxRayM = 400.0;
					double TLo = 0.0;
					double THi = -1.0;
					for (double T = StepM; T <= MaxRayM; T += StepM)
					{
						const FVector Q = Pm - L * T;
						if (Q.Z <= static_cast<double>(HF->SampleSurface(Q.X, Q.Y)))
						{
							THi = T;
							break;
						}
						TLo = T;
					}
					if (THi > 0.0)
					{
						for (int32 It = 0; It < 20; ++It)
						{
							const double TM = 0.5 * (TLo + THi);
							const FVector Q = Pm - L * TM;
							(Q.Z <= static_cast<double>(HF->SampleSurface(Q.X, Q.Y)) ? THi : TLo) = TM;
						}
						const FVector Hit = Pm - L * THi;
						FVector2D Gs;
						if (View.Project(Hit * 100.0, Gs))
						{
							Pts.Add(Gs);
						}
					}
				}
			}
		}
		if (Pts.Num() == 0)
		{
			continue;
		}
		FillHull(Pts, VP.X, VP.Y, Bits);
		++Used;
		Casters += bCaster ? 1 : 0;
	}
	const FString UnionWith = OpStr(Op, TEXT("union_with"));
	if (!UnionWith.IsEmpty())
	{
		const TArray<uint8>* Old = ScatterMaskBits.Find(PoseName + TEXT("/") + UnionWith);
		const FIntPoint* OldSize = ScatterMaskSize.Find(PoseName + TEXT("/") + UnionWith);
		if (!Old || !OldSize || *OldSize != VP)
		{
			OpError = FString::Printf(TEXT("scatter_mask union_with '%s': no earlier mask of that name at pose %s (or another viewport size)"), *UnionWith, *PoseName);
			return EStep::Failed;
		}
		for (int32 I = 0; I < Bits.Num(); ++I)
		{
			Bits[I] |= (*Old)[I];
		}
	}
	// Row runs [y, x0, x1] (inclusive): compact enough for a 1920x1080 mask, and imgdiff.footprint_mask expands them.
	TArray<TSharedPtr<FJsonValue>> Runs;
	int64 Pixels = 0;
	for (int32 Y = 0; Y < VP.Y; ++Y)
	{
		int32 X = 0;
		while (X < VP.X)
		{
			if (!Bits[Y * VP.X + X])
			{
				++X;
				continue;
			}
			const int32 X0 = X;
			while (X < VP.X && Bits[Y * VP.X + X])
			{
				++X;
			}
			TArray<TSharedPtr<FJsonValue>> Run;
			Run.Add(MakeShared<FJsonValueNumber>(Y));
			Run.Add(MakeShared<FJsonValueNumber>(X0));
			Run.Add(MakeShared<FJsonValueNumber>(X - 1));
			Runs.Add(MakeShared<FJsonValueArray>(Run));
			Pixels += X - X0;
		}
	}
	ScatterMaskBits.Add(PoseName + TEXT("/") + Name, Bits);
	ScatterMaskSize.Add(PoseName + TEXT("/") + Name, VP);
	TSharedPtr<FJsonObject> PoseObj;
	const TSharedPtr<FJsonObject>* Existing = nullptr;
	if (Footprints->TryGetObjectField(PoseName, Existing) && Existing && Existing->IsValid())
	{
		PoseObj = *Existing;
	}
	else
	{
		PoseObj = MakeShared<FJsonObject>();
	}
	TArray<TSharedPtr<FJsonValue>> Vp;
	Vp.Add(MakeShared<FJsonValueNumber>(VP.X));
	Vp.Add(MakeShared<FJsonValueNumber>(VP.Y));
	PoseObj->SetArrayField(TEXT("viewport"), Vp);
	TSharedRef<FJsonObject> Entry = MakeShared<FJsonObject>();
	Entry->SetArrayField(TEXT("runs"), Runs);
	Entry->SetNumberField(TEXT("px"), static_cast<double>(Pixels));
	Entry->SetNumberField(TEXT("instances"), static_cast<double>(Used));
	Entry->SetNumberField(TEXT("casters"), static_cast<double>(Casters));
	Entry->SetStringField(TEXT("layers"), OpStr(Op, TEXT("layers"), TEXT("all")));
	Entry->SetStringField(TEXT("union_with"), UnionWith);
	Entry->SetStringField(TEXT("only"), OnlyRef);
	Entry->SetStringField(TEXT("near"), NearRef);
	PoseObj->SetObjectField(Name, Entry);
	Footprints->SetObjectField(PoseName, PoseObj);
	if (!WriteJsonFile(TEXT("footprints.json"), Footprints))
	{
		OpError = TEXT("cannot write footprints.json");
		return EStep::Failed;
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("name"), Name);
	J->SetStringField(TEXT("pose"), PoseName);
	J->SetNumberField(TEXT("px"), static_cast<double>(Pixels));
	J->SetNumberField(TEXT("instances"), static_cast<double>(Used));
	J->SetNumberField(TEXT("casters"), static_cast<double>(Casters));
	J->SetStringField(TEXT("union_with"), UnionWith);
	J->SetStringField(TEXT("only"), OnlyRef);
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	ScatterMasks.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_mask %s/%s: %lld instances (%lld casters), %lld px%s"), *PoseName, *Name, Used, Casters, Pixels,
		UnionWith.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(", union with %s"), *UnionWith));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepMovieStart(const FJsonObject& Op)
{
	// The first half of `movie` (GIsDumpingMovie > 0 dumps that many frames as MovieFrame*.png, UnrealClient.cpp:281-289), without waiting for the frames.
	const int32 Frames = FMath::Max(1, static_cast<int32>(OpNum(Op, TEXT("frames"), 30.0)));
	MovieRequested = Frames;
	MovieStartUtc = FDateTime::UtcNow();
	GIsDumpingMovie = Frames;
	UE_LOG(LogChimeraTerrain, Display, TEXT("movie_start %d frames -> %s"), Frames, *FPaths::ConvertRelativePathToFull(FPaths::ScreenShotDir()));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepMovieWait(const FJsonObject& Op)
{
	if (MovieRequested <= 0)
	{
		OpError = TEXT("movie_wait without a movie_start");
		return EStep::Failed;
	}
	if (OpFrame == 0)
	{
		return EStep::Running;   // StepMovie's own first frame would restart the dump; its later frames only wait and list the files
	}
	return StepMovie(Op);
}

void ATerrainScriptDirector::AddScatterOpResults(FJsonObject& ScatterBlock) const
{
	ScatterBlock.SetArrayField(TEXT("dumps"), ScatterDumps);
	ScatterBlock.SetArrayField(TEXT("checks"), ScatterChecks);
	ScatterBlock.SetArrayField(TEXT("counts"), ScatterCountsRows);
	ScatterBlock.SetArrayField(TEXT("view_counts"), ScatterViewCounts);
	ScatterBlock.SetArrayField(TEXT("masks"), ScatterMasks);
	TArray<TSharedPtr<FJsonValue>> Targets;
	for (const TPair<FString, TSharedPtr<FScatterTarget>>& Pr : ScatterTargets)
	{
		if (!Pr.Value.IsValid())
		{
			continue;
		}
		TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
		J->SetStringField(TEXT("name"), Pr.Value->Name);
		J->SetStringField(TEXT("class"), UTF8_TO_TCHAR(ScatterClassName(static_cast<EScatterClass>(Pr.Value->Class))));
		J->SetStringField(TEXT("key"), FString::Printf(TEXT("0x%016llx"), static_cast<unsigned long long>(Pr.Value->Key)));
		J->SetNumberField(TEXT("x_m"), Pr.Value->X);
		J->SetNumberField(TEXT("y_m"), Pr.Value->Y);
		J->SetNumberField(TEXT("z_m"), Pr.Value->Z);
		J->SetStringField(TEXT("zone"), Pr.Value->Zone);
		Targets.Add(MakeShared<FJsonValueObject>(J));
	}
	ScatterBlock.SetArrayField(TEXT("targets"), Targets);
}
