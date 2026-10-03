// Project Chimera terrain trial (plan C 4 C9, plan C scatter 3.10). Original Chimera code.
// The unit layer's pure part: a deterministic layout of N units on the map, and the transform of a unit standing on the DRAWN surface
// (FTerrainHeightfield::SampleSurface, the triangulated surface the mesh, the pick and depthcheck agree with; never a new bilinear sampler and never
// the sim's nearest-cell lookup, scatter plan 3.10 / r8d 1.7). No UObjects: the same code runs in the automation tests (-nullrhi) and in the game.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	/** Asset path of the unit mesh (copied from ProjectChimera/Content/LookTest/Roster with the same path, EXECUTION 3 C20). */
	constexpr const TCHAR* UnitMeshPath = TEXT("/Game/LookTest/Roster/crucible_mortar/crucible_mortar/StaticMeshes/crucible_mortar");
	/** Uniform scale of alpha/siege_engine in SimTrial/unit_meshes.json (size 361 x 319 x 311 cm; front +X, feet on z = 0). */
	constexpr double UnitScale = 3.612544;
	/** Half the distance (m) between the height samples that give a unit its slope: inside a 3.6 m footprint. */
	constexpr float UnitProbeM = 1.0f;
	/** Largest spacing (m) of the layout grid; fewer than ~330 units spread over the map at this spacing, more units pack closer. */
	constexpr float UnitSpacingMaxM = 10.0f;
	/** Units stay this far (m) from the map edge. */
	constexpr float UnitEdgeMarginM = 16.0f;

	/** One unit's place on the map: terrain-space metres and the yaw (degrees) about the surface normal. */
	struct FUnitSlot
	{
		float X = 0.0f;
		float Y = 0.0f;
		float YawDeg = 0.0f;
	};

	/**
	 * Deterministic layout of Count units: a near-square grid centred on the origin (columns = ceil(sqrt(Count))), spacing =
	 * min(UnitSpacingMaxM, 0.95 * usable side / columns), each unit jittered by up to 0.3 spacing and given a yaw from a fixed integer hash of its index.
	 * Independent of everything but (Count, HalfExtentM).
	 */
	void BuildUnitLayout(int32 Count, int32 HalfExtentM, TArray<FUnitSlot>& Out);

	/**
	 * The unit's transform in Unreal cm: location (X, Y, SampleSurface(X, Y)) * 100, rotation = the yaw about +Z followed by the tilt that carries +Z onto the
	 * surface normal (central differences of SampleSurface at +-UnitProbeM), uniform scale UnitScale. Pure function of the heightfield and the slot.
	 */
	FTransform UnitTransform(const FTerrainHeightfield& HF, const FUnitSlot& Slot);

	/**
	 * True when UnitTransform(HF, Slot) can change after a change of the vertices in VertexRect (half-open): the surface samples it reads
	 * (x +- UnitProbeM, y +- UnitProbeM) read the vertex cells whose corners touch the rect. Conservative by at most a cell, never optimistic: the tests
	 * prove that updating only these units equals a full recompute.
	 */
	bool UnitDependsOnVertexRect(const FUnitSlot& Slot, int32 HalfExtentM, const FTerrainRect& VertexRect);

	/** FNV-1a 64 over the float32 bits (little-endian) of location, rotation (x y z w) of every transform, in order. */
	uint64 UnitsFnv(TConstArrayView<FTransform> Transforms);
}
