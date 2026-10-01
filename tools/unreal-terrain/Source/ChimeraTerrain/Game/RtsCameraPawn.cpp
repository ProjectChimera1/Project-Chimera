// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Game/RtsCameraPawn.h"

#include "Camera/CameraComponent.h"

bool FTerrainCameraPose::Find(const FString& Name, FTerrainCameraPose& Out)
{
	if (Name.Equals(TEXT("rts80"), ESearchCase::IgnoreCase))
	{
		Out.LocationCm = FVector(0.0, -5142.0, 6128.0);
		Out.Rotation = FRotator(-50.0, 90.0, 0.0);
		Out.VFovDeg = 75.0f;
		return true;
	}
	if (Name.Equals(TEXT("oblique"), ESearchCase::IgnoreCase))
	{
		// From (60, -90, 25) m toward the map centre: yaw atan2(90, -60) = 123.7, pitch -atan(25 / 108.2) = -13.0.
		Out.LocationCm = FVector(6000.0, -9000.0, 2500.0);
		Out.Rotation = FRotator(-13.0, 123.7, 0.0);
		Out.VFovDeg = 50.0f;
		return true;
	}
	if (Name.Equals(TEXT("closeup"), ESearchCase::IgnoreCase))
	{
		// Low and near S1's dirt path (path from (-60,-20) to (60,70) m): from (-66,-30) m, 3.5 m up, toward (-48,-8) m:
		// yaw atan2(22, 18) = 50.7, pitch -atan(3.5 / 28.4) = -7.0. C7 judges the ground look from this pose and may retune it.
		Out.LocationCm = FVector(-6600.0, -3000.0, 350.0);
		Out.Rotation = FRotator(-7.0, 50.7, 0.0);
		Out.VFovDeg = 60.0f;
		return true;
	}
	if (Name.Equals(TEXT("spike"), ESearchCase::IgnoreCase))
	{
		Out.LocationCm = FVector(-12800.0, -15500.0, 400.0);
		Out.Rotation = FRotator(45.0, 90.0, 0.0);
		Out.VFovDeg = 75.0f;
		return true;
	}
	return false;
}

ARtsCameraPawn::ARtsCameraPawn()
{
	PrimaryActorTick.bCanEverTick = false;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("Camera"));
	Camera->SetupAttachment(Root);
	Camera->bConstrainAspectRatio = false;
	Camera->SetFieldOfView(HFovFromVFov(75.0f, 16.0f / 9.0f));
	SetCanBeDamaged(false);
	// The pose owns the camera rotation; the controller's rotation must not override it (APawn::FaceRotation).
	bUseControllerRotationPitch = false;
	bUseControllerRotationYaw = false;
	bUseControllerRotationRoll = false;
}

float ARtsCameraPawn::HFovFromVFov(float VFovDeg, float Aspect)
{
	return FMath::RadiansToDegrees(2.0f * FMath::Atan(FMath::Tan(FMath::DegreesToRadians(VFovDeg) * 0.5f) * Aspect));
}

void ARtsCameraPawn::ApplyPose(const FTerrainCameraPose& Pose, float Aspect)
{
	SetActorLocationAndRotation(Pose.LocationCm, Pose.Rotation, false, nullptr, ETeleportType::TeleportPhysics);
	Camera->SetFieldOfView(HFovFromVFov(Pose.VFovDeg, Aspect));
	if (AController* C = GetController())
	{
		C->SetControlRotation(Pose.Rotation);
	}
}
