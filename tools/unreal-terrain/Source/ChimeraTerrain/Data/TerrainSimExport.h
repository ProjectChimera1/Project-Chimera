// Chimera terrain trial, plan C 3.2 and 3.9: how an edited heightfield maps onto the sim's ElevationGrid (pure C++, no UObjects).
// The sim side (Godot-free C#, ElevationGrid.cs, HeightmapCellMapping.cs, FixedPoint.cs) is the authority; this mirrors it bit for bit.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	namespace TerrainSimExport
	{
		/** Sim ElevationGrid is 256 x 256 cells of 1 m, origin (-128, -128) (ScenarioLoadPhase.cs:216-285). */
		constexpr int32 SimGridCells = 256;
		constexpr int32 SimGridHalfM = 128;

		/** Fixed.FromFloat: (int)(h * 65536.0f), truncation toward zero (FixedPoint.cs:27). Exact: x * 2^16 only changes the exponent. */
		int32 FixedRawFromFloat(float H);

		/** HeightmapCellMapping.CellToTexel, integer-only: ((2*cell + 1) * region) / (2 * count), truncating, clamped to [0, region-1]. */
		int32 CellToTexel(int32 CellIndex, int32 CellCount, int32 RegionSize);

		/** B = E - 128: the vertex offset between the sim's 256-wide window and the heightfield (trial E = 160 -> B = 32). Needs E >= 128. */
		int32 VertexOffset(const FTerrainHeightfield& HF);

		/** Heightfield vertex for sim cell index C on one axis: CellToTexel(C, 256, 256) + B. */
		int32 CellToVertex(const FTerrainHeightfield& HF, int32 CellIndex);

		/** Raw Fixed heights of the sim grid, row-major [row * 256 + col] (col = X, row = Z). */
		void BuildSimGrid(const FTerrainHeightfield& HF, TArray<int32>& OutRaw);

		/** FNV-1a 32 over the little-endian 4 bytes of each Raw, row-major. */
		uint32 SimGridFnv(const TArray<int32>& Raw);

		/**
		 * ElevationGrid.Sample on raw Fixed world coordinates (16.16): col = ((x - minX) / cell).ToInt() with Fixed division
		 * ((x << 16) / cell, truncating) then arithmetic shift, clamped to the grid; WorldMin = -128, cell = 1.
		 */
		int32 SampleSimGridRaw(const TArray<int32>& Raw, int32 WorldXRaw, int32 WorldZRaw);

		/** The 16 probe points (sim-world X and Z as raw Fixed) written to terrain.json and re-evaluated by the C# elevhash tool. */
		void GetProbePoints(TArray<FIntPoint>& Out);

		/** The single axis mapping: sim (x, z, h) metres to Unreal cm (100x, 100z, 100h). Must match check (a)'s host mapping. */
		FVector SimToUe(double SimX, double SimZ, double H);
		/** Recorded as terrain.json `axes`. */
		const TCHAR* AxesString();
	}
}
