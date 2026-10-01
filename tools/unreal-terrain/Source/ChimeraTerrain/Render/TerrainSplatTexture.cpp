// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Render/TerrainSplatTexture.h"

#include "ChimeraTerrain.h"
#include "Engine/Texture2D.h"
#include "Misc/App.h"
#include "RHITypes.h"

namespace ChimeraTerrain
{
	bool FTerrainSplatTexture::Create(const FTerrainHeightfield& HF)
	{
		check(IsInGameThread());
		Size = HF.SplatSize();
		check(HF.Splat.Num() == Size * Size * SplatLayerCount);
		UTexture2D* Tex = UTexture2D::CreateTransient(Size, Size, PF_R8G8B8A8, FName(TEXT("T_ChimeraTerrainSplat")),
			TConstArrayView64<uint8>(HF.Splat.GetData(), HF.Splat.Num()));
		if (!Tex)
		{
			UE_LOG(LogChimeraTerrain, Error, TEXT("splat: CreateTransient returned null"));
			return false;
		}
		Tex->SRGB = false;
		Tex->Filter = TF_Bilinear;
		Tex->AddressX = TA_Clamp;
		Tex->AddressY = TA_Clamp;
		Tex->UpdateResource();
		if (!FApp::CanEverRender())
		{
			// -nullrhi (automation): no texture resource can exist (Texture2D.cpp:656). Keep the object, upload nothing.
			UE_LOG(LogChimeraTerrain, Display, TEXT("splat texture %dx%d created without a resource (no rendering in this process)"), Size, Size);
			Texture.Reset(Tex);
			bNoRender = true;
			return true;
		}
		if (!Tex->GetResource())
		{
			UE_LOG(LogChimeraTerrain, Error, TEXT("splat: texture has no resource after UpdateResource"));
			return false;
		}
		Texture.Reset(Tex);
		UE_LOG(LogChimeraTerrain, Display, TEXT("splat texture %dx%d PF_R8G8B8A8 linear bilinear clamp"), Size, Size);
		return true;
	}

	void FTerrainSplatTexture::UpdateRect(const FTerrainHeightfield& HF, const FTerrainRect& InRect)
	{
		UTexture2D* Tex = Texture.Get();
		if (!Tex || bNoRender)
		{
			return;
		}
		const FTerrainRect R = InRect.Intersect(FTerrainRect(0, 0, Size, Size));
		if (R.IsEmpty())
		{
			return;
		}
		const int32 W = R.Width();
		const int32 H = R.Height();
		const int32 Pitch = W * SplatLayerCount;
		uint8* Data = new uint8[static_cast<SIZE_T>(Pitch) * H];
		for (int32 Y = 0; Y < H; ++Y)
		{
			FMemory::Memcpy(Data + static_cast<SIZE_T>(Y) * Pitch, HF.SplatTexel(R.X0, R.Y0 + Y), Pitch);
		}
		FUpdateTextureRegion2D* Region = new FUpdateTextureRegion2D(R.X0, R.Y0, 0, 0, W, H);
		Counters->Submits.fetch_add(1);
		Counters->BytesSubmitted.fetch_add(static_cast<int64>(Pitch) * H);
		TSharedRef<FTerrainSplatCounters, ESPMode::ThreadSafe> C = Counters;
		Tex->UpdateTextureRegions(0, 1, Region, Pitch, SplatLayerCount, Data, [C](uint8* SrcData, const FUpdateTextureRegion2D* Regions)
		{
			delete[] SrcData;
			delete Regions;
			C->Cleanups.fetch_add(1);
		});
	}

	void FTerrainSplatTexture::UpdateAll(const FTerrainHeightfield& HF)
	{
		UpdateRect(HF, FTerrainRect(0, 0, Size, Size));
	}
}
