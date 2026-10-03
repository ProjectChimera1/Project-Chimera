// Copyright Epic Games, Inc. All Rights Reserved.

#include "Game/ChimeraHudActor.h"
#include "Game/ChimeraHudShot.h"
#include "Ui/ChimeraUi.h"
#include "Ui/SChimeraMatchHud.h"
#include "ChimeraHud.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "Engine/UserInterfaceSettings.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"

void AChimeraHudActor::BeginPlay()
{
	Super::BeginPlay();

	float UiScale = 1.0f;
	if (FParse::Value(FCommandLine::Get(), TEXT("-HudUiScale="), UiScale))
	{
		// Set before the widget is added (README's 80-150% hook).
		GetMutableDefault<UUserInterfaceSettings>()->ApplicationScale = UiScale;
		UE_LOG(LogChimeraHud, Display, TEXT("ApplicationScale=%f"), UiScale);
	}

	if (FParse::Param(FCommandLine::Get(), TEXT("HudOff")))
	{
		// HUD-cost baseline (plan B T8, T/hud_cost.ps1): the same map, game mode and command line with no root widget.
		UE_LOG(LogChimeraHud, Display, TEXT("HUD off: root widget not added (-HudOff)"));
	}
	else if (GEngine && GEngine->GameViewport)
	{
		FString Backdrop = TEXT("#14161A");
		FParse::Value(FCommandLine::Get(), TEXT("-HudBackdrop="), Backdrop, false);
		FString Pattern;
		FParse::Value(FCommandLine::Get(), TEXT("-HudTestPattern="), Pattern, false);

		Root = SNew(SChimeraMatchHud).Backdrop(Backdrop).TestPattern(Pattern);
		GEngine->GameViewport->AddViewportWidgetContent(Root.ToSharedRef(), 10);
		UE_LOG(LogChimeraHud, Display, TEXT("root widget added backdrop=%s pattern=%s"), *Backdrop, *Pattern);
	}
	else
	{
		UE_LOG(LogChimeraHud, Error, TEXT("no GameViewport; HUD not added"));
	}

	FChimeraHudShot::StartIfRequested();
}

void AChimeraHudActor::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	if (Root.IsValid() && GEngine && GEngine->GameViewport)
	{
		GEngine->GameViewport->RemoveViewportWidgetContent(Root.ToSharedRef());
	}
	Root.Reset();
	Super::EndPlay(EndPlayReason);
}
