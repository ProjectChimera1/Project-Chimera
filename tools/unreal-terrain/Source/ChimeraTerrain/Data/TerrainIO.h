// Chimera terrain trial, plan C 3.2: terrain files. terrain.json (dims, origin, spacing, layer ids, axes, fnvs, sim_grid_fnv, probes)
// + height.r32 (little-endian float32, row-major) + splat.rgba8 (RGBA8, row-major). Pure C++, no UObjects.
#pragma once

#include "CoreMinimal.h"
#include "Data/TerrainHeightfield.h"

namespace ChimeraTerrain
{
	namespace TerrainIO
	{
		extern const TCHAR* const JsonFileName;   // terrain.json
		extern const TCHAR* const HeightFileName; // height.r32
		extern const TCHAR* const SplatFileName;  // splat.rgba8

		/** "0x%08x". */
		FString HashToString(uint32 Hash);

		/** The terrain.json text for HF (fnvs, sim_grid_fnv and probes are derived from the arrays, never stored separately). */
		FString BuildJson(const FTerrainHeightfield& HF);

		/** Write the three files into Dir (created if needed). Same data in, same bytes out. */
		bool Save(const FTerrainHeightfield& HF, const FString& Dir, FString& OutError);

		/**
		 * Read the three files from Dir into HF (re-initialised with the stored half extent and InChunkQuads). Validates sizes, finite
		 * in-range heights, splat texels summing to 255 and that height_fnv, splat_fnv and sim_grid_fnv match the data; any failure
		 * returns false with a reason in OutError and leaves HF untouched. The axes field must equal
		 * TerrainSimExport::AxesString().
		 */
		bool Load(FTerrainHeightfield& HF, int32 InChunkQuads, const FString& Dir, FString& OutError);
	}
}
