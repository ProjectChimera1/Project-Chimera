// Copyright Chimera. AChimeraSimGameMode: started with /Engine/Maps/Entry?game=/Script/ChimeraSimHost.ChimeraSimGameMode (plan A F24).
// No pawn, no HUD of its own; spawns the arena, an overview camera and the sim director (plan A 3.7 "Start").
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "ChimeraSimGameMode.generated.h"

class ACameraActor;

UCLASS(NotBlueprintable)
class AChimeraSimGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AChimeraSimGameMode();

	virtual void StartPlay() override;

private:
	/** The "overview" camera of plan A 3.7 (pivot 0,0,0, 150 m, pitch 55, yaw 90, hFOV 90); A11 adds the shot cameras. */
	ACameraActor* SpawnOverviewCamera();
};
