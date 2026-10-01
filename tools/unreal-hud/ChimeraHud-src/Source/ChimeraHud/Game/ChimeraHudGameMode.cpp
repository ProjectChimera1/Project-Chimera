// Copyright Epic Games, Inc. All Rights Reserved.

#include "Game/ChimeraHudGameMode.h"
#include "Game/ChimeraHudActor.h"

AChimeraHudGameMode::AChimeraHudGameMode()
{
	HUDClass = AChimeraHudActor::StaticClass();
}
