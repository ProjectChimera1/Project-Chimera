// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/TerrainHud.h"

#include "Engine/Canvas.h"
#include "Engine/Engine.h"
#include "EngineUtils.h"
#include "ChimeraTerrain.h"
#include "Game/TerrainActor.h"

void ATerrainHud::DrawHUD()
{
	Super::DrawHUD();
	if (!Canvas)
	{
		return;
	}
	const ATerrainActor* Terrain = nullptr;
	for (TActorIterator<ATerrainActor> It(GetWorld()); It; ++It)
	{
		Terrain = *It;
		break;
	}
	if (!Terrain)
	{
		return;
	}
	static const TCHAR* const ModeNames[] = { TEXT("Raise"), TEXT("Lower"), TEXT("Smooth"), TEXT("Flatten"), TEXT("Paint") };
	static const TCHAR* const LayerNames[] = { TEXT("Grass"), TEXT("Dirt"), TEXT("Rock"), TEXT("Snow") };
	const ChimeraTerrain::FTerrainBrushParams& P = Terrain->GetBrushParams();
	const int32 ModeIdx = FMath::Clamp(static_cast<int32>(P.Mode), 0, 4);
	const int32 LayerIdx = FMath::Clamp(P.PaintLayer, 0, 3);
	const FString BrushPart = FString::Printf(TEXT("Terrain  mode %s  size %.0f m  strength %.0f  layer %s"),
		ModeNames[ModeIdx], P.DiameterM, P.Strength, LayerNames[LayerIdx]);
	const FString Line = FString::Printf(TEXT("%s  last tick %.2f ms"), *BrushPart, Terrain->GetLastTickMs());
	if (BrushPart != LastLoggedBrush)
	{
		LastLoggedBrush = BrushPart;
		UE_LOG(LogChimeraTerrain, Display, TEXT("hud: %s"), *Line);
	}
	DrawText(Line, FLinearColor::White, 24.0f, 24.0f, GEngine->GetSmallFont(), 1.25f);
}
