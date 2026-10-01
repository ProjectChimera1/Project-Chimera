// Copyright Chimera. See ChimeraArena.h.
#include "ChimeraArena.h"

#include "ChimeraSimLog.h"
#include "Components/DirectionalLightComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/PostProcessComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "UObject/ConstructorHelpers.h"

namespace ArenaRecipe
{
	// lt_common.py _A with gi_method/reflection_method SCREEN_SPACE (A_noLumen), lines 427-439.
	constexpr float SunPitch = -32.0f;
	constexpr float SunYaw = 120.0f;
	constexpr float SunLux = 10.0f;
	constexpr float SunSourceAngle = 2.0f;
	constexpr float SunTemperature = 5600.0f;
	constexpr float SkyLightIntensity = 1.0f;
	constexpr float FogDensity = 0.02f;
	constexpr float FogHeightFalloff = 0.2f;
	constexpr float Saturation = 0.95f;
	constexpr float Contrast = 1.05f;
	constexpr float Bloom = 0.3f;
	constexpr float Vignette = 0.4f;
	constexpr float Ao = 0.5f;
	constexpr float MotionBlur = 0.0f;
	constexpr float ExposureBias = 0.0f;
	// lt_build.py: 'Tiling' 240 on a 1200 m plane = 5 m tiles; 300 m keeps 5 m tiles at 60.
	constexpr float GroundTiling = 60.0f;
	const TCHAR* GroundMaterial = TEXT("/Game/LookTest/Materials/M_LT_Ground.M_LT_Ground");
}

AChimeraArena::AChimeraArena()
{
	PrimaryActorTick.bCanEverTick = false;

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetRootComponent(Root);

	Ground = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("Ground"));
	Ground->SetupAttachment(Root);
	static ConstructorHelpers::FObjectFinder<UStaticMesh> PlaneMesh(TEXT("/Engine/BasicShapes/Plane.Plane"));
	if (PlaneMesh.Succeeded())
	{
		Ground->SetStaticMesh(PlaneMesh.Object);
	}
	// The engine plane is 100 cm square.
	Ground->SetRelativeScale3D(FVector(GroundSizeM, GroundSizeM, 1.0f));
	Ground->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Ground->SetCanEverAffectNavigation(false);

	Sun = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Sun"));
	Sun->SetupAttachment(Root);
	Sun->SetMobility(EComponentMobility::Movable);
	Sun->SetRelativeRotation(FRotator(ArenaRecipe::SunPitch, ArenaRecipe::SunYaw, 0.0f));

	Atmosphere = CreateDefaultSubobject<USkyAtmosphereComponent>(TEXT("Atmosphere"));
	Atmosphere->SetupAttachment(Root);

	SkyLight = CreateDefaultSubobject<USkyLightComponent>(TEXT("SkyLight"));
	SkyLight->SetupAttachment(Root);
	SkyLight->SetMobility(EComponentMobility::Movable);

	Fog = CreateDefaultSubobject<UExponentialHeightFogComponent>(TEXT("Fog"));
	Fog->SetupAttachment(Root);

	Post = CreateDefaultSubobject<UPostProcessComponent>(TEXT("Post"));
	Post->SetupAttachment(Root);
	Post->bUnbound = true;
	FPostProcessSettings& S = Post->Settings;
	S.bOverride_DynamicGlobalIlluminationMethod = true;
	S.DynamicGlobalIlluminationMethod = EDynamicGlobalIlluminationMethod::ScreenSpace;
	S.bOverride_ReflectionMethod = true;
	S.ReflectionMethod = EReflectionMethod::ScreenSpace;
	S.bOverride_ColorSaturation = true;
	S.ColorSaturation = FVector4(ArenaRecipe::Saturation, ArenaRecipe::Saturation, ArenaRecipe::Saturation, 1.0f);
	S.bOverride_ColorContrast = true;
	S.ColorContrast = FVector4(ArenaRecipe::Contrast, ArenaRecipe::Contrast, ArenaRecipe::Contrast, 1.0f);
	S.bOverride_BloomIntensity = true;
	S.BloomIntensity = ArenaRecipe::Bloom;
	S.bOverride_VignetteIntensity = true;
	S.VignetteIntensity = ArenaRecipe::Vignette;
	S.bOverride_AmbientOcclusionIntensity = true;
	S.AmbientOcclusionIntensity = ArenaRecipe::Ao;
	S.bOverride_MotionBlurAmount = true;
	S.MotionBlurAmount = ArenaRecipe::MotionBlur;
	S.bOverride_AutoExposureBias = true;
	S.AutoExposureBias = ArenaRecipe::ExposureBias;
}

void AChimeraArena::BeginPlay()
{
	Super::BeginPlay();

	// Lights and fog through their runtime setters (all Movable), as apply_recipe sets them on components.
	Sun->SetIntensity(ArenaRecipe::SunLux);
	Sun->SetLightSourceAngle(ArenaRecipe::SunSourceAngle);
	Sun->SetUseTemperature(true);
	Sun->SetTemperature(ArenaRecipe::SunTemperature);
	Sun->SetAtmosphereSunLight(true);
	SkyLight->SetRealTimeCapture(true);
	SkyLight->SetIntensity(ArenaRecipe::SkyLightIntensity);
	Fog->SetFogDensity(ArenaRecipe::FogDensity);
	Fog->SetFogHeightFalloff(ArenaRecipe::FogHeightFalloff);
	Fog->SetVolumetricFog(false);

	UMaterialInterface* Mat = LoadObject<UMaterialInterface>(nullptr, ArenaRecipe::GroundMaterial);
	if (Mat != nullptr)
	{
		UMaterialInstanceDynamic* Mid = UMaterialInstanceDynamic::Create(Mat, this);
		Mid->SetScalarParameterValue(TEXT("Tiling"), ArenaRecipe::GroundTiling);
		Ground->SetMaterial(0, Mid);
	}
	else
	{
		// Not a sim error: the ground keeps the engine default material (the run stays valid for parity).
		UE_LOG(LogChimeraSim, Warning, TEXT("arena: %s did not load; ground keeps its default material"), ArenaRecipe::GroundMaterial);
	}
	UE_LOG(LogChimeraSim, Display, TEXT("arena: ground %.0f m, sun pitch %.0f yaw %.0f %.0f lux, fog %.3f/%.2f, GI+reflections ScreenSpace, material %s"),
		GroundSizeM, ArenaRecipe::SunPitch, ArenaRecipe::SunYaw, ArenaRecipe::SunLux, ArenaRecipe::FogDensity, ArenaRecipe::FogHeightFalloff,
		Mat != nullptr ? TEXT("M_LT_Ground") : TEXT("default"));
}
