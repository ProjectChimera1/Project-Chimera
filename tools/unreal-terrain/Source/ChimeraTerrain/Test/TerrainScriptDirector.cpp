// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Test/TerrainScriptDirector.h"

#include "AssetCompilingManager.h"
#include "ChimeraTerrain.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Data/TerrainIO.h"
#include "Data/TerrainPick.h"
#include "Data/TerrainSimExport.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "Game/ChimeraTerrainPlayerController.h"
#include "Game/RtsCameraPawn.h"
#include "Game/TerrainActor.h"
#include "Game/TerrainLighting.h"
#include "Game/TerrainScatterActor.h"
#include "Data/TerrainScatter.h"
#include "Render/TerrainChunkComponent.h"
#include "Core/RealtimeMeshCollision.h"
#include "CollisionQueryParams.h"
#include "Engine/HitResult.h"
#include "GameFramework/HUD.h"
#include "GameFramework/PlayerController.h"
#include "Camera/PlayerCameraManager.h"
#include "HAL/FileManager.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformMisc.h"
#include "HAL/PlatformProcess.h"
#include "Misc/App.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "ProfilingDebugging/CsvProfiler.h"
#include "RenderingThread.h"
#include "TextureResource.h"
#include "UObject/UObjectGlobals.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "ShaderCompiler.h"
#include "UnrealClient.h"

using namespace ChimeraTerrain;

namespace
{
	double NumField(const FJsonObject& O, const TCHAR* Name, double Default)
	{
		double V = Default;
		return O.TryGetNumberField(Name, V) ? V : Default;
	}

	FString StrField(const FJsonObject& O, const TCHAR* Name, const FString& Default = FString())
	{
		FString V;
		return O.TryGetStringField(Name, V) ? V : Default;
	}

	bool ParseMode(const FString& S, ETerrainBrushMode& Out)
	{
		static const TCHAR* const Names[] = { TEXT("raise"), TEXT("lower"), TEXT("smooth"), TEXT("flatten"), TEXT("paint") };
		for (int32 I = 0; I < 5; ++I)
		{
			if (S.Equals(Names[I], ESearchCase::IgnoreCase))
			{
				Out = static_cast<ETerrainBrushMode>(I);
				return true;
			}
		}
		return false;
	}

	/** [[x, y], ...] -> points (terrain metres). A path may also be "@<name>" (or contain "@<name>" items): the stored scatter_target's XY. */
	bool ParsePath(const FJsonObject& Op, TArray<FVector2D>& Out, const TMap<FString, TSharedPtr<FScatterTarget>>* Targets = nullptr)
	{
		FString Single;
		if (Op.TryGetStringField(TEXT("path"), Single) && Single.StartsWith(TEXT("@")))
		{
			const TSharedPtr<FScatterTarget>* T = Targets ? Targets->Find(Single.Mid(1)) : nullptr;
			if (!T || !T->IsValid())
			{
				return false;
			}
			Out.Add(FVector2D((*T)->X, (*T)->Y));
			return true;
		}
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (!Op.TryGetArrayField(TEXT("path"), Arr) || Arr->Num() == 0)
		{
			return false;
		}
		for (const TSharedPtr<FJsonValue>& V : *Arr)
		{
			FString Ref;
			if (V.IsValid() && V->TryGetString(Ref) && Ref.StartsWith(TEXT("@")))
			{
				const TSharedPtr<FScatterTarget>* T = Targets ? Targets->Find(Ref.Mid(1)) : nullptr;
				if (!T || !T->IsValid())
				{
					return false;
				}
				Out.Add(FVector2D((*T)->X, (*T)->Y));
				continue;
			}
			const TArray<TSharedPtr<FJsonValue>>* P = nullptr;
			if (!V.IsValid() || !V->TryGetArray(P) || P->Num() != 2)
			{
				return false;
			}
			Out.Add(FVector2D((*P)[0]->AsNumber(), (*P)[1]->AsNumber()));
		}
		return true;
	}

	/** Point at fraction T in [0, 1] of the polyline's arc length. */
	FVector2D PathPoint(const TArray<FVector2D>& Path, double T)
	{
		if (Path.Num() == 1)
		{
			return Path[0];
		}
		double Total = 0.0;
		for (int32 I = 1; I < Path.Num(); ++I)
		{
			Total += FVector2D::Distance(Path[I - 1], Path[I]);
		}
		if (Total <= 0.0)
		{
			return Path[0];
		}
		double Want = FMath::Clamp(T, 0.0, 1.0) * Total;
		for (int32 I = 1; I < Path.Num(); ++I)
		{
			const double Seg = FVector2D::Distance(Path[I - 1], Path[I]);
			if (Want <= Seg || I == Path.Num() - 1)
			{
				const double A = Seg > 0.0 ? FMath::Clamp(Want / Seg, 0.0, 1.0) : 0.0;
				return FMath::Lerp(Path[I - 1], Path[I], A);
			}
			Want -= Seg;
		}
		return Path.Last();
	}

	/** Number triple [x, y, r] from a field. */
	bool Triple(const FJsonObject& Op, const TCHAR* Name, double& A, double& B, double& C)
	{
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (!Op.TryGetArrayField(Name, Arr) || Arr->Num() < 3)
		{
			return false;
		}
		A = (*Arr)[0]->AsNumber();
		B = (*Arr)[1]->AsNumber();
		C = (*Arr)[2]->AsNumber();
		return true;
	}

	/**
	 * Normal of the triangulated surface at terrain metres (X, Y): the cell's triangles (BL,TL,TR) and (BL,TR,BR) split along the
	 * BL-TR diagonal, as the mesh and FTerrainHeightfield::SampleSurface.
	 */
	FVector SurfaceNormal(const FTerrainHeightfield& HF, double X, double Y)
	{
		const double E = HF.HalfExtentM();
		const double VX = FMath::Clamp(X + E, 0.0, HF.Width() - 1.000001);
		const double VY = FMath::Clamp(Y + E, 0.0, HF.Width() - 1.000001);
		const int32 CX = FMath::FloorToInt(VX);
		const int32 CY = FMath::FloorToInt(VY);
		const double U = VX - CX;
		const double V = VY - CY;
		const double H00 = HF.GetHeight(CX, CY);
		const double H10 = HF.GetHeight(CX + 1, CY);
		const double H01 = HF.GetHeight(CX, CY + 1);
		const double H11 = HF.GetHeight(CX + 1, CY + 1);
		double Dx;
		double Dy;
		if (V >= U)
		{
			Dx = H11 - H01;
			Dy = H01 - H00;
		}
		else
		{
			Dx = H10 - H00;
			Dy = H11 - H10;
		}
		return FVector(-Dx, -Dy, 1.0).GetSafeNormal();
	}

	TSharedPtr<FJsonValue> PixelsToJson(const TSet<FIntPoint>& Set)
	{
		TArray<FIntPoint> Sorted = Set.Array();
		Sorted.Sort([](const FIntPoint& A, const FIntPoint& B) { return A.Y != B.Y ? A.Y < B.Y : A.X < B.X; });
		TArray<TSharedPtr<FJsonValue>> Arr;
		Arr.Reserve(Sorted.Num());
		for (const FIntPoint& P : Sorted)
		{
			TArray<TSharedPtr<FJsonValue>> XY;
			XY.Add(MakeShared<FJsonValueNumber>(P.X));
			XY.Add(MakeShared<FJsonValueNumber>(P.Y));
			Arr.Add(MakeShared<FJsonValueArray>(XY));
		}
		return MakeShared<FJsonValueArray>(Arr);
	}

	TSharedPtr<FJsonValue> VecToJson(const FVector& V)
	{
		TArray<TSharedPtr<FJsonValue>> A;
		A.Add(MakeShared<FJsonValueNumber>(V.X));
		A.Add(MakeShared<FJsonValueNumber>(V.Y));
		A.Add(MakeShared<FJsonValueNumber>(V.Z));
		return MakeShared<FJsonValueArray>(A);
	}

	TSharedPtr<FJsonValue> PairToJson(double A, double B)
	{
		TArray<TSharedPtr<FJsonValue>> Arr;
		Arr.Add(MakeShared<FJsonValueNumber>(A));
		Arr.Add(MakeShared<FJsonValueNumber>(B));
		return MakeShared<FJsonValueArray>(Arr);
	}

	double UsedPhysicalMB()
	{
		return static_cast<double>(FPlatformMemory::GetStats().UsedPhysical) / (1024.0 * 1024.0);
	}

	FIntPoint ViewportSize()
	{
		if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
		{
			return GEngine->GameViewport->Viewport->GetSizeXY();
		}
		return FIntPoint(0, 0);
	}

	/** Terrain-space ray visibility and screen projection for a camera (the G1 lambdas, shared by the footprint ops). */
	struct FScreenProjector
	{
		const FTerrainHeightfield& HF;
		APlayerController* PC;
		FVector Cam;
		FIntPoint Viewport;

		FScreenProjector(const FTerrainHeightfield& InHF, APlayerController* InPC, const FVector& InCam, const FIntPoint& InViewport)
			: HF(InHF), PC(InPC), Cam(InCam), Viewport(InViewport)
		{
		}

		/** True when nothing of the terrain lies between the camera and P (0.25 m tolerance). */
		bool Visible(const FVector& P) const
		{
			const FVector D = P - Cam;
			const double Dist = D.Size();
			FTerrainHit Hit;
			if (!TerrainPick::RayCast(HF, Cam, D / Dist, Dist + 1.0, Hit))
			{
				return true;
			}
			return Hit.Distance >= Dist - 0.25;
		}

		bool Project(const FVector& P, FIntPoint& Out) const
		{
			FVector2D S;
			if (!PC->ProjectWorldLocationToScreen(P * 100.0, S, false))
			{
				return false;
			}
			Out = FIntPoint(FMath::FloorToInt(S.X), FMath::FloorToInt(S.Y));
			return Out.X >= 0 && Out.Y >= 0 && Out.X < Viewport.X && Out.Y < Viewport.Y;
		}
	};
}

ATerrainScriptDirector::ATerrainScriptDirector()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.bStartWithTickEnabled = true;
	PrimaryActorTick.TickGroup = TG_PostUpdateWork;
}

FString ATerrainScriptDirector::OutPath(const FString& FileName) const
{
	return FPaths::Combine(Options.OutDir, FileName);
}

bool ATerrainScriptDirector::LoadScript(FString& OutError)
{
	FString Text;
	if (!FFileHelper::LoadFileToString(Text, *Options.ScriptPath))
	{
		OutError = FString::Printf(TEXT("cannot read script %s"), *Options.ScriptPath);
		return false;
	}
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Text);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = FString::Printf(TEXT("script %s is not valid JSON"), *Options.ScriptPath);
		return false;
	}
	ScriptName = StrField(*Root, TEXT("name"), FPaths::GetBaseFilename(Options.ScriptPath));
	{
		// The script's bytes as run (task S5): results.json script_sha256 ties a run to the script that drove it.
		TArray<uint8> ScriptBytes;
		if (FFileHelper::LoadFileToArray(ScriptBytes, *Options.ScriptPath))
		{
			ScriptSha256 = ATerrainScatter::Sha256HexOf(ScriptBytes);
		}
	}
	const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
	if (!Root->TryGetArrayField(TEXT("ops"), Arr) || Arr->Num() == 0)
	{
		OutError = TEXT("script has no ops");
		return false;
	}
	for (const TSharedPtr<FJsonValue>& V : *Arr)
	{
		const TSharedPtr<FJsonObject>* O = nullptr;
		if (!V.IsValid() || !V->TryGetObject(O) || !(*O)->HasTypedField<EJson::String>(TEXT("op")))
		{
			OutError = FString::Printf(TEXT("op %d is not an object with an \"op\" string"), Ops.Num());
			return false;
		}
		Ops.Add(*O);
	}
	return true;
}

void ATerrainScriptDirector::Start(const FChimeraTerrainOptions& InOptions, ATerrainActor* InTerrain, ATerrainLighting* InLighting, bool bTerrainOk)
{
	Options = InOptions;
	Terrain = InTerrain;
	Lighting = InLighting;
	if (Options.OutDir.IsEmpty())
	{
		Options.OutDir = FPaths::ConvertRelativePathToFull(FPaths::Combine(FPaths::ProjectDir(), TEXT("Out"), TEXT("manual")));
	}
	IFileManager::Get().MakeDirectory(*Options.OutDir, true);
	if (!bTerrainOk || !Terrain || !Lighting)
	{
		StartError = TEXT("terrain or lighting failed to initialise");
	}
	else if (!LoadScript(StartError))
	{
		// StartError set.
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("director: script '%s' ops=%d out=%s%s"), *ScriptName, Ops.Num(), *Options.OutDir,
		StartError.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(" START ERROR: %s"), *StartError));
	bStarted = true;
	OpStartSeconds = FPlatformTime::Seconds();
	OpPhaseSeconds = OpStartSeconds;
	RunStartSeconds = OpStartSeconds;
	NextMemSampleSeconds = OpStartSeconds;
}

void ATerrainScriptDirector::SetPhase(const FString& Name)
{
	Metrics.SetPhase(Name);
	if (Terrain)
	{
		Terrain->SetPhase(Name);
	}
	RecordPhaseEvent();
}

void ATerrainScriptDirector::RecordPhaseEvent() const
{
#if CSV_PROFILER
	// parse_terrain.py tags the CSV frames with the phase from these events (the EVENTS column).
	if (FCsvProfiler::IsCapturing())
	{
		FCsvProfiler::RecordEvent(CSV_CATEGORY_INDEX_GLOBAL, FString::Printf(TEXT("phase_%s"), *Metrics.GetPhase()), false);
	}
#endif
}

void ATerrainScriptDirector::SampleMemoryIfDue(bool bForce)
{
	const double Now = FPlatformTime::Seconds();
	if (!bForce && Now < NextMemSampleSeconds)
	{
		return;
	}
	NextMemSampleSeconds = Now + 2.0;
	// Bodies are counted only where plan C 3.8 asks for them (soak) and at forced samples; other runs record UsedPhysical only.
	const bool bCountBodies = bForce || (Walk.bActive && Walk.bSoak);
	Metrics.SampleMemory(Now - RunStartSeconds, bCountBodies, Terrain ? static_cast<double>(Terrain->GetUndoBytes()) / (1024.0 * 1024.0) : -1.0);
}

APlayerController* ATerrainScriptDirector::GetPC() const
{
	UWorld* World = GetWorld();
	return World ? World->GetFirstPlayerController() : nullptr;
}

bool ATerrainScriptDirector::CompileQueuesIdle()
{
	// EXECUTION 2.2 compile-idle wait (ShaderCompiler.h:1214,1371; AssetCompilingManager.h:64).
	const bool bShaders = GShaderCompilingManager == nullptr || GShaderCompilingManager->GetNumRemainingJobs() == 0;
	return bShaders && FAssetCompilingManager::Get().GetNumRemainingAssets() == 0;
}

bool ATerrainScriptDirector::ApplyPose(const FString& PoseName)
{
	FTerrainCameraPose Pose;
	if (!FTerrainCameraPose::Find(PoseName, Pose))
	{
		return false;
	}
	APlayerController* PC = GetPC();
	ARtsCameraPawn* Pawn = PC ? Cast<ARtsCameraPawn>(PC->GetPawn()) : nullptr;
	if (!Pawn)
	{
		return false;
	}
	float Aspect = 16.0f / 9.0f;
	if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
	{
		const FIntPoint Size = GEngine->GameViewport->Viewport->GetSizeXY();
		if (Size.X > 0 && Size.Y > 0)
		{
			Aspect = static_cast<float>(Size.X) / static_cast<float>(Size.Y);
		}
	}
	Pawn->ApplyPose(Pose, Aspect);
	CurrentPose = PoseName;
	return true;
}

double ATerrainScriptDirector::OpTimeoutSeconds(const FString& Name, const FJsonObject& Op) const
{
	if (Name == TEXT("settle"))
	{
		return Options.SettleTimeoutS;
	}
	if (Name == TEXT("shot"))
	{
		return 60.0;
	}
	if (Name == TEXT("idle"))
	{
		return NumField(Op, TEXT("seconds"), 0.0) + 120.0;
	}
	if (Name == TEXT("stroke") || Name == TEXT("paint"))
	{
		return 120.0 + NumField(Op, TEXT("ticks"), 1.0) * 0.5;
	}
	if (Name == TEXT("random_walk"))
	{
		return NumField(Op, TEXT("seconds"), 60.0) * 1.5 + 120.0;
	}
	if (Name == TEXT("soak"))
	{
		return NumField(Op, TEXT("minutes"), 5.0) * 60.0 * 1.5 + 180.0;
	}
	if (Name == TEXT("residue"))
	{
		return 2.0 * NumField(Op, TEXT("delay_s"), 12.0) + 120.0;
	}
	if (Name == TEXT("wait_collision"))
	{
		return 30.0 + 5.0;   // the op fails itself with the chunk list at 30 s (plan C 3.8); this is the backstop
	}
	if (Name == TEXT("scatter_wait"))
	{
		return NumField(Op, TEXT("timeout_s"), 120.0) + 30.0;   // the op times out itself at timeout_s; this is the backstop
	}
	if (Name == TEXT("scatter_verify"))
	{
		return 300.0;
	}
	if (Name == TEXT("scatter_fresh"))
	{
		return Options.SettleTimeoutS + 120.0;
	}
	if (Name == TEXT("movie_wait"))
	{
		return 120.0;
	}
	if (Name == TEXT("scatter_dump") || Name == TEXT("scatter_check") || Name == TEXT("scatter_mask") || Name == TEXT("scatter_view_counts"))
	{
		return 300.0;
	}
	return 120.0;
}

void ATerrainScriptDirector::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	if (!bStarted || bFinished)
	{
		return;
	}
	if (!StartError.IsEmpty())
	{
		Finish(ExitOpFailed, StartError);
		return;
	}
	Metrics.SampleFrame(FApp::GetDeltaTime());
	if (Terrain)
	{
		if (ChimeraTerrain::ITerrainChunkRenderer* R = Terrain->GetRenderer())
		{
			R->PollCompletions();
		}
	}
	SampleMemoryIfDue(false);

	// One op may finish and the next start in the same frame only for instantaneous ops; frame-spanning ops yield.
	for (int32 Guard = 0; Guard < 64 && !bFinished; ++Guard)
	{
		if (OpIndex >= Ops.Num())
		{
			Finish(ExitOk, FString());
			return;
		}
		const FJsonObject& Op = *Ops[OpIndex];
		const FString Name = StrField(Op, TEXT("op"));
		const double Now = FPlatformTime::Seconds();
		if (OpFrame == 0 && OpPhase == 0)
		{
			OpStartFrame = GFrameCounter;
			UE_LOG(LogChimeraTerrain, Display, TEXT("op %d/%d %s frame=%llu"), OpIndex + 1, Ops.Num(), *Name, static_cast<unsigned long long>(GFrameCounter));
			const FString PhaseName = StrField(Op, TEXT("phase"));
			if (!PhaseName.IsEmpty())
			{
				SetPhase(PhaseName);
			}
		}
		if (Now - OpStartSeconds > OpTimeoutSeconds(Name, Op))
		{
			Finish(ExitOpTimeout, FString::Printf(TEXT("op %d (%s) timed out after %.0f s (phase %d)"), OpIndex + 1, *Name, Now - OpStartSeconds, OpPhase));
			return;
		}
		OpError.Reset();
		const EStep Step = StepOp(Op, Name);
		++OpFrame;
		if (Step == EStep::Running)
		{
			return;
		}
		if (Step == EStep::Failed)
		{
			Finish(ExitOpFailed, FString::Printf(TEXT("op %d (%s) failed: %s"), OpIndex + 1, *Name, *OpError));
			return;
		}
		if (Step == EStep::TimedOut)
		{
			Finish(ExitOpTimeout, FString::Printf(TEXT("op %d (%s) timed out: %s"), OpIndex + 1, *Name, *OpError));
			return;
		}
		// Done.
		TSharedRef<FJsonObject> T = MakeShared<FJsonObject>();
		T->SetNumberField(TEXT("index"), OpIndex + 1);
		T->SetStringField(TEXT("op"), Name);
		T->SetNumberField(TEXT("seconds"), FPlatformTime::Seconds() - OpStartSeconds);
		T->SetNumberField(TEXT("frames"), OpFrame);
		// Game frames (GFrameCounter) of the op's first and last step: the parser places the scatter's frame-stamped proxy and compile events in op windows.
		T->SetNumberField(TEXT("frame_start"), static_cast<double>(OpStartFrame));
		T->SetNumberField(TEXT("frame_end"), static_cast<double>(GFrameCounter));
		{
			const FString OpName = StrField(Op, TEXT("name"));
			if (!OpName.IsEmpty())
			{
				T->SetStringField(TEXT("name"), OpName);
			}
		}
		Timeline.Add(MakeShared<FJsonValueObject>(T));
		if (bFinished)
		{
			return;
		}
		++OpIndex;
		OpFrame = 0;
		OpPhase = 0;
		OpStartSeconds = FPlatformTime::Seconds();
		OpPhaseSeconds = OpStartSeconds;
		// Ops that waited for frames end the frame here so the next op sees a rendered frame.
		if (T->GetNumberField(TEXT("frames")) > 1)
		{
			return;
		}
	}
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepOp(const FJsonObject& Op, const FString& Name)
{
	if (Name == TEXT("camera")) return StepCamera(Op);
	if (Name == TEXT("look")) return StepLook(Op);
	if (Name == TEXT("visible")) return StepVisible(Op);
	if (Name == TEXT("settle")) return StepSettle(Op);
	if (Name == TEXT("idle")) return StepIdle(Op);
	if (Name == TEXT("stroke")) return StepStroke(Op, false);
	if (Name == TEXT("paint")) return StepStroke(Op, true);
	if (Name == TEXT("hash")) return StepHash(Op);
	if (Name == TEXT("shot")) return StepShot(Op);
	if (Name == TEXT("g1_regions")) return StepG1Regions(Op);
	if (Name == TEXT("undo")) return StepUndoRedo(Op, true);
	if (Name == TEXT("redo")) return StepUndoRedo(Op, false);
	if (Name == TEXT("hitch")) return StepHitch(Op);
	if (Name == TEXT("temporal_freeze")) return StepTemporalFreeze(Op);
	if (Name == TEXT("save")) return StepSave(Op);
	if (Name == TEXT("load")) return StepLoad(Op);
	if (Name == TEXT("random_walk")) return StepRandomWalk(Op, false);
	if (Name == TEXT("soak")) return StepRandomWalk(Op, true);
	if (Name == TEXT("gc")) return StepGc(Op);
	if (Name == TEXT("residue")) return StepResidue(Op);
	if (Name == TEXT("csv")) return StepCsv(Op);
	if (Name == TEXT("movie")) return StepMovie(Op);
	if (Name == TEXT("depthcheck")) return StepDepthCheck(Op);
	if (Name == TEXT("project_footprint")) return StepProjectFootprint(Op);
	if (Name == TEXT("await_mouse")) return StepAwaitMouse(Op);
	if (Name == TEXT("wait_collision")) return StepWaitCollision(Op);
	if (Name == TEXT("verify_collision")) return StepVerifyCollision(Op);
	if (Name == TEXT("scatter")) return StepScatter(Op);
	if (Name == TEXT("scatter_wait")) return StepScatterWait(Op);
	if (Name == TEXT("scatter_visible")) return StepScatterVisible(Op);
	if (Name == TEXT("scatter_verify")) return StepScatterVerify(Op);
	if (Name == TEXT("scatter_fresh")) return StepScatterFresh(Op);
	if (Name == TEXT("scatter_dump")) return StepScatterDump(Op);
	if (Name == TEXT("scatter_check")) return StepScatterCheck(Op);
	if (Name == TEXT("scatter_counts")) return StepScatterCounts(Op);
	if (Name == TEXT("scatter_view_counts")) return StepScatterViewCounts(Op);
	if (Name == TEXT("scatter_target")) return StepScatterTarget(Op);
	if (Name == TEXT("scatter_mask")) return StepScatterMask(Op);
	if (Name == TEXT("movie_start")) return StepMovieStart(Op);
	if (Name == TEXT("movie_wait")) return StepMovieWait(Op);
	if (Name == TEXT("fail"))
	{
		OpError = TEXT("fail op (exit-code contract test)");
		return EStep::Failed;
	}
	if (Name == TEXT("exit"))
	{
		Finish(ExitOk, FString());
		return EStep::Done;
	}
	OpError = FString::Printf(TEXT("unknown op '%s'"), *Name);
	return EStep::Failed;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepCamera(const FJsonObject& Op)
{
	if (OpFrame == 0)
	{
		const FString Pose = StrField(Op, TEXT("pose"));
		if (!ApplyPose(Pose))
		{
			OpError = FString::Printf(TEXT("unknown pose '%s' or no camera pawn"), *Pose);
			return EStep::Failed;
		}
	}
	// Two frames for the camera manager and the view to follow.
	return OpFrame >= 2 ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepLook(const FJsonObject& Op)
{
	if (OpFrame == 0)
	{
		const FString Mode = StrField(Op, TEXT("mode"));
		const bool bCompare = Mode.Equals(TEXT("compare"), ESearchCase::IgnoreCase);
		if (!bCompare && !Mode.Equals(TEXT("full"), ESearchCase::IgnoreCase))
		{
			OpError = FString::Printf(TEXT("look mode '%s' (compare | full)"), *Mode);
			return EStep::Failed;
		}
		Lighting->SetCompareMode(bCompare, Options.CompareEV100);
		// Plan C 3.5 compare mode: BrushRadius = 0 (no ring in numeric shots).
		Terrain->SetBrushRingEnabled(!bCompare);
		if (APlayerController* PC = GetPC())
		{
			if (PC->MyHUD)
			{
				PC->MyHUD->bShowHUD = !bCompare;
			}
		}
	}
	return OpFrame >= 2 ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepVisible(const FJsonObject& Op)
{
	if (OpFrame == 0)
	{
		Terrain->SetTerrainVisible(NumField(Op, TEXT("value"), 1.0) != 0.0);
	}
	return OpFrame >= 2 ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepSettle(const FJsonObject& Op)
{
	const int32 Frames = static_cast<int32>(NumField(Op, TEXT("frames"), 30.0));
	if (OpPhase == 0)
	{
		if (CompileQueuesIdle() && !Terrain->HasPendingWork())
		{
			OpPhase = 1;
			OpPhaseSeconds = FPlatformTime::Seconds();
			ShotStableFrames = 0;
		}
		return EStep::Running;
	}
	if (++ShotStableFrames < Frames)
	{
		return EStep::Running;
	}
	const double Ms = (FPlatformTime::Seconds() - OpStartSeconds) * 1000.0;
	TSharedRef<FJsonObject> S = MakeShared<FJsonObject>();
	S->SetNumberField(TEXT("settle_ms"), Ms);
	S->SetNumberField(TEXT("compile_wait_ms"), (OpPhaseSeconds - OpStartSeconds) * 1000.0);
	S->SetNumberField(TEXT("frames"), Frames);
	Settles.Add(MakeShared<FJsonValueObject>(S));
	UE_LOG(LogChimeraTerrain, Display, TEXT("settle_ms=%.0f (compile wait %.0f ms, %d frames)"), Ms, (OpPhaseSeconds - OpStartSeconds) * 1000.0, Frames);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepIdle(const FJsonObject& Op)
{
	const double Seconds = NumField(Op, TEXT("seconds"), 0.0);
	const int32 Frames = static_cast<int32>(NumField(Op, TEXT("frames"), 0.0));
	if (Seconds > 0.0)
	{
		return FPlatformTime::Seconds() - OpStartSeconds >= Seconds ? EStep::Done : EStep::Running;
	}
	return OpFrame >= Frames ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepStroke(const FJsonObject& Op, bool bForcePaint)
{
	TArray<FVector2D> Path;
	FTerrainBrushParams P;
	bool bModeOk = true;
	if (bForcePaint)
	{
		P.Mode = ETerrainBrushMode::Paint;
	}
	else
	{
		bModeOk = ParseMode(StrField(Op, TEXT("mode")), P.Mode);
	}
	if (!bModeOk || !ParsePath(Op, Path, &ScatterTargets))
	{
		OpError = TEXT("stroke needs mode (raise|lower|smooth|flatten|paint) and path [[x,y],...] (or \"@<scatter_target name>\" of a target taken earlier)");
		return EStep::Failed;
	}
	const FString ModeName = bForcePaint ? FString(TEXT("paint")) : StrField(Op, TEXT("mode"));
	P.DiameterM = static_cast<float>(NumField(Op, TEXT("d"), 20.0));
	P.Strength = static_cast<float>(NumField(Op, TEXT("s"), 10.0));
	P.PaintLayer = static_cast<int32>(NumField(Op, TEXT("layer"), 0.0));
	const int32 Ticks = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("ticks"), 1.0)));
	const int32 PerFrame = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("per_frame"), 1.0)));
	bool bHitchOp = false;
	Op.TryGetBoolField(TEXT("hitch"), bHitchOp);

	if (OpFrame == 0)
	{
		Terrain->BeginStroke(P, Path[0]);
		StrokeTicksDone = 0;
		bStrokeHitched = false;
	}
	for (int32 K = 0; K < PerFrame && StrokeTicksDone < Ticks; ++K)
	{
		if (bHitchOp && Options.HitchMs > 0 && !bStrokeHitched && StrokeTicksDone >= Ticks / 2)
		{
			// P8: a game-thread stall in the middle of a stroke; the tick list must not change (ticks never depend on DeltaTime).
			bStrokeHitched = true;
			const double H0 = FPlatformTime::Seconds();
			FPlatformProcess::Sleep(static_cast<float>(Options.HitchMs) / 1000.0f);
			TSharedRef<FJsonObject> Hj = MakeShared<FJsonObject>();
			Hj->SetStringField(TEXT("source"), TEXT("stroke"));
			Hj->SetNumberField(TEXT("op_index"), OpIndex + 1);
			Hj->SetNumberField(TEXT("tick"), StrokeTicksDone);
			Hj->SetNumberField(TEXT("ms_requested"), Options.HitchMs);
			Hj->SetNumberField(TEXT("ms_slept"), (FPlatformTime::Seconds() - H0) * 1000.0);
			Hitches.Add(MakeShared<FJsonValueObject>(Hj));
			UE_LOG(LogChimeraTerrain, Display, TEXT("hitch %d ms inside stroke op %d at tick %d"), Options.HitchMs, OpIndex + 1, StrokeTicksDone);
		}
		const double T = Ticks > 1 ? static_cast<double>(StrokeTicksDone) / static_cast<double>(Ticks - 1) : 0.0;
		FTerrainTickTiming Timing;
		Terrain->ApplyTick(PathPoint(Path, T), &Timing);
		++StrokeTicksDone;
	}
	if (StrokeTicksDone < Ticks)
	{
		return EStep::Running;
	}
	const bool bPushed = Terrain->EndStroke();
	TSharedRef<FJsonObject> S = MakeShared<FJsonObject>();
	S->SetStringField(TEXT("mode"), ModeName);
	S->SetNumberField(TEXT("d"), P.DiameterM);
	S->SetNumberField(TEXT("s"), P.Strength);
	S->SetNumberField(TEXT("layer"), P.PaintLayer);
	S->SetNumberField(TEXT("ticks"), Ticks);
	S->SetNumberField(TEXT("ticks_applied"), StrokeTicksDone);
	S->SetNumberField(TEXT("ticks_dropped"), 0);
	S->SetNumberField(TEXT("per_frame"), PerFrame);
	S->SetNumberField(TEXT("frames"), OpFrame + 1);
	S->SetBoolField(TEXT("undo_pushed"), bPushed);
	S->SetBoolField(TEXT("hitched"), bStrokeHitched);
	S->SetStringField(TEXT("phase"), Terrain->GetPhase());
	Strokes.Add(MakeShared<FJsonValueObject>(S));

	// Footprint discs for project_footprint: centres every R/2 metres along the path.
	TArray<FString> FpNames;
	FString FpOne;
	const TArray<TSharedPtr<FJsonValue>>* FpArr = nullptr;
	if (Op.TryGetStringField(TEXT("fp"), FpOne) && !FpOne.IsEmpty())
	{
		FpNames.Add(FpOne);
	}
	else if (Op.TryGetArrayField(TEXT("fp"), FpArr))
	{
		for (const TSharedPtr<FJsonValue>& V : *FpArr)
		{
			FpNames.Add(V->AsString());
		}
	}
	if (FpNames.Num() > 0)
	{
		double Len = 0.0;
		for (int32 I = 1; I < Path.Num(); ++I)
		{
			Len += FVector2D::Distance(Path[I - 1], Path[I]);
		}
		const double R = P.DiameterM * 0.5;
		const int32 N = FMath::Max(1, FMath::CeilToInt(Len / FMath::Max(0.5, R * 0.5)));
		for (const FString& SetName : FpNames)
		{
			TArray<FVector>& Set = FootprintSets.FindOrAdd(SetName);
			for (int32 I = 0; I <= N; ++I)
			{
				const FVector2D C = PathPoint(Path, static_cast<double>(I) / N);
				Set.Add(FVector(C.X, C.Y, R));
			}
		}
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("stroke %s d=%.0f s=%.0f ticks=%d ticks_applied=%d undo_pushed=%d"), *ModeName,
		P.DiameterM, P.Strength, Ticks, StrokeTicksDone, bPushed ? 1 : 0);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepHash(const FJsonObject& Op)
{
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("hash%d"), OpIndex + 1));
	TSharedRef<FJsonObject> H = MakeShared<FJsonObject>();
	H->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
	H->SetStringField(TEXT("splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	H->SetNumberField(TEXT("ticks_applied_total"), static_cast<double>(Terrain->GetTicksApplied()));
	if (Terrain->GetHeightfield().HalfExtentM() >= TerrainSimExport::SimGridHalfM)
	{
		TArray<int32> SimRaw;
		TerrainSimExport::BuildSimGrid(Terrain->GetHeightfield(), SimRaw);
		H->SetStringField(TEXT("sim_grid_fnv"), TerrainIO::HashToString(TerrainSimExport::SimGridFnv(SimRaw)));
	}
	if (Scatter && Scatter->IsEnabled())
	{
		// Plan C scatter 3.8: only while scatter is enabled, so old scripts write identical hash JSON.
		Scatter->AddHashFields(*H);
		UE_LOG(LogChimeraTerrain, Display, TEXT("hash %s scatter_fnv=%s scatter_live_fnv=%s scatter_count=%.0f"), *Name, *H->GetStringField(TEXT("scatter_fnv")),
			*H->GetStringField(TEXT("scatter_live_fnv")), H->GetNumberField(TEXT("scatter_count")));
	}
	Hashes->SetObjectField(Name, H);
	UE_LOG(LogChimeraTerrain, Display, TEXT("hash %s height_fnv=%s splat_fnv=%s"), *Name, *H->GetStringField(TEXT("height_fnv")), *H->GetStringField(TEXT("splat_fnv")));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepShot(const FJsonObject& Op)
{
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("shot%d"), OpIndex + 1));
	if (OpFrame == 0)
	{
		ShotStableFrames = 0;
	}
	if (OpPhase == 0)
	{
		// Every outstanding mesh update and splat cleanup has completed, plus 2 frames (plan C 3.8).
		if (Terrain->HasPendingWork())
		{
			ShotStableFrames = 0;
			return EStep::Running;
		}
		if (++ShotStableFrames < 2)
		{
			return EStep::Running;
		}
		ShotPath = FPaths::ConvertRelativePathToFull(OutPath(Name + TEXT(".png")));
		FPaths::NormalizeFilename(ShotPath);
		IFileManager::Get().Delete(*ShotPath, false, true, true);
		ShotRequestUtc = FDateTime::UtcNow();
		ShotLastSize = -1;
		ShotStableFrames = 0;
		APlayerController* PC = GetPC();
		if (!PC)
		{
			OpError = TEXT("no player controller for HighResShot");
			return EStep::Failed;
		}
		const FString Cmd = FString::Printf(TEXT("HighResShot 1920x1080 filename=%s"), *ShotPath);
		PC->ConsoleCommand(Cmd, false);
		UE_LOG(LogChimeraTerrain, Display, TEXT("shot %s requested: %s"), *Name, *Cmd);
		OpPhase = 1;
		return EStep::Running;
	}
	// Screenshot done = the file exists, is non-empty, is newer than the request and its size held for 2 frames (EXECUTION 2.2).
	const int64 Size = IFileManager::Get().FileSize(*ShotPath);
	if (Size <= 0)
	{
		return EStep::Running;
	}
	const FDateTime Stamp = IFileManager::Get().GetTimeStamp(*ShotPath);
	if (Stamp < ShotRequestUtc - FTimespan::FromSeconds(2.0))
	{
		return EStep::Running;
	}
	if (Size != ShotLastSize)
	{
		ShotLastSize = Size;
		ShotStableFrames = 0;
		return EStep::Running;
	}
	if (++ShotStableFrames < 2)
	{
		return EStep::Running;
	}
	TSharedRef<FJsonObject> S = MakeShared<FJsonObject>();
	S->SetStringField(TEXT("path"), ShotPath);
	S->SetNumberField(TEXT("bytes"), static_cast<double>(Size));
	S->SetStringField(TEXT("pose"), CurrentPose);
	S->SetStringField(TEXT("look"), Lighting->IsCompareMode() ? TEXT("compare") : TEXT("full"));
	S->SetBoolField(TEXT("terrain_visible"), Terrain->IsTerrainVisible());
	S->SetNumberField(TEXT("seconds"), FPlatformTime::Seconds() - OpStartSeconds);
	Shots->SetObjectField(Name, S);
	UE_LOG(LogChimeraTerrain, Display, TEXT("shot written %s (%lld bytes)"), *ShotPath, Size);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepG1Regions(const FJsonObject& Op)
{
	const FString PoseName = StrField(Op, TEXT("pose"), TEXT("rts80"));
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
	if (!PC || !PC->PlayerCameraManager)
	{
		OpError = TEXT("no camera manager");
		return EStep::Failed;
	}
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	const FVector Cam = PC->PlayerCameraManager->GetCameraLocation() / 100.0;
	const FVector L = Lighting->GetDirectionToSun();
	FIntPoint Viewport(0, 0);
	if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
	{
		Viewport = GEngine->GameViewport->Viewport->GetSizeXY();
	}

	auto Visible = [&](const FVector& P) -> bool
	{
		const FVector D = P - Cam;
		const double Dist = D.Size();
		FTerrainHit Hit;
		if (!TerrainPick::RayCast(HF, Cam, D / Dist, Dist + 1.0, Hit))
		{
			return true;
		}
		return Hit.Distance >= Dist - 0.25;
	};
	auto SunOccluded = [&](const FVector& P, const FVector& N) -> bool
	{
		FTerrainHit Hit;
		return TerrainPick::RayCast(HF, P + N * 0.05 + L * 0.05, L, 2000.0, Hit);
	};
	auto Project = [&](const FVector& P, FIntPoint& Out) -> bool
	{
		FVector2D S;
		if (!PC->ProjectWorldLocationToScreen(P * 100.0, S, false))
		{
			return false;
		}
		Out = FIntPoint(FMath::FloorToInt(S.X), FMath::FloorToInt(S.Y));
		return Out.X >= 0 && Out.Y >= 0 && Out.X < Viewport.X && Out.Y < Viewport.Y;
	};

	TSharedRef<FJsonObject> PoseObj = MakeShared<FJsonObject>();
	PoseObj->SetField(TEXT("camera_m"), VecToJson(Cam));
	PoseObj->SetField(TEXT("sun_dir"), VecToJson(L));
	TArray<TSharedPtr<FJsonValue>> VP;
	VP.Add(MakeShared<FJsonValueNumber>(Viewport.X));
	VP.Add(MakeShared<FJsonValueNumber>(Viewport.Y));
	PoseObj->SetArrayField(TEXT("viewport"), VP);

	double HX, HY, HR;
	double SX, SY, SR;
	if (Triple(Op, TEXT("hill"), HX, HY, HR))
	{
		double RingA = 5.0;
		double RingB = 40.0;
		const TArray<TSharedPtr<FJsonValue>>* Ring = nullptr;
		if (Op.TryGetArrayField(TEXT("ring"), Ring) && Ring->Num() == 2)
		{
			RingA = (*Ring)[0]->AsNumber();
			RingB = (*Ring)[1]->AsNumber();
		}
		const double Step = NumField(Op, TEXT("step"), 0.25);
		// The cast shadow can reach h / tan(sun elevation) beyond the centre; sample far enough for it.
		const double SunElev = FMath::Asin(FMath::Clamp(L.Z, 0.05, 1.0));
		const double Peak = HF.SampleSurface(HX, HY);
		const double Outer = FMath::Max(HR + RingB, Peak / FMath::Tan(SunElev) + 5.0);
		TSet<FIntPoint> Sun, Shadow, Cast, Lit;
		int64 NSun = 0, NShadow = 0, NCast = 0, NLit = 0;
		for (double Y = HY - Outer; Y <= HY + Outer; Y += Step)
		{
			for (double X = HX - Outer; X <= HX + Outer; X += Step)
			{
				const double Dist = FVector2D::Distance(FVector2D(X, Y), FVector2D(HX, HY));
				if (Dist > Outer)
				{
					continue;
				}
				const double H = HF.SampleSurface(X, Y);
				const FVector P(X, Y, H);
				const FVector N = SurfaceNormal(HF, X, Y);
				const double NdotL = FVector::DotProduct(N, L);
				FIntPoint Px;
				if (Dist <= HR - 1.0 && H > 2.0)
				{
					if (NdotL >= 0.6 && Visible(P) && !SunOccluded(P, N) && Project(P, Px))
					{
						Sun.Add(Px);
						++NSun;
					}
					else if (NdotL <= -0.1 && Visible(P) && Project(P, Px))
					{
						Shadow.Add(Px);
						++NShadow;
					}
				}
				else if (Dist >= HR + 1.0 && FMath::Abs(H) < 0.01 && N.Z > 0.9999)
				{
					const bool bOcc = SunOccluded(P, N);
					if (bOcc && Visible(P) && Project(P, Px))
					{
						Cast.Add(Px);
						++NCast;
					}
					else if (!bOcc && Dist - HR >= RingA && Dist - HR <= RingB && Visible(P) && Project(P, Px))
					{
						Lit.Add(Px);
						++NLit;
					}
				}
			}
		}
		PoseObj->SetField(TEXT("hill_sun"), PixelsToJson(Sun));
		PoseObj->SetField(TEXT("hill_shadow"), PixelsToJson(Shadow));
		PoseObj->SetField(TEXT("cast_shadow"), PixelsToJson(Cast));
		PoseObj->SetField(TEXT("lit_ground"), PixelsToJson(Lit));
		TArray<TSharedPtr<FJsonValue>> Hill;
		Hill.Add(MakeShared<FJsonValueNumber>(HX));
		Hill.Add(MakeShared<FJsonValueNumber>(HY));
		Hill.Add(MakeShared<FJsonValueNumber>(HR));
		Hill.Add(MakeShared<FJsonValueNumber>(Peak));
		PoseObj->SetArrayField(TEXT("hill_xyrh"), Hill);
		UE_LOG(LogChimeraTerrain, Display, TEXT("g1_regions %s hill peak=%.2f m: sun px=%d shadow px=%d cast px=%d lit px=%d (samples %lld/%lld/%lld/%lld)"),
			*PoseName, Peak, Sun.Num(), Shadow.Num(), Cast.Num(), Lit.Num(), NSun, NShadow, NCast, NLit);
	}
	if (Triple(Op, TEXT("spike"), SX, SY, SR))
	{
		const double Step = NumField(Op, TEXT("step"), 0.1);
		TSet<FIntPoint> Spike;
		const double Peak = HF.SampleSurface(SX, SY);
		for (double Y = SY - SR; Y <= SY + SR; Y += Step)
		{
			for (double X = SX - SR; X <= SX + SR; X += Step)
			{
				const double H = HF.SampleSurface(X, Y);
				if (H <= 1.0)
				{
					continue;
				}
				const FVector P(X, Y, H);
				FIntPoint Px;
				if (Visible(P) && Project(P, Px))
				{
					Spike.Add(Px);
				}
			}
		}
		PoseObj->SetField(TEXT("spike"), PixelsToJson(Spike));
		TArray<TSharedPtr<FJsonValue>> Sp;
		Sp.Add(MakeShared<FJsonValueNumber>(SX));
		Sp.Add(MakeShared<FJsonValueNumber>(SY));
		Sp.Add(MakeShared<FJsonValueNumber>(SR));
		Sp.Add(MakeShared<FJsonValueNumber>(Peak));
		PoseObj->SetArrayField(TEXT("spike_xyrh"), Sp);
		// The base chunk and its exact bounds, so the parser can show the base box sits outside the frustum.
		const int32 SpikeChunk = HF.OwnerChunkOfVertex(FMath::RoundToInt(SX + HF.HalfExtentM()), FMath::RoundToInt(SY + HF.HalfExtentM()));
		PoseObj->SetNumberField(TEXT("spike_chunk"), SpikeChunk);
		UE_LOG(LogChimeraTerrain, Display, TEXT("g1_regions %s spike peak=%.2f m chunk=%d projected px=%d"), *PoseName, Peak, SpikeChunk, Spike.Num());
	}
	Footprints->SetObjectField(PoseName, PoseObj);
	if (!WriteJsonFile(TEXT("footprints.json"), Footprints))
	{
		OpError = TEXT("cannot write footprints.json");
		return EStep::Failed;
	}
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepUndoRedo(const FJsonObject& Op, bool bUndo)
{
	const int32 N = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("n"), 1.0)));
	const TCHAR* const What = bUndo ? TEXT("undo") : TEXT("redo");
	for (int32 I = 0; I < N; ++I)
	{
		const bool bOk = bUndo ? Terrain->UndoLast() : Terrain->RedoLast();
		if (!bOk)
		{
			OpError = FString::Printf(TEXT("%s %d of %d: nothing to %s, or a stroke is open"), What, I + 1, N, What);
			return EStep::Failed;
		}
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetStringField(TEXT("op"), What);
	J->SetNumberField(TEXT("n"), N);
	J->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
	J->SetStringField(TEXT("splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	UndoRedos.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("%s x%d height_fnv=%s splat_fnv=%s"), What, N, *J->GetStringField(TEXT("height_fnv")), *J->GetStringField(TEXT("splat_fnv")));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepHitch(const FJsonObject& Op)
{
	const int32 Ms = FMath::Clamp(static_cast<int32>(NumField(Op, TEXT("ms"), 100.0)), 0, 10000);
	const double T0 = FPlatformTime::Seconds();
	FPlatformProcess::Sleep(static_cast<float>(Ms) / 1000.0f);
	TSharedRef<FJsonObject> Hj = MakeShared<FJsonObject>();
	Hj->SetStringField(TEXT("source"), TEXT("op"));
	Hj->SetNumberField(TEXT("op_index"), OpIndex + 1);
	Hj->SetNumberField(TEXT("ms_requested"), Ms);
	Hj->SetNumberField(TEXT("ms_slept"), (FPlatformTime::Seconds() - T0) * 1000.0);
	Hitches.Add(MakeShared<FJsonValueObject>(Hj));
	UE_LOG(LogChimeraTerrain, Display, TEXT("hitch %d ms"), Ms);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepTemporalFreeze(const FJsonObject& Op)
{
	// Engine: SceneVisibility.cpp r.Test.FreezeTemporalSequences (stops ViewState->FrameIndex and the TSR sample index advancing) and
	// r.TemporalAA.Debug.OverrideTemporalIndex (pins the TSR jitter index), both under !UE_BUILD_SHIPPING. A static scene then renders the same frame every
	// frame within one freeze window, so two shots of it agree to a fraction of 1/255 and SX10/SX18 see staleness rather than TSR and screen-space noise.
	// Frames of different windows are not comparable (each window pins a different frame index). Render state only.
	const bool bOn = NumField(Op, TEXT("value"), 1.0) != 0.0;
	static const TCHAR* const Names[2] = {TEXT("r.Test.FreezeTemporalSequences"), TEXT("r.TemporalAA.Debug.OverrideTemporalIndex")};
	static const TCHAR* const OnValues[2] = {TEXT("1"), TEXT("0")};
	static const TCHAR* const OffValues[2] = {TEXT("0"), TEXT("-1")};
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	J->SetNumberField(TEXT("value"), bOn ? 1 : 0);
	J->SetNumberField(TEXT("frame"), static_cast<double>(GFrameCounter));
	bool bAll = true;
	TSharedRef<FJsonObject> Rb = MakeShared<FJsonObject>();
	for (int32 K = 0; K < 2; ++K)
	{
		IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(Names[K]);
		if (!Cv)
		{
			bAll = false;
			continue;
		}
		Cv->Set(bOn ? OnValues[K] : OffValues[K], ECVF_SetByCode);
		Rb->SetStringField(Names[K], Cv->GetString());
	}
	J->SetBoolField(TEXT("available"), bAll);
	J->SetObjectField(TEXT("readback"), Rb);
	TemporalFreezes.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("temporal_freeze %d available=%d"), bOn ? 1 : 0, bAll ? 1 : 0);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepSave(const FJsonObject& Op)
{
	FString Err;
	if (!Terrain->SaveTo(Options.OutDir, Err))
	{
		OpError = Err;
		return EStep::Failed;
	}
	Saved = MakeShared<FJsonObject>();
	Saved->SetStringField(TEXT("dir"), Options.OutDir);
	Saved->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
	Saved->SetStringField(TEXT("splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	FString SimText = TEXT("n/a");
	if (Terrain->GetHeightfield().HalfExtentM() >= TerrainSimExport::SimGridHalfM)
	{
		TArray<int32> Raw;
		TerrainSimExport::BuildSimGrid(Terrain->GetHeightfield(), Raw);
		SimText = TerrainIO::HashToString(TerrainSimExport::SimGridFnv(Raw));
		Saved->SetStringField(TEXT("sim_grid_fnv"), SimText);
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("save %s height_fnv=%s splat_fnv=%s sim_grid_fnv=%s"), *Options.OutDir, *Saved->GetStringField(TEXT("height_fnv")),
		*Saved->GetStringField(TEXT("splat_fnv")), *SimText);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepLoad(const FJsonObject& Op)
{
	FString Dir = StrField(Op, TEXT("dir"));
	if (Dir.IsEmpty())
	{
		Dir = Options.LoadDir;
	}
	if (Dir.IsEmpty())
	{
		OpError = TEXT("load needs \"dir\" or -ChimeraTerrainLoad=<absolute dir>");
		return EStep::Failed;
	}
	FString Err;
	if (!Terrain->LoadFrom(Dir, Err))
	{
		OpError = FString::Printf(TEXT("load %s: %s"), *Dir, *Err);
		return EStep::Failed;
	}
	Loaded = MakeShared<FJsonObject>();
	Loaded->SetStringField(TEXT("dir"), Dir);
	Loaded->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
	Loaded->SetStringField(TEXT("splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	UE_LOG(LogChimeraTerrain, Display, TEXT("load %s height_fnv=%s splat_fnv=%s"), *Dir, *Loaded->GetStringField(TEXT("height_fnv")), *Loaded->GetStringField(TEXT("splat_fnv")));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepRandomWalk(const FJsonObject& Op, bool bSoak)
{
	constexpr int32 MaxCatchUpTicks = 15; // plan C 3.4: at most 0.5 s of ticks per frame; a longer hitch drops ticks
	if (OpFrame == 0)
	{
		Walk = FWalkState();
		Walk.bActive = true;
		Walk.bSoak = bSoak;
		Walk.Rng.Initialize(static_cast<int32>(NumField(Op, TEXT("seed"), 1.0)));
		Walk.DurationS = bSoak ? NumField(Op, TEXT("minutes"), 5.0) * 60.0 : NumField(Op, TEXT("seconds"), 60.0);
		Walk.CycleS = FMath::Max(0.5, NumField(Op, TEXT("cycle_s"), 5.0));
		Walk.SpeedMps = NumField(Op, TEXT("speed"), 8.0);
		Walk.BoundsM = FMath::Clamp(NumField(Op, TEXT("bounds"), 100.0), 5.0, static_cast<double>(Terrain->GetHeightfield().HalfExtentM()) - 5.0);
		Walk.Hz = FMath::Clamp(static_cast<int32>(NumField(Op, TEXT("hz"), 30.0)), 1, 240);
		Walk.Strength = static_cast<float>(NumField(Op, TEXT("s"), 10.0));
		const TArray<TSharedPtr<FJsonValue>>* D = nullptr;
		if (Op.TryGetArrayField(TEXT("diameters"), D) && D->Num() > 0)
		{
			for (const TSharedPtr<FJsonValue>& V : *D)
			{
				Walk.Diameters.Add(static_cast<float>(V->AsNumber()));
			}
		}
		else
		{
			Walk.Diameters = { 5.0f, 20.0f, 60.0f, 100.0f };
		}
		Walk.Pos = FVector2D(Walk.Rng.FRandRange(-Walk.BoundsM, Walk.BoundsM), Walk.Rng.FRandRange(-Walk.BoundsM, Walk.BoundsM));
		Walk.Heading = Walk.Rng.FRandRange(0.0, 2.0 * PI);
		if (bSoak)
		{
			SampleMemoryIfDue(true);
			Metrics.MarkMemoryBaseline();
		}
		UE_LOG(LogChimeraTerrain, Display, TEXT("%s: %.0f s seed=%d cycle=%.1f s hz=%d strength=%.0f bounds=%.0f m"), bSoak ? TEXT("soak") : TEXT("random_walk"),
			Walk.DurationS, static_cast<int32>(NumField(Op, TEXT("seed"), 1.0)), Walk.CycleS, Walk.Hz, Walk.Strength, Walk.BoundsM);
		return EStep::Running;
	}

	const double Dt = FApp::GetDeltaTime();
	Walk.ElapsedS += Dt;
	const bool bDone = Walk.ElapsedS >= Walk.DurationS;
	if (!bDone)
	{
		// One mode per cycle_s; inside it every diameter in turn (cycle_s / N each), so every diameter meets every mode in a 60 s walk
		// (plan C 3.8: the five modes every 5 s at d 5/20/60/100). A new stroke (and undo entry) starts at each slot.
		const int32 NumD = Walk.Diameters.Num();
		const double SlotS = Walk.CycleS / NumD;
		const int32 Slot = FMath::FloorToInt(Walk.ElapsedS / SlotS);
		if (Slot != Walk.Segment)
		{
			if (Terrain->IsStrokeOpen())
			{
				Terrain->EndStroke();
			}
			Walk.Segment = Slot;
			++Walk.Segments;
			FTerrainBrushParams P;
			P.Mode = static_cast<ETerrainBrushMode>((Slot / NumD) % 5);
			P.DiameterM = Walk.Diameters[Slot % NumD];
			P.Strength = Walk.Strength;
			if (P.Mode == ETerrainBrushMode::Paint)
			{
				// Layers 1..3: painting layer 0 over the all-grass start changes no byte, so it would exercise no splat upload.
				P.PaintLayer = 1 + (Walk.PaintSegments++ % (SplatLayerCount - 1));
			}
			Terrain->BeginStroke(P, Walk.Pos);
		}
		Walk.Accumulator += Dt;
		int32 Due = FMath::FloorToInt(Walk.Accumulator * Walk.Hz);
		if (Due > MaxCatchUpTicks)
		{
			Walk.Dropped += Due - MaxCatchUpTicks;
			Due = MaxCatchUpTicks;
			Walk.Accumulator = 0.0;
		}
		else
		{
			Walk.Accumulator -= static_cast<double>(Due) / Walk.Hz;
		}
		for (int32 I = 0; I < Due; ++I)
		{
			Walk.Heading += Walk.Rng.FRandRange(-0.35, 0.35);
			Walk.Pos.X += FMath::Cos(Walk.Heading) * Walk.SpeedMps / Walk.Hz;
			Walk.Pos.Y += FMath::Sin(Walk.Heading) * Walk.SpeedMps / Walk.Hz;
			if (FMath::Abs(Walk.Pos.X) > Walk.BoundsM)
			{
				Walk.Pos.X = FMath::Clamp(Walk.Pos.X, -Walk.BoundsM, Walk.BoundsM);
				Walk.Heading = PI - Walk.Heading;
			}
			if (FMath::Abs(Walk.Pos.Y) > Walk.BoundsM)
			{
				Walk.Pos.Y = FMath::Clamp(Walk.Pos.Y, -Walk.BoundsM, Walk.BoundsM);
				Walk.Heading = -Walk.Heading;
			}
			FTerrainTickTiming Timing;
			Terrain->ApplyTick(Walk.Pos, &Timing);
			++Walk.Ticks;
		}
		return EStep::Running;
	}

	if (Terrain->IsStrokeOpen())
	{
		Terrain->EndStroke();
	}
	TSharedRef<FJsonObject> W = MakeShared<FJsonObject>();
	W->SetStringField(TEXT("op"), bSoak ? TEXT("soak") : TEXT("random_walk"));
	W->SetNumberField(TEXT("seconds"), Walk.ElapsedS);
	W->SetNumberField(TEXT("ticks_applied"), static_cast<double>(Walk.Ticks));
	W->SetNumberField(TEXT("ticks_dropped"), static_cast<double>(Walk.Dropped));
	W->SetNumberField(TEXT("segments"), Walk.Segments);
	W->SetNumberField(TEXT("hz"), Walk.Hz);
	W->SetStringField(TEXT("phase"), Terrain->GetPhase());
	Walks.Add(MakeShared<FJsonValueObject>(W));
	UE_LOG(LogChimeraTerrain, Display, TEXT("%s done: %.1f s ticks=%lld dropped=%lld segments=%d"), bSoak ? TEXT("soak") : TEXT("random_walk"), Walk.ElapsedS, Walk.Ticks, Walk.Dropped, Walk.Segments);
	Walk.bActive = false;
	if (bSoak)
	{
		// One blocking gc and a final sample (plan C 3.8); the samples before it were taken with no forced GC.
		SampleMemoryIfDue(true);
		const EStep R = StepGc(Op);
		// P6 reads its after-GC figures from this sample by index; sampling continues every 2 s after the soak.
		Metrics.MarkAfterGc();
		AfterGcSeconds = FPlatformTime::Seconds();
		return R;
	}
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepResidue(const FJsonObject& Op)
{
	// Reported only, after every gated sample (C5 rework). UE's Mimalloc keeps freed pages in the process for mi.MemoryResetDelay ms
	// (MallocMimalloc.cpp:33, default 10000) and UsedPhysical is the working set (WindowsPlatformMemory.cpp:342), so a sample taken
	// milliseconds after a free cannot show the release. Phase 0: wait delay_s (default 12) after the soak's GC, then a DELAYED sample
	// with the undo history still held. Phase 1: clear the history (FTerrainUndo::Clear), a second blocking GC (UObjectGlobals.h
	// CollectGarbage), FMemory::Trim (UnrealMemory.h:229), an immediate sample, then wait delay_s again. Phase 2: the RESIDUE sample.
	// Never feeds P6's gated figures (TerrainMetrics.cpp MemoryToJson's window ends at the after-GC sample).
	const double DelayS = FMath::Max(0.0, NumField(Op, TEXT("delay_s"), 12.0));
	const double Now = FPlatformTime::Seconds();
	auto UndoMb = [this]() { return Terrain ? static_cast<double>(Terrain->GetUndoBytes()) / (1024.0 * 1024.0) : -1.0; };
	if (OpFrame == 0)
	{
		OpPhase = 0;
		OpPhaseSeconds = AfterGcSeconds > 0.0 ? AfterGcSeconds : Now;
	}
	if (Now - OpPhaseSeconds < DelayS)
	{
		return EStep::Running;
	}
	if (OpPhase == 0)
	{
		Metrics.SampleMemory(Now - RunStartSeconds, true, UndoMb());
		Metrics.MarkDelayed();
		UE_LOG(LogChimeraTerrain, Display, TEXT("residue: delayed sample %.1f s after the soak gc: used %.0f MB, undo %.1f MB"), Now - OpPhaseSeconds, UsedPhysicalMB(), UndoMb());
		ResidueUsedBeforeMb = UsedPhysicalMB();
		ResidueUndoClearedMb = UndoMb();
		if (Terrain)
		{
			Terrain->ClearUndoHistory();
		}
		const double T0 = FPlatformTime::Seconds();
		CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS, true);
		FMemory::Trim(true);
		ResidueGcMs = (FPlatformTime::Seconds() - T0) * 1000.0;
		Metrics.SampleMemory(FPlatformTime::Seconds() - RunStartSeconds, true, UndoMb());
		ResidueUsedImmediateMb = UsedPhysicalMB();
		OpPhase = 1;
		OpPhaseSeconds = FPlatformTime::Seconds();
		return EStep::Running;
	}
	Metrics.SampleMemory(Now - RunStartSeconds, true, UndoMb());
	Metrics.MarkResidue();
	NextMemSampleSeconds = FPlatformTime::Seconds() + 2.0;
	TSharedRef<FJsonObject> G = MakeShared<FJsonObject>();
	G->SetStringField(TEXT("kind"), TEXT("residue"));
	G->SetNumberField(TEXT("ms"), ResidueGcMs);
	G->SetNumberField(TEXT("delay_s"), DelayS);
	G->SetNumberField(TEXT("used_mb_before"), ResidueUsedBeforeMb);
	G->SetNumberField(TEXT("used_mb_immediate"), ResidueUsedImmediateMb);
	G->SetNumberField(TEXT("used_mb_after"), UsedPhysicalMB());
	G->SetNumberField(TEXT("undo_mb_cleared"), ResidueUndoClearedMb);
	Gcs.Add(MakeShared<FJsonValueObject>(G));
	UE_LOG(LogChimeraTerrain, Display, TEXT("residue: undo %.1f MB cleared, gc+trim %.1f ms, used %.0f -> %.0f MB at once -> %.0f MB after %.0f s"),
		ResidueUndoClearedMb, ResidueGcMs, ResidueUsedBeforeMb, ResidueUsedImmediateMb, UsedPhysicalMB(), DelayS);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepGc(const FJsonObject& Op)
{
	const double BeforeMb = UsedPhysicalMB();
	const double T0 = FPlatformTime::Seconds();
	CollectGarbage(GARBAGE_COLLECTION_KEEPFLAGS, true);
	const double Ms = (FPlatformTime::Seconds() - T0) * 1000.0;
	Metrics.SampleMemory(FPlatformTime::Seconds() - RunStartSeconds, true, Terrain ? static_cast<double>(Terrain->GetUndoBytes()) / (1024.0 * 1024.0) : -1.0);
	NextMemSampleSeconds = FPlatformTime::Seconds() + 2.0;
	TSharedRef<FJsonObject> G = MakeShared<FJsonObject>();
	G->SetNumberField(TEXT("ms"), Ms);
	G->SetNumberField(TEXT("used_mb_before"), BeforeMb);
	G->SetNumberField(TEXT("used_mb_after"), UsedPhysicalMB());
	Gcs.Add(MakeShared<FJsonValueObject>(G));
	UE_LOG(LogChimeraTerrain, Display, TEXT("gc %.1f ms used %.0f -> %.0f MB"), Ms, BeforeMb, UsedPhysicalMB());
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepCsv(const FJsonObject& Op)
{
	const FString Mode = StrField(Op, TEXT("mode"));
#if CSV_PROFILER
	if (Mode.Equals(TEXT("start"), ESearchCase::IgnoreCase))
	{
		if (OpFrame == 0)
		{
			if (FCsvProfiler::IsCapturing())
			{
				OpError = TEXT("a CSV capture is already running");
				return EStep::Failed;
			}
			// Uncompressed, straight into the out dir (BeginCapture takes a destination folder; CsvProfiler.cpp:3973-3976).
			if (IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(TEXT("csv.CompressionMode")))
			{
				Cv->Set(0);
			}
			CsvStartedPath = FPaths::Combine(Options.OutDir, TEXT("terrain.csv"));
			IFileManager::Get().Delete(*CsvStartedPath, false, true, true);
			FCsvProfiler::Get()->BeginCapture(-1, Options.OutDir, TEXT("terrain.csv"));
			return EStep::Running;
		}
		if (!FCsvProfiler::IsCapturing())
		{
			if (OpFrame > 30)
			{
				OpError = TEXT("CSV capture did not start within 30 frames");
				return EStep::Failed;
			}
			return EStep::Running;
		}
		bCsvActive = true;
		RecordPhaseEvent();
		UE_LOG(LogChimeraTerrain, Display, TEXT("csv start -> %s"), *CsvStartedPath);
		return EStep::Done;
	}
	if (Mode.Equals(TEXT("stop"), ESearchCase::IgnoreCase))
	{
		if (OpFrame == 0)
		{
			if (!FCsvProfiler::IsCapturing())
			{
				OpError = TEXT("csv stop: no capture is running");
				return EStep::Failed;
			}
			CsvFuture = MakeShared<TSharedFuture<FString>>(FCsvProfiler::Get()->EndCapture());
			return EStep::Running;
		}
		if (!CsvFuture.IsValid() || !CsvFuture->IsValid())
		{
			OpError = TEXT("csv stop: EndCapture returned no future");
			return EStep::Failed;
		}
		if (!CsvFuture->IsReady())
		{
			return EStep::Running;
		}
		FString Written = CsvFuture->Get();
		bCsvActive = false;
		FString Final = FPaths::Combine(Options.OutDir, TEXT("terrain.csv"));
		FPaths::NormalizeFilename(Written);
		if (!IFileManager::Get().FileExists(*Final) && IFileManager::Get().FileExists(*Written))
		{
			IFileManager::Get().Move(*Final, *Written, true);
		}
		const int64 Bytes = IFileManager::Get().FileSize(*Final);
		if (Bytes <= 0)
		{
			OpError = FString::Printf(TEXT("csv stop: no CSV at %s (profiler wrote '%s')"), *Final, *Written);
			return EStep::Failed;
		}
		CsvInfo = MakeShared<FJsonObject>();
		CsvInfo->SetStringField(TEXT("path"), Final);
		CsvInfo->SetStringField(TEXT("profiler_path"), Written);
		CsvInfo->SetNumberField(TEXT("bytes"), static_cast<double>(Bytes));
		UE_LOG(LogChimeraTerrain, Display, TEXT("csv stop -> %s (%lld bytes)"), *Final, Bytes);
		return EStep::Done;
	}
	OpError = TEXT("csv needs mode start | stop");
	return EStep::Failed;
#else
	OpError = FString::Printf(TEXT("csv %s: CSV_PROFILER is not compiled into this build"), *Mode);
	return EStep::Failed;
#endif
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepMovie(const FJsonObject& Op)
{
	// GIsDumpingMovie > 0 dumps that many frames as MovieFrame*.png (UnrealClient.cpp:281-289); UI is excluded (GameViewportClient.cpp:2281-2286).
	const int32 Frames = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("frames"), 30.0)));
	const FString Dir = FPaths::ConvertRelativePathToFull(FPaths::ScreenShotDir());
	if (OpFrame == 0)
	{
		MovieRequested = Frames;
		MovieStartUtc = FDateTime::UtcNow();
		GIsDumpingMovie = Frames;
		UE_LOG(LogChimeraTerrain, Display, TEXT("movie %d frames -> %s"), Frames, *Dir);
		return EStep::Running;
	}
	if (GIsDumpingMovie != 0)
	{
		return EStep::Running;
	}
	TArray<FString> Files;
	IFileManager::Get().FindFiles(Files, *FPaths::Combine(Dir, TEXT("MovieFrame*.*")), true, false);
	FString Ext;
	TArray<FString> FreshFiles;
	for (const FString& F : Files)
	{
		// A file is this op's when it was written after the op started and no earlier movie op of this run claimed it (a second movie started
		// within the 2 s timestamp slack would otherwise list the first one's last frames); older MovieFrame files in the folder belong to
		// earlier runs and are listed nowhere, so consumers (C9's video, S6's M9) take exactly movies[].files.
		const FDateTime Stamp = IFileManager::Get().GetTimeStamp(*FPaths::Combine(Dir, F));
		if (!MovieClaimed.Contains(F) && Stamp >= MovieStartUtc - FTimespan::FromSeconds(2.0) && IFileManager::Get().FileSize(*FPaths::Combine(Dir, F)) > 0)
		{
			FreshFiles.Add(F);
		}
	}
	// MovieFrame names carry the process's running frame-dump index (zero padded), so name order is frame order: this op's frames are the first
	// MovieRequested unclaimed ones.
	FreshFiles.Sort();
	const int32 Seen = FreshFiles.Num();
	if (Seen < MovieRequested)
	{
		return EStep::Running; // files are written asynchronously; the op timeout (120 s) fails a run that never gets them
	}
	FreshFiles.SetNum(MovieRequested);
	for (const FString& F : FreshFiles)
	{
		MovieClaimed.Add(F);
		Ext = FPaths::GetExtension(F);
	}
	const int32 Fresh = FreshFiles.Num();
	TSharedRef<FJsonObject> M = MakeShared<FJsonObject>();
	M->SetNumberField(TEXT("frames_requested"), MovieRequested);
	M->SetNumberField(TEXT("files_written"), Fresh);
	M->SetNumberField(TEXT("unclaimed_files_seen"), Seen);
	M->SetStringField(TEXT("dir"), Dir);
	M->SetStringField(TEXT("extension"), Ext);
	TArray<TSharedPtr<FJsonValue>> FileArr;
	for (const FString& F : FreshFiles)
	{
		FileArr.Add(MakeShared<FJsonValueString>(F));
	}
	M->SetArrayField(TEXT("files"), FileArr);
	Movies.Add(MakeShared<FJsonValueObject>(M));
	UE_LOG(LogChimeraTerrain, Display, TEXT("movie done: %d files .%s in %s"), Fresh, *Ext, *Dir);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepDepthCheck(const FJsonObject& Op)
{
	// P11: the GPU mesh equals the CPU heightfield. SceneCapture2D SCS_SceneDepth (EngineTypes.h:540, depth in R, view-space cm) into an
	// RTF_R32f target, read back with ReadLinearColorPixels (UnrealClient.h:162, RCM_MinMax = values unscaled), against the analytic ray's
	// view depth at n seeded pixels.
	constexpr int32 W = 1920;
	constexpr int32 H = 1080;
	const FString PoseName = StrField(Op, TEXT("pose"), TEXT("rts80"));
	const FString Name = StrField(Op, TEXT("name"), PoseName);
	FTerrainCameraPose Pose;
	if (!FTerrainCameraPose::Find(PoseName, Pose))
	{
		OpError = FString::Printf(TEXT("unknown pose '%s'"), *PoseName);
		return EStep::Failed;
	}
	if (OpFrame == 0)
	{
		if (!Capture)
		{
			CaptureTarget = NewObject<UTextureRenderTarget2D>(this, TEXT("DepthCheckTarget"));
			CaptureTarget->RenderTargetFormat = RTF_R32f;
			CaptureTarget->InitAutoFormat(W, H);
			CaptureTarget->UpdateResourceImmediate(true);
			Capture = NewObject<USceneCaptureComponent2D>(this, TEXT("DepthCheckCapture"));
			Capture->TextureTarget = CaptureTarget;
			Capture->CaptureSource = SCS_SceneDepth;
			Capture->bCaptureEveryFrame = false;
			Capture->bCaptureOnMovement = false;
			Capture->PrimitiveRenderMode = ESceneCapturePrimitiveRenderMode::PRM_UseShowOnlyList;
			Capture->ShowOnlyActors.Add(Terrain.Get());
			Capture->RegisterComponent();
		}
		Capture->SetWorldLocationAndRotation(Pose.LocationCm, Pose.Rotation);
		Capture->FOVAngle = ARtsCameraPawn::HFovFromVFov(Pose.VFovDeg, static_cast<float>(W) / static_cast<float>(H));
		return EStep::Running;
	}
	if (OpFrame < 3 || !CompileQueuesIdle() || Terrain->HasPendingWork())
	{
		return EStep::Running;
	}
	// The first capture of a fresh view can come back empty while its pipeline states build: capture twice, read the second.
	Capture->CaptureScene();
	FlushRenderingCommands();
	Capture->CaptureScene();
	FTextureRenderTargetResource* Res = CaptureTarget->GameThread_GetRenderTargetResource();
	TArray<FLinearColor> Px;
	if (!Res || !Res->ReadLinearColorPixels(Px, FReadSurfaceDataFlags(RCM_MinMax, CubeFace_MAX)) || Px.Num() != W * H)
	{
		OpError = FString::Printf(TEXT("depth readback failed (%d pixels)"), Px.Num());
		return EStep::Failed;
	}
	auto GpuHit = [&](int32 X, int32 Y, float& OutDepth) -> bool
	{
		const float D = Px[Y * W + X].R;
		OutDepth = D;
		return FMath::IsFinite(D) && D > 0.0f && D < 1.0e6f;
	};
	int64 FrameHits = 0;
	float MinHit = TNumericLimits<float>::Max();
	float MaxHit = 0.0f;
	for (int32 I = 0; I < W * H; ++I)
	{
		const float D = Px[I].R;
		if (FMath::IsFinite(D) && D > 0.0f && D < 1.0e6f)
		{
			++FrameHits;
			MinHit = FMath::Min(MinHit, D);
			MaxHit = FMath::Max(MaxHit, D);
		}
	}

	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	const FRotationMatrix RM(Pose.Rotation);
	const FVector Fwd = RM.GetUnitAxis(EAxis::X);
	const FVector Right = RM.GetUnitAxis(EAxis::Y);
	const FVector Up = RM.GetUnitAxis(EAxis::Z);
	const double HFov = FMath::DegreesToRadians(Capture->FOVAngle);
	const double TanH = FMath::Tan(HFov * 0.5);
	const double TanV = TanH * static_cast<double>(H) / static_cast<double>(W);
	const FVector CamM = Pose.LocationCm / 100.0;
	const int32 N = FMath::Max(100, static_cast<int32>(NumField(Op, TEXT("n"), 10000.0)));
	FRandomStream Rng(static_cast<int32>(NumField(Op, TEXT("seed"), 1234.0)));

	// One accumulator per pixel set: the uniform full-frame set (the plan's 10k bar) and, with "fp", a seeded set drawn from the projected
	// footprint of that stroke set, so a stale region the size of one chunk quadrant cannot hide under the full-frame p99.
	struct FDepthAcc
	{
		FTerrainSeries Delta;
		FTerrainSeries DeltaNoBand;
		int32 N = 0;
		int32 BothHit = 0;
		int32 BothMiss = 0;
		int32 GpuOnly = 0;
		int32 AnalyticOnly = 0;
		int32 InBand = 0;
		int32 OutBand = 0;
		TArray<TSharedPtr<FJsonValue>> Examples;

		void Write(FJsonObject& O) const
		{
			O.SetNumberField(TEXT("n"), N);
			O.SetNumberField(TEXT("both_hit"), BothHit);
			O.SetNumberField(TEXT("both_miss"), BothMiss);
			O.SetNumberField(TEXT("gpu_only_hit"), GpuOnly);
			O.SetNumberField(TEXT("analytic_only_hit"), AnalyticOnly);
			O.SetNumberField(TEXT("hit_miss_disagree_in_band"), InBand);
			O.SetNumberField(TEXT("hit_miss_disagree_outside_band"), OutBand);
			O.SetObjectField(TEXT("abs_delta_cm"), Delta.ToJson());
			O.SetObjectField(TEXT("abs_delta_cm_outside_band"), DeltaNoBand.ToJson());
			O.SetArrayField(TEXT("disagreement_examples"), Examples);
		}
	};
	auto Evaluate = [&](int32 X, int32 Y, FDepthAcc& A)
	{
		++A.N;
		float GpuD = 0.0f;
		const bool bGpu = GpuHit(X, Y, GpuD);
		const double Nx = (static_cast<double>(X) + 0.5) / W * 2.0 - 1.0;
		const double Ny = 1.0 - (static_cast<double>(Y) + 0.5) / H * 2.0;
		const FVector Dir = (Fwd + Right * (Nx * TanH) + Up * (Ny * TanV)).GetSafeNormal();
		FTerrainHit Hit;
		const bool bAnalytic = TerrainPick::RayCast(HF, CamM, Dir, 5000.0, Hit);
		// Silhouette band: a GPU pixel within 2 px whose hit state differs from this pixel's.
		bool bBand = false;
		for (int32 Dy = -2; Dy <= 2 && !bBand; ++Dy)
		{
			for (int32 Dx = -2; Dx <= 2; ++Dx)
			{
				const int32 Qx = X + Dx;
				const int32 Qy = Y + Dy;
				if (Qx < 0 || Qy < 0 || Qx >= W || Qy >= H)
				{
					continue;
				}
				float Dummy = 0.0f;
				if (GpuHit(Qx, Qy, Dummy) != bGpu)
				{
					bBand = true;
					break;
				}
			}
		}
		if (bGpu && bAnalytic)
		{
			++A.BothHit;
			const double AnalyticCm = Hit.Distance * FVector::DotProduct(Dir, Fwd) * 100.0;
			const double Dd = FMath::Abs(AnalyticCm - static_cast<double>(GpuD));
			A.Delta.Add(Dd);
			if (!bBand)
			{
				A.DeltaNoBand.Add(Dd);
			}
		}
		else if (!bGpu && !bAnalytic)
		{
			++A.BothMiss;
		}
		else
		{
			if (bGpu)
			{
				++A.GpuOnly;
			}
			else
			{
				++A.AnalyticOnly;
			}
			if (bBand)
			{
				++A.InBand;
			}
			else
			{
				++A.OutBand;
			}
			if (A.Examples.Num() < 12)
			{
				TSharedRef<FJsonObject> E = MakeShared<FJsonObject>();
				E->SetNumberField(TEXT("x"), X);
				E->SetNumberField(TEXT("y"), Y);
				E->SetNumberField(TEXT("gpu_cm"), GpuD);
				E->SetBoolField(TEXT("gpu_hit"), bGpu);
				E->SetBoolField(TEXT("analytic_hit"), bAnalytic);
				E->SetBoolField(TEXT("in_band"), bBand);
				A.Examples.Add(MakeShared<FJsonValueObject>(E));
			}
		}
	};

	FDepthAcc Full;
	for (int32 I = 0; I < N; ++I)
	{
		const int32 X = Rng.RandRange(0, W - 1);
		const int32 Y = Rng.RandRange(0, H - 1);
		Evaluate(X, Y, Full);
	}

	TSharedRef<FJsonObject> D = MakeShared<FJsonObject>();
	D->SetStringField(TEXT("name"), Name);
	D->SetStringField(TEXT("pose"), PoseName);
	D->SetStringField(TEXT("height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
	D->SetNumberField(TEXT("width"), W);
	D->SetNumberField(TEXT("height"), H);
	D->SetNumberField(TEXT("frame_gpu_hit_fraction"), static_cast<double>(FrameHits) / (static_cast<double>(W) * H));
	D->SetNumberField(TEXT("frame_gpu_depth_min_cm"), FrameHits > 0 ? MinHit : 0.0);
	D->SetNumberField(TEXT("frame_gpu_depth_max_cm"), MaxHit);
	Full.Write(*D);

	// Footprint-targeted samples: the named stroke set's discs, ground points every `fp_step` m, kept where the terrain does not hide them
	// from the capture camera, projected with the capture's own pinhole (the inverse of the ray maths above), then fp_n seeded draws.
	const FString FpSet = StrField(Op, TEXT("fp"));
	FString FpLog;
	if (!FpSet.IsEmpty())
	{
		const TArray<FVector>* Discs = FootprintSets.Find(FpSet);
		if (!Discs || Discs->Num() == 0)
		{
			OpError = FString::Printf(TEXT("depthcheck: no footprint set '%s' (strokes need \"fp\": \"%s\")"), *FpSet, *FpSet);
			return EStep::Failed;
		}
		const FScreenProjector Vis(HF, nullptr, CamM, FIntPoint(W, H));
		const double Step = FMath::Max(0.05, NumField(Op, TEXT("fp_step"), 0.25));
		double MinX = TNumericLimits<double>::Max();
		double MinY = MinX;
		double MaxX = -MinX;
		double MaxY = -MinX;
		for (const FVector& Disc : *Discs)
		{
			MinX = FMath::Min(MinX, Disc.X - Disc.Z);
			MaxX = FMath::Max(MaxX, Disc.X + Disc.Z);
			MinY = FMath::Min(MinY, Disc.Y - Disc.Z);
			MaxY = FMath::Max(MaxY, Disc.Y + Disc.Z);
		}
		TSet<FIntPoint> PixelSet;
		for (double Yw = MinY; Yw <= MaxY; Yw += Step)
		{
			for (double Xw = MinX; Xw <= MaxX; Xw += Step)
			{
				bool bInside = false;
				for (const FVector& Disc : *Discs)
				{
					const double Dx = Xw - Disc.X;
					const double Dy = Yw - Disc.Y;
					if (Dx * Dx + Dy * Dy <= Disc.Z * Disc.Z)
					{
						bInside = true;
						break;
					}
				}
				if (!bInside)
				{
					continue;
				}
				const FVector P(Xw, Yw, HF.SampleSurface(Xw, Yw));
				const FVector V = P - CamM;
				const double Z = FVector::DotProduct(V, Fwd);
				if (Z <= 0.01)
				{
					continue;
				}
				const double Sx = FVector::DotProduct(V, Right) / (Z * TanH);
				const double Sy = FVector::DotProduct(V, Up) / (Z * TanV);
				const int32 PixX = FMath::FloorToInt((Sx + 1.0) * 0.5 * W);
				const int32 PixY = FMath::FloorToInt((1.0 - Sy) * 0.5 * H);
				if (PixX < 0 || PixY < 0 || PixX >= W || PixY >= H || !Vis.Visible(P))
				{
					continue;
				}
				PixelSet.Add(FIntPoint(PixX, PixY));
			}
		}
		TArray<FIntPoint> Pixels = PixelSet.Array();
		Pixels.Sort([](const FIntPoint& A, const FIntPoint& B) { return A.Y != B.Y ? A.Y < B.Y : A.X < B.X; });
		const int32 FpN = FMath::Max(100, static_cast<int32>(NumField(Op, TEXT("fp_n"), 2000.0)));
		FRandomStream FpRng(static_cast<int32>(NumField(Op, TEXT("fp_seed"), 4321.0)));
		FDepthAcc Fp;
		if (Pixels.Num() > 0)
		{
			for (int32 I = 0; I < FpN; ++I)
			{
				const FIntPoint& Q = Pixels[FpRng.RandRange(0, Pixels.Num() - 1)];
				Evaluate(Q.X, Q.Y, Fp);
			}
		}
		TSharedRef<FJsonObject> F = MakeShared<FJsonObject>();
		F->SetStringField(TEXT("set"), FpSet);
		F->SetNumberField(TEXT("discs"), Discs->Num());
		F->SetNumberField(TEXT("pixels"), Pixels.Num());
		Fp.Write(*F);
		D->SetObjectField(TEXT("footprint"), F);
		FpLog = FString::Printf(TEXT("; footprint %s: %d px, n=%d both_hit=%d p99=%.3f max=%.3f cm, disagree in band %d outside %d"), *FpSet, Pixels.Num(),
			Fp.N, Fp.BothHit, Fp.Delta.Percentile(99.0), Fp.Delta.Max(), Fp.InBand, Fp.OutBand);
	}
	DepthChecks.Add(MakeShared<FJsonValueObject>(D));
	UE_LOG(LogChimeraTerrain, Display, TEXT("depthcheck %s pose=%s n=%d both_hit=%d p50=%.3f p99=%.3f max=%.3f cm, hit/miss disagree in band %d outside %d (frame hit %.1f %%)%s"),
		*Name, *PoseName, N, Full.BothHit, Full.Delta.Percentile(50.0), Full.Delta.Percentile(99.0), Full.Delta.Max(), Full.InBand, Full.OutBand,
		100.0 * FrameHits / (static_cast<double>(W) * H), *FpLog);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepProjectFootprint(const FJsonObject& Op)
{
	const FString PoseName = StrField(Op, TEXT("pose"), TEXT("rts80"));
	const FString SetName = StrField(Op, TEXT("name"));
	const FString OutName = StrField(Op, TEXT("as"), SetName);
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
	const TArray<FVector>* Discs = FootprintSets.Find(SetName);
	if (!Discs || Discs->Num() == 0)
	{
		OpError = FString::Printf(TEXT("no footprint set '%s' (strokes need \"fp\": \"%s\")"), *SetName, *SetName);
		return EStep::Failed;
	}
	APlayerController* PC = GetPC();
	if (!PC || !PC->PlayerCameraManager)
	{
		OpError = TEXT("no camera manager");
		return EStep::Failed;
	}
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();
	const FIntPoint Viewport = ViewportSize();
	const FVector Cam = PC->PlayerCameraManager->GetCameraLocation() / 100.0;
	const FScreenProjector Proj(HF, PC, Cam, Viewport);
	const double Step = FMath::Max(0.05, NumField(Op, TEXT("step"), 0.15));
	double MinX = TNumericLimits<double>::Max();
	double MinY = MinX;
	double MaxX = -MinX;
	double MaxY = -MinX;
	for (const FVector& Disc : *Discs)
	{
		MinX = FMath::Min(MinX, Disc.X - Disc.Z);
		MaxX = FMath::Max(MaxX, Disc.X + Disc.Z);
		MinY = FMath::Min(MinY, Disc.Y - Disc.Z);
		MaxY = FMath::Max(MaxY, Disc.Y + Disc.Z);
	}
	TSet<FIntPoint> Pixels;
	int64 Samples = 0;
	for (double Y = MinY; Y <= MaxY; Y += Step)
	{
		for (double X = MinX; X <= MaxX; X += Step)
		{
			bool bInside = false;
			for (const FVector& Disc : *Discs)
			{
				const double Dx = X - Disc.X;
				const double Dy = Y - Disc.Y;
				if (Dx * Dx + Dy * Dy <= Disc.Z * Disc.Z)
				{
					bInside = true;
					break;
				}
			}
			if (!bInside)
			{
				continue;
			}
			++Samples;
			const FVector P(X, Y, HF.SampleSurface(X, Y));
			FIntPoint Px;
			if (Proj.Visible(P) && Proj.Project(P, Px))
			{
				Pixels.Add(Px);
			}
		}
	}
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
	PoseObj->SetField(TEXT("camera_m"), VecToJson(Cam));
	TArray<TSharedPtr<FJsonValue>> VP;
	VP.Add(MakeShared<FJsonValueNumber>(Viewport.X));
	VP.Add(MakeShared<FJsonValueNumber>(Viewport.Y));
	PoseObj->SetArrayField(TEXT("viewport"), VP);
	PoseObj->SetField(OutName, PixelsToJson(Pixels));
	Footprints->SetObjectField(PoseName, PoseObj);
	if (!WriteJsonFile(TEXT("footprints.json"), Footprints))
	{
		OpError = TEXT("cannot write footprints.json");
		return EStep::Failed;
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("project_footprint %s/%s (set %s): %d discs, %lld ground samples, %d visible pixels"), *PoseName, *OutName, *SetName, Discs->Num(), Samples, Pixels.Num());
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepAwaitMouse(const FJsonObject& Op)
{
	// C8 plugs in here: the controller records one JSON object per finished mouse stroke on the terrain actor; this op publishes where the
	// strokes should be dragged (world targets and their viewport pixels) and waits for them.
	const int32 N = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("n"), 3.0)));
	const FString PoseName = StrField(Op, TEXT("pose"), TEXT("rts80"));
	if (OpFrame == 0)
	{
		MouseTargetCount = N;
		if (!ApplyPose(PoseName))
		{
			OpError = FString::Printf(TEXT("unknown pose '%s'"), *PoseName);
			return EStep::Failed;
		}
		return EStep::Running;
	}
	if (OpPhase == 0)
	{
		if (OpFrame < 3)
		{
			return EStep::Running;
		}
		APlayerController* PC = GetPC();
		if (!PC)
		{
			OpError = TEXT("no player controller");
			return EStep::Failed;
		}
		const FTerrainHeightfield& HF = Terrain->GetHeightfield();
		const FIntPoint Viewport = ViewportSize();
		TArray<TSharedPtr<FJsonValue>> Targets;
		for (int32 I = 0; I < N; ++I)
		{
			// Fixed drags (plan C 8): 30 m long along +X, one per default lane; d 30, s 30, 60 ticks are the controller's.
			const double X0 = -45.0 + 45.0 * I;
			const double Y0 = 30.0;
			const double X1 = X0 + 30.0;
			const FVector A(X0, Y0, HF.SampleSurface(X0, Y0));
			const FVector B(X1, Y0, HF.SampleSurface(X1, Y0));
			FVector2D Sa;
			FVector2D Sb;
			if (!PC->ProjectWorldLocationToScreen(A * 100.0, Sa, false) || !PC->ProjectWorldLocationToScreen(B * 100.0, Sb, false))
			{
				OpError = FString::Printf(TEXT("mouse target %d does not project to the screen"), I);
				return EStep::Failed;
			}
			// P10's footprint mask: discs of the stroke radius (d 30) every 7.5 m along each planned drag, set `mouse`
			// (project_footprint {name: mouse} after the strokes; imgdiff --mask footprint --key rts80/mouse).
			{
				TArray<FVector>& MouseSet = FootprintSets.FindOrAdd(TEXT("mouse"));
				for (int32 K = 0; K <= 4; ++K)
				{
					MouseSet.Add(FVector(X0 + (X1 - X0) * K / 4.0, Y0, 15.0));
				}
			}
			TSharedRef<FJsonObject> T = MakeShared<FJsonObject>();
			T->SetNumberField(TEXT("index"), I);
			T->SetField(TEXT("from_world_m"), VecToJson(A));
			T->SetField(TEXT("to_world_m"), VecToJson(B));
			T->SetField(TEXT("from_px"), PairToJson(Sa.X, Sa.Y));
			T->SetField(TEXT("to_px"), PairToJson(Sb.X, Sb.Y));
			T->SetNumberField(TEXT("d"), 30);
			T->SetNumberField(TEXT("s"), 30);
			T->SetNumberField(TEXT("ticks"), 60);
			Targets.Add(MakeShared<FJsonValueObject>(T));
		}
		TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("pose"), PoseName);
		Root->SetField(TEXT("viewport"), PairToJson(Viewport.X, Viewport.Y));
		Root->SetArrayField(TEXT("strokes"), Targets);
		if (!WriteJsonFile(TEXT("mouse_targets.json"), Root))
		{
			OpError = TEXT("cannot write mouse_targets.json");
			return EStep::Failed;
		}
		// The controller takes mouse strokes and keys only while this op runs (disarmed again below), so no other op of a scripted run
		// can be edited by a stray click or key.
		if (AChimeraTerrainPlayerController* ArmPC = Cast<AChimeraTerrainPlayerController>(PC))
		{
			ArmPC->SetInputArmed(true);
		}
		UE_LOG(LogChimeraTerrain, Display, TEXT("await_mouse: %d targets written, controller armed, waiting for the strokes"), N);
		OpPhase = 1;
		return EStep::Running;
	}
	AChimeraTerrainPlayerController* MousePC = Cast<AChimeraTerrainPlayerController>(GetPC());
	// hitch_ms (with hitch_stroke = index): sleep the game thread once inside that mouse stroke, after 20 of its ticks, to prove the
	// 15-tick catch-up cap and ticks_dropped (plan C 3.4).
	const int32 HitchMsOp = FMath::Clamp(static_cast<int32>(NumField(Op, TEXT("hitch_ms"), 0.0)), 0, 10000);
	if (HitchMsOp > 0 && MousePC && !bMouseHitched && MousePC->IsMouseStrokeOpen()
		&& Terrain->GetMouseStrokeRecords().Num() == static_cast<int32>(NumField(Op, TEXT("hitch_stroke"), 0.0)) && MousePC->GetStrokeTicksApplied() >= 20)
	{
		bMouseHitched = true;
		const double T0 = FPlatformTime::Seconds();
		FPlatformProcess::Sleep(static_cast<float>(HitchMsOp) / 1000.0f);
		TSharedRef<FJsonObject> Hj = MakeShared<FJsonObject>();
		Hj->SetStringField(TEXT("source"), TEXT("await_mouse"));
		Hj->SetNumberField(TEXT("op_index"), OpIndex + 1);
		Hj->SetNumberField(TEXT("ms_requested"), HitchMsOp);
		Hj->SetNumberField(TEXT("ms_slept"), (FPlatformTime::Seconds() - T0) * 1000.0);
		Hj->SetNumberField(TEXT("stroke"), Terrain->GetMouseStrokeRecords().Num());
		Hj->SetNumberField(TEXT("at_stroke_tick"), MousePC->GetStrokeTicksApplied());
		Hitches.Add(MakeShared<FJsonValueObject>(Hj));
		UE_LOG(LogChimeraTerrain, Display, TEXT("hitch %d ms inside mouse stroke %d at tick %d"), HitchMsOp, Terrain->GetMouseStrokeRecords().Num(), MousePC->GetStrokeTicksApplied());
	}
	if (Terrain->GetMouseStrokeRecords().Num() < N)
	{
		return EStep::Running;
	}
	// tail = number of applied key actions expected after the last stroke (undo, redo, brush keys): wait for them too.
	const int32 Tail = FMath::Max(0, static_cast<int32>(NumField(Op, TEXT("tail"), 0.0)));
	if (Tail > 0 && (!MousePC || MousePC->CountKeyActionsAfterStroke(N) < Tail))
	{
		return EStep::Running;
	}
	if (MousePC)
	{
		MousePC->SetInputArmed(false);
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("await_mouse: %d mouse strokes and %d key actions after them recorded; controller disarmed"),
		Terrain->GetMouseStrokeRecords().Num(), MousePC ? MousePC->CountKeyActionsAfterStroke(N) : 0);
	return EStep::Done;
}

bool ATerrainScriptDirector::PhysicsRay(const FVector& OriginM, const FVector& DirM, double MaxM, FVector& OutHitM, bool& OutTerrain,
	const UPrimitiveComponent** OutComponent) const
{
	OutTerrain = false;
	if (OutComponent)
	{
		*OutComponent = nullptr;
	}
	UWorld* World = GetWorld();
	if (!World)
	{
		return false;
	}
	// Complex trace on the Visibility channel, the one channel the chunks block (plan C 3.6; World.h:2161).
	FCollisionQueryParams Params(SCENE_QUERY_STAT(ChimeraTerrainCollision), true);
	if (APlayerController* PC = GetPC())
	{
		if (APawn* Pawn = PC->GetPawn())
		{
			Params.AddIgnoredActor(Pawn);
		}
	}
	FHitResult Hit;
	const FVector Start = OriginM * 100.0;
	const FVector End = (OriginM + DirM * MaxM) * 100.0;
	if (!World->LineTraceSingleByChannel(Hit, Start, End, ECC_Visibility, Params))
	{
		return false;
	}
	OutHitM = Hit.ImpactPoint / 100.0;
	OutTerrain = Cast<UTerrainChunkComponent>(Hit.GetComponent()) != nullptr;
	if (OutComponent)
	{
		*OutComponent = Hit.GetComponent();
	}
	return true;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepWaitCollision(const FJsonObject& Op)
{
	// Plan C 3.8 wait_collision (30 s): all collision futures resolved AND every chunk's body has a trimesh (BodySetup.h:278) AND the
	// component's physics state uses that body AND a vertical probe ray at the chunk centre hits the terrain.
	constexpr double TimeoutS = 30.0;
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("wait%d"), OpIndex + 1));
	ITerrainChunkRenderer* R = Terrain->GetRenderer();
	if (!R)
	{
		OpError = TEXT("no renderer");
		return EStep::Failed;
	}
	R->PollCompletions();
	TArray<FTerrainChunkCollisionState> States;
	R->GetChunkCollisionStates(States);
	if (CollisionSubmitsAtLastWait.Num() != States.Num())
	{
		// First wait of the run: everything submitted since the start counts as touched.
		CollisionSubmitsAtLastWait.Init(0, States.Num());
	}
	const double Top = static_cast<double>(MaxHeightM) + 20.0;
	int32 Pending = 0;
	int32 BadResult = 0;
	int32 NoTrimesh = 0;
	int32 NotCurrent = 0;
	int32 ProbeMiss = 0;
	int32 Touched = 0;
	int32 MinTri = TNumericLimits<int32>::Max();
	TArray<int32> Bad;
	for (const FTerrainChunkCollisionState& S : States)
	{
		if (S.Submits != CollisionSubmitsAtLastWait[S.Chunk])
		{
			++Touched;
		}
		MinTri = FMath::Min(MinTri, S.TriMeshes);
		bool bOk = true;
		if (S.bPending)
		{
			++Pending;
			bOk = false;
		}
		else if (S.LastResult != static_cast<uint8>(ERealtimeMeshCollisionUpdateResult::Updated)
			&& S.LastResult != static_cast<uint8>(ERealtimeMeshCollisionUpdateResult::Ignored))
		{
			// Ignored = an older async cook finishing after a newer one (RealtimeMeshData.cpp:131-135); the body check below decides.
			++BadResult;
			bOk = false;
		}
		if (S.TriMeshes <= 0)
		{
			++NoTrimesh;
			bOk = false;
		}
		if (!S.bPhysicsCurrent)
		{
			++NotCurrent;
			bOk = false;
		}
		if (bOk)
		{
			FVector HitM;
			bool bTerrain = false;
			// The probe must hit THIS chunk's body, not a neighbour's (a stale or missing body would otherwise hide behind one).
			const UPrimitiveComponent* HitComp = nullptr;
			if (!PhysicsRay(FVector(S.CenterM.X, S.CenterM.Y, Top), FVector(0.0, 0.0, -1.0), 2.0 * Top, HitM, bTerrain, &HitComp) || !bTerrain
				|| HitComp != S.Component)
			{
				++ProbeMiss;
				bOk = false;
			}
		}
		if (!bOk)
		{
			Bad.Add(S.Chunk);
		}
	}
	const double Elapsed = FPlatformTime::Seconds() - OpStartSeconds;
	if (Bad.Num() > 0 && Elapsed < TimeoutS)
	{
		return EStep::Running;
	}
	TSharedRef<FJsonObject> W = MakeShared<FJsonObject>();
	W->SetStringField(TEXT("name"), Name);
	W->SetNumberField(TEXT("ms"), Elapsed * 1000.0);
	W->SetNumberField(TEXT("frames"), OpFrame + 1);
	W->SetNumberField(TEXT("chunks"), States.Num());
	W->SetNumberField(TEXT("touched"), Touched);
	W->SetNumberField(TEXT("min_trimeshes"), States.Num() > 0 ? MinTri : 0);
	W->SetNumberField(TEXT("probes_hit"), States.Num() - Bad.Num());
	W->SetNumberField(TEXT("pending"), Pending);
	W->SetNumberField(TEXT("bad_result"), BadResult);
	W->SetNumberField(TEXT("no_trimesh"), NoTrimesh);
	W->SetNumberField(TEXT("not_current"), NotCurrent);
	W->SetNumberField(TEXT("probe_miss"), ProbeMiss);
	W->SetBoolField(TEXT("ok"), Bad.Num() == 0 && States.Num() > 0);
	CollisionWaits.Add(MakeShared<FJsonValueObject>(W));
	FString BadList;
	for (const int32 Id : Bad)
	{
		BadList += FString::Printf(TEXT("%s%d"), BadList.IsEmpty() ? TEXT("") : TEXT(","), Id);
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("wait_collision %s: %.0f ms chunks=%d touched=%d min_trimeshes=%d pending=%d bad_result=%d no_trimesh=%d not_current=%d probe_miss=%d"),
		*Name, Elapsed * 1000.0, States.Num(), Touched, States.Num() > 0 ? MinTri : 0, Pending, BadResult, NoTrimesh, NotCurrent, ProbeMiss);
	if (Bad.Num() > 0 || States.Num() == 0)
	{
		OpError = FString::Printf(TEXT("collision not current after %.0f s: pending=%d bad_result=%d no_trimesh=%d not_current=%d probe_miss=%d chunks=[%s]"),
			Elapsed, Pending, BadResult, NoTrimesh, NotCurrent, ProbeMiss, *BadList);
		return EStep::TimedOut;
	}
	for (const FTerrainChunkCollisionState& S : States)
	{
		CollisionSubmitsAtLastWait[S.Chunk] = S.Submits;
	}
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepVerifyCollision(const FJsonObject& Op)
{
	// Plan C 3.8 verify_collision: seeded frustum rays plus vertical rays at the vertices of the last dirty rects, each cast through
	// physics (LineTraceSingleByChannel ECC_Visibility) and the analytic pick; disagreements both ways and |dz| of the hit points.
	const double T0 = FPlatformTime::Seconds();
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("verify%d"), OpIndex + 1));
	const FString PoseName = StrField(Op, TEXT("pose"), TEXT("rts80"));
	FTerrainCameraPose Pose;
	if (!FTerrainCameraPose::Find(PoseName, Pose))
	{
		OpError = FString::Printf(TEXT("unknown pose '%s'"), *PoseName);
		return EStep::Failed;
	}
	const int32 N = FMath::Max(0, static_cast<int32>(NumField(Op, TEXT("n"), 2000.0)));
	const int32 VertexCap = FMath::Max(0, static_cast<int32>(NumField(Op, TEXT("vertex_cap"), 4000.0)));
	FRandomStream Rng(static_cast<int32>(NumField(Op, TEXT("seed"), 4242.0)));
	const FTerrainHeightfield& HF = Terrain->GetHeightfield();

	struct FAcc
	{
		int32 N = 0;
		int32 BothHit = 0;
		int32 BothMiss = 0;
		int32 PhysicsOnly = 0;
		int32 PickOnly = 0;
		int32 Foreign = 0;
		FTerrainSeries DzCm;
		FTerrainSeries DistCm;
		TArray<TSharedPtr<FJsonValue>> Examples;

		int32 Disagreements() const { return PhysicsOnly + PickOnly + Foreign; }

		void Write(FJsonObject& O) const
		{
			O.SetNumberField(TEXT("n"), N);
			O.SetNumberField(TEXT("both_hit"), BothHit);
			O.SetNumberField(TEXT("both_miss"), BothMiss);
			O.SetNumberField(TEXT("physics_only_hit"), PhysicsOnly);
			O.SetNumberField(TEXT("pick_only_hit"), PickOnly);
			O.SetNumberField(TEXT("foreign_hit"), Foreign);
			O.SetNumberField(TEXT("disagreements"), Disagreements());
			O.SetNumberField(TEXT("max_dz_cm"), DzCm.Num() > 0 ? DzCm.Max() : 0.0);
			O.SetObjectField(TEXT("dz_cm"), DzCm.ToJson());
			O.SetObjectField(TEXT("dist_cm"), DistCm.ToJson());
			O.SetArrayField(TEXT("disagreement_examples"), Examples);
		}
	};
	auto CastBoth = [&](const FVector& Origin, const FVector& Dir, double MaxM, FAcc& A, const TCHAR* Kind)
	{
		++A.N;
		FTerrainHit Pick;
		const bool bPick = TerrainPick::RayCast(HF, Origin, Dir, MaxM, Pick);
		FVector PhysM = FVector::ZeroVector;
		bool bTerrain = false;
		const bool bPhys = PhysicsRay(Origin, Dir, MaxM, PhysM, bTerrain);
		FString Why;
		if (bPhys && !bTerrain)
		{
			++A.Foreign;
			Why = TEXT("foreign");
		}
		else if (bPhys && bPick)
		{
			++A.BothHit;
			A.DzCm.Add(FMath::Abs(PhysM.Z - Pick.Position.Z) * 100.0);
			A.DistCm.Add(FMath::Abs(FVector::Distance(Origin, PhysM) - Pick.Distance) * 100.0);
		}
		else if (!bPhys && !bPick)
		{
			++A.BothMiss;
		}
		else if (bPhys)
		{
			++A.PhysicsOnly;
			Why = TEXT("physics_only");
		}
		else
		{
			++A.PickOnly;
			Why = TEXT("pick_only");
		}
		if (!Why.IsEmpty() && A.Examples.Num() < 10)
		{
			TSharedRef<FJsonObject> E = MakeShared<FJsonObject>();
			E->SetStringField(TEXT("kind"), Kind);
			E->SetStringField(TEXT("why"), Why);
			E->SetField(TEXT("origin_m"), VecToJson(Origin));
			E->SetField(TEXT("dir"), VecToJson(Dir));
			if (bPick)
			{
				E->SetField(TEXT("pick_m"), VecToJson(Pick.Position));
			}
			if (bPhys)
			{
				E->SetField(TEXT("physics_m"), VecToJson(PhysM));
			}
			A.Examples.Add(MakeShared<FJsonValueObject>(E));
		}
	};

	// Frustum rays: seeded pixels of a 1920x1080 view at the pose (the depthcheck's camera model).
	constexpr double ViewW = 1920.0;
	constexpr double ViewH = 1080.0;
	const FRotationMatrix RM(Pose.Rotation);
	const FVector Fwd = RM.GetUnitAxis(EAxis::X);
	const FVector Right = RM.GetUnitAxis(EAxis::Y);
	const FVector Up = RM.GetUnitAxis(EAxis::Z);
	const double TanH = FMath::Tan(FMath::DegreesToRadians(ARtsCameraPawn::HFovFromVFov(Pose.VFovDeg, static_cast<float>(ViewW / ViewH))) * 0.5);
	const double TanV = TanH * ViewH / ViewW;
	const FVector CamM = Pose.LocationCm / 100.0;
	FAcc Frustum;
	for (int32 I = 0; I < N; ++I)
	{
		const double Nx = Rng.FRandRange(-1.0, 1.0);
		const double Ny = Rng.FRandRange(-1.0, 1.0);
		const FVector Dir = (Fwd + Right * (Nx * TanH) + Up * (Ny * TanV)).GetSafeNormal();
		CastBoth(CamM, Dir, 5000.0, Frustum, TEXT("frustum"));
	}

	// Vertex rays: every vertex inside the rects rewritten since the last verify, strided evenly down to the cap.
	const TArray<FTerrainRect> Rects = Terrain->ConsumeCollisionRects();
	TBitArray<> Mark(false, HF.Width() * HF.Width());
	FTerrainRect Bounds;
	for (const FTerrainRect& Rc : Rects)
	{
		const FTerrainRect C = Rc.Intersect(FTerrainRect(0, 0, HF.Width(), HF.Width()));
		Bounds.Union(C);
		for (int32 Y = C.Y0; Y < C.Y1; ++Y)
		{
			for (int32 X = C.X0; X < C.X1; ++X)
			{
				Mark[Y * HF.Width() + X] = true;
			}
		}
	}
	TArray<int32> Verts;
	for (TConstSetBitIterator<> It(Mark); It; ++It)
	{
		Verts.Add(It.GetIndex());
	}
	const int32 Stride = (VertexCap > 0 && Verts.Num() > VertexCap) ? FMath::DivideAndRoundUp(Verts.Num(), VertexCap) : 1;
	const double Top = static_cast<double>(MaxHeightM) + 20.0;
	FAcc Vertex;
	for (int32 I = 0; I < Verts.Num() && (VertexCap <= 0 || Vertex.N < VertexCap); I += Stride)
	{
		const int32 X = Verts[I] % HF.Width();
		const int32 Y = Verts[I] / HF.Width();
		CastBoth(FVector(HF.VertexToWorld(X), HF.VertexToWorld(Y), Top), FVector(0.0, 0.0, -1.0), 2.0 * Top, Vertex, TEXT("vertex"));
	}

	TSharedRef<FJsonObject> V = MakeShared<FJsonObject>();
	V->SetStringField(TEXT("name"), Name);
	V->SetStringField(TEXT("pose"), PoseName);
	V->SetNumberField(TEXT("seed"), NumField(Op, TEXT("seed"), 4242.0));
	TSharedRef<FJsonObject> Fj = MakeShared<FJsonObject>();
	Frustum.Write(*Fj);
	V->SetObjectField(TEXT("frustum"), Fj);
	TSharedRef<FJsonObject> Vj = MakeShared<FJsonObject>();
	Vertex.Write(*Vj);
	Vj->SetNumberField(TEXT("rects"), Rects.Num());
	Vj->SetNumberField(TEXT("vertices_in_rects"), Verts.Num());
	Vj->SetNumberField(TEXT("stride"), Stride);
	TArray<TSharedPtr<FJsonValue>> B;
	B.Add(MakeShared<FJsonValueNumber>(Bounds.X0));
	B.Add(MakeShared<FJsonValueNumber>(Bounds.Y0));
	B.Add(MakeShared<FJsonValueNumber>(Bounds.X1));
	B.Add(MakeShared<FJsonValueNumber>(Bounds.Y1));
	Vj->SetArrayField(TEXT("rect_bounds"), B);
	V->SetObjectField(TEXT("vertex"), Vj);
	const int32 Dis = Frustum.Disagreements() + Vertex.Disagreements();
	const double MaxDz = FMath::Max(Frustum.DzCm.Num() > 0 ? Frustum.DzCm.Max() : 0.0, Vertex.DzCm.Num() > 0 ? Vertex.DzCm.Max() : 0.0);
	V->SetNumberField(TEXT("rays"), Frustum.N + Vertex.N);
	V->SetNumberField(TEXT("disagreements"), Dis);
	V->SetNumberField(TEXT("max_dz_cm"), MaxDz);
	V->SetNumberField(TEXT("ms"), (FPlatformTime::Seconds() - T0) * 1000.0);
	CollisionVerifies.Add(MakeShared<FJsonValueObject>(V));
	UE_LOG(LogChimeraTerrain, Display, TEXT("verify_collision %s: frustum n=%d both_hit=%d phys_only=%d pick_only=%d foreign=%d; vertex n=%d (of %d in %d rects) both_hit=%d phys_only=%d pick_only=%d foreign=%d; disagreements=%d max_dz=%.4f cm"),
		*Name, Frustum.N, Frustum.BothHit, Frustum.PhysicsOnly, Frustum.PickOnly, Frustum.Foreign, Vertex.N, Verts.Num(), Rects.Num(), Vertex.BothHit,
		Vertex.PhysicsOnly, Vertex.PickOnly, Vertex.Foreign, Dis, MaxDz);
	return EStep::Done;
}

// ---- scatter ops (task S4, plan C scatter 3.8) ------------------------------------------------------------------------------------------

bool ATerrainScriptDirector::NeedScatter()
{
	if (!Scatter || !Scatter->WasRequested())
	{
		OpError = TEXT("needs -ChimeraTerrainScatter=1");
		return false;
	}
	return true;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatter(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	if (OpFrame == 0)
	{
		const bool bOn = NumField(Op, TEXT("value"), 1.0) != 0.0;
		TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
		J->SetBoolField(TEXT("value"), bOn);
		J->SetNumberField(TEXT("op_index"), OpIndex + 1);
		J->SetNumberField(TEXT("frame"), static_cast<double>(GFrameCounter));
		J->SetBoolField(TEXT("pending_before"), Scatter->HasPendingWork());
		J->SetNumberField(TEXT("busy_before"), Scatter->NumBusy());
		J->SetNumberField(TEXT("in_flight_before"), Scatter->NumInFlight());
		if (bOn)
		{
			Scatter->Enable();
		}
		else
		{
			Scatter->Disable();
		}
		ScatterToggles.Add(MakeShared<FJsonValueObject>(J));
	}
	return OpFrame >= 2 ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterWait(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const double TimeoutS = NumField(Op, TEXT("timeout_s"), 120.0);
	if (OpPhase == 0)
	{
		if (Scatter->HasPendingWork())
		{
			if (FPlatformTime::Seconds() - OpStartSeconds > TimeoutS)
			{
				OpError = FString::Printf(TEXT("scatter still pending after %.0f s"), TimeoutS);
				return EStep::TimedOut;
			}
			return EStep::Running;
		}
		OpPhase = 1;
		OpPhaseSeconds = FPlatformTime::Seconds();
		ShotStableFrames = 0;
		return EStep::Running;
	}
	if (++ShotStableFrames < 2)
	{
		return EStep::Running;
	}
	TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
	J->SetNumberField(TEXT("op_index"), OpIndex + 1);
	J->SetNumberField(TEXT("scatter_wait_ms"), (OpPhaseSeconds - OpStartSeconds) * 1000.0);
	J->SetNumberField(TEXT("frames"), OpFrame + 1);
	ScatterWaits.Add(MakeShared<FJsonValueObject>(J));
	UE_LOG(LogChimeraTerrain, Display, TEXT("scatter_wait_ms=%.0f"), (OpPhaseSeconds - OpStartSeconds) * 1000.0);
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterVisible(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	if (OpFrame == 0)
	{
		const FString LayerSpec = StrField(Op, TEXT("layers"), TEXT("all"));
		uint32 Mask = 0;
		if (LayerSpec.Equals(TEXT("all"), ESearchCase::IgnoreCase))
		{
			Mask = (1u << ScatterLayerCount) - 1u;
		}
		else
		{
			TArray<FString> Names;
			LayerSpec.ParseIntoArray(Names, TEXT(","), true);
			for (FString N : Names)
			{
				N.TrimStartAndEndInline();
				bool bFound = false;
				for (int32 L = 0; L < ScatterLayerCount; ++L)
				{
					if (N.Equals(UTF8_TO_TCHAR(ScatterLayerName(static_cast<EScatterLayer>(L))), ESearchCase::IgnoreCase))
					{
						Mask |= 1u << L;
						bFound = true;
					}
				}
				if (!bFound)
				{
					OpError = FString::Printf(TEXT("scatter_visible: unknown layer '%s' (grass, groundcover, shrubs, trees, rocks or all)"), *N);
					return EStep::Failed;
				}
			}
		}
		Scatter->SetLayersVisible(Mask, NumField(Op, TEXT("value"), 1.0) != 0.0);
	}
	return OpFrame >= 2 ? EStep::Done : EStep::Running;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterVerify(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("verify%d"), OpIndex + 1));
	bool bPass = false;
	TArray<FScatterTarget> TargetList;
	for (const TPair<FString, TSharedPtr<FScatterTarget>>& Pr : ScatterTargets)
	{
		if (Pr.Value.IsValid())
		{
			TargetList.Add(*Pr.Value);
		}
	}
	const TSharedRef<FJsonObject> V = Scatter->Verify(Name, bPass, &TargetList);
	V->SetNumberField(TEXT("op_index"), OpIndex + 1);
	ScatterVerifies.Add(MakeShared<FJsonValueObject>(V));
	return EStep::Done;
}

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepScatterFresh(const FJsonObject& Op)
{
	if (!NeedScatter())
	{
		return EStep::Failed;
	}
	const FString Name = StrField(Op, TEXT("name"), FString::Printf(TEXT("fresh%d"), OpIndex + 1));
	static const TCHAR* const ForceCvar = TEXT("r.Shadow.Virtual.Cache.ForceInvalidateDirectional");
	if (OpFrame == 0)
	{
		// Proxies rebuilt from the CPU arrays (each counted as an expected rebuild), and the directional VSM cache dropped for 10 frames (F20).
		FreshRebuilt = Scatter->RebuildAllProxies();
		IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(ForceCvar);
		FreshCvarOld = Cv ? Cv->GetString() : FString();
		if (Cv)
		{
			Cv->Set(TEXT("1"), ECVF_SetByCode);
		}
		FreshStage = 1;
		FreshFrames = 0;
		return EStep::Running;
	}
	if (FreshStage == 1)
	{
		if (++FreshFrames < 10)
		{
			return EStep::Running;
		}
		if (IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(ForceCvar))
		{
			Cv->Set(FreshCvarOld.IsEmpty() ? TEXT("0") : *FreshCvarOld, ECVF_SetByCode);
		}
		FreshStage = 2;
		ShotStableFrames = 0;
		return EStep::Running;
	}
	if (FreshStage == 2)
	{
		// settle 150 (plan C scatter 3.8: Nanite pages, TSR, VSM's 100-frame static threshold).
		if (!CompileQueuesIdle() || Terrain->HasPendingWork())
		{
			ShotStableFrames = 0;
			return EStep::Running;
		}
		if (++ShotStableFrames < 150)
		{
			return EStep::Running;
		}
		Scatter->ClearExpectedRebuilds();
		FreshShotOp = MakeShared<FJsonObject>();
		FreshShotOp->SetStringField(TEXT("op"), TEXT("shot"));
		FreshShotOp->SetStringField(TEXT("name"), Name + TEXT("_fresh"));
		FreshStage = 3;
		OpPhase = 0;
		ShotStableFrames = 0;
		return EStep::Running;
	}
	const EStep Shot = StepShot(*FreshShotOp);
	if (Shot == EStep::Done)
	{
		TSharedRef<FJsonObject> J = MakeShared<FJsonObject>();
		J->SetStringField(TEXT("name"), Name);
		J->SetStringField(TEXT("shot"), Name + TEXT("_fresh"));
		J->SetNumberField(TEXT("components_rebuilt"), FreshRebuilt);
		J->SetNumberField(TEXT("op_index"), OpIndex + 1);
		ScatterFreshes.Add(MakeShared<FJsonValueObject>(J));
		FreshStage = 0;
	}
	return Shot;
}

TSharedRef<FJsonObject> ATerrainScriptDirector::CollisionToJson() const
{
	TSharedRef<FJsonObject> O = MakeShared<FJsonObject>();
	TSharedRef<FJsonObject> Opt = MakeShared<FJsonObject>();
	Opt->SetBoolField(TEXT("fast_cook"), Options.bFastCook);
	int32 DuringMs = 0;
	if (const IConsoleVariable* Cv = IConsoleManager::Get().FindConsoleVariable(TEXT("chimera.terrain.CollisionDuringStroke")))
	{
		DuringMs = Cv->GetInt();
	}
	Opt->SetNumberField(TEXT("during_stroke_ms"), DuringMs);
	O->SetObjectField(TEXT("options"), Opt);
	O->SetArrayField(TEXT("waits"), CollisionWaits);
	O->SetArrayField(TEXT("verifies"), CollisionVerifies);
	const ITerrainChunkRenderer* R = Terrain ? Terrain->GetRenderer() : nullptr;
	if (!R)
	{
		return O;
	}
	const uint8 Updated = static_cast<uint8>(ERealtimeMeshCollisionUpdateResult::Updated);
	const uint8 Ignored = static_cast<uint8>(ERealtimeMeshCollisionUpdateResult::Ignored);
	const uint8 Error = static_cast<uint8>(ERealtimeMeshCollisionUpdateResult::Error);
	// Cook = submit -> future value, valid only for an Updated body with a trimesh (plan C 3.6). Edit cooks exclude the initial build.
	FTerrainSeries CookMs;
	FTerrainSeries CookFrames;
	FTerrainSeries InitCookMs;
	TMap<FString, FTerrainSeries> CookByReason;
	int32 NUpdated = 0;
	int32 NIgnored = 0;
	int32 NError = 0;
	int32 NUnknown = 0;
	int32 NoTrimesh = 0;
	TArray<TSharedPtr<FJsonValue>> CookValues;
	for (const FTerrainCollisionCook& C : R->GetCollisionCooks())
	{
		NUpdated += C.Result == Updated ? 1 : 0;
		NIgnored += C.Result == Ignored ? 1 : 0;
		NError += C.Result == Error ? 1 : 0;
		NUnknown += (C.Result != Updated && C.Result != Ignored && C.Result != Error) ? 1 : 0;
		if (C.Result != Updated)
		{
			continue;
		}
		if (C.TriMeshes <= 0)
		{
			++NoTrimesh;
			continue;
		}
		CookByReason.FindOrAdd(CollisionReasonName(C.Reason)).Add(C.Ms);
		if (C.Reason == ETerrainCollisionReason::Init)
		{
			InitCookMs.Add(C.Ms);
			continue;
		}
		CookMs.Add(C.Ms);
		CookFrames.Add(C.Frames);
		CookValues.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(C.Ms * 1000.0) / 1000.0));
	}
	O->SetNumberField(TEXT("cooks"), R->GetCollisionCooks().Num());
	O->SetNumberField(TEXT("updated"), NUpdated);
	O->SetNumberField(TEXT("ignored"), NIgnored);
	O->SetNumberField(TEXT("errors"), NError);
	O->SetNumberField(TEXT("unknown"), NUnknown);
	O->SetNumberField(TEXT("updated_without_trimesh"), NoTrimesh);
	O->SetObjectField(TEXT("cook_ms"), CookMs.ToJson());
	O->SetObjectField(TEXT("cook_frames"), CookFrames.ToJson());
	O->SetObjectField(TEXT("init_cook_ms"), InitCookMs.ToJson());
	TSharedRef<FJsonObject> ByR = MakeShared<FJsonObject>();
	for (const TPair<FString, FTerrainSeries>& P : CookByReason)
	{
		ByR->SetObjectField(P.Key, P.Value.ToJson());
	}
	O->SetObjectField(TEXT("cook_ms_by_reason"), ByR);
	O->SetArrayField(TEXT("cook_ms_values"), CookValues);

	// GT apply per stroke end (plan C 5 P5): geometry build + submit + the chunks' physics-state recreation, for stroke end, undo, redo.
	FTerrainSeries GtApply;
	FTerrainSeries Submit;
	FTerrainSeries Physics;
	TMap<FString, FTerrainSeries> GtByReason;
	int32 Incomplete = 0;
	TArray<TSharedPtr<FJsonValue>> GtValues;
	for (const FTerrainCollisionBatch& B : R->GetCollisionBatches())
	{
		if (!B.bComplete)
		{
			++Incomplete;
			continue;
		}
		if (B.Chunks == 0)
		{
			continue;
		}
		GtByReason.FindOrAdd(CollisionReasonName(B.Reason)).Add(B.GtApplyMs());
		if (B.Reason == ETerrainCollisionReason::StrokeEnd || B.Reason == ETerrainCollisionReason::Undo || B.Reason == ETerrainCollisionReason::Redo)
		{
			GtApply.Add(B.GtApplyMs());
			Submit.Add(B.SubmitMs);
			Physics.Add(B.PhysicsStateMs);
			GtValues.Add(MakeShared<FJsonValueNumber>(FMath::RoundToDouble(B.GtApplyMs() * 1000.0) / 1000.0));
		}
	}
	O->SetNumberField(TEXT("batches"), R->GetCollisionBatches().Num());
	O->SetNumberField(TEXT("batches_incomplete"), Incomplete);
	O->SetObjectField(TEXT("gt_apply_ms"), GtApply.ToJson());
	O->SetObjectField(TEXT("submit_ms"), Submit.ToJson());
	O->SetObjectField(TEXT("physics_state_ms"), Physics.ToJson());
	TSharedRef<FJsonObject> GtR = MakeShared<FJsonObject>();
	for (const TPair<FString, FTerrainSeries>& P : GtByReason)
	{
		GtR->SetObjectField(P.Key, P.Value.ToJson());
	}
	O->SetObjectField(TEXT("gt_apply_ms_by_reason"), GtR);
	O->SetStringField(TEXT("gt_apply_definition"), TEXT("per batch: collision geometry build + SetCustomComplexMeshGeometry submit on the GT, plus each chunk's ")
		TEXT("Destroy/CreatePhysicsState seconds (UTerrainChunkComponent) up to the resolve. Not timed: the rest of RMC's GT continuation ")
		TEXT("(URealtimeMesh::ApplyCollisionUpdate, RealtimeMesh.cpp:373-421: NewObject<UBodySetup>, CopyComplexGeometryToBodySetup, UV copy)."));
	O->SetStringField(TEXT("cook_definition"), TEXT("cook_ms: edit cooks only (stroke_end, mid_stroke, undo, redo, load), submit to resolve; ")
		TEXT("the startup cooks (reason init) are in init_cook_ms, reported separately."));
	O->SetArrayField(TEXT("gt_apply_ms_values"), GtValues);
	return O;
}

bool ATerrainScriptDirector::WriteJsonFile(const FString& FileName, const TSharedRef<FJsonObject>& Obj) const
{
	FString Text;
	const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Text);
	if (!FJsonSerializer::Serialize(Obj, Writer))
	{
		return false;
	}
	return FFileHelper::SaveStringToFile(Text, *OutPath(FileName), FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
}

void ATerrainScriptDirector::Finish(uint8 Code, const FString& Reason)
{
	if (bFinished)
	{
		return;
	}
	bFinished = true;
	const bool bCompleted = Code == ExitOk;
	Results->SetStringField(TEXT("script"), ScriptName);
	Results->SetStringField(TEXT("script_path"), Options.ScriptPath);
	Results->SetStringField(TEXT("script_sha256"), ScriptSha256);
	{
		// The effective scalability groups of every run, scatter or not (plan C scatter 3.8: every compared pair, C1 vs C1S included, must have equal sg.*).
		static const TCHAR* const SgCvars[] = {TEXT("sg.ResolutionQuality"), TEXT("sg.ViewDistanceQuality"), TEXT("sg.AntiAliasingQuality"), TEXT("sg.ShadowQuality"),
			TEXT("sg.GlobalIlluminationQuality"), TEXT("sg.ReflectionQuality"), TEXT("sg.PostProcessQuality"), TEXT("sg.TextureQuality"), TEXT("sg.EffectsQuality"),
			TEXT("sg.FoliageQuality"), TEXT("sg.ShadingQuality"), TEXT("sg.LandscapeQuality")};
		TSharedRef<FJsonObject> SG = MakeShared<FJsonObject>();
		for (const TCHAR* Cv : SgCvars)
		{
			const IConsoleVariable* V = IConsoleManager::Get().FindConsoleVariable(Cv);
			SG->SetStringField(Cv, V ? V->GetString() : FString(TEXT("missing")));
		}
		Results->SetObjectField(TEXT("sg"), SG);
	}
	Results->SetBoolField(TEXT("completed"), bCompleted);
	Results->SetNumberField(TEXT("exit_code"), Code);
	// ops_done counts ops that finished (an `exit` op counts itself).
	Results->SetNumberField(TEXT("ops_done"), Timeline.Num() + ((bCompleted && OpIndex < Ops.Num()) ? 1 : 0));
	Results->SetNumberField(TEXT("ops_total"), Ops.Num());
	Results->SetStringField(TEXT("fail_reason"), Reason);

	TSharedRef<FJsonObject> Opt = MakeShared<FJsonObject>();
	Opt->SetNumberField(TEXT("half"), Options.HalfExtentM);
	Opt->SetNumberField(TEXT("chunk"), Options.ChunkQuads);
	Opt->SetStringField(TEXT("draw_type"), Options.DrawType == ETerrainDrawType::Dynamic ? TEXT("Dynamic") : TEXT("Static"));
	Opt->SetNumberField(TEXT("compare_ev100"), Options.CompareEV100);
	Opt->SetNumberField(TEXT("hitch_ms"), Options.HitchMs);
	Opt->SetStringField(TEXT("load_dir"), Options.LoadDir);
	Opt->SetStringField(TEXT("ground_params"), Options.GroundParams);
	if (Scatter && Scatter->WasRequested())
	{
		Opt->SetObjectField(TEXT("scatter"), Scatter->OptionsJson());
	}
	Results->SetObjectField(TEXT("options"), Opt);
	if (Scatter && Scatter->WasRequested())
	{
		// Plan C scatter 3.8 results block (S5 adds checks, dumps, view counts).
		TSharedRef<FJsonObject> SJ = Scatter->ResultsJson();
		SJ->SetArrayField(TEXT("verifies"), ScatterVerifies);
		SJ->SetArrayField(TEXT("waits"), ScatterWaits);
		SJ->SetArrayField(TEXT("freshes"), ScatterFreshes);
		SJ->SetArrayField(TEXT("toggles"), ScatterToggles);
		AddScatterOpResults(*SJ);
		Results->SetObjectField(TEXT("scatter"), SJ);
	}
	if (Terrain)
	{
		Results->SetObjectField(TEXT("material"), Terrain->DescribeMaterial());
	}

	if (Terrain)
	{
		if (const ITerrainChunkRenderer* R = Terrain->GetRenderer())
		{
			const FTerrainRenderStats St = R->GetStats();
			TSharedRef<FJsonObject> RJ = MakeShared<FJsonObject>();
			RJ->SetStringField(TEXT("renderer"), R->GetName());
			RJ->SetNumberField(TEXT("chunks"), St.Chunks);
			RJ->SetNumberField(TEXT("triangles"), static_cast<double>(St.Triangles));
			RJ->SetNumberField(TEXT("ranged_edits"), static_cast<double>(St.RangedEdits));
			RJ->SetNumberField(TEXT("uploaded_vertices"), static_cast<double>(St.UploadedVertices));
			RJ->SetNumberField(TEXT("proxy_recreates"), static_cast<double>(St.ProxyRecreates));
			RJ->SetNumberField(TEXT("proxy_recreates_during_strokes"), static_cast<double>(St.ProxyRecreatesDuringStrokes));
			RJ->SetNumberField(TEXT("bounds_widenings"), static_cast<double>(St.BoundsWidenings));
			RJ->SetNumberField(TEXT("bounds_exact_pushes"), static_cast<double>(St.BoundsExactPushes));
			RJ->SetObjectField(TEXT("edit_latency"), FTerrainMetrics::LatencyToJson(R->GetEditLatencies()));
			Results->SetObjectField(TEXT("render"), RJ);
		}
		TSharedRef<FJsonObject> SJ = MakeShared<FJsonObject>();
		SJ->SetNumberField(TEXT("submits"), static_cast<double>(Terrain->GetSplat().Submits()));
		SJ->SetNumberField(TEXT("cleanups"), static_cast<double>(Terrain->GetSplat().Cleanups()));
		Results->SetObjectField(TEXT("splat"), SJ);
		// S4a: change events broadcast by the terrain actor, per kind (plan C scatter 3.5).
		TSharedRef<FJsonObject> EJ = MakeShared<FJsonObject>();
		for (int32 K = 0; K < static_cast<int32>(ETerrainChangeKind::Count); ++K)
		{
			EJ->SetNumberField(TerrainChangeKindName(static_cast<ETerrainChangeKind>(K)), static_cast<double>(Terrain->GetEventCount(static_cast<ETerrainChangeKind>(K))));
		}
		Results->SetObjectField(TEXT("terrain_events"), EJ);
		Results->SetStringField(TEXT("final_height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
		Results->SetStringField(TEXT("final_splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	}
	Results->SetObjectField(TEXT("hashes"), Hashes);
	Results->SetObjectField(TEXT("shots"), Shots);
	Results->SetArrayField(TEXT("strokes"), Strokes);
	Results->SetArrayField(TEXT("settles"), Settles);
	Results->SetArrayField(TEXT("timeline"), Timeline);
	Results->SetObjectField(TEXT("metrics"), Metrics.FramesToJson());
	Results->SetObjectField(TEXT("memory"), Metrics.MemoryToJson());
	Results->SetArrayField(TEXT("depthchecks"), DepthChecks);
	Results->SetArrayField(TEXT("skipped"), Skipped);
	Results->SetArrayField(TEXT("walks"), Walks);
	Results->SetArrayField(TEXT("hitches"), Hitches);
	Results->SetArrayField(TEXT("temporal_freeze"), TemporalFreezes);
	Results->SetArrayField(TEXT("gcs"), Gcs);
	Results->SetArrayField(TEXT("movies"), Movies);
	Results->SetArrayField(TEXT("undo_redo"), UndoRedos);
	Results->SetObjectField(TEXT("collision"), CollisionToJson());
	if (Saved.IsValid())
	{
		Results->SetObjectField(TEXT("saved"), Saved.ToSharedRef());
	}
	if (Loaded.IsValid())
	{
		Results->SetObjectField(TEXT("loaded"), Loaded.ToSharedRef());
	}
	if (CsvInfo.IsValid())
	{
		Results->SetObjectField(TEXT("csv"), CsvInfo.ToSharedRef());
	}
	if (Terrain)
	{
		Results->SetObjectField(TEXT("ticks"), FTerrainMetrics::TicksToJson(Terrain->GetTickSamples(), Terrain->GetPhaseNames()));
		FFileHelper::SaveStringToFile(FTerrainMetrics::TicksToCsv(Terrain->GetTickSamples(), Terrain->GetPhaseNames()), *OutPath(TEXT("ticks.csv")),
			FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM);
		TArray<TSharedPtr<FJsonValue>> Mouse;
		for (const TSharedRef<FJsonObject>& M : Terrain->GetMouseStrokeRecords())
		{
			Mouse.Add(MakeShared<FJsonValueObject>(M));
		}
		Results->SetArrayField(TEXT("mouse_strokes"), Mouse);
	}
	if (const AChimeraTerrainPlayerController* ResultPC = Cast<AChimeraTerrainPlayerController>(GetPC()))
	{
		Results->SetArrayField(TEXT("mouse_keys"), ResultPC->GetKeyRecords());
		Results->SetObjectField(TEXT("mouse_controller"), ResultPC->ControllerSummaryJson());
	}
	FIntPoint Viewport(0, 0);
	if (GEngine && GEngine->GameViewport && GEngine->GameViewport->Viewport)
	{
		Viewport = GEngine->GameViewport->Viewport->GetSizeXY();
	}
	Results->SetStringField(TEXT("viewport"), FString::Printf(TEXT("%dx%d"), Viewport.X, Viewport.Y));

	const bool bWrote = WriteJsonFile(TEXT("results.json"), Results);
	UE_LOG(LogChimeraTerrain, Display, TEXT("RESULT completed=%s exit=%d ops=%d/%d results=%s%s"), bCompleted ? TEXT("true") : TEXT("false"), Code,
		static_cast<int32>(Results->GetNumberField(TEXT("ops_done"))), Ops.Num(), *OutPath(TEXT("results.json")),
		Reason.IsEmpty() ? TEXT("") : *FString::Printf(TEXT(" reason: %s"), *Reason));
	if (!bWrote)
	{
		UE_LOG(LogChimeraTerrain, Error, TEXT("cannot write results.json to %s"), *Options.OutDir);
		Code = ExitOpFailed;
	}
	if (GLog)
	{
		GLog->Flush();
	}
	// EXECUTION 2.2: failures exit forced (TerminateProcess with the code after the log flush); success may exit normally with 0.
	if (Code != ExitOk)
	{
		FPlatformMisc::RequestExitWithStatus(true, Code, TEXT("ChimeraTerrainDirector"));
	}
	else
	{
		FPlatformMisc::RequestExitWithStatus(false, 0, TEXT("ChimeraTerrainDirector"));
	}
}
