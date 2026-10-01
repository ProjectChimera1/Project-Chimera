// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.1 Game/: the RTS camera pawn. The script director and (C8) the player controller place it at named poses.
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Pawn.h"
#include "RtsCameraPawn.generated.h"

class UCameraComponent;

/** A named camera pose: location (cm), rotation and VERTICAL field of view (Godot's convention; RtsCameraController.cs:45-46). */
struct FTerrainCameraPose
{
	FVector LocationCm = FVector::ZeroVector;
	FRotator Rotation = FRotator::ZeroRotator;
	float VFovDeg = 75.0f;

	/**
	 * Built-in poses:
	 *  rts80   look-test CAM_Gameplay (0,-5142,6128) cm, rot (-50,90,0), vFOV 75 (plan C 3.8 S1; lt_common.py:549-552).
	 *  oblique a low three-quarter view of the map centre for look shots (C4/C7 may retune it).
	 *  closeup a low view of S1's dirt path for the ground look (C7 may retune it).
	 *  spike   G1's bounds check: 4 m above the ground at (-128,-155) m, pitched up 45 deg, so no flat ground is in view and the
	 *          G1 spike's chunk lies entirely below the frustum's bottom plane until its edited bounds say otherwise.
	 */
	static bool Find(const FString& Name, FTerrainCameraPose& Out);
};

UCLASS()
class CHIMERATERRAIN_API ARtsCameraPawn : public APawn
{
	GENERATED_BODY()

public:
	ARtsCameraPawn();

	/** Place the camera; FOV is given vertically and converted to Unreal's horizontal FOV at the viewport aspect (16:9 default). */
	void ApplyPose(const FTerrainCameraPose& Pose, float Aspect = 16.0f / 9.0f);

	/** Horizontal FOV for a vertical FOV at an aspect (lt_common.py hfov_from_vfov). */
	static float HFovFromVFov(float VFovDeg, float Aspect);

	UCameraComponent* GetCamera() const { return Camera; }

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UCameraComponent> Camera;
};
