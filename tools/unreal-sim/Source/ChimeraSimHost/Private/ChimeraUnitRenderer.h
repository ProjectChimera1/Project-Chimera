// Copyright Chimera. AChimeraUnitRenderer: draws every sim unit and building with instanced static meshes (plan A 3.7 "Rendering";
// R3 4.1-4.3). Id-keyed: one ISM per (faction, definition) from chimera_unit_def_id and P/SimTrial/unit_meshes.json, one instance per
// entity id for its generation; positions are lerp(prev, pos, alpha) of chimera_read_units mapped UE(X,Y,Z) = 100*(x, z, y).
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "ChimeraSimLibrary.h"
#include "ChimeraUnitRenderer.generated.h"

class UInstancedStaticMeshComponent;
class UPostProcessComponent;
class UMaterialInterface;
class UStaticMesh;

/** One draw group: an ISM for one (faction ordinal, definition id) of unit_meshes.json, or a proxy cube when unmapped. */
struct FChimeraDrawGroup
{
	uint8 Faction = 0;
	FString DefId;
	bool bBuilding = false;
	bool bProxy = false;
	/** unit_meshes.json values: uniform scale, actor yaw that turns the mesh front to +X, pivot lift, height after scale (cm). */
	FVector Scale = FVector::OneVector;
	double FrontYawDeg = 0.0;
	double FootZCm = 0.0;
	double HeightCm = 100.0;
	/** Half the horizontal diagonal of size_cm (after scale): the most the mesh reaches from its pivot axis at any yaw. */
	double RadiusCm = 50.0;
	FString MeshPath;
	int32 Component = -1;
	/** Pending transforms, one per instance (index = ISM instance index); written whole once per frame. */
	TArray<FTransform> Transforms;
};

/** One instance creation, in order. The verify step rebuilds its own id -> (group, index) map from this journal (plan A 3.7 "Verify"). */
struct FChimeraInstanceAdd
{
	int32 Id = 0;
	int32 Generation = 0;
	int32 Group = 0;
	int32 Index = 0;
};

/** One building instance creation, in order (slots are recycled, so a slot can appear more than once; the last entry is current). */
struct FChimeraBuildingAdd
{
	int32 Slot = 0;
	uint8 Faction = 0;
	FString DefId;
	int32 Group = 0;
	int32 Index = 0;
};

UCLASS(NotBlueprintable)
class AChimeraUnitRenderer : public AActor
{
	GENERATED_BODY()

public:
	AChimeraUnitRenderer();

	/**
	 * Loads the mesh table, reads the session's units and buildings once and creates their groups and instances.
	 * bHideUnits keeps unit groups invisible (A12's -ChimeraSimHideUnits); bDeadUnderGround moves dead/phased instances under the
	 * ground instead of scaling them to 0 (the plan A 3.7 fallback). False with OutError when the table cannot be read.
	 */
	bool Init(int32 InSession, const FString& MeshesJsonPath, bool bHideUnits, bool bDeadUnderGround, FString& OutError);

	/** Reads units and buildings and writes every instance transform for interpolation alpha (one batch update per component). */
	bool Update(double Alpha, FString& OutError);

	/** Hides or shows the unit groups (buildings stay); used for the hidden frame of a shot pair. */
	void SetUnitsVisible(bool bVisible);
	bool AreUnitsVisible() const { return bUnitsVisible; }

	/** Holds auto exposure (adaptation 0.001 EV/s) through an unbound post-process component, so a shot pair shares one exposure. */
	void SetExposureHold(bool bHold);

	const TArray<FChimeraDrawGroup>& GetGroups() const { return Groups; }
	UInstancedStaticMeshComponent* GetComponent(int32 Group) const;
	const TArray<FChimeraInstanceAdd>& GetJournal() const { return Journal; }
	/** Building creations; entries at or after GetInitialBuildingAdds() were made during the run (AI-built buildings). */
	const TArray<FChimeraBuildingAdd>& GetBuildingJournal() const { return BuildingJournal; }
	int32 GetInitialBuildingAdds() const { return InitialBuildingAdds; }
	/** True when -ChimeraSimHideUnits keeps the unit components invisible (A12's HideUnits run). */
	bool IsHidingUnits() const { return bHide; }
	/** Unit high-water mark at Init: ids at or above it were created during the run (AI-created units). */
	int32 GetInitialUnitRows() const { return InitialUnitRows; }
	int32 GetReAdds() const { return ReAdds; }
	int32 GetNewIdAdds() const { return NewIdAdds; }
	int32 GetBuildingAdds() const { return BuildingAdds; }
	/** Per id: whether the last Update drew it (alive and not phased) - used only for logging, verify reads the ISMs. */
	int32 GetDrawnLastFrame() const { return DrawnLastFrame; }
	/** World direction the sun's light travels (for the shadow extent of the army boxes); zero when no directional light exists. */
	FVector GetSunDirection() const;

	/** Plain-text summary of the groups ("alpha/worker x20, ..."). */
	FString Describe() const;

	static constexpr double PosScale = 100.0 / 65536.0; // Fixed raw -> metres -> cm
	static constexpr double MinYawDisplacementM = 0.01;  // plan A 3.7: yaw from pos - prev when longer than 0.01 m (F18)
	static constexpr double UnderGroundCm = -100000.0;
	/** A per-tick displacement above 1 m is a spawn or an unset prev, not a move (fastest unit: 6.5 m/s = 0.22 m per tick). */
	static constexpr double MaxTickDisplacementCm = 100.0;

private:
	struct FMeshEntry
	{
		FString Id; // "alpha/worker"
		uint8 Faction = 0;
		FString DefId;
		bool bBuilding = false;
		FString Mesh;
		TArray<FString> TeamMis;
		double Scale = 1.0;
		double FrontYawDeg = 0.0;
		double FootZCm = 0.0;
		double HeightCm = 100.0;
		double RadiusCm = 50.0;
	};

	struct FUnitSlot
	{
		int32 Generation = -1;
		uint8 MeshType = 0;
		uint8 Faction = 0;
		int32 Group = -1;
		int32 Index = -1;
		double YawDeg = 0.0;
	};

	struct FBuildingSlot
	{
		bool bAlive = false;
		uint8 Type = 0;
		uint8 Faction = 0;
		FString DefId;
		int32 Group = -1;
		int32 Index = -1;
	};

	bool LoadTable(const FString& Path, FString& OutError);
	int32 FindOrCreateGroup(uint8 Faction, const FString& DefId, bool bBuilding, int32 Reserve);
	/** Appends an instance; INDEX_NONE (with OutError) if the ISM did not return the next append index. */
	int32 AddInstance(int32 Group, const FTransform& Xf, FString& OutError);
	FTransform UnitTransform(const FChimeraDrawGroup& G, const ChimeraUnit& U, double Alpha, double YawDeg, bool bDrawn) const;
	FTransform BuildingTransform(const FChimeraDrawGroup& G, const ChimeraBuilding& B, bool bDrawn) const;
	static FString FactionName(uint8 Faction);
	static double InitialYaw(uint8 Faction);
	UMaterialInterface* LoadMaterial(const FString& Path) const;
	static FString ObjectPath(const FString& PackagePath);

	int32 Session = 0;
	bool bHide = false;
	bool bUnderGround = false;
	bool bUnitsVisible = true;
	int32 InitialUnitRows = 0;
	int32 ReAdds = 0;
	int32 NewIdAdds = 0;
	int32 BuildingAdds = 0;
	int32 InitialBuildingAdds = 0;
	int32 DrawnLastFrame = 0;

	TArray<FMeshEntry> Table;
	TMap<FString, int32> GroupByKey; // "u:<faction>:<def>" / "b:<faction>:<def>"
	TArray<FChimeraDrawGroup> Groups;
	TArray<FUnitSlot> Units;
	TArray<FBuildingSlot> Buildings;
	TArray<FChimeraInstanceAdd> Journal;
	TArray<FChimeraBuildingAdd> BuildingJournal;
	TArray<ChimeraUnit> UnitRows;
	TArray<ChimeraBuilding> BuildingRows;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UPostProcessComponent> ExposurePost;

	UPROPERTY(Transient)
	TArray<TObjectPtr<UInstancedStaticMeshComponent>> Components;
};
