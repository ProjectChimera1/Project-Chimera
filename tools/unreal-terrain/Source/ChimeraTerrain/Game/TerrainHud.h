// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.4: debug HUD (AHUD Canvas, C++): mode, size, strength, layer, last tick ms. Hidden in compare mode.
// Each time the brush part of the drawn line changes it is logged once ("hud: ..."), so a run proves what the HUD showed.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/HUD.h"
#include "TerrainHud.generated.h"

UCLASS()
class CHIMERATERRAIN_API ATerrainHud : public AHUD
{
	GENERATED_BODY()

public:
	virtual void DrawHUD() override;

private:
	/** The brush part of the HUD line last written to the log (C8: the log shows the HUD following the mouse keys). */
	FString LastLoggedBrush;
};
