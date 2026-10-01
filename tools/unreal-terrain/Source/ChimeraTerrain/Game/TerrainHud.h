// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.4: debug HUD (AHUD Canvas, C++): mode, size, strength, layer, last tick ms. Hidden in compare mode.
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
};
