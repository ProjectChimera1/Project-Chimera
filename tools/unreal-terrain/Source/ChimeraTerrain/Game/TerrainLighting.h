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

	/**
	 * Recipe values (lt_common.py:561-576, A_noLumen), with one G1 look-pass change: the sun is white (6500 K, was 5600 K). G0 measured
	 * C7's light warming the ground by linear gains R 1.20 / G 0.85 / B 0.64 at rts80, about a third of its hue gap to Manor Lords.
	 * A higher sun (pitch -42) was tried and dropped: gate G1's hill then has no self-shadowed side (hill_shadow 0 px at -42, 39 px at
	 * -38, 207 at -36, 680 at -32; Out/g1_pitch*), and -32 costs only 1.5 deg of grass hue against -36 (g1_v6_1 vs g1_v7_1). Lumen stays off.
	 */
	static constexpr float SunPitch = -32.0f;
	static constexpr float SunYaw = 120.0f;
	static constexpr float SunLux = 10.0f;
	static constexpr float SunSourceAngle = 2.0f;
	static constexpr float SunTemperature = 6500.0f;
	/** Fixed exposure used by compare mode, EV100 (ExtendDefaultLuminanceRange=True, DefaultEngine.ini). Override: -ChimeraTerrainExposure=. */
	static constexpr float DefaultCompareEV100 = 2.0f;

	/** Compare mode (plan C 3.5): clouds hidden and exposure fixed at EV100; full mode: clouds on, auto exposure. */
	void SetCompareMode(bool bCompare, float EV100);
	bool IsCompareMode() const { return bCompareMode; }
	float GetCompareEV100() const { return CompareEV100; }

	/**
	 * -ChimeraTerrainLight=Name=Value,... (G1 look tuning without a rebuild). Names: SunPitch, SunYaw, SunLux, SunTemp, SunAngle,
	 * SkyInt, FogDensity, FogFalloff, FogStart, FogR/FogG/FogB (inscattering colour, linear), FogSkyR/FogSkyG/FogSkyB (scale of the
	 * sky atmosphere's contribution to the fog), GroundR/GroundG/GroundB (SkyAtmosphere ground albedo, linear 0..1), ExposureBias,
	 * Saturation, Contrast, ShadowSat, ShadowContrast, ShadowGainR/ShadowGainG/ShadowGainB (colour grading of the shadow range),
	 * WhiteTemp, WhiteTint. An unknown name or a non-numeric value is logged as an Error (the run's log scan then fails).
	 * Returns the number of items applied.
	 */
	int32 ApplyOverrides(const FString& Spec);

	/**
	 * G1 round 1: the look's lighting and grading values live as text in Config/DefaultGame.ini,
	 * [/Script/ChimeraTerrain.TerrainLighting] LookOverrides=<ApplyOverrides spec>, so they can be tuned without a rebuild. The game mode
	 * applies them first and any -ChimeraTerrainLight= after them. Returns the spec read (empty when the key is absent).
	 */
	static FString ReadConfigLookOverrides();

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
