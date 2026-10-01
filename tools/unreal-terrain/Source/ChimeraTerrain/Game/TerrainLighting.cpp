// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/TerrainLighting.h"

#include "ChimeraTerrain.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/PostProcessComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/VolumetricCloudComponent.h"
#include "Materials/MaterialInterface.h"

namespace
{
	/** Engine cloud material, assigned and never edited (lt_common.py:40, 799-800). Cooked via DirectoriesToAlwaysCook. */
	const TCHAR* const CloudMaterialPath = TEXT("/Engine/EngineSky/VolumetricClouds/m_SimpleVolumetricCloud_Inst.m_SimpleVolumetricCloud_Inst");
}

ATerrainLighting::ATerrainLighting()
{
	PrimaryActorTick.bCanEverTick = false;

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;

	// Sun: lt_common.py apply_recipe (sun_pitch -32, yaw 120, 10 lux, source angle 2, 5600 K, atmosphere sun, cloud shadows).
	Sun = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Sun"));
	Sun->SetupAttachment(Root);
	Sun->SetMobility(EComponentMobility::Movable);
	Sun->SetRelativeRotation(FRotator(SunPitch, SunYaw, 0.0f));
	Sun->Intensity = SunLux;
	Sun->LightSourceAngle = SunSourceAngle;
	Sun->bUseTemperature = true;
	Sun->Temperature = SunTemperature;
	Sun->bAtmosphereSunLight = true;
	Sun->bCastCloudShadows = true;

	Atmosphere = CreateDefaultSubobject<USkyAtmosphereComponent>(TEXT("Atmosphere"));
	Atmosphere->SetupAttachment(Root);

	// Sky light: movable, real-time capture, intensity 1.0.
	SkyLight = CreateDefaultSubobject<USkyLightComponent>(TEXT("SkyLight"));
	SkyLight->SetupAttachment(Root);
	SkyLight->SetMobility(EComponentMobility::Movable);
	SkyLight->bRealTimeCapture = true;
	SkyLight->Intensity = 1.0f;

	// Exponential height fog 0.02 / 0.2, no volumetric fog.
	Fog = CreateDefaultSubobject<UExponentialHeightFogComponent>(TEXT("Fog"));
	Fog->SetupAttachment(Root);
	Fog->FogDensity = 0.02f;
	Fog->FogHeightFalloff = 0.2f;

	Clouds = CreateDefaultSubobject<UVolumetricCloudComponent>(TEXT("Clouds"));
	Clouds->SetupAttachment(Root);

	// Unbound post-process volume: GI and reflections ScreenSpace (A_noLumen), saturation 0.95, contrast 1.05, bloom 0.3,
	// vignette 0.4, AO 0.5, motion blur 0, exposure bias 0.
	Post = CreateDefaultSubobject<UPostProcessComponent>(TEXT("Post"));
	Post->SetupAttachment(Root);
	Post->bUnbound = true;
	FPostProcessSettings& S = Post->Settings;
	S.bOverride_DynamicGlobalIlluminationMethod = true;
	S.DynamicGlobalIlluminationMethod = EDynamicGlobalIlluminationMethod::ScreenSpace;
	S.bOverride_ReflectionMethod = true;
	S.ReflectionMethod = EReflectionMethod::ScreenSpace;
	S.bOverride_ColorSaturation = true;
	S.ColorSaturation = FVector4(0.95, 0.95, 0.95, 1.0);
	S.bOverride_ColorContrast = true;
	S.ColorContrast = FVector4(1.05, 1.05, 1.05, 1.0);
	S.bOverride_BloomIntensity = true;
	S.BloomIntensity = 0.3f;
	S.bOverride_VignetteIntensity = true;
	S.VignetteIntensity = 0.4f;
	S.bOverride_AmbientOcclusionIntensity = true;
	S.AmbientOcclusionIntensity = 0.5f;
	S.bOverride_MotionBlurAmount = true;
	S.MotionBlurAmount = 0.0f;
	S.bOverride_AutoExposureBias = true;
	S.AutoExposureBias = 0.0f;
}

void ATerrainLighting::BeginPlay()
{
	Super::BeginPlay();
	if (UMaterialInterface* CloudMat = LoadObject<UMaterialInterface>(nullptr, CloudMaterialPath))
	{
		Clouds->SetMaterial(CloudMat);
	}
	else
	{
		UE_LOG(LogChimeraTerrain, Error, TEXT("lighting: could not load cloud material %s"), CloudMaterialPath);
	}
	UE_LOG(LogChimeraTerrain, Display, TEXT("lighting: LT_A_noLumen recipe spawned (sun pitch %.0f yaw %.0f %.0f lux)"), SunPitch, SunYaw, SunLux);
}

void ATerrainLighting::SetCompareMode(bool bCompare, float EV100)
{
	bCompareMode = bCompare;
	CompareEV100 = EV100;
	Clouds->SetVisibility(!bCompare);
	FPostProcessSettings& S = Post->Settings;
	// Min == Max disables eye adaptation (Scene.h:1988-2002); EV100 because ExtendDefaultLuminanceRange is on.
	S.bOverride_AutoExposureMinBrightness = bCompare;
	S.bOverride_AutoExposureMaxBrightness = bCompare;
	S.AutoExposureMinBrightness = EV100;
	S.AutoExposureMaxBrightness = EV100;
	UE_LOG(LogChimeraTerrain, Display, TEXT("look %s (clouds %s, exposure %s)"), bCompare ? TEXT("compare") : TEXT("full"),
		bCompare ? TEXT("off") : TEXT("on"), bCompare ? *FString::Printf(TEXT("fixed EV100 %.2f"), EV100) : TEXT("auto"));
}

FVector ATerrainLighting::GetDirectionToSun() const
{
	return -Sun->GetForwardVector();
}
