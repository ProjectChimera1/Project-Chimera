// Copyright Chimera. See ChimeraSimGameMode.h.
#include "ChimeraSimGameMode.h"

#include "Camera/CameraActor.h"
#include "Camera/CameraComponent.h"
#include "ChimeraArena.h"
#include "ChimeraSimDirector.h"
#include "ChimeraSimLog.h"
#include "ChimeraSimOptions.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "HAL/PlatformMisc.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "UObject/UnrealType.h"

AChimeraSimGameMode::AChimeraSimGameMode()
{
	DefaultPawnClass = nullptr; // pawnless: the view comes from the camera actor (plan A F28)
}

void AChimeraSimGameMode::StartPlay()
{
	Super::StartPlay();
	UWorld* World = GetWorld();

	FChimeraSimOptions Options;
	FString Err;
	if (!Options.Parse(Err))
	{
		UE_LOG(LogChimeraSim, Error, TEXT("options: %s (exit 8)"), *Err);
		if (FParse::Param(FCommandLine::Get(), TEXT("ChimeraSimExitWhenDone")))
		{
			GLog->Flush();
			FPlatformMisc::RequestExitWithStatus(true, 8);
		}
		return;
	}
	Options.Echo();

	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	ACameraActor* Overview = nullptr;
	if (!Options.bNoArena)
	{
		World->SpawnActor<AChimeraArena>(AChimeraArena::StaticClass(), FTransform::Identity, Params);
		Overview = SpawnOverviewCamera();
	}
	else
	{
		UE_LOG(LogChimeraSim, Display, TEXT("no arena: keeping the map's own lighting and auto-activating camera"));
	}

	AChimeraSimDirector* Director = World->SpawnActorDeferred<AChimeraSimDirector>(AChimeraSimDirector::StaticClass(), FTransform::Identity,
		nullptr, nullptr, ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
	Director->Configure(Options);
	Director->SetOverviewCamera(Overview);
	Director->FinishSpawning(FTransform::Identity);
}

ACameraActor* AChimeraSimGameMode::SpawnOverviewCamera()
{
	// lt_common.py _orbit_cam: behind the pivot along -Y, pitch above the ground; yaw 90 puts world -X on screen right (F31).
	const double DistM = 150.0, PitchDeg = 55.0;
	const FVector Loc(0.0, -DistM * FMath::Cos(FMath::DegreesToRadians(PitchDeg)) * 100.0, DistM * FMath::Sin(FMath::DegreesToRadians(PitchDeg)) * 100.0);
	const FRotator Rot(-PitchDeg, 90.0, 0.0);
	FActorSpawnParameters Params;
	Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	ACameraActor* Cam = GetWorld()->SpawnActor<ACameraActor>(ACameraActor::StaticClass(), Loc, Rot, Params);
	if (Cam == nullptr)
	{
		UE_LOG(LogChimeraSim, Warning, TEXT("camera: spawn failed"));
		return nullptr;
	}
	Cam->GetCameraComponent()->SetFieldOfView(90.0f);
	Cam->GetCameraComponent()->SetConstraintAspectRatio(false);

	// AutoActivateForPlayer is private with no setter (CameraActor.h:24); set it through reflection so
	// APlayerController::AutoManageActiveCameraTarget (PlayerController.cpp:603-633) keeps picking this camera.
	if (FByteProperty* Prop = CastField<FByteProperty>(ACameraActor::StaticClass()->FindPropertyByName(TEXT("AutoActivateForPlayer"))))
	{
		Prop->SetPropertyValue_InContainer(Cam, (uint8)EAutoReceiveInput::Player0);
	}
	// The player controller already exists at StartPlay, so also make the camera its view target now.
	if (APlayerController* PC = GetWorld()->GetFirstPlayerController())
	{
		PC->SetViewTarget(Cam);
	}
	UE_LOG(LogChimeraSim, Display, TEXT("camera: overview at (%.0f, %.0f, %.0f) cm pitch -%.0f yaw 90 hfov 90, auto_activate=%d"),
		Loc.X, Loc.Y, Loc.Z, PitchDeg, Cam->GetAutoActivatePlayerIndex());
	return Cam;
}
