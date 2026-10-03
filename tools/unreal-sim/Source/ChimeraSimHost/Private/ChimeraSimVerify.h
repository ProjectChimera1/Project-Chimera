// Copyright Chimera. The verify step of plan A 3.7, kept apart from the render path: a fresh chimera_read_units and
// chimera_read_buildings into buffers of its own, its own id -> (group, index) and slot -> (group, index) maps rebuilt from the
// renderer's creation journals, and the ISMs' own instance transforms (GetInstanceTransform) compared with 100 * map(pos).
// Independent of the journals: the count of drawn instances over every ISM (an orphaned or never-journalled instance left at a
// non-zero scale shows up there). Plus the screen projection of the units per army for check_shots.py.
#pragma once

#include "CoreMinimal.h"
#include "ChimeraSimLibrary.h"
#include "Dom/JsonObject.h"

class AChimeraUnitRenderer;
class APlayerController;

/** One verify at one tick (alpha 1, stepping frozen). */
struct FChimeraVerifyResult
{
	int32 Tick = 0;
	int32 Rows = 0;
	int32 Alive = 0;         // flags & Alive
	int32 Phased = 0;        // alive and Phased (inside a building: not drawn)
	int32 Expected = 0;      // alive and not phased: the ids that must be visible
	int32 Visible = 0;       // ids whose current-generation instance has a non-zero scale in a visible component
	int32 DeadVisible = 0;   // visible but not expected
	int32 AliveHidden = 0;   // expected but not visible (includes NoInstance)
	int32 NoInstance = 0;    // expected but the journal has no instance of this generation
	int32 GroupMismatch = 0; // instance in a group whose faction/definition differs from a fresh chimera_unit_def_id
	double MaxErrCm = 0.0;
	int32 MaxErrId = -1;
	int64 StatsAlive = -1;   // chimera_stats[6]
	int32 InitialRows = 0;
	int32 NewIdsAlive = 0;   // alive ids >= the initial high-water mark (created during the run, e.g. by the AI)
	int32 NewIdsVisible = 0;
	/** Instances with a non-zero scale over every unit ISM, counted without the journal (must equal Expected). */
	int32 DrawnInstances = 0;
	/** -ChimeraSimHideUnits: the unit components are invisible by option, so unit visibility is judged by instance scale only. */
	bool bUnitsHiddenByOption = false;

	/** Buildings (plan A 3.3: the ai variant builds 2 -> 7): fresh chimera_read_buildings vs the building ISMs. */
	int32 BuildingRows = 0;
	int32 BuildingsAlive = 0;
	int32 BuildingsVisible = 0;        // alive slots whose current instance is drawn in the group of (faction, fresh def id)
	int32 BuildingDeadVisible = 0;     // dead slots whose last instance is still drawn
	int32 BuildingAliveHidden = 0;     // alive slots without a drawn current instance (includes no instance)
	int32 BuildingGroupMismatch = 0;   // current instance in a group whose faction/definition differs from a fresh chimera_building_def_id
	int32 BuildingDrawnInstances = 0;  // instances with a non-zero scale over every building ISM (must equal BuildingsAlive)
	double BuildingMaxErrCm = 0.0;
	int32 BuildingMaxErrSlot = -1;
	int32 InitialBuildingAdds = 0;     // journal entries made at Init (the scenario's buildings)
	int32 AiCreatedAlive = 0;          // alive slots whose current instance was added during the run (AI-built)
	int32 AiCreatedVisible = 0;
	TArray<TSharedPtr<FJsonValue>> AiCreated; // [slot, faction, def id, visible] per AI-built alive slot
	bool bBuildingsPass = false;

	TArray<int32> DeadVisibleIds;
	TArray<int32> AliveHiddenIds;
	bool bPass = false;

	/** The fresh rows and each row's group (or -1), kept for the projections of the shots at this tick. */
	TArray<ChimeraUnit> Units;
	TArray<int32> GroupOf;
	TArray<TSharedPtr<FJsonValue>> Shots;

	TSharedRef<FJsonObject> ToJson() const;
};

namespace ChimeraSimVerify
{
	/** Runs the verify; false only on an ABI error (OutError). A failed comparison is bPass = false, not an error. */
	bool Run(int32 Session, const AChimeraUnitRenderer& Renderer, int32 Tick, FChimeraVerifyResult& Out, FString& OutError);

	/** Projects up to SamplesPerArmy alive units per army (id stride) at mid-height, an army box over every alive unit's foot, top
	 *  and shadow tip, and per unit a footprint box (foot, top and shadow tip, each widened by the mesh radius in X and Y) for every
	 *  unit whose footprint reaches the screen, through APlayerController::ProjectWorldLocationToScreen with the current view target. */
	TSharedRef<FJsonObject> ProjectShot(const APlayerController& PC, const AChimeraUnitRenderer& Renderer, const FChimeraVerifyResult& V,
		const FIntPoint& ViewportSize, int32 SamplesPerArmy);

	constexpr double MaxErrCmBar = 0.5;  // plan A 4 A11
	constexpr double VisibleScaleEps = 1e-4;
}
