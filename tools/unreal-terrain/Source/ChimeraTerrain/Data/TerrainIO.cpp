// Project Chimera terrain trial (plan C). Original Chimera code.

#include "Data/TerrainIO.h"
#include "Data/TerrainSimExport.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Policies/PrettyJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace ChimeraTerrain
{
	const TCHAR* const TerrainIO::JsonFileName = TEXT("terrain.json");
	const TCHAR* const TerrainIO::HeightFileName = TEXT("height.r32");
	const TCHAR* const TerrainIO::SplatFileName = TEXT("splat.rgba8");

	namespace
	{
		const TCHAR* const TerrainIOLayerNames[SplatLayerCount] = {TEXT("Grass"), TEXT("Dirt"), TEXT("Rock"), TEXT("Snow")};
		const TCHAR* const TerrainIOLayerChannels[SplatLayerCount] = {TEXT("R"), TEXT("G"), TEXT("B"), TEXT("A")};

		void TerrainIOHeightBytes(const FTerrainHeightfield& HF, TArray<uint8>& Out)
		{
			// Little-endian float32 row-major; written byte by byte so the file layout does not depend on the host.
			Out.SetNumUninitialized(HF.Heights.Num() * 4);
			int32 I = 0;
			for (const float H : HF.Heights)
			{
				uint32 Bits;
				FMemory::Memcpy(&Bits, &H, sizeof(Bits));
				Out[I++] = static_cast<uint8>(Bits & 0xFFu);
				Out[I++] = static_cast<uint8>((Bits >> 8) & 0xFFu);
				Out[I++] = static_cast<uint8>((Bits >> 16) & 0xFFu);
				Out[I++] = static_cast<uint8>((Bits >> 24) & 0xFFu);
			}
		}

		bool TerrainIOParseHash(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, uint32& Out)
		{
			FString S;
			if (!Obj->TryGetStringField(Field, S) || !S.StartsWith(TEXT("0x")) || S.Len() != 10)
			{
				return false;
			}
			Out = static_cast<uint32>(FCString::Strtoui64(*S.Mid(2), nullptr, 16));
			return true;
		}
	}

	FString TerrainIO::HashToString(uint32 Hash)
	{
		return FString::Printf(TEXT("0x%08x"), Hash);
	}

	FString TerrainIO::BuildJson(const FTerrainHeightfield& HF)
	{
		// The sim window needs E >= 128; a smaller test field has no sim grid (hash 0, no probes).
		const bool bHasSimGrid = HF.HalfExtentM() >= TerrainSimExport::SimGridHalfM;
		TArray<int32> SimRaw;
		if (bHasSimGrid)
		{
			TerrainSimExport::BuildSimGrid(HF, SimRaw);
		}
		const FString HeightFnv = HashToString(HF.HeightFnv());
		const FString SplatFnv = HashToString(HF.SplatFnv());
		const FString SimFnv = HashToString(bHasSimGrid ? TerrainSimExport::SimGridFnv(SimRaw) : 0u);

		FString Out;
		TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> W = TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&Out);
		const double E = HF.HalfExtentM();
		W->WriteObjectStart();
		W->WriteValue(TEXT("format"), TEXT("chimera-terrain"));
		W->WriteValue(TEXT("version"), 1);
		W->WriteValue(TEXT("half_extent_m"), HF.HalfExtentM());
		W->WriteValue(TEXT("width"), HF.Width());
		W->WriteValue(TEXT("height"), HF.Width());
		W->WriteValue(TEXT("spacing_m"), 1.0);
		W->WriteArrayStart(TEXT("origin_m"));
		W->WriteValue(-E);
		W->WriteValue(-E);
		W->WriteArrayEnd();
		W->WriteValue(TEXT("height_limit_m"), static_cast<double>(MaxHeightM));
		W->WriteValue(TEXT("height_format"), TEXT("float32-le-row-major"));
		W->WriteObjectStart(TEXT("splat"));
		W->WriteValue(TEXT("width"), HF.SplatSize());
		W->WriteValue(TEXT("height"), HF.SplatSize());
		W->WriteValue(TEXT("texel_m"), 1.0 / SplatTexelsPerMeter);
		W->WriteValue(TEXT("format"), TEXT("rgba8-row-major"));
		W->WriteObjectEnd();
		W->WriteArrayStart(TEXT("layers"));
		for (int32 L = 0; L < SplatLayerCount; ++L)
		{
			W->WriteObjectStart();
			W->WriteValue(TEXT("id"), L);
			W->WriteValue(TEXT("name"), TerrainIOLayerNames[L]);
			W->WriteValue(TEXT("channel"), TerrainIOLayerChannels[L]);
			W->WriteObjectEnd();
		}
		W->WriteArrayEnd();
		W->WriteValue(TEXT("axes"), TerrainSimExport::AxesString());
		W->WriteValue(TEXT("height_fnv"), *HeightFnv);
		W->WriteValue(TEXT("splat_fnv"), *SplatFnv);
		W->WriteValue(TEXT("sim_grid_fnv"), *SimFnv);
		W->WriteObjectStart(TEXT("sim_grid"));
		W->WriteValue(TEXT("cells"), TerrainSimExport::SimGridCells);
		W->WriteValue(TEXT("origin_m"), static_cast<double>(-TerrainSimExport::SimGridHalfM));
		W->WriteValue(TEXT("cell_m"), 1.0);
		W->WriteValue(TEXT("vertex_offset"), bHasSimGrid ? TerrainSimExport::VertexOffset(HF) : 0);
		W->WriteValue(TEXT("fnv"), TEXT("fnv1a32 over the little-endian 4 bytes of each raw Fixed, row-major"));
		W->WriteObjectEnd();
		W->WriteArrayStart(TEXT("probes"));
		TArray<FIntPoint> Probes;
		if (bHasSimGrid)
		{
			TerrainSimExport::GetProbePoints(Probes);
		}
		for (const FIntPoint& P : Probes)
		{
			const int32 Value = TerrainSimExport::SampleSimGridRaw(SimRaw, P.X, P.Y);
			const FVector Ue = TerrainSimExport::SimToUe(P.X / 65536.0, P.Y / 65536.0, Value / 65536.0);
			W->WriteObjectStart();
			W->WriteValue(TEXT("sim_x_raw"), P.X);
			W->WriteValue(TEXT("sim_z_raw"), P.Y);
			W->WriteValue(TEXT("value_raw"), Value);
			W->WriteArrayStart(TEXT("ue_cm"));
			W->WriteValue(Ue.X);
			W->WriteValue(Ue.Y);
			W->WriteValue(Ue.Z);
			W->WriteArrayEnd();
			W->WriteObjectEnd();
		}
		W->WriteArrayEnd();
		W->WriteObjectEnd();
		W->Close();
		return Out;
	}

	bool TerrainIO::Save(const FTerrainHeightfield& HF, const FString& Dir, FString& OutError)
	{
		OutError.Reset();
		if (!HF.IsInitialized())
		{
			OutError = TEXT("heightfield not initialised");
			return false;
		}
		IFileManager& FM = IFileManager::Get();
		if (!FM.MakeDirectory(*Dir, true))
		{
			OutError = FString::Printf(TEXT("cannot create %s"), *Dir);
			return false;
		}
		TArray<uint8> HeightBytes;
		TerrainIOHeightBytes(HF, HeightBytes);
		if (!FFileHelper::SaveArrayToFile(HeightBytes, *FPaths::Combine(Dir, HeightFileName)))
		{
			OutError = FString::Printf(TEXT("cannot write %s"), HeightFileName);
			return false;
		}
		if (!FFileHelper::SaveArrayToFile(HF.Splat, *FPaths::Combine(Dir, SplatFileName)))
		{
			OutError = FString::Printf(TEXT("cannot write %s"), SplatFileName);
			return false;
		}
		if (!FFileHelper::SaveStringToFile(BuildJson(HF), *FPaths::Combine(Dir, JsonFileName), FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
		{
			OutError = FString::Printf(TEXT("cannot write %s"), JsonFileName);
			return false;
		}
		return true;
	}

	bool TerrainIO::Load(FTerrainHeightfield& HF, int32 InChunkQuads, const FString& Dir, FString& OutError)
	{
		OutError.Reset();
		FString JsonText;
		if (!FFileHelper::LoadFileToString(JsonText, *FPaths::Combine(Dir, JsonFileName)))
		{
			OutError = FString::Printf(TEXT("cannot read %s"), JsonFileName);
			return false;
		}
		TSharedPtr<FJsonObject> Obj;
		if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(JsonText), Obj) || !Obj.IsValid())
		{
			OutError = TEXT("terrain.json is not valid JSON");
			return false;
		}
		FString Format;
		int32 Version = 0;
		int32 Half = 0;
		int32 Width = 0;
		int32 Height = 0;
		if (!Obj->TryGetStringField(TEXT("format"), Format) || Format != TEXT("chimera-terrain") || !Obj->TryGetNumberField(TEXT("version"), Version) || Version != 1)
		{
			OutError = TEXT("terrain.json: unknown format or version");
			return false;
		}
		if (!Obj->TryGetNumberField(TEXT("half_extent_m"), Half) || !Obj->TryGetNumberField(TEXT("width"), Width) || !Obj->TryGetNumberField(TEXT("height"), Height)
			|| Half < 1 || Half > 4096 || Width != 2 * Half + 1 || Height != Width)
		{
			OutError = TEXT("terrain.json: bad half_extent_m, width or height");
			return false;
		}
		FString Axes;
		if (!Obj->TryGetStringField(TEXT("axes"), Axes) || Axes != TerrainSimExport::AxesString())
		{
			OutError = FString::Printf(TEXT("terrain.json: axes '%s' differs from '%s'"), *Axes, TerrainSimExport::AxesString());
			return false;
		}
		uint32 WantHeightFnv = 0;
		uint32 WantSplatFnv = 0;
		uint32 WantSimFnv = 0;
		if (!TerrainIOParseHash(Obj, TEXT("height_fnv"), WantHeightFnv) || !TerrainIOParseHash(Obj, TEXT("splat_fnv"), WantSplatFnv) || !TerrainIOParseHash(Obj, TEXT("sim_grid_fnv"), WantSimFnv))
		{
			OutError = TEXT("terrain.json: missing or malformed fnv field");
			return false;
		}

		TArray<uint8> HeightBytes;
		TArray<uint8> SplatBytes;
		if (!FFileHelper::LoadFileToArray(HeightBytes, *FPaths::Combine(Dir, HeightFileName)))
		{
			OutError = FString::Printf(TEXT("cannot read %s"), HeightFileName);
			return false;
		}
		if (!FFileHelper::LoadFileToArray(SplatBytes, *FPaths::Combine(Dir, SplatFileName)))
		{
			OutError = FString::Printf(TEXT("cannot read %s"), SplatFileName);
			return false;
		}
		// Build into a local field so a rejected file leaves the caller's heightfield untouched.
		FTerrainHeightfield Loaded;
		Loaded.Init(Half, InChunkQuads);
		if (HeightBytes.Num() != Loaded.Heights.Num() * 4)
		{
			OutError = FString::Printf(TEXT("%s has %d bytes, expected %d"), HeightFileName, HeightBytes.Num(), Loaded.Heights.Num() * 4);
			return false;
		}
		if (SplatBytes.Num() != Loaded.Splat.Num())
		{
			OutError = FString::Printf(TEXT("%s has %d bytes, expected %d"), SplatFileName, SplatBytes.Num(), Loaded.Splat.Num());
			return false;
		}
		for (int32 I = 0; I < Loaded.Heights.Num(); ++I)
		{
			const uint32 Bits = static_cast<uint32>(HeightBytes[4 * I]) | (static_cast<uint32>(HeightBytes[4 * I + 1]) << 8)
				| (static_cast<uint32>(HeightBytes[4 * I + 2]) << 16) | (static_cast<uint32>(HeightBytes[4 * I + 3]) << 24);
			float H;
			FMemory::Memcpy(&H, &Bits, sizeof(H));
			if (!FMath::IsFinite(H) || FMath::Abs(H) > MaxHeightM)
			{
				OutError = FString::Printf(TEXT("%s: vertex %d holds a non-finite or out-of-range height"), HeightFileName, I);
				return false;
			}
			Loaded.Heights[I] = H;
		}
		Loaded.Splat = MoveTemp(SplatBytes);
		const int32 Texels = Loaded.SplatSize() * Loaded.SplatSize();
		for (int32 T = 0; T < Texels; ++T)
		{
			const uint8* P = &Loaded.Splat[T * SplatLayerCount];
			if (static_cast<int32>(P[0]) + P[1] + P[2] + P[3] != 255)
			{
				OutError = FString::Printf(TEXT("%s: texel %d does not sum to 255"), SplatFileName, T);
				return false;
			}
		}
		if (Loaded.HeightFnv() != WantHeightFnv)
		{
			OutError = FString::Printf(TEXT("height_fnv mismatch: file %s, json %s"), *HashToString(Loaded.HeightFnv()), *HashToString(WantHeightFnv));
			return false;
		}
		if (Loaded.SplatFnv() != WantSplatFnv)
		{
			OutError = FString::Printf(TEXT("splat_fnv mismatch: file %s, json %s"), *HashToString(Loaded.SplatFnv()), *HashToString(WantSplatFnv));
			return false;
		}
		if (Loaded.HalfExtentM() >= TerrainSimExport::SimGridHalfM)
		{
			TArray<int32> SimRaw;
			TerrainSimExport::BuildSimGrid(Loaded, SimRaw);
			if (TerrainSimExport::SimGridFnv(SimRaw) != WantSimFnv)
			{
				OutError = TEXT("sim_grid_fnv mismatch");
				return false;
			}
		}
		HF = MoveTemp(Loaded);
		return true;
	}
}
