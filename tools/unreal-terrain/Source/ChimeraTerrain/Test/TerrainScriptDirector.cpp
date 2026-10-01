// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Test/TerrainScriptDirector.h"

#include "AssetCompilingManager.h"
#include "ChimeraTerrain.h"
#include "Data/TerrainIO.h"
#include "Data/TerrainPick.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/World.h"
#include "Game/RtsCameraPawn.h"
#include "Game/TerrainActor.h"
#include "Game/TerrainLighting.h"
#include "GameFramework/HUD.h"
#include "GameFramework/PlayerController.h"
#include "Camera/PlayerCameraManager.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformMisc.h"
#include "Misc/App.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
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

	/** [[x, y], ...] -> points (terrain metres). */
	bool ParsePath(const FJsonObject& Op, TArray<FVector2D>& Out)
	{
		const TArray<TSharedPtr<FJsonValue>>* Arr = nullptr;
		if (!Op.TryGetArrayField(TEXT("path"), Arr) || Arr->Num() == 0)
		{
			return false;
		}
		for (const TSharedPtr<FJsonValue>& V : *Arr)
		{
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
	if (Name == TEXT("stroke"))
	{
		return 120.0 + NumField(Op, TEXT("ticks"), 1.0) * 0.5;
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
			UE_LOG(LogChimeraTerrain, Display, TEXT("op %d/%d %s"), OpIndex + 1, Ops.Num(), *Name);
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
	if (Name == TEXT("stroke")) return StepStroke(Op);
	if (Name == TEXT("hash")) return StepHash(Op);
	if (Name == TEXT("shot")) return StepShot(Op);
	if (Name == TEXT("g1_regions")) return StepG1Regions(Op);
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

ATerrainScriptDirector::EStep ATerrainScriptDirector::StepStroke(const FJsonObject& Op)
{
	TArray<FVector2D> Path;
	FTerrainBrushParams P;
	if (!ParseMode(StrField(Op, TEXT("mode")), P.Mode) || !ParsePath(Op, Path))
	{
		OpError = TEXT("stroke needs mode (raise|lower|smooth|flatten|paint) and path [[x,y],...]");
		return EStep::Failed;
	}
	P.DiameterM = static_cast<float>(NumField(Op, TEXT("d"), 20.0));
	P.Strength = static_cast<float>(NumField(Op, TEXT("s"), 10.0));
	P.PaintLayer = static_cast<int32>(NumField(Op, TEXT("layer"), 0.0));
	const int32 Ticks = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("ticks"), 1.0)));
	const int32 PerFrame = FMath::Max(1, static_cast<int32>(NumField(Op, TEXT("per_frame"), 1.0)));

	if (OpFrame == 0)
	{
		Terrain->BeginStroke(P, Path[0]);
		StrokeTicksDone = 0;
	}
	for (int32 K = 0; K < PerFrame && StrokeTicksDone < Ticks; ++K)
	{
		const double T = Ticks > 1 ? static_cast<double>(StrokeTicksDone) / static_cast<double>(Ticks - 1) : 0.0;
		FTerrainTickTiming Timing;
		Terrain->ApplyTick(PathPoint(Path, T), &Timing);
		Metrics.AddTick(Timing.ApplyMs, Timing.UploadMs, Timing.SplatMs);
		++StrokeTicksDone;
	}
	if (StrokeTicksDone < Ticks)
	{
		return EStep::Running;
	}
	const bool bPushed = Terrain->EndStroke();
	TSharedRef<FJsonObject> S = MakeShared<FJsonObject>();
	S->SetStringField(TEXT("mode"), StrField(Op, TEXT("mode")));
	S->SetNumberField(TEXT("d"), P.DiameterM);
	S->SetNumberField(TEXT("s"), P.Strength);
	S->SetNumberField(TEXT("layer"), P.PaintLayer);
	S->SetNumberField(TEXT("ticks"), Ticks);
	S->SetNumberField(TEXT("ticks_applied"), StrokeTicksDone);
	S->SetNumberField(TEXT("ticks_dropped"), 0);
	S->SetNumberField(TEXT("per_frame"), PerFrame);
	S->SetNumberField(TEXT("frames"), OpFrame + 1);
	S->SetBoolField(TEXT("undo_pushed"), bPushed);
	Strokes.Add(MakeShared<FJsonValueObject>(S));
	UE_LOG(LogChimeraTerrain, Display, TEXT("stroke %s d=%.0f s=%.0f ticks=%d ticks_applied=%d undo_pushed=%d"), *StrField(Op, TEXT("mode")),
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
	Results->SetObjectField(TEXT("options"), Opt);

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
			Results->SetObjectField(TEXT("render"), RJ);
		}
		TSharedRef<FJsonObject> SJ = MakeShared<FJsonObject>();
		SJ->SetNumberField(TEXT("submits"), static_cast<double>(Terrain->GetSplat().Submits()));
		SJ->SetNumberField(TEXT("cleanups"), static_cast<double>(Terrain->GetSplat().Cleanups()));
		Results->SetObjectField(TEXT("splat"), SJ);
		Results->SetStringField(TEXT("final_height_fnv"), TerrainIO::HashToString(Terrain->HeightFnv()));
		Results->SetStringField(TEXT("final_splat_fnv"), TerrainIO::HashToString(Terrain->SplatFnv()));
	}
	Results->SetObjectField(TEXT("hashes"), Hashes);
	Results->SetObjectField(TEXT("shots"), Shots);
	Results->SetArrayField(TEXT("strokes"), Strokes);
	Results->SetArrayField(TEXT("settles"), Settles);
	Results->SetArrayField(TEXT("timeline"), Timeline);
	Results->SetObjectField(TEXT("metrics"), Metrics.ToJson());
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
