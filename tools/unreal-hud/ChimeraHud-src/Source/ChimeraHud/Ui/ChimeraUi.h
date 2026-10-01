// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Fonts/CompositeFont.h"
#include "Fonts/SlateFontInfo.h"

class SWidget;
struct FSlateBrush;

/**
 * Shared HUD vocabulary (plan B 2.3-2.5): colour tokens and conversion, the 14 text styles of r4 section 6, the one place text
 * widgets and image/rounded brushes are constructed, the render route switches measured by T4a, and the file loaders every
 * image or font read goes through (they log path + sha256 for the shot script's allow-list check).
 */
namespace ChimeraUi
{
	/** Design tokens of board 3.1a (r4 section 6), sRGB 0xRRGGBB. Always pass them through Hex(). */
	namespace Token
	{
		constexpr uint32 Void = 0x0B0C0E;
		constexpr uint32 Surface0 = 0x14161A;
		constexpr uint32 Surface1 = 0x1C1F25;
		constexpr uint32 Surface2 = 0x20242B;
		constexpr uint32 Surface3 = 0x262A31;
		constexpr uint32 Surface4 = 0x2A2F38;
		constexpr uint32 Border = 0x3A3F48;
		constexpr uint32 BorderWarm = 0x4A4234;
		constexpr uint32 KeycapBorder = 0x5C5446;
		constexpr uint32 GoldDark = 0x8A6E3C;
		constexpr uint32 Gold = 0xC9A86A;
		constexpr uint32 GoldBright = 0xE3C887;
		constexpr uint32 Text = 0xECE6D8;
		constexpr uint32 TextMuted = 0x9A958A;
		constexpr uint32 Verdigris = 0x4FB39A;
		constexpr uint32 SuccessFill = 0x173129;
	}

	/** sRGB bytes to a linear colour. Never use the FColor overload of FSlateColorBrush: it reinterprets bytes as linear (SlateColorBrush.h:30). */
	FLinearColor Hex(uint32 Rgb, float Alpha = 1.0f);

	/** Parses "#RRGGBB" (or "RRGGBB"); returns false when malformed. */
	bool ParseHex(const FString& Text, uint32& OutRgb);

	/** Reads a file and logs "LogChimeraHud: loaded <path> <sha256>". Returns false (and logs an Error) when it cannot be read. */
	bool LoadFileBytes(const FString& Path, TArray<uint8>& OutBytes);

	/** Decodes a PNG into a BGRA8 sRGB Slate brush at its own pixel size. Null (with an Error log) on failure. */
	TSharedPtr<FSlateBrush> LoadPngBrush(const FString& Path, const FName& BrushName);

	/** Absolute path of a file under H/HudData (forward slashes, matches the shot script's allow-list prefix). */
	FString HudDataPath(const FString& Relative);

	// ------------------------------------------------------------------ render routes (T4a)

	/** Text placement route: SChimeraText with fractional advances (frac) or plain STextBlock (block, route test only). */
	enum class ETextRoute : uint8 { Frac, Block };
	/** Icon route: runtime SVG (nanosvg) or PNGs pre-rasterised by Chromium from the same SVGs (make_svgs.py --png). */
	enum class EIconRoute : uint8 { Svg, Png };

	/** The route switches, parsed once from the command line (-HudHinting=, -HudKern=, -HudText=, -HudIcons=) over the compiled defaults. */
	struct FRoute
	{
		EFontHinting Hinting = EFontHinting::None;
		bool bKern = true;
		ETextRoute Text = ETextRoute::Frac;
		EIconRoute Icons = EIconRoute::Png;
		FString HintingName;
	};
	const FRoute& Route();

	/** Logs "text route ..." (with the Slate.EnableLegacyFontHinting value) and the kern check line of T4a. Call once from the root widget. */
	void LogRouteAndKernCheck();

	// ------------------------------------------------------------------ text

	enum class EFamily : uint8 { Inter, Cinzel, Mono };

	/** The type styles of board 3.1a (r4 section 6; the tab row's "11px and 12px" entry is two styles here). */
	enum class EChimeraText : uint8
	{
		ResourceValue,	// 600 15px/15px JetBrains Mono, #ECE6D8 (chips)
		ResourceRate,	// 500 11px/11px JetBrains Mono, #9A958A (chips)
		Clock,			// 600 18px/18px JetBrains Mono, #ECE6D8
		Label12,		// 500 12px/12px Inter (speed pill #9A958A, Menu #ECE6D8)
		AlertCount,		// 600 13px/13px JetBrains Mono, #9A958A
		Toast,			// 500 13px/1.3 Inter, #ECE6D8
		Keycap,			// 600 10px/14px JetBrains Mono, #ECE6D8
		TabNumber,		// 600 11px/11px JetBrains Mono, #E3C887
		TabCount,		// 600 12px/12px JetBrains Mono, #ECE6D8
		UnitName,		// 600 20px/1.1 Cinzel, #ECE6D8
		UnitRole,		// 400 13px/1.2 Inter, #9A958A
		HpText,			// 500 12px/12px JetBrains Mono, #ECE6D8
		StatLine,		// 500 12px/1.3 JetBrains Mono, #ECE6D8
		CostLabel,		// 600 9px/9px JetBrains Mono, #9A958A
		Count
	};

	struct FTextStyle
	{
		EFamily Family;
		int32 Weight;
		float Px;			// CSS font-size
		float LineHeight;	// CSS line-height in px (the line box the text sits in)
		uint32 Color;		// default colour
		float NudgeY;		// per-style Y nudge in px (plan B 2.3: at most 2 px, with a measured reason); 0 for every style today
	};
	const FTextStyle& Style(EChimeraText InStyle);

	/** The family's hhea metrics in font units (fontTools, r5 section 1.3; typo == hhea and USE_TYPO_METRICS set for all three). */
	struct FFamilyMetrics { float UnitsPerEm; float Ascender; float Descender; };
	FFamilyMetrics Metrics(EFamily Family);

	/**
	 * Baseline position in the CSS line box, in px from the line box top, as Blink computes it for these fonts:
	 * A = round(ascender * px / upm), D = round(descender * px / upm) (Skia rounds the metrics), half-leading = floor((L - (A + D)) / 2),
	 * baseline = half-leading + A. Checked against the glyph rects of r4 (e.g. JetBrains Mono 15px in a 15px box: content 20 px, top -3).
	 */
	float CssBaseline(const FTextStyle& InStyle);

	/** Font of a style under the current route (Size = px x 0.75, LetterSpacing 1 = GPOS kerning on, 0 = off; r5 1.2-1.5). */
	FSlateFontInfo Font(EChimeraText InStyle);
	FSlateFontInfo Font(EFamily Family, int32 Weight, float Px, bool bKern);

	/** The only place text widgets are built: a line box of the style's line-height holding SChimeraText (frac) or STextBlock (block). */
	TSharedRef<SWidget> Text(EChimeraText InStyle, const FString& InText, TOptional<uint32> InColor = TOptional<uint32>());

	// ------------------------------------------------------------------ brushes (cached for the process; widgets keep raw pointers)

	/** A crisp, pixel-snapped solid quad brush (FSlateColorBrush over FLinearColor). */
	const FSlateBrush* Solid(uint32 Rgb, float Alpha = 1.0f);

	/** Fill-only rounded box (plan B 2.4: the outline constructor is never used). Radii are (TL, TR, BR, BL) in px. */
	const FSlateBrush* KeycapBrush(uint32 Fill, const FVector4f& Radii);

	/** White icon brush of the given on-screen size, tinted at draw time: HudData/Icons/<name>.svg (svg route) or <name>_<size>.png. */
	const FSlateBrush* IconBrush(const FString& Name, int32 SizePx);
}
