// Chimera terrain trial, plan C 3.6: the analytic ray pick over the CPU height array (pure C++, no UObjects, no physics).
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	struct FTerrainHit
	{
		bool bHit = false;
		/** Distance along the (normalised) ray direction, metres. */
		double Distance = 0.0;
		/** Hit point in terrain-space metres. */
		FVector Position = FVector::ZeroVector;
		/** Quad cell that was hit. */
		int32 CellX = 0;
		int32 CellY = 0;
	};

	namespace TerrainPick
	{
		/**
		 * Cast a ray in terrain-space metres against the triangulated surface: 2D DDA over the cells the ray crosses (nearest cell first),
		 * then the cell's two triangles (BL,TL,TR) and (BL,TR,BR), double precision. Zero lag, no physics cost. The first cell with a
		 * hit holds the nearest hit. Misses when the ray never enters the map footprint or the +-MaxHeightM slab, or the surface is
		 * not reached within MaxDistance. This shares the source array with the sim, NOT its semantics: the sim samples nearest-cell
		 * (ElevationGrid.Sample), so gameplay height queries must never use the pick.
		 */
		bool RayCast(const FTerrainHeightfield& HF, const FVector& Origin, const FVector& Direction, double MaxDistance, FTerrainHit& OutHit);
	}
}
