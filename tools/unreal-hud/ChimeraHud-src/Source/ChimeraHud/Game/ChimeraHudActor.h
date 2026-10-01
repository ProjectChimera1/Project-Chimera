// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/HUD.h"
#include "ChimeraHudActor.generated.h"

class SWidget;

/**
 * AHUD that owns the Slate root widget (SChimeraMatchHud). Adds it to the game viewport on BeginPlay, removes it on EndPlay,
 * applies -HudUiScale, and starts the capture automation when -HudShot or -HudWarmup is on the command line.
 */
UCLASS()
class AChimeraHudActor : public AHUD
{
	GENERATED_BODY()

public:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	TSharedPtr<SWidget> Root;
};
