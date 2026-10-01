// Project Chimera terrain trial (plan C). Original Chimera code.
// Plan C 3.5: the RGBA8 splat map as a transient texture, updated by dirty rects through UpdateTextureRegions.
#pragma once

#include "CoreMinimal.h"
#include "UObject/StrongObjectPtr.h"
#include "Data/TerrainHeightfield.h"

class UTexture2D;

namespace ChimeraTerrain
{
	/** Counters shared with the render-thread cleanup callback (plan C 3.5: submits == cleanups > 0 after S1). */
	struct FTerrainSplatCounters
	{
		std::atomic<int64> Submits{0};
		std::atomic<int64> Cleanups{0};
		std::atomic<int64> BytesSubmitted{0};
	};

	class FTerrainSplatTexture
	{
	public:
		/**
		 * CreateTransient(S, S, PF_R8G8B8A8, Name, splat bytes) (Texture2D.h:342), then SRGB=false, TF_Bilinear, TA_Clamp and
		 * UpdateResource() again (CreateTransient only calls it when data is passed and these properties change after it;
		 * Texture2D.cpp:1349-1353). Fails (returns false) when no resource exists: UpdateTextureRegions would silently drop updates
		 * and never call the cleanup (Texture2D.cpp:1438-1452).
		 */
		bool Create(const FTerrainHeightfield& HF);

		/** Upload the texel rect (half-open, splat texel indices) from HF.Splat. Heap copy freed in the cleanup callback. */
		void UpdateRect(const FTerrainHeightfield& HF, const FTerrainRect& TexelRect);

		/** Upload every texel (after a load). */
		void UpdateAll(const FTerrainHeightfield& HF);

		UTexture2D* GetTexture() const { return Texture.Get(); }
		int64 Submits() const { return Counters->Submits.load(); }
		int64 Cleanups() const { return Counters->Cleanups.load(); }
		bool HasPendingWork() const { return Counters->Submits.load() != Counters->Cleanups.load(); }

	private:
		TStrongObjectPtr<UTexture2D> Texture;
		TSharedRef<FTerrainSplatCounters, ESPMode::ThreadSafe> Counters = MakeShared<FTerrainSplatCounters, ESPMode::ThreadSafe>();
		int32 Size = 0;
		/** True under -nullrhi: the texture has no resource and uploads are skipped. */
		bool bNoRender = false;
	};
}
