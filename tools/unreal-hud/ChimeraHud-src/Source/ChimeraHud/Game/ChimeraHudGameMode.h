// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "ChimeraHudGameMode.generated.h"

/** Game mode of the HUD trial: engine default pawn, HUDClass = AChimeraHudActor (adds the Slate root widget). */
UCLASS()
class AChimeraHudGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AChimeraHudGameMode();
};
