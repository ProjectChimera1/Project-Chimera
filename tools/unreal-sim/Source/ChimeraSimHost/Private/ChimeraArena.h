// Copyright Chimera. AChimeraArena: the battle arena of a sim run (plan A 3.7): a 300 m ground plane with M_LT_Ground and the
// Look A_noLumen lighting recipe (plan A F31; P/LookTest/tools/lt_common.py RECIPES['A_noLumen'], applied as apply_recipe does).
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "ChimeraArena.generated.h"

class UStaticMeshComponent;
class UDirectionalLightComponent;
class USkyAtmosphereComponent;
class USkyLightComponent;
class UExponentialHeightFogComponent;
class UPostProcessComponent;

UCLASS(NotBlueprintable)
class AChimeraArena : public AActor
{
	GENERATED_BODY()

public:
	AChimeraArena();

	/** Ground side length in metres (the units fight inside +-100 m in x, +-90 m in z). */
	static constexpr float GroundSizeM = 300.0f;

protected:
	virtual void BeginPlay() override;

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UStaticMeshComponent> Ground;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UDirectionalLightComponent> Sun;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USkyAtmosphereComponent> Atmosphere;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USkyLightComponent> SkyLight;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UExponentialHeightFogComponent> Fog;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UPostProcessComponent> Post;
};
