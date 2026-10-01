// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.5 lighting: the LT_A_noLumen recipe (tools/unreal-looktest/lt_common.py _A + A_noLumen) spawned in C++, plus the
// compare mode of plan C 3.5 (clouds off, fixed exposure).
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "TerrainLighting.generated.h"

class UDirectionalLightComponent;
class USkyAtmosphereComponent;
class USkyLightComponent;
class UExponentialHeightFogComponent;
class UVolumetricCloudComponent;
class UPostProcessComponent;

UCLASS()
class CHIMERATERRAIN_API ATerrainLighting : public AActor
{
	GENERATED_BODY()

public:
	ATerrainLighting();

	/** Recipe values (lt_common.py:561-576, A_noLumen). */
	static constexpr float SunPitch = -32.0f;
	static constexpr float SunYaw = 120.0f;
	static constexpr float SunLux = 10.0f;
	static constexpr float SunSourceAngle = 2.0f;
	static constexpr float SunTemperature = 5600.0f;
	/** Fixed exposure used by compare mode, EV100 (ExtendDefaultLuminanceRange=True, DefaultEngine.ini). Override: -ChimeraTerrainExposure=. */
	static constexpr float DefaultCompareEV100 = 2.0f;

	/** Compare mode (plan C 3.5): clouds hidden and exposure fixed at EV100; full mode: clouds on, auto exposure. */
	void SetCompareMode(bool bCompare, float EV100);
	bool IsCompareMode() const { return bCompareMode; }
	float GetCompareEV100() const { return CompareEV100; }

	/** Unit vector pointing from the ground TOWARD the sun (the light travels along -this). */
	FVector GetDirectionToSun() const;

	virtual void BeginPlay() override;

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UDirectionalLightComponent> Sun;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USkyAtmosphereComponent> Atmosphere;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USkyLightComponent> SkyLight;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UExponentialHeightFogComponent> Fog;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UVolumetricCloudComponent> Clouds;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UPostProcessComponent> Post;

	bool bCompareMode = false;
	float CompareEV100 = DefaultCompareEV100;
};
