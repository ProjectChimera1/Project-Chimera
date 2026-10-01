// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/ChimeraUi.h"
#include "ChimeraHud.h"
#include "Brushes/SlateDynamicImageBrush.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Modules/ModuleManager.h"

namespace ChimeraUi
{
	/** SHA-256 hex of a byte range. The engine's FPlatformMisc::GetSHA256Signature has no Windows implementation (it asserts), so this is local. */
	static FString Sha256Hex(const uint8* Data, uint64 Size)
	{
		static const uint32 K[64] = {
			0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
			0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
			0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
			0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2 };
		uint32 H[8] = { 0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19 };
		TArray<uint8> Msg;
		Msg.Append(Data, (int32)Size);
		Msg.Add(0x80);
		while (Msg.Num() % 64 != 56) { Msg.Add(0); }
		const uint64 Bits = Size * 8;
		for (int32 i = 7; i >= 0; --i) { Msg.Add((uint8)(Bits >> (8 * i))); }
		auto Rotr = [](uint32 X, uint32 N) { return (X >> N) | (X << (32 - N)); };
		for (int32 Off = 0; Off < Msg.Num(); Off += 64)
		{
			uint32 W[64];
			for (int32 i = 0; i < 16; ++i)
			{
				W[i] = ((uint32)Msg[Off + 4 * i] << 24) | ((uint32)Msg[Off + 4 * i + 1] << 16) | ((uint32)Msg[Off + 4 * i + 2] << 8) | (uint32)Msg[Off + 4 * i + 3];
			}
			for (int32 i = 16; i < 64; ++i)
			{
				const uint32 S0 = Rotr(W[i - 15], 7) ^ Rotr(W[i - 15], 18) ^ (W[i - 15] >> 3);
				const uint32 S1 = Rotr(W[i - 2], 17) ^ Rotr(W[i - 2], 19) ^ (W[i - 2] >> 10);
				W[i] = W[i - 16] + S0 + W[i - 7] + S1;
			}
			uint32 a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
			for (int32 i = 0; i < 64; ++i)
			{
				const uint32 T1 = h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + W[i];
				const uint32 T2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
				h = g; g = f; f = e; e = d + T1; d = c; c = b; b = a; a = T1 + T2;
			}
			H[0] += a; H[1] += b; H[2] += c; H[3] += d; H[4] += e; H[5] += f; H[6] += g; H[7] += h;
		}
		FString Out;
		for (int32 i = 0; i < 8; ++i) { Out += FString::Printf(TEXT("%08x"), H[i]); }
		return Out;
	}

	FLinearColor Hex(uint32 Rgb, float Alpha)
	{
		const FColor C((Rgb >> 16) & 0xFF, (Rgb >> 8) & 0xFF, Rgb & 0xFF, 255);
		FLinearColor L = FLinearColor::FromSRGBColor(C);
		L.A = Alpha;
		return L;
	}

	bool ParseHex(const FString& Text, uint32& OutRgb)
	{
		FString S = Text;
		S.RemoveFromStart(TEXT("#"));
		if (S.Len() != 6)
		{
			return false;
		}
		for (TCHAR Ch : S)
		{
			if (!FChar::IsHexDigit(Ch))
			{
				return false;
			}
		}
		OutRgb = FParse::HexNumber(*S);
		return true;
	}

	bool LoadFileBytes(const FString& Path, TArray<uint8>& OutBytes)
	{
		if (!FFileHelper::LoadFileToArray(OutBytes, *Path))
		{
			UE_LOG(LogChimeraHud, Error, TEXT("cannot read %s"), *Path);
			return false;
		}
		UE_LOG(LogChimeraHud, Display, TEXT("loaded %s %s"), *Path, *Sha256Hex(OutBytes.GetData(), (uint64)OutBytes.Num()));
		return true;
	}

	TSharedPtr<FSlateDynamicImageBrush> LoadPngBrush(const FString& Path, const FName& BrushName)
	{
		TArray<uint8> Bytes;
		if (!LoadFileBytes(Path, Bytes))
		{
			return nullptr;
		}
		IImageWrapperModule& Module = FModuleManager::LoadModuleChecked<IImageWrapperModule>(TEXT("ImageWrapper"));
		TSharedPtr<IImageWrapper> Wrapper = Module.CreateImageWrapper(EImageFormat::PNG);
		TArray<uint8> Raw;
		if (!Wrapper.IsValid() || !Wrapper->SetCompressed(Bytes.GetData(), Bytes.Num()) || !Wrapper->GetRaw(ERGBFormat::BGRA, 8, Raw))
		{
			UE_LOG(LogChimeraHud, Error, TEXT("cannot decode PNG %s"), *Path);
			return nullptr;
		}
		const FVector2f Size((float)Wrapper->GetWidth(), (float)Wrapper->GetHeight());
		UE_LOG(LogChimeraHud, Display, TEXT("decoded %s %dx%d"), *Path, Wrapper->GetWidth(), Wrapper->GetHeight());
		return FSlateDynamicImageBrush::CreateWithImageData(BrushName, Size, Raw);
	}
}
