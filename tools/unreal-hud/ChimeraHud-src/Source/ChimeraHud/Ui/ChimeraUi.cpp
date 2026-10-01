// Copyright Epic Games, Inc. All Rights Reserved.

#include "Ui/ChimeraUi.h"
#include "Ui/Widgets/SChimeraText.h"
#include "ChimeraHud.h"
#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateDynamicImageBrush.h"
#include "Brushes/SlateImageBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Fonts/FontCache.h"
#include "Fonts/FontMeasure.h"
#include "Framework/Application/SlateApplication.h"
#include "HAL/IConsoleManager.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Rendering/SlateRenderer.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Text/STextBlock.h"

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

	TSharedPtr<FSlateBrush> LoadPngBrush(const FString& Path, const FName& BrushName)
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

	FString HudDataPath(const FString& Relative)
	{
		FString Dir = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
		FPaths::NormalizeDirectoryName(Dir);
		return Dir / TEXT("HudData") / Relative;
	}

	// ------------------------------------------------------------------ routes

	/** Compiled defaults of the T4a route test: the CHOSEN row of tools/unreal-hud/results/t4_routes.csv (png_best_on). */
	static constexpr EFontHinting DefaultHinting = EFontHinting::None;
	static constexpr bool bDefaultKern = true;
	static constexpr ETextRoute DefaultText = ETextRoute::Frac;
	static constexpr EIconRoute DefaultIcons = EIconRoute::Png;	// t4_routes.csv: png vector MAD 0.147 vs svg 2.746 over top.*

	static const TCHAR* HintingName(EFontHinting H)
	{
		switch (H)
		{
		case EFontHinting::None: return TEXT("None");
		case EFontHinting::Default: return TEXT("Default");
		case EFontHinting::AutoLight: return TEXT("AutoLight");
		case EFontHinting::Auto: return TEXT("Auto");
		case EFontHinting::Monochrome: return TEXT("Monochrome");
		default: return TEXT("?");
		}
	}

	const FRoute& Route()
	{
		static const FRoute Parsed = []()
		{
			FRoute R;
			R.Hinting = DefaultHinting;
			R.bKern = bDefaultKern;
			R.Text = DefaultText;
			R.Icons = DefaultIcons;
			const TCHAR* Cmd = FCommandLine::Get();
			FString V;
			if (FParse::Value(Cmd, TEXT("-HudHinting="), V, false))
			{
				if (V.Equals(TEXT("None"), ESearchCase::IgnoreCase)) { R.Hinting = EFontHinting::None; }
				else if (V.Equals(TEXT("Default"), ESearchCase::IgnoreCase)) { R.Hinting = EFontHinting::Default; }
				else if (V.Equals(TEXT("AutoLight"), ESearchCase::IgnoreCase)) { R.Hinting = EFontHinting::AutoLight; }
				else if (V.Equals(TEXT("Auto"), ESearchCase::IgnoreCase)) { R.Hinting = EFontHinting::Auto; }
				else { UE_LOG(LogChimeraHud, Error, TEXT("bad -HudHinting=%s (None|Default|AutoLight|Auto)"), *V); }
			}
			if (FParse::Value(Cmd, TEXT("-HudKern="), V, false))
			{
				if (V.Equals(TEXT("on"), ESearchCase::IgnoreCase)) { R.bKern = true; }
				else if (V.Equals(TEXT("off"), ESearchCase::IgnoreCase)) { R.bKern = false; }
				else { UE_LOG(LogChimeraHud, Error, TEXT("bad -HudKern=%s (on|off)"), *V); }
			}
			if (FParse::Value(Cmd, TEXT("-HudText="), V, false))
			{
				if (V.Equals(TEXT("frac"), ESearchCase::IgnoreCase)) { R.Text = ETextRoute::Frac; }
				else if (V.Equals(TEXT("block"), ESearchCase::IgnoreCase)) { R.Text = ETextRoute::Block; }
				else { UE_LOG(LogChimeraHud, Error, TEXT("bad -HudText=%s (frac|block)"), *V); }
			}
			if (FParse::Value(Cmd, TEXT("-HudIcons="), V, false))
			{
				if (V.Equals(TEXT("svg"), ESearchCase::IgnoreCase)) { R.Icons = EIconRoute::Svg; }
				else if (V.Equals(TEXT("png"), ESearchCase::IgnoreCase)) { R.Icons = EIconRoute::Png; }
				else { UE_LOG(LogChimeraHud, Error, TEXT("bad -HudIcons=%s (svg|png)"), *V); }
			}
			R.HintingName = HintingName(R.Hinting);
			return R;
		}();
		return Parsed;
	}

	// ------------------------------------------------------------------ text styles and fonts

	const FTextStyle& Style(EChimeraText InStyle)
	{
		// r4 section 6 (CSS shorthand weight size/line-height family); line-height multipliers resolved to px as Blink does.
		static const FTextStyle Styles[(int32)EChimeraText::Count] = {
			{ EFamily::Mono, 600, 15.f, 15.f, Token::Text, 0.f },		// ResourceValue
			{ EFamily::Mono, 500, 11.f, 11.f, Token::TextMuted, 0.f },	// ResourceRate
			{ EFamily::Mono, 600, 18.f, 18.f, Token::Text, 0.f },		// Clock
			{ EFamily::Inter, 500, 12.f, 12.f, Token::Text, 0.f },		// Label12
			{ EFamily::Mono, 600, 13.f, 13.f, Token::TextMuted, 0.f },	// AlertCount
			{ EFamily::Inter, 500, 13.f, 16.9f, Token::Text, 0.f },		// Toast (1.3)
			{ EFamily::Mono, 600, 10.f, 14.f, Token::Text, 0.f },		// Keycap
			{ EFamily::Mono, 600, 11.f, 11.f, Token::GoldBright, 0.f },	// TabNumber
			{ EFamily::Mono, 600, 12.f, 12.f, Token::Text, 0.f },		// TabCount
			{ EFamily::Cinzel, 600, 20.f, 22.f, Token::Text, 0.f },		// UnitName (1.1)
			{ EFamily::Inter, 400, 13.f, 15.6f, Token::TextMuted, 0.f },	// UnitRole (1.2)
			{ EFamily::Mono, 500, 12.f, 12.f, Token::Text, 0.f },		// HpText
			{ EFamily::Mono, 500, 12.f, 15.6f, Token::Text, 0.f },		// StatLine (1.3)
			{ EFamily::Mono, 600, 9.f, 9.f, Token::TextMuted, 0.f },		// CostLabel
		};
		return Styles[FMath::Clamp((int32)InStyle, 0, (int32)EChimeraText::Count - 1)];
	}

	FFamilyMetrics Metrics(EFamily Family)
	{
		switch (Family)
		{
		case EFamily::Inter: return { 2048.f, 1984.f, 494.f };
		case EFamily::Cinzel: return { 1000.f, 976.f, 372.f };
		default: return { 1000.f, 1020.f, 300.f };	// JetBrains Mono
		}
	}

	float CssBaseline(const FTextStyle& InStyle)
	{
		const FFamilyMetrics M = Metrics(InStyle.Family);
		const float A = FMath::FloorToFloat(M.Ascender * InStyle.Px / M.UnitsPerEm + 0.5f);
		const float D = FMath::FloorToFloat(M.Descender * InStyle.Px / M.UnitsPerEm + 0.5f);
		const float HalfLeading = FMath::FloorToFloat((InStyle.LineHeight - (A + D)) * 0.5f);
		return HalfLeading + A + InStyle.NudgeY;
	}

	/** One composite font per family, typefaces "400".."700" from the static OFL TTFs, built once with the route's hinting. */
	static TSharedPtr<const FCompositeFont> Family(EFamily InFamily)
	{
		struct FFace { const TCHAR* Name; const TCHAR* File; };
		static const FFace InterFaces[] = { {TEXT("400"), TEXT("Inter-Regular.ttf")}, {TEXT("500"), TEXT("Inter-Medium.ttf")},
			{TEXT("600"), TEXT("Inter-SemiBold.ttf")}, {TEXT("700"), TEXT("Inter-Bold.ttf")} };
		static const FFace CinzelFaces[] = { {TEXT("500"), TEXT("Cinzel-Medium.ttf")}, {TEXT("600"), TEXT("Cinzel-SemiBold.ttf")},
			{TEXT("700"), TEXT("Cinzel-Bold.ttf")} };
		static const FFace MonoFaces[] = { {TEXT("400"), TEXT("JetBrainsMono-Regular.ttf")}, {TEXT("500"), TEXT("JetBrainsMono-Medium.ttf")},
			{TEXT("600"), TEXT("JetBrainsMono-SemiBold.ttf")} };

		auto Make = [](const TCHAR* Dir, TArrayView<const FFace> Faces) -> TSharedPtr<const FCompositeFont>
		{
			// Created once and deliberately never freed: the composite font is an FGCObject and must not be destroyed by static
			// teardown after the GC is gone; the Slate font cache keys on it for the whole process.
			TSharedRef<FStandaloneCompositeFont>* Holder = new TSharedRef<FStandaloneCompositeFont>(MakeShared<FStandaloneCompositeFont>());
			for (const FFace& F : Faces)
			{
				const FString Path = HudDataPath(FString(TEXT("Fonts")) / Dir / F.File);
				TArray<uint8> Bytes;
				LoadFileBytes(Path, Bytes);		// logs path + sha256 for the allow-list; FreeType reads the file itself (LazyLoad)
				(*Holder)->DefaultTypeface.AppendFont(FName(F.Name), Path, Route().Hinting, EFontLoadingPolicy::LazyLoad);
			}
			return TSharedPtr<const FCompositeFont>(*Holder);
		};
		static const TSharedPtr<const FCompositeFont> Inter = Make(TEXT("Inter"), InterFaces);
		static const TSharedPtr<const FCompositeFont> Cinzel = Make(TEXT("Cinzel"), CinzelFaces);
		static const TSharedPtr<const FCompositeFont> Mono = Make(TEXT("JetBrainsMono"), MonoFaces);
		return InFamily == EFamily::Inter ? Inter : InFamily == EFamily::Cinzel ? Cinzel : Mono;
	}

	FSlateFontInfo Font(EFamily InFamily, int32 Weight, float Px, bool bKern)
	{
		FSlateFontInfo Info(Family(InFamily), Px * 0.75f, FName(*FString::FromInt(Weight)));	// r5 1.2: Size = px x 72/96
		// r5 1.3/1.5: LetterSpacing 1 adds (int)(1 x Size / 1000) = 0 px and switches HarfBuzz 'kern' (GPOS) on; 0 leaves it off.
		Info.LetterSpacing = bKern ? 1 : 0;
		return Info;
	}

	FSlateFontInfo Font(EChimeraText InStyle)
	{
		const FTextStyle& S = Style(InStyle);
		return Font(S.Family, S.Weight, S.Px, Route().bKern);
	}

	TSharedRef<SWidget> Text(EChimeraText InStyle, const FString& InText, TOptional<uint32> InColor)
	{
		const FTextStyle& S = Style(InStyle);
		const FLinearColor Color = Hex(InColor.Get(S.Color));
		const FSlateFontInfo Info = Font(InStyle);
		const float Baseline = CssBaseline(S);
		if (Route().Text == ETextRoute::Frac)
		{
			return SNew(SChimeraText).Text(InText).Font(Info).Color(Color).LineHeight(S.LineHeight).BaselineY(Baseline);
		}
		// Block route (T4a test only): plain STextBlock, whole-pixel advances, placed so its baseline sits at the same CSS baseline.
		const TSharedRef<FSlateFontMeasure> Measure = FSlateApplication::Get().GetRenderer()->GetFontMeasureService();
		const float Ascent = (float)Measure->GetMaxCharacterHeight(Info) + (float)Measure->GetBaseline(Info);
		return SNew(SBox).HeightOverride(S.LineHeight).VAlign(VAlign_Top).Padding(FMargin(0.f, Baseline - Ascent, 0.f, 0.f))
			[
				SNew(STextBlock).Text(FText::FromString(InText)).Font(Info).ColorAndOpacity(FSlateColor(Color))
				.TextShapingMethod(ETextShapingMethod::FullShaping)
			];
	}

	// ------------------------------------------------------------------ brushes

	/** Process-lifetime brush cache (deliberately leaked: widgets hold raw FSlateBrush pointers and static teardown order is unsafe). */
	static TMap<FString, TSharedPtr<FSlateBrush>>& Brushes()
	{
		static TMap<FString, TSharedPtr<FSlateBrush>>* Map = new TMap<FString, TSharedPtr<FSlateBrush>>();
		return *Map;
	}

	const FSlateBrush* Solid(uint32 Rgb, float Alpha)
	{
		const FString Key = FString::Printf(TEXT("solid:%06x:%.4f"), Rgb, Alpha);
		TSharedPtr<FSlateBrush>& B = Brushes().FindOrAdd(Key);
		if (!B.IsValid())
		{
			B = MakeShared<FSlateColorBrush>(Hex(Rgb, Alpha));
		}
		return B.Get();
	}

	const FSlateBrush* KeycapBrush(uint32 Fill, const FVector4f& Radii)
	{
		const FString Key = FString::Printf(TEXT("round:%06x:%.2f:%.2f:%.2f:%.2f"), Fill, Radii.X, Radii.Y, Radii.Z, Radii.W);
		TSharedPtr<FSlateBrush>& B = Brushes().FindOrAdd(Key);
		if (!B.IsValid())
		{
			// Fill + radius form only: the fill edge is crisp (smoothstep spread .5, SlateShaderCommon.ush:100-118), outline width stays 0.
			B = MakeShared<FSlateRoundedBoxBrush>(Hex(Fill), FVector4(Radii.X, Radii.Y, Radii.Z, Radii.W));
		}
		return B.Get();
	}

	const FSlateBrush* IconBrush(const FString& Name, int32 SizePx)
	{
		const bool bPng = Route().Icons == EIconRoute::Png;
		const FString Key = FString::Printf(TEXT("icon:%s:%s:%d"), bPng ? TEXT("png") : TEXT("svg"), *Name, SizePx);
		TSharedPtr<FSlateBrush>& B = Brushes().FindOrAdd(Key);
		if (B.IsValid())
		{
			return B.Get();
		}
		if (bPng)
		{
			const FString Path = HudDataPath(FString::Printf(TEXT("Icons/%s_%d.png"), *Name, SizePx));
			B = LoadPngBrush(Path, FName(*FString::Printf(TEXT("ChimeraIcon_%s_%d"), *Name, SizePx)));
		}
		else
		{
			const FString Path = HudDataPath(FString::Printf(TEXT("Icons/%s.svg"), *Name));
			TArray<uint8> Bytes;
			if (LoadFileBytes(Path, Bytes))		// logs path + sha256; the vector cache rasterises the file itself (nanosvg)
			{
				B = MakeShared<FSlateVectorImageBrush>(Path, FVector2f((float)SizePx, (float)SizePx), FLinearColor::White);
			}
		}
		if (!B.IsValid())
		{
			UE_LOG(LogChimeraHud, Error, TEXT("icon %s %d px did not load"), *Name, SizePx);
			B = MakeShared<FSlateColorBrush>(FLinearColor::Transparent);
		}
		return B.Get();
	}

	// ------------------------------------------------------------------ T4a diagnostics

	/** Exact advance sum of a string in px, from HarfBuzz full shaping at FontScale 16 (1/16 px quantisation). */
	static float ShapedWidth16(const FString& Str, const FSlateFontInfo& Info)
	{
		const TSharedRef<FSlateFontCache> Cache = FSlateApplication::Get().GetRenderer()->GetFontCache();
		const FShapedGlyphSequenceRef Seq = Cache->ShapeUnidirectionalText(Str, Info, 16.f, TextBiDi::ETextDirection::LeftToRight, ETextShapingMethod::FullShaping);
		int32 Sum = 0;
		for (const FShapedGlyphEntry& G : Seq->GetGlyphsToRender())
		{
			Sum += G.XAdvance;
		}
		return (float)Sum / 16.f;
	}

	void LogRouteAndKernCheck()
	{
		static bool bDone = false;
		if (bDone)
		{
			return;
		}
		bDone = true;
		const FRoute& R = Route();
		int32 Legacy = -1;
		if (IConsoleVariable* Var = IConsoleManager::Get().FindConsoleVariable(TEXT("Slate.EnableLegacyFontHinting")))
		{
			Legacy = Var->GetInt();
		}
		int32 Shaping = -1;
		if (IConsoleVariable* Var = IConsoleManager::Get().FindConsoleVariable(TEXT("Slate.DefaultTextShapingMethod")))
		{
			Shaping = Var->GetInt();
		}
		UE_LOG(LogChimeraHud, Display, TEXT("text route hinting=%s kern=%s text=%s icons=%s legacyHinting=%d shapingCvar=%d"),
			*R.HintingName, R.bKern ? TEXT("on") : TEXT("off"), R.Text == ETextRoute::Frac ? TEXT("frac") : TEXT("block"),
			R.Icons == EIconRoute::Svg ? TEXT("svg") : TEXT("png"), Legacy, Shaping);

		// Kern check (plan B T4a): GPOS kerning reaches the shaped advances only with LetterSpacing != 0 (SlateTextShaper.cpp:723).
		const FString Probe = TEXT("Covenant Acolyte");
		const float W = ShapedWidth16(Probe, Font(EFamily::Cinzel, 600, 20.f, true));
		const float W0 = ShapedWidth16(Probe, Font(EFamily::Cinzel, 600, 20.f, false));
		// FSlateFontMeasure (the plan's measuring route) uses the legacy FT_Get_Kerning path (FontMeasure.cpp:216, FontCache.cpp:724-747),
		// which these GPOS-only fonts do not have: logged so the difference is on record.
		const TSharedRef<FSlateFontMeasure> Measure = FSlateApplication::Get().GetRenderer()->GetFontMeasureService();
		const float M = Measure->Measure(Probe, Font(EFamily::Cinzel, 600, 20.f, true), 16.f).X / 16.f;
		UE_LOG(LogChimeraHud, Display, TEXT("kern check \"%s\" Cinzel600 20px kern=%.4f nokern=%.4f (FSlateFontMeasure kern=%.4f)"), *Probe, W, W0, M);
	}
}
